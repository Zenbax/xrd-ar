using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace ARMeshyDemo.Net
{
    /// <summary>
    /// Downloads external texture files referenced in GLB/glTF files.
    /// Handles the case where glTFast cannot automatically download HTTP texture URLs.
    /// </summary>
    public class ExternalTextureDownloader : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private int maxConcurrentDownloads = 3;
        [SerializeField] private float downloadTimeout = 30f;

        /// <summary>
        /// Download multiple textures from URLs and return them as Texture2D objects.
        /// </summary>
        public IEnumerator DownloadTextures(
            string[] textureUrls, 
            Action<Texture2D[]> onSuccess, 
            Action<string> onError,
            Action<float> onProgress = null)
        {
            if (textureUrls == null || textureUrls.Length == 0)
            {
                onError?.Invoke("No texture URLs provided.");
                yield break;
            }

            Log($"Starting download of {textureUrls.Length} textures...");
            
            var textures = new Texture2D[textureUrls.Length];
            int completed = 0;
            int failed = 0;

            // Download textures in parallel (limited concurrency)
            var activeDownloads = new List<Coroutine>();
            
            for (int i = 0; i < textureUrls.Length; i++)
            {
                int index = i; // Capture for closure
                var url = textureUrls[i];
                
                if (string.IsNullOrEmpty(url))
                {
                    Log($"Texture {index}: URL is empty, skipping");
                    completed++;
                    continue;
                }

                // Wait if too many concurrent downloads
                while (activeDownloads.Count >= maxConcurrentDownloads)
                {
                    yield return null;
                }

                var coroutine = StartCoroutine(DownloadSingleTexture(
                    url,
                    index,
                    tex =>
                    {
                        textures[index] = tex;
                        completed++;
                        onProgress?.Invoke((float)completed / textureUrls.Length);
                        Log($"Texture {index}: Downloaded successfully ({tex.width}x{tex.height})");
                    },
                    error =>
                    {
                        failed++;
                        completed++;
                        onProgress?.Invoke((float)completed / textureUrls.Length);
                        Debug.LogWarning($"[ExternalTextureDownloader] Texture {index}: Failed - {error}");
                    }
                ));

                activeDownloads.Add(coroutine);
            }

            // Wait for all downloads to complete
            while (completed < textureUrls.Length)
            {
                yield return null;
            }

            if (failed > 0)
            {
                Debug.LogWarning($"[ExternalTextureDownloader] {failed}/{textureUrls.Length} textures failed to download");
            }

            if (failed == textureUrls.Length)
            {
                onError?.Invoke($"All {textureUrls.Length} texture downloads failed.");
                yield break;
            }

            Log($"Download complete: {textureUrls.Length - failed}/{textureUrls.Length} textures successful");
            onSuccess?.Invoke(textures);
        }

        private IEnumerator DownloadSingleTexture(
            string url, 
            int index,
            Action<Texture2D> onSuccess, 
            Action<string> onError)
        {
            Log($"Texture {index}: Downloading from {url}");

            using (var request = UnityWebRequestTexture.GetTexture(url))
            {
                request.timeout = (int)downloadTimeout;
                
                var operation = request.SendWebRequest();
                float startTime = Time.time;

                while (!operation.isDone)
                {
                    if (Time.time - startTime > downloadTimeout)
                    {
                        onError?.Invoke($"Timeout after {downloadTimeout}s");
                        yield break;
                    }
                    yield return null;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    onError?.Invoke($"{request.responseCode} - {request.error}");
                    yield break;
                }

                var texture = DownloadHandlerTexture.GetContent(request);
                if (texture == null)
                {
                    onError?.Invoke("Failed to decode texture from download");
                    yield break;
                }

                // Mark as non-readable to save memory (optional)
                // texture.Apply(false, true);

                onSuccess?.Invoke(texture);
            }
        }

        /// <summary>
        /// Attempts to construct texture URLs from a base URI and common naming patterns.
        /// </summary>
        public static string[] GuessTextureUrls(string baseUri, int textureCount)
        {
            if (string.IsNullOrEmpty(baseUri)) return new string[0];

            var urls = new string[textureCount];
            
            // Common patterns used by Meshy/glTF exporters
            string[] patterns = {
                "texture_{0}.png",
                "texture_{0}.jpg",
                "texture{0}.png",
                "texture{0}.jpg",
                "{0}.png",
                "{0}.jpg",
                "albedo.png",
                "normal.png", 
                "metallic.png"
            };

            for (int i = 0; i < textureCount; i++)
            {
                // Try each pattern
                foreach (var pattern in patterns)
                {
                    var filename = string.Format(pattern, i);
                    urls[i] = baseUri + filename;
                    break; // Use first pattern for each texture
                }
            }

            return urls;
        }

        private static void Log(string msg) => Debug.Log($"[ExternalTextureDownloader] {msg}");
    }
}
