using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using Siccity.GLTFUtility;

#if UNITY_RENDER_PIPELINES_UNIVERSAL
using UnityEngine.Rendering.Universal;
#endif
#if UNITY_RENDER_PIPELINES_HIGH_DEFINITION
using UnityEngine.Rendering.HighDefinition;
#endif

namespace ARMeshyDemo.Net
{
    /// <summary>
    /// GLB loader using GLTFUtility - better texture support for Meshy models
    /// </summary>
    public class GltfLoader : MonoBehaviour
    {
        public enum LitPolicy { Auto, URP_SimpleLit, URP_Lit }

        [Header("Post-process")]
        [SerializeField] private float uniformScale = 1f;
        [SerializeField] private int setLayer = -1;

        [Header("Material remap")]
        [Tooltip("If enabled, replaces glTF materials with pipeline Lit/Unlit shaders.")]
        [SerializeField] private bool remapMaterials = true;  // ? Changed to true - ensures compatible materials
        [SerializeField] private LitPolicy litPolicy = LitPolicy.Auto;
        [SerializeField] private bool unlitIfNoTextures = false;

        [Header("Material safety")]
        [Tooltip("Auto remap to pipeline shaders when pink/unsupported materials are detected.")]
        [SerializeField] private bool autoRemapOnPink = true; // ? Changed back to true - safety net for broken shaders

        [Header("Debug")]
        [SerializeField] private bool logMaterialDebug = true;
        [SerializeField] private bool verboseTextureDebug = true;

        // ---------------- Public entry points ----------------

        public IEnumerator LoadFromUrl(string url, Action<GameObject> onOk, Action<string> onErr, Action<float> onProgress = null)
        {
            if (string.IsNullOrWhiteSpace(url)) { onErr?.Invoke("GLB URL is empty."); yield break; }
            Log($"Downloading GLB: {url}");
            
            using var req = UnityWebRequest.Get(url);
            req.downloadHandler = new DownloadHandlerBuffer();
            var op = req.SendWebRequest();
            
            while (!op.isDone) 
            { 
                onProgress?.Invoke(Mathf.Clamp01(req.downloadProgress * 0.5f)); 
                yield return null; 
            }
            
            if (req.result != UnityWebRequest.Result.Success) 
            { 
                onErr?.Invoke($"Download error: {req.responseCode} {req.error}"); 
                yield break; 
            }
            
            var data = req.downloadHandler.data;
            if (data == null || data.Length == 0) 
            { 
                onErr?.Invoke("Downloaded 0 bytes."); 
                yield break; 
            }
            
            yield return LoadFromBytes(data, onOk, onErr, p => onProgress?.Invoke(0.5f + 0.5f * p));
        }

        public IEnumerator LoadFromBytes(byte[] glbBytes, Action<GameObject> onOk, Action<string> onErr, Action<float> onProgress = null)
        {
            Log($"--- GLTFUTILITY IMPORT STARTED ---");
            
            // ? GLTFUtility: Write to temp file (it works best with files)
            var tempPath = Path.Combine(Application.temporaryCachePath, $"temp_model_{Guid.NewGuid()}.glb");
            
            try
            {
                File.WriteAllBytes(tempPath, glbBytes);
            }
            catch (Exception ex)
            {
                onErr?.Invoke($"Failed to write temp file: {ex.Message}");
                yield break;
            }
            
            // ? GLTFUtility: Import on MAIN THREAD (must access GraphicsSettings)
            GameObject loadedObject = null;
            Exception importException = null;
            
            onProgress?.Invoke(0.5f);
            yield return null; // Let Unity breathe
            
            try
            {
                // This runs synchronously on main thread - may cause a frame skip
                loadedObject = Importer.LoadFromFile(tempPath);
            }
            catch (Exception ex)
            {
                importException = ex;
            }
            
            // Cleanup temp file
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* ignore cleanup errors */ }
            
            if (importException != null)
            {
                onErr?.Invoke($"GLTFUtility import failed: {importException.Message}");
                yield break;
            }
            
            if (loadedObject == null)
            {
                onErr?.Invoke("GLTFUtility did not create a model.");
                yield break;
            }
            
            Log($"--- GLTF IMPORT COMPLETED ---");
            
            // ? Wait for textures to finish loading - increased wait time for complex models
            yield return null;
            yield return null;
            yield return new WaitForSeconds(0.5f);  // ? Increased from 0.1s to 0.5s for better texture loading
            
            // ? Check what was imported
            var renderers = loadedObject.GetComponentsInChildren<Renderer>(true);
            Log($"--- POST-INSTANTIATION CHECK ---");
            Log($"Renderers found: {renderers.Length}");
            
            int materialCount = 0;
            int textureCount = 0;
            
            foreach (var r in renderers)
            {
                Log($"  Renderer: {r.name}, Materials: {r.sharedMaterials.Length}");
                materialCount += r.sharedMaterials.Length;
                
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat)
                    {
                        Log($"    Material: {mat.name}, Shader: {mat.shader?.name ?? "NULL"}");
                        
                        // Count textures
                        if (mat.mainTexture != null)
                        {
                            textureCount++;
                            Log($"      mainTexture: {mat.mainTexture.name} ({mat.mainTexture.width}x{mat.mainTexture.height})");
                        }
                        
                        // Check all texture properties
                        if (verboseTextureDebug)
                        {
                            DumpMaterialTextures(mat, r.name);
                        }
                    }
                }
            }
            
            Log($"Materials: {materialCount}, Textures: {textureCount}");
            
            // ? Post-load material validation - detect broken materials early
            bool hasBrokenMaterials = false;
            foreach (var r in renderers)
            {
                foreach (var mat in r.sharedMaterials)
                {
                    if (!mat || !mat.shader || !mat.shader.isSupported)
                    {
                        LogError($"? Broken material detected: {mat?.name ?? "null"} on renderer: {r.name}");
                        if (mat && mat.shader)
                        {
                            LogError($"   Shader: {mat.shader.name}, Supported: {mat.shader.isSupported}");
                        }
                        hasBrokenMaterials = true;
                    }
                }
            }
            
            if (hasBrokenMaterials && !remapMaterials)
            {
                LogError("?? Forcing material remap due to broken/unsupported materials...");
                EnsureMaterials(loadedObject);
            }
            
            // Post-processing
            if (!Mathf.Approximately(uniformScale, 1f)) 
                loadedObject.transform.localScale = Vector3.one * uniformScale;
                
            if (setLayer >= 0 && setLayer <= 31) 
                SetLayerRecursively(loadedObject, setLayer);
                
            DebugPipeline();

            // ? Check if materials need remapping
            bool needAutoRemap = !remapMaterials && autoRemapOnPink && NeedsRemapDueToMissingShaders(loadedObject);
            
            if (remapMaterials || needAutoRemap)
            {
                if (needAutoRemap) 
                    Log("Auto-remap triggered: detected pink/unsupported shaders.");
                EnsureMaterials(loadedObject);
            }
            
            if (logMaterialDebug) 
                LogMaterialSummary(loadedObject);
                
            onOk?.Invoke(loadedObject);
        }

        // ---------------- Internals ----------------

        private void SetLayerRecursively(GameObject go, int layer)
        { 
            go.layer = layer; 
            foreach (Transform c in go.transform) 
                SetLayerRecursively(c.gameObject, layer); 
        }

        private bool UsingURP()
        {
            var current = GraphicsSettings.currentRenderPipeline;
            if (!current) return false;
            return current.GetType().Name.Contains("Universal");
        }

        private bool UsingHDRP()
        {
            var current = GraphicsSettings.currentRenderPipeline;
            if (!current) return false;
            var n = current.GetType().Name;
            return n.Contains("HDRender") || n.Contains("HighDefinition") || n.Contains("HDRP");
        }

        private void DebugPipeline()
        {
            var rpName = GraphicsSettings.currentRenderPipeline ? GraphicsSettings.currentRenderPipeline.GetType().Name : "(null)";
            Log($"RP Runtime: {rpName} | URP={UsingURP()} | HDRP={UsingHDRP()} | Lit policy: {litPolicy} | Remap: {remapMaterials}");
        }

        private Shader PickLitShader()
        {
            Shader shader = null;
            
            if (UsingURP())
            {
                if (litPolicy == LitPolicy.URP_Lit)
                {
                    shader = Shader.Find("Universal Render Pipeline/Lit");
                    if (shader) return shader;
                }
                shader = Shader.Find("Universal Render Pipeline/Simple Lit");
                if (shader) return shader;
                
                shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader) return shader;
            }

            if (UsingHDRP())
            {
                shader = Shader.Find("HDRP/Lit");
                if (shader) return shader;
            }

            // Fallback to Standard (always available)
            shader = Shader.Find("Standard");
            if (shader) return shader;
            
            // Last resort: Unity's default diffuse
            shader = Shader.Find("Diffuse");
            if (shader) return shader;
            
            Log("[ERROR] No suitable Lit shader found! Using fallback.");
            return Shader.Find("Sprites/Default"); // Unity's most basic shader
        }

        private Shader PickUnlitShader()
        {
            Shader shader = null;
            
            if (UsingURP())
            {
                shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader) return shader;
            }
            
            shader = Shader.Find("Unlit/Color");
            if (shader) return shader;
            
            shader = Shader.Find("Unlit/Texture");
            if (shader) return shader;
            
            Log("[ERROR] No suitable Unlit shader found! Using fallback.");
            return Shader.Find("Sprites/Default");
        }

        private Texture GetBaseTexture(Material m)
        { 
            if (!m) return null; 
            if (m.HasProperty("_BaseMap")) return m.GetTexture("_BaseMap");
            if (m.HasProperty("_MainTex")) return m.GetTexture("_MainTex");
            if (m.HasProperty("_BaseColorMap")) return m.GetTexture("_BaseColorMap");
            return null;
        }

        private void EnsureMaterials(GameObject root)
        {
            var targetLit = PickLitShader();
            var targetUnlit = PickUnlitShader();
            
            // ? Safety check: Make sure we have valid shaders
            if (!targetLit)
            {
                LogError("Failed to find any Lit shader! Skipping material remap.");
                return;
            }
            if (!targetUnlit)
            {
                LogError("Failed to find any Unlit shader! Skipping material remap.");
                return;
            }
            
            Log($"[REMAP] TargetLit={targetLit?.name} | TargetUnlit={targetUnlit?.name}");

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.materials; 
                if (mats == null || mats.Length == 0) 
                {
                    // ? Safety: Only create new material if shader is valid
                    try
                    {
                        if (targetLit != null)
                        {
                            r.materials = new[] { new Material(targetLit) };
                        }
                        else
                        {
                            LogError($"Cannot create material for {r.name}: targetLit shader is null");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        LogError($"Failed to create material for {r.name}: {ex.Message}");
                    }
                    continue; 
                }
                
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (!m || !m.shader) 
                    {
                        // ? Safety: Only create new material if shader is valid
                        try
                        {
                            if (targetLit != null)
                            {
                                mats[i] = new Material(targetLit);
                                changed = true;
                            }
                            else
                            {
                                LogError($"Cannot replace null material on {r.name}: targetLit shader is null");
                            }
                        }
                        catch (System.Exception ex)
                        {
                            LogError($"Failed to replace null material on {r.name}: {ex.Message}");
                        }
                        continue; 
                    }
                    
                    // Gather textures BEFORE changing shader
                    var textures = GatherTextures(m);
                    bool hasAnyTexture = textures.baseMap || textures.normal || textures.metallic || 
                                        textures.occlusion || textures.emissive;
                    
                    var desired = (!hasAnyTexture && unlitIfNoTextures) ? targetUnlit : targetLit;
                    
                    // ? Safety: Verify desired shader is not null
                    if (desired == null)
                    {
                        LogError($"Desired shader is null for material {m.name} on {r.name}");
                        continue;
                    }
                    
                    if (m.shader == desired) continue;
                    
                    var oldShader = m.shader.name;
                    
                    // ? Safety: Try-catch shader assignment
                    try
                    {
                        m.shader = desired;
                        
                        // Re-apply textures to new shader
                        ApplyTexturesToShader(m, textures, desired == targetLit);
                        
                        changed = true;
                        Log($"[REMAP] {r.name}/{m.name}: {oldShader} ? {desired.name}");
                    }
                    catch (System.Exception ex)
                    {
                        LogError($"Failed to remap shader for {m.name}: {ex.Message}");
                    }
                }
                
                if (changed)
                {
                    try
                    {
                        r.materials = mats;
                    }
                    catch (System.Exception ex)
                    {
                        LogError($"Failed to assign materials to {r.name}: {ex.Message}");
                    }
                }
            }
        }

        private static void Log(string msg) => Debug.Log($"[GLTF-UTILITY] {msg}");
        private static void LogError(string msg) => Debug.LogError($"[GLTF-UTILITY] {msg}");

        private (Texture baseMap, Texture normal, Texture metallic, Texture occlusion, Texture emissive, Color baseColor) GatherTextures(Material m)
        {
            Texture baseMap = null, normal = null, metallic = null, occlusion = null, emissive = null;
            Color baseColor = Color.white;
            
            // Try various property names
            if (m.HasProperty("_BaseMap")) baseMap = m.GetTexture("_BaseMap");
            if (!baseMap && m.HasProperty("_MainTex")) baseMap = m.GetTexture("_MainTex");
            if (!baseMap && m.HasProperty("_BaseColorMap")) baseMap = m.GetTexture("_BaseColorMap");
            
            if (m.HasProperty("_BumpMap")) normal = m.GetTexture("_BumpMap");
            if (!normal && m.HasProperty("_NormalMap")) normal = m.GetTexture("_NormalMap");
            
            if (m.HasProperty("_MetallicGlossMap")) metallic = m.GetTexture("_MetallicGlossMap");
            if (!metallic && m.HasProperty("_MetallicRoughnessMap")) metallic = m.GetTexture("_MetallicRoughnessMap");
            
            if (m.HasProperty("_OcclusionMap")) occlusion = m.GetTexture("_OcclusionMap");
            if (m.HasProperty("_EmissionMap")) emissive = m.GetTexture("_EmissionMap");
            
            if (m.HasProperty("_BaseColor")) baseColor = m.GetColor("_BaseColor");
            else if (m.HasProperty("_Color")) baseColor = m.GetColor("_Color");
            
            return (baseMap, normal, metallic, occlusion, emissive, baseColor);
        }

        private void ApplyTexturesToShader(Material m, (Texture baseMap, Texture normal, Texture metallic, Texture occlusion, Texture emissive, Color baseColor) tex, bool isLit)
        {
#if UNITY_RENDER_PIPELINES_UNIVERSAL
            if (UsingURP())
            {
                if (tex.baseMap && m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex.baseMap);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", tex.baseColor);
                
                if (isLit)
                {
                    if (tex.normal && m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", tex.normal);
                    if (tex.metallic && m.HasProperty("_MetallicGlossMap")) m.SetTexture("_MetallicGlossMap", tex.metallic);
                    if (tex.occlusion && m.HasProperty("_OcclusionMap")) m.SetTexture("_OcclusionMap", tex.occlusion);
                    if (tex.emissive && m.HasProperty("_EmissionMap"))
                    {
                        m.SetTexture("_EmissionMap", tex.emissive);
                        m.EnableKeyword("_EMISSION");
                    }
                }
                return;
            }
#endif
#if UNITY_RENDER_PIPELINES_HIGH_DEFINITION
            if (UsingHDRP())
            {
                if (tex.baseMap && m.HasProperty("_BaseColorMap")) m.SetTexture("_BaseColorMap", tex.baseMap);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", tex.baseColor);
                
                if (isLit)
                {
                    if (tex.normal && m.HasProperty("_NormalMap")) m.SetTexture("_NormalMap", tex.normal);
                    if (tex.metallic && m.HasProperty("_MaskMap")) m.SetTexture("_MaskMap", tex.metallic);
                    if (tex.emissive && m.HasProperty("_EmissiveColorMap")) m.SetTexture("_EmissiveColorMap", tex.emissive);
                }
                return;
            }
#endif
            // Built-in
            if (tex.baseMap && m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex.baseMap);
            if (m.HasProperty("_Color")) m.SetColor("_Color", tex.baseColor);
            
            if (isLit)
            {
                if (tex.normal && m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", tex.normal);
                if (tex.metallic && m.HasProperty("_MetallicGlossMap")) m.SetTexture("_MetallicGlossMap", tex.metallic);
                if (tex.occlusion && m.HasProperty("_OcclusionMap")) m.SetTexture("_OcclusionMap", tex.occlusion);
                if (tex.emissive && m.HasProperty("_EmissionMap"))
                {
                    m.SetTexture("_EmissionMap", tex.emissive);
                    m.EnableKeyword("_EMISSION");
                }
            }
        }

        private bool NeedsRemapDueToMissingShaders(GameObject root)
        {
            bool usingSRP = GraphicsSettings.currentRenderPipeline != null;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (!m || !m.shader) return true;
                    if (m.shader.name == "Hidden/InternalErrorShader") return true;
                    if (!m.shader.isSupported) return true;
                    if (usingSRP && m.shader.name.StartsWith("Standard")) return true;
                }
            }
            return false;
        }

        private void LogMaterialSummary(GameObject root)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (!m) continue;
                    var tex = GatherTextures(m);
                    Log($"Mat '{m.name}' | Shader: {m.shader?.name} | " +
                        $"Base:{tex.baseMap != null} Norm:{tex.normal != null} Metal:{tex.metallic != null} " +
                        $"Occ:{tex.occlusion != null} Emis:{tex.emissive != null} | Supported:{m.shader?.isSupported}");
                }
            }
        }

        private void DumpMaterialTextures(Material m, string rendererName)
        {
            Log($"[MAT-TEX] Renderer={rendererName} Material={m.name} Shader={m.shader?.name}");
            
            string[] props = {
                "_BaseMap", "_MainTex", "_BaseColorMap",
                "_BumpMap", "_NormalMap",
                "_MetallicGlossMap", "_MetallicRoughnessMap", "_MaskMap",
                "_OcclusionMap",
                "_EmissionMap", "_EmissiveTexture"
            };
            
            foreach (var p in props)
            {
                if (!m.HasProperty(p)) continue;
                var tex = m.GetTexture(p);
                if (!tex) continue;
                
                if (tex is Texture2D t2d)
                {
                    Log($"  {p}: {tex.name} ({t2d.width}x{t2d.height}) fmt={t2d.format} mips={t2d.mipmapCount}");
                }
                else
                {
                    Log($"  {p}: {tex.name} (type={tex.GetType().Name})");
                }
            }
        }
    }
}
