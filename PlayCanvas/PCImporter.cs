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
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

#endregion using

namespace Assets.Editor.PlayCanvas {
    public class PCImporter : EditorWindow {

        #region Parameters

        [NonSerialized]
        private EntityIDMapping entityMapping; // Ссылка на маппинг ID

        [NonSerialized]
        private FolderMapping folderMapping; // Ссылка на маппинг папок


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

        private bool statsInitialized = false;
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


        // Поля Playcanvas
        private string nickname = "";
        private string tokenId = "";
        private string projectId = "";
        private string branchId = "";

        private Vector2 scrollPosition = Vector2.zero;

        // Поля для папок
        private Dictionary<int, PCFolder> folderById = new();
        private List<PCFolder> rootFolders = new();

        private const string folderName = "PlayCanvasData"; // Папка для хранения данных

        private const string entityMappingPath = "EntityIDMapping.asset"; // Папка для хранения данных
        private const string folderMappingPath = "FolderMapping.asset"; // Папка для хранения данных

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
            
            statsInitialized = false;
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

            if (GUILayout.Button("Import scene")) {
                if (string.IsNullOrEmpty(entityJsonPath)) {
                    Debug.LogError("Entity JSON Path is empty.");
                    return;
                }

                targetFolderMat = CreateLightMaterialsFolder();

                if (targetFolderMat == null) {
                    Debug.LogError("Failed to create material folder.");
                    return;
                }else{
                    if (showDebugLogs) Debug.Log($"Material folder created at: {targetFolderMat}");
                }

                ImportScene();
                AssetDatabase.Refresh();
            }

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

            if (GUILayout.Button("Create Folders from JSON")){
                string jsonPath = EditorUtility.OpenFilePanel("Select PlayCanvas JSON", "", "json");
                if (!string.IsNullOrEmpty(jsonPath)){
                    ParseFoldersFromJson(jsonPath);
                    CreateUnityFolders("Assets/PlayCanvasImports");
                    SaveFolderMapping();
                    AssetDatabase.Refresh();
                    Debug.Log("Folder structure created and mapped successfully!");
                }
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

        /// <summary>
        /// Initializes the mappings for EntityID and Folder, ensuring they are loaded or created if not existing.
        /// </summary>
        private void InitializeMappings() {
            string cleanFolderName = string.Concat(folderName.Split(System.IO.Path.GetInvalidFileNameChars()));
            string folderPath = $"Assets/{cleanFolderName}";

            // Create the folder if it doesn't exist
            if (!AssetDatabase.IsValidFolder(folderPath)) {
                string parentFolder = "Assets";
                string newFolder = cleanFolderName;
                
                // Ensure we can create the folder
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

            // Now create or load the mappings
            string entityMappingFullPath = $"Assets/{cleanFolderName}/{entityMappingPath}";
            entityMapping = AssetDatabase.LoadAssetAtPath<EntityIDMapping>(entityMappingFullPath);
            if (entityMapping == null) {
                entityMapping = ScriptableObject.CreateInstance<EntityIDMapping>();
                AssetDatabase.CreateAsset(entityMapping, entityMappingFullPath);
            }
            entityMapping.entries.Clear();

            string folderMappingFullPath = $"Assets/{cleanFolderName}/{folderMappingPath}";
            folderMapping = AssetDatabase.LoadAssetAtPath<FolderMapping>(folderMappingFullPath);
            if (folderMapping == null) {
                folderMapping = ScriptableObject.CreateInstance<FolderMapping>();
                AssetDatabase.CreateAsset(folderMapping, folderMappingFullPath);
            }
            folderMapping.folders.Clear();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (showDebugLogs) Debug.Log("Mappings initialized.");
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

        private void ProcessTypedLightComponent(LightComponent light, ref SceneStatistics stats)
        {
            // Считаем по типам
            switch (light.type?.ToLower())
            {
                case "point": stats.PointLights++; break;
                case "spot": stats.SpotLights++; break;
                case "directional": stats.DirectionalLights++; break;
            }
            
            // Считаем по формам
            switch (light.shape)
            {
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

            using (StreamReader reader = new(jsonFilePath)) { // Чтение JSON файла
                jsonContent = reader.ReadToEnd();
            }

            lastJsonFilePath = jsonFilePath;

            try {
                JsonSerializerSettings settings = new() {
                    NullValueHandling = NullValueHandling.Ignore,
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    Converters = new List<JsonConverter> {
                        new ComponentConverter() // Конвертер для компонентов
                    }
                };

                sceneData = JsonConvert.DeserializeObject<SceneData>(jsonContent, settings);

                if (sceneData?.root == null) {
                    Debug.LogError("Failed to parse JSON data or invalid structure.");
                    sceneData = null;
                }
                else if (showDebugLogs) {
                    Debug.Log($"Scene data loaded successfully from {jsonFilePath}");
                }

            } catch (JsonException ex) {
                Debug.LogError($"Error parsing JSON: {ex.Message}");
                sceneData = null;
            } catch (Exception ex) {
                Debug.LogError($"Unexpected error loading scene data: {ex.Message}");
                sceneData = null;
            }
        }

        public void ImportScene() {
            LoadSceneDataFromJsonFile(entityJsonPath); // Загружаем данные сцены из JSON

            if (sceneData?.root == null) {
                Debug.LogError("Import failed: Invalid scene data.");
                return;
            }

            ClearSceneHierarchy(); // Очищаем иерархию сцены


            stats = new SceneStatistics();

            CollectSceneStats(sceneData.root, ref stats);

            if (showDebugLogs) {
                Debug.Log($"Total Nodes: {stats.TotalNodes}");
                Debug.Log($"Mesh Nodes: {stats.MeshNodes}");
                Debug.Log($"Total Lights: {stats.TotalLights}");
                Debug.Log($"Point Lights: {stats.PointLights}");
                Debug.Log($"Spot Lights: {stats.SpotLights}");
                Debug.Log($"Directional Lights: {stats.DirectionalLights}");
                Debug.Log($"Rectangle Lights: {stats.RectangleLights}");
                Debug.Log($"Disk Lights: {stats.DiskLights}");
                Debug.Log($"Cone Lights: {stats.ConeLights}");
            }
            
            InitializeHDRAtlas(stats.DiskLights + stats.RectangleLights); // Инициализируем HDR атлас
            CreateGameObjectHierarchy(sceneData.root); // Создаем иерархию объектов

            EditorUtility.SetDirty(entityMapping);

            ApplyHDRAtlas(); // Применяем атлас к материалу
            //statsCollected = false;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
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
                case 0:
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
                case 1:
                    ConfigureRectangleLight(obj, color, intensity, scale);
                    if (showDebugLogs) Debug.Log("Configured rectangle light: " + obj.name);
                    break;
                case 2:
                    ConfigureDiscLight(obj, color, intensity, scale);
                    if (showDebugLogs) Debug.Log("Configured disc light: " + obj.name);
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

            ApplyPlayCanvasTransform(obj, pcPos, pcEuler, pcScale);

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
        public static void ApplyPlayCanvasTransform(GameObject obj, Vector3 pcPosition, Vector3 pcEulerAngles, Vector3 pcScale) {
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

            Quaternion rotU = GetUnityRotation(pcEulerAngles);

            // Apply transformation
            obj.transform.SetLocalPositionAndRotation(posU, rotU);
            obj.transform.localScale = scaleU;
        }

        // Конвертирует углы Эйлера PlayCanvas (порядок XYZ) в кватернион
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

        /// <summary>
        /// Парсинг папок из JSON-файла
        /// </summary>
        /// <param name="jsonPath">Путь к JSON-файлу</param>
        private void ParseFoldersFromJson(string jsonPath){
            
            string json = File.ReadAllText(jsonPath);   // Чтение JSON-файла
            JObject root = JObject.Parse(json);         // Парсинг JSON в JObject
            JArray result = (JArray)root["result"];     // Получение массива результатов

            folderById.Clear();  // Очистка словаря папок
            rootFolders.Clear(); // Очистка списка корневых папок

            // Шаг 1: Извлекаем все папки
            foreach (JToken item in result){
                if (item["type"]?.ToString() == "folder"){ // Проверяем, является ли элемент папкой
                    
                    int? nullableId = (int?)item["id"]; // Пытаемся получить id
                    if (!nullableId.HasValue) continue; // Если id null, пропускаем элемент
                    int id = nullableId.Value;          // Получаем id

                    string name = item["name"]?.ToString() ?? $"Unnamed_{id}"; // Получаем имя

                    // Сохраняем id в словаре
                    int parentId = 0;                                                      // Пытаемся получить parentId
                    if (item["parent"] != null && item["parent"].Type != JTokenType.Null){ // Если parentId null, пропускаем элемент
                        parentId = (int)item["parent"];                                    // Получаем parentId
                    }

                    // Создаем объект папки
                    PCFolder folder = new() {
                        id = id,            // Сохраняем id
                        name = name,        // Сохраняем имя
                        parentId = parentId // Сохраняем parentId
                    };
                    folderById[id] = folder; // Сохраняем id в словаре
                }
            }

            Debug.Log($"Parsed {folderById.Count} folders from JSON.");

            // Шаг 2: Строим иерархию
            foreach (PCFolder folder in folderById.Values){ // Проходимся по всем папкам
                if (folder.parentId == 0){ // Если parentId = 0, это корневая папка
                    rootFolders.Add(folder); // Добавляем папку в список корневых
                }else if (folderById.TryGetValue(folder.parentId, out PCFolder parentFolder)){ // Если parentId есть в словаре
                    parentFolder.subfolders.Add(folder); // Добавляем папку в список подкаталогов родительской папки
                }
            }

            Debug.Log($"Found {rootFolders.Count} root folders.");
        }

        /// <summary>
        /// Создает иерархию папок в Unity на основе данных из PlayCanvas.
        /// </summary>
        /// <param name="unityRootPath">Корневой путь в Unity, где будут созданы папки.</param>
        private void CreateUnityFolders(string unityRootPath) {
            // Инициализируем folderMapping, если он равен null
            if (folderMapping == null) {
                string cleanFolderName = string.Concat(folderName.Split(System.IO.Path.GetInvalidFileNameChars()));
                string folderMappingPath = $"Assets/{cleanFolderName}/FolderMapping.asset";
                
                // Убеждаемся, что родительская директория существует
                string parentFolder = $"Assets/{cleanFolderName}"; // Папка для хранения маппинга
                if (!AssetDatabase.IsValidFolder(parentFolder)) { // Если родительская директория не существует
                    AssetDatabase.CreateFolder("Assets", cleanFolderName); // Создаем родительскую директорию
                }
                
                folderMapping = AssetDatabase.LoadAssetAtPath<FolderMapping>(folderMappingPath); // Загружаем существующий объект FolderMapping
                
                // Создаем новый объект FolderMapping, если он не был загружен
                if (folderMapping == null) {
                    folderMapping = ScriptableObject.CreateInstance<FolderMapping>();
                    AssetDatabase.CreateAsset(folderMapping, folderMappingPath);
                }
            }

            // Проверяем и создаем корневой путь, если он пуст
            if (string.IsNullOrEmpty(unityRootPath)) {
                unityRootPath = "Assets/PlayCanvasImports"; // Папка по умолчанию
            }

            try {
                // Создаем корневую папку, если она не существует
                if (!AssetDatabase.IsValidFolder(unityRootPath)) {
                    string parent = Path.GetDirectoryName(unityRootPath);
                    string folderName = Path.GetFileName(unityRootPath);
                    
                    // Проверяем, существует ли родительская папка
                    if (!AssetDatabase.IsValidFolder(parent)) {
                        Debug.LogError($"Родительская папка не существует: {parent}");
                        return;
                    }
                    
                    AssetDatabase.CreateFolder(parent, folderName);
                }

                // Рекурсивно создаем все папки
                HashSet<string> createdPaths = new HashSet<string>();
                AssetDatabase.StartAssetEditing();
                try {
                    if (rootFolders != null) {
                        foreach (PCFolder folder in rootFolders) {
                            if (folder != null) {
                                CreateFolderRecursive(folder, unityRootPath, createdPaths);
                            }
                        }
                    }
                }
                finally {
                    AssetDatabase.StopAssetEditing();
                }
            }
            catch (Exception ex) {
                Debug.LogError($"Не удалось создать папки: {ex.Message}");
            }
            finally {
                SaveFolderMapping(); // Сохраняем отображение папок
            }
            AssetDatabase.Refresh(); // Только один раз после всех изменений!
        }

        /// <summary>
        /// Рекурсивно создает папки, основанные на структуре папок из PlayCanvas.
        /// </summary>
        /// <param name="folder">Папка, которую нужно создать.</param>
        /// <param name="parentPath">Путь к родительской папке.</param>
        /// <param name="createdPaths">Множество созданных путей.</param>
        private void CreateFolderRecursive(PCFolder folder, string parentPath, HashSet<string> createdPaths) {
            if (folder == null || 
                string.IsNullOrEmpty(parentPath) || 
                folderById == null || 
                folderById.Count == 0 || 
                folder.id == 0 || 
                string.IsNullOrEmpty(folder.name)) return;

            string folderName = SanitizeFolderName(folder.name); // Удаляем недопустимые символы
            if (string.IsNullOrEmpty(folderName)) folderName = $"Unnamed_{folder.id}"; // Если имя пустое, используем id

            string newFolderPath = Path.Combine(parentPath, folderName).Replace("\\", "/");
            if (!createdPaths.Contains(newFolderPath)) {
                createdPaths.Add(newFolderPath);
                if (!AssetDatabase.IsValidFolder(newFolderPath)) { // Проверяем, существует ли папка
                    AssetDatabase.CreateFolder(parentPath, folderName);
                }

                if (folderMapping != null) {// Добавляем папку в folderMapping
                    folderMapping.AddFolder(folder.id, folder.name, newFolderPath);
                }
            }

            if (folder.subfolders != null && folder.subfolders.Count > 0) {            // Рекурсивно создаем подкаталоги
                foreach (PCFolder subFolder in folder.subfolders) {                    // Проверяем, есть ли подкаталоги
                    if (subFolder != null) {
                        CreateFolderRecursive(subFolder, newFolderPath, createdPaths); // Рекурсивно создаем подкаталог
                    }
                }
            }
        }

        /// <summary>
        /// Sanitizes the name of a folder by replacing invalid characters with underscores.
        /// </summary>
        /// <param name="name">The name of the folder to sanitize.</param>
        /// <returns>The sanitized name of the folder.</returns>
        private string SanitizeFolderName(string name)
        {
            // Replace invalid characters with underscores using a regular expression
            return Regex.Replace(name, @"[<>:""/\\|?*]", "_");
        }

        /// <summary>
        /// Сохраняет текущее отображение папок в AssetDatabase.
        /// </summary>
        private void SaveFolderMapping() {
            if (folderMapping != null) {
                EditorUtility.SetDirty(folderMapping);
                AssetDatabase.SaveAssets();
            } else {
                Debug.LogError("Cannot save null folder mapping");
            }
        }

        #endregion Folder

        #region Web

        #endregion Web

        #endregion CreateGameObjects

        #endregion SceneUtility

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
                    "model" => value.ToObject<ModelComponent>(),
                    _ => value,
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
        public Entity root;
    }

    [System.Serializable]
    public class PCFolder {
        public int id;
        public string name;
        public int parentId;
        public List<PCFolder> subfolders = new List<PCFolder>(); // Ensure this is always initialized

        public PCFolder() {
            subfolders = new List<PCFolder>();
        }
    }

    #endregion SceneData
}

#endif