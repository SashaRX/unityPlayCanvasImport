using System;
//using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;



public class PlayCanvasImporterWindow : EditorWindow {

    private static bool showDebugLogs = true;
    private static string entityJsonPath = "";
    private static string targetFolderMat = "";
    private static Mesh quadMesh = null;
    private static Mesh diskMesh = null;

    private static bool isImportSuccessful = false;
    //private static readonly bool bShowLights = false;
    
    private static string nickname = "";
    private static string tokenId = "";
    private static string projectId = "";
    private static string branchId = "";

    // Основные словари
    private readonly List<Entity> lightEntities = new();    
    private readonly Dictionary<string, GameObject> idToGameObject = new();
    private readonly Dictionary<string, Entity> originalEntities = new();
    private static readonly List<PCAsset> downloadedAssets = new();
    private static readonly List<PCFolder> downloadedFolders = new();

    private Vector2 scrollPosition = Vector2.zero;


    // Поля для атласа
    private int atlasWidth = 0, atlasHeight = 0; // Размеры атласа
    private readonly List<Color> atlasColors = new(); // Промежуточное хранение
    private int colorCount = 0; // сколько реально добавлено
    private Material lightAtlasMat = null; // Общий материал для всех источников света
    private Texture2D hdrAtlasTexture = null; // Текстура атласа
    private bool showPlayCanvasSettings = true; // Переменная для управления Foldout

    [MenuItem("Window/PlayCanvas Importer")]

    public static void ShowWindow() {
        GetWindow<PlayCanvasImporterWindow>("PlayCanvas Importer");
    }

    private void OnEnable(){
        nickname       = EditorPrefs.GetString("PlayCanvas_Nickname", "");
        tokenId        = EditorPrefs.GetString("PlayCanvas_TokenID", "");
        projectId      = EditorPrefs.GetString("PlayCanvas_ProjectID", "");
        branchId       = EditorPrefs.GetString("PlayCanvas_BranchID", "");
        entityJsonPath = EditorPrefs.GetString("JSON_Path", "");
        showDebugLogs  =  EditorPrefs.GetBool("DebugLog", false);
    }

    void OnGUI() {
        GUILayout.Label("Import scene from PlayCanvas", EditorStyles.boldLabel);

        GUILayout.Space(10);
        // Чекбокс для включения/выключения сообщений в консоли
        showDebugLogs = EditorGUILayout.Toggle("Enable Console Logs", showDebugLogs);
        GUILayout.Space(10);

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
        GUILayout.Space(10);

        if (GUILayout.Button("Analyze Lights in JSON")) {
            if (string.IsNullOrEmpty(entityJsonPath)) {
                Debug.LogError("Entity JSON Path is empty.");
                return;
            }
            AnalyzeLightsFromJson(entityJsonPath);
        }

        if (GUILayout.Button("Import scene")) {
            if (string.IsNullOrEmpty(entityJsonPath)) {
                Debug.LogError("Entity JSON Path is empty.");
                return;
            }
            ClearSceneHierarchy();
            targetFolderMat = CreateMaterialFolder();
            if (targetFolderMat == null) {
                Debug.LogError("Failed to create material folder.");
                return;
            }
            ImportScene();
            AssetDatabase.Refresh();
        }

        GUI.enabled = isImportSuccessful;
        if (GUILayout.Button("Export Changes")) {
            if (string.IsNullOrEmpty(entityJsonPath)) {
                Debug.LogError("Entity JSON Path is empty.");
                return;
            }
            string savePath = Path.Combine(Path.GetDirectoryName(entityJsonPath), "modifiedEntities.json");
            ExportModifiedEntities(savePath);
        }
        GUI.enabled = !isImportSuccessful;

        GUI.enabled = true;
        DrawLightsInfo();

        GUILayout.Space(20);

        // Используем Foldout для PlayCanvas GUI
        showPlayCanvasSettings = EditorGUILayout.Foldout(showPlayCanvasSettings, "PlayCanvas Settings", true);
        if (showPlayCanvasSettings) {
            PlayCanvasGUI();
        }
    }

    private void PlayCanvasGUI() {
        // Обернем в Layout Box для визуального разделения
        EditorGUILayout.BeginVertical("box");

        GUILayout.Space(5);

        GUILayout.Label("PlayCanvas Importer", EditorStyles.boldLabel);

        GUILayout.Space(10);
        // Поле для ника
        //nickname = EditorGUILayout.TextField("PlayCanvas Nickname:", nickname);

        // Поле для token ID
        tokenId = EditorGUILayout.TextField("PlayCanvas Token ID:", tokenId);

        // Поле для ID проекта
        projectId = EditorGUILayout.TextField("Project ID:", projectId);

        // Поле для ID ветки
        branchId = EditorGUILayout.TextField("Branch ID:", branchId);

        GUILayout.Space(10);
        EditorGUILayout.BeginHorizontal();
        // Кнопка для импорта ассетов
        if (GUILayout.Button("Assets Import")) {
            DownloadPlayCanvasAssetsList();
            Debug.Log($"Assets import with Token ID: {tokenId} and Project ID: {projectId}");
        }

        // Кнопка для сохранения данных
        if (GUILayout.Button("Save Settings")) {
            EditorPrefs.SetString("PlayCanvas_Nickname", nickname);
            EditorPrefs.SetString("PlayCanvas_TokenID", tokenId);
            EditorPrefs.SetString("PlayCanvas_ProjectID", projectId);
            EditorPrefs.SetString("PlayCanvas_BranchID", branchId);
            EditorPrefs.SetString("JSON_Path", entityJsonPath);
            EditorPrefs.SetBool("DebugLog", showDebugLogs);
            Debug.Log("Settings saved.");
        }
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(5);

        EditorGUILayout.EndVertical();

        GUILayout.Space(15);
        GUILayout.Label($"Downloaded Assets: {downloadedAssets.Count}");
        GUILayout.Label($"Downloaded Folders: {downloadedFolders.Count}");
    }

    private void AnalyzeLightsFromJson(string jsonPath) {
        lightEntities.Clear();
        
        if (string.IsNullOrEmpty(jsonPath)) {
            Debug.LogError("Path to JSON file is empty.");
            return;
        }
        
        if (!File.Exists(jsonPath)) {
            Debug.LogError($"File {jsonPath} does not exist.");
            return;
        }
        
        string jsonContent;
        try {
            jsonContent = File.ReadAllText(jsonPath);
        } catch (Exception ex) {
            Debug.LogError($"Error loading JSON file: {ex.Message}");
            return;
        }
        
        SceneData sceneData;
        try {
            sceneData = JsonConvert.DeserializeObject<SceneData>(jsonContent);
        } catch (Exception ex) {
            Debug.LogError($"Error deserializing JSON content: {ex.Message}");
            return;
        }
        
        if (sceneData == null || sceneData.root == null) {
            Debug.LogError("Error: JSON-file does not contain correct scene data.");
            return;
        }
        
        try {
            FindLights(sceneData.root);
        } catch (Exception ex) {
            Debug.LogError($"Error when analyzing lights: {ex.Message}");
            return;
        }

        if (showDebugLogs) Debug.Log($"Found light sources in JSON: {lightEntities.Count}");
    }

    private void FindLights(Entity entity) {
        if (entity.components != null && entity.components.ContainsKey("light")) {
            lightEntities.Add(entity);
        }
        foreach (Entity child in entity.children) {
            FindLights(child);
        }
    }

    private void DrawLightsInfo() {
        if (lightEntities == null) {
            throw new ArgumentNullException(nameof(lightEntities), "The list of light entities should not be null.");
        }
        
        if (lightEntities.Count == 0) {
            GUILayout.Label("Not a light source in JSON.", EditorStyles.boldLabel);
            return;
        }
        
        scrollPosition = GUILayout.BeginScrollView(scrollPosition, GUILayout.Height(100));
        GUILayout.BeginVertical("box");

        int index = 1;
        foreach (Entity entity in lightEntities) {
            if (entity == null) {
                Debug.LogError("The list of light entities contains a null element. This is a bug.");
                continue;
            }
            if (!entity.components.ContainsKey("light")) {
                if (showDebugLogs) {
                    Debug.LogWarning($"Object {entity.name} does not contain light component. " +
                                     $"All components: {JsonConvert.SerializeObject(entity.components)}");
                }
                continue;
            }
            if (entity.components["light"] is not JObject lightData){
                Debug.LogError($"Object {entity.name} has an unexpected format of light component. " +
                               $"All components: {JsonConvert.SerializeObject(entity.components["light"])}");
                continue;
            }
            string lightType = lightData["type"]?.ToString() ?? "Unknown";
            int shapeValue = lightData["shape"]?.ToObject<int>() ?? -1;
            string lightShape = shapeValue switch {
                0 => "Cone",
                1 => "Rectangle",
                2 => "Disk",
                _ => "Unknown"
            };
            float[] colorArray = lightData["color"]?.ToObject<float[]>() ?? new float[] {1,1,1};
            Color lightColor;
            try {
                lightColor = new Color(colorArray[0], colorArray[1], colorArray[2]);
            } catch (Exception ex) {
                Debug.LogError($"Error when creating color for light {entity.name}: {ex.Message}");
                continue;
            }
            string intensity = lightData["intensity"]?.ToString() ?? "Unknown";
            
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{index}. {entity.name}", GUILayout.Width(110));
            index++;
            GUILayout.Label(intensity, GUILayout.Width(25));
            GUILayout.Label(lightShape, GUILayout.Width(60));
            GUILayout.Label(lightType, GUILayout.Width(60));
            EditorGUI.DrawRect(GUILayoutUtility.GetRect(20, 20), lightColor);
            GUILayout.EndHorizontal();
        }
        GUILayout.EndVertical();
        GUILayout.EndScrollView();
    }

    public static void ClearSceneHierarchy(){
        GameObject[] rootObjects = SceneManager.GetActiveScene().GetRootGameObjects();
        foreach (GameObject rootObject in rootObjects){
            if (rootObject.hideFlags != HideFlags.NotEditable && rootObject.hideFlags != HideFlags.HideAndDontSave)
                GameObject.DestroyImmediate(rootObject);
        }
        if(showDebugLogs) Debug.Log("Scene hierarchy cleared.");
    }

    private static string CreateMaterialFolder(){
        string scenePath = SceneManager.GetActiveScene().path;
        string sceneDir = Path.GetDirectoryName(scenePath);
        string targetFolderMat = Path.Combine(sceneDir, "lightMaterials");

        if (Directory.Exists(targetFolderMat)){
            foreach (string file in Directory.GetFiles(targetFolderMat))
                File.Delete(file);
            foreach (string dir in Directory.GetDirectories(targetFolderMat))
                Directory.Delete(dir, true);
        } else {
            Directory.CreateDirectory(targetFolderMat);
        }
        AssetDatabase.Refresh();
        return targetFolderMat;
    }

    void ImportScene() {
        if (string.IsNullOrEmpty(entityJsonPath)) {
            Debug.LogError("File sceneData.json not found.");
            return;
        }
        if (!File.Exists(entityJsonPath)) {
            Debug.LogError("File scene not found.");
            return;
        }
        if (showDebugLogs) Debug.Log($"Loading scene from JSON-file: {entityJsonPath}");
        
        string jsonContent = File.ReadAllText(entityJsonPath);
        SceneData sceneData;
        try {
            sceneData = JsonConvert.DeserializeObject<SceneData>(jsonContent);
        } catch (Exception ex) {
            Debug.LogError($"Error load JSON-file scene: {ex.Message}");
            return;
        }
        if (sceneData == null || sceneData.root == null) {
            Debug.LogError("Error load JSON-file scene.");
            return;
        }
        
        originalEntities.Clear();
        idToGameObject.Clear();
        

        // Посчитаем, сколько будет mesh-источников
        int neededMeshLights = CountMeshLights(sceneData.root);
        // Выберем размер текстуры как минимальную степень двойки, покрывающую нужное количество пикселей
        int side = (int)Mathf.Ceil(Mathf.Sqrt(neededMeshLights));
        // Делаем, чтобы side = 2^n
        side = MakePowerOfTwo(side);
        // Сохраняем в atlasWidth, atlasHeight
        atlasWidth = side;
        atlasHeight = side;

        // Создадим список цветов (могли бы напрямую работать с пикселями, 
        // но тут покажем «накопление», а потом Apply)
        atlasColors.Clear();
        for (int i = 0; i < atlasWidth * atlasHeight; i++) {
            atlasColors.Add(Color.clear);
        }
        colorCount = 0;

        // Очистим текстуру
        if(this.hdrAtlasTexture!=null) {
            DestroyImmediate(this.hdrAtlasTexture);
            this.hdrAtlasTexture = null;
        }

        // Создаем новую текстуру
        this.hdrAtlasTexture = new Texture2D(atlasWidth, atlasHeight, TextureFormat.RGBAHalf, false, true){
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point,
            anisoLevel = 0,
            name = "HDR_Atlas",
            alphaIsTransparency = false
        };

        AssetDatabase.CreateAsset(this.hdrAtlasTexture, AssetDatabase.GenerateUniqueAssetPath($"{targetFolderMat}/lightAtlas_tex.asset"));

        // Очистим материал
        if( this.lightAtlasMat != null ){
            DestroyImmediate(this.lightAtlasMat);
            this.lightAtlasMat = null;
        }

        // Создаем материал
        this.lightAtlasMat = new Material(Shader.Find("Bakery/Light"));
        this.lightAtlasMat.SetTexture("_MainTex", hdrAtlasTexture);
        this.lightAtlasMat.SetColor("_Color", Color.white);
        this.lightAtlasMat.SetFloat("intensity", 1.0f);

        AssetDatabase.CreateAsset(this.lightAtlasMat, AssetDatabase.GenerateUniqueAssetPath($"{targetFolderMat}/lightAtlas_mat.mat"));
        
        AssetDatabase.SaveAssets();
        // Создаём объекты сцены
        CreateGameObjectHierarchy(sceneData.root);

        // Применяем атлас
        ApplyHDRAtlas();

        // После создания всей иерархии синхронизируем оригинал с фактическим состоянием
        StoreFinalImportedState();

        isImportSuccessful = true;
        if (showDebugLogs) Debug.Log("Import scene completed.");
    }

    private int MakePowerOfTwo(int x){
        int p = 1;
        while (p < x) p <<= 1;
        return p;
    }

    void CreateGameObjectHierarchy(Entity entity, GameObject parent = null) {
        // Запоминаем объект в originalEntities
        if (!originalEntities.ContainsKey(entity.id)) {
            originalEntities[entity.id] = entity;
        }

        string objectName = !string.IsNullOrEmpty(entity.name) ? entity.name : entity.id;
        if (showDebugLogs) Debug.Log($"Create object: {objectName}");

        GameObject obj = new(objectName);
        obj.transform.parent = parent ? parent.transform : null;
        obj.transform.localScale = Vector3.one;

        idToGameObject[entity.id] = obj;

        if (entity.position != null) {
            obj.transform.position = new Vector3(entity.position[0],
                                                 entity.position[1],
                                                entity.position[2]);
        }

        if (entity.rotation != null) {
            Quaternion playCanvasRotation = Quaternion.Euler(entity.rotation[0], -entity.rotation[2], -entity.rotation[1]);
            obj.transform.rotation = Quaternion.Euler(90, 0, 0) * playCanvasRotation;
        }

        entity.scale ??= new List<float> { 1f, 1f, 1f };
        if (entity.components != null) {
            AddComponents(obj, entity.components, entity.scale);
        }

        foreach (var child in entity.children) {
            CreateGameObjectHierarchy(child, obj);
        }
    }

    private void StoreFinalImportedState() {
        foreach (var kvp in idToGameObject) {
            string id = kvp.Key;
            GameObject go = kvp.Value;
            Entity ent = originalEntities[id];

            // Сохраняем актуальное имя родителя, позицию и угол Эйлера
            ent.parent = go.transform.parent ? go.transform.parent.name : null;

            Vector3 pos = go.transform.position;
            ent.position = new List<float> { pos.x, pos.y, pos.z };

            Vector3 euler = go.transform.rotation.eulerAngles;
            // тут можно ещё округлить, чтобы отсечь микроскопические отклонения
            euler.x = Mathf.Round(euler.x * 100f)/100f;
            euler.y = Mathf.Round(euler.y * 100f)/100f;
            euler.z = Mathf.Round(euler.z * 100f)/100f;
            ent.rotation = new List<float> { euler.x, euler.y, euler.z };

            Renderer rend = go.GetComponent<Renderer>();
            if (rend && rend.sharedMaterial) {
                ent.material = rend.sharedMaterial.name;
                Color c = rend.sharedMaterial.color;
                ent.color = new float[] { c.r, c.g, c.b };
            }
        }
    }

    /*private Material CreateMaterial(string name, Color color, float intensity = 1.0f) {
        Material material = new Material(Shader.Find("Bakery/Light")) { name = name };
        material.SetColor("_Color", color);
        material.SetFloat("intensity", intensity);
        AssetDatabase.CreateAsset(material, AssetDatabase.GenerateUniqueAssetPath($"{targetFolderMat}/{name}.asset"));
        AssetDatabase.SaveAssets();
        return material;
    }*/

    private static Mesh GetQuad() {
        if (quadMesh == null) {
            quadMesh = new Mesh{
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

    private static Mesh GetDisk(float radius = .5f, int segments = 32) {
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

    public void ExportModifiedEntities(string exportPath) {
        var diffs = new List<Dictionary<string, object>>();
        float tolerance = 0.001f;

        foreach (var pair in idToGameObject) {
            string id = pair.Key;
            GameObject obj = pair.Value;
            if (!originalEntities.TryGetValue(id, out Entity original))
                continue;

            var diff = new Dictionary<string, object>{
                ["id"] = id
            };

            // 1) Позиция
            Vector3 currentPos = obj.transform.position;
            Vector3 importedPos = (original.position != null && original.position.Count == 3)
                ? new Vector3(original.position[0], original.position[1], original.position[2])
                : Vector3.zero;
            if (!Approximately(currentPos, importedPos, tolerance)) {
                diff["position"] = new float[] { currentPos.x, currentPos.y, currentPos.z };
            }

            // 2) Поворот
            Quaternion expectedRot = Quaternion.Euler(original.rotation[0], original.rotation[1], original.rotation[2]);
            Vector3 currentEuler = obj.transform.rotation.eulerAngles;
            Vector3 expectedEuler = expectedRot.eulerAngles;
            if (!ApproximatelyEuler(currentEuler, expectedEuler, 1f)){
                diff["rotation"] = new float[] { currentEuler.x, currentEuler.y, currentEuler.z };
            }

            // 3) Родитель
            string currentParent = obj.transform.parent ? obj.transform.parent.name : null;
            if (original.parent != currentParent) {
                diff["parent"] = currentParent;
            }

            // 4) Материал
            Renderer renderer = obj.GetComponent<Renderer>();
            string currentMat = (renderer != null && renderer.sharedMaterial != null) ? renderer.sharedMaterial.name : null;
            if (original.material != currentMat) {
                diff["material"] = currentMat;
            }

            // 5) Цвет
            Color currentColor = (renderer != null && renderer.sharedMaterial != null)
                ? renderer.sharedMaterial.color
                : Color.white;
            float[] importedColor = (original.color != null && original.color.Length == 3)
                ? original.color
                : new float[] { 1f, 1f, 1f };
            if (!Approximately(currentColor, importedColor, tolerance)) {
                diff["color"] = new float[] { currentColor.r, currentColor.g, currentColor.b };
            }

            // Проверяем, есть ли изменения кроме самого "id"
            if (diff.Count > 1) {
                // При наличии изменений добавим в diff имя объекта
                diff["name"] = obj.name;

                diffs.Add(diff);
            }
        }

        if (diffs.Count == 0 && showDebugLogs) {
            Debug.Log("No changes found. Exporting empty array.");
        }

        string jsonContent = JsonConvert.SerializeObject(diffs, Formatting.Indented);
        File.WriteAllText(exportPath, jsonContent);
        if (showDebugLogs) Debug.Log("Exported modified entities to " + exportPath);
    }

    private bool Approximately(Vector3 a, Vector3 b, float tolerance) {
        return Mathf.Abs(a.x - b.x) < tolerance &&
               Mathf.Abs(a.y - b.y) < tolerance &&
               Mathf.Abs(a.z - b.z) < tolerance;
    }

    private bool Approximately(Color a, float[] b, float tolerance) {
        return Mathf.Abs(a.r - b[0]) < tolerance &&
               Mathf.Abs(a.g - b[1]) < tolerance &&
               Mathf.Abs(a.b - b[2]) < tolerance;
    }

    private bool ApproximatelyEuler(Vector3 a, Vector3 b, float maxAngleDiff){
        float dx = Mathf.DeltaAngle(a.x, b.x);
        float dy = Mathf.DeltaAngle(a.y, b.y);
        float dz = Mathf.DeltaAngle(a.z, b.z);
        return Mathf.Abs(dx) <= maxAngleDiff &&
               Mathf.Abs(dy) <= maxAngleDiff &&
               Mathf.Abs(dz) <= maxAngleDiff;
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
        }else{
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
        lightComponent.areaSize = new Vector2(scale[0]*0.5f, scale[2]*0.5f);
        lightComponent.type = LightType.Disc;
        BakeryLightMesh bakeryDiscLight = obj.AddComponent<BakeryLightMesh>();
        bakeryDiscLight.color = color;
        bakeryDiscLight.intensity = intensity;

        Vector2? uvCoord = AddHDRColor(color * intensity);
        if (!uvCoord.HasValue) {
            Debug.LogError("Error: could not add color to the atlas!");
            return;
        }else{
            if (showDebugLogs)Debug.Log($"{obj.name} offset uv: " + uvCoord.ToString());
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

    private int CountMeshLights(Entity entity) {
        int count = 0;
        if (entity.components != null && entity.components.ContainsKey("light")) {
            // Проверяем, действительно ли нужен mesh. 
            // Напр.: type="spot" и shape=1 (Rectangle) или shape=2 (Disk)
            if (entity.components["light"] is JObject lightObj){
                string type = lightObj["type"]?.ToString().ToLower() ?? "";
                int shape = lightObj["shape"]?.ToObject<int>() ?? 0;
                if (type == "spot" && (shape == 1 || shape == 2)){
                    count = 1;
                }
            }
        }
        foreach (var child in entity.children) {
            count += CountMeshLights(child);
        }
        return count;
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

    private void ApplyHDRAtlas() {
        // Записываем массив
        //hdrAtlasTexture.SetPixels(atlasColors.ToArray());
        hdrAtlasTexture.SetPixels32(atlasColors.ConvertAll(c => (Color32)c).ToArray());
        hdrAtlasTexture.Apply();
    }

    private void AddComponents(GameObject obj, Dictionary<string, object> components, List<float> scale){
        if (components.ContainsKey("camera")){
            obj.AddComponent<Camera>();
        }
        if (components.ContainsKey("light")){
            object lightDataRaw = components["light"];
            Dictionary<string, object> lightData = lightDataRaw as Dictionary<string, object> ??
                JsonConvert.DeserializeObject<Dictionary<string, object>>(JsonConvert.SerializeObject(lightDataRaw));
            if (lightData != null){
                Light lightComponent = obj.AddComponent<Light>();
                int shape = lightData.ContainsKey("shape") ? Convert.ToInt32(lightData["shape"]) : 0;
                if (lightData.ContainsKey("type")){
                    string lightType = lightData["type"].ToString().ToLower();
                    float intensity = Convert.ToSingle(lightData["intensity"]);
                    List<float> colorArray = (lightData["color"] as JArray)?.ToObject<List<float>>();
                    if (colorArray == null) colorArray = new List<float> {1f,1f,1f};
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
        }
    }

    private async void DownloadPlayCanvasAssetsList() {
        if (string.IsNullOrEmpty(tokenId) || string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(branchId)) {
            Debug.LogError("Token ID, Project ID and Branch ID must be filled.");
            return;
        }
        // 1) Делаем запрос
        string url = $"https://playcanvas.com/api/projects/{projectId}/assets?branch={branchId}&skip=0&limit=10000";
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {tokenId}");

        string json;
        try {
            json = await client.GetStringAsync(url);
        } catch (HttpRequestException ex) {
            Debug.LogError($"Error requesting PlayCanvas: {ex.Message}");
            return;
        }

        if (string.IsNullOrEmpty(json)) {
            Debug.LogWarning("Empty response from PlayCanvas. Maybe no assets or API error.");
            return;
        }

        // Сохраняем JSON в файл (для отладки)
        string filePath = Path.Combine(Application.dataPath, "playcanvas_assets.json");
        File.WriteAllText(filePath, json);
        if (showDebugLogs) Debug.Log($"Assets list saved to: {filePath}");

        // 2) Подготовим словари для папок и ассетов
        Dictionary<int, PCFolder> folderById = new();
        Dictionary<int, PCAsset> assetById = new();

        // 3) Парсим JSON
        try {
            JObject root = JObject.Parse(json);
            if (root["result"] is JArray resultArray) {
                foreach (JToken item in resultArray) {
                    // Берём общие поля
                    int assetId = (int)item["id"];
                    string assetName = (string)item["name"];
                    string assetType = (string)item["type"];
                    int? parentId = (int?)item["parent"];

                    switch (assetType) {
                        // Если это папка
                        case "folder": {
                            PCFolder folder = new(){
                                id = assetId,
                                name = assetName,
                                parent = parentId ?? 0,
                                subfolders = new List<PCFolder>(),
                                assets = new List<PCAsset>()
                            };
                            folderById[folder.id] = folder;
                            if (showDebugLogs) Debug.Log($"Folder found: {assetName}, parent={folder.parent}");
                            break;
                        }
                        // Если это файл (texture/model/material/…)
                        case "texture":
                        //case "model":
                        case "scene":
                        case "material": {
                            if (item["file"] is JObject fileObj) {
                                string filename = (string)fileObj["filename"];
                                string fileUrl = (string)fileObj["url"] ?? "";
                                int size = 0;
                                if (fileObj["size"] != null && fileObj["size"].Type == JTokenType.Integer) size = (int)fileObj["size"];
                                PCAsset asset = new(){
                                    id = assetId,
                                    name = assetName,
                                    type = assetType,
                                    folder = parentId ?? 0,
                                    filename = filename,
                                    size = size,
                                    fileUrl = fileUrl
                                };
                                assetById[asset.id] = asset;
                                if (showDebugLogs) Debug.Log($"Asset: {assetName} (type={assetType}, file={filename})");
                            } else {
                                PCAsset asset = new(){
                                    id = assetId,
                                    name = assetName,
                                    type = assetType,
                                    folder = parentId ?? 0,
                                    filename = null,
                                    size = 0,
                                    fileUrl = null
                                };
                                assetById[asset.id] = asset;
                                if (showDebugLogs) Debug.LogWarning($"Asset without file: {assetName} (type={assetType})");
                            }
                            break;
                        }
                        default: {
                            if (showDebugLogs) Debug.LogWarning($"Unknown asset type: {assetType}, name={assetName}");
                            break;
                        }
                    }
                }
            } else {
                Debug.LogWarning("No 'result' array found in JSON.");
            }
        } catch (JsonException ex) {
            Debug.LogError($"JSON parsing error: {ex.Message}");
            return;
        }

        Repaint();

        // 4) Теперь строим дерево: папки → subfolders, а также папки → assets
        foreach (var kvp in folderById) {
            PCFolder folder = kvp.Value;
            int parent = folder.parent;
            if (parent != 0 && folderById.TryGetValue(parent, out PCFolder parentFolder)) {
                parentFolder.subfolders.Add(folder);
            }
        }
        foreach (var kvp in assetById) {
            PCAsset asset = kvp.Value;
            int parentFolderId = asset.folder;
            if (parentFolderId != 0 && folderById.TryGetValue(parentFolderId, out PCFolder parentFolder)) {
                parentFolder.assets.Add(asset);
            }
        }

        // 5) Найдём корневые папки (у которых parent=0)
        List<PCFolder> rootFolders = new();
        foreach (var folder in folderById.Values) {
            if (folder.parent == 0) rootFolders.Add(folder);
        }

        // Создаём (или используем уже существующую) папку "Assets/PlayCanvasImports"
        string rootUnityFolder = "Assets/PlayCanvasImports";
        if (!AssetDatabase.IsValidFolder(rootUnityFolder)) {
            // Создаём
            AssetDatabase.CreateFolder("Assets", "PlayCanvasImports");
        }

        // Вызываем метод, который создаёт полноценное дерево папок
        CreateUnityFolders.CreateUnityFoldersFromPlayCanvas(rootFolders, rootUnityFolder);

        Debug.Log("Done creating PlayCanvas folder hierarchy in Unity.");

        CreateUnityFolders.DownloadAllAssets(folderById, assetById, tokenId);
    }
}

public class XYZConverter : JsonConverter<List<float>>{
    public override List<float> ReadJson(JsonReader reader, Type objectType, List<float> existingValue, bool hasExistingValue, JsonSerializer serializer){
        JObject obj = serializer.Deserialize<JObject>(reader);
        if (obj != null && obj["x"] != null && obj["y"] != null && obj["z"] != null){
            return new List<float> { (float)obj["x"], (float)obj["y"], (float)obj["z"] };
        }
        return new List<float> { 0, 0, 0 };
    }

    public override void WriteJson(JsonWriter writer, List<float> value, JsonSerializer serializer){
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

public static class CreateUnityFolders {
    // Кэш словаря: folderID -> полный Unity-путь ("Assets/…")
    private static readonly Dictionary<int, string> folderPathById = new();
    public static void CreateUnityFoldersFromPlayCanvas(List<PCFolder> rootFolders, string unityRootPath) {
        if (!AssetDatabase.IsValidFolder(unityRootPath)) {
            Debug.LogWarning($"Root folder '{unityRootPath}' not found. Please create it manually.");
        }

        folderPathById.Clear();

        // Для каждой корневой папки создаём подпапку внутри unityRootPath.
        foreach (var rootFolder in rootFolders) {
            string createdPath = CreateFolderRecursive(rootFolder, unityRootPath);
            Debug.Log($"Created folder: {createdPath}");
        }
    }

    private static string CreateFolderRecursive(PCFolder folder, string parentPath) {
        string folderName = SanitizeFolderName(folder.name);

        // Проверяем, нет ли уже подпапки Assets/PlayCanvasImports/.../folderName
        string newFolderPath = parentPath + "/" + folderName;
        if (AssetDatabase.IsValidFolder(newFolderPath)) {
            // Папка уже существует — не создаём заново.
            // Просто используем имеющийся путь.
            folderPathById[folder.id] = newFolderPath;
        }
        else {
            // Папки нет — создаём
            string guid = AssetDatabase.CreateFolder(parentPath, folderName);
            newFolderPath = AssetDatabase.GUIDToAssetPath(guid);
            folderPathById[folder.id] = newFolderPath;
            Debug.Log($"Created folder: {newFolderPath}");
        }

        // Рекурсивно обходим все subfolders
        foreach (var sub in folder.subfolders) {
            CreateFolderRecursive(sub, newFolderPath);
        }
        return newFolderPath;
    }

    private static string SanitizeFolderName(string folderName) {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        foreach (char c in invalidChars) {
            folderName = folderName.Replace(c, '_');
        }
        folderName = folderName.Trim();
        return folderName;
    }

    private static async Task<bool> DownloadAssetFile(string baseUrl, string token, PCAsset asset, string localPath) {
        // baseUrl может быть "https://playcanvas.com" или "https://api.playcanvas.com"
        // fileUrl обычно начинается с "/api/..."
        string fullUrl = baseUrl + asset.fileUrl;

        using HttpClient http = new();
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        try{
            byte[] data = await http.GetByteArrayAsync(fullUrl);
            // Сохраняем
            File.WriteAllBytes(localPath, data);
            Debug.Log($"Downloaded: {asset.name} -> {localPath}");
            return true;
        }catch (Exception e){
            Debug.LogError($"Error downloading {asset.name} from {fullUrl} : {e.Message}");
            return false;
        }
    }

    public static async void DownloadAllAssets(Dictionary<int, PCFolder> folderById, Dictionary<int, PCAsset> assetById, string tokenId) {
        // Примерно:
        string baseUrl = "https://playcanvas.com"; // или "https://api.playcanvas.com"
        // tokenId уже есть

        int counter = 0;
        foreach (var kvp in assetById) {
            PCAsset asset = kvp.Value;
            if (string.IsNullOrEmpty(asset.fileUrl)) {
                // нет файла для скачивания
                continue;
            }
            if (!folderById.TryGetValue(asset.folder, out PCFolder parentFolder)) {
                // нет соответствующей папки
                continue;
            }
            // Получаем Unity-папку
            if (!folderPathById.TryGetValue(asset.folder, out string unityPath)) {
                Debug.LogWarning($"No unity folder for parent {asset.folder}, skipping asset {asset.name}");
                continue;
            }
            // Полный локальный путь
            string localPath = Path.Combine(unityPath, asset.filename); 

            if (File.Exists(localPath)) {
                Debug.Log($"File {asset.filename} already exists, skipping download.");
                continue;
            }
            // Скачаем
            bool success = await DownloadAssetFile(baseUrl, tokenId, asset, localPath);
            if (success) {
                counter++;
            }
        }
        Debug.Log($"Downloaded {counter} assets.");
        AssetDatabase.Refresh(); // Чтобы Unity подтянул новые файлы
    }

    public static string GetUnityPathForFolder(int folderId) {
        if (folderPathById.TryGetValue(folderId, out string path)) {
            return path;
        }
        return null;
    }
}

// Для удобства: корень сцены
[System.Serializable]
public class SceneData {
    public Entity root;
}

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

    public Dictionary<string, object> components;
    public List<Entity> children = new();
    public string parent;
    public string material;
    public float[] color;
}

[System.Serializable]
public class PCAsset{
    public int id;
    public string name;
    public string type;
    public int folder;  // или parent
    public string filename;
    public int size;
    public string fileUrl;
}


[Serializable]
public class PCFolder {
    public int id;
    public string name;
    public int parent; // 0, если у папки нет родителя (корень)
    // для удобства:
    public List<PCFolder> subfolders = new();
    public List<PCAsset> assets = new();
}