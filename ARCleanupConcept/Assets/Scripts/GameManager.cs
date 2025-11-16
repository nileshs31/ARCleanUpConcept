using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;

public class GameManager : MonoBehaviour
{
    public static GameManager instance;
    [SerializeField] TextMeshProUGUI scoreUi;
    [SerializeField] GameObject confettiObj, promptScanSurfaces, gameOverObj;
    [SerializeField] PlaneScannerAndObjectsSpawner planeScanner;
    public int cleanedObj = 0;

    private void Awake()
    {
        instance = this;
    }
    // Start is called before the first frame update
    void Start()
    {
        scoreUi.text = "Garbage Cleaned: " + cleanedObj + " / " + planeScanner.maxSpawns;
    }

    // Update is called once per frame
    void Update()
    {
        
    }
    public void UpdateScore()
    {
        cleanedObj++;
        scoreUi.text = "Garbage Cleaned: " + cleanedObj + " / " + planeScanner.maxSpawns;
        if(cleanedObj == planeScanner.maxSpawns)
        {
            confettiObj.SetActive(true);
            gameOverObj.SetActive(true);
        }
    }

    public void RestartGame()
    {
        StartCoroutine(RestartRoutine());
    }

    public void QuitGame()
    {
        Application.Quit();
    }

    IEnumerator RestartRoutine()
    {
        confettiObj.SetActive(false);
        gameOverObj.SetActive(false);
        promptScanSurfaces.SetActive(false); 
        // find AR managers
        var arSession = FindObjectOfType<ARSession>();
        var planeManager = FindObjectOfType<ARPlaneManager>();
        var raycastManager = FindObjectOfType<ARRaycastManager>();
        var pointCloudManager = FindObjectOfType<ARPointCloudManager>();


        // 1) disable managers to avoid updates while cleaning
        if (planeManager != null) planeManager.enabled = false;
        if (raycastManager != null) raycastManager.enabled = false;
        if (pointCloudManager != null) pointCloudManager.enabled = false;

        // wait a frame
        yield return null;

        // 2) destroy existing plane GameObjects created by ARPlaneManager
        if (planeManager != null)
        {
            // build a list by iterating the trackables (safe across ARFoundation versions)
            var planes = new List<ARPlane>();
            foreach (var p in planeManager.trackables)
            {
                if (p != null) planes.Add(p);
            }

            // destroy plane gameobjects
            foreach (var p in planes)
            {
                if (p != null && p.gameObject != null)
                    Destroy(p.gameObject);
            }
        }

        // 3) clear spawner's spawned objects and bin (if we have a spawner)
        if (planeScanner != null)
        {
            planeScanner.ForceClearAllSpawned();
        }
        else
        {
            // fallback: destroy any tagged objects if your project relies on tags
            foreach (var g in GameObject.FindGameObjectsWithTag("Garbage"))
                Destroy(g);
            var bin = GameObject.FindGameObjectWithTag("Bin");
            if (bin != null) Destroy(bin);
        }

        // wait a frame to allow destruction to complete
        yield return null;

        // 4) reset AR session (clears anchors & tracking state)
        if (arSession != null)
        {
            // instance Reset (some ARFoundation versions require instance)
            arSession.Reset();
        }
        else
        {
            Debug.LogWarning("[ARRestartManager] ARSession not found; cannot call Reset().");
        }

        // also toggle ARSession component if present to force a restart sequence
        if (arSession != null)
        {
            arSession.enabled = false;
            yield return null;
            arSession.enabled = true;
        }

        // small wait
        yield return null;

        // 5) re-enable managers so detection starts again
        if (planeManager != null) planeManager.enabled = true;
        if (raycastManager != null) raycastManager.enabled = true;
        if (pointCloudManager != null) pointCloudManager.enabled = true;

        // 6) reset spawner internal state so it'll spawn again when threshold met
        if (planeScanner != null) planeScanner.ResetSpawnerState();
        planeScanner.spawnedObjs = 0;
        cleanedObj = 0;
        scoreUi.text = "Garbage Cleaned: " + cleanedObj + " / " + planeScanner.maxSpawns;
        Debug.Log("[ARRestartManager] Soft restart complete — scanning restarted.");
        promptScanSurfaces.SetActive(true);
        yield break;
    }
}
