using UnityEngine;
using UnityEngine.XR.ARFoundation;
using System.Text;
using TMPro;                           // <-- add this

public class ARStatusHUD : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ARSession arSession;
    [SerializeField] private ARTrackedImageManager trackedImageManager;
    [SerializeField] private TextMeshProUGUI statusText;  // <-- concrete TMP UI type

    private readonly StringBuilder sb = new StringBuilder();

    void Update()
    {
        if (!statusText) return;

        sb.Clear();
        sb.AppendLine($"ARSession: {ARSession.state}");
        sb.AppendLine($"Reason: {ARSession.notTrackingReason}");

        if (trackedImageManager)
        {
            sb.AppendLine($"Tracked Images: {trackedImageManager.trackables.count}");
            foreach (var img in trackedImageManager.trackables)
                sb.AppendLine($"• {img.referenceImage.name} [{img.trackingState}]");
        }

        statusText.text = sb.ToString();
    }
}
