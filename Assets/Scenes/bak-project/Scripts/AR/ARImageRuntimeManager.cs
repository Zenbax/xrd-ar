using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace Scenes.bak_project.Scripts.AR
{
    public class ARImageRuntimeManager : MonoBehaviour
    {
        [Header("Refs")]
        public ARTrackedImageManager trackedImageManager;
        public ARCameraManager cameraManager;  // ? ADD THIS - assign in Inspector!
        public GameObject modelRoot;
        public GameObject loadingSpinnerPrefab;

        private MutableRuntimeReferenceImageLibrary runtimeLib;
        private Texture2D lastCaptured;

        void OnEnable()
        {
            if (trackedImageManager == null)
            {
                Debug.LogError("ARTrackedImageManager not assigned");
                enabled = false;
                return;
            }

            // ? Find ARCameraManager if not assigned
            if (cameraManager == null)
            {
                cameraManager = FindObjectOfType<ARCameraManager>();
                if (cameraManager == null)
                {
                    Debug.LogError("ARCameraManager not found! Assign it in Inspector.");
                }
            }

            trackedImageManager.trackedImagesChanged += OnTrackedImagesChanged;

            if (trackedImageManager.referenceLibrary is MutableRuntimeReferenceImageLibrary m)
            {
                runtimeLib = m;
            }
            else if (trackedImageManager.descriptor.supportsMutableLibrary)
            {
                runtimeLib = trackedImageManager.CreateRuntimeLibrary() as MutableRuntimeReferenceImageLibrary;
                trackedImageManager.referenceLibrary = runtimeLib;
            }
            else
            {
                Debug.LogWarning("Mutable runtime image libraries not supported on this device.");
            }
        }

        void OnDisable()
        {
            if (trackedImageManager != null)
                trackedImageManager.trackedImagesChanged -= OnTrackedImagesChanged;
        }

        /// <summary>
        /// ? FIXED: Capture from AR camera feed (XRCpuImage) instead of screen
        /// This captures the pure camera feed without UI overlays, in the correct format for ARCore validation.
        /// </summary>
        public Texture2D CaptureCameraFrame()
        {
            if (cameraManager == null || !cameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage))
            {
                Debug.LogError("[ARImageRuntime] Failed to acquire camera image. Make sure ARCameraManager is assigned!");
                return null;
            }

            // ? Convert to RGBA32 with mipmaps (required by ARCore for image validation)
            var conversionParams = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, cpuImage.width, cpuImage.height),
                outputDimensions = new Vector2Int(cpuImage.width, cpuImage.height),
                outputFormat = TextureFormat.RGBA32,  // ? ARCore prefers RGBA32
                transformation = XRCpuImage.Transformation.MirrorY  // ? Flip for Unity coordinate system
            };

            // ? Create texture with mipmaps (CRITICAL for ARCore tracking quality)
            var texture = new Texture2D(
                conversionParams.outputDimensions.x,
                conversionParams.outputDimensions.y,
                conversionParams.outputFormat,
                mipChain: true  // ? ARCore needs mipmaps for scale-invariant tracking
            );

            // ? Convert CPU image to texture
            var rawData = texture.GetRawTextureData<byte>();
            cpuImage.Convert(conversionParams, rawData);
            texture.Apply(updateMipmaps: true);  // ? Generate mipmap chain

            cpuImage.Dispose();

            lastCaptured = texture;
            Debug.Log($"[ARImageRuntime] Captured AR camera frame: {texture.width}x{texture.height}, format={texture.format}, mipmaps={texture.mipmapCount}");
            
            return texture;
        }

        public void AddCapturedAsReferenceImage(Action onAdded, Action<string> onError)
        {
            if (runtimeLib == null) 
            { 
                onError?.Invoke("Runtime image library not supported"); 
                return; 
            }
            
            if (lastCaptured == null) 
            { 
                onError?.Invoke("No captured image"); 
                return; 
            }

            // ? Validate texture properties before adding (helps catch issues early)
            if (lastCaptured.width < 128 || lastCaptured.height < 128)
            {
                onError?.Invoke($"Image too small: {lastCaptured.width}x{lastCaptured.height} (minimum 128x128)");
                return;
            }

            if (lastCaptured.format != TextureFormat.RGBA32 && lastCaptured.format != TextureFormat.RGB24)
            {
                onError?.Invoke($"Invalid texture format: {lastCaptured.format} (need RGBA32 or RGB24)");
                return;
            }

            if (lastCaptured.mipmapCount <= 1)
            {
                Debug.LogWarning("[ARImageRuntime] Texture has no mipmaps! This may reduce tracking quality.");
            }

            const float sizeMeters = 0.2f; // heuristic; change if you know physical size.
            
            Debug.Log($"[ARImageRuntime] Adding image to library: {lastCaptured.width}x{lastCaptured.height}, format={lastCaptured.format}, mipmaps={lastCaptured.mipmapCount}, physical size={sizeMeters}m");
            
            var jobState = runtimeLib.ScheduleAddImageWithValidationJob(lastCaptured, "CapturedRef", sizeMeters);
            StartCoroutine(WaitForJob(jobState, onAdded, onError));
        }

        private IEnumerator WaitForJob(AddReferenceImageJobState jobState, Action onDone, Action<string> onError)
        {
            // Wait for the underlying job handle to complete
            while (!jobState.jobHandle.IsCompleted)
                yield return null;

            try
            {
                // Complete the job handle and check status
                jobState.jobHandle.Complete();
                
                Debug.Log($"[ARImageRuntime] AddImage job completed with status: {jobState.status}");
                
                if (jobState.status == AddReferenceImageJobStatus.Success)
                {
                    Debug.Log("[ARImageRuntime] ? Image added successfully to tracking library!");
                    onDone?.Invoke();
                }
                else
                {
                    string errorMsg = $"Add image job failed: {jobState.status}";
                    
                    // ? Provide detailed error messages to help debugging
                    switch (jobState.status)
                    {
                        case AddReferenceImageJobStatus.ErrorInvalidImage:
                            errorMsg = "Image validation failed. Ensure image:\n" +
                                      "• Is at least 128x128 pixels\n" +
                                      "• Has high contrast features (not blurry)\n" +
                                      "• Has texture variation (not solid color)\n" +
                                      "• Contains trackable patterns (edges, corners)";
                            break;
                        case AddReferenceImageJobStatus.ErrorUnknown:
                            errorMsg = "Unknown error adding image. Check ARCore logs for details.";
                            break;
                    }
                    
                    Debug.LogError($"[ARImageRuntime] {errorMsg}");
                    onError?.Invoke(errorMsg);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[ARImageRuntime] Exception in AddImage job: {e}");
                onError?.Invoke(e.Message);
            }
        }

        private void OnTrackedImagesChanged(ARTrackedImagesChangedEventArgs args)
        {
            // Consumed by orchestrator via its own subscription; nothing to do here.
        }
    }
}
