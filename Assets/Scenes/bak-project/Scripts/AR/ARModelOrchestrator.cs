using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using Siccity.GLTFUtility;
using Scenes.bak_project.Scripts.AR;

public class ARModelOrchestrator : MonoBehaviour
{
    [Header("Scene Refs")]
    public ARImageRuntimeManager arImageRuntime;
    public ARTrackedImageManager trackedImageManager;
    public MeshyClient2 meshyClient2;
    public GameObject modelRoot;
    public GameObject loadingSpinnerPrefab;

    [Header("UI")]
    public Button generateModelButton;
    public GameObject redoPanel;
    public Button redoMeshButton;
    public Button redoTextureButton;
    public Button redoAnimationButton;

    [Header("Meshy Defaults")]
    public string aiModel = "latest";      // "meshy-4", "meshy-5", or "latest" (Meshy 6 Preview)
    public string topology = "triangle";   // "triangle" | "quad"
    public int targetPolycount = 30000;    // 100..300000
    public string symmetryMode = "auto";   // "off" | "auto" | "on"
    public bool shouldRemesh = true;
    public bool shouldTexture = true;
    public bool enablePbr = true;
    public bool isATPose = false;
    public bool moderation = false;
    [TextArea] public string defaultTexturePrompt = "realistic fox fur, PBR";

    private ModelRegistry registry;
    private GameObject currentSpinner;
    private string currentImageHash;

    void Awake()
    {
        registry = new ModelRegistry();
    }

    void Start()
    {
        if (!arImageRuntime || !trackedImageManager || !meshyClient2)
        {
            Debug.LogError("Assign ARImageRuntimeManager, ARTrackedImageManager, MeshyClient2 in Inspector.");
            enabled = false;
            return;
        }

        generateModelButton.onClick.AddListener(OnGenerateClicked);
        redoMeshButton.onClick.AddListener(() => Redo(WhatToRedo.Mesh));
        redoTextureButton.onClick.AddListener(() => Redo(WhatToRedo.Texture));
        redoAnimationButton.onClick.AddListener(() => Redo(WhatToRedo.Animation));

        trackedImageManager.trackedImagesChanged += OnTrackedImagesChanged;
        SetModeGenerate();
    }

    void OnDestroy()
    {
        if (trackedImageManager != null)
            trackedImageManager.trackedImagesChanged -= OnTrackedImagesChanged;
    }

    private void SetModeGenerate()
    {
        if (generateModelButton) generateModelButton.gameObject.SetActive(true);
        if (redoPanel) redoPanel.SetActive(false);
    }

    private void SetModeRedo()
    {
        if (generateModelButton) generateModelButton.gameObject.SetActive(false);
        if (redoPanel) redoPanel.SetActive(true);
    }

    private void OnTrackedImagesChanged(ARTrackedImagesChangedEventArgs evt)
    {
        foreach (var img in evt.added) TryPlaceIfKnown(img);
        foreach (var img in evt.updated) TryPlaceIfKnown(img);
    }

    private void TryPlaceIfKnown(ARTrackedImage img)
    {
        if (img.trackingState != UnityEngine.XR.ARSubsystems.TrackingState.Tracking) return;

        var camTex = arImageRuntime.CaptureCameraFrame();
        var hash = ImageHash.AverageHash(camTex);
        var entry = registry.GetByHash(hash);
        if (entry != null && File.Exists(entry.localGlbPath))
        {
            StartCoroutine(LoadAndAttachModel(entry.localGlbPath, img.transform));
            SetModeRedo();
        }
        else
        {
            SetModeGenerate();
        }
    }

    private void OnGenerateClicked()
    {
        var tex = arImageRuntime.CaptureCameraFrame();
        currentImageHash = ImageHash.AverageHash(tex);
        var png = tex.EncodeToPNG();

        arImageRuntime.AddCapturedAsReferenceImage(
            onAdded: () =>
            {
                StartCoroutine(RunMeshyFlow_CreateAndPlace(
                    png,
                    // use defaults on first create
                    aiModel, topology, targetPolycount, symmetryMode,
                    shouldRemesh, shouldTexture, enablePbr, isATPose,
                    defaultTexturePrompt, null, // texture_image_url: null
                    moderation
                ));
            },
            onError: (err) => Debug.LogWarning("AddRef error: " + err)
        );
    }

    private IEnumerator RunMeshyFlow_CreateAndPlace(
        byte[] png,
        string _aiModel, string _topology, int _targetPoly, string _symmetry,
        bool _shouldRemesh, bool _shouldTexture, bool _enablePbr, bool _isATPose,
        string _texturePrompt, string _textureImageUrl,
        bool _moderation
    )
    {
        ShowSpinner(true);

        string taskId = null;
        yield return meshyClient2.CreateImageTo3DTask(
            png,
            onTaskCreated: id => taskId = id,
            onError: err => { Debug.LogError(err); taskId = null; },
            ai_model: _aiModel,
            topology: _topology,
            target_polycount: _targetPoly,
            symmetry_mode: _symmetry,
            should_remesh: _shouldRemesh,
            should_texture: _shouldTexture,
            enable_pbr: _enablePbr,
            is_a_t_pose: _isATPose,
            texture_prompt: _texturePrompt,
            texture_image_url: _textureImageUrl,
            moderation: _moderation
        );

        if (string.IsNullOrEmpty(taskId))
        {
            ShowSpinner(false);
            yield break;
        }

        string glbUrl = null;
        yield return meshyClient2.PollTaskUntilReady(
            taskId,
            onGlbUrl: url => glbUrl = url,
            onError: err => { Debug.LogError(err); glbUrl = null; }
        );

        if (string.IsNullOrEmpty(glbUrl))
        {
            ShowSpinner(false);
            yield break;
        }

        byte[] glbBytes = null;
        yield return meshyClient2.DownloadBytes(glbUrl, bytes => glbBytes = bytes, err => Debug.LogError(err));
        if (glbBytes == null)
        {
            ShowSpinner(false);
            yield break;
        }

        var localPath = Path.Combine(Application.persistentDataPath, $"meshy_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.glb");
        File.WriteAllBytes(localPath, glbBytes);

        var entry = new ModelEntry { imageHash = currentImageHash, localGlbPath = localPath, arGuid = Guid.NewGuid().ToString() };
        registry.AddOrUpdate(entry);

        foreach (var img in trackedImageManager.trackables)
        {
            if (img.trackingState == UnityEngine.XR.ARSubsystems.TrackingState.Tracking)
            {
                yield return LoadAndAttachModel(localPath, img.transform);
                break;
            }
        }

        ShowSpinner(false);
        SetModeRedo();
    }

    private IEnumerator LoadAndAttachModel(string glbPath, Transform anchor)
    {
        ShowSpinner(true);

        // ✅ GLTFUtility: Load on MAIN THREAD (required for GraphicsSettings access)
        // Background threading causes material/shader errors
        GameObject loadedObject = null;
        Exception loadException = null;

        try
        {
            // This blocks the main thread but is safe and required by GLTFUtility
            loadedObject = Importer.LoadFromFile(glbPath);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ARModelOrchestrator] Failed to load GLB: {ex}");
            loadException = ex;
        }

        // ✅ Wait for textures to finish loading
        yield return null;
        yield return null;
        yield return new WaitForSeconds(0.5f);

        if (loadException != null || loadedObject == null)
        {
            Debug.LogError($"[ARModelOrchestrator] Load failed: {loadException?.Message ?? "Unknown error"}");
            ShowSpinner(false);
            yield break;
        }

        // Parent to anchor
        loadedObject.transform.SetParent(anchor, false);
        loadedObject.transform.localPosition = Vector3.zero;
        loadedObject.transform.localRotation = Quaternion.identity;
        loadedObject.transform.localScale = Vector3.one * 0.1f;

        Debug.Log($"[ARModelOrchestrator] Successfully loaded and attached model");
        ShowSpinner(false);
    }

    private enum WhatToRedo { Mesh, Texture, Animation }

    private void Redo(WhatToRedo what)
    {
        var tex = arImageRuntime.CaptureCameraFrame();
        var hash = ImageHash.AverageHash(tex);
        var entry = registry.GetByHash(hash);
        if (entry == null)
        {
            Debug.Log("No existing model mapping — generating fresh.");
            OnGenerateClicked();
            return;
        }

        // Adjust Meshy parameters based on what to redo:
        string _aiModel = aiModel;
        string _topology = topology;
        int _targetPoly = targetPolycount;
        string _symmetry = symmetryMode;
        bool _shouldRemesh = shouldRemesh;
        bool _shouldTexture = shouldTexture;
        bool _enablePbr = enablePbr;
        bool _isATPose = isATPose;
        string _texturePrompt = defaultTexturePrompt;
        string _textureImageUrl = null;
        bool _moderation = moderation;

        switch (what)
        {
            case WhatToRedo.Mesh:
                _shouldRemesh = true;            // force remesh
                // keep texture true so final has textures; turn off if you want mesh-only:
                // _shouldTexture = false;
                break;

            case WhatToRedo.Texture:
                _shouldTexture = true;
                _enablePbr = true;
                // you can tweak _texturePrompt here or show a UI to edit it
                break;

            case WhatToRedo.Animation:
                // Meshy Image-to-3D doesn’t expose animation options directly in the docs you pasted.
                // You can request A/T-pose for easier rigging:
                _isATPose = true;
                // Otherwise this will regenerate the model in A/T pose for easier external animation/retarget.
                break;
        }

        var png = tex.EncodeToPNG();
        StartCoroutine(RunMeshyFlow_RedoAndReplace(
            png, hash,
            _aiModel, _topology, _targetPoly, _symmetry,
            _shouldRemesh, _shouldTexture, _enablePbr, _isATPose,
            _texturePrompt, _textureImageUrl,
            _moderation
        ));
    }

    private IEnumerator RunMeshyFlow_RedoAndReplace(
        byte[] png, string imageHash,
        string _aiModel, string _topology, int _targetPoly, string _symmetry,
        bool _shouldRemesh, bool _shouldTexture, bool _enablePbr, bool _isATPose,
        string _texturePrompt, string _textureImageUrl,
        bool _moderation
    )
    {
        ShowSpinner(true);

        string taskId = null;
        yield return meshyClient2.CreateImageTo3DTask(
            png,
            onTaskCreated: id => taskId = id,
            onError: err => { Debug.LogError(err); taskId = null; },
            ai_model: _aiModel,
            topology: _topology,
            target_polycount: _targetPoly,
            symmetry_mode: _symmetry,
            should_remesh: _shouldRemesh,
            should_texture: _shouldTexture,
            enable_pbr: _enablePbr,
            is_a_t_pose: _isATPose,
            texture_prompt: _texturePrompt,
            texture_image_url: _textureImageUrl,
            moderation: _moderation
        );

        if (string.IsNullOrEmpty(taskId))
        {
            ShowSpinner(false);
            yield break;
        }

        string glbUrl = null;
        yield return meshyClient2.PollTaskUntilReady(
            taskId,
            onGlbUrl: url => glbUrl = url,
            onError: err => { Debug.LogError(err); glbUrl = null; }
        );

        if (string.IsNullOrEmpty(glbUrl))
        {
            ShowSpinner(false);
            yield break;
        }

        byte[] glbBytes = null;
        yield return meshyClient2.DownloadBytes(glbUrl, bytes => glbBytes = bytes, err => Debug.LogError(err));
        if (glbBytes == null)
        {
            ShowSpinner(false);
            yield break;
        }

        var localPath = Path.Combine(Application.persistentDataPath, $"meshy_redo_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.glb");
        File.WriteAllBytes(localPath, glbBytes);

        var entry = registry.GetByHash(imageHash) ?? new ModelEntry { imageHash = imageHash, arGuid = Guid.NewGuid().ToString() };
        entry.localGlbPath = localPath;
        registry.AddOrUpdate(entry);

        foreach (var img in trackedImageManager.trackables)
        {
            if (img.trackingState == UnityEngine.XR.ARSubsystems.TrackingState.Tracking)
            {
                yield return LoadAndAttachModel(localPath, img.transform);
                break;
            }
        }

        ShowSpinner(false);
        SetModeRedo();
    }

    private void ShowSpinner(bool show)
    {
        if (show && currentSpinner == null && loadingSpinnerPrefab != null)
        {
            currentSpinner = Instantiate(loadingSpinnerPrefab, modelRoot != null ? modelRoot.transform : null);
        }
        else if (!show && currentSpinner != null)
        {
            Destroy(currentSpinner);
            currentSpinner = null;
        }
    }
}