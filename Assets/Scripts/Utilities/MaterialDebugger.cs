using UnityEngine;

namespace ARMeshyDemo.Debugging
{
    /// <summary>
    /// Debug utility to inspect materials on loaded models.
    /// Add this component to any GameObject to debug its materials.
    /// Right-click component in Inspector ? "Debug Materials" to run.
    /// </summary>
    public class MaterialDebugger : MonoBehaviour
    {
        [Header("Auto-run on Start")]
        [SerializeField] private bool debugOnStart = false;

        [Header("Verbose Output")]
        [SerializeField] private bool showTextureDetails = true;
        [SerializeField] private bool showShaderProperties = false;

        void Start()
        {
            if (debugOnStart)
            {
                DebugMaterials();
            }
        }

        [ContextMenu("Debug Materials")]
        public void DebugMaterials()
        {
            var renderers = GetComponentsInChildren<Renderer>(true);
            
            if (renderers.Length == 0)
            {
                UnityEngine.Debug.LogWarning($"[MaterialDebugger] No renderers found on {gameObject.name}");
                return;
            }

            UnityEngine.Debug.Log($"[MaterialDebugger] ========== MATERIAL DEBUG: {gameObject.name} ==========");
            UnityEngine.Debug.Log($"[MaterialDebugger] Total Renderers: {renderers.Length}");

            int totalMaterials = 0;
            int brokenMaterials = 0;
            int missingTextures = 0;

            foreach (var r in renderers)
            {
                UnityEngine.Debug.Log($"\n[MaterialDebugger] Renderer: {r.name} (Active: {r.enabled})");
                UnityEngine.Debug.Log($"  Type: {r.GetType().Name}");
                UnityEngine.Debug.Log($"  Materials: {r.sharedMaterials.Length}");

                foreach (var mat in r.sharedMaterials)
                {
                    totalMaterials++;

                    if (!mat)
                    {
                        UnityEngine.Debug.LogError($"  ? NULL Material!");
                        brokenMaterials++;
                        continue;
                    }

                    bool isBroken = !mat.shader || !mat.shader.isSupported;
                    string status = isBroken ? "? BROKEN" : "? OK";

                    UnityEngine.Debug.Log($"\n  {status} Material: {mat.name}");
                    UnityEngine.Debug.Log($"    Shader: {mat.shader?.name ?? "NULL"}");
                    UnityEngine.Debug.Log($"    Shader Supported: {mat.shader?.isSupported ?? false}");
                    UnityEngine.Debug.Log($"    Render Queue: {mat.renderQueue}");

                    if (isBroken)
                    {
                        brokenMaterials++;
                    }

                    if (showTextureDetails)
                    {
                        DebugMaterialTextures(mat, ref missingTextures);
                    }

                    if (showShaderProperties && mat.shader)
                    {
                        DebugShaderProperties(mat);
                    }
                }
            }

            UnityEngine.Debug.Log($"\n[MaterialDebugger] ========== SUMMARY ==========");
            UnityEngine.Debug.Log($"Total Materials: {totalMaterials}");
            UnityEngine.Debug.Log($"Broken Materials: {brokenMaterials}");
            UnityEngine.Debug.Log($"Missing Textures: {missingTextures}");

            if (brokenMaterials > 0)
            {
                UnityEngine.Debug.LogError($"?? FOUND {brokenMaterials} BROKEN MATERIALS - Enable Material Remapping in GltfLoader!");
            }
            else
            {
                UnityEngine.Debug.Log("? All materials are valid!");
            }
        }

        private void DebugMaterialTextures(Material mat, ref int missingCount)
        {
            string[] textureProps = {
                "_BaseMap", "_MainTex", "_BaseColorMap",           // Albedo
                "_BumpMap", "_NormalMap",                          // Normal
                "_MetallicGlossMap", "_MetallicRoughnessMap",     // Metallic/Roughness
                "_OcclusionMap",                                   // Occlusion
                "_EmissionMap", "_EmissiveTexture"                 // Emission
            };

            int foundTextures = 0;

            foreach (var prop in textureProps)
            {
                if (!mat.HasProperty(prop))
                    continue;

                var tex = mat.GetTexture(prop);
                if (!tex)
                    continue;

                foundTextures++;

                if (tex is Texture2D t2d)
                {
                    UnityEngine.Debug.Log($"    ? {prop}: {tex.name} ({t2d.width}x{t2d.height}) fmt={t2d.format} mips={t2d.mipmapCount}");
                }
                else
                {
                    UnityEngine.Debug.Log($"    ? {prop}: {tex.name} (type={tex.GetType().Name})");
                }
            }

            if (foundTextures == 0)
            {
                UnityEngine.Debug.LogWarning($"    ?? No textures found on material!");
                missingCount++;
            }
            else
            {
                UnityEngine.Debug.Log($"    Total Textures: {foundTextures}");
            }
        }

        private void DebugShaderProperties(Material mat)
        {
            int propertyCount = mat.shader.GetPropertyCount();
            UnityEngine.Debug.Log($"    Shader Properties: {propertyCount}");

            for (int i = 0; i < propertyCount; i++)
            {
                var propName = mat.shader.GetPropertyName(i);
                var propType = mat.shader.GetPropertyType(i);
                UnityEngine.Debug.Log($"      [{i}] {propName} ({propType})");
            }
        }

        [ContextMenu("List All Shaders in Project")]
        public void ListShaders()
        {
            UnityEngine.Debug.Log("[MaterialDebugger] ========== AVAILABLE SHADERS ==========");

            string[] shaderNames = {
                "Universal Render Pipeline/Lit",
                "Universal Render Pipeline/Simple Lit",
                "Universal Render Pipeline/Unlit",
                "HDRP/Lit",
                "Standard",
                "Standard (Specular setup)",
                "Unlit/Color",
                "Unlit/Texture"
            };

            foreach (var shaderName in shaderNames)
            {
                var shader = Shader.Find(shaderName);
                if (shader)
                {
                    UnityEngine.Debug.Log($"? {shaderName} - Supported: {shader.isSupported}");
                }
                else
                {
                    UnityEngine.Debug.LogWarning($"? {shaderName} - NOT FOUND");
                }
            }
        }
    }
}
