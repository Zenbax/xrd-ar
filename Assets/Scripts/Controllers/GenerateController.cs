using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using ARMeshyDemo.UI;
using ARMeshyDemo.AR;
using ARMeshyDemo.Net;
using Debug = UnityEngine.Debug;

namespace ARMeshyDemo.Controllers
{
    public class GenerateController : MonoBehaviour
    {
        [Header("UI")]
        [SerializeField] private UIController ui;
        [SerializeField] private Button generateButton;

        [Header("Overlay")]
        [SerializeField] private ARMeshyDemo.AR.ModelOverlayController overlay; // <- BIND i Inspector

        [Header("AR")]
        [SerializeField] private CameraCapture cameraCapture;
        [SerializeField] private RuntimeImageLibraryController runtimeLib;
        [Tooltip("Physical width (meters) to register for the captured reference image.")]
        [SerializeField] private float refPhysicalWidthMeters = 0.15f;

        [Header("Net")]
        [SerializeField] private MeshyClient meshyClient;
        [SerializeField] private GltfLoader gltfLoader;

        // --- Last-run / loaded state -----------------------------------------
        private byte[] _lastRefJpg;
        private Texture2D _lastRefTex;
        private string _lastTaskId;
        private string _lastGlbUrl;
        private GameObject _loadedModel;

        private bool _busy;
        private bool _cancelRequested;
        private int _prevSleep;

        void OnEnable()
        {
            if (generateButton) generateButton.onClick.AddListener(OnGenerateClicked);
            if (ui && ui.CancelButton) ui.CancelButton.onClick.AddListener(OnCancelClicked);
            if (ui && ui.RetryButton) ui.RetryButton.onClick.AddListener(OnRetryClicked);
        }

        void OnDisable()
        {
            if (generateButton) generateButton.onClick.RemoveListener(OnGenerateClicked);
            if (ui && ui.CancelButton) ui.CancelButton.onClick.RemoveListener(OnCancelClicked);
            if (ui && ui.RetryButton) ui.RetryButton.onClick.RemoveListener(OnRetryClicked);
        }

        private void OnGenerateClicked()
        {
            if (_busy) return;
            StartCoroutine(GenerateRoutine(fullPipeline: true));
        }

        private void OnRetryClicked()
        {
            if (_busy) return;
            ui.HideError();
            StartCoroutine(GenerateRoutine(fullPipeline: true));
        }

        private void OnCancelClicked()
        {
            if (!_busy) return;
            _cancelRequested = true;
            ui.SetStatus("Cancelling...");
            ui.ShowCancel(false);
        }

        public GameObject GetLoadedModel() => _loadedModel;

        private IEnumerator GenerateRoutine(bool fullPipeline)
        {
            // --- Preconditions -------------------------------------------------
            if (ui == null) Debug.LogWarning("[Generate] UIController not assigned");
            if (cameraCapture == null) { ui?.SetLoading(false, "CameraCapture missing."); yield break; }
            if (runtimeLib == null) { ui?.SetLoading(false, "RuntimeImageLibraryController missing."); yield break; }
            if (meshyClient == null) { ui?.SetLoading(false, "MeshyClient missing."); yield break; }
            if (gltfLoader == null) { ui?.SetLoading(false, "GltfLoader missing."); yield break; }

            _busy = true;
            _cancelRequested = false;
            _lastGlbUrl = null;
            _loadedModel = null;

            _prevSleep = Screen.sleepTimeout;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            ui.SetProgressVisible(true);
            ui.SetProgress(0f, "Capturing reference photo...");
            ui.ShowCancel(true);
            ui.ShowRetry(false);
            ui.SetLoading(true);

            // === Capture (10%) ================================================
            _lastRefJpg = null;
            _lastRefTex = null;

            // Ask for a smaller JPG for upload (e.g. long side 1024) but keep a full-res Texture2D for tracking
            const int UploadLongSide = 1024;

            yield return StartCoroutine(cameraCapture.CaptureForTrackingAndUpload(
            UploadLongSide,
            (jpg, tex) =>
            {
                _lastRefJpg = jpg;     // send this to Meshy
                _lastRefTex = tex;     // use this (full-res) for AddImageFromTexture
            }));


            if (_cancelRequested) { Done("Cancelled."); yield break; }

            if (_lastRefJpg == null || _lastRefTex == null)
            {
                Fail("Capture failed. Try again.");
                yield break;
            }
            ui.SetProgress(0.10f, "Adding reference for tracking...");

            // === Runtime add (20%) ============================================
            bool addOk = false;
            yield return StartCoroutine(runtimeLib.AddImageFromTexture(
                _lastRefTex, null, refPhysicalWidthMeters,
                onDone: (n, ok) => addOk = ok
            ));

            if (_cancelRequested) { Done("Cancelled."); yield break; }
            if (!addOk)
            {
                Fail("Failed to add tracking reference.");
                yield break;
            }
            ui.SetProgress(0.20f, "Uploading to Meshy...");

            // === Create task (25%→30%) =======================================
            string errMsg = null;
            string taskId = null;

            // ✅ Explicitly request textures and PBR materials
            yield return StartCoroutine(meshyClient.CreateImageTo3D(
                _lastRefJpg, "image/jpeg",
                onOk: id => taskId = id,
                onErr: e => errMsg = e,
                shouldRemesh: true,
                shouldTexture: true,  // ✅ Enable texturing
                enablePbr: true       // ✅ Enable PBR materials
            ));

            if (_cancelRequested) { Done("Cancelled."); yield break; }
            if (!string.IsNullOrEmpty(errMsg) || string.IsNullOrEmpty(taskId))
            {
                Fail(errMsg ?? "Upload failed.");
                yield break;
            }

            _lastTaskId = taskId;
            ui.SetProgress(0.25f, "Generating 3D model...");

            // === Poll (25% → 80%) ============================================
            bool canDownload = false;

            yield return StartCoroutine(meshyClient.PollImageTo3D(
                taskId,
                intervalSec: 3f,
                onProgress: task =>
                {
                    float p = Mathf.Lerp(0.25f, 0.80f, Mathf.Clamp01(task.progress / 100f));
                    ui.SetProgress(p, $"Generating... {task.progress:0}%");
                },
                onDone: task =>
                {
                    if (task?.model_urls == null)
                    {
                        errMsg = "Task completed but no model URLs returned.";
                        return;
                    }

                    // ✅ PREFER GLB (works with GLTFUtility, has embedded textures)
                    if (!string.IsNullOrEmpty(task.model_urls.glb))
                    {
                        _lastGlbUrl = task.model_urls.glb;
                        Debug.Log("[GenerateController] Using GLB format (GLTFUtility)");
                    }
                    // ✅ FALLBACK: Try FBX if available (requires TriLib 2 - $95)
                    else if (!string.IsNullOrEmpty(task.model_urls.fbx))
                    {
                        _lastGlbUrl = task.model_urls.fbx;
                        Debug.LogWarning("[GenerateController] Using FBX format (requires TriLib 2 plugin)");
                    }
                    // ✅ LAST RESORT: OBJ (requires runtime OBJ loader)
                    else if (!string.IsNullOrEmpty(task.model_urls.obj))
                    {
                        _lastGlbUrl = task.model_urls.obj;
                        Debug.LogWarning("[GenerateController] Using OBJ format (requires OBJ loader plugin)");
                    }
                    else
                    {
                        errMsg = "No supported model format available.";
                        return;
                    }

                    canDownload = true;
                },
                onErr: e => { errMsg = e; },
                timeoutSec: 600f
            ));

            if (_cancelRequested) { Done("Cancelled."); yield break; }
            if (!string.IsNullOrEmpty(errMsg))
            {
                Fail(errMsg);
                yield break;
            }
            if (!canDownload || string.IsNullOrEmpty(_lastGlbUrl))
            {
                Fail("No model URL from task.");
                yield break;
            }

            Debug.Log("[Generate] GLB URL: " + _lastGlbUrl);

            // === Download & Load (80% → 100%) =================================
            ui.SetStatus("Downloading & loading GLB...");
            string glbErr = null;

            yield return StartCoroutine(gltfLoader.LoadFromUrl(
                _lastGlbUrl,
                onOk: go =>
                {
                    _loadedModel = go;
                    _loadedModel.SetActive(false); // aktiveres ved attach
                    ui.SetProgress(1f, "Model ready ✓");

                    // Forsøg at attach'e STRAKS (hvis billedet allerede er i view)
                    overlay?.TryAttachToCurrentlyTracked();
                    if (overlay == null)
                    {
                        overlay = FindObjectOfType<ARMeshyDemo.AR.ModelOverlayController>();
                        overlay?.TryAttachToCurrentlyTracked();
                    }
                },
                onErr: e => glbErr = e,
                onProgress: p =>
                {
                    float mapped = Mathf.Lerp(0.80f, 1f, Mathf.Clamp01(p));
                    ui.SetProgress(mapped, $"Loading model... {(mapped * 100f):0}%");
                }
            ));

            if (!string.IsNullOrEmpty(glbErr) || _loadedModel == null)
            {
                Fail(glbErr ?? "Failed to load model.");
                yield break;
            }

            // KALD FELTET igen (ingen lokal var!)
            overlay?.TryAttachToCurrentlyTracked();
            if (overlay == null)
            {
                overlay = FindObjectOfType<ARMeshyDemo.AR.ModelOverlayController>();
                overlay?.TryAttachToCurrentlyTracked();
            }

            Done("Model ready ✓");
        }

        private void Fail(string message)
        {
            _busy = false;
            ui.SetLoading(false);
            ui.ShowCancel(false);
            ui.SetProgressVisible(false);
            ui.ShowError(message, showRetry: true);
            Screen.sleepTimeout = _prevSleep;
        }

        private void Done(string status)
        {
            _busy = false;
            ui.SetLoading(false, status);
            ui.ShowCancel(false);
            ui.SetProgressVisible(false);
            Screen.sleepTimeout = _prevSleep;
        }
    }
}
