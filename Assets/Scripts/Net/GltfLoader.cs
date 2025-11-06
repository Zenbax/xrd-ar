using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using GLTFast;

#if UNITY_RENDER_PIPELINES_UNIVERSAL
using UnityEngine.Rendering.Universal;
#endif
#if UNITY_RENDER_PIPELINES_HIGH_DEFINITION
using UnityEngine.Rendering.HighDefinition;
#endif

namespace ARMeshyDemo.Net
{
    /// <summary>
    /// Robust GLB loader for URP/HDRP/Built-in:
    /// - Downloads GLB (e.g., Meshy signed URL) and loads via glTFast.
    /// - Forces safe Lit materials per active pipeline to avoid magenta.
    /// - URP defaults to Simple Lit (small variant footprint).
    /// </summary>
    public class GltfLoader : MonoBehaviour
    {
        public enum LitPolicy
        {
            Auto,           // URP: Simple Lit, HDRP: HDRP/Lit, Built-in: Standard
            URP_SimpleLit,  // Force Simple Lit when URP
            URP_Lit         // Force full URP/Lit when URP (large variant set!)
        }

        [Header("Post-process")]
        [SerializeField] private float uniformScale = 1f;
        [SerializeField] private int setLayer = -1;

        [Header("Material remap")]
        [SerializeField] private LitPolicy litPolicy = LitPolicy.Auto;
        [SerializeField] private bool unlitIfNoTextures = false;

        [Header("Debug")]
        [SerializeField] private bool logMaterialDebug = true;

        // ---------------- Public entry points ----------------

        public IEnumerator LoadFromUrl(
            string url,
            Action<GameObject> onOk,
            Action<string> onErr,
            Action<float> onProgress = null)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                onErr?.Invoke("GLB URL is empty.");
                yield break;
            }

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

        public IEnumerator LoadFromBytes(
            byte[] glbBytes,
            Action<GameObject> onOk,
            Action<string> onErr,
            Action<float> onProgress = null)
        {
            var gltf = new GltfImport();

            Task<bool> importTask;
            try
            {
                // Works across glTFast versions.
                importTask = gltf.LoadGltfBinary(glbBytes);
            }
            catch (Exception e)
            {
                onErr?.Invoke("glTFast import threw: " + e.Message);
                yield break;
            }

            while (!importTask.IsCompleted)
            {
                onProgress?.Invoke(0.15f + 0.7f * Mathf.PingPong(Time.time * 0.25f, 1f));
                yield return null;
            }

            if (!importTask.Result)
            {
                onErr?.Invoke("glTFast could not import GLB bytes.");
                yield break;
            }

            var root = new GameObject("GLB_Model_Root");
            var ok = gltf.InstantiateMainScene(root.transform);
            if (!ok)
            {
                Destroy(root);
                onErr?.Invoke("Could not instantiate main scene from GLB.");
                yield break;
            }

            if (!Mathf.Approximately(uniformScale, 1f))
                root.transform.localScale = Vector3.one * uniformScale;

            if (setLayer >= 0 && setLayer <= 31)
                SetLayerRecursively(root, setLayer);

            DebugPipeline();
            EnsureMaterials(root);

            if (logMaterialDebug)
                LogMaterialSummary(root);

            onOk?.Invoke(root);
            yield break;
        }

        // ---------------- Internals ----------------

        private void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform)
                SetLayerRecursively(c.gameObject, layer);
        }

        private void DebugPipeline()
        {
#if UNITY_RENDER_PIPELINES_UNIVERSAL
            var rp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            Log($"RP: {(rp ? "URP" : "Unknown")} | Lit policy: {litPolicy}");
#elif UNITY_RENDER_PIPELINES_HIGH_DEFINITION
            var rp = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
            Log($"RP: {(rp ? "HDRP" : "Unknown")} | Lit policy: {litPolicy}");
#else
            Log("RP: Built-in | Lit policy: " + litPolicy);
#endif
        }

        private Shader PickLitShader()
        {
#if UNITY_RENDER_PIPELINES_UNIVERSAL
            if (litPolicy == LitPolicy.URP_Lit)
            {
                var lit = Shader.Find("Universal Render Pipeline/Lit");
                if (lit) return lit;
            }

            // Prefer Simple Lit for variant sanity
            var simple = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (simple) return simple;

            // Fallback
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            if (unlit) return unlit;
            return Shader.Find("Standard");

#elif UNITY_RENDER_PIPELINES_HIGH_DEFINITION
            var hd = Shader.Find("HDRP/Lit");
            if (hd) return hd;
            return Shader.Find("Standard");

#else
            return Shader.Find("Standard");
#endif
        }

        private Shader PickUnlitShader()
        {
#if UNITY_RENDER_PIPELINES_UNIVERSAL
            var s = Shader.Find("Universal Render Pipeline/Unlit");
            if (s) return s;
#endif
            return Shader.Find("Unlit/Color");
        }

        private void EnsureMaterials(GameObject root)
        {
            var targetLit = PickLitShader();
            var targetUnlit = PickUnlitShader();

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.materials;
                if (mats == null || mats.Length == 0)
                {
                    r.materials = new[] { new Material(targetLit) };
                    continue;
                }

                var changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (!m || !m.shader)
                    {
                        mats[i] = new Material(targetLit);
                        changed = true;
                        continue;
                    }

                    // Gather commonly-used textures before swapping
                    Texture baseMap = null, mainTex = null, normal = null, metallic = null, occlusion = null;

                    if (m.HasProperty("_BaseMap")) baseMap = m.GetTexture("_BaseMap");
                    if (m.HasProperty("_MainTex")) mainTex = m.GetTexture("_MainTex");
                    if (m.HasProperty("_BumpMap")) normal = m.GetTexture("_BumpMap");
                    if (m.HasProperty("_MetallicGlossMap")) metallic = m.GetTexture("_MetallicGlossMap");
                    if (m.HasProperty("_OcclusionMap")) occlusion = m.GetTexture("_OcclusionMap");

                    bool hasAnyTexture = (baseMap || mainTex || normal || metallic || occlusion);

                    // Decide target shader (optionally unlit when nothing to show)
                    var desired = (!hasAnyTexture && unlitIfNoTextures) ? targetUnlit : targetLit;
                    if (m.shader == desired) continue;

                    m.shader = desired;

#if UNITY_RENDER_PIPELINES_UNIVERSAL
                    if (desired == targetLit)
                    {
                        if (baseMap) m.SetTexture("_BaseMap", baseMap);
                        else if (mainTex) m.SetTexture("_BaseMap", mainTex);
                        if (normal   && m.HasProperty("_BumpMap"))          m.SetTexture("_BumpMap", normal);
                        if (metallic && m.HasProperty("_MetallicGlossMap")) m.SetTexture("_MetallicGlossMap", metallic);
                        if (occlusion&& m.HasProperty("_OcclusionMap"))     m.SetTexture("_OcclusionMap", occlusion);
                    }
                    else
                    {
                        // URP Unlit uses _BaseMap for base color texture
                        if (baseMap) m.SetTexture("_BaseMap", baseMap);
                        else if (mainTex) m.SetTexture("_BaseMap", mainTex);
                    }
#elif UNITY_RENDER_PIPELINES_HIGH_DEFINITION
                    if (desired == targetLit)
                    {
                        if (baseMap) m.SetTexture("_BaseColorMap", baseMap);
                        else if (mainTex) m.SetTexture("_BaseColorMap", mainTex);
                        if (normal   && m.HasProperty("_NormalMap")) m.SetTexture("_NormalMap", normal);
                        if (metallic && m.HasProperty("_MaskMap"))   m.SetTexture("_MaskMap", metallic);
                        if (occlusion&& m.HasProperty("_OcclusionMap")) m.SetTexture("_OcclusionMap", occlusion);
                    }
                    else
                    {
                        if (baseMap && m.HasProperty("_UnlitColorMap")) m.SetTexture("_UnlitColorMap", baseMap);
                        else if (mainTex && m.HasProperty("_UnlitColorMap")) m.SetTexture("_UnlitColorMap", mainTex);
                    }
#else
                    if (desired == targetLit)
                    {
                        if (mainTex) m.SetTexture("_MainTex", mainTex);
                        else if (baseMap) m.SetTexture("_MainTex", baseMap);
                        if (normal && m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", normal);
                        if (metallic && m.HasProperty("_MetallicGlossMap")) m.SetTexture("_MetallicGlossMap", metallic);
                        if (occlusion && m.HasProperty("_OcclusionMap")) m.SetTexture("_OcclusionMap", occlusion);
                    }
                    else
                    {
                        if (mainTex) m.SetTexture("_MainTex", mainTex);
                        else if (baseMap) m.SetTexture("_MainTex", baseMap);
                    }
#endif
                    changed = true;
                }

                if (changed) r.materials = mats; // write back
            }
        }

        private void LogMaterialSummary(GameObject root)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m)
                        Log($"Mat '{m.name}' | Shader: {(m.shader ? m.shader.name : "(null)")} " +
                            $"Base:{HasTex(m, "_BaseMap") || HasTex(m, "_MainTex") || HasTex(m, "_BaseColorMap")} " +
                            $"Norm:{HasTex(m, "_BumpMap") || HasTex(m, "_NormalMap")} " +
                            $"Metal:{HasTex(m, "_MetallicGlossMap") || HasTex(m, "_MaskMap")} " +
                            $"Occ:{HasTex(m, "_OcclusionMap")}");
        }

        private bool HasTex(Material m, string prop) =>
            m.HasProperty(prop) && m.GetTexture(prop) != null;

        private static void Log(string msg) => Debug.Log($"[GLTF-LOADER] {msg}");
    }
}
