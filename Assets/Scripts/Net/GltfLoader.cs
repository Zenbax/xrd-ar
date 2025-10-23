using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using GLTFast;                 // glTFast API
using Debug = UnityEngine.Debug;

namespace ARMeshyDemo.Net
{
    /// <summary>
    /// Downloader en .glb og indlæser den via glTFast.
    /// Returnerer et root-GameObject med hele modellen som children.
    /// </summary>
    public class GltfLoader : MonoBehaviour
    {
        [Tooltip("Valgfri: Skaler model efter indlæsning (1 = uændret)")]
        [SerializeField] private float uniformScale = 1f;

        [Tooltip("Valgfri: Læg modellen på denne layer (−1 = uændret).")]
        [SerializeField] private int setLayer = -1;

        /// <summary>
        /// Hent GLB fra URL og indlæs. onProgress: 0..1
        /// </summary>
        public IEnumerator LoadFromUrl(
            string url,
            Action<GameObject> onOk,
            Action<string> onErr,
            Action<float> onProgress = null)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                onErr?.Invoke("GLB URL er tom.");
                yield break;
            }

            using (var req = UnityWebRequest.Get(url))
            {
                req.downloadHandler = new DownloadHandlerBuffer();
                var op = req.SendWebRequest();

                while (!op.isDone)
                {
                    onProgress?.Invoke(Mathf.Clamp01(req.downloadProgress * 0.5f)); // første halvdel = download
                    yield return null;
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    onErr?.Invoke($"Download fejl: {req.responseCode} {req.error}");
                    yield break;
                }

                var data = req.downloadHandler.data;
                if (data == null || data.Length == 0)
                {
                    onErr?.Invoke("Downloadede 0 bytes.");
                    yield break;
                }

                // 2) Importér via glTFast
                yield return LoadFromBytes(
                    data,
                    onOk,
                    onErr,
                    p => onProgress?.Invoke(0.5f + 0.5f * Mathf.Clamp01(p)) // anden halvdel = import/instans
                );
            }
        }

        /// <summary>
        /// Indlæs GLB fra bytes (hvis du fx allerede har downloadet).
        /// </summary>
        public IEnumerator LoadFromBytes(
            byte[] glbBytes,
            Action<GameObject> onOk,
            Action<string> onErr,
            Action<float> onProgress = null)
        {
            var gltf = new GltfImport();

            // Nyere glTFast: brug den simple overload uden ImportSettings
            Task<bool> importTask = gltf.LoadGltfBinary(glbBytes);

            while (!importTask.IsCompleted)
            {
                // (valgfri) opdater en grov progress her, hvis du ønsker
                yield return null;
            }

            if (!importTask.Result)
            {
                onErr?.Invoke("glTFast kunne ikke importere GLB’en.");
                yield break;
            }

            // Instantiér direkte til et nyt root-objekt
            var root = new GameObject("GLB_Model_Root");
            bool ok = gltf.InstantiateMainScene(root.transform);
            if (!ok)
            {
                Destroy(root);
                onErr?.Invoke("Kunne ikke instantiere hovedscenen fra GLB.");
                yield break;
            }

            // Valgfrit: skaler og layer
            if (!Mathf.Approximately(uniformScale, 1f))
                root.transform.localScale = Vector3.one * uniformScale;

            if (setLayer >= 0 && setLayer <= 31)
                SetLayerRecursively(root, setLayer);

            onProgress?.Invoke(1f);
            onOk?.Invoke(root);
        }

        private void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform)
                SetLayerRecursively(c.gameObject, layer);
        }
    }
}
