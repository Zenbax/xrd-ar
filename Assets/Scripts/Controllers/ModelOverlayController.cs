using System;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using ARMeshyDemo.Controllers;
using Debug = UnityEngine.Debug;

namespace ARMeshyDemo.AR
{
    public class ModelOverlayController : MonoBehaviour
    {
        [Header("Refs")]
        [SerializeField] private ARTrackedImageManager trackedImageManager;
        [SerializeField] private RuntimeImageLibraryController runtimeLib;
        [SerializeField] private GenerateController generateController;

        [Header("Placement")]
        [Tooltip("Match model width to tracked image width (in meters).")]
        [SerializeField] private bool matchTrackedWidth = true;

        [Tooltip("Extra multiplier on top of width-matching scale.")]
        [SerializeField] private float widthScaleMultiplier = 1f;

        [Tooltip("Offset from tracked image origin (local space). Y > 0 lifts the model up off the card.")]
        [SerializeField] private Vector3 localPositionOffset = new Vector3(0f, 0.02f, 0f);

        [Tooltip("Extra rotation in local space. Use this to make the model stand up and face the camera.")]
        [SerializeField] private Vector3 localEulerOffset = new Vector3(90f, 0f, 0f);

        [SerializeField] private bool hideWhenNotTracking = true;

        [Header("Locking")]
        [Tooltip("Lock to the first tracked instance so the model does not jump between identical images.")]
        [SerializeField] private bool lockToFirstSeen = true;

        [SerializeField] private float lostReleaseSeconds = 1.0f;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;

        [Tooltip("If true, try to attach immediately when the model is loaded, even if the tracked event came earlier.")]
        [SerializeField] private bool tryAttachImmediatelyOnModelLoaded = true;

        [Tooltip("Draw gizmos at the attach point (Editor only).")]
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
            // Grab model reference once it exists
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

            // Handle lock + lost tracking timer
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
        /// Try to attach the model to the currently tracked reference image (if any).
        /// Called when the model finishes loading, or when we come back to the image.
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
                // Already locked to a different instance
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

            // Compute bounds once per session
            if (!_haveBounds)
            {
                _modelBounds = CalculateBoundsSafe(_model);
                _haveBounds = _modelBounds.size.sqrMagnitude > 0f;
                Log($"Model bounds = {_modelBounds.size}");
            }

            // Parent to the tracked image so pos/rot follow it
            _model.transform.SetParent(img.transform, worldPositionStays: false);

            // Reset local transform before applying our offsets
            _model.transform.localPosition = Vector3.zero;
            _model.transform.localRotation = Quaternion.identity;
            _model.transform.localScale = Vector3.one;

            // Width-based scaling (optional)
            if (_haveBounds && matchTrackedWidth)
            {
                float imageWidth = Mathf.Max(1e-4f, img.size.x);       // meters
                float modelWidth = Mathf.Max(1e-4f, _modelBounds.size.x);

                float scale = (imageWidth / modelWidth) * Mathf.Max(1e-4f, widthScaleMultiplier);
                _model.transform.localScale = Vector3.one * scale;
            }
            else
            {
                // Ensure non-zero scale
                var s = _model.transform.localScale;
                if (Mathf.Abs(s.x) < 1e-4f) _model.transform.localScale = Vector3.one * 0.1f;
            }

            // Apply position + rotation offsets
            _model.transform.localPosition += localPositionOffset;
            _model.transform.localRotation *= Quaternion.Euler(localEulerOffset);

            // Always show when attached
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
                // Fallback – tiny bounds if no renderer
                return new Bounds(Vector3.zero, Vector3.one * 0.001f);
            }

            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                b.Encapsulate(renderers[i].bounds);

            // Convert world-bounds to an approximate local size (assuming uniform scale)
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
