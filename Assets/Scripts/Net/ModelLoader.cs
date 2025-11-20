using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace ARMeshyDemo.Net
{
    /// <summary>
    /// Universal model loader that delegates to appropriate loader based on file extension.
    /// Supports GLB (via GltfLoader) and FBX/OBJ (download only - requires manual import in Unity Editor).
    /// </summary>
    public class ModelLoader : MonoBehaviour
    {
        [SerializeField] private GltfLoader gltfLoader;

        private void Awake()
        {
            if (gltfLoader == null)
                gltfLoader = gameObject.AddComponent<GltfLoader>();
        }

        public IEnumerator LoadFromUrl(string url, Action<GameObject> onOk, Action<string> onErr, Action<float> onProgress = null)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                onErr?.Invoke("Model URL is empty.");
                yield break;
            }

            var urlLower = url.ToLowerInvariant();

            // Determine format from URL
            if (urlLower.Contains(".glb") || urlLower.Contains("model.glb"))
            {
                Debug.Log("[ModelLoader] Detected GLB format, using GltfLoader");
                yield return gltfLoader.LoadFromUrl(url, onOk, onErr, onProgress);
            }
            else if (urlLower.Contains(".fbx"))
            {
                Debug.Log("[ModelLoader] FBX format detected");
                onErr?.Invoke("FBX runtime loading not yet implemented. Use GLB format instead.");
                yield break;
                
                // Note: FBX runtime loading requires third-party plugins like:
                // - TriLib 2 (paid asset)
                // - Assimp for Unity
                // For now, we only support GLB
            }
            else if (urlLower.Contains(".obj"))
            {
                Debug.Log("[ModelLoader] OBJ format detected");
                onErr?.Invoke("OBJ runtime loading not yet implemented. Use GLB format instead.");
                yield break;
            }
            else
            {
                // Default to GLB loader
                Debug.LogWarning($"[ModelLoader] Unknown format for URL: {url}, attempting GLB loader");
                yield return gltfLoader.LoadFromUrl(url, onOk, onErr, onProgress);
            }
        }
    }
}
