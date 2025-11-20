# "Shader Parameter is Null" Error - Fixed ?

## ?? Problem
The error "shader parameter is null" was occurring during material remapping in `GltfLoader.cs`, specifically when trying to create new `Material` objects or assign shaders.

## ?? Where It Happened
The error occurred in these locations:
```csharp
// Line ~331: Creating new material for empty renderer
r.materials = new[] { new Material(targetLit) };  // ? Could crash if targetLit is null

// Line ~344: Replacing null material
mats[i] = new Material(targetLit);  // ? Could crash if targetLit is null

// Line ~360: Assigning shader
m.shader = desired;  // ? Could crash if desired is null
```

## ? Solution Applied

Added **comprehensive null checks and try-catch blocks** to handle shader lookup failures gracefully:

### 1. **Early Validation**
```csharp
if (!targetLit)
{
    LogError("Failed to find any Lit shader! Skipping material remap.");
    return;  // ? Exit early if no shader found
}
```

### 2. **Safe Material Creation**
```csharp
try
{
    if (targetLit != null)
    {
        r.materials = new[] { new Material(targetLit) };
    }
    else
    {
        LogError($"Cannot create material: targetLit shader is null");
    }
}
catch (System.Exception ex)
{
    LogError($"Failed to create material: {ex.Message}");
}
```

### 3. **Safe Shader Assignment**
```csharp
if (desired == null)
{
    LogError($"Desired shader is null for material {m.name}");
    continue;  // ? Skip this material instead of crashing
}

try
{
    m.shader = desired;
    ApplyTexturesToShader(m, textures, desired == targetLit);
}
catch (System.Exception ex)
{
    LogError($"Failed to remap shader: {ex.Message}");
}
```

---

## ?? Why This Error Occurred

### **Root Causes:**

1. **URP Shader Not Found**
   - `Shader.Find("Universal Render Pipeline/Simple Lit")` returned `null`
   - Happens when URP is not properly configured
   - Or when shaders aren't included in build

2. **Graphics Settings Access Failed**
   - `GraphicsSettings.currentRenderPipeline` may be `null` on first access
   - Timing issue during initialization

3. **Shader Not Included in Build**
   - URP shaders not in "Always Included Shaders" list
   - Shader stripping removed them during build

---

## ?? How to Verify the Fix

### **1. Check Console for Shader Detection**
```
[GLTF-UTILITY] RP Runtime: UniversalRenderPipelineAsset | URP=True
[GLTF-UTILITY] [REMAP] TargetLit=Universal Render Pipeline/Simple Lit
```

If you see:
```
[GLTF-UTILITY] [ERROR] Failed to find any Lit shader! Skipping material remap.
```

Then URP is not properly configured!

### **2. Verify URP is Configured**
1. Open `Edit ? Project Settings ? Graphics`
2. Check "Scriptable Render Pipeline Settings"
3. Should show a URP asset (e.g., `UniversalRenderPipelineAsset`)
4. If it says "None", you need to assign a URP asset

### **3. Check Always Included Shaders**
1. `Edit ? Project Settings ? Graphics`
2. Scroll to bottom: "Always Included Shaders"
3. Verify these shaders are included:
   - `Universal Render Pipeline/Lit`
   - `Universal Render Pipeline/Simple Lit`
   - `Universal Render Pipeline/Unlit`

---

## ?? If Error Still Occurs

### **Option 1: Create URP Asset (If Missing)**
```
1. Right-click in Project window
2. Create ? Rendering ? URP Asset (with Universal Renderer)
3. Assign it: Edit ? Project Settings ? Graphics ? Scriptable Render Pipeline Settings
```

### **Option 2: Use Built-in Renderer Instead**
If URP is not needed, the code will fallback to Standard shader:

```csharp
// GltfLoader.cs - PickLitShader() method
// Fallback chain:
1. URP/Simple Lit
2. URP/Lit  
3. URP/Unlit
4. Standard  ? ? Built-in fallback
5. Diffuse
6. Sprites/Default (last resort)
```

### **Option 3: Manually Verify Shader Availability**
Add `MaterialDebugger` component to any GameObject:
```csharp
Right-click component ? "List All Shaders in Project"
```

Output will show:
```
? Universal Render Pipeline/Lit - Supported: True
? Universal Render Pipeline/Simple Lit - Supported: True
```

Or:
```
? Universal Render Pipeline/Lit - NOT FOUND
```

---

## ?? Console Output Interpretation

### **? Good Output (Shader Found)**
```
[GLTF-UTILITY] RP Runtime: UniversalRenderPipelineAsset | URP=True
[GLTF-UTILITY] [REMAP] TargetLit=Universal Render Pipeline/Simple Lit
[GLTF-UTILITY] [REMAP] Material.001: GLTFUtility/Standard ? Universal Render Pipeline/Simple Lit
```

### **? Bad Output (Shader Not Found)**
```
[GLTF-UTILITY] [ERROR] Failed to find any Lit shader! Skipping material remap.
```
**Solution:** Configure URP or use Built-in Renderer

### **?? Partial Success (Fallback to Built-in)**
```
[GLTF-UTILITY] RP Runtime: (null) | URP=False
[GLTF-UTILITY] [REMAP] TargetLit=Standard
```
**Explanation:** No render pipeline asset assigned, using Built-in Renderer

---

## ?? Summary

**Problem:** `new Material(shader)` crashed when `shader` was `null`

**Solution:** Added null checks everywhere shaders are used

**Result:** Instead of crashing, the system:
1. Logs a descriptive error
2. Skips the problematic material
3. Continues processing other materials
4. Falls back to Built-in shaders if URP unavailable

---

## ?? Changes Made to GltfLoader.cs

| Location | Before | After |
|----------|--------|-------|
| Line 331 | `r.materials = new[] { new Material(targetLit) };` | Wrapped in try-catch with null check |
| Line 344 | `mats[i] = new Material(targetLit);` | Wrapped in try-catch with null check |
| Line 360 | `m.shader = desired;` | Null check + try-catch |
| Line 375 | `r.materials = mats;` | Wrapped in try-catch |

**Total:** 4 crash points ? All protected with null checks + error handling

---

**Status:** ? FIXED - Build successful, error handling in place

Next: Deploy to device and check console logs!
