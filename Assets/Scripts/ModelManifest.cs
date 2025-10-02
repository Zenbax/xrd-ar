using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

[Serializable]
public class ModelManifest
{
    [Serializable]
    public class Entry
    {
        public string key;
        public string path;
        public long bytes;
        public long timestamp;
    }

    public List<Entry> entries = new();

    public string GetPath(string key)
    {
        var e = entries.Find(x => x.key == key);
        return e?.path ?? string.Empty;
    }

    public void Upsert(string key, string path)
    {
        var fi = new FileInfo(path);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var e = entries.Find(x => x.key == key);
        if (e == null)
        {
            entries.Add(new Entry { key = key, path = path, bytes = fi.Exists ? fi.Length : 0, timestamp = ts });
        }
        else
        {
            e.path = path;
            e.bytes = fi.Exists ? fi.Length : 0;
            e.timestamp = ts;
        }
    }

    public void Save(string manifestPath)
    {
        File.WriteAllText(manifestPath, JsonUtility.ToJson(this, true));
    }

    public static ModelManifest Load(string manifestPath)
    {
        if (!File.Exists(manifestPath)) return new ModelManifest();
        var json = File.ReadAllText(manifestPath);
        return JsonUtility.FromJson<ModelManifest>(json) ?? new ModelManifest();
    }
}
