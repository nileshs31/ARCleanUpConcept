using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
// nothing needed here
#endif
// If you use XR Interaction Toolkit, enable this using directive:
#if UNITY_XR_TOOLKIT_AVAILABLE
using UnityEngine.XR.Interaction.Toolkit;
#endif

public class GarbageCan : MonoBehaviour
{
    public float collectDuration = 0.45f;

    // reference to spawner (optional; auto-find if not assigned)
    public PlaneScannerAndObjectsSpawner spawner;
    [SerializeField] GameObject pfx;

    void Reset()
    {
        // attempt to find spawner automatically in scene
        if (spawner == null)
            spawner = FindObjectOfType<PlaneScannerAndObjectsSpawner>();
    }

    void Awake()
    {
        if (spawner == null)
            spawner = FindObjectOfType<PlaneScannerAndObjectsSpawner>();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Garbage"))
        {
            StartCoroutine(CollectAndDestroyCoroutine(other.transform.root.gameObject));
        }
    }

    IEnumerator CollectAndDestroyCoroutine(GameObject garbage)
    {
        if (garbage == null) yield break;

        garbage.GetComponent<XRGrabInteractable>().enabled = false;   
        garbage.GetComponent<ARTransformer>().enabled = false;

        Transform gTrans = garbage.transform;
        Vector3 startPos = gTrans.position;
        Vector3 originalScale = gTrans.localScale;

        gTrans.localScale = Vector3.zero;

        Vector3 targetPos = transform.position;

        float t = 0f;
        while (t < collectDuration)
        {
            t += Time.deltaTime;
            float f = Mathf.SmoothStep(0f, 1f, t / collectDuration);
            gTrans.position = Vector3.Lerp(startPos, targetPos, f);
            gTrans.localScale = Vector3.Lerp(originalScale, Vector3.zero, f);
            yield return null;
        }

        gTrans.position = targetPos;
        gTrans.localScale = originalScale; 
        pfx.SetActive(true);

        if (spawner != null)
        {
            spawner.NotifyItemRemoved();
        }
        Destroy(garbage);
        yield return new WaitForSeconds(0.75f);
        pfx.SetActive(false);

    }
}
