# Texture Loading Improvements for Meshy GLB Models

## Problem Analysis

Based on your logs, the issue was:
```
Materials: 1, Textures: 3, Images: 0
baseTex=False
```

The GLB from Meshy **DID contain 3 textures**, but they weren't being detected/assigned to materials because:

1. **Timing Issue**: Textures weren't fully loaded before we checked for them
2. **Property Name Mismatch**: glTFast uses different texture property names than URP/Standard shaders
3. **Premature Shader Remapping**: The system was converting glTFast materials to URP shaders before textures were loaded

## Changes Made

### 1. **Assets/Scripts/Net/GltfLoader.cs** - Improved Texture Loading

#### Added Longer Wait Times
```csharp
// Wait multiple frames after import
yield return null;
yield return null;
yield return new WaitForSeconds(0.1f); // Extra time for textures

// Wait again after instantiation
yield return null;
yield return null;
```

#### Added glTFast Property Names
The glTFast library uses these property names (case-sensitive):
- `baseColorTexture` (not `_BaseMap`)
- `normalTexture` (not `_BumpMap`)
- `metallicRoughnessTexture` (not `_MetallicGlossMap`)
- `occlusionTexture` (not `_OcclusionMap`)
- `emissiveTexture` (not `_EmissionMap`)

#### Added `HasAnyTexture()` Helper
```csharp
private bool HasAnyTexture(Material m)
{
    // Checks both glTFast and URP/Standard property names
    string[] texProps = {
        "baseColorTexture", "_baseColorTexture",
        "normalTexture", "_normalTexture",
        // ... etc
    };
}
```

#### Improved Auto-Remap Logic
```csharp
// DON'T remap if glTFast shader is supported AND has textures
if (name.IndexOf("glTF", StringComparison.OrdinalIgnoreCase) >= 0 && sh.isSupported)
{
    bool hasTextures = HasAnyTexture(m);
    if (hasTextures)
    {
        continue; // Keep glTFast shader with textures
    }
}
```

### 2. **Assets/Scripts/Net/MeshyClient.cs** - Request All Formats

#### Changed Default Behavior
```csharp
[SerializeField] private bool requestAllFormats = true; // Was: requestOnlyGlb = true
```

Now Meshy will generate GLB, FBX, OBJ, and USDZ formats. This gives more options if GLB textures still fail.

#### Better Logging
```csharp
Debug.Log($"[MeshyClient] Requesting formats: {(requestAllFormats ? "ALL formats" : "GLB only")}");
```

### 3. **Assets/Scripts/Net/ModelLoader.cs** - New Universal Loader (Created)

Created a format-agnostic loader that can:
- Detect format from URL
- Delegate to GltfLoader for GLB
- Show clear error for FBX/OBJ (requires third-party plugins)

**Note**: Unity doesn't support runtime FBX loading without paid assets like TriLib 2.

### 4. **Assets/Scripts/Controllers/GenerateController.cs** - Bug Fix

Fixed button event listener cleanup:
```csharp
ui.CancelButton.onClick.RemoveListener(...) // Was missing .onClick
```

## Testing Recommendations

### 1. Test with Current Changes (GLB with Better Detection)
- The texture detection should now work correctly
- You should see logs like:
  ```
  [MAT-DETAIL] Found textures: base=True, normal=True, metallic=True
  [AUTO-REMAP] Keeping supported glTFast shader with textures: glTFast/pbrMetallicRoughness
  ```

### 2. If Textures Still Don't Show

Check the Pre-Remap dump for texture property names:
```
[MAT-TEX]   baseColorTexture: name=... size=1024x1024
```

If textures exist but still don't render:
1. Make sure URP is properly configured
2. Check if glTFast shaders are included in build (Graphics Settings ? Always Included Shaders)
3. Try enabling `remapMaterials = true` in GltfLoader inspector

### 3. Alternative: Use FBX Format

To use FBX instead of GLB, you would need:

1. **Install a runtime FBX loader** like:
   - TriLib 2 (Unity Asset Store - $95)
   - Assimp for Unity

2. **Modify GenerateController.cs**:
   ```csharp
   onDone: task => { 
       // Prefer FBX over GLB
       if (!string.IsNullOrEmpty(task?.model_urls?.fbx))
           _lastGlbUrl = task.model_urls.fbx;
       else
           _lastGlbUrl = task.model_urls.glb;
   }
   ```

3. **Update GltfLoader or create FbxLoader** to handle FBX format

## What to Look For in Logs

### ? Good - Textures Loaded Successfully
```
[GLTF-LOADER] Materials: 1, Textures: 3, Images: 3
[MAT-DETAIL] Found textures: base=True, normal=True, metallic=True
[AUTO-REMAP] Keeping supported glTFast shader with textures
[MAT-TEX]   baseColorTexture: name=texture_0 size=1024x1024
```

### ? Bad - Still No Textures
```
[GLTF-LOADER] Materials: 1, Textures: 0, Images: 0
[MAT-DETAIL] Found textures: base=False, normal=False, metallic=False
[AUTO-REMAP] Triggered: detected pink/unsupported shaders
```

## Configuration Changes

In Unity Inspector, you can now configure:

### GltfLoader Component:
- `Verbose Texture Debug` - See all texture properties
- `Remap Materials` - Force conversion to URP shaders (useful if glTFast shaders don't render)
- `Auto Remap On Pink` - Automatically fix pink materials

### MeshyClient Component:
- `Request All Formats` (NEW) - Get GLB + FBX + OBJ + USDZ from Meshy

## Summary

The main improvements are:
1. ? **Longer wait times** for texture loading
2. ? **glTFast property name detection** (baseColorTexture, normalTexture, etc.)
3. ? **Don't remap materials** that already have textures
4. ? **Request all formats** from Meshy for flexibility
5. ? **Better debugging** to see exactly what's happening

The issue was **timing + property names**, not that GLB lacks textures. The changes should fix texture loading from Meshy's GLB files.
