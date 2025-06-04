using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

[CreateAssetMenu(fileName = "EntityIDMapping", menuName = "PlayCanvas/Entity ID Mapping", order = 1)]
public class EntityIDMapping : ScriptableObject {
    public List<Entry> entries = new();

    // <summary>
    // Represents a mapping between a PlayCanvas ID and a GameObject.
    // </summary>
    [System.Serializable]
    public class Entry {
        public string globalObjectIdString; // Сериализуемый GlobalObjectId
        public string id;                  // PlayCanvas ID

        [System.NonSerialized]
        private GameObject _gameObject;    // Кэшированный GameObject (не сериализуется)

        public GameObject gameObject {
            get {
                if (_gameObject == null && !string.IsNullOrEmpty(globalObjectIdString)) {
                    if (GlobalObjectId.TryParse(globalObjectIdString, out GlobalObjectId id)) {
                        _gameObject = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as GameObject;
                    }
                }
                return _gameObject;
            }
            set {
                _gameObject = value;
                if (value != null) {
                    GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(value);
                    globalObjectIdString = id.ToString();
                } else {
                    globalObjectIdString = null;
                }
            }
        }
    }


    // <summary>
    // Finds a GameObject by its PlayCanvas ID.
    // </summary>   
    public GameObject FindGameObjectByID(string id) { 
        foreach (var entry in entries) {
            if (entry.id == id) {
                return entry.gameObject;
            }
        }
        return null;
    }
}