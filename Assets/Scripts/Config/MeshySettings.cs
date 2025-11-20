using UnityEngine;

namespace ARMeshyDemo.Config
{
    public enum MeshyAiModel
    {
        ApiDefault, // Let Meshy decide (currently "latest"/Meshy-6)
        Meshy4,
        Meshy5,
        Latest      // Meshy-6 preview
    }

    [CreateAssetMenu(menuName = "AR Meshy/Settings", fileName = "MeshySettings")]
    public class MeshySettings : ScriptableObject
    {
        [Header("Auth")]
        [Tooltip("Sæt din Meshy API key (msy-...). For dev kan den også hentes fra miljøvariablen MESHY_API_KEY hvis feltet er tomt.")]
        public string apiKey;

        [Header("Model / Cost")]
        [Tooltip("Vælg hvilken AI-model der bruges som default.\n" +
                 "- ApiDefault: brug Meshys egen default (pt. latest/Meshy-6-preview)\n" +
                 "- Meshy4 / Meshy5: billigere modeller\n" +
                 "- Latest: Meshy-6-preview (dyrest, højeste kvalitet)")]
        public MeshyAiModel defaultAiModel = MeshyAiModel.Meshy5;

        public string ResolveApiKey()
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
                return apiKey.Trim();

            var fromEnv = System.Environment.GetEnvironmentVariable("MESHY_API_KEY");
            return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv.Trim();
        }

        /// <summary>
        /// Returnerer model-valg som streng til API’et.
        /// overrideValue vinder over enumvalget.
        /// </summary>
        public string ResolveAiModel(string overrideValue = null)
        {
            if (!string.IsNullOrWhiteSpace(overrideValue))
                return overrideValue.Trim();

            switch (defaultAiModel)
            {
                case MeshyAiModel.Meshy4: return "meshy-4";
                case MeshyAiModel.Meshy5: return "meshy-5";
                case MeshyAiModel.Latest: return "latest";
                case MeshyAiModel.ApiDefault:
                default:
                    // null => feltet udelades i JSON, Meshy bruger sin egen default
                    return null;
            }
        }
    }
}
