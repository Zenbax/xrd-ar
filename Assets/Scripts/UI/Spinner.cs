using UnityEngine;

namespace ARMeshyDemo.UI
{
    public class Spinner : MonoBehaviour
    {
        [SerializeField] private float speed = 180f; // grader/sek.

        void Update()
        {
            transform.Rotate(0f, 0f, -speed * Time.deltaTime);
        }
    }
}
