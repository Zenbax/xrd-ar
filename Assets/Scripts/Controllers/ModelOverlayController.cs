using System;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using ARMeshyDemo.Controllers;
using Debug = UnityEngine.Debug; // Brug Unity Debug

namespace ARMeshyDemo.AR
{
    public class ModelOverlayController : MonoBehaviour
    {
        [Header("Refs")]
        [SerializeField] private ARTrackedImageManager trackedImageManager;
        [SerializeField] private RuntimeImageLibraryController runtimeLib;
        [SerializeField] private GenerateController generateController;

        [Header("Placement")]
        [Tooltip("Skaler modellen så dens bredde matcher det trackede billedes bredde (meter).")]
        [SerializeField] private bool matchTrackedWidth = true;
        [SerializeField] private float widthScaleMultiplier = 1f;
        [SerializeField] private Vector3 localPositionOffset = Vector3.zero;
        [Tooltip("Typisk skal GLB'er roteres -90 i X for at stå op på et AR-billede (som ligger i X-Y).")]
        [SerializeField] private Vector3 localEulerOffset = new Vector3(-90, 0, 0);
        [SerializeField] private bool hideWhenNotTracking = true;

        [Header("Locking")]
        [Tooltip("Lås til første trackede instans, så modellen ikke hopper mellem flere kopier af samme billede.")]
        [SerializeField] private bool lockToFirstSeen = true;
        [SerializeField] private float lostReleaseSeconds = 1.0f;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;
        [Tooltip("Hvis true: forsøger at re-attache når modellen bliver loadet, selv hvis tracked event kom før.")]
        [SerializeField] private bool tryAttachImmediatelyOnModelLoaded = true;
        [Tooltip("Vis gizmos på det aktuelle attach-punkt (kun i Editor).")]
        [SerializeField] private bool drawAttachGizmos = false;

        // Runtime state
        private GameObject _model;
        private Bounds _modelBounds;
        private bool _haveBounds;

        private TrackableId? _lockedTrackable;
        private float _lostTimer = 0f;

        // ---------------------------------------------------------------------

        private void Awake()
        {
            if (!trackedImageManager) trackedImageManager = FindObjectOfType<ARTrackedImageManager>();
            if (!runtimeLib) runtimeLib = FindObjectOfType<RuntimeImageLibraryController>();
            if (!generateController) generateController = FindObjectOfType<GenerateController>();

            if (!trackedImageManager) LogError("Missing ARTrackedImageManager");
            if (!runtimeLib) LogError("Missing RuntimeImageLibraryController");
            if (!generateController) LogError("Missing GenerateController");
        }

        private void OnEnable()
        {
            if (trackedImageManager != null)
                trackedImageManager.trackedImagesChanged += OnTrackedImagesChanged;

            Log("OnEnable()");
        }

        private void OnDisable()
        {
            if (trackedImageManager != null)
                trackedImageManager.trackedImagesChanged -= OnTrackedImagesChanged;

            Log("OnDisable()");
        }

        private void Update()
        {
            // Hent model reference hvis vi ikke har gjort det endnu.
            if (_model == null && generateController != null)
            {
                var m = generateController.GetLoadedModel();
                if (m != null)
                {
                    _model = m;
                    Log("[Update] Model reference fetched from GenerateController.");

                    if (tryAttachImmediatelyOnModelLoaded)
                        TryAttachToCurrentlyTracked();
                }
            }

            if (lockToFirstSeen && _lockedTrackable.HasValue)
            {
                var img = FindTrackedImageById(_lockedTrackable.Value);
                bool good = img != null && img.trackingState == TrackingState.Tracking;
                if (!good)
                {
                    _lostTimer += Time.deltaTime;
                    if (_lostTimer >= lostReleaseSeconds)
                    {
                        Log("Lost tracking long enough, releasing lock.");
                        _lockedTrackable = null;
                        _lostTimer = 0f;
                        if (_model && hideWhenNotTracking) _model.SetActive(false);
                    }
                }
                else
                {
                    _lostTimer = 0f;
                }
            }
        }

        private ARTrackedImage FindTrackedImageById(TrackableId id)
        {
            foreach (var img in trackedImageManager.trackables)
                if (img.trackableId == id) return img;
            return null;
        }

        /// <summary>
        /// Forsøg at attache modellen til det aktuelt trackede referencebillede (hvis i view).
        /// Kaldes bl.a. når modellen er blevet loadet, eller når brugeren kommer tilbage på billedet.
        /// </summary>
        public void TryAttachToCurrentlyTracked()
        {
            var targetName = runtimeLib != null ? runtimeLib.CurrentRefName : null;
            Log("TryAttachToCurrentlyTracked name = " + (targetName ?? "<null>"));

            if (string.IsNullOrEmpty(targetName) || trackedImageManager == null) return;

            foreach (var img in trackedImageManager.trackables)
            {
                Log($"Seen {img.referenceImage.name} state={img.trackingState} size={img.size}");
                if (img.referenceImage.name == targetName && img.trackingState == TrackingState.Tracking)
                {
                    AttachTo(img);
                    break;
                }
            }
        }

        private void OnTrackedImagesChanged(ARTrackedImagesChangedEventArgs args)
        {
            var targetName = runtimeLib != null ? runtimeLib.CurrentRefName : null;
            if (string.IsNullOrEmpty(targetName))
            {
                Log("trackedImagesChanged but CurrentRefName is null/empty.");
                return;
            }

            foreach (var img in args.added) Consider(img, targetName, "added");
            foreach (var img in args.updated) Consider(img, targetName, "updated");

            foreach (var img in args.removed)
            {
                if (_lockedTrackable.HasValue && img.trackableId == _lockedTrackable.Value)
                {
                    Log("Removed locked image → hide & unlock");
                    if (_model) _model.SetActive(false);
                    _lockedTrackable = null;
                    _lostTimer = 0f;
                }
            }
        }

        private void Consider(ARTrackedImage img, string targetName, string phase)
        {
            if (img.referenceImage.name != targetName) return;

            Log($"{phase} {img.referenceImage.name} state={img.trackingState} size={img.size}");

            if (img.trackingState != TrackingState.Tracking) return;

            if (lockToFirstSeen && _lockedTrackable.HasValue && img.trackableId != _lockedTrackable.Value)
            {
                // Allerede låst til en anden instans.
                return;
            }

            AttachTo(img);
        }

        private void AttachTo(ARTrackedImage img)
        {
            if (_model == null)
            {
                _model = generateController != null ? generateController.GetLoadedModel() : null;
                if (_model == null)
                {
                    LogWarning("No model yet to attach.");
                    return;
                }
            }

            // Beregn bounds én gang pr. session (når vi får modellen første gang)
            if (!_haveBounds)
            {
                _modelBounds = CalculateBoundsSafe(_model);
                _haveBounds = _modelBounds.size.sqrMagnitude > 0f;
                Log($"Model bounds = {_modelBounds.size}");
            }

            // Parent til det trackede billede, så pos/rot følger billedet
            _model.transform.SetParent(img.transform, worldPositionStays: false);
            _model.transform.localPosition = Vector3.zero;
            _model.transform.localRotation = Quaternion.identity;

            // Skaler så bredden matcher det trackede billedes bredde i meter
            if (_haveBounds && matchTrackedWidth)
            {
                float imageWidth = Mathf.Max(1e-4f, img.size.x); // meter
                float modelWidth = Mathf.Max(1e-4f, _modelBounds.size.x);
                float scale = (imageWidth / modelWidth) * Mathf.Max(1e-4f, widthScaleMultiplier);
                _model.transform.localScale = Vector3.one * scale;
            }
            else
            {
                // Sikr at vi ikke har nulskala
                var s = _model.transform.localScale;
                if (Mathf.Abs(s.x) < 1e-4f) _model.transform.localScale = Vector3.one * 0.1f;
            }

            // Offsets (fine-tuning)
            _model.transform.localPosition += localPositionOffset;
            _model.transform.localRotation *= Quaternion.Euler(localEulerOffset);

            // Vis ALTID ved attach (for at undgå usynlig model)
            _model.SetActive(true);

            if (lockToFirstSeen)
            {
                _lockedTrackable = img.trackableId;
                _lostTimer = 0f;
            }

            Log("Attached model to tracked image.");
        }

        // --- Helpers ---------------------------------------------------------

        private static Bounds CalculateBoundsSafe(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);
            if (renderers == null || renderers.Length == 0)
            {
                // Fallback – hvis ingen renderer (fx tomt root), giv en lille størrelse
                return new Bounds(Vector3.zero, Vector3.one * 0.001f);
            }

            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                b.Encapsulate(renderers[i].bounds);

            // Konverter world-bounds til "lokal" vurdering af størrelsen ift. nuværende skala
            float uniformScale = Mathf.Abs(go.transform.lossyScale.x) < 1e-6f ? 1f : go.transform.lossyScale.x;
            if (uniformScale <= 1e-6f) uniformScale = 1f;

            var sizeLocalApprox = b.size / uniformScale;
            if (sizeLocalApprox.sqrMagnitude <= 1e-10f)
                sizeLocalApprox = Vector3.one * 0.001f;

            return new Bounds(Vector3.zero, sizeLocalApprox);
        }

        private void Log(string msg)
        {
            if (verboseLogs) Debug.Log($"[Overlay] {msg}");
        }

        private void LogWarning(string msg)
        {
            if (verboseLogs) Debug.LogWarning($"[Overlay] {msg}");
        }

        private void LogError(string msg)
        {
            Debug.LogError($"[Overlay] {msg}");
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!drawAttachGizmos || _model == null) return;

            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(_model.transform.position, Vector3.one * 0.02f);
            Gizmos.DrawLine(_model.transform.position, _model.transform.position + _model.transform.right * 0.05f);
            Gizmos.DrawLine(_model.transform.position, _model.transform.position + _model.transform.up * 0.05f);
            Gizmos.DrawLine(_model.transform.position, _model.transform.position + _model.transform.forward * 0.05f);
        }
#endif
    }
}
