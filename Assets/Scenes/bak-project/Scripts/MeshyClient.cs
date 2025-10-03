using System;
using System.Collections;
using System.Text;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

public class MeshyClient : MonoBehaviour
{
    [SerializeField] private MeshyConfig config;

    [Serializable]
    private class CreateTaskRequest
    {
        public string image_url;
        public string ai_model;          // optional (e.g., "latest", "meshy-5")
        public string topology;          // optional ("triangle" | "quad")
        public int? target_polycount;    // optional
        public string symmetry_mode;     // optional ("off" | "auto" | "on")
        public bool? should_remesh;      // optional
        public bool? should_texture;     // optional
        public bool? enable_pbr;         // optional
        public bool? is_a_t_pose;        // optional
        public string texture_prompt;    // optional
        public string texture_image_url; // optional
        public bool? moderation;         // optional
    }

    [Serializable]
    private class CreateTaskResponse { public string result; } // task id

    [Serializable]
    private class ModelUrls { public string glb; public string fbx; public string obj; public string usdz; }

    [Serializable]
    private class TaskError { public string message; }

    [Serializable]
    private class TaskStatusResponse
    {
        public string id;
        public ModelUrls model_urls;
        public string thumbnail_url;
        public string texture_prompt;
        public string texture_image_url;
        public int progress;
        public long started_at;
        public long created_at;
        public long expires_at;
        public long finished_at;
        public string status;           // PENDING | IN_PROGRESS | SUCCEEDED | FAILED | CANCELED
        public TaskError task_error;
    }

    public void InitIfNeeded()
    {
        if (!config) config = Resources.Load<MeshyConfig>("MeshyConfig");
        if (!config) Debug.LogError("Missing MeshyConfig in Resources");
    }

    // Helper: build data URI from PNG bytes
    private static string PngBytesToDataUri(byte[] png) =>
        "data:image/png;base64," + Convert.ToBase64String(png);

    /// <summary>
    /// Creates an Image-to-3D task. Returns task id via onTaskCreated.
    /// </summary>
    public IEnumerator CreateImageTo3DTask(
        byte[] imagePng,
        Action<string> onTaskCreated,
        Action<string> onError,
        // Optional parameters that mirror the API
        string ai_model = null,              // "meshy-4" | "meshy-5" | "latest"
        string topology = null,              // "triangle" | "quad"
        int? target_polycount = null,
        string symmetry_mode = null,         // "off" | "auto" | "on"
        bool? should_remesh = null,
        bool? should_texture = null,
        bool? enable_pbr = null,
        bool? is_a_t_pose = null,
        string texture_prompt = null,
        string texture_image_url = null,
        bool? moderation = null
    )
    {
        InitIfNeeded();

        var body = new CreateTaskRequest {
            image_url        = PngBytesToDataUri(imagePng),
            ai_model         = ai_model,
            topology         = topology,
            target_polycount = target_polycount,
            symmetry_mode    = symmetry_mode,
            should_remesh    = should_remesh,
            should_texture   = should_texture,
            enable_pbr       = enable_pbr,
            is_a_t_pose      = is_a_t_pose,
            texture_prompt   = texture_prompt,
            texture_image_url= texture_image_url,
            moderation       = moderation
        };

        var json = JsonUtility.ToJson(body);
        var bytes = Encoding.UTF8.GetBytes(json);

        using (var req = new UnityWebRequest($"{config.baseUrl}/openapi/v1/image-to-3d", "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(bytes);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Authorization", $"Bearer {config.apiKey}");
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke(req.error + " | " + req.downloadHandler.text);
                yield break;
            }

            var create = JsonUtility.FromJson<CreateTaskResponse>(req.downloadHandler.text);
            if (create == null || string.IsNullOrEmpty(create.result))
            {
                onError?.Invoke("Invalid create response");
            }
            else onTaskCreated?.Invoke(create.result);
        }
    }

    /// <summary>
    /// Polls until SUCCEEDED/FAILED. Returns the GLB URL via onGlbUrl (if present).
    /// </summary>
    public IEnumerator PollTaskUntilReady(
        string taskId,
        Action<string> onGlbUrl,
        Action<string> onError,
        float pollInterval = 2f,
        float timeout = 300f
    )
    {
        InitIfNeeded();
        float elapsed = 0f;

        while (elapsed < timeout)
        {
            using (var req = UnityWebRequest.Get($"{config.baseUrl}/openapi/v1/image-to-3d/{taskId}"))
            {
                req.SetRequestHeader("Authorization", $"Bearer {config.apiKey}");
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    onError?.Invoke(req.error + " | " + req.downloadHandler.text);
                    yield break;
                }

                var status = JsonUtility.FromJson<TaskStatusResponse>(req.downloadHandler.text);
                if (status == null)
                {
                    onError?.Invoke("Invalid status response");
                    yield break;
                }

                switch (status.status)
                {
                    case "SUCCEEDED":
                        var glb = status.model_urls != null ? status.model_urls.glb : null;
                        if (string.IsNullOrEmpty(glb))
                        {
                            onError?.Invoke("No GLB URL in success response");
                        }
                        else
                        {
                            onGlbUrl?.Invoke(glb);
                        }
                        yield break;

                    case "FAILED":
                    case "CANCELED":
                        onError?.Invoke(status.task_error != null ? status.task_error.message : $"Task {status.status}");
                        yield break;

                    case "PENDING":
                    case "IN_PROGRESS":
                    default:
                        // keep polling
                        break;
                }
            }
            yield return new WaitForSeconds(pollInterval);
            elapsed += pollInterval;
        }

        onError?.Invoke("Timed out waiting for model");
    }

    public IEnumerator DownloadBytes(string url, Action<byte[]> onBytes, Action<string> onError)
    {
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) onError?.Invoke(req.error);
            else onBytes?.Invoke(req.downloadHandler.data);
        }
    }
}