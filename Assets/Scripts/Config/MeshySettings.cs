using UnityEngine;

namespace ARMeshyDemo.Config
{
    [CreateAssetMenu(menuName = "AR Meshy/Settings", fileName = "MeshySettings")]
    public class MeshySettings : ScriptableObject
    {
        [Tooltip("Sæt din Meshy API key (msy-...). For dev kan den også hentes fra miljøvariablen MESHY_API_KEY hvis feltet er tomt.")]
        public string apiKey;

        public string ResolveApiKey()
        {
            if (!string.IsNullOrWhiteSpace(apiKey)) return apiKey.Trim();
            var fromEnv = System.Environment.GetEnvironmentVariable("MESHY_API_KEY");
            return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv.Trim();
        }
    }
}
