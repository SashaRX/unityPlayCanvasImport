using UnityEngine;
using System.Collections.Generic;

[CreateAssetMenu(fileName = "FolderMapping", menuName = "PlayCanvas/Folder Mapping", order = 1)]
public class FolderMapping : ScriptableObject{
    [System.Serializable]
    public class FolderEntry {
        public int id;
        public string name;
        public string path;
    }

    public List<FolderEntry> folders = new();

    public void AddFolder(int id, string name, string path){
        foreach (var folder in folders){
            if (folder.id == id){
                folder.name = name;
                folder.path = path;
                return;
            }
        }
        folders.Add(new FolderEntry { id = id, name = name, path = path });
    }

    public string GetPathById(int id){
        foreach (var folder in folders){
            if (folder.id == id){
                return folder.path;
            }
        }
        return null;
    }
}