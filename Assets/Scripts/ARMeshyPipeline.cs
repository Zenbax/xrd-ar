// ARMeshyPipeline.cs
using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using GLTFast; // com.unity.cloud.gltfast
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using System.Text.RegularExpressions;

public class ARMeshyPipeline : MonoBehaviour
{
    [Header("AR")]
    [SerializeField] private ARCameraManager arCameraManager;
    [SerializeField] private ARTrackedImageManager trackedImageManager;
    [SerializeField] private float runtimeImagePhysicalSizeMeters = 0.2f;

    [Header("Meshy")]
    [Tooltip("Paste your msy_... key here")]
    [SerializeField] private string meshyApiKey = "REPLACE_WITH_API_KEY";
    [SerializeField] private string meshyCreateUrl = "https://api.meshy.ai/v1/image-to-3d";
    [SerializeField] private string meshyJobUrl = "https://api.meshy.ai/v1/jobs";

    [Space(6)]
    [SerializeField] private bool preferJsonUpload = true;  // keep ON
    [SerializeField] private bool autoFallbackUpload = false; // keep OFF
    public enum PollPathStyle { JobsSlashId, CreateUrlSlashId }
    [SerializeField] private PollPathStyle pollPathStyle = PollPathStyle.JobsSlashId;

    [Header("UI")]
    [SerializeField] private ProgressUI progressUI;

    private string manifestPath;
    private ModelManifest manifest;
    private string lastCreateJobRawResponse;

    void Awake()
    {
        manifestPath = Path.Combine(Application.persistentDataPath, "model_manifest.json");
        manifest = ModelManifest.Load(manifestPath);
        progressUI?.Show(false);
    }

    public void RunPipeline() => StartCoroutine(CaptureAndProcess());

    private IEnumerator CaptureAndProcess()
    {
        yield return CaptureTextureFromARCamera(imageTexture =>
        {
            if (imageTexture == null)
            {
                Debug.LogError("Failed to capture camera image.");
                return;
            }
            StartCoroutine(ProcessTexture(imageTexture));
        });
    }

    private IEnumerator ProcessTexture(Texture2D tex)
    {
        string key = ComputeHash(tex);
        string cachedPath = manifest.GetPath(key);

        if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
        {
            yield return LoadGlb(cachedPath);
            yield break;
        }

        yield return AddRuntimeReferenceImage(tex);

        progressUI?.Show(true);
        progressUI?.Set(0.05f, "Starting 3D generation...");

        string jobId = null;
        yield return StartCoroutine(CreateMeshyJob_JSON_TryVariants(tex, id => jobId = id));

        if (string.IsNullOrEmpty(jobId))
        {
            string snippet = string.IsNullOrEmpty(lastCreateJobRawResponse)
                ? "<no body>"
                : Truncate(lastCreateJobRawResponse.Replace('\r', ' ').Replace('\n', ' '), 240);
            progressUI?.Set(0f, "Failed to start job\n" + snippet);
            Debug.LogError("Failed to start job. Raw response:\n" + lastCreateJobRawResponse);
            yield break;
        }

        string glbUrl = null;
        yield return StartCoroutine(PollMeshyJob(jobId, p =>
        {
            progressUI?.Set(Mathf.Lerp(0.1f, 0.95f, p), $"Generating... {Mathf.RoundToInt(p * 100)}%");
        }, url => glbUrl = url));

        if (string.IsNullOrEmpty(glbUrl))
        {
            progressUI?.Set(0f, "Generation failed");
            yield break;
        }

        progressUI?.Set(0.96f, "Downloading model...");
        string localPath = Path.Combine(Application.persistentDataPath, $"{key}.glb");
        yield return StartCoroutine(DownloadFile(glbUrl, localPath));

        manifest.Upsert(key, localPath);
        manifest.Save(manifestPath);

        progressUI?.Set(1f, "Loading model...");
        yield return LoadGlb(localPath);

        progressUI?.Show(false);
    }

    // ---------- CAPTURE ----------
    private IEnumerator CaptureTextureFromARCamera(Action<Texture2D> onDone)
    {
        if (arCameraManager == null)
        {
            onDone?.Invoke(null);
            yield break;
        }

        if (!arCameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage))
        {
            yield return new WaitForEndOfFrame();
            var texFallback = ScreenCapture.CaptureScreenshotAsTexture();
            onDone?.Invoke(texFallback);
            yield break;
        }

        using (cpuImage)
        {
            var conversionParams = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, cpuImage.width, cpuImage.height),
                outputDimensions = new Vector2Int(cpuImage.width, cpuImage.height),
                outputFormat = TextureFormat.RGBA32,
                transformation = XRCpuImage.Transformation.MirrorX
            };

            var data = new NativeArray<byte>(cpuImage.GetConvertedDataSize(conversionParams), Allocator.Temp);
            cpuImage.Convert(conversionParams, data);

            var tex = new Texture2D(conversionParams.outputDimensions.x, conversionParams.outputDimensions.y, TextureFormat.RGBA32, false);
            tex.LoadRawTextureData(data);
            tex.Apply();
            data.Dispose();

            onDone?.Invoke(tex);
        }
    }

    private IEnumerator AddRuntimeReferenceImage(Texture2D tex)
    {
        var lib = trackedImageManager.referenceLibrary as MutableRuntimeReferenceImageLibrary;
        if (lib == null)
        {
            Debug.LogWarning("Provider does not support MutableRuntimeReferenceImageLibrary.");
            yield break;
        }

        JobHandle handle = lib.ScheduleAddImageJob(tex, "RuntimeAddedImage", runtimeImagePhysicalSizeMeters);
        while (!handle.IsCompleted) yield return null;
        handle.Complete();
    }

    // ---------- CREATE JOB (JSON variants ONLY) ----------
    private IEnumerator CreateMeshyJob_JSON_TryVariants(Texture2D tex, Action<string> onJobId)
    {
        byte[] png = tex.EncodeToPNG();
        string b64 = Convert.ToBase64String(png);

        // Try a few common payload shapes. The server will reject ones it doesn't support.
        var variants = new string[]
        {
            // 1) image base64 without data: prefix
            JsonPayload(new { image = b64, target_face_count = 50000, texture_size = 1024, output_format = "glb" }),

            // 2) image base64 WITH data URI
            JsonPayload(new { image = $"data:image/png;base64,{b64}", target_face_count = 50000, texture_size = 1024, output_format = "glb" }),

            // 3) some APIs use image_base64
            JsonPayload(new { image_base64 = b64, target_face_count = 50000, texture_size = 1024, output_format = "glb" })
        };

        foreach (var json in variants)
        {
            using (UnityWebRequest req = new UnityWebRequest(meshyCreateUrl, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", $"Bearer {meshyApiKey}");

                yield return req.SendWebRequest();

                lastCreateJobRawResponse = req.downloadHandler?.text;

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"Meshy(JSON) create failed for variant ({req.responseCode}): {req.error}\nBody:\n{lastCreateJobRawResponse}\nPayload:\n{json}");
                    continue;
                }

                string jobId =
                    ExtractAny(lastCreateJobRawResponse, "job_id") ??
                    ExtractAny(lastCreateJobRawResponse, "task_id") ??
                    ExtractAny(lastCreateJobRawResponse, "id");

                if (string.IsNullOrEmpty(jobId))
                {
                    Debug.LogWarning($"Meshy(JSON) success but no id. Body:\n{lastCreateJobRawResponse}\nPayload:\n{json}");
                    continue;
                }

                Debug.Log($"Meshy(JSON) job id: {jobId}");
                onJobId?.Invoke(jobId);
                yield break;
            }
        }

        onJobId?.Invoke(null);
    }

    private static string JsonPayload(object o) => JsonUtility.ToJson(o);

    // ---------- POLL ----------
    private IEnumerator PollMeshyJob(string jobId, Action<float> onProgress, Action<string> onCompleteUrl)
    {
        string urlToGet =
            (pollPathStyle == PollPathStyle.JobsSlashId)
            ? meshyJobUrl.TrimEnd('/') + "/" + jobId
            : meshyCreateUrl.TrimEnd('/') + "/" + jobId;

        while (true)
        {
            using (UnityWebRequest req = UnityWebRequest.Get(urlToGet))
            {
                req.SetRequestHeader("Authorization", $"Bearer {meshyApiKey}");
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"Meshy poll failed ({req.responseCode}): {req.error}\nBody:\n{req.downloadHandler.text}");
                    yield return new WaitForSeconds(2f);
                    continue;
                }

                string body = req.downloadHandler.text;

                string status =
                    ExtractAny(body, "status") ??
                    ExtractAny(body, "state");
                string progressStr =
                    ExtractAny(body, "progress") ??
                    ExtractAny(body, "pct") ??
                    ExtractAny(body, "percent");
                string resultUrl =
                    ExtractAny(body, "result_url") ??
                    ExtractAny(body, "glb") ??
                    ExtractAny(body, "download_url") ??
                    ExtractUrlEndingWith(body, ".glb");

                if (float.TryParse(progressStr, out var pVal))
                {
                    if (pVal > 1.01f) pVal /= 100f;
                    onProgress?.Invoke(Mathf.Clamp01(pVal));
                }

                if (!string.IsNullOrEmpty(status) &&
                    status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(resultUrl))
                {
                    onCompleteUrl?.Invoke(resultUrl);
                    yield break;
                }

                if (!string.IsNullOrEmpty(status) &&
                    status.Equals("failed", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogError("Meshy job failed. Body:\n" + body);
                    onCompleteUrl?.Invoke(null);
                    yield break;
                }

                yield return new WaitForSeconds(2f);
            }
        }
    }

    // ---------- DOWNLOAD / LOAD ----------
    private IEnumerator DownloadFile(string httpUrl, string localPath)
    {
        using (UnityWebRequest req = UnityWebRequest.Get(httpUrl))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"Download failed ({req.responseCode}): {req.error}\nURL: {httpUrl}");
                yield break;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(localPath));
            File.WriteAllBytes(localPath, req.downloadHandler.data);
        }
    }

    private IEnumerator LoadGlb(string localPath)
    {
        var gltf = new GltfImport();
        var loadTask = gltf.Load(localPath);
        while (!loadTask.IsCompleted) yield return null;

        if (loadTask.Result)
            gltf.InstantiateMainScene(transform);
        else
            Debug.LogError("Failed to load GLB: " + localPath);
    }

    // ---------- HELPERS ----------
    private static string ComputeHash(Texture2D tex)
    {
        var bytes = tex.EncodeToPNG();
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
        return value.Substring(0, max) + "...";
    }

    // tolerant extractor
    private static string ExtractAny(string json, string key)
    {
        var token = $"\"{key}\"";
        int i = json.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        i = json.IndexOf(':', i);
        if (i < 0) return null;

        int j = i + 1;
        while (j < json.Length && char.IsWhiteSpace(json[j])) j++;

        if (j < json.Length && json[j] == '"')
        {
            int startQ = j + 1;
            int endQ = json.IndexOf('"', startQ);
            if (endQ > startQ) return json.Substring(startQ, endQ - startQ);
        }
        else
        {
            int end = j;
            while (end < json.Length && ",}\n\r\t ".IndexOf(json[end]) < 0) end++;
            if (end > j) return json.Substring(j, end - j);
        }
        return null;
    }

    private static string ExtractUrlEndingWith(string json, string suffix)
    {
        var pattern = @"https?:\/\/[^\s""']+" + Regex.Escape(suffix);
        var m = Regex.Match(json, pattern);
        return m.Success ? m.Value : null;
    }
}
