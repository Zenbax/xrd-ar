# Understanding "Textures: 3, Images: 0" Issue

## What the Numbers Mean

In glTFast (the GLB loader library):

- **Images** = Raw image data (PNG/JPEG bytes) loaded from the GLB file
- **Textures** = Unity `Texture2D` objects created from those images
- **Materials** = Unity `Material` objects that reference the textures

## The Problem

```
Materials: 1, Textures: 3, Images: 0
```

This means:
- ? glTFast found 3 texture **references** in the GLB
- ? But loaded 0 actual image **data**
- ? So the textures are "empty shells" with no image content

## Possible Causes

### 1. **External Texture References** (Most Likely)
The GLB might reference textures via external URLs instead of embedding them:
```json
{
  "images": [
    { "uri": "https://example.com/texture.png" }  ? External!
  ]
}
```

Instead of:
```json
{
  "images": [
    { "bufferView": 0 }  ? Embedded in GLB
  ]
}
```

### 2. **Meshy API Format Issue**
Meshy might be:
- Generating GLBs with external texture URLs
- Requiring a specific `model_formats` parameter
- Needing the `texture_richness` parameter set

### 3. **glTFast Loading Failure**
The library might be:
- Silently failing to download external textures
- Missing the base URI to resolve relative paths
- Timing out on texture downloads

## New Diagnostics Added

The updated code now provides detailed logging:

### 1. **Texture Issue Warning**
```
[GLTF-LOADER] ?? TEXTURE ISSUE: Textures defined but Images=0!
[GLTF-LOADER] This suggests textures are referenced externally or failed to load.
[GLTF-LOADER] Base URI: https://assets.meshy.ai/.../
```

### 2. **Post-Instantiation Check**
```
[GLTF-LOADER] --- POST-INSTANTIATION CHECK ---
[GLTF-LOADER] Renderers found: 1
[GLTF-LOADER]   Renderer: Node-0, Materials: 1
[GLTF-LOADER]     Material: Standard (Instance), Shader: Standard
[GLTF-LOADER]       mainTexture: NULL  ? Problem!
```

Or if working:
```
[GLTF-LOADER]       mainTexture: texture_0 (1024x1024)  ? Good!
```

### 3. **Import Settings**
The code now explicitly configures:
```csharp
var importSettings = new ImportSettings
{
    GenerateMipMaps = true,           // Create mipmaps
    AnisotropicFilterLevel = 3,       // Better filtering
    NodeNameMethod = NameImportMethod.OriginalUnique
};
```

## Next Steps to Diagnose

### Run the App and Check Logs For:

**1. External Texture Warning:**
```
?? TEXTURE ISSUE: Textures defined but Images=0!
Base URI: <what does this show?>
```

**2. Material Texture Status:**
```
mainTexture: NULL  ? Textures failed to load
```
vs
```
mainTexture: texture_0 (1024x1024)  ? Textures loaded!
```

**3. Material Property Dump:**
```
[MAT-TEX]   baseColorTexture: name=texture_0 size=1024x1024
```
(This will show if glTFast DID load them but under different property names)

## Solutions Based on Findings

### If Base URI is NULL:
The issue is we're not providing a base path for external textures. The fix is already in place (`TryGetDirectoryBaseUri`), but check if it's working.

### If Textures Are External:
Contact Meshy support or:
1. Try requesting `fbx` format instead (may have embedded textures)
2. Check if there's a `texture_mode` parameter in the API
3. Download textures separately and apply manually

### If glTFast Isn't Loading Embedded Textures:
This would be a bug in glTFast. Solutions:
1. Update glTFast package to latest version
2. Try alternative GLB loaders (TriLib 2, UnityGLTF)
3. Manually extract textures using glTF binary specification

## Test With a Known-Good GLB

Download a sample GLB with embedded textures:
```
https://github.com/KhronosGroup/glTF-Sample-Models/tree/master/2.0/Duck/glTF-Binary
```

Load it with the same code. If it works, the issue is Meshy's GLB format.

## Expected Output After Fix

When working correctly, you should see:
```
[GLTF-LOADER] Materials: 1, Textures: 3, Images: 3  ? All 3 images loaded!
[GLTF-LOADER] --- POST-INSTANTIATION CHECK ---
[GLTF-LOADER]   Renderer: Node-0, Materials: 1
[GLTF-LOADER]     Material: StandardMaterial, Shader: glTFast/pbrMetallicRoughness
[GLTF-LOADER]       mainTexture: albedo (2048x2048)
[MAT-TEX]   baseColorTexture: name=albedo size=2048x2048
[MAT-TEX]   normalTexture: name=normal size=2048x2048
[MAT-TEX]   metallicRoughnessTexture: name=metallic size=2048x2048
```

Then the model should render with proper textures!
