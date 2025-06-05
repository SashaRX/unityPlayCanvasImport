using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

[CreateAssetMenu(fileName = "AssetIDMapping", menuName = "PlayCanvas/Asset ID Mapping", order = 1)]
public class AssetIDMapping : ScriptableObject {
    public enum AssetType { Model, Texture, Material }

    [System.Serializable]
    public class Entry {
        public int playCanvasId;
        public AssetType type;
        public string unityAssetGUID;
    }

    public List<Entry> entries = new();

    /// <summary>
    /// Returns the Unity asset path that corresponds to the given PlayCanvas asset ID or null if not mapped.
    /// </summary>
    public string GetPathById(int playCanvasId) {
        foreach (var e in entries) {
            if (e.playCanvasId == playCanvasId && !string.IsNullOrEmpty(e.unityAssetGUID)) {
                return AssetDatabase.GUIDToAssetPath(e.unityAssetGUID);
            }
        }
        return null;
    }

    /// <summary>
    /// Adds or updates a mapping entry for a freshly‑imported asset.
    /// </summary>
    public void Register(int playCanvasId, AssetType type, UnityEngine.Object unityAsset) {
        string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(unityAsset));
        foreach (var e in entries) {
            if (e.playCanvasId == playCanvasId) {
                e.unityAssetGUID = guid;
                e.type = type;
                return;
            }
        }
        entries.Add(new Entry { playCanvasId = playCanvasId, type = type, unityAssetGUID = guid });
    }
}
