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

            // trackedImagesChanged is deprecated in newer AR Foundation versions but still accessible;
            // trackablesChanged has an inaccessible setter here, so use trackedImagesChanged.
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

        public Texture2D CaptureCameraFrame()
        {
            var rt = new RenderTexture(Screen.width, Screen.height, 24);
            ScreenCapture.CaptureScreenshotIntoRenderTexture(rt);
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            rt.Release();
            lastCaptured = tex;
            return tex;
        }

        public void AddCapturedAsReferenceImage(Action onAdded, Action<string> onError)
        {
            if (runtimeLib == null) { onError?.Invoke("Runtime image library not supported"); return; }
            if (lastCaptured == null) { onError?.Invoke("No captured image"); return; }

            const float sizeMeters = 0.2f; // heuristic; change if you know physical size.
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
                if (jobState.status == AddReferenceImageJobStatus.Success)
                {
                    onDone?.Invoke();
                }
                else
                {
                    onError?.Invoke($"Add image job failed: {jobState.status}");
                }
            }
            catch (Exception e)
            {
                onError?.Invoke(e.Message);
            }
        }

        private void OnTrackedImagesChanged(ARTrackedImagesChangedEventArgs args)
        {
            // Consumed by orchestrator via its own subscription; nothing to do here.
        }
    }
}
