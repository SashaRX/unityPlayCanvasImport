#if UNITY_EDITOR
#region using
// System
using System;
using System.Globalization;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Threading;

// Newtonsoft
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Unity
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;

#endregion using

namespace Assets.Editor.PlayCanvas {
    public class PCImporter : EditorWindow {

        #region Parameters

        [NonSerialized]
        private EntityIDMapping entityMapping; // Ссылка на маппинг ID

        [NonSerialized]
        private FolderMapping folderMapping; // Ссылка на маппинг папок

        [NonSerialized] 
        private AssetIDMapping assetIDMapping;

        private bool showDebugLogs = true; // Переменная для показа логов, вырубаю, что бы было меньше сообщений в консоли
        private bool showPlayCanvasSettings = true; // Переменная для управления Foldout для настроек PlayCanvas
        private bool showSceneStats = true; // Переменная для управления Foldout для статистики сцены

        private string entityJsonPath = ""; // Путь к JSON файлу
        private string jsonContent = ""; // Хранит последнее загруженное содержимое JSON
        private string lastJsonFilePath = ""; // Хранит путь к последнему загруженному JSON файлу
        private string targetFolderMat = ""; // Папка для материалов

        [NonSerialized]
        private SceneData sceneData = null; // Данные сцены

        [NonSerialized]
        private SceneStatistics stats;

        //private bool statsInitialized = false;
        private bool statsCollected = false; 

        // Поля для атласа
        private int atlasWidth = 0, atlasHeight = 0; // Размеры атласа

        private readonly List<Color> atlasColors = new(); // Промежуточное хранение
        private int colorCount = 0; // сколько реально добавлено

        [NonSerialized]
        private Material lightAtlasMat = null; // Общий материал для всех источников света

        [NonSerialized]
        private Texture2D hdrAtlasTexture = null; // Текстура атласа

        private static Mesh quadMesh = null; // Кэшированный меш для квадрата
        private static Mesh diskMesh = null; // Кэшированный меш для диска

        [NonSerialized]
        private readonly Dictionary<string, GameObject> idToGameObject = new();

        [NonSerialized]
        private readonly Dictionary<string, Entity> originalEntities = new();

        [NonSerialized]
        private Dictionary<int, AssetUsageInfo> collectedRenderAssets = new();

        private static Dictionary<PrimitiveType, Mesh> primitiveCache = new();

        private Dictionary<int, string> materialPathCache = new();
        private Dictionary<int, PCAsset> allAssetsCache = null;
        private Dictionary<int, PCAsset> allAssets = null;
        // Поля Playcanvas
        private string nickname = "";
        private string tokenId = "";
        private string projectId = "";
        private string branchId = "";
        private const string folderName = "PlayCanvasData"; // Папка для хранения данных
        private const string entityMappingPath = "EntityIDMapping.asset"; // Папка для хранения данных
        private const string folderMappingPath = "FolderMapping.asset"; // Папка для хранения данных
        private const string assetMappingPath = "AssetIDMapping.asset";

        #region Asset Dependencies

        // Enum для статусов ассетов
        private enum AssetStatus {
            NotDownloaded,
            UpToDate,
            Outdated,
            Corrupted
        }

        // Структура для хранения информации об использовании ассетов
        [Serializable]
        public class AssetUsageInfo {
            public int assetId;
            public string assetType;
            public HashSet<string> usedByEntities = new();
            public HashSet<string> usedByPaths = new();
            
            // Новые поля для container support
            public int? containerAssetId;
            public int? renderIndex;
        }

        // Структура для кеша ассетов
        [Serializable]
        public class AssetCacheEntry {
            public int playCanvasId;
            public string localPath;
            public string hash;
            public DateTime lastModified;
            public long fileSize;
            public DateTime downloadedAt;
            public int usageCount;
        }

        // Класс для управления кешем
        [Serializable]
        public class AssetCache {
            public Dictionary<int, AssetCacheEntry> entries = new Dictionary<int, AssetCacheEntry>();
            
            public void Save(string path) {
                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            
            public static AssetCache Load(string path) {
                if (!File.Exists(path)) return new AssetCache();
                string json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<AssetCache>(json) ?? new AssetCache();
            }

        }

        private class FolderNode {
            public int id;
            public string name;
            public int? parentId;
            public List<FolderNode> children = new();
            public string fullPath;
        }

        // Поля для хранения собранных зависимостей
        [NonSerialized]
        private Dictionary<int, AssetUsageInfo> collectedModels = new();

        [NonSerialized]
        private Dictionary<int, AssetUsageInfo> collectedMaterials = new();

        [NonSerialized]
        private Dictionary<int, AssetUsageInfo> collectedTextures = new();

        [NonSerialized]
        private Dictionary<int, List<int>> modelToMaterialsMap = new();

        [NonSerialized]
        private Dictionary<int, HashSet<int>> materialToTexturesMap = new();

        [NonSerialized]
        private Dictionary<int, int> renderToContainerMap = new(); // Новый маппинг для container

        [NonSerialized]
        private Dictionary<int, int> containerToFBXMap = new(); // Container ID -> FBX ID

        [NonSerialized]
        private AssetCache assetCache;

        private string AssetCachePath => $"Assets/{folderName}/AssetCache.json";

        [NonSerialized]
        private Dictionary<int, JObject> cachedMaterialData = new();

        private class ProcessedAsset {
            public GameObject prefab;
            public Mesh mesh;
            public Material[] materials;
            public string[] submeshNames; // Для отладки
        }
        private Dictionary<int, ProcessedAsset> processedAssets = new();

        private class DownloadTask {
            public int assetId;
            public PCAsset asset;
            public string downloadUrl;
            public string targetPath;
            public float priority; // Вычисляемый приоритет
        }

        #endregion Asset Dependencies

        [MenuItem("Window/PlayCanvas")]

        #endregion Parameters

        public static void ShowWindow() {
            PCImporter w = GetWindow<PCImporter>("PlayCanvas", true);
            w.Show(); // Замените ShowUtility() на Show()
        }

        private void OnEnable() {
            EditorApplication.delayCall += DelayedInitialization;
        }

        private void DelayedInitialization() {
            EditorApplication.delayCall -= DelayedInitialization;
            
            //statsInitialized = false;
            nickname = EditorPrefs.GetString("PlayCanvas_Nickname", "");
            tokenId = EditorPrefs.GetString("PlayCanvas_TokenID", "");
            projectId = EditorPrefs.GetString("PlayCanvas_ProjectID", "");
            branchId = EditorPrefs.GetString("PlayCanvas_BranchID", "");
            entityJsonPath = EditorPrefs.GetString("JSON_Path", "");
            showDebugLogs = EditorPrefs.GetBool("DebugLog", false);
            showSceneStats = EditorPrefs.GetBool("ShowSceneStats", true);

            statsCollected = false;
            InitializeMappings(); // Загрузка маппинга
        }
        
        #region GUI

        void OnGUI() {
            GUILayout.Space(5);
            GUILayout.Label("Import scene from PlayCanvas", EditorStyles.boldLabel);
            GUILayout.Space(5);

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();
            entityJsonPath = EditorGUILayout.TextField("Entity JSON Path:", entityJsonPath);
            if (GUILayout.Button("Select", GUILayout.Width(80))) {
                entityJsonPath = EditorUtility.OpenFilePanel("Select entityData.json", "", "json");
                if (string.IsNullOrEmpty(entityJsonPath)) {
                    Debug.LogError("Canceling import.");
                    return;
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();

            GUILayout.Space(5);

            EditorGUILayout.BeginVertical("box");
            showPlayCanvasSettings = EditorGUILayout.Foldout(showPlayCanvasSettings, "PlayCanvas Settings", true, EditorStyles.boldLabel);
            if (showPlayCanvasSettings) {
                PlayCanvasGUI();
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical("box");
            showSceneStats = EditorGUILayout.Foldout(showSceneStats, "Scene Statistics", true, EditorStyles.boldLabel);
            if (showSceneStats) {
                DrawSceneStatistics();
            }
            EditorGUILayout.EndVertical();

            showDebugLogs = EditorGUILayout.Toggle("Enable Console Logs", showDebugLogs); // Чекбокс для включения/выключения сообщений в консоли

            EditorGUILayout.Space();
            if (assetCache != null && assetCache.entries.Count > 0) {
                EditorGUILayout.HelpBox($"Cached assets: {assetCache.entries.Count}", MessageType.Info);
            }

            if (folderMapping != null) {
                EditorGUILayout.HelpBox($"Folder mapping status: {folderMapping.folders.Count} folders loaded", MessageType.Info);
            } else {
                EditorGUILayout.HelpBox("Folder mapping is null!", MessageType.Error);
            }

            if (GUILayout.Button("Full Import Pipeline", GUILayout.Height(30))) {
                EditorApplication.delayCall += async () => {
                    try {
                        LoadSceneDataFromJsonFile(entityJsonPath);

                        if (sceneData?.root == null) {
                            Debug.LogError("Scene data not loaded or invalid. Aborting import.");
                            return;
                        }

                        stats = new SceneStatistics();
                        CollectSceneStats(sceneData.root, ref stats);
                        
                        targetFolderMat = CreateLightMaterialsFolder();
                        InitializeHDRAtlas(stats.DiskLights + stats.RectangleLights);
                        
                        // НОВОЕ: Загружаем список ассетов перед сбором зависимостей
                        allAssetsCache = await FetchAssetsListFromAPI();
                        allAssets = allAssetsCache; // Временно сохраняем для использования в CollectAssetDependencies
                        
                        // Теперь собираем зависимости с доступом к информации о папках
                        CollectAssetDependencies(sceneData.root);
                        
                        await DownloadSceneAssetsOptimized();
                        
                        // Новый шаг - обработка container/render assets
                        await ProcessContainerAssets();
                        
                        await PostProcessImportedAssets();
                        
                        ClearSceneHierarchy();
                        CreateGameObjectHierarchy(sceneData.root);
                        
                        ApplyHDRAtlas();
                        ApplyAssetsToScene();
                        
                        Debug.Log("Full import complete!");
                    }
                    catch (Exception ex) {
                        Debug.LogError($"Import failed: {ex.Message}\n{ex.StackTrace}");
                    }
                };
            }
        }

        private void PlayCanvasGUI() {
            // Поле для token ID
            tokenId = EditorGUILayout.TextField("PlayCanvas Token ID:", tokenId);

            // Поле для ID проекта
            projectId = EditorGUILayout.TextField("Project ID:", projectId);

            // Поле для ID ветки
            branchId = EditorGUILayout.TextField("Branch ID:", branchId);

            EditorGUILayout.BeginHorizontal();

            // Кнопка для сохранения данных
            if (GUILayout.Button("Save Settings")) {
                EditorPrefs.SetString("PlayCanvas_Nickname", nickname);
                EditorPrefs.SetString("PlayCanvas_TokenID", tokenId);
                EditorPrefs.SetString("PlayCanvas_ProjectID", projectId);
                EditorPrefs.SetString("PlayCanvas_BranchID", branchId);
                EditorPrefs.SetString("JSON_Path", entityJsonPath);
                EditorPrefs.SetBool("DebugLog", showDebugLogs);
                EditorPrefs.SetBool("ShowSceneStats", showSceneStats);
                Debug.Log("Settings saved.");
            }
            EditorGUILayout.EndHorizontal();
        }

        private void InitializeMappings() {
            string cleanFolderName = string.Concat(folderName.Split(System.IO.Path.GetInvalidFileNameChars()));
            string folderPath = $"Assets/{cleanFolderName}";

            // Create the folder if it doesn't exist
            if (!AssetDatabase.IsValidFolder(folderPath)) {
                string parentFolder = "Assets";
                string newFolder = cleanFolderName;
                
                if (!AssetDatabase.IsValidFolder(parentFolder)) {
                    Debug.LogError($"Cannot create folder {folderPath} - parent folder {parentFolder} is invalid");
                    return;
                }
                
                string createdFolder = AssetDatabase.CreateFolder(parentFolder, newFolder);
                if (string.IsNullOrEmpty(createdFolder)) {
                    Debug.LogError($"Failed to create folder: {folderPath}");
                    return;
                }
            }

            // Entity mapping
            string entityMappingFullPath = $"Assets/{cleanFolderName}/{entityMappingPath}";
            entityMapping = AssetDatabase.LoadAssetAtPath<EntityIDMapping>(entityMappingFullPath);
            if (entityMapping == null) {
                entityMapping = ScriptableObject.CreateInstance<EntityIDMapping>();
                AssetDatabase.CreateAsset(entityMapping, entityMappingFullPath);
            }
            entityMapping.entries.Clear();

            // Folder mapping - ВАЖНО!
            string folderMappingFullPath = $"Assets/{cleanFolderName}/{folderMappingPath}";
            folderMapping = AssetDatabase.LoadAssetAtPath<FolderMapping>(folderMappingFullPath);
            if (folderMapping == null) {
                Debug.Log("Creating new FolderMapping asset");
                folderMapping = ScriptableObject.CreateInstance<FolderMapping>();
                AssetDatabase.CreateAsset(folderMapping, folderMappingFullPath);
            }
            // НЕ очищаем папки здесь, чтобы сохранить данные между сессиями
            // folderMapping.folders.Clear();

            // Asset mapping
            string assetMappingFullPath = $"Assets/{cleanFolderName}/{assetMappingPath}";
            assetIDMapping = AssetDatabase.LoadAssetAtPath<AssetIDMapping>(assetMappingFullPath);
            if (assetIDMapping == null) {
                Debug.Log("Creating new AssetIDMapping asset");
                assetIDMapping = ScriptableObject.CreateInstance<AssetIDMapping>();
                AssetDatabase.CreateAsset(assetIDMapping, assetMappingFullPath);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"Mappings initialized. FolderMapping has {folderMapping.folders.Count} entries");
        }

        #region SceneStatistics

        private void DrawSceneStatistics(){
            if (sceneData?.root == null) {
                EditorGUILayout.HelpBox("No scene data loaded", MessageType.Info);
                return;
            }

            // Собираем статистику только один раз
            if (!statsCollected) {
                stats = new SceneStatistics();
                CollectSceneStats(sceneData.root, ref stats);
                statsCollected = true;
            }

            // Отображаем статистику
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField($"Total Nodes: {stats.TotalNodes}");
            EditorGUILayout.LabelField($"Mesh Nodes: {stats.MeshNodes}");
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Lights:", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Point: {stats.PointLights}", GUILayout.MinWidth(50));
            EditorGUILayout.LabelField($"Spot: {stats.SpotLights}", GUILayout.MinWidth(50));
            EditorGUILayout.LabelField($"Direct: {stats.DirectionalLights}", GUILayout.MinWidth(60));
            EditorGUILayout.LabelField($"Total: {stats.TotalLights}", EditorStyles.boldLabel, GUILayout.MinWidth(40));
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Light Shapes:", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Rect: {stats.RectangleLights}", GUILayout.MinWidth(50));
            EditorGUILayout.LabelField($"Disk: {stats.DiskLights}", GUILayout.MinWidth(50));
            EditorGUILayout.LabelField($"Cone: {stats.ConeLights}", GUILayout.MinWidth(50));
            EditorGUILayout.EndHorizontal();

            // Отображаем информацию об атласе
            if (stats.TotalLights > 0){
                EditorGUILayout.Space();
                int atlasSize = MakePowerOfTwo((int)Mathf.Ceil(Mathf.Sqrt(stats.TotalLights)));
                atlasSize = Mathf.Min(atlasSize, 2048); // Ограничиваем максимальный размер
                EditorGUILayout.HelpBox($"Atlas size: {atlasSize}x{atlasSize} (for {stats.DiskLights + stats.RectangleLights} lights)", MessageType.Info);
            }
        }

        private void CollectSceneStats(Entity entity, ref SceneStatistics stats){
            if (entity == null) return;
            
            stats.TotalNodes++;
            
            if (entity.components != null){
                // Проверяем модель
                if (entity.components.TryGetValue("model", out object modelObj) && modelObj is ModelComponent) {
                    stats.MeshNodes++;
                }
                
                // Проверяем свет
                if (entity.components.TryGetValue("light", out object lightObj) && lightObj is LightComponent lightComponent) {
                    stats.TotalLights++;
                    ProcessTypedLightComponent(lightComponent, ref stats);
                }
            }
            
            // Рекурсивно обходим детей
            if (entity.children != null && entity.children.Count > 0){
                foreach (Entity child in entity.children){
                    if (child != null){
                        CollectSceneStats(child, ref stats);
                    }
                }
            }
        }

        private void ProcessTypedLightComponent(LightComponent light, ref SceneStatistics stats){
            // Считаем по типам
            switch (light.type?.ToLower()){
                case "point": stats.PointLights++; break;
                case "spot": stats.SpotLights++; break;
                case "directional": stats.DirectionalLights++; break;
            }
            
            // Считаем по формам
            switch (light.shape){
                case 0: stats.ConeLights++; break;
                case 1: stats.RectangleLights++; break;
                case 2: stats.DiskLights++; break;
            }
        }

        private struct SceneStatistics{
            public int TotalNodes;
            public int MeshNodes;
            public int TotalLights;
            public int PointLights;
            public int SpotLights;
            public int DirectionalLights;
            public int RectangleLights;
            public int DiskLights;
            public int ConeLights;
        }

        #endregion SceneStatistics
                
        #endregion GUI

        private void LoadSceneDataFromJsonFile(string jsonFilePath) {
            if (string.IsNullOrEmpty(jsonFilePath) || !File.Exists(jsonFilePath)) {
                Debug.LogError($"JSON file does not exist or path is invalid: {jsonFilePath}");
                return;
            }

            if (lastJsonFilePath == jsonFilePath && jsonContent != null && jsonContent.Length == new FileInfo(jsonFilePath).Length) {
                if (showDebugLogs) Debug.Log("Scene data already loaded and unchanged.");
                return;
            }

            using (StreamReader reader = new(jsonFilePath)) {
                jsonContent = reader.ReadToEnd();
            }

            lastJsonFilePath = jsonFilePath;

            try {
                JsonSerializerSettings settings = new() {
                    NullValueHandling = NullValueHandling.Ignore,
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    Converters = new List<JsonConverter> {
                        new ComponentConverter()
                    }
                };

                sceneData = JsonConvert.DeserializeObject<SceneData>(jsonContent, settings);

                if (sceneData?.root == null) {
                    Debug.LogError("Failed to parse JSON data or invalid structure.");
                    sceneData = null;
                }
                else {
                    // Логирование загруженных данных
                    if (showDebugLogs) {
                        Debug.Log($"Scene data loaded successfully from {jsonFilePath}");
                        Debug.Log($"Materials: {sceneData.materials?.Count ?? 0}");
                        Debug.Log($"Textures: {sceneData.textures?.Count ?? 0}");
                        Debug.Log($"Containers: {sceneData.containers?.Count ?? 0}");
                        Debug.Log($"Models: {sceneData.models?.Count ?? 0}");
                    }
                }

            } catch (JsonException ex) {
                Debug.LogError($"Error parsing JSON: {ex.Message}");
                sceneData = null;
            } catch (Exception ex) {
                Debug.LogError($"Unexpected error loading scene data: {ex.Message}");
                sceneData = null;
            }
        }

        #region SceneUtility

        #region light
        private string CreateLightMaterialsFolder() { // Создаем директорию для материалов
            string sceneDirectory = Path.GetDirectoryName(SceneManager.GetActiveScene().path); // Получаем директорию текущей сцены
            if (string.IsNullOrEmpty(sceneDirectory)) {
                Debug.LogError("Failed to get scene directory.");
                return null;
            }
            string lightMaterialsDirectory = Path.Combine(sceneDirectory, "LightMaterials"); // Создаем директорию для материалов

            if (Directory.Exists(lightMaterialsDirectory)) { // Проверяем, существует ли директория
                foreach (string file in Directory.EnumerateFiles(lightMaterialsDirectory)) { // Перебираем файлы в директории
                    File.Delete(file); // Удаляем все файлы в директории
                }

                foreach (string directory in Directory.EnumerateDirectories(lightMaterialsDirectory)) { // Перебираем директории в директории
                    Directory.Delete(directory, true); // Удаляем все директории в директории
                }
            } else {
                Directory.CreateDirectory(lightMaterialsDirectory); // Создаем директорию
            }

            AssetDatabase.Refresh(); // Обновляем AssetDatabase, чтобы увидеть изменения

            return lightMaterialsDirectory;
        }

        private void ClearSceneHierarchy() { // Очищаем иерархию сцены
            entityMapping.entries.Clear();
            EditorUtility.SetDirty(entityMapping);

            Scene scene = SceneManager.GetActiveScene();
            GameObject[] rootObjects = scene.GetRootGameObjects();

            foreach (GameObject rootObject in rootObjects) {
                if ((rootObject.hideFlags & (HideFlags.NotEditable | HideFlags.HideAndDontSave)) == 0) {
                    GameObject.DestroyImmediate(rootObject);
                }
            }

            if (showDebugLogs) Debug.Log("Scene hierarchy cleared.");
        }

        private void HandleSpotLight(GameObject obj, Dictionary<string, object> lightData, int shape, Color color, float intensity, List<float> scale) {
            switch (shape) {
                case 0: // Cone
                    float outerConeAngle = Convert.ToSingle(lightData["outerConeAngle"]) * 2.0f;
                    float innerConeAngle = Convert.ToSingle(lightData["innerConeAngle"]) * 2.0f;
                    Light lightComponent = obj.GetComponent<Light>();
                    lightComponent.type = LightType.Spot;

                    lightComponent.spotAngle = outerConeAngle;
                    lightComponent.innerSpotAngle = innerConeAngle;

                    BakeryPointLight bakerySpotLight = obj.AddComponent<BakeryPointLight>();
                    bakerySpotLight.projMode = BakeryPointLight.ftLightProjectionMode.Cone;
                    bakerySpotLight.color = color;
                    bakerySpotLight.intensity = intensity;

                    float innerPercent = innerConeAngle * 100.0f / outerConeAngle;
                    bakerySpotLight.innerAngle = innerPercent;
                    bakerySpotLight.angle = outerConeAngle;
                    break;
                    
                case 1: // Rectangle
                    ConfigureRectangleLight(obj, color, intensity, scale);
                    if (showDebugLogs) Debug.Log("Configured rectangle light: " + obj.name);
                    break;
                    
                case 2: // Disk
                    ConfigureDiscLight(obj, color, intensity, scale);
                    if (showDebugLogs) Debug.Log("Configured disc light: " + obj.name);
                    break;
                    
                case 3: // Sphere (добавляем поддержку)
                    // В Unity нет нативной поддержки сферических источников света
                    // Используем Point Light как ближайший аналог
                    Light sphereLight = obj.GetComponent<Light>();
                    sphereLight.type = LightType.Point;
                    sphereLight.range = scale[0]; // Используем scale как радиус
                    
                    // Если есть Bakery, добавляем компонент
                    BakeryPointLight bakerySphereLight = obj.AddComponent<BakeryPointLight>();
                    bakerySphereLight.color = color;
                    bakerySphereLight.intensity = intensity;
                    bakerySphereLight.cutoff = scale[0]; // Радиус сферы
                    
                    if (showDebugLogs) Debug.Log($"Configured sphere light: {obj.name} with radius {scale[0]}");
                    break;
                    
                default:
                    if (showDebugLogs) Debug.LogWarning($"Unknown light shape: {shape}");
                    break;
            }
        }

        private void ConfigureRectangleLight(GameObject obj, Color color, float intensity, List<float> scale) {
            Light lightComponent = obj.GetComponent<Light>();
            lightComponent.areaSize = new Vector2(scale[0], scale[2]);
            lightComponent.type = LightType.Rectangle;
            BakeryLightMesh bakeryRectangleLight = obj.AddComponent<BakeryLightMesh>();
            bakeryRectangleLight.color = color;
            bakeryRectangleLight.intensity = intensity;

            // Получаем пиксель (цвет * интенсивность)
            Vector2? uvCoord = AddHDRColor(color * intensity);
            if (!uvCoord.HasValue) {
                Debug.LogError("Error: could not add color to the atlas!");
                return;
            }
            else {
                if (showDebugLogs) Debug.Log($"{obj.name} offset uv: " + uvCoord.ToString());
            }

            // Создаем Quad Mesh (см. ваш код)
            MeshFilter quadFilter = obj.AddComponent<MeshFilter>();
            Mesh mesh = GetQuad();
            Mesh meshCopy = Instantiate(mesh);
            Vector2[] uvs = meshCopy.uv;
            for (int i = 0; i < uvs.Length; i++) {
                // всем вершинам одинаковые UV
                uvs[i] = uvCoord.Value;
            }
            meshCopy.uv = uvs;
            quadFilter.sharedMesh = meshCopy;
            quadFilter.transform.localScale = new Vector3(scale[0], scale[2], 1.0f);

            MeshRenderer meshRendererQuad = obj.AddComponent<MeshRenderer>();
            // Назначаем ОДИН общий материал, а не создаём новый
            meshRendererQuad.sharedMaterial = lightAtlasMat;
        }

        private void ConfigureDiscLight(GameObject obj, Color color, float intensity, List<float> scale) {
            Light lightComponent = obj.GetComponent<Light>();
            lightComponent.areaSize = new Vector2(scale[0] * 0.5f, scale[2] * 0.5f);
            lightComponent.type = LightType.Disc;
            BakeryLightMesh bakeryDiscLight = obj.AddComponent<BakeryLightMesh>();
            bakeryDiscLight.color = color;
            bakeryDiscLight.intensity = intensity;

            Vector2? uvCoord = AddHDRColor(color * intensity);
            if (!uvCoord.HasValue) {
                Debug.LogError("Error: could not add color to the atlas!");
                return;
            }
            else {
                if (showDebugLogs) Debug.Log($"{obj.name} offset uv: " + uvCoord.ToString());
            }

            MeshFilter diskFilter = obj.AddComponent<MeshFilter>();
            Mesh mesh = GetDisk();
            Mesh meshCopy = Instantiate(mesh);
            Vector2[] uvs = meshCopy.uv;
            for (int i = 0; i < uvs.Length; i++) {
                uvs[i] = uvCoord.Value;
            }
            meshCopy.uv = uvs;
            diskFilter.sharedMesh = meshCopy;
            diskFilter.transform.localScale = new Vector3(scale[0], scale[2], 1.0f);

            MeshRenderer meshRendererDisk = obj.AddComponent<MeshRenderer>();
            meshRendererDisk.sharedMaterial = lightAtlasMat;
        }

        private static Mesh GetQuad() { // Создаем квадратный меш
            if (quadMesh == null) {
                quadMesh = new Mesh {
                    vertices = new Vector3[] {
                        new(-0.5f, -0.5f, 0.0f), new(0.5f, -0.5f, 0.0f),
                        new(0.5f, 0.5f, 0.0f),  new(-0.5f, 0.5f, 0.0f)
                    },
                    triangles = new int[] { 0, 1, 2, 0, 2, 3 },
                    uv = new Vector2[] {
                        Vector2.zero, Vector2.zero,
                        Vector2.zero, Vector2.zero
                    }
                };
                quadMesh.RecalculateNormals();
                quadMesh.RecalculateBounds();
            }
            return quadMesh;
        }
    
        private static Mesh GetDisk(float radius = .5f, int segments = 32) { // Создаем меш в форме диска
            if (diskMesh == null) {
                diskMesh = new Mesh();
                int vertexCount = segments + 1;
                Vector3[] vertices = new Vector3[vertexCount];
                Vector2[] uv = new Vector2[vertexCount];
                Vector3[] normals = new Vector3[vertexCount];
                float angleStep = 360f / segments * Mathf.Deg2Rad;
                for (int i = 0; i < segments; i++) {
                    float angle = i * angleStep;
                    vertices[i + 1] = new Vector3(radius * Mathf.Cos(angle), radius * Mathf.Sin(angle), 0f);
                    uv[i + 1] = new Vector2((vertices[i + 1].x / (2 * radius)) + 0.5f, (vertices[i + 1].y / (2 * radius)) + 0.5f);
                    normals[i + 1] = Vector3.up;
                }
                vertices[0] = Vector3.zero;
                uv[0] = new Vector2(0.5f, 0.5f);
                normals[0] = Vector3.up;
                int[] triangles = new int[segments * 3];
                for (int i = 0; i < segments; i++) {
                    triangles[i * 3] = 0;
                    triangles[i * 3 + 1] = i + 1;
                    triangles[i * 3 + 2] = (i == segments - 1) ? 1 : i + 2;
                }
                diskMesh.SetVertices(vertices);
                diskMesh.SetUVs(0, uv);
                diskMesh.SetNormals(normals);
                diskMesh.SetTriangles(triangles, 0);
            }
            return diskMesh;
        }

        private Vector2? AddHDRColor(Color hdrColor) {
            if (colorCount >= atlasWidth * atlasHeight) {
                Debug.LogWarning("The Atlas is overflowing, there is no room for new light!");
                return null;
            }
            int x = colorCount % atlasWidth;
            int y = colorCount / atlasWidth;
            atlasColors[y * atlasWidth + x] = hdrColor;
            colorCount++;

            // Возвращаем UV (центр пикселя)
            float u = (x + 0.5f) / atlasWidth;
            float v = (y + 0.5f) / atlasHeight;
            return new Vector2(u, v);
        }

        private void InitializeHDRAtlas( int countLight = 1) {
            // Оптимизация: кэшируем часто используемые значения
            string matFolder = targetFolderMat;
            
            int side = MakePowerOfTwo((int)Mathf.Ceil(Mathf.Sqrt(countLight)));

            atlasWidth = atlasHeight = side; // Сохраняем размеры атласа, текстура будет квадратной
            if (showDebugLogs) Debug.Log($"Atlas size: {side}x{side}");

            // Оптимизация: инициализация массива цветов с использованием сильной типизации
            int totalPixels = side * side;
            atlasColors.Clear(); // Используем существующий список вместо создания нового
            for (int i = 0; i < totalPixels; i++) {
                atlasColors.Add(Color.clear); // Заполняем массив прозрачными цветами
            }
            colorCount = 0; // Счетчик добавленных цветов

            // Оптимизация: очистка ресурсов
            if (this.hdrAtlasTexture != null) {
                DestroyImmediate(this.hdrAtlasTexture);
                this.hdrAtlasTexture = null;
            }

            // Оптимизация: создание текстуры с сильной типизацией
            this.hdrAtlasTexture = new Texture2D(side, side, TextureFormat.RGBAHalf, false, true) {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
                anisoLevel = 0,
                name = "HDR_Atlas",
                alphaIsTransparency = false
            };

            // Оптимизация: создание ассетов
            string texturePath = AssetDatabase.GenerateUniqueAssetPath($"{matFolder}/lightAtlas_tex.asset");
            AssetDatabase.CreateAsset(this.hdrAtlasTexture, texturePath);

            // Оптимизация: очистка и создание материала
            if (this.lightAtlasMat != null) {
                DestroyImmediate(this.lightAtlasMat);
            }

            this.lightAtlasMat = new Material(Shader.Find("Bakery/Light")) {
                mainTexture = hdrAtlasTexture,
                color = Color.white
            };
            this.lightAtlasMat.SetFloat("intensity", 1.0f);

            string materialPath = AssetDatabase.GenerateUniqueAssetPath($"{matFolder}/lightAtlas_mat.mat");
            AssetDatabase.CreateAsset(this.lightAtlasMat, materialPath);

            //AssetDatabase.SaveAssets();
            ApplyHDRAtlas(); // Добавьте эту строку
        }

        private void ApplyHDRAtlas() {
            if (hdrAtlasTexture == null || atlasColors.Count == 0) return;

            // Создаем массив Color32 из atlasColors
            Color32[] colors32 = new Color32[atlasColors.Count];
            for (int i = 0; i < atlasColors.Count; i++) {
                colors32[i] = atlasColors[i];
            }

            hdrAtlasTexture.SetPixels32(colors32);
            hdrAtlasTexture.Apply();
            
            // Обязательно сохраняем изменения
            EditorUtility.SetDirty(hdrAtlasTexture);
            //AssetDatabase.SaveAssets();
        }

        #endregion Light

        #region CreateGameObjects

        private static Quaternion GetUnityRotationForModel(Vector3 pcEuler){
            // 1) X — повернуть на −90
            float x = pcEuler.x - 90f;

            // 2) Y ← Z  |  3) Z ← Y + 180
            float y = pcEuler.z;
            float z = 180f - pcEuler.y;

            return Quaternion.Euler(x, y, z);
        }


        void CreateGameObjectHierarchy(Entity entity, GameObject parent = null) {
            // Проверяем, существует ли сущность
            if (originalEntities.ContainsKey(entity.id)) {
                Debug.LogWarning($"Entity with ID {entity.id} already exists. Skipping.");
                return;
            }
            originalEntities[entity.id] = entity;

            // Задаем имя объекта
            string objectName = string.IsNullOrEmpty(entity.name) ? entity.id : entity.name;
            if (showDebugLogs) Debug.Log($"Creating object: {objectName}");

            // Устанавливаем значения по умолчанию
            entity.position ??= new List<float> { 0f, 0f, 0f };
            entity.rotation ??= new List<float> { 0f, 0f, 0f };
            entity.scale    ??= new List<float> { 1f, 1f, 1f };

            // Проверяем, существует ли GameObject, или создаем новый
            GameObject obj;
            if (idToGameObject.TryGetValue(entity.id, out GameObject existingObj)) {
                Debug.LogWarning($"GameObject for entity {entity.id} already exists. Using existing object.");
                obj = existingObj;
            } else {
                obj = new GameObject(objectName);
                obj.transform.parent = parent != null ? parent.transform : null; // Упрощенная установка родителя
                idToGameObject[entity.id] = obj;
            }

            // Преобразуем данные PlayCanvas в Unity
            Vector3 pcPos   = new(entity.position[0], entity.position[1], entity.position[2]);
            Vector3 pcEuler = new(entity.rotation[0], entity.rotation[1], entity.rotation[2]);
            Vector3 pcScale = new(entity.scale[0],    entity.scale[1],    entity.scale[2]);

            bool isModel = entity.components != null &&
                        (entity.components.ContainsKey("model") ||
                        (entity.components.TryGetValue("render", out var r) &&
                            r is JObject rObj &&
                            rObj["type"]?.ToString() == "asset"));

            ApplyPlayCanvasTransform(obj, pcPos, pcEuler, pcScale, isModel);

            // Добавляем запись в entityMapping
            entityMapping.entries.Add(new EntityIDMapping.Entry {
                gameObject = obj,
                id = entity.id
            });

            // Добавляем компоненты, если они есть
            if (entity.components != null) {
                AddComponents(obj, entity.components, entity.scale);
            }

            // Обрабатываем детей
            foreach (Entity child in entity.children) {
                CreateGameObjectHierarchy(child, obj);
            }
        }

        public static void ApplyPlayCanvasTransform(GameObject obj, Vector3 pcPosition, Vector3 pcEulerAngles, Vector3 pcScale, bool isModel = false) {
            // Create transformation matrix and inversion matrix
            Matrix4x4 Mpc = Matrix4x4.TRS(pcPosition, EulerToQuaternion(pcEulerAngles), pcScale);
            Matrix4x4 C = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));

            // Transform matrix and extract position, scale, rotation
            Matrix4x4 Mu = C * Mpc * C;
            Vector3 posU = Mu.GetColumn(3);
            Vector3 scaleU = new(
                Mu.GetColumn(0).magnitude,
                Mu.GetColumn(1).magnitude,
                Mu.GetColumn(2).magnitude
            );

            // Check and correct scale
            scaleU = new Vector3(
                Mathf.Approximately(scaleU.x, 0f) ? 1f : scaleU.x,
                Mathf.Approximately(scaleU.y, 0f) ? 1f : scaleU.y,
                Mathf.Approximately(scaleU.z, 0f) ? 1f : scaleU.z
            );

            Quaternion rotU = isModel 
            ? GetUnityRotationForModel(pcEulerAngles)
            : GetUnityRotation(pcEulerAngles);

            // Apply transformation
            obj.transform.SetLocalPositionAndRotation(posU, rotU);
            obj.transform.localScale = scaleU;
        }

        private static Quaternion EulerToQuaternion(Vector3 eulerAngles){
            // Порядок поворотов в PlayCanvas: X -> Y -> Z
            Quaternion qx = Quaternion.AngleAxis(eulerAngles.x, Vector3.right);
            Quaternion qy = Quaternion.AngleAxis(eulerAngles.y, Vector3.up);
            Quaternion qz = Quaternion.AngleAxis(eulerAngles.z, Vector3.forward);
            return qz * qy * qx; // XYZ порядок
        }
        // Получает поворот для Unity
        private static Quaternion GetUnityRotation(Vector3 pcEulerAngles){
            // Альтернативный путь для других поворотов: преобразуем через кватернион
            Quaternion pcQuat = EulerToQuaternion(pcEulerAngles);

            // Инверсия Z: корректируем кватернион
            Matrix4x4 C = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
            Matrix4x4 Mpc = Matrix4x4.Rotate(pcQuat);
            Matrix4x4 Mu = C * Mpc * C;
            Vector3 forwardU = Mu.GetColumn(2).normalized;
            Vector3 upU = Mu.GetColumn(1).normalized;

            // Проверка на ортогональность
            if (Vector3.Dot(forwardU, upU) > 0.99f){
                Debug.LogWarning("Forward and Up vectors are nearly parallel, using fallback Up vector");
                upU = Vector3.up;
            }

            return Quaternion.LookRotation(forwardU, upU);
        }

        private void AddComponents(GameObject obj, Dictionary<string, object> components, List<float> scale) {
            if (components.ContainsKey("camera")) {
                AddCameraComponent(obj);
            }
            if (components.ContainsKey("light")) {
                Quaternion additionalRotation = Quaternion.Euler(90f, 0f, 0f); // Поворот на 90 градусов по X
                obj.transform.localRotation *= additionalRotation;

                AddLightComponent(obj, components["light"], scale);
            }
            if (components.ContainsKey("model")) {
                ProcessModelComponent(obj, components["model"]);
            }
            // Add more if statements for other component types if needed
        }

        private void AddCameraComponent(GameObject obj) {
            obj.AddComponent<Camera>();
        }

        private void AddLightComponent(GameObject obj, object lightDataRaw, List<float> scale) {
            Dictionary<string, object> lightData = lightDataRaw as Dictionary<string, object> ??
                JsonConvert.DeserializeObject<Dictionary<string, object>>(JsonConvert.SerializeObject(lightDataRaw));
            
            if (lightData == null) {
                Debug.LogError("Invalid light data");
                return;
            }
            
            Light lightComponent = obj.AddComponent<Light>();
            
            int shape = lightData.ContainsKey("shape") ? Convert.ToInt32(lightData["shape"]) : 0;
            
            if (lightData.ContainsKey("type")) {
                string lightType = lightData["type"].ToString().ToLower();
                float intensity = Convert.ToSingle(lightData["intensity"]);
                List<float> colorArray = (lightData["color"] as JArray)?.ToObject<List<float>>();
                colorArray ??= new List<float> { 1f, 1f, 1f };
                Color color = new(colorArray[0], colorArray[1], colorArray[2]);
                
                lightComponent.intensity = intensity;
                lightComponent.color = color;
                
                switch (lightType) {
                    case "directional":
                        lightComponent.type = LightType.Directional;
                        BakeryDirectLight bakeryDirectLight = obj.AddComponent<BakeryDirectLight>();
                        bakeryDirectLight.color = color;
                        bakeryDirectLight.intensity = intensity;
                        break;
                    case "spot":
                        HandleSpotLight(obj, lightData, shape, color, intensity, scale);
                        break;
                    case "point":
                        lightComponent.type = LightType.Point;
                        BakeryPointLight bakeryPointLight = obj.AddComponent<BakeryPointLight>();
                        bakeryPointLight.color = color;
                        bakeryPointLight.intensity = intensity;
                        break;
                    default:
                        Debug.LogError($"{obj.name}: Unknown light type!");
                        break;
                }
            }
        }

        private void ProcessModelComponent(GameObject obj, object modelDataRaw) {
            try {
                object modelData = modelDataRaw;
                
                if (modelData is JObject jObj && jObj["asset"] != null) {
                    Debug.Log($"Model component: {obj.name}, ID: {jObj["asset"].ToString()}");
                }
                else if (modelData is ModelComponent typedModel) {
                    Debug.Log($"Model component: {obj.name}, ID: {typedModel.asset}");
                }
                else {
                    string serialized = JsonConvert.SerializeObject(modelData);
                    ModelComponent typedModelData = JsonConvert.DeserializeObject<ModelComponent>(serialized);
                    if (typedModelData?.asset != null) {
                        Debug.Log($"Model component: {obj.name}, ID: {typedModelData.asset}");
                    } else {
                        Debug.LogWarning($"{obj.name}: Could not extract asset ID from model component");
                    }
                }
            }
            catch (Exception ex) {
                Debug.LogError($"{obj.name}: Error processing model component: {ex.Message}");
            }
        }

        #region Folder

        #endregion Folder

        #region Web

        #endregion Web

        #endregion CreateGameObjects

        #region CollectAssetDependencies

        private void CollectAssetDependencies(Entity rootEntity) {
            // Очищаем предыдущие данные
            collectedModels.Clear();
            collectedMaterials.Clear();
            collectedTextures.Clear();
            collectedRenderAssets.Clear();
            modelToMaterialsMap.Clear();
            materialToTexturesMap.Clear();
            materialPathCache.Clear();
            renderToContainerMap.Clear();
            containerToFBXMap.Clear();
            
            // Собираем зависимости 
            CollectFromEntity(rootEntity, "");
            
            if (showDebugLogs) {
                Debug.Log($"=== Asset Dependencies Collected ===");
                Debug.Log($"Models: {collectedModels.Count}");
                Debug.Log($"Render Assets: {collectedRenderAssets.Count}");
                Debug.Log($"Materials: {collectedMaterials.Count}");
                Debug.Log($"Textures: {collectedTextures.Count}");
                Debug.Log($"Container mappings: {renderToContainerMap.Count}");
                
                // Выводим первые несколько текстур для отладки
                foreach (var tex in collectedTextures.Take(5)) {
                    Debug.Log($"  Texture {tex.Key}: used by {tex.Value.usedByEntities.Count} entities");
                }
            }
        }

        private void CollectFromEntity(Entity entity, string path) {
            if (entity == null) return;
            
            string currentPath = string.IsNullOrEmpty(path) ? entity.name : $"{path}/{entity.name}";
            
            // Проверяем компонент render
            if (entity.components?.TryGetValue("render", out object renderObj) == true) {
                ProcessRenderForDependencies(entity, renderObj, currentPath);
            }
            
            // ВАЖНО: Проверяем компонент model
            if (entity.components?.TryGetValue("model", out object modelObj) == true) {
                ProcessModelForDependencies(entity, modelObj, currentPath);
            }


            // Рекурсивно обходим детей
            if (entity.children != null) {
                foreach (Entity child in entity.children) {
                    CollectFromEntity(child, currentPath);
                }
            }
        }

        private void ProcessRenderForDependencies(Entity entity, object renderObj, string path) {
            int renderAssetId = 0;
            List<int> materialIds = new();
            int? containerAssetId = null;
            int? renderIndex = null;
            
            try {
                if (renderObj is JObject jObj) {
                    string renderType = jObj["type"]?.Value<string>() ?? "";
                    
                    if (renderType == "asset") {
                        JToken assetToken = jObj["asset"];
                        if (assetToken != null && assetToken.Type != JTokenType.Null) {
                            renderAssetId = assetToken.Value<int>();
                        }
                        
                        JToken containerToken = jObj["containerAsset"];
                        if (containerToken != null && containerToken.Type != JTokenType.Null) {
                            containerAssetId = containerToken.Value<int>();
                        }
                        
                        JToken indexToken = jObj["renderIndex"];
                        if (indexToken != null && indexToken.Type != JTokenType.Null) {
                            renderIndex = indexToken.Value<int>();
                        }
                    }
                    
                    // Получаем материалы
                    if (jObj["materialAssets"] is JArray matArray) {
                        foreach (JToken mat in matArray) {
                            if (mat != null && mat.Type != JTokenType.Null) {
                                int matId = mat.Value<int>();
                                if (matId != 0) materialIds.Add(matId);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) {
                Debug.LogError($"Error processing render component at {path}: {ex.Message}");
                return;
            }
            
            // Регистрируем render asset
            if (renderAssetId != 0) {
                RegisterAsset(collectedRenderAssets, renderAssetId, "render", entity.id, path);
                
                if (containerAssetId.HasValue && renderIndex.HasValue) {
                    renderToContainerMap[renderAssetId] = containerAssetId.Value;
                    
                    AssetUsageInfo renderInfo = collectedRenderAssets[renderAssetId];
                    renderInfo.containerAssetId = containerAssetId;
                    renderInfo.renderIndex = renderIndex;
                }
            }

            // Регистрируем материалы и текстуры из словаря
            foreach (int matId in materialIds) {
                RegisterAsset(collectedMaterials, matId, "material", entity.id, path);
                
                // Текстуры из глобального словаря
                if (sceneData.materials != null && sceneData.materials.TryGetValue(matId, out MaterialData matData)) {
                    if (matData.textures != null) {
                        foreach (var texturePair in matData.textures) {
                            RegisterAsset(collectedTextures, texturePair.Value, "texture", entity.id, path);
                        }
                    }
                }
            }
        }
        
        private void ProcessModelForDependencies(Entity entity, object modelObj, string path) {
            int modelId = 0;
            List<int> materialIds = new();
            
            try {
                if (modelObj is JObject jObj) {
                    // Получаем ID модели
                    JToken assetToken = jObj["asset"];
                    if (assetToken != null && assetToken.Type != JTokenType.Null) {
                        modelId = assetToken.Value<int>();
                    }
                    
                    // Получаем материалы из materialAssets
                    if (jObj["materialAssets"] is JArray matArray) {
                        foreach (JToken mat in matArray) {
                            if (mat != null && mat.Type != JTokenType.Null) {
                                int matId = mat.Value<int>();
                                if (matId != 0) materialIds.Add(matId);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) {
                Debug.LogError($"Error processing model component at {path}: {ex.Message}");
                return;
            }
            
            // Регистрируем модель
            if (modelId != 0) {
                RegisterAsset(collectedModels, modelId, "model", entity.id, path);
                
                if (materialIds.Count > 0) {
                    modelToMaterialsMap[modelId] = materialIds;
                }
            }
            
            // Регистрируем материалы И их текстуры из словаря
            foreach (int matId in materialIds) {
                RegisterAsset(collectedMaterials, matId, "material", entity.id, path);
                
                // Собираем текстуры из глобального словаря
                if (sceneData.materials != null && sceneData.materials.TryGetValue(matId, out MaterialData matData)) {
                    if (matData.textures != null) {
                        foreach (var texturePair in matData.textures) {
                            RegisterAsset(collectedTextures, texturePair.Value, "texture", entity.id, path);
                            if (showDebugLogs) {
                                Debug.Log($"Found {texturePair.Key}: {texturePair.Value} in material {matData.name}");
                            }
                        }
                    }
                }
            }
        }

        private string GetRenderAssetName(int assetId) {
            // Пытаемся найти имя render asset в нашем кеше
            if (collectedRenderAssets.TryGetValue(assetId, out AssetUsageInfo usage)) {
                // Можно попытаться получить имя из пути или entity
                // Это базовая реализация - может потребоваться улучшение
                return $"Render_{assetId}";
            }
            
            // Альтернативный способ - из загруженных ассетов
            if (assetCache?.entries.TryGetValue(assetId, out AssetCacheEntry cached) == true) {
                return Path.GetFileNameWithoutExtension(cached.localPath);
            }
            
            return $"Render_{assetId}";
        }
		
        private void RegisterAsset(Dictionary<int, AssetUsageInfo> collection, int assetId, string type, string entityId, string path) {
            if (!collection.TryGetValue(assetId, out AssetUsageInfo usage)) {
                usage = new AssetUsageInfo {
                    assetId = assetId,
                    assetType = type
                };
                collection[assetId] = usage;
            }
            
            usage.usedByEntities.Add(entityId);
            usage.usedByPaths.Add(path);
        }

        private AssetStatus CheckAssetStatus(int assetId, PCAsset pcAsset){
            // Загружаем кеш при первом обращении
            assetCache ??= AssetCache.Load(AssetCachePath);

            // 0) Записи нет ─ значит ассет ещё не скачивали
            if (!assetCache.entries.TryGetValue(assetId, out AssetCacheEntry cached)) {
                if (showDebugLogs) Debug.Log($"Asset {assetId} not in cache – NotDownloaded");
                return AssetStatus.NotDownloaded;
            }

            // 1) Файл исчез из-под Unity
            if (!File.Exists(cached.localPath)) {
                if (showDebugLogs) Debug.Log($"Asset {assetId} lost on disk – Corrupted");
                return AssetStatus.Corrupted;
            }

            // 2) Самый надёжный способ – сверяем hash-сумму
            if (!string.IsNullOrEmpty(pcAsset.hash) && !string.IsNullOrEmpty(cached.hash))
                return pcAsset.hash == cached.hash ? AssetStatus.UpToDate : AssetStatus.Outdated;

            // 3) Уже импортирован и прописан в AssetIDMapping → считаем актуальным
            string existingPath = assetIDMapping.GetPathById(assetId);        // правильный метод и ID
            if (!string.IsNullOrEmpty(existingPath) && File.Exists(existingPath)){
                return AssetStatus.UpToDate;                                   // метод должен вернуть статус
            }

            // 4) Файл более свежий на сервере
            if (pcAsset.modifiedAt.HasValue && cached.lastModified < pcAsset.modifiedAt.Value)
                return AssetStatus.Outdated;

            // 5) Размер не совпал с кешом
            FileInfo fi = new(cached.localPath);
            if (fi.Length != cached.fileSize)
                return AssetStatus.Corrupted;

            // Всё ок
            if (showDebugLogs) Debug.Log($"Asset {assetId} – UpToDate");
            return AssetStatus.UpToDate;
        }

        private async Task PostProcessImportedAssets() {
            // Добавляем отладку
            Debug.Log($"PostProcessImportedAssets: collectedModels={collectedModels.Count}, cache entries={assetCache?.entries?.Count ?? 0}");
            
            // Ждем обновления AssetDatabase после загрузки файлов
            AssetDatabase.Refresh();
            await Task.Delay(500);
                    
            // Обрабатываем все загруженные FBX
            foreach (KeyValuePair<int, AssetUsageInfo> modelEntry in collectedModels) {
                int modelId = modelEntry.Key;
                
                if (!assetCache.entries.TryGetValue(modelId, out AssetCacheEntry cached)) {
                    Debug.LogWarning($"Model {modelId} not in cache - may have failed to download");
                    
                    // ИСПРАВЛЕНИЕ: Создаем заглушку для отсутствующих моделей
                    processedAssets[modelId] = new ProcessedAsset {
                        prefab = null,
                        mesh = GetPrimitiveMesh(PrimitiveType.Cube), // Используем куб как заглушку
                        materials = new Material[] { new Material(Shader.Find("Universal Render Pipeline/Lit")) },
                        submeshNames = new string[] { "Missing_Model" }
                    };
                    continue;
                }
                
                if (!File.Exists(cached.localPath)) {
                    Debug.LogWarning($"Model file not found: {cached.localPath}");
                    continue;
                }
                
                // Обрабатываем FBX
                ProcessedAsset processed = ProcessFBXAsset(modelId, cached.localPath);
                if (processed != null) {
                    processedAssets[modelId] = processed;
                    Debug.Log($"Processed model {modelId}: {cached.localPath}");
                } else {
                    Debug.LogError($"Failed to process model {modelId}");
                }
            }

            EditorUtility.SetDirty(assetIDMapping);
            AssetDatabase.SaveAssets();
        }

        private async Task ProcessContainerAssets() {
            Debug.Log("=== Processing Container Assets ===");
            
            // Ждем обновления AssetDatabase
            AssetDatabase.Refresh();
            await Task.Delay(500);
            
            // Обрабатываем каждый render asset с контейнером
            foreach (var renderEntry in collectedRenderAssets.Where(r => r.Value.containerAssetId.HasValue)) {
                int renderAssetId = renderEntry.Key;
                AssetUsageInfo renderInfo = renderEntry.Value;
                int containerId = renderInfo.containerAssetId.Value;
                // Находим правильный индекс для этого render asset
var renderMatch = containerData.renders.FirstOrDefault(r => r.id == renderAssetId);
int meshIndex = renderMatch?.index ?? renderInfo.renderIndex ?? 0;  // ← Добавить эту строку

Debug.Log($"Processing render asset {renderAssetId} '{renderMatch?.name}' from container {containerId} at index {meshIndex}");
                // Используем данные из словаря
                if (sceneData.containers != null && sceneData.containers.TryGetValue(containerId, out ContainerData containerData)) {
                    // Находим правильный индекс для этого render asset
                    var renderMatch = containerData.renders.FirstOrDefault(r => r.id == renderAssetId);
                    int meshIndex = renderMatch?.index ?? renderInfo.renderIndex ?? 0;  // ← Добавить эту строку

                    Debug.Log($"Processing render asset {renderAssetId} '{renderMatch?.name}' from container {containerId} at index {meshIndex}");
                }

                // Находим FBX для контейнера
                if (!containerToFBXMap.TryGetValue(containerId, out int fbxId)) {
                    Debug.LogError($"No FBX mapping found for container {containerId}");
                    continue;
                }
                
                // Проверяем, загружен ли FBX
                if (!assetCache.entries.TryGetValue(fbxId, out AssetCacheEntry fbxCached)) {
                    Debug.LogError($"FBX {fbxId} not in cache for container {containerId}");
                    continue;
                }
                
                if (!File.Exists(fbxCached.localPath)) {
                    Debug.LogError($"FBX file not found: {fbxCached.localPath}");
                    continue;
                }
                
                // Извлекаем меш из FBX
                ProcessedAsset extracted = ExtractMeshFromFBXContainer(fbxId, fbxCached.localPath, meshIndex, renderAssetId);
                
                if (extracted != null) {
                    processedAssets[renderAssetId] = extracted;
                    Debug.Log($"Successfully extracted mesh for render asset {renderAssetId}");
                } else {
                    Debug.LogError($"Failed to extract mesh for render asset {renderAssetId}");
                }
            }
            
            Debug.Log($"Processed {processedAssets.Count} render assets from containers");
        }

        private ProcessedAsset ExtractMeshFromFBXContainer(int fbxId, string fbxPath, int meshIndex, int renderAssetId) {
            try {
                // Загружаем FBX как GameObject
                GameObject fbxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                if (fbxPrefab == null) {
                    Debug.LogError($"Failed to load FBX: {fbxPath}");
                    return null;
                }
                
                // Получаем все MeshFilter с учетом иерархии
                MeshFilter[] meshFilters = fbxPrefab.GetComponentsInChildren<MeshFilter>();
                
                if (meshFilters.Length == 0) {
                    Debug.LogError($"No mesh filters found in FBX: {fbxPath}");
                    return null;
                }
                
                if (meshIndex >= meshFilters.Length) {
                    Debug.LogError($"Mesh index {meshIndex} out of bounds. FBX has {meshFilters.Length} meshes");
                    return null;
                }
                
                // Берем меш по индексу
                MeshFilter targetFilter = meshFilters[meshIndex];
                Mesh mesh = targetFilter.sharedMesh;
                
                if (mesh == null) {
                    Debug.LogError($"Mesh at index {meshIndex} is null");
                    return null;
                }
                
                // Проверяем имя
                string expectedName = null;
                if (allAssets != null && allAssets.TryGetValue(renderAssetId, out PCAsset renderAsset)) {
                    expectedName = renderAsset.name;
                }
                
                string actualName = targetFilter.gameObject.name;
                
                if (!string.IsNullOrEmpty(expectedName) && expectedName != actualName) {
                    Debug.LogWarning($"Mesh name mismatch for render asset {renderAssetId}: " +
                                   $"expected '{expectedName}', got '{actualName}' at index {meshIndex}");
                }
                
                // Получаем материалы
                Material[] materials = null;
                MeshRenderer renderer = targetFilter.GetComponent<MeshRenderer>();
                if (renderer != null && renderer.sharedMaterials != null) {
                    materials = renderer.sharedMaterials;
                }
                
                // Регистрируем в AssetIDMapping
                assetIDMapping.Register(renderAssetId, AssetIDMapping.AssetType.Model, mesh);
                
                return new ProcessedAsset {
                    prefab = fbxPrefab,
                    mesh = mesh,
                    materials = materials,
                    submeshNames = new string[] { actualName }
                };
            }
            catch (Exception ex) {
                Debug.LogError($"Error extracting mesh from FBX: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        private ProcessedAsset ProcessFBXAsset(int modelId, string fbxPath) {
            // Загружаем FBX как префаб
            GameObject fbxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (fbxPrefab == null) {
                Debug.LogError($"Failed to load FBX: {fbxPath}");
                return null;
            }
            
            // Извлекаем mesh
            Mesh mesh = null;
            MeshFilter[] filters = fbxPrefab.GetComponentsInChildren<MeshFilter>();
            
            if (filters.Length > 0) {
                mesh = filters[0].sharedMesh;
            } else {
                // Альтернативный способ - поиск среди sub-assets
                UnityEngine.Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
                mesh = subAssets.OfType<Mesh>().FirstOrDefault();
            }
            
            if (mesh == null) {
                Debug.LogError($"No mesh found in FBX: {fbxPath}");
                return null;
            }
            
            // Извлекаем материалы из FBX
            Material[] materials = null;
            MeshRenderer[] renderers = fbxPrefab.GetComponentsInChildren<MeshRenderer>();
            
            if (renderers.Length > 0 && renderers[0].sharedMaterials != null) {
                materials = renderers[0].sharedMaterials;
                Debug.Log($"Found {materials.Length} materials in FBX {modelId}");
            } else {
                // Если нет материалов в FBX, создаем дефолтный
                Debug.LogWarning($"No materials found in FBX {modelId}, creating default");
                materials = new Material[] { 
                    new(Shader.Find("Universal Render Pipeline/Lit")) 
                };
            }
            
            assetIDMapping.Register(modelId, AssetIDMapping.AssetType.Model, fbxPrefab);
            return new ProcessedAsset{
                prefab = fbxPrefab,
                mesh = mesh,
                materials = materials,
                submeshNames = GetSubmeshNames(mesh)
            };
        }

        private string[] GetSubmeshNames(Mesh mesh) {
            // Для отладки - пытаемся получить имена submesh'ей
            string[] names = new string[mesh.subMeshCount];
            for (int i = 0; i < mesh.subMeshCount; i++) {
                names[i] = $"Submesh_{i}";
            }
            return names;
        }

        private Material[] CreateMaterialsForModel(List<int> materialIds, JArray materialsData = null) {
            Material[] materials = new Material[materialIds.Count];
            
            for (int i = 0; i < materialIds.Count; i++) {
                int matId = materialIds[i];
                
                // Проверяем кеш
                string matCacheKey = $"mat_{matId}";
                Material cachedMat = AssetDatabase.LoadAssetAtPath<Material>($"Assets/{folderName}/Materials/{matCacheKey}.mat");
                
                if (cachedMat != null) {
                    materials[i] = cachedMat;
                    continue;
                }
                
                // Получаем путь папки для материала
                string matFolderPath = materialPathCache.ContainsKey(matId) 
                    ? materialPathCache[matId] 
                    : GetMaterialFolderPath(matId);
                
                // Используем глобальный словарь материалов
                if (sceneData.materials != null && sceneData.materials.TryGetValue(matId, out MaterialData materialData)) {
                    materials[i] = CreateMaterialFromPlayCanvas(materialData, matFolderPath);
                } else {
                    // Fallback - создаем дефолтный материал
                    materials[i] = new Material(Shader.Find("Universal Render Pipeline/Lit")) {
                        name = $"PC_Material_{matId}"
                    };
                    Debug.LogWarning($"Material {matId} not found in global dictionary, using default");
                }
            }
            
            return materials;
        }

        private Material CreateMaterialFromPlayCanvas(MaterialData materialData, string folderPath = null) {
            string safeName = SanitizeFileName(materialData.name);
            
            Material mat = new(Shader.Find("Universal Render Pipeline/Lit")) {
                name = safeName
            };

            // Основные цвета
            if (materialData.diffuse != null && materialData.diffuse.Length >= 3) {
                Color diffuse = new(
                    materialData.diffuse[0],
                    materialData.diffuse[1],
                    materialData.diffuse[2],
                    1f
                );
                mat.SetColor("_BaseColor", diffuse);
            }
            
            if (materialData.emissive != null && materialData.emissive.Length >= 3) {
                Color emissive = new(
                    materialData.emissive[0],
                    materialData.emissive[1],
                    materialData.emissive[2]
                );
                
                if (emissive != Color.black || materialData.textures?.ContainsKey("emissiveMap") == true) {
                    mat.EnableKeyword("_EMISSION");
                    mat.SetColor("_EmissionColor", emissive * materialData.emissiveIntensity);
                }
            }
            
            // Прозрачность
            if (materialData.opacity < 1f) {
                mat.SetFloat("_Surface", 1);
                mat.SetFloat("_AlphaClip", 1);
                mat.SetFloat("_Cutoff", materialData.opacity * 0.5f);
            }
            
            // Применение текстур
            if (materialData.textures != null) {
                foreach (var texturePair in materialData.textures) {
                    string pcSlot = texturePair.Key;
                    int textureId = texturePair.Value;
                    
                    if (textureId == 0) continue;
                    
                    Texture2D tex = LoadTextureAsset(textureId);
                    if (tex == null) continue;
                    
                    // Маппинг PlayCanvas слотов на Unity
                    switch (pcSlot) {
                        case "diffuseMap":
                            mat.SetTexture("_BaseMap", tex);
                            break;
                        case "normalMap":
                            mat.SetTexture("_BumpMap", tex);
                            mat.EnableKeyword("_NORMALMAP");
                            break;
                        case "emissiveMap":
                            mat.SetTexture("_EmissionMap", tex);
                            break;
                        case "metalnessMap":
                            mat.SetTexture("_MetallicGlossMap", tex);
                            break;
                        case "aoMap":
                            mat.SetTexture("_OcclusionMap", tex);
                            break;
                    }
                }
            }
            
            // Параметры материала
            mat.SetFloat("_Metallic", materialData.metalness);
            mat.SetFloat("_Smoothness", materialData.gloss);
            
            // Сохранение материала
            if (string.IsNullOrEmpty(folderPath)) {
                folderPath = GetMaterialFolderPath(materialData.id);
            }
            
            string matPath = Path.Combine("Assets", folderName, folderPath).Replace('\\', '/');
            
            if (!AssetDatabase.IsValidFolder(matPath)) {
                CreateFolderRecursive(matPath);
            }
            
            string fullPath = $"{matPath}/{safeName}.mat";
            AssetDatabase.CreateAsset(mat, fullPath);
            
            return mat;
        }

        private void CreateFolderRecursive(string folderPath) {
            string[] parts = folderPath.Split('/');
            string currentPath = parts[0];
            
            for (int i = 1; i < parts.Length; i++) {
                string parentPath = currentPath;
                currentPath = $"{currentPath}/{parts[i]}";
                
                if (!AssetDatabase.IsValidFolder(currentPath)) {
                    AssetDatabase.CreateFolder(parentPath, parts[i]);
                }
            }
        }

        private string GetMaterialFolderPath(int materialId) {
            Debug.Log($"=== GetMaterialFolderPath for material {materialId} ===");
            
            // Проверяем в allAssetsCache
            if (allAssetsCache != null && allAssetsCache.TryGetValue(materialId, out PCAsset materialAsset)) {
                string folderPath = GetPlayCanvasFolderPath(materialAsset.folder);
                Debug.Log($"Material {materialId} '{materialAsset.name}' has folder {materialAsset.folder} -> path: '{folderPath}'");
                return folderPath;
            }
            
            // Если материал не найден в кеше, возвращаем путь по умолчанию
            Debug.LogWarning($"Material {materialId} not found in cache, using default path");
            return "content/materials"; // Дефолтный путь как в PlayCanvas
        }

        private int ExtractAssetId(object renderData) {
            try {
                if (renderData is JObject jObj) {
                    string renderType = jObj["type"]?.Value<string>() ?? "";
                    
                    if (renderType != "asset") {
                        return 0;
                    }
                    
                    JToken assetToken = jObj["asset"];
                    if (assetToken == null || assetToken.Type == JTokenType.Null) {
                        return 0;
                    }
                    return assetToken.Value<int>();
                }
                
                // Fallback логика
                return 0;
            }
            catch (Exception ex) {
                Debug.LogError($"Failed to extract asset ID: {ex.Message}");
                return 0;
            }
        }

        private string SanitizeFileName(string name) {
            foreach (char c in Path.GetInvalidFileNameChars()) {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private void ApplyAssetsToScene() {
            int successCount = 0;
            int failCount = 0;
            
            EditorUtility.DisplayProgressBar("Applying Assets", "Processing GameObjects...", 0f);
            
            try {
                foreach (EntityIDMapping.Entry entry in entityMapping.entries) {
                    if (entry.gameObject == null) continue;
                    
                    Entity entity = originalEntities[entry.id];
                    if (entity?.components == null) continue;
                    
                    bool success = false;
                    
                    // ВАЖНО: Проверяем оба компонента - и render, и model
                    if (entity.components.TryGetValue("render", out object renderObj)) {
                        success = ApplyRenderComponent(entry.gameObject, renderObj);
                    }
                    else if (entity.components.TryGetValue("model", out object modelObj)) {
                        success = ApplyModelComponent(entry.gameObject, modelObj);
                    }
                    
                    if (success) successCount++;
                    else failCount++;
                    
                    float progress = (float)(successCount + failCount) / entityMapping.entries.Count;
                    EditorUtility.DisplayProgressBar("Applying Assets", 
                        $"Processed: {successCount + failCount}/{entityMapping.entries.Count}", progress);
                }
            }
            finally {
                EditorUtility.ClearProgressBar();
            }
            
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"Assets applied: {successCount} successful, {failCount} failed");
        }

        private bool ApplyRenderComponent(GameObject obj, object renderData) {
            try {
                string renderType = "";
                JArray materialsDataArray = null;

                MeshFilter meshFilter = obj.GetComponent<MeshFilter>() ?? obj.AddComponent<MeshFilter>();
                MeshRenderer meshRenderer = obj.GetComponent<MeshRenderer>() ?? obj.AddComponent<MeshRenderer>();
                
                if (renderData is JObject jObj) {
                    renderType = jObj["type"]?.Value<string>() ?? "";
                    materialsDataArray = jObj["materialsData"] as JArray;
                    
                    if (renderType != "asset") {
                        return ApplyPrimitiveRender(obj, renderType, renderData);
                    }
                }
                
                // Извлекаем ID ассета
                int assetId = ExtractAssetId(renderData);
                if (assetId == 0) {
                    Debug.LogWarning($"No valid asset ID for {obj.name}");
                    return false;
                }
                
                if (!processedAssets.TryGetValue(assetId, out ProcessedAsset processed)) {
                    Debug.LogWarning($"No processed asset for ID {assetId} on {obj.name}");
                    
                    // Используем существующие переменные без переобъявления
                    meshFilter.sharedMesh = GetPrimitiveMesh(PrimitiveType.Cube);
                    meshRenderer.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit")) {
                        name = "Missing_Material"
                    };
                    
                    return false;
                }
                                
                // Применяем mesh и материалы
                //MeshFilter meshFilter = obj.GetComponent<MeshFilter>() ?? obj.AddComponent<MeshFilter>();
                meshFilter.sharedMesh = processed.mesh;
                
                //MeshRenderer meshRenderer = obj.GetComponent<MeshRenderer>() ?? obj.AddComponent<MeshRenderer>();
                
                // Создаем материалы на основе materialsData если есть
                if (materialsDataArray != null && materialsDataArray.Count > 0) {
                    Material[] materials = new Material[materialsDataArray.Count];
                    for (int i = 0; i < materialsDataArray.Count; i++) {
                        JToken matData = materialsDataArray[i];
                        int matId = matData["id"].Value<int>();
                        
                        // Получаем путь папки для материала из его собственного parent
                        string matFolderPath = GetMaterialFolderPath(matId);
                        if (sceneData.materials != null && sceneData.materials.TryGetValue(matId, out MaterialData materialData)) {
                            materials[i] = CreateMaterialFromPlayCanvas(materialData, matFolderPath);
                        } else {
                            Debug.LogWarning($"Material {matId} not found in global dictionary");
                            materials[i] = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                        }
                    }
                    meshRenderer.sharedMaterials = materials;
                }
                else {
                    // Применяем материалы из FBX
                    if (processed.materials != null && processed.materials.Length > 0) {
                        meshRenderer.sharedMaterials = processed.materials;
                    }
                }
                
                // Настройки рендеринга
                ApplyRenderSettings(meshRenderer, renderData);
                
                return true;
            }
            catch (Exception ex) {
                Debug.LogError($"Failed to apply render component to {obj.name}: {ex.Message}");
                return false;
            }
        }

        private bool ApplyModelComponent(GameObject obj, object modelData) {
            try {
                Debug.Log($"ApplyModelComponent for {obj.name}");
                
                // Проверка GameObject
                if (obj == null) {
                    Debug.LogError("GameObject is null!");
                    return false;
                }
                
                int modelId = 0;
                JArray materialsDataArray = null;
                
                if (modelData is JObject jObj) {
                    JToken assetToken = jObj["asset"];
                    if (assetToken != null && assetToken.Type != JTokenType.Null) {
                        modelId = assetToken.Value<int>();
                    }
                    materialsDataArray = jObj["materialsData"] as JArray;
                    
                    Debug.Log($"Model ID: {modelId}, materialsData count: {materialsDataArray?.Count ?? 0}");
                }
                else if (modelData is ModelComponent modelComp) {
                    if (modelComp.asset != null) {
                        if (modelComp.asset is string assetStr && int.TryParse(assetStr, out int parsedId)) {
                            modelId = parsedId;
                        }
                    }
                }
                
                if (modelId == 0) {
                    Debug.LogError($"No model ID found for {obj.name}");
                    return false;
                }
                
                Debug.Log($"Looking for processed asset {modelId} in dictionary with {processedAssets.Count} entries");
                
                if (!processedAssets.TryGetValue(modelId, out ProcessedAsset processed)) {
                    Debug.LogError($"No processed asset found for model {modelId}");
                    return false;
                }
                
                Debug.Log($"Found processed asset with mesh: {(processed.mesh != null ? processed.mesh.name : null)}");
                
                // ВАЖНОЕ ИСПРАВЛЕНИЕ: Добавляем проверки и используем правильный способ добавления компонентов
                if (!obj.TryGetComponent<MeshFilter>(out var meshFilter)) {
                    Debug.Log($"Adding MeshFilter to {obj.name}");
                    meshFilter = obj.AddComponent<MeshFilter>();
                    
                    // Дополнительная проверка после добавления
                    if (meshFilter == null) {
                        Debug.LogError($"Failed to add MeshFilter to {obj.name}");
                        return false;
                    }
                }
                
                if (processed.mesh != null) {
                    meshFilter.sharedMesh = processed.mesh;
                    Debug.Log($"Assigned mesh {processed.mesh.name} to {obj.name}");
                } else {
                    Debug.LogError($"Processed mesh is null for {obj.name}");
                    return false;
                }
                
                if (!obj.TryGetComponent<MeshRenderer>(out var meshRenderer)) {
                    Debug.Log($"Adding MeshRenderer to {obj.name}");
                    meshRenderer = obj.AddComponent<MeshRenderer>();
                }
                
                // Создаем материалы на основе данных из JSON
                if (materialsDataArray != null && materialsDataArray.Count > 0) {
                    Debug.Log($"Creating {materialsDataArray.Count} materials from JSON data");
                    Material[] materials = new Material[materialsDataArray.Count];
                    for (int i = 0; i < materialsDataArray.Count; i++) {
                        JToken matData = materialsDataArray[i];
                        int matId = matData["id"].Value<int>();
                        
                        // Получаем путь папки для материала
                        string matFolderPath = GetMaterialFolderPath(matId);
                        Debug.Log($"Creating material {matId} in folder: '{matFolderPath}'");
                        
                        if (sceneData.materials != null && sceneData.materials.TryGetValue(matId, out MaterialData materialData)) {
                            materials[i] = CreateMaterialFromPlayCanvas(materialData, matFolderPath);
                        } else {
                            Debug.LogWarning($"Material {matId} not found in global dictionary");
                            materials[i] = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                        }
                    }
                    meshRenderer.sharedMaterials = materials;
                }
                
                Debug.Log($"Successfully applied model to {obj.name}");
                return true;
            }
            catch (Exception ex) {
                Debug.LogError($"Failed to apply model component to {obj.name}: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private bool ApplyPrimitiveRender(GameObject obj, string primitiveType, object renderData) {
            try {
                // Проверка GameObject
                if (obj == null) {
                    Debug.LogError("GameObject is null!");
                    return false;
                }
                
                // Получаем или создаем компоненты с правильными проверками
                MeshFilter meshFilter = obj.GetComponent<MeshFilter>();
                if (meshFilter == null) {
                    Debug.Log($"Adding MeshFilter to {obj.name}");
                    meshFilter = obj.AddComponent<MeshFilter>();
                    if (meshFilter == null) {
                        Debug.LogError($"Failed to add MeshFilter to {obj.name}");
                        return false;
                    }
                }
                
                MeshRenderer meshRenderer = obj.GetComponent<MeshRenderer>();
                if (meshRenderer == null) {
                    Debug.Log($"Adding MeshRenderer to {obj.name}");
                    meshRenderer = obj.AddComponent<MeshRenderer>();
                    if (meshRenderer == null) {
                        Debug.LogError($"Failed to add MeshRenderer to {obj.name}");
                        return false;
                    }
                }
                
                // Создаем примитив
                switch (primitiveType.ToLower()) {
                    case "box":
                        meshFilter.sharedMesh = GetPrimitiveMeshScaled(PrimitiveType.Cube);
                        break;
                    case "plane":
                        meshFilter.sharedMesh = GetPrimitiveMeshScaled(PrimitiveType.Plane);
                        break;
                    case "sphere":
                        meshFilter.sharedMesh = GetPrimitiveMeshScaled(PrimitiveType.Sphere);
                        break;
                    case "cylinder":
                        meshFilter.sharedMesh = GetPrimitiveMeshScaled(PrimitiveType.Cylinder);
                        break;
                    case "capsule":
                        meshFilter.sharedMesh = GetPrimitiveMeshScaled(PrimitiveType.Capsule);
                        break;
                    default:
                        Debug.LogWarning($"Unknown primitive type: {primitiveType}");
                        return false;
                }
                
                // Обработка материалов с поддержкой materialsData
                if (renderData is JObject jObj) {
                    if (jObj["materialsData"] is JArray materialsDataArray && materialsDataArray.Count > 0) {
                        // Создаем материалы из materialsData
                        Material[] materials = new Material[materialsDataArray.Count];
                        for (int i = 0; i < materialsDataArray.Count; i++) {
                            JToken matData = materialsDataArray[i];
                            int matId = matData["id"].Value<int>();
                            
                            // Получаем правильный путь для материала
                            string matFolderPath = GetMaterialFolderPath(matId);
                            if (sceneData.materials.TryGetValue(matId, out MaterialData materialData)) {
                                materials[i] = CreateMaterialFromPlayCanvas(materialData, matFolderPath);
                            }
                        }
                        meshRenderer.sharedMaterials = materials;
                    } else {
                        // Fallback на materialAssets
                        List<int> materialIds = new();
                        if (jObj["materialAssets"] is JArray matArray) {
                            foreach (JToken mat in matArray) {
                                if (mat != null && mat.Type != JTokenType.Null) {
                                    int matId = mat.Value<int>();
                                    if (matId != 0) materialIds.Add(matId);
                                }
                            }
                        }


                        if (materialIds.Count > 0) {
                            // ИСПРАВЛЕНИЕ: Передаем materialsData в CreateMaterialsForModel
                            Material[] materials = CreateMaterialsForModel(materialIds, jObj["materialsData"] as JArray);
                            meshRenderer.sharedMaterials = materials;
                        }
                        else {
                            // Материал по умолчанию
                            Material defaultMat = new(Shader.Find("Universal Render Pipeline/Lit"));
                            meshRenderer.sharedMaterial = defaultMat;
                        }
                    }
                }
                // Настройки рендеринга
                ApplyRenderSettings(meshRenderer, renderData);
                
                Debug.Log($"Applied primitive {primitiveType} to {obj.name}");
                return true;
            }
            catch (Exception ex) {
                Debug.LogError($"Failed to apply primitive to {obj.name}: {ex.Message}");
                return false;
            }
        }

        private static Mesh GetPrimitiveMeshScaled(PrimitiveType type) {
            Mesh originalMesh = GetPrimitiveMesh(type);
            
            // Для плоскости Unity создает 10x10, PlayCanvas использует 1x1
            if (type == PrimitiveType.Plane) {
                // Создаем копию меша с масштабированными вершинами
                Mesh scaledMesh = UnityEngine.Object.Instantiate(originalMesh);
                Vector3[] vertices = scaledMesh.vertices;
                for (int i = 0; i < vertices.Length; i++) {
                    vertices[i] *= 0.1f; // Уменьшаем в 10 раз
                }
                scaledMesh.vertices = vertices;
                scaledMesh.RecalculateBounds();
                return scaledMesh;
            }
            
            return originalMesh;
        }

        private static Mesh GetPrimitiveMesh(PrimitiveType type) {
            if (!primitiveCache.TryGetValue(type, out Mesh mesh)) {
                GameObject temp = GameObject.CreatePrimitive(type);
                mesh = temp.GetComponent<MeshFilter>().sharedMesh;
                DestroyImmediate(temp);
                primitiveCache[type] = mesh;
            }
            return mesh;
        }

        private void ApplyRenderSettings(MeshRenderer renderer, object renderData) {
            // Применяем настройки из PlayCanvas
            if (renderData is JObject jObj) {
                renderer.shadowCastingMode = jObj["castShadows"]?.Value<bool>() ?? true 
                    ? UnityEngine.Rendering.ShadowCastingMode.On 
                    : UnityEngine.Rendering.ShadowCastingMode.Off;
                
                renderer.receiveShadows = jObj["receiveShadows"]?.Value<bool>() ?? true;
                
                // Статичность для батчинга
                bool isStatic = jObj["isStatic"]?.Value<bool>() ?? false;
                if (isStatic) {
                    renderer.gameObject.isStatic = true;
                }
            }
        }

        #endregion CollectAssetDependencies

        #endregion SceneUtility

        #region Download

        private string GetAssetPath(PCAsset asset) {
            // Получаем путь папки из PlayCanvas
            string folderPath = GetPlayCanvasFolderPath(asset.folder);
            
            string extension = asset.type switch {
                "model" => ".fbx",
                "material" => ".mat",
                "texture" => Path.GetExtension(asset.filename) ?? ".png",
                _ => Path.GetExtension(asset.filename) ?? ""
            };
            
            // Используем оригинальное имя файла
            string filename = string.IsNullOrEmpty(asset.filename) 
                ? $"{asset.name}{extension}"
                : Path.GetFileNameWithoutExtension(asset.filename) + extension;
            
            filename = SanitizeFileName(filename);
            
            return Path.Combine("Assets", folderName, folderPath, filename).Replace('\\', '/');
        }
        
        private async Task<Dictionary<int, string>> FetchFoldersFromAPI() {
            Dictionary<int, string> folderPaths = new();
            
            string url = $"https://playcanvas.com/api/projects/{projectId}/assets?branchId={branchId}&limit=10000";
            
            using HttpClient client = new();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {tokenId}");
            
            try {
                string json = await client.GetStringAsync(url);
                JObject root = JObject.Parse(json);
                
                if (root["result"] is JArray resultArray) {
                    // Шаг 1: Собираем все папки
                    Dictionary<int, FolderNode> allFolders = new();
                    List<FolderNode> rootFolders = new();
                    
                    foreach (JToken item in resultArray) {
                        if ((string)item["type"] != "folder") continue;
                        
                        FolderNode folder = new() {
                            id = (int)item["id"],
                            name = (string)item["name"],
                            parentId = null // Инициализируем как null
                        };
                        
                        // Безопасная обработка parent
                        JToken parentToken = item["parent"];
                        if (parentToken != null && parentToken.Type != JTokenType.Null) {
                            folder.parentId = parentToken.Value<int>();
                        }
                        
                        allFolders[folder.id] = folder;
                        
                        // Если parent null или 0 - это корневая папка
                        if (!folder.parentId.HasValue || folder.parentId.Value == 0) {
                            rootFolders.Add(folder);
                            Debug.Log($"Found root folder: {folder.name} (ID: {folder.id})");
                        }
                    }
                    
                    Debug.Log($"Total folders found: {allFolders.Count}, root folders: {rootFolders.Count}");
                    
                    // Шаг 2: Строим дерево папок
                    foreach (var folder in allFolders.Values) {
                        if (folder.parentId.HasValue && folder.parentId.Value != 0) {
                            if (allFolders.TryGetValue(folder.parentId.Value, out FolderNode parent)) {
                                parent.children.Add(folder);
                            } else {
                                Debug.LogWarning($"Parent folder {folder.parentId} not found for folder {folder.name}");
                                // Если родитель не найден, считаем папку корневой
                                rootFolders.Add(folder);
                            }
                        }
                    }
                    
                    // Шаг 3: Рекурсивно строим полные пути
                    foreach (var rootFolder in rootFolders) {
                        BuildFolderPaths(rootFolder, "", folderPaths);
                    }
                    
                    // Шаг 4: Сохраняем в FolderMapping
                    foreach (var kvp in folderPaths) {
                        folderMapping.AddFolder(kvp.Key, allFolders[kvp.Key].name, kvp.Value);
                        
                        //if (showDebugLogs) {
                        //    Debug.Log($"Folder mapping: ID {kvp.Key} -> {kvp.Value}");
                        //}
                    }
                    
                    Debug.Log($"Loaded {folderPaths.Count} folder paths from PlayCanvas");
                }
            }
            catch (Exception ex) {
                Debug.LogError($"Failed to fetch folders: {ex.Message}\n{ex.StackTrace}");
            }
            
            // Сохраняем изменения
            EditorUtility.SetDirty(folderMapping);
            AssetDatabase.SaveAssets();
            
            return folderPaths;
        }

        private void BuildFolderPaths(FolderNode folder, string parentPath, Dictionary<int, string> result) {
            // Строим полный путь для текущей папки
            string currentPath = string.IsNullOrEmpty(parentPath) 
                ? folder.name 
                : $"{parentPath}/{folder.name}";
            
            folder.fullPath = currentPath;
            result[folder.id] = currentPath;
            
            // ОТЛАДКА
            if (showDebugLogs && result.Count < 10) {
                Debug.Log($"Built path for folder {folder.id} ({folder.name}): '{currentPath}'");
            }
            
            // Рекурсивно обрабатываем детей
            foreach (var child in folder.children) {
                BuildFolderPaths(child, currentPath, result);
            }
        }

        private string GetPlayCanvasFolderPath(int folderId) {
            if (folderId == 0) return ""; // Ассет в корне проекта
            
            // Проверяем в маппинге
            string cachedPath = folderMapping?.GetPathById(folderId);
            if (!string.IsNullOrEmpty(cachedPath)) {
                return cachedPath; // Уже содержит правильный путь без префиксов
            }
            
            // Если не нашли в кеше
            Debug.LogWarning($"Folder {folderId} not found in mapping!");
            return $"_UnmappedFolder_{folderId}";
        }

        private string TransformGLBtoFBXAssetUrl(string url, int sourceId) {
            // Для отладки
            Debug.Log($"TransformGLBtoFBXAssetUrl: url={url}, sourceId={sourceId}");
            
            // Если URL уже для FBX, не трансформируем
            if (url.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) {
                return url;
            }
            
            // ИСПРАВЛЕНИЕ: Проверяем, что sourceId валиден
            if (sourceId == 0) {
                Debug.LogWarning($"Cannot transform URL without valid sourceId: {url}");
                return url; // Возвращаем оригинальный URL
            }
            
            // Преобразуем URL для загрузки FBX вместо GLB
            string transformed = Regex.Replace(url,
                @"^(\/api\/assets\/)\d+(\/file\/.+?)\.glb(.*)$",
                $"${{1}}{sourceId}${{2}}.fbx${{3}}",
                RegexOptions.IgnoreCase);
            
            Debug.Log($"Transformed result: {transformed}");
            
            // Если URL не изменился (не подошел под паттерн), возвращаем оригинал
            return transformed != url ? transformed : url;
        }

        private async Task DownloadSceneAssetsOptimized() {
            // Загружаем кеш
            assetCache = AssetCache.Load(AssetCachePath);
            
            // Получаем список всех ассетов из API
            Dictionary<int, PCAsset> allAssets = await FetchAssetsListFromAPI();
            
            Debug.Log($"Fetched {allAssets.Count} assets from API");
            Debug.Log($"Looking for {collectedModels.Count} models");
            Debug.Log($"Looking for {collectedRenderAssets.Count} render assets");
            Debug.Log($"Looking for {collectedTextures.Count} textures");
            
            // Создаем задачи загрузки
            List<DownloadTask> downloadTasks = new();
            
            // 1. Обрабатываем модели
            foreach (KeyValuePair<int, AssetUsageInfo> modelInfo in collectedModels) {
                if (!allAssets.TryGetValue(modelInfo.Key, out PCAsset pcAsset)) {
                    Debug.LogWarning($"Model {modelInfo.Key} not found in API response");
                    continue;
                }
                
                AssetStatus status = CheckAssetStatus(modelInfo.Key, pcAsset);
                
                if (status != AssetStatus.UpToDate) {
                    string downloadUrl = pcAsset.url;
                    
                    if (pcAsset.type == "model" && pcAsset.sourceId != 0) {
                        downloadUrl = TransformGLBtoFBXAssetUrl(downloadUrl, pcAsset.sourceId);
                    }
                    
                    downloadTasks.Add(new DownloadTask {
                        assetId = pcAsset.id,
                        asset = pcAsset,
                        downloadUrl = downloadUrl,
                        targetPath = GetAssetPath(pcAsset),
                        priority = CalculatePriority(pcAsset, modelInfo.Value.usedByEntities.Count)
                    });
                }
            }
            
            // 2. НОВОЕ: Обрабатываем контейнеры используя словарь из sceneData
            if (sceneData.containers != null) {
                foreach (var containerPair in sceneData.containers) {
                    int containerId = containerPair.Key;
                    ContainerData containerData = containerPair.Value;
                    
                    Debug.Log($"Processing container {containerId} '{containerData.name}' with {containerData.renders.Count} renders");
                    
                    // Мапим контейнер на FBX
                    if (containerData.sourceId.HasValue && containerData.sourceId.Value != 0) {
                        containerToFBXMap[containerId] = containerData.sourceId.Value;
                        
                        // Проверяем нужно ли загрузить FBX
                        if (allAssets.TryGetValue(containerData.sourceId.Value, out PCAsset fbxAsset)) {
                            AssetStatus status = CheckAssetStatus(fbxAsset.id, fbxAsset);
                            
                            if (status != AssetStatus.UpToDate) {
                                downloadTasks.Add(new DownloadTask {
                                    assetId = fbxAsset.id,
                                    asset = fbxAsset,
                                    downloadUrl = fbxAsset.url,
                                    targetPath = GetAssetPath(fbxAsset),
                                    priority = CalculatePriority(fbxAsset, containerData.renders.Count) * 2.0f
                                });
                                Debug.Log($"Added FBX {fbxAsset.id} for container {containerId} to download queue");
                            }
                        } else {
                            Debug.LogWarning($"FBX asset {containerData.sourceId.Value} not found in API for container {containerId}");
                        }
                    } else {
                        Debug.LogWarning($"Container {containerId} '{containerData.name}' has no sourceId");
                    }
                }
            }
            
            // 3. Обрабатываем текстуры
            foreach (KeyValuePair<int, AssetUsageInfo> textureInfo in collectedTextures) {
                if (!allAssets.TryGetValue(textureInfo.Key, out PCAsset pcAsset)) {
                    // Пробуем получить информацию из словаря
                    if (sceneData.textures != null && sceneData.textures.TryGetValue(textureInfo.Key, out TextureData texData)) {
                        // Создаем PCAsset из TextureData
                        pcAsset = new PCAsset {
                            id = texData.id,
                            name = texData.name,
                            type = texData.type,
                            filename = texData.filename,
                            url = texData.url,
                            size = texData.size,
                            hash = texData.hash,
                            folder = texData.path?[texData.path.Length - 1] ?? 0
                        };
                    } else {
                        Debug.LogWarning($"Texture {textureInfo.Key} not found in API or scene data");
                        continue;
                    }
                }
                
                AssetStatus status = CheckAssetStatus(textureInfo.Key, pcAsset);
                
                if (status != AssetStatus.UpToDate) {
                    downloadTasks.Add(new DownloadTask {
                        assetId = pcAsset.id,
                        asset = pcAsset,
                        downloadUrl = pcAsset.url,
                        targetPath = GetAssetPath(pcAsset),
                        priority = CalculatePriority(pcAsset, textureInfo.Value.usedByEntities.Count) * 1.5f
                    });
                }
            }
            
            Debug.Log($"Total download tasks: {downloadTasks.Count}");
            Debug.Log($"Container mappings: {containerToFBXMap.Count}");
            
            // 4. Не нужно анализировать материалы - они создаются из словаря
            
            // 5. Сортируем по приоритету
            downloadTasks.Sort((a, b) => b.priority.CompareTo(a.priority));
            
            // 6. Загружаем с батчингом
            await ExecuteDownloadsWithProgress(downloadTasks);
        }

        private float CalculatePriority(PCAsset asset, int usageCount) {
            // Чем меньше файл И чем чаще используется = тем выше приоритет
            float sizePenalty = asset.size / (1024f * 1024f); // Размер в MB
            float usageBonus = usageCount * 10f; // Бонус за частоту использования
            
            // Бонус для определенных типов
            float typeFactor = asset.type switch {
                "texture" => 1.5f,  // Текстуры важнее
                "material" => 1.2f,
                "model" => 1.0f,
                _ => 0.8f
            };
            
            return (usageBonus * typeFactor) / (sizePenalty + 1f);
        }

        private async Task ExecuteDownloadsWithProgress(List<DownloadTask> tasks) {
            const int MAX_CONCURRENT = 5;
            SemaphoreSlim semaphore = new(MAX_CONCURRENT);
            HttpClient httpClient = new();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {tokenId}");
            
            int completed = 0;
            int total = tasks.Count;
            bool cancelled = false;
            
            // Создаем структуру папок заранее
            PrepareOptimizedFolderStructure(tasks);
            
            EditorApplication.LockReloadAssemblies();
            AssetDatabase.StartAssetEditing();
            
            try {
                // Используем IProgress для thread-safe обновления прогресса
                Progress<string> progress = new(status => {
                    // Этот код выполняется в главном потоке
                    if (EditorUtility.DisplayCancelableProgressBar(
                        "PlayCanvas Import", 
                        status, 
                        (float)completed / total)) {
                        cancelled = true;
                    }
                });
                
                IEnumerable<Task> downloadPromises = tasks.Select(async task => {
                    if (cancelled) return;
                    
                    await semaphore.WaitAsync();
                    
                    try {
                        await DownloadSingleAsset(httpClient, task);
                        
                        Interlocked.Increment(ref completed);
                        
                        // Отправляем обновление через IProgress
                        ((IProgress<string>)progress).Report($"Downloaded {completed}/{total}: {task.asset.name}");
                    }
                    finally {
                        semaphore.Release();
                    }
                });
                
                await Task.WhenAll(downloadPromises);
            }
            finally {
                AssetDatabase.StopAssetEditing();
                EditorApplication.UnlockReloadAssemblies();
                EditorUtility.ClearProgressBar();
                
                // Сохраняем обновленный кеш
                assetCache.Save(AssetCachePath);
                
                AssetDatabase.Refresh();
                
                if (cancelled) {
                    Debug.Log("Download cancelled by user");
                } else {
                    Debug.Log($"Successfully downloaded {completed} assets");
                }
            }
        }

        private async Task DownloadSingleAsset(HttpClient client, DownloadTask task) {
            try {
                // Создаем директорию если нужно
                string directory = Path.GetDirectoryName(task.targetPath);
                if (!Directory.Exists(directory)) { 
                    Directory.CreateDirectory(directory);
                }
                
                // ИСПРАВЛЕНИЕ: Преобразуем относительный URL в абсолютный
                string fullUrl = task.downloadUrl;
                if (fullUrl.StartsWith("/")) {
                    fullUrl = $"https://playcanvas.com{fullUrl}";
                }
                
                // Добавляем параметры аутентификации если их нет
                if (!fullUrl.Contains("branchId=") && !string.IsNullOrEmpty(branchId)) {
                    fullUrl += fullUrl.Contains("?") ? "&" : "?";
                    fullUrl += $"branchId={branchId}";
                }
                
                // Загружаем файл
                HttpResponseMessage response = await client.GetAsync(fullUrl);
                response.EnsureSuccessStatusCode();
                

                
                if (!response.IsSuccessStatusCode) {
                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) {
                        Debug.LogError($"Access denied (403) for {task.asset.name}. Check authorization token and project permissions.");
                        
                        // Для GLB файлов пробуем альтернативный URL
                        if (fullUrl.Contains(".glb") && task.asset.sourceId != 0) {
                            Debug.Log($"Trying alternative FBX URL for {task.asset.name}");
                            string altUrl = TransformGLBtoFBXAssetUrl(fullUrl, task.asset.sourceId);
                            if (altUrl != fullUrl) {
                                response = await client.GetAsync(altUrl);
                            }
                        }
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) {
                        Debug.LogError($"Access denied (403) for {task.asset.name}. Check authorization token and project permissions.");
                        
                        // Для container assets пробуем найти прямой FBX
                        if (task.asset.type == "container" && allAssets != null) {
                            var directFBX = allAssets.Values.FirstOrDefault(a => 
                                a.type == "model" && 
                                a.name == task.asset.name &&
                                a.url.EndsWith(".fbx"));
                                
                            if (directFBX != null) {
                                Debug.Log($"Trying direct FBX URL for container {task.asset.name}");
                                string fbxUrl = directFBX.url;
                                if (fbxUrl.StartsWith("/")) {
                                    fbxUrl = $"https://playcanvas.com{fbxUrl}?branchId={branchId}";
                                }
                                response = await client.GetAsync(fbxUrl);
                            }
                        }
                    }
                    
                    if (!response.IsSuccessStatusCode) {
                        Debug.LogError($"Failed to download {task.asset.name}: {response.StatusCode} ({response.ReasonPhrase})");
                        return;
                    }
                }


                byte[] content = await response.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(task.targetPath, content);
                
                // Обновляем кеш
                assetCache.entries[task.assetId] = new AssetCacheEntry {
                    playCanvasId = task.assetId,
                    localPath    = task.targetPath,
                    hash         = task.asset.hash,
                    lastModified = task.asset.modifiedAt ?? DateTime.Now,
                    fileSize     = content.Length,
                    downloadedAt = DateTime.Now,
                    usageCount   = collectedModels.ContainsKey(task.assetId) ? collectedModels[task.assetId].usedByEntities.Count : 1
                };
                
                if (showDebugLogs) {
                    Debug.Log($"Downloaded: {task.asset.name} -> {task.targetPath}");
                }
            }
            catch (Exception ex) {
                Debug.LogError($"Failed to download {task.asset.name}: {ex.Message}");
            }
        }

        private void PrepareOptimizedFolderStructure(List<DownloadTask> tasks) {
            // Собираем уникальные пути папок
            HashSet<string> requiredFolders = new HashSet<string>();
            
            foreach (DownloadTask task in tasks) {
                string dir = Path.GetDirectoryName(task.targetPath).Replace('\\', '/');
                
                // Добавляем все родительские папки
                while (!string.IsNullOrEmpty(dir) && dir != "Assets") {
                    requiredFolders.Add(dir);
                    dir = Path.GetDirectoryName(dir).Replace('\\', '/');
                }
            }
            
            // Создаем только нужные папки (сортируем по глубине)
            foreach (string folder in requiredFolders.OrderBy(f => f.Split('/').Length)) {
                if (!AssetDatabase.IsValidFolder(folder)) {
                    string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
                    string folderName = Path.GetFileName(folder);
                    
                    // Проверяем что родительская папка существует
                    if (!AssetDatabase.IsValidFolder(parent)) {
                        Debug.LogWarning($"Parent folder doesn't exist: {parent}");
                        continue;
                    }
                    
                    AssetDatabase.CreateFolder(parent, folderName);
                    if (showDebugLogs) {
                        Debug.Log($"Created folder: {folder}");
                    }
                }
            }
        }

        private async Task<Dictionary<int, PCAsset>> FetchAssetsListFromAPI() {
            Dictionary<int, PCAsset> result = new();
            
            if (string.IsNullOrEmpty(tokenId) || string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(branchId)) {
                Debug.LogError("Token ID, Project ID and Branch ID must be filled.");
                return result;
            }
            
            // ВАЖНО: Сначала загружаем структуру папок
            Debug.Log("Loading folder structure from PlayCanvas...");
            await FetchFoldersFromAPI();
            
            // Проверяем результат
            Debug.Log($"Folders loaded: {folderMapping.folders.Count}");
            
            string url = $"https://playcanvas.com/api/projects/{projectId}/assets?branchId={branchId}&skip=0&limit=10000";
            
            using (HttpClient client = new()) {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {tokenId}");
                
                try {
                    string json = await client.GetStringAsync(url);
                    JObject root = JObject.Parse(json);
                    
                    Debug.Log($"API Response: {json.Length} chars");
                    
                    if (root["result"] is JArray resultArray) {
                        Debug.Log($"Found {resultArray.Count} assets in API response");
                        
                        foreach (JToken item in resultArray) {
                            // ИСПРАВЛЕНИЕ: Безопасное извлечение ID
                            JToken idToken = item["id"];
                            if (idToken == null || idToken.Type == JTokenType.Null) {
                                continue;
                            }
                            int assetId = idToken.Value<int>();
                            
                            string assetType = (string)item["type"];
                            
                            // Расширяем список поддерживаемых типов
                            if (assetType != "model" && assetType != "material" && 
                                assetType != "texture" && assetType != "render" && 
                                assetType != "template" && assetType != "container") {
                                continue;
                            }
                            
                            PCAsset asset = new() {
                                id = assetId,
                                name = (string)item["name"],
                                type = assetType,
                                folder = 0,
                                modifiedAt = null
                            };
                            
                            JToken parentToken = item["parent"];
                            if (parentToken != null && parentToken.Type != JTokenType.Null) {
                                asset.folder = parentToken.Value<int>();
                            }
                            
                            string modifiedAtStr = (string)item["modifiedAt"];
                            if (!string.IsNullOrEmpty(modifiedAtStr) && DateTime.TryParse(modifiedAtStr, out DateTime dt)) {
                                asset.modifiedAt = dt;
                            }
                            
                            // Извлекаем данные файла
                            if (item["file"] is JObject fileObj) {
                                asset.filename = (string)fileObj["filename"];
                                
                                // ИСПРАВЛЕНИЕ: Безопасное извлечение size
                                JToken sizeToken = fileObj["size"];
                                if (sizeToken != null && sizeToken.Type != JTokenType.Null) {
                                    asset.size = sizeToken.Value<int>();
                                }
                                
                                asset.hash = (string)fileObj["hash"] ?? "";
                                asset.url = (string)fileObj["url"] ?? "";
                            }
                            
                            // Для render assets сохраняем связь с container/template
                            if (assetType == "render" || assetType == "model") {
                                JToken sourceIdToken = item["sourceId"];
                                if (sourceIdToken != null && sourceIdToken.Type != JTokenType.Null) {
                                    asset.sourceId = sourceIdToken.Value<int>();
                                }
                            }
                            
                            result[assetId] = asset;
                            
                            // Отладка: показываем путь для некоторых ассетов
                            if (showDebugLogs && result.Count <= 5) {
                                string path = GetAssetPath(asset);
                                Debug.Log($"Asset {asset.name} will be saved to: {path}");
                            }
                        }
                        
                        Debug.Log($"Parsed {result.Count} assets (models: {result.Values.Count(a => a.type == "model")})");
                    }
                }
                catch (Exception ex) {
                    Debug.LogError($"Failed to fetch assets list: {ex.Message}\n{ex.StackTrace}");
                }
            }
            
            return result;
        }

        private Task DownloadMaterialAssets(Dictionary<int, PCAsset> allAssets, List<DownloadTask> downloadTasks) {
            foreach (KeyValuePair<int, AssetUsageInfo> matInfo in collectedMaterials) {
                if (!allAssets.TryGetValue(matInfo.Key, out PCAsset pcAsset)) {
                    continue;
                }
                
                AssetStatus status = CheckAssetStatus(matInfo.Key, pcAsset);
                if (status != AssetStatus.UpToDate) {
                    // Для материалов важно сохранить правильный путь
                    string targetPath = GetAssetPath(pcAsset);
                    
                    DownloadTask task = new() {
                        assetId = pcAsset.id,
                        asset = pcAsset,
                        downloadUrl = pcAsset.url,
                        targetPath = targetPath,
                        priority = CalculatePriority(pcAsset, matInfo.Value.usedByEntities.Count)
                    };
                    
                    downloadTasks.Add(task);
                    
                    if (showDebugLogs) {
                        Debug.Log($"Material {pcAsset.name} will be saved to: {targetPath}");
                    }
                }
            }

            return Task.CompletedTask;

        }

        #endregion Download

        private Texture2D LoadTextureAsset(int textureId) {
            // Проверяем, загружена ли текстура
            if (!assetCache.entries.TryGetValue(textureId, out AssetCacheEntry cached)) {
                Debug.LogWarning($"Texture {textureId} not found in cache");
                return null;
            }
            
            // Загружаем текстуру из файла
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(cached.localPath);
            if (texture == null) {
                Debug.LogError($"Failed to load texture from {cached.localPath}");
            } else {
                assetIDMapping.Register(textureId, AssetIDMapping.AssetType.Texture, texture);
            }
            
            return texture;
        }

        private int ExtractModelId(object renderData) {
            try {
                if (renderData is JObject jObj) {
                    // Сначала проверяем тип рендера
                    string renderType = jObj["type"]?.Value<string>() ?? "";
                    
                    // Примитивы не имеют modelId
                    if (renderType != "asset") {
                        return 0;
                    }
                    
                    // Извлекаем asset только для type="asset"
                    JToken assetToken = jObj["asset"];
                    if (assetToken == null || assetToken.Type == JTokenType.Null) {
                        return 0;
                    }
                    return assetToken.Value<int>();
                }
                
                if (renderData is Dictionary<string, object> dict && dict.ContainsKey("asset")) {
                    object asset = dict["asset"];
                    if (asset == null) return 0;
                    return Convert.ToInt32(asset);
                }
                
                // Fallback
                string json = JsonConvert.SerializeObject(renderData);
                JObject parsed = JObject.Parse(json);
                
                // Проверяем тип перед извлечением asset
                string type = parsed["type"]?.Value<string>() ?? "";
                if (type != "asset") return 0;
                
                return parsed["asset"]?.Value<int>() ?? 0;
            }
            catch (Exception ex) {
                Debug.LogError($"Failed to extract model ID: {ex.Message}");
                return 0;
            }
        }

        private async Task AnalyzeMaterialsAndTextures(Dictionary<int, PCAsset> allAssets, List<DownloadTask> downloadTasks) {
            // Загрузка материалов уже обрабатывается в основном методе
            // Этот метод остается для совместимости
            await Task.CompletedTask;
        }

        #region Math

        private int MakePowerOfTwo(int x) { // Возвращает ближайшую степень двойки, которая больше или равна x
            int p = 1;
            while (p < x) p <<= 1;
            return p;
        }

        #endregion Math
    }

    #region Converters

    public class ComponentConverter : JsonConverter {
        public override bool CanConvert(Type objectType) {
            return objectType == typeof(Dictionary<string, object>);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer) {
            Dictionary<string, object> result = new Dictionary<string, object>();
            JObject jobject = JObject.Load(reader);
            
            foreach (KeyValuePair<string, JToken> prop in jobject) {
                string key = prop.Key;
                JToken value = prop.Value;

                result[key] = key switch {
                    "light" => value.ToObject<LightComponent>(),
                    // ИЗМЕНЕНИЕ: оставляем model и render как JObject для обработки зависимостей
                    "model" => value,
                    "render" => value,
                    _ => value, // Оставляем как JToken для неизвестных компонентов
                };
            }
            
            return result;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) {
            throw new NotImplementedException();
        }
    }

    public class XYZConverter : JsonConverter<List<float>> {
        public override List<float> ReadJson(JsonReader reader, Type objectType, List<float> existingValue, bool hasExistingValue, JsonSerializer serializer) {
            JObject obj = serializer.Deserialize<JObject>(reader);
            if (obj != null && obj["x"] != null && obj["y"] != null && obj["z"] != null) {
                return new List<float> { (float)obj["x"], (float)obj["y"], (float)obj["z"] };
            }
            return new List<float> { 0, 0, 0 };
        }

        public override void WriteJson(JsonWriter writer, List<float> value, JsonSerializer serializer) {
            writer.WriteStartObject();
            writer.WritePropertyName("x");
            writer.WriteValue(value[0]);
            writer.WritePropertyName("y");
            writer.WriteValue(value[1]);
            writer.WritePropertyName("z");
            writer.WriteValue(value[2]);
            writer.WriteEndObject();
        }
    }

    public class ColorArrayConverter : JsonConverter<Color> {
        public override Color ReadJson(JsonReader reader, Type objectType, Color existingValue, bool hasExistingValue, JsonSerializer serializer) {
            // Десериализуем массив чисел
            float[] colorArray = serializer.Deserialize<float[]>(reader);
            
            if (colorArray == null || colorArray.Length < 3) {
                return Color.white; // Возвращаем белый цвет по умолчанию
            }
            
            // Преобразуем массив в Color (RGB)
            return new Color(colorArray[0], colorArray[1], colorArray[2]);
        }

        public override void WriteJson(JsonWriter writer, Color value, JsonSerializer serializer) {
            // Сериализуем Color обратно в массив чисел
            float[] colorArray = new float[] { value.r, value.g, value.b };
            serializer.Serialize(writer, colorArray);
        }
    }

    #endregion

    #region SceneData

    // Сущность сцены
    [System.Serializable]
    public class Entity {
        public string id;
        public string name;

        [JsonConverter(typeof(XYZConverter))]
        public List<float> position;

        [JsonConverter(typeof(XYZConverter))]
        public List<float> rotation;

        [JsonConverter(typeof(XYZConverter))]
        public List<float> scale;

        //public Dictionary<string, JToken> components;

        // Изменяем тип хранения компонентов
        
        public Dictionary<string, object> components;
        
        // Вспомогательные методы для типизированного доступа
        public T GetComponent<T>(string name) where T : class{
            return components?.TryGetValue(name, out object component) == true
                ? component as T ?? JsonConvert.DeserializeObject<T>(component.ToString())
                : null;
        }


        [SerializeReference] // Добавляем атрибут для предотвращения проблем сериализации
        public List<Entity> children = new();

        public string parent;
        public string material;
        public float[] color;
    }

    public class ModelComponent {
        public string asset { get; set; }
        // Другие свойства модели
    }

    public class LightComponent {
        public string type { get; set; }
        public int shape { get; set; }
        public float intensity { get; set; }
        public float range { get; set; }
        public int innerConeAngle { get; set; }
        public int outerConeAngle { get; set; }
        public bool isStatic { get; set; }
        public bool isEnabled { get; set; }
        public bool castShadows { get; set; }

        [JsonConverter(typeof(ColorArrayConverter))]
        public Color color { get; set; }
    }

    [System.Serializable]
    public class SceneData {
        public Dictionary<int, MaterialData> materials;
        public Dictionary<int, TextureData> textures;
        public Dictionary<int, ContainerData> containers;
        public Dictionary<int, ModelData> models;
        public Entity root;
        public SceneSettings scene;
    }

    [System.Serializable]
    public class SceneSettings {
        public SkyboxSettings skybox;
        public float[] ambientLight;
        public object layers;
    }

    [System.Serializable]
    public class SkyboxSettings {
        public int texture;
        public float intensity;
        public float[] rotation;
    }

    [System.Serializable]
    public class PCAsset {
        public int id;
        public string name;
        public string type;
        public int folder;  // или parent
        public string filename;
        public int size;
//        public string url;
        public string url;
        public DateTime? modifiedAt; // nullable DateTime
        public int sourceId;
        public string localPath;
        public string hash;


        // Добавить для поддержки связей между ассетами
        public int? containerId; // Для render ассетов - ссылка на container
        public int? templateId;  // Для render ассетов - ссылка на template
    }

    [System.Serializable]
    public class PCFolder {
        public int id;
        public string name;
        //public int parentId;
        public List<PCFolder> subfolders = new(); // Ensure this is always initialized

        //public PCFolder() {
        //    subfolders = new List<PCFolder>();
        //}
    }

    public class RenderComponent {
        public bool enabled { get; set; } = true;
        public string type { get; set; } // "asset", "plane", "box", etc.
        public object asset { get; set; } // может быть int или null
        public JArray materialAssets { get; set; }
        public JArray layers { get; set; }
        public object batchGroupId { get; set; }
        public bool castShadows { get; set; } = true;
        public bool castShadowsLightmap { get; set; } = false;
        public bool receiveShadows { get; set; } = true;
        public bool lightmapped { get; set; } = false;
        public float lightmapSizeMultiplier { get; set; } = 1f;
        public bool castShadowsLightMap { get; set; } = true;
        public bool lightMapped { get; set; } = false;
        public float lightMapSizeMultiplier { get; set; } = 1f;
        public bool isStatic { get; set; } = false;
        public object rootBone { get; set; }
    }
    
    [System.Serializable]
    public class MaterialData {
        public int id;
        public string name;
        public float[] diffuse;
        public float[] specular;
        public float[] emissive;
        public float emissiveIntensity;
        public float opacity;
        public float metalness;
        public float gloss;
        public bool glossInvert;
        public int blendType;
        public float alphaTest;
        public bool alphaToCoverage;
        public bool twoSidedLighting;
        public Dictionary<string, int> textures;
        public float[] diffuseMapTiling;
        public float[] diffuseMapOffset;
    }

    [System.Serializable]
    public class TextureData {
        public int id;
        public string name;
        public string type;
        public int[] path;
        public string filename;
        public string url;
        public int size;
        public string hash;
    }

    [System.Serializable]
    public class ContainerData {
        public int id;
        public string name;
        public string type;
        public int? sourceId;
        public List<RenderInfo> renders;
    }

    [System.Serializable]
    public class RenderInfo {
        public int id;
        public string name;
        public int index;
    }

    [System.Serializable]
    public class ModelData {
        public int id;
        public string name;
        public string type;
        public int[] path;
    }


    #endregion SceneData
}

#endif