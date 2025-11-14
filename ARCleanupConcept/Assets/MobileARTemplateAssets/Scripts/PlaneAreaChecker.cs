using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

[RequireComponent(typeof(ARPlaneManager))]
public class PlaneAreaWithBinSpawner : MonoBehaviour
{
    public enum SpawnTriggerMode { SinglePlane, TotalArea }

    [Header("References")]
    public ARPlaneManager arPlaneManager;
    public Camera arCamera; // optional, for orientation of spawned objects

    [Header("Bin Settings (spawned first)")]
    public GameObject binPrefab;
    [Tooltip("Vertical offset above plane in meters")]
    public float binYOffset = 0.02f;
    [Tooltip("Minimum radius around bin (meters) where garbage will NOT spawn")]
    public float binClearRadius = 0.6f;

    [Header("Spawn Settings")]
    public GameObject[] objectPrefabs;    // trash prefabs
    public SpawnTriggerMode spawnMode = SpawnTriggerMode.SinglePlane;
    [Tooltip("Square meters")]
    public float areaThreshold = 1.0f;    // m^2
    public int maxSpawns = 10;
    public bool autoSpawnOnce = true;     // spawn once when threshold met

    [Header("Placement Constraints")]
    [Tooltip("Minimum distance (meters) between spawned garbage items")]
    public float minDistanceBetweenObjects = 0.25f;
    [Tooltip("How many random samples we try per object before giving up")]
    public int maxPlacementAttemptsPerItem = 20;

    // internal
    bool hasSpawned = false;            // used to gate initial spawn (bin + first wave)
    GameObject spawnedBin;
    List<Vector3> placedPositions = new List<Vector3>();
    List<GameObject> spawnedObjects = new List<GameObject>();
    float previousTotalArea = 0f;

    void Reset()
    {
        arPlaneManager = GetComponent<ARPlaneManager>();
    }

    void OnEnable()
    {
        if (arPlaneManager == null) arPlaneManager = GetComponent<ARPlaneManager>();
        arPlaneManager.planesChanged += OnPlanesChanged;
    }

    void OnDisable()
    {
        if (arPlaneManager != null) arPlaneManager.planesChanged -= OnPlanesChanged;
    }

    void OnPlanesChanged(ARPlanesChangedEventArgs args)
    {
        // Clean up list from destroyed items (so we can top up)
        CleanSpawnedList();

        // compute areas (both total and best single plane)
        float totalHorizontalArea = 0f;
        ARPlane bestPlane = null;
        float bestPlaneArea = 0f;

        foreach (var plane in arPlaneManager.trackables)
        {
            if (plane.alignment != UnityEngine.XR.ARSubsystems.PlaneAlignment.HorizontalUp)
                continue;

            float area = ComputePolygonArea(plane.boundary);
            totalHorizontalArea += area;
            if (area > bestPlaneArea)
            {
                bestPlaneArea = area;
                bestPlane = plane;
            }
        }

        bool thresholdMet = (spawnMode == SpawnTriggerMode.TotalArea)
            ? totalHorizontalArea >= areaThreshold
            : (bestPlane != null && bestPlaneArea >= areaThreshold);

        Debug.Log($"[PlaneSpawner] totalArea: {totalHorizontalArea:F2}m², bestPlane: {bestPlaneArea:F2}m², thresholdMet: {thresholdMet}, spawnedCount: {spawnedObjects.Count}");

        // Condition to trigger spawning:
        // - threshold met AND
        //   (a) total area increased since last check OR
        //   (b) we currently have fewer spawned objects than maxSpawns
        bool areaIncreased = totalHorizontalArea > previousTotalArea + 0.001f; // tiny hysteresis
        bool needsMore = spawnedObjects.Count < maxSpawns;

        if (thresholdMet && (areaIncreased || needsMore))
        {
            // spawn bin first if not present
            if (spawnedBin == null && bestPlane != null)
            {
                Vector3 binWorldPos = ComputePlaneCenterWorld(bestPlane);
                binWorldPos += Vector3.up * binYOffset;
                SpawnBinAt(binWorldPos, bestPlane.transform.rotation);
            }

            // start filling until filled or attempts exhausted
            StartCoroutine(TryFillSpawnsUntilMax(bestPlane));
        }

        previousTotalArea = totalHorizontalArea;
    }

    IEnumerator TryFillSpawnsUntilMax(ARPlane hintPlane)
    {
        // Attempt to spawn additional items until we reach maxSpawns or attempts exhausted.
        int target = maxSpawns;
        int safeLoopGuard = 0;
        int maxTotalAttempts = Mathf.Max(200, maxSpawns * 30); // overall attempts cap to avoid infinite loops

        while (spawnedObjects.Count < target && safeLoopGuard < maxTotalAttempts)
        {
            safeLoopGuard++;

            // Try to spawn one item using either SinglePlane (prefer hintPlane) or AcrossPlanes
            bool spawnedOne = false;

            if (spawnMode == SpawnTriggerMode.SinglePlane && hintPlane != null)
            {
                // try a few placements on the hintPlane
                var boundary = CopyBoundaryToArray(hintPlane.boundary);
                var bounds = GetBounds(boundary);

                for (int attempt = 0; attempt < maxPlacementAttemptsPerItem && !spawnedOne; ++attempt)
                {
                    Vector2 sample = new Vector2(
                        UnityEngine.Random.Range(bounds.min.x, bounds.max.x),
                        UnityEngine.Random.Range(bounds.min.y, bounds.max.y)
                    );
                    if (!PointInPolygon(sample, boundary)) continue;

                    Vector3 localPos = new Vector3(sample.x, 0f, sample.y);
                    Vector3 worldPos = hintPlane.transform.TransformPoint(localPos + Vector3.up * 0.02f);

                    if (spawnedBin != null && Vector3.Distance(worldPos, spawnedBin.transform.position) < binClearRadius)
                        continue;

                    var go = SpawnRandomPrefabAt(worldPos, hintPlane.transform.rotation);
                    if (go != null) spawnedOne = true;
                }
            }
            else
            {
                // spawn across tracked planes, choose weighted by area
                var planes = new List<(ARPlane plane, float area)>();
                float totalArea = 0f;
                foreach (var p in arPlaneManager.trackables)
                {
                    if (p.alignment != UnityEngine.XR.ARSubsystems.PlaneAlignment.HorizontalUp) continue;
                    float a = ComputePolygonArea(p.boundary);
                    if (a <= 0.001f) continue;
                    planes.Add((p, a));
                    totalArea += a;
                }

                if (planes.Count == 0) break;

                // pick plane weighted by area
                float r = UnityEngine.Random.Range(0f, totalArea);
                float acc = 0f;
                ARPlane chosen = planes[0].plane;
                foreach (var t in planes)
                {
                    acc += t.area;
                    if (r <= acc) { chosen = t.plane; break; }
                }

                var boundary = CopyBoundaryToArray(chosen.boundary);
                var bounds = GetBounds(boundary);

                for (int attempt = 0; attempt < maxPlacementAttemptsPerItem && !spawnedOne; ++attempt)
                {
                    Vector2 sample = new Vector2(
                        UnityEngine.Random.Range(bounds.min.x, bounds.max.x),
                        UnityEngine.Random.Range(bounds.min.y, bounds.max.y)
                    );
                    if (!PointInPolygon(sample, boundary)) continue;

                    Vector3 localPos = new Vector3(sample.x, 0f, sample.y);
                    Vector3 worldPos = chosen.transform.TransformPoint(localPos + Vector3.up * 0.02f);

                    if (spawnedBin != null && Vector3.Distance(worldPos, spawnedBin.transform.position) < binClearRadius)
                        continue;

                    var go = SpawnRandomPrefabAt(worldPos, chosen.transform.rotation);
                    if (go != null) spawnedOne = true;
                }
            }

            if (!spawnedOne)
            {
                // If we couldn't spawn this iteration, yield a frame to let planes update or reduce CPU spin
                yield return null;
            }
            else
            {
                // small delay to spread instantiation (improves UX)
                yield return new WaitForSeconds(0.05f);
            }
        }

        yield break;
    }

    // compute an approximate center in plane local space using axis-aligned bounds center
    Vector3 ComputePlaneCenterWorld(ARPlane plane)
    {
        var boundary = CopyBoundaryToArray(plane.boundary);
        var bounds = GetBounds(boundary);
        Vector2 center2 = (bounds.min + bounds.max) * 0.5f;
        Vector3 local = new Vector3(center2.x, 0f, center2.y);
        return plane.transform.TransformPoint(local);
    }

    void SpawnBinAt(Vector3 worldPos, Quaternion planeRot)
    {
        if (binPrefab == null) return;
        if (spawnedBin != null) Destroy(spawnedBin);

        spawnedBin = Instantiate(binPrefab, worldPos, Quaternion.identity);

        // Optionally face the camera horizontally
        if (arCamera != null)
        {
            Vector3 lookTarget = new Vector3(arCamera.transform.position.x, spawnedBin.transform.position.y, arCamera.transform.position.z);
            spawnedBin.transform.LookAt(lookTarget);
        }
    }

    // spawn helper: returns instantiated GameObject or null on failure
    GameObject SpawnRandomPrefabAt(Vector3 worldPos, Quaternion planeRot)
    {
        if (objectPrefabs == null || objectPrefabs.Length == 0) return null;

        var prefab = objectPrefabs[UnityEngine.Random.Range(0, objectPrefabs.Length)];
        float radius = EstimatePrefabClearRadius(prefab);

        // Try jittered placements near worldPos
        Vector3 chosenPos = Vector3.zero;
        bool placed = false;
        for (int attempt = 0; attempt < maxPlacementAttemptsPerItem; ++attempt)
        {
            Vector3 sample = worldPos + (Vector3)(UnityEngine.Random.insideUnitCircle * 0.15f);
            sample.y = worldPos.y;
            if (!CanPlaceAt(sample, radius)) continue;
            chosenPos = sample;
            placed = true;
            break;
        }

        if (!placed)
        {
            if (!CanPlaceAt(worldPos, radius)) return null;
            chosenPos = worldPos;
        }

        var go = Instantiate(prefab, chosenPos, Quaternion.identity);

        // optional: orient toward camera
        if (arCamera != null)
        {
            Vector3 lookTarget = new Vector3(arCamera.transform.position.x, go.transform.position.y, arCamera.transform.position.z);
            go.transform.LookAt(lookTarget);
        }

        // add to tracking lists
        spawnedObjects.Add(go);
        placedPositions.Add(go.transform.position);

        // optionally hold kinematic for a short time to avoid physics jitter
        var rb = go.GetComponent<Rigidbody>();
        if (rb != null) StartCoroutine(TemporarilyHoldKinematic(rb, 0.06f));

        return go;
    }

    IEnumerator TemporarilyHoldKinematic(Rigidbody r, float holdTime)
    {
        bool wasKinematic = r.isKinematic;
        r.isKinematic = true;
        yield return new WaitForSeconds(holdTime);
        // only change if object still exists
        if (r != null) r.isKinematic = wasKinematic ? true : false;
    }

    // Keep track of placed positions for simple distance checks
    bool CanPlaceAt(Vector3 worldPos, float requiredRadius)
    {
        float minDist = minDistanceBetweenObjects + requiredRadius; // add prefab radius
        for (int i = 0; i < placedPositions.Count; ++i)
        {
            if (Vector3.Distance(worldPos, placedPositions[i]) < minDist)
                return false;
        }

        if (spawnedBin != null)
        {
            if (Vector3.Distance(worldPos, spawnedBin.transform.position) < binClearRadius + requiredRadius)
                return false;
        }

        return true;
    }

    // Estimate a radius (in meters) for overlap checks using the prefab's collider bounds
    float EstimatePrefabClearRadius(GameObject prefab)
    {
        var col = prefab.GetComponentInChildren<Collider>();
        if (col != null)
        {
            var bounds = col.bounds;
            float radius = bounds.extents.magnitude * 0.5f;
            radius = Mathf.Clamp(radius, 0.05f, 0.6f);
            return radius;
        }
        return 0.12f;
    }

    // remove null references from spawnedObjects and keep placedPositions in sync
    void CleanSpawnedList()
    {
        // remove null objects and corresponding placedPositions
        for (int i = spawnedObjects.Count - 1; i >= 0; --i)
        {
            if (spawnedObjects[i] == null)
            {
                // remove both lists' entries if they line up
                if (i < placedPositions.Count) placedPositions.RemoveAt(i);
                spawnedObjects.RemoveAt(i);
            }
        }

        // If counts mismatch (defensive), rebuild placedPositions from existing spawnedObjects
        if (placedPositions.Count != spawnedObjects.Count)
        {
            placedPositions.Clear();
            foreach (var g in spawnedObjects)
            {
                if (g != null) placedPositions.Add(g.transform.position);
            }
        }
    }

    // --- Utility: compute polygon area (boundary provided as NativeArray<Vector2>) ---
    float ComputePolygonArea(NativeArray<Vector2> boundary)
    {
        if (boundary.Length < 3) return 0f;
        double area = 0.0;
        for (int i = 0; i < boundary.Length; ++i)
        {
            int j = (i + 1) % boundary.Length;
            Vector2 vi = boundary[i];
            Vector2 vj = boundary[j];
            area += (vi.x * vj.y) - (vj.x * vi.y);
        }
        return Mathf.Abs((float)(area * 0.5));
    }

    Vector2[] CopyBoundaryToArray(NativeArray<Vector2> boundary)
    {
        Vector2[] arr = new Vector2[boundary.Length];
        for (int i = 0; i < boundary.Length; ++i) arr[i] = boundary[i];
        return arr;
    }

    struct SimpleBounds { public Vector2 min, max; }
    SimpleBounds GetBounds(Vector2[] poly)
    {
        Vector2 min = poly[0], max = poly[0];
        for (int i = 1; i < poly.Length; ++i)
        {
            min = Vector2.Min(min, poly[i]);
            max = Vector2.Max(max, poly[i]);
        }
        return new SimpleBounds { min = min, max = max };
    }

    bool PointInPolygon(Vector2 point, Vector2[] polygon)
    {
        int windingNumber = 0;
        for (int i = 0; i < polygon.Length; ++i)
        {
            int j = (i + 1) % polygon.Length;
            Vector2 vi = polygon[i];
            Vector2 vj = polygon[j];

            if (vi.y <= point.y)
            {
                if (vj.y > point.y)
                {
                    if (Cross(vj - vi, point - vi) < 0f)
                        ++windingNumber;
                }
            }
            else
            {
                if (vj.y <= point.y)
                {
                    if (Cross(vj - vi, point - vi) > 0f)
                        --windingNumber;
                }
            }
        }
        return windingNumber != 0;
    }

    static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
}
