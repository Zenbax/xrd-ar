using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ARMeshyDemo.UI
{
    public class UIController : MonoBehaviour
    {
        [Header("Core")]
        [SerializeField] private GameObject loader;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private Button generateButton;

        [Header("Progress & Actions")]
        [SerializeField] private Slider progressBar;
        [SerializeField] private Button cancelButton;
        [SerializeField] private Button retryButton;

        [Header("Error UI")]
        [SerializeField] private GameObject errorPanel;
        [SerializeField] private TMP_Text errorText;

        public Button CancelButton => cancelButton;
        public Button RetryButton => retryButton;

        public void SetLoading(bool isLoading, string status = null)
        {
            if (loader) loader.SetActive(isLoading);
            if (generateButton) generateButton.interactable = !isLoading;
            if (!string.IsNullOrEmpty(status)) SetStatus(status);
        }

        public void SetStatus(string status)
        {
            if (statusText) statusText.text = status;
        }

        public void SetProgressVisible(bool visible)
        {
            if (progressBar) progressBar.gameObject.SetActive(visible);
            if (!visible && progressBar) progressBar.value = 0f;
        }

        /// 0..1
        public void SetProgress(float v, string status = null)
        {
            if (progressBar) progressBar.value = Mathf.Clamp01(v);
            if (!string.IsNullOrEmpty(status)) SetStatus(status);
        }

        public void ShowCancel(bool visible)
        {
            if (cancelButton) cancelButton.gameObject.SetActive(visible);
        }

        public void ShowRetry(bool visible)
        {
            if (retryButton) retryButton.gameObject.SetActive(visible);
        }

        public void ShowError(string message, bool showRetry = true)
        {
            if (errorText) errorText.text = message;
            if (errorPanel) errorPanel.SetActive(true);
            ShowRetry(showRetry);
        }

        public void HideError()
        {
            if (errorPanel) errorPanel.SetActive(false);
            ShowRetry(false);
        }

        public void ResetUI(string status = "Ready")
        {
            SetLoading(false, status);
            SetProgressVisible(false);
            ShowCancel(false);
            HideError();
        }

        // Kald i Inspector (Panel_Error OK-knap) hvis du lavede en OK-knap
        public void OnErrorOkClicked()
        {
            HideError();
        }
    }
}
