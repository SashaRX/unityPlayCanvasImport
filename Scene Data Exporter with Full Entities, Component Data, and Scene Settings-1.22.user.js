// ==UserScript==
// @name        Scene Data Exporter with Full Entities, Component Data, and Scene Settings
// @namespace   Violentmonkey Scripts
// @match       https://playcanvas.com/editor/scene/*
// @grant       none
// @version     1.22
// @description Экспорт данных сцены из PlayCanvas с полной информацией о сущностях, их компонентах, ассетах и настройках сцены, исключая пустые массивы
// ==/UserScript==
/* global editor, pc, pcui */

(function() {

    function waitForEditor() {
        if (typeof editor !== 'undefined' && typeof pc !== 'undefined' && typeof pcui !== 'undefined') {
            console.log('Editor, pc, and pcui are available now.');
            createButton();
        } else {
            console.log('Waiting for editor...');
            setTimeout(waitForEditor, 100);
        }
    }

    function roundFloat(value) {
        return Math.round(value * 1000) / 1000;
    }

    function isDefaultValue(value, defaultValue) {
        return value.x === defaultValue.x && value.y === defaultValue.y && value.z === defaultValue.z;
    }

    function isEmptyArray(arr) {
        return Array.isArray(arr) && arr.length === 0;
    }

    function cleanEmptyArrays(data) {
        const stack = [data];
        while (stack.length > 0) {
            const current = stack.pop();
            Object.keys(current).forEach(key => {
                if (isEmptyArray(current[key])) {
                    delete current[key];
                } else if (typeof current[key] === 'object' && current[key] !== null) {
                    stack.push(current[key]);
                }
            });
        }
    }

    function getEntityData(entity) {
        const position = {
            x: roundFloat(entity.get('position')[0]),
            y: roundFloat(entity.get('position')[1]),
            z: roundFloat(entity.get('position')[2])
        };

        const rotation = {
            x: roundFloat(entity.get('rotation')[0]),
            y: roundFloat(entity.get('rotation')[1]),
            z: roundFloat(entity.get('rotation')[2])
        };

        const scale = {
            x: roundFloat(entity.get('scale')[0]),
            y: roundFloat(entity.get('scale')[1]),
            z: roundFloat(entity.get('scale')[2])
        };

        const data = {
            id: entity.get('resource_id'),
            name: entity.get('name'),
            components: getComponentData(entity),
            children: []
        };

        if (!isDefaultValue(position, { x: 0, y: 0, z: 0 })) {
            data.position = position;
        }

        if (!isDefaultValue(rotation, { x: 0, y: 0, z: 0 })) {
            data.rotation = rotation;
        }

        if (!isDefaultValue(scale, { x: 1, y: 1, z: 1 })) {
            data.scale = scale;
        }

        const children = entity.get('children');
        if (children && children.length > 0) {
            children.forEach(childId => {
                const child = editor.call('entities:get', childId);
                if (child) {
                    data.children.push(getEntityData(child));
                } else {
                    console.error('Child entity not found:', childId);
                }
            });
        }

        if (Object.keys(data.components).length === 0) {
            delete data.components;
        }
        if (data.children.length === 0) {
            delete data.children;
        }

        return data;
    }

    function getMaterialTextureData(materialId) {
        const materialAsset = editor.call('assets:get', materialId);
        if (!materialAsset) return {};

        const materialData = materialAsset.get('data');
        const textureSlots = ['diffuseMap', 'normalMap', 'emissiveMap', 'metalnessMap', 'glossMap', 'opacityMap', 'aoMap'];
        const textures = {};

        textureSlots.forEach(slot => {
            if (materialData[slot]) {
                const textureId = materialData[slot];
                const textureAsset = editor.call('assets:get', textureId);
                if (textureAsset) {
                    textures[slot] = {
                        id: textureId,
                        name: textureAsset.get('name'),
                        path: textureAsset.get('file.filename')
                    };
                }
            }
        });

        return textures;
    }

    function getMaterialProperties(materialId) {
        const materialAsset = editor.call('assets:get', materialId);
        if (!materialAsset) return {};

        const materialData = materialAsset.get('data');
        const properties = {};

        // Извлекаем цвет альбедо
        if (materialData.diffuse) {
            properties.albedoColor = materialData.diffuse; // Например, [1, 0, 0] для красного
        }

        // Извлекаем глосс
        if (materialData.glossiness !== undefined) {
            properties.glossiness = materialData.glossiness; // Например, 0.5
        }

        // Извлекаем альфу
        if (materialData.opacity !== undefined) {
            properties.alpha = materialData.opacity; // Например, 1 для непрозрачного
        }

        return properties;
    }

    function getComponentData(entity) {
        const components = {};
        const componentsList = entity.get('components');
        if (componentsList) {
            Object.keys(componentsList).forEach(name => {
                const componentData = { ...entity.get(`components.${name}`) };

                // Обработка компонента model
                if (name === 'model' && componentData.type === 'asset' && componentData.asset) {
                    const modelAsset = editor.call('assets:get', componentData.asset);
                    if (modelAsset) {
                        const mapping = modelAsset.get('data.mapping');
                        if (mapping && Array.isArray(mapping)) {
                            const materials = [];
                            const materialsData = [];

                            mapping.forEach(map => {
                                if (map && map.material) {
                                    materials.push(map.material);

                                    // Собираем данные материала
                                    const matAsset = editor.call('assets:get', map.material);
                                    if (matAsset) {
                                        const matData = matAsset.get('data');
                                        materialsData.push({
                                            id: map.material,
                                            name: matAsset.get('name'),
                                            diffuse: matData.diffuse || [1, 1, 1],
                                            diffuseMap: matData.diffuseMap || null,
                                            opacityMap: matData.opacityMap || null,
                                            emissiveMap: matData.emissiveMap || null,
                                            emissive: matData.emissive || [0, 0, 0],
                                            emissiveIntensity: matData.emissiveIntensity || 1,
                                            opacity: matData.opacity || 1
                                        });
                                    }
                                }
                            });

                            if (materials.length > 0) {
                                componentData.materialAssets = materials;
                                componentData.materialsData = materialsData;
                            }
                        }
                    }
                }

                // Обработка компонента render
                if (name === 'render') {
                    if (componentData.materialAssets && !Array.isArray(componentData.materialAssets)) {
                        componentData.materialAssets = [componentData.materialAssets];
                    }

                    // Собираем данные материалов для render
                    if (componentData.materialAssets) {
                        const materialsData = [];
                        componentData.materialAssets.forEach(matId => {
                            const matAsset = editor.call('assets:get', matId);
                            if (matAsset) {
                                const matData = matAsset.get('data');
                                materialsData.push({
                                    id: matId,
                                    name: matAsset.get('name'),
                                    diffuse: matData.diffuse || [1, 1, 1],
                                    diffuseMap: matData.diffuseMap || null,
                                    opacityMap: matData.opacityMap || null,
                                    emissiveMap: matData.emissiveMap || null,
                                    emissive: matData.emissive || [0, 0, 0],
                                    emissiveIntensity: matData.emissiveIntensity || 1,
                                    opacity: matData.opacity || 1
                                });
                            }
                        });
                        componentData.materialsData = materialsData;
                    }
                }
                components[name] = componentData;
            });
        }
        return components;
    }

    function getAssets() {
        const assets = {};
        editor.assets.list().forEach(asset => {
            const assetData = {
                name: asset.get('name'),
                type: asset.get('type'),
                ...(asset.get('type') !== 'folder' && { file: asset.get('file'), path: asset.get('path') }),
                ...(asset.get('type') === 'material' && extractMaterialData(asset))
            };
            assets[asset.get('id')] = assetData;
        });
        return assets;
    }

    function extractMaterialData(asset) {
        const data = asset.get('data');
        return {
            data: {
                ...(data.diffuse && !arraysEqual(data.diffuse, [1, 1, 1]) && { diffuse: data.diffuse }),
                ...(data.diffuseMap && { diffuseMap: data.diffuseMap }),
                ...(data.emissive && !arraysEqual(data.emissive, [0, 0, 0]) && { emissive: data.emissive }),
                ...(data.emissiveMap && { emissiveMap: data.emissiveMap }),
                ...(data.emissiveIntensity !== undefined && { emissiveIntensity: data.emissiveIntensity }),
                ...(data.opacityMap && { opacityMap: data.opacityMap }),
                ...(data.opacity !== undefined && data.opacity !== 1 && { opacity: data.opacity }),
                ...(data.normalMap && { normalMap: data.normalMap }),
                ...(data.metalnessMap && { metalnessMap: data.metalnessMap }),
                ...(data.glossMap && { glossMap: data.glossMap }),
                ...(data.aoMap && { aoMap: data.aoMap })
            }
        };
    }

    function arraysEqual(a, b) {
        return a.length === b.length && a.every((val, index) => val === b[index]);
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

    function exportData(filename, data) {
        const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    }

    function createButton() {
        const entityBtn = new pcui.Button({ text: 'Export Entity Data' });
        const assetBtn = new pcui.Button({ text: 'Export Assets Data' });
        entityBtn.style.position = 'absolute';
        assetBtn.style.position = 'absolute';
        entityBtn.style.bottom = '32px';
        entityBtn.style.right = '0px';
        assetBtn.style.bottom = '0px';
        assetBtn.style.right = '0px';
        editor.call('layout.viewport').append(entityBtn);
        editor.call('layout.viewport').append(assetBtn);

        entityBtn.on('click', () => {
            const root = editor.call('entities:root');
            if (!root) {
                console.error('Root entity not found');
                return;
            }
            const entityData = getEntityData(root);
            const sceneData = getSceneData();
            exportData('entityData.json', { root: entityData, scene: sceneData });
        });

        assetBtn.on('click', () => {
            const assetsData = getAssets();
            exportData('assetsData.json', assetsData);
        });
    }

    editor.once('load', () => createButton());

    waitForEditor();
})();
