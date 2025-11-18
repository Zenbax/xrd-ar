using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using ARMeshyDemo.Config;
using Debug = UnityEngine.Debug;

namespace ARMeshyDemo.Net
{
    /// <summary>
    /// Kalder Meshy Image->3D API:
    /// 1) POST /openapi/v1/image-to-3d med image_url (data URI)
    /// 2) Poll GET /openapi/v1/image-to-3d/{id} indtil SUCCEEDED
    /// Docs: https://docs.meshy.ai/api/image-to-3d
    /// </summary>
    public class MeshyClient : MonoBehaviour
    {
        [SerializeField] private MeshySettings settings;
        [Header("Format")]
        [Tooltip("Request all formats (glb, fbx, obj, usdz) for maximum compatibility. Disable to request only GLB.")]
        [SerializeField] private bool requestAllFormats = true;

        private const string BaseUrl = "https://api.meshy.ai/openapi/v1";
        private string ApiKey => settings != null ? settings.ResolveApiKey() : null;

        [Serializable]
        private class CreateReq
        {
            public string image_url;
            public bool should_remesh = true;
            public bool should_texture = true;
            public bool enable_pbr = true;
            // Valgfrit: bed kun om bestemte output formater (Meshy ignorerer hvis ikke understøttet).
            public string[] model_formats; // fx ["glb"]
        }

        [Serializable] private class CreateRes { public string result; }

        [Serializable]
        public class ModelUrls
        {
            public string glb;
            public string fbx;
            public string obj;
            public string usdz;
        }

        [Serializable]
        public class ImageTo3DTask
        {
            public string id;
            public string status;     // PENDING, IN_PROGRESS, SUCCEEDED, FAILED
            public float progress;    // 0..100
            public ModelUrls model_urls;
            public string failure_reason;
        }

        public IEnumerator CreateImageTo3D(
            byte[] imageBytes, 
            string mimeType,
            Action<string> onOk, 
            Action<string> onErr,
            bool shouldRemesh = true,
            bool shouldTexture = true,
            bool enablePbr = true)
        {
            var key = ApiKey;
            if (string.IsNullOrEmpty(key))
            {
                onErr?.Invoke("Meshy API key mangler. Sæt den i MeshySettings eller miljøvariabel.");
                yield break;
            }

            // Byg data URI (Meshy docs accepterer base64 data URI i image_url)
            var b64 = Convert.ToBase64String(imageBytes);
            var dataUri = $"data:{mimeType};base64,{b64}";

            var payload = new CreateReq { 
                image_url = dataUri,
                should_remesh = shouldRemesh,
                should_texture = shouldTexture,
                enable_pbr = enablePbr
            };
            
            // ? Request all formats by default for better texture support
            if (!requestAllFormats)
                payload.model_formats = new[] { "glb" };

            var json = JsonUtility.ToJson(payload);
            
            // ? Debug logging to verify texture parameters are being sent
            Debug.Log($"[MeshyClient] Creating task with: should_texture={shouldTexture}, enable_pbr={enablePbr}, should_remesh={shouldRemesh}");
            Debug.Log($"[MeshyClient] Requesting formats: {(requestAllFormats ? "ALL formats (glb, fbx, obj, usdz)" : "GLB only")}");
            Debug.Log($"[MeshyClient] Request payload: {json}");

            using (var req = new UnityWebRequest($"{BaseUrl}/image-to-3d", "POST"))
            {
                var bodyRaw = Encoding.UTF8.GetBytes(json);
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Authorization", $"Bearer {key}");
                req.SetRequestHeader("Content-Type", "application/json");

                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    onErr?.Invoke($"CreateImageTo3D failed: {req.responseCode} {req.error} {req.downloadHandler.text}");
                    yield break;
                }

                CreateRes res = null;
                try { res = JsonUtility.FromJson<CreateRes>(req.downloadHandler.text); }
                catch (Exception e)
                {
                    onErr?.Invoke("Ugyldigt JSON-svar: " + e);
                    yield break;
                }

                if (string.IsNullOrEmpty(res?.result))
                {
                    onErr?.Invoke("Intet task-id i svar.");
                    yield break;
                }

                Debug.Log($"[MeshyClient] Task created successfully: {res.result}");
                onOk?.Invoke(res.result);
            }
        }

        public IEnumerator GetImageTo3DTask(string taskId,
                                            Action<ImageTo3DTask> onOk,
                                            Action<string> onErr)
        {
            var key = ApiKey;
            if (string.IsNullOrEmpty(key))
            {
                onErr?.Invoke("Meshy API key mangler.");
                yield break;
            }

            using (var req = UnityWebRequest.Get($"{BaseUrl}/image-to-3d/{taskId}"))
            {
                req.SetRequestHeader("Authorization", $"Bearer {key}");

                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    onErr?.Invoke($"Get task failed: {req.responseCode} {req.error} {req.downloadHandler.text}");
                    yield break;
                }

                ImageTo3DTask task = null;
                try { task = JsonUtility.FromJson<ImageTo3DTask>(req.downloadHandler.text); }
                catch (Exception e)
                {
                    onErr?.Invoke("Ugyldigt JSON-svar: " + e);
                    yield break;
                }

                onOk?.Invoke(task);
            }
        }

        /// <summary>
        /// Poller task til SUCCEEDED/FAILED. Returnerer det sidste task-objekt.
        /// </summary>
        public IEnumerator PollImageTo3D(
     string taskId,
     float intervalSec,
     Action<ImageTo3DTask> onProgress,
     Action<ImageTo3DTask> onDone,
     Action<string> onErr,
     float timeoutSec = 600f,
     System.Func<bool> shouldCancel = null)
        {
            float t = 0f;
            ImageTo3DTask last = null;

            while (t < timeoutSec)
            {
                if (shouldCancel != null && shouldCancel())
                {
                    onErr?.Invoke("Cancelled by user.");
                    yield break;
                }

                yield return GetImageTo3DTask(taskId,
                    onOk: task =>
                    {
                        last = task;
                        onProgress?.Invoke(task);
                    },
                    onErr: err =>
                    {
                        last = null;
                        Debug.LogWarning(err);
                    });

                if (last != null)
                {
                    var s = last.status ?? "";
                    if (s.Equals("SUCCEEDED", StringComparison.OrdinalIgnoreCase))
                    {
                        // ? Log all available URLs
                        Debug.Log($"[MeshyClient] Task SUCCEEDED!");
                        if (last.model_urls != null)
                        {
                            Debug.Log($"[MeshyClient] Available URLs:");
                            if (!string.IsNullOrEmpty(last.model_urls.glb)) Debug.Log($"  GLB: {last.model_urls.glb}");
                            if (!string.IsNullOrEmpty(last.model_urls.fbx)) Debug.Log($"  FBX: {last.model_urls.fbx}");
                            if (!string.IsNullOrEmpty(last.model_urls.obj)) Debug.Log($"  OBJ: {last.model_urls.obj}");
                            if (!string.IsNullOrEmpty(last.model_urls.usdz)) Debug.Log($"  USDZ: {last.model_urls.usdz}");
                        }
                        else
                        {
                            Debug.LogWarning("[MeshyClient] ?? No model_urls in response!");
                        }
                        
                        onDone?.Invoke(last);
                        yield break;
                    }
                    if (s.Equals("FAILED", StringComparison.OrdinalIgnoreCase))
                    {
                        onErr?.Invoke("Task FAILED: " + (last.failure_reason ?? "Unknown"));
                        yield break;
                    }
                }

                float wait = intervalSec;
                // (Valgfrit) backoff lidt ved 0% i starten
                yield return new WaitForSeconds(wait);
                t += wait;
            }

            onErr?.Invoke("Timeout while waiting for Meshy task.");
        }

    }
}
