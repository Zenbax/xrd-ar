using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

[RequireComponent(typeof(ARTrackedImageManager))]
public class TrackedImagePrefabSpawner : MonoBehaviour
{
    private ARTrackedImageManager trackedImageManager;

    [System.Serializable]
    public struct ImagePrefab
    {
        public string imageName;
        public GameObject prefab;
    }

    public List<ImagePrefab> imagePrefabs;
    private Dictionary<string, GameObject> spawnedPrefabs = new Dictionary<string, GameObject>();

    void Awake()
    {
        trackedImageManager = GetComponent<ARTrackedImageManager>();
    }

    void OnEnable()
    {
        trackedImageManager.trackedImagesChanged += OnTrackedImagesChanged;
    }

    void OnDisable()
    {
        trackedImageManager.trackedImagesChanged -= OnTrackedImagesChanged;
    }

    private void OnTrackedImagesChanged(ARTrackedImagesChangedEventArgs args)
    {
        foreach (var trackedImage in args.added)
        {
            SpawnPrefab(trackedImage);
        }

        foreach (var trackedImage in args.updated)
        {
            UpdatePrefab(trackedImage);
        }

        foreach (var trackedImage in args.removed)
        {
            RemovePrefab(trackedImage);
        }
    }

    private void SpawnPrefab(ARTrackedImage trackedImage)
    {
        foreach (var item in imagePrefabs)
        {
            if (trackedImage.referenceImage.name == item.imageName)
            {
                GameObject prefab = Instantiate(item.prefab, trackedImage.transform.position, trackedImage.transform.rotation);
                spawnedPrefabs[trackedImage.referenceImage.name] = prefab;
            }
        }
    }

    private void UpdatePrefab(ARTrackedImage trackedImage)
    {
        if (spawnedPrefabs.TryGetValue(trackedImage.referenceImage.name, out var prefab))
        {
            prefab.transform.position = trackedImage.transform.position;
            prefab.transform.rotation = trackedImage.transform.rotation;
            prefab.SetActive(trackedImage.trackingState == TrackingState.Tracking);
        }
    }

    private void RemovePrefab(ARTrackedImage trackedImage)
    {
        if (spawnedPrefabs.TryGetValue(trackedImage.referenceImage.name, out var prefab))
        {
            Destroy(prefab);
            spawnedPrefabs.Remove(trackedImage.referenceImage.name);
        }
    }
}
