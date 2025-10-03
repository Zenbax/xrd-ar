using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

[Serializable]
public class ModelEntry
{
    public string imageHash;
    public string localGlbPath;
    public string arGuid;
}

[Serializable]
public class ModelRegistryData { public List<ModelEntry> entries = new(); }

public class ModelRegistry
{
    private string path;
    private ModelRegistryData data = new();

    public ModelRegistry()
    {
        path = Path.Combine(Application.persistentDataPath, "model_registry.json");
        Load();
    }

    public void AddOrUpdate(ModelEntry entry)
    {
        var idx = data.entries.FindIndex(e => e.imageHash == entry.imageHash);
        if (idx >= 0) data.entries[idx] = entry; else data.entries.Add(entry);
        Save();
    }

    public ModelEntry GetByHash(string hash) => data.entries.Find(e => e.imageHash == hash);

    void Load()
    {
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            data = JsonUtility.FromJson<ModelRegistryData>(json) ?? new ModelRegistryData();
        }
    }

    void Save()
    {
        File.WriteAllText(path, JsonUtility.ToJson(data, true));
    }
}