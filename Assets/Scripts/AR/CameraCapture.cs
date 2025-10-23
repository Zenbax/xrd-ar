using System;
using System.Collections;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ARMeshyDemo.AR
{
    /// <summary>
    /// Captures a full-res CPU image from ARCameraManager.
    /// Returns: (a) hi-res Texture2D for AR tracking, (b) JPG bytes (optionally downscaled) for upload.
    /// </summary>
    public class CameraCapture : MonoBehaviour
    {
        [Header("Refs")]
        [SerializeField] private ARCameraManager arCameraManager;

        [Header("Encode")]
        [Range(1, 100)]
        [SerializeField] private int jpgQuality = 90;

        public void SetCameraManager(ARCameraManager cam) => arCameraManager = cam;

        /// <param name="uploadLongSide">
        /// If > 0, downscales the image’s long side to this many pixels for the JPG upload.
        /// If 0, uses the full resolution for JPG too (larger upload).
        /// </param>
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

            // Try until we get an image this frame or the next
            XRCpuImage cpuImage;
            if (!arCameraManager.TryAcquireLatestCpuImage(out cpuImage))
            {
                yield return null;
                if (!arCameraManager.TryAcquireLatestCpuImage(out cpuImage))
                {
                    Debug.LogError("[CameraCapture] Could not acquire CPU image.");
                    onComplete?.Invoke(null, null);
                    yield break;
                }
            }

            // Convert to RGBA32 at native resolution.
            // MirrorY is commonly correct for ARCore so the texture matches what the camera sees.
            var conv = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, cpuImage.width, cpuImage.height),
                outputDimensions = new Vector2Int(cpuImage.width, cpuImage.height),
                outputFormat = TextureFormat.RGBA32,
                transformation = XRCpuImage.Transformation.MirrorY
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

            // Full-res texture for AR tracking (keep readable!)
            var trackingTex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false, linear: false);
            trackingTex.LoadRawTextureData(raw);
            trackingTex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            request.Dispose();

            // Build JPG for upload (downscale if requested)
            Texture2D jpgSource = trackingTex;
            if (uploadLongSide > 0)
            {
                jpgSource = DownscaleLongSideGPU(trackingTex, uploadLongSide);
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

            Debug.Log($"[CameraCapture] Captured {w}x{h}; upload size = {jpg.Length / 1024} KB");
            onComplete?.Invoke(jpg, trackingTex);
#endif
        }

        // Fast GPU downscale using a temporary RenderTexture
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
