using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using System.Text;
using TMPro;

public class ARStatusHUD : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ARSession arSession;
    [SerializeField] private ARTrackedImageManager trackedImageManager;
    [SerializeField] private TextMeshProUGUI statusText;

    private readonly StringBuilder sb = new StringBuilder();

    void Update()
    {
        if (!statusText) return;

        sb.Clear();

        // --- AR Session State ---
        sb.AppendLine($"ARSession: {ARSession.state}");
        sb.AppendLine($"Reason: {ARSession.notTrackingReason}");

        // --- Runtime Image Library Info ---
        if (trackedImageManager)
        {
            var lib = trackedImageManager.referenceLibrary;

            if (lib is MutableRuntimeReferenceImageLibrary runtimeLib)
            {
                sb.AppendLine($"Runtime Library Count: {runtimeLib.count}");
            }
            else if (lib is XRReferenceImageLibrary staticLib)
            {
                sb.AppendLine($"Static Library: {staticLib.name} (Count: {staticLib.count})");
            }
            else if (lib != null)
            {
                sb.AppendLine("Reference Library: (Unknown type)");
            }
            else
            {
                sb.AppendLine("No active reference library.");
            }

            // --- Tracked Images ---
            sb.AppendLine($"Tracked Images: {trackedImageManager.trackables.count}");

            foreach (var img in trackedImageManager.trackables)
                sb.AppendLine($"• {img.referenceImage.name} [{img.trackingState}]");
        }
        else
        {
            sb.AppendLine("No ARTrackedImageManager assigned.");
        }

        // Update text UI
        statusText.text = sb.ToString();
    }
}
