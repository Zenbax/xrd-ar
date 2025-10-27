using System;
using System.Collections;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ARMeshyDemo.AR
{
    /// <summary>
    /// Captures a full-resolution CPU image from ARCameraManager, returning:
    ///  • hi-res Texture2D (RGBA32, readable) for AR tracking
    ///  • JPG bytes (optionally downscaled) for upload.
    ///
    /// Adds robust waiting after changing camera configuration and before
    /// acquiring CPU frames, to avoid race conditions where the stream
    /// briefly stops.
    /// </summary>
    public class CameraCapture : MonoBehaviour
    {
        [Header("Refs")]
        [SerializeField] private ARCameraManager arCameraManager;

        [Header("Capture Options")]
        [Tooltip("Force the highest available camera resolution before capturing.")]
        [SerializeField] private bool forceHighestResolution = true;

        [Tooltip("Flip vertically to match ARCore camera orientation (usually correct on Android).")]
        [SerializeField] private bool mirrorY = true;

        [Tooltip("How long (seconds) to wait for the camera stream to provide a CPU image.")]
        [SerializeField] private float cpuImageTimeoutSec = 2.0f;

        [Header("Encode")]
        [Range(1, 100)]
        [SerializeField] private int jpgQuality = 90;

        public void SetCameraManager(ARCameraManager cam) => arCameraManager = cam;

        /// <summary>
        /// Captures for tracking + upload.
        /// uploadLongSide:
        ///   If > 0, downscale the JPG so the long side equals this many pixels (saves bandwidth).
        ///   If 0, upload full resolution (larger upload).
        /// </summary>
        public IEnumerator CaptureForTrackingAndUpload(
            int uploadLongSide,
            Action<byte[], Texture2D> onComplete)
        {
#if UNITY_EDITOR
            Debug.LogWarning("[CameraCapture] CPU capture doesn't run in Editor. Test on device.");
            onComplete?.Invoke(null, null);
            yield break;
#else
            if (arCameraManager == null)
            {
                Debug.LogError("[CameraCapture] ARCameraManager is null.");
                onComplete?.Invoke(null, null);
                yield break;
            }

            // 1) Ensure camera subsystem and permission are ready
            yield return StartCoroutine(EnsureCameraReady());

            // 2) Optionally force the highest camera configuration
            if (forceHighestResolution)
            {
                TrySetHighestCameraResolution();
                // IMPORTANT: after changing configuration, the stream can briefly stop.
                // Wait until we can actually acquire a CPU image again.
                yield return StartCoroutine(WaitForCpuImageAvailable(cpuImageTimeoutSec));
            }

            // 3) Acquire a CPU image with a bounded timeout (works even if config wasn't changed)
            XRCpuImage cpuImage;
            if (!TryAcquireCpuImageWithTimeout(out cpuImage, cpuImageTimeoutSec))
            {
                Debug.LogError("[CameraCapture] Could not acquire CPU image (timeout).");
                onComplete?.Invoke(null, null);
                yield break;
            }

            // 4) Convert to RGBA32 at native resolution
            var conv = new XRCpuImage.ConversionParams
            {
                inputRect        = new RectInt(0, 0, cpuImage.width, cpuImage.height),
                outputDimensions = new Vector2Int(cpuImage.width, cpuImage.height),
                outputFormat     = TextureFormat.RGBA32,
                transformation   = mirrorY
                                    ? XRCpuImage.Transformation.MirrorY
                                    : XRCpuImage.Transformation.None
            };

            var request = cpuImage.ConvertAsync(conv);
            cpuImage.Dispose();

            while (!request.status.IsDone())
                yield return null;

            if (request.status != XRCpuImage.AsyncConversionStatus.Ready)
            {
                Debug.LogError($"[CameraCapture] ConvertAsync failed: {request.status}");
                request.Dispose();
                onComplete?.Invoke(null, null);
                yield break;
            }

            var raw = request.GetData<byte>();
            int w = conv.outputDimensions.x;
            int h = conv.outputDimensions.y;

            // Full-res, readable texture for runtime tracking
            var trackingTex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false, linear: false);
            trackingTex.LoadRawTextureData(raw);
            trackingTex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            request.Dispose();

            // 5) Build JPG for upload (optionally downscaled)
            Texture2D jpgSource = trackingTex;
            if (uploadLongSide > 0)
            {
                jpgSource = DownscaleLongSideGPU(trackingTex, uploadLongSide);
                Debug.Log($"[CameraCapture] Downscaled upload to {jpgSource.width}x{jpgSource.height}");
            }

            byte[] jpg = null;
            try
            {
                jpg = jpgSource.EncodeToJPG(jpgQuality);
            }
            catch (Exception e)
            {
                Debug.LogError($"[CameraCapture] EncodeToJPG failed: {e}");
                if (jpgSource != trackingTex) Destroy(jpgSource);
                Destroy(trackingTex);
                onComplete?.Invoke(null, null);
                yield break;
            }

            if (jpgSource != trackingTex) Destroy(jpgSource);

            Debug.Log($"[CameraCapture] Captured {w}x{h}; upload JPG = {jpg.Length / 1024} KB");
            onComplete?.Invoke(jpg, trackingTex);
#endif
        }

        // --- Helpers ---------------------------------------------------------

        /// <summary>Wait until the camera subsystem is running and (likely) permission granted.</summary>
        private IEnumerator EnsureCameraReady()
        {
            float start = Time.realtimeSinceStartup;

            // Wait for subsystem
            while (arCameraManager.subsystem == null || !arCameraManager.subsystem.running)
            {
                if (Time.realtimeSinceStartup - start > 3f) break; // don't hang forever
                yield return null;
            }

            // Permission is requested by AR Foundation automatically; give it a moment
            int tries = 0;
            while (!arCameraManager.permissionGranted && tries < 20)
            {
                tries++;
                yield return new WaitForSeconds(0.05f);
            }
        }

        /// <summary>Pick the largest (width*height) camera configuration and set it as current.</summary>
        private void TrySetHighestCameraResolution()
        {
            if (arCameraManager == null) return;

            using var configs = arCameraManager.GetConfigurations(Allocator.Temp);
            if (!configs.IsCreated || configs.Length == 0)
            {
                Debug.LogWarning("[CameraCapture] No AR camera configurations found.");
                return;
            }

            XRCameraConfiguration best = configs[0];
            for (int i = 1; i < configs.Length; i++)
            {
                var c = configs[i];
                if (c.width * c.height > best.width * best.height)
                    best = c;
            }

            try
            {
                arCameraManager.currentConfiguration = best;
                Debug.Log($"[CameraCapture] Forced highest camera config: {best.width}x{best.height}" +
                          (best.framerate.HasValue ? $" @ {best.framerate.Value}fps" : ""));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CameraCapture] Could not set camera configuration: {e.Message}");
            }
        }

        /// <summary>Wait until TryAcquireLatestCpuImage succeeds or timeout.</summary>
        private IEnumerator WaitForCpuImageAvailable(float timeoutSec)
        {
            float t = 0f;
            while (t < timeoutSec)
            {
                if (arCameraManager.TryAcquireLatestCpuImage(out var img))
                {
                    img.Dispose();
                    yield break; // success
                }
                t += Time.deltaTime;
                yield return null;
            }
            Debug.LogWarning("[CameraCapture] WaitForCpuImageAvailable timed out.");
        }

        /// <summary>Attempt to acquire a CPU image within a time budget.</summary>
        private bool TryAcquireCpuImageWithTimeout(out XRCpuImage cpuImage, float timeoutSec)
        {
            float t = 0f;
            while (t < timeoutSec)
            {
                if (arCameraManager.TryAcquireLatestCpuImage(out cpuImage))
                    return true;
                t += Time.deltaTime;
            }
            cpuImage = default;
            return false;
        }

        /// <summary>Fast GPU downscale to a target long side.</summary>
        private Texture2D DownscaleLongSideGPU(Texture2D src, int targetLongSide)
        {
            int w = src.width;
            int h = src.height;
            float scale = (w >= h) ? (targetLongSide / (float)w) : (targetLongSide / (float)h);
            int nw = Mathf.Max(1, Mathf.RoundToInt(w * scale));
            int nh = Mathf.Max(1, Mathf.RoundToInt(h * scale));

            var rt = RenderTexture.GetTemporary(nw, nh, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            Graphics.Blit(src, rt);

            var prev = RenderTexture.active;
            RenderTexture.active = rt;

            var dst = new Texture2D(nw, nh, TextureFormat.RGBA32, false, false);
            dst.ReadPixels(new Rect(0, 0, nw, nh), 0, 0);
            dst.Apply(false, false);

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return dst;
        }
    }
}
