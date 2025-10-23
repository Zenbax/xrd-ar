using System;
using System.Collections;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ARMeshyDemo.AR
{
    /// <summary>
    /// Henter et stillbillede (CPU) fra AR-kameraet og konverterer det til Texture2D + JPG-bytes.
    /// Brug som Coroutine: yield return StartCoroutine(CaptureJpgCoroutine(...));
    /// </summary>
    public class CameraCapture : MonoBehaviour
    {
        [Header("Refs")]
        [SerializeField] private ARCameraManager arCameraManager;

        [Header("Encode")]
        [Range(1, 100)]
        [SerializeField] private int jpgQuality = 90;

        public void SetCameraManager(ARCameraManager cam) => arCameraManager = cam;

        /// <summary>
        /// Tager et foto fra ARCameraManager som JPG. Kalder onComplete(jpgBytes, texture).
        /// Texture2D bliver i RGB24/RGBA32 og er 'readable'. Husk at Destroy() den selv, når du er færdig.
        /// </summary>
        public IEnumerator CaptureJpgCoroutine(Action<byte[], Texture2D> onComplete)
        {
#if UNITY_EDITOR
            UnityEngine.Debug.LogWarning("[CameraCapture] CPU capture virker ikke i Editor. Brug device for ægte kamera.");
            onComplete?.Invoke(null, null);
            yield break;
#else
            if (arCameraManager == null)
            {
                UnityEngine.Debug.LogError("[CameraCapture] ARCameraManager er ikke sat.");
                onComplete?.Invoke(null, null);
                yield break;
            }

            if (!arCameraManager.permissionGranted)
            {
                UnityEngine.Debug.LogWarning("[CameraCapture] Kamera-tilladelse ikke bekræftet endnu.");
                // AR Foundation beder normalt selv om tilladelse. Vent kort og prøv igen.
                yield return new WaitForSeconds(0.5f);
            }

            // Forsøg at hente et CPU-billede fra seneste frame
            if (!arCameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage))
            {
                UnityEngine.Debug.LogWarning("[CameraCapture] Kunne ikke hente CPU-image i denne frame. Prøver næste frame...");
                yield return null; // vent en frame
                if (!arCameraManager.TryAcquireLatestCpuImage(out cpuImage))
                {
                    UnityEngine.Debug.LogError("[CameraCapture] Kunne stadig ikke hente CPU-image.");
                    onComplete?.Invoke(null, null);
                    yield break;
                }
            }

            // Vi konverterer til RGBA32 via async conversion for at undgå unsafe pointer.
            var conversionParams = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, cpuImage.width, cpuImage.height),
                outputDimensions = new Vector2Int(cpuImage.width, cpuImage.height),
                outputFormat = TextureFormat.RGBA32,
                transformation = XRCpuImage.Transformation.None // evt. MirrorX/MirrorY/Rotate90 hvis du vil rotere
            };

            // Start async-konvertering
            var request = cpuImage.ConvertAsync(conversionParams);

            // Vi er færdige med cpuImage-ressourcen
            cpuImage.Dispose();

            // Vent til async conversion er færdig
            while (!request.status.IsDone())
                yield return null;

            if (request.status != XRCpuImage.AsyncConversionStatus.Ready)
            {
                UnityEngine.Debug.LogError($"[CameraCapture] ConvertAsync fejlede: {request.status}");
                request.Dispose();
                onComplete?.Invoke(null, null);
                yield break;
            }

            // Hent konverterede bytes (RGBA32)
            var rawData = request.GetData<byte>();
            int width = conversionParams.outputDimensions.x;
            int height = conversionParams.outputDimensions.y;

            // Lav Texture2D og fyld data
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false, linear: false);
            tex.LoadRawTextureData(rawData);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);

            // Ryd request-ressource
            request.Dispose();

            // Encode til JPG
            byte[] jpg = null;
            try
            {
                jpg = tex.EncodeToJPG(jpgQuality);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"[CameraCapture] EncodeToJPG fejl: {e}");
                UnityEngine.Object.Destroy(tex);
                onComplete?.Invoke(null, null);
                yield break;
            }

            onComplete?.Invoke(jpg, tex);
            yield break;
#endif
        }
    }
}
