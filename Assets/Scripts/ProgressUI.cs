using UnityEngine;
using UnityEngine.UI;
using TMPro; // If you don't use TMP, replace TMP_Text with Text and remove this line.

public class ProgressUI : MonoBehaviour
{
    [SerializeField] private Slider slider;
    [SerializeField] private TMP_Text label; // Or: public Text label;

    public void Show(bool visible) => gameObject.SetActive(visible);

    public void Set(float normalized, string message)
    {
        if (slider) slider.value = Mathf.Clamp01(normalized);
        if (label) label.text = message;
    }
}
