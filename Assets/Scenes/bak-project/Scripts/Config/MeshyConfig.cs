using UnityEngine;

[CreateAssetMenu(fileName = "MeshyConfig", menuName = "Config/MeshyConfig")]
public class MeshyConfig : ScriptableObject
{
    [Tooltip("Meshy API Key (keep safe)")]
    public string apiKey = "msy_F2B3CtfRFRgJeqdmKiU5b2IG4NavTnc3kQrg";
    [Tooltip("Optional: Base URL, default uses https://api.meshy.ai")]
    public string baseUrl = "https://api.meshy.ai";
}

