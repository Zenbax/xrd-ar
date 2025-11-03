using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Debug = UnityEngine.Debug;

namespace ARMeshyDemo.AR
{
    public class RuntimeImageLibraryController : MonoBehaviour
    {
        [Header("AR")]
        [SerializeField] private ARTrackedImageManager trackedImageManager;
        [SerializeField] private XRReferenceImageLibrary serializedLibrary;
        [SerializeField] private float defaultPhysicalWidthMeters = 0.15f;

        [Header("Debug / Overlay")]
        [SerializeField] private bool verboseLogs = true;
        [SerializeField] private GameObject debugOverlayPrefab;
        [SerializeField] private bool matchTrackedSize = true;

        private MutableRuntimeReferenceImageLibrary _runtimeLibrary;
        private string _currentRefName;
        private readonly Dictionary<Guid, GameObject> _spawned = new();
        private bool _usingRuntimeLibrary;

        // Awake: only gather references; do NOT touch ARCore yet.
        private void Awake()
        {
            if (!trackedImageManager)
                trackedImageManager = GetComponent<ARTrackedImageManager>();

            if (!trackedImageManager)
            {
                LogError("ARTrackedImageManager is missing on this GameObject.", true);
                enabled = false;
                return;
            }
        }

        // Start: wait until AR is ready, then initialize the library safely.
        private IEnumerator Start()
        {
            // Wait until AR session/subsystem is ready and running
            while (ARSession.state < ARSessionState.Ready ||
                   trackedImageManager.subsystem == null ||
                   !trackedImageManager.subsystem.running)
            {
                yield return null;
            }

            InitializeRuntimeLibrary();
        }

        private void OnEnable()
        {
            if (trackedImageManager != null)
                trackedImageManager.trackedImagesChanged += OnTrackedImagesChanged;
        }

        private void OnDisable()
        {
            if (trackedImageManager != null)
                trackedImageManager.trackedImagesChanged -= OnTrackedImagesChanged;
        }

        // --------------------------------------------------------------------
        // Initialization (safe, crash-proof)
        // --------------------------------------------------------------------
        private void InitializeRuntimeLibrary()
        {
            var desc = trackedImageManager.descriptor;
            if (desc == null)
            {
                LogWarning("No ARTrackedImageSubsystemDescriptor found; using static library.");
                trackedImageManager.referenceLibrary = serializedLibrary;
                _usingRuntimeLibrary = false;
                return;
            }

            Log($"Subsystem: {desc.id}, supportsMutableLibrary={desc.supportsMutableLibrary}");

            // Base library to preload into the runtime clone (if supported)
            XRReferenceImageLibrary baseLib = serializedLibrary ??
                trackedImageManager.referenceLibrary as XRReferenceImageLibrary;

            if (!desc.supportsMutableLibrary)
            {
                LogWarning("Mutable libraries not supported on this device. Using static library.");
                trackedImageManager.referenceLibrary = baseLib;
                _usingRuntimeLibrary = false;
                return;
            }

            try
            {
                _runtimeLibrary = trackedImageManager.CreateRuntimeLibrary(baseLib)
                    as MutableRuntimeReferenceImageLibrary;
            }
            catch (Exception e)
            {
                LogError($"CreateRuntimeLibrary() failed: {e.Message}");
                _runtimeLibrary = null;
            }

            if (_runtimeLibrary != null)
            {
                trackedImageManager.referenceLibrary = _runtimeLibrary;
                _usingRuntimeLibrary = true;
                Log($"Mutable runtime library created. Preloaded={(baseLib != null ? baseLib.count : 0)}");
            }
            else
            {
                LogWarning("Falling back to static serialized library.");
                trackedImageManager.referenceLibrary = baseLib;
                _usingRuntimeLibrary = false;
            }
        }

        // --------------------------------------------------------------------
        // Public API
        // --------------------------------------------------------------------
        public IEnumerator AddImageFromTexture(
            Texture2D tex,
            string name = null,
            float? widthMeters = null,
            Action<string, bool> onDone = null)
        {
            if (!_usingRuntimeLibrary || _runtimeLibrary == null)
            {
                LogError("Runtime add attempted but mutable libraries are not supported/active.");
                onDone?.Invoke(null, false);
                yield break;
            }

            if (tex == null)
            {
                LogError("Texture is null.");
                onDone?.Invoke(null, false);
                yield break;
            }

            var useTex = EnsureReadableRGBA32(tex);
            string imageName = name ?? $"meshy-ref-{DateTime.UtcNow:yyyyMMddHHmmss}";
            float width = widthMeters ?? defaultPhysicalWidthMeters;

            Log($"ScheduleAddImageWithValidationJob('{imageName}', {useTex.width}x{useTex.height})");

            AddReferenceImageJobState jobState;
            try
            {
                NativeSlice<byte> rawBytes = useTex.GetRawTextureData<byte>();
                var dims = new Vector2Int(useTex.width, useTex.height);

                var referenceImage = new XRReferenceImage(
                    new SerializableGuid(0, 0),
                    new SerializableGuid(0, 0),
                    new Vector2(width, width), // physical size (approx)
                    imageName,
                    null
                );

                jobState = _runtimeLibrary.ScheduleAddImageWithValidationJob(
                    rawBytes, dims, useTex.format, referenceImage);
            }
            catch (Exception e)
            {
                LogError($"ScheduleAddImageWithValidationJob exception: {e}");
                onDone?.Invoke(null, false);
                yield break;
            }

            while (!jobState.jobHandle.IsCompleted)
                yield return null;
            jobState.jobHandle.Complete();

            bool ok = jobState.status == AddReferenceImageJobStatus.Success;
            if (!ok)
            {
                LogError($"AddImage failed: {jobState.status}");
                onDone?.Invoke(null, false);
                yield break;
            }

            _currentRefName = imageName;
            Log($"AddImage success. Library now has {_runtimeLibrary.count} images.");
            onDone?.Invoke(_currentRefName, true);
        }

        public string CurrentRefName => _currentRefName;

        public void ClearDebugOverlays()
        {
            foreach (var kv in _spawned)
                if (kv.Value) Destroy(kv.Value);
            _spawned.Clear();
        }

        // --------------------------------------------------------------------
        // Tracked Image Events
        // --------------------------------------------------------------------
        private void OnTrackedImagesChanged(ARTrackedImagesChangedEventArgs args)
        {
            foreach (var img in args.added)   TryHandle(img, "added");
            foreach (var img in args.updated) TryHandle(img, "updated");

            foreach (var img in args.removed)
            {
                if (_spawned.TryGetValue(img.referenceImage.guid, out var go) && go)
                    Destroy(go);
                _spawned.Remove(img.referenceImage.guid);
            }
        }

        private void TryHandle(ARTrackedImage img, string phase)
        {
            if (!string.Equals(img.referenceImage.name, _currentRefName, StringComparison.Ordinal))
                return;
            if (img.trackingState != TrackingState.Tracking)
                return;

            if (!_spawned.TryGetValue(img.referenceImage.guid, out var go) || !go)
            {
                go = debugOverlayPrefab
                    ? Instantiate(debugOverlayPrefab, img.transform)
                    : GameObject.CreatePrimitive(PrimitiveType.Quad);

                go.transform.SetParent(img.transform, worldPositionStays: false);

                if (!debugOverlayPrefab)
                {
                    var mr = go.GetComponent<MeshRenderer>();
                    if (mr)
                    {
                        var mat = new Material(Shader.Find("Unlit/Color"));
                        mat.color = new Color(0f, 1f, 0.2f, 0.35f);
                        mr.material = mat;
                    }
                }

                _spawned[img.referenceImage.guid] = go;
            }

            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            if (matchTrackedSize)
            {
                var size = img.size;
                go.transform.localScale = new Vector3(size.x, size.y, 1f);
            }
        }

        // --------------------------------------------------------------------
        // Helpers
        // --------------------------------------------------------------------
        private Texture2D EnsureReadableRGBA32(Texture2D source)
        {
            if (source.isReadable && source.format == TextureFormat.RGBA32)
                return source;

            try
            {
                var tmp = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                tmp.SetPixels32(source.GetPixels32());
                tmp.Apply(false, false);
                return tmp;
            }
            catch (Exception e)
            {
                LogWarning($"Failed to clone texture to RGBA32: {e.Message}");
                return source;
            }
        }

        private void Log(string msg)
        {
            if (verboseLogs) Debug.Log($"[RuntimeImageLibrary] {msg}");
        }
        private void LogWarning(string msg)
        {
            if (verboseLogs) Debug.LogWarning($"[RuntimeImageLibrary] {msg}");
        }
        private void LogError(string msg, bool always = false)
        {
            if (always || verboseLogs) Debug.LogError($"[RuntimeImageLibrary] {msg}");
        }
    }
}
