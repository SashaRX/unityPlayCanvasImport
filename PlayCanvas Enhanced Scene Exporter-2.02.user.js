// ==UserScript==
// @name        PlayCanvas Enhanced Scene Exporter
// @namespace   Violentmonkey Scripts
// @match       https://playcanvas.com/editor/scene/*
// @grant       none
// @version     2.02
// @description Экспорт сцены с глобальными словарями материалов, текстур и контейнеров
// ==/UserScript==
/* global editor, pc, pcui */

(function() {
    // Глобальные коллекции для сбора уникальных ресурсов
    const collectedMaterials = new Map();
    const collectedTextures = new Map();
    const collectedContainers = new Map();
    const collectedModels = new Map();

    function waitForEditor() {
        if (typeof editor !== 'undefined' && typeof pc !== 'undefined' && typeof pcui !== 'undefined') {
            createButton();
        } else {
            setTimeout(waitForEditor, 100);
        }
    }

    function roundFloat(value) {
        return Math.round(value * 1000) / 1000;
    }

    function vecToXYZ(arr) {
        return {
            x: roundFloat(arr[0] || 0),
            y: roundFloat(arr[1] || 0),
            z: roundFloat(arr[2] || 0)
        };
    }

    // Собираем данные о текстуре
    function collectTexture(textureId) {
        if (!textureId || collectedTextures.has(textureId)) return;

        const texAsset = editor.call('assets:get', textureId);
        if (!texAsset) return;

        const texData = {
            id: textureId,
            name: texAsset.get('name'),
            type: texAsset.get('type'),
            path: texAsset.get('path')
        };

        // Получаем информацию о файле
        const file = texAsset.get('file');
        if (file) {
            texData.filename = file.filename || null;
            texData.url = file.url || null;
            texData.size = file.size || null;
            texData.hash = file.hash || null;
        }

        collectedTextures.set(textureId, texData);
    }

    // Собираем данные о материале
    function collectMaterial(materialId) {
        if (!materialId || collectedMaterials.has(materialId)) return;

        const matAsset = editor.call('assets:get', materialId);
        if (!matAsset) return;

        const matData = matAsset.get('data');
        const material = {
            id: materialId,
            name: matAsset.get('name'),
            // Основные свойства
            diffuse: matData.diffuse || [1, 1, 1],
            specular: matData.specular || [0, 0, 0],
            emissive: matData.emissive || [0, 0, 0],
            emissiveIntensity: matData.emissiveIntensity || 1,
            opacity: matData.opacity !== undefined ? matData.opacity : 1,
            metalness: matData.metalness || 0,
            gloss: matData.gloss || 0.25,
            glossInvert: matData.glossInvert || false,
            // Параметры рендеринга
            blendType: matData.blendType || 0,
            alphaTest: matData.alphaTest || 0,
            alphaToCoverage: matData.alphaToCoverage || false,
            twoSidedLighting: matData.twoSidedLighting || false,
            // Текстуры
            textures: {}
        };

        // Собираем все типы текстур
        const textureSlots = [
            'diffuseMap', 'normalMap', 'emissiveMap', 'opacityMap',
            'metalnessMap', 'glossMap', 'specularMap', 'lightMap',
            'aoMap', 'heightMap', 'sphereMap', 'cubeMap'
        ];

        textureSlots.forEach(slot => {
            if (matData[slot]) {
                material.textures[slot] = matData[slot];
                collectTexture(matData[slot]);
            }
        });

        // Дополнительные параметры текстур
        ['Tiling', 'Offset'].forEach(suffix => {
            const prop = `diffuseMap${suffix}`;
            if (matData[prop]) {
                material[prop] = matData[prop];
            }
        });

        collectedMaterials.set(materialId, material);
    }

    // Собираем данные о контейнере
    function collectContainer(containerId) {
        if (!containerId || collectedContainers.has(containerId)) return;

        const containerAsset = editor.call('assets:get', containerId);
        if (!containerAsset) return;

        const containerData = {
            id: containerId,
            name: containerAsset.get('name'),
            type: containerAsset.get('type'),
            sourceId: containerAsset.get('source_asset_id') || null,
            renders: []
        };

        // Ищем все render assets этого контейнера
        const allAssets = editor.call('assets:list');
        allAssets.forEach(asset => {
            if (asset.get('type') === 'render') {
                const data = asset.get('data');
                if (data && data.containerAsset === containerId) {
                    containerData.renders.push({
                        id: asset.get('id'),
                        name: asset.get('name'),
                        index: data.renderIndex
                    });
                }
            }
        });

        collectedContainers.set(containerId, containerData);
    }

    // Обработка компонента model
    function processModelComponent(entity, componentData) {
        const result = {
            ...componentData,
            materialAssets: []
        };

        if (componentData.type === 'asset' && componentData.asset) {
            const modelAsset = editor.call('assets:get', componentData.asset);
            if (modelAsset) {
                // Сохраняем информацию о модели
                if (!collectedModels.has(componentData.asset)) {
                    collectedModels.set(componentData.asset, {
                        id: componentData.asset,
                        name: modelAsset.get('name'),
                        type: modelAsset.get('type'),
                        path: modelAsset.get('path')
                    });
                }

                // Собираем материалы из mapping
                const mapping = modelAsset.get('data.mapping');
                if (mapping && Array.isArray(mapping)) {
                    mapping.forEach(map => {
                        if (map && map.material) {
                            result.materialAssets.push(map.material);
                            collectMaterial(map.material);
                        }
                    });
                }
            }
        }

        // НЕ добавляем materialsData - они будут в глобальном словаре
        return result;
    }

    // Обработка компонента render
    function processRenderComponent(entity, componentData) {
        const result = { ...componentData };

        // Собираем информацию о контейнере если есть
        if (componentData.type === 'asset' && componentData.asset) {
            const renderAsset = editor.call('assets:get', componentData.asset);
            if (renderAsset) {
                const renderData = renderAsset.get('data');
                if (renderData) {
                    result.containerAsset = renderData.containerAsset || null;
                    result.renderIndex = renderData.index || 0;

                    if (result.containerAsset) {
                        collectContainer(result.containerAsset);
                    }
                }
            }
        }

        // Собираем материалы
        if (componentData.materialAssets) {
            const materials = Array.isArray(componentData.materialAssets)
                ? componentData.materialAssets
                : [componentData.materialAssets];

            materials.forEach(matId => {
                if (matId) collectMaterial(matId);
            });
        }

        // НЕ добавляем materialsData
        return result;
    }

    function getComponentData(entity) {
        const components = {};
        const entityComponents = entity.get('components');

        if (entityComponents) {
            Object.keys(entityComponents).forEach(name => {
                let componentData = { ...entity.get(`components.${name}`) };

                // Специальная обработка для model и render
                if (name === 'model') {
                    componentData = processModelComponent(entity, componentData);
                } else if (name === 'render') {
                    componentData = processRenderComponent(entity, componentData);
                }

                components[name] = componentData;
            });
        }

        return components;
    }

    function getEntityData(entity) {
        const data = {
            id: entity.get('resource_id'),
            name: entity.get('name'),
            position: vecToXYZ(entity.get('position')),
            rotation: vecToXYZ(entity.get('rotation')),
            scale: vecToXYZ(entity.get('scale')),
            components: getComponentData(entity),
            children: []
        };

        const children = entity.get('children');
        children.forEach(childId => {
            const child = editor.call('entities:get', childId);
            if (child) data.children.push(getEntityData(child));
        });

        return data;
    }

    function getSceneData() {
        const settings = editor.call('sceneSettings');
        return {
            skybox: {
                texture: settings.get('render.skybox'),
                intensity: settings.get('render.skyboxIntensity'),
                rotation: settings.get('render.skyboxRotation')
            },
            ambientLight: settings.get('render.global_ambient'),
            layers: settings.get('layerOrder')
        };
    }

    function exportData() {
        // Очищаем коллекции
        collectedMaterials.clear();
        collectedTextures.clear();
        collectedContainers.clear();
        collectedModels.clear();

        const root = editor.call('entities:root');
        if (!root) return;

        // Собираем данные сцены (это также заполнит глобальные коллекции)
        const rootData = getEntityData(root);

        // Формируем итоговую структуру
        const sceneData = {
            // Глобальные словари
            materials: Object.fromEntries(collectedMaterials),
            textures: Object.fromEntries(collectedTextures),
            containers: Object.fromEntries(collectedContainers),
            models: Object.fromEntries(collectedModels),
            // Данные сцены
            root: rootData,
            scene: getSceneData()
        };

        console.log('Export summary:');
        console.log(`- Materials: ${collectedMaterials.size}`);
        console.log(`- Textures: ${collectedTextures.size}`);
        console.log(`- Containers: ${collectedContainers.size}`);
        console.log(`- Models: ${collectedModels.size}`);

        const blob = new Blob([JSON.stringify(sceneData, null, 2)], { type: 'application/json' });
        const url = URL.createObjectURL(blob);

        const a = document.createElement('a');
        a.href = url;
        a.download = 'scene_export_v2.json';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    }

    function createButton() {
        const exportBtn = new pcui.Button({ text: 'Export Scene v2' });
        exportBtn.style.position = 'absolute';
        exportBtn.style.bottom = '10px';
        exportBtn.style.right = '10px';
        exportBtn.style.zIndex = '9999';
        editor.call('layout.viewport').append(exportBtn);

        exportBtn.on('click', exportData);
    }

    editor.once('load', createButton);
    waitForEditor();
})();