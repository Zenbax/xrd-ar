using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Jobs; // JobHandle
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Debug = UnityEngine.Debug;

namespace ARMeshyDemo.AR
{
    /// <summary>
    /// Opretter et runtime image library og tilføjer et Texture2D ved kørsel.
    /// Lytter på trackedImagesChanged og (valgfrit) viser et debug-overlay oven på det matchede billede.
    /// Tilpasset AR Foundation 6, hvor ScheduleAddImageJob returnerer JobHandle.
    /// </summary>
    public class RuntimeImageLibraryController : MonoBehaviour
    {
        [Header("AR")]
        [SerializeField] private ARTrackedImageManager trackedImageManager;
        [SerializeField] private XRReferenceImageLibrary serializedLibrary;
        [Tooltip("Fallback fysisk bredde i meter for referencen, hvis der ikke angives andet.")]
        [SerializeField] private float defaultPhysicalWidthMeters = 0.15f;

        [Header("Debug / Overlay")]
        [SerializeField] private bool verboseLogs = true;
        [SerializeField] private GameObject debugOverlayPrefab; // fx en Quad
        [SerializeField] private bool matchTrackedSize = true;

        private MutableRuntimeReferenceImageLibrary _runtimeLibrary;
        private string _currentRefName;
        private readonly Dictionary<Guid, GameObject> _spawned = new();

        // --- Lifecyle --------------------------------------------------------

        private void Awake()
        {
            if (!trackedImageManager)
                trackedImageManager = GetComponent<ARTrackedImageManager>();

            if (!trackedImageManager)
            {
                LogError("ARTrackedImageManager mangler på objektet.", true);
                enabled = false;
                return;
            }

            // Log platform/descriptor info
            var desc = trackedImageManager.descriptor;
            if (desc != null)
            {
                Log($"Subsystem: {desc.id}, supportsMutableLibrary={desc.supportsMutableLibrary}, supportsMovingImages={desc.supportsMovingImages}");
            }
            else
            {
                LogWarning("Kunne ikke læse ARTrackedImageSubsystemDescriptor (descriptor == null).");
            }

            Log($"SerializedLibrary: {(serializedLibrary ? serializedLibrary.name : "None")}");

            // Opret mutable runtime library (kræver ARCore/Android og descriptor.supportsMutableLibrary)
            _runtimeLibrary = trackedImageManager.CreateRuntimeLibrary(serializedLibrary) as MutableRuntimeReferenceImageLibrary;
            if (_runtimeLibrary == null)
            {
                LogWarning("CreateRuntimeLibrary gav ikke et MutableRuntimeReferenceImageLibrary. Runtime add er sandsynligvis ikke understøttet på denne platform.");
            }
            else
            {
                // Sørg for at manageren bruger vores runtime-library
                trackedImageManager.referenceLibrary = _runtimeLibrary;
                Log("Mutable runtime library oprettet og sat på ARTrackedImageManager.");
            }

            trackedImageManager.trackedImagesChanged += OnTrackedImagesChanged;
        }

        private void OnEnable()
        {
            Log("RuntimeImageLibraryController OnEnable()");
        }

        private void OnDisable()
        {
            Log("RuntimeImageLibraryController OnDisable()");
        }

        private void OnDestroy()
        {
            if (trackedImageManager)
                trackedImageManager.trackedImagesChanged -= OnTrackedImagesChanged;
        }

        // --- Public API ------------------------------------------------------

        /// <summary>
        /// Tilføj et Texture2D som reference-billede ved runtime.
        /// Kalder onDone(name, success).
        /// </summary>
        public IEnumerator AddImageFromTexture(
            Texture2D tex,
            string name = null,
            float? widthMeters = null,
            Action<string, bool> onDone = null)
        {
            if (_runtimeLibrary == null)
            {
                LogError("Runtime library er null. (Platform/driver understøtter ikke mutable libraries?)");
                onDone?.Invoke(null, false);
                yield break;
            }

            if (tex == null)
            {
                LogError("Texture er null.");
                onDone?.Invoke(null, false);
                yield break;
            }

            string imageName = name ?? $"meshy-ref-{DateTime.UtcNow:yyyyMMddHHmmss}";
            float width = widthMeters ?? defaultPhysicalWidthMeters;

            Log($"ScheduleAddImageJob('{imageName}', width={width:0.###} m, tex={tex.width}x{tex.height}, format={tex.format})");
            var start = Time.realtimeSinceStartup;

            JobHandle handle;
            try
            {
                handle = _runtimeLibrary.ScheduleAddImageJob(tex, imageName, width);
            }
            catch (Exception e)
            {
                LogError($"ScheduleAddImageJob exception: {e}");
                onDone?.Invoke(null, false);
                yield break;
            }

            while (!handle.IsCompleted)
                yield return null;

            handle.Complete();

            var dt = (Time.realtimeSinceStartup - start) * 1000f;
            _currentRefName = imageName;
            Log($"AddImage job complete på {dt:0} ms. CurrentRefName='{_currentRefName}'");

            onDone?.Invoke(_currentRefName, true);
        }

        /// <summary>Navnet på den sidst tilføjede reference (bruges til at filtrere events).</summary>
        public string CurrentRefName => _currentRefName;

        /// <summary>Fjerner alle debug-overlays, hvis du vil “rense scenen”.</summary>
        public void ClearDebugOverlays()
        {
            foreach (var kv in _spawned)
                if (kv.Value) Destroy(kv.Value);
            _spawned.Clear();
            Log("ClearDebugOverlays()");
        }

        // --- ARTrackedImage events ------------------------------------------

        private void OnTrackedImagesChanged(ARTrackedImagesChangedEventArgs args)
        {
            if (verboseLogs)
            {
                Log($"trackedImagesChanged: added={args.added.Count}, updated={args.updated.Count}, removed={args.removed.Count} (currentRef='{_currentRefName ?? "null"}')");
            }

            foreach (var img in args.added) TryHandle(img, "added");
            foreach (var img in args.updated) TryHandle(img, "updated");

            foreach (var img in args.removed)
            {
                if (_spawned.TryGetValue(img.referenceImage.guid, out var go) && go)
                    Destroy(go);
                _spawned.Remove(img.referenceImage.guid);

                if (verboseLogs)
                    Log($"removed {img.referenceImage.name} state={img.trackingState}");
            }
        }

        private void TryHandle(ARTrackedImage img, string phase)
        {
            var refName = img.referenceImage.name;

            if (verboseLogs)
                Log($"{phase} name='{refName}', tracking={img.trackingState}, size(m)=({img.size.x:0.###},{img.size.y:0.###})");

            // Reagér kun på det seneste tilføjede referencebillede
            if (!string.Equals(refName, _currentRefName, StringComparison.Ordinal))
                return;

            if (img.trackingState != TrackingState.Tracking)
                return;

            // Sørg for overlay-objekt
            if (!_spawned.TryGetValue(img.referenceImage.guid, out var go) || !go)
            {
                if (debugOverlayPrefab)
                {
                    go = Instantiate(debugOverlayPrefab, img.transform);
                    if (verboseLogs) Log("Instantiated debugOverlayPrefab som barn af det trackede billede.");
                }
                else
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    go.transform.SetParent(img.transform, worldPositionStays: false);
                    var mr = go.GetComponent<MeshRenderer>();
                    if (mr) mr.material.color = new Color(0f, 1f, 0.2f, 0.4f); // gennemsigtig grøn
                    if (verboseLogs) Log("Instantiated default Quad overlay (grøn, gennemsigtig).");
                }

                _spawned[img.referenceImage.guid] = go;
            }

            // Placér/skalér ovenpå det trackede billede
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            if (matchTrackedSize)
            {
                var size = img.size; // meter
                go.transform.localScale = new Vector3(size.x, size.y, 1f);
            }

            if (verboseLogs)
            {
                var s = go.transform.localScale;
                Log($"Overlay aktivt på '{refName}'. scale=({s.x:0.###},{s.y:0.###},{s.z:0.###}) localPos=(0,0,0) localRot=(0,0,0)");
            }
        }

        // --- Logging helpers -------------------------------------------------

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
