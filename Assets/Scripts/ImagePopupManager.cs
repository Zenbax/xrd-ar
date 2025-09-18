using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class ImagePopupManager : MonoBehaviour
{
    [Header("What to spawn above each tracked picture")]
    public GameObject popupPrefab;

    [Header("Placement")]
    [Tooltip("Meters above the center of the image")]
    public float heightOffset = 0.15f;

    ARTrackedImageManager _imgManager;
    Camera _arCam;

    // Track one spawned popup per reference image (by GUID)
    readonly Dictionary<TrackableId, GameObject> _spawned = new();

    void Awake()
    {
        _imgManager = GetComponent<ARTrackedImageManager>();
        _arCam = Camera.main;
    }

    void OnEnable()  => _imgManager.trackedImagesChanged += OnChanged;
    void OnDisable() => _imgManager.trackedImagesChanged -= OnChanged;

    void OnChanged(ARTrackedImagesChangedEventArgs args)
    {
        // New detections
        foreach (var img in args.added)
            CreateOrUpdate(img);

        // Updated pose/state
        foreach (var img in args.updated)
            CreateOrUpdate(img);

        // Lost tracking
        foreach (var img in args.removed)
        {
            if (_spawned.TryGetValue(img.trackableId, out var go))
            {
                Destroy(go);
                _spawned.Remove(img.trackableId);
            }
        }
    }

    void CreateOrUpdate(ARTrackedImage img)
    {
        // Spawn if needed
        if (!_spawned.TryGetValue(img.trackableId, out var go))
        {
            if (popupPrefab == null) return;
            go = Instantiate(popupPrefab, transform);
            _spawned[img.trackableId] = go;
        }

        // Show/hide based on tracking state
        bool visible = img.trackingState == TrackingState.Tracking;
        go.SetActive(visible);
        if (!visible) return;

        // Position above the image center
        var pose = img.transform;
        Vector3 up = pose.up; // image's normal
        go.transform.position = pose.position + up * heightOffset;

        // Keep it upright relative to the image, then face the camera (billboard)
        go.transform.rotation = Quaternion.LookRotation(
            (_arCam.transform.position - go.transform.position).normalized,
            up
        );

        // Optional: scale relative to image size
        // var size = img.size; // width, height (meters)
        // go.transform.localScale = Vector3.one * (size.x * 0.25f);
    }
}
