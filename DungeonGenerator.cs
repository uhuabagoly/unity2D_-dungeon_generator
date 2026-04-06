using System.Collections.Generic;
using UnityEngine;

public class DungeonGenerator : MonoBehaviour
{
    [Header("Dungeon Settings")]
    public GameObject[] normalRoomPrefabs;
    public GameObject[] complexRoomPrefabs;
    public GameObject[] specialRoomPrefabs;

    [Header("Chances and Limits")]
    [Range(0f, 1f)] public float specialRoomChance = 0.1f;
    [Range(0f, 1f)] public float complexRoomChance = 0.3f;

    public int minTotalRooms = 50;
    public float generationSpeed = 0.5f;

    [Header("Positioning Settings")]
    public Vector2 fallbackRoomSize = new Vector2(45f, 45f);
    private const float CONNECTION_THRESHOLD = 10f;
    private const int COLLISION_RETRY_LIMIT = 5;

    // Room tracking
    private List<GameObject> rooms = new List<GameObject>();
    private GameObject lastRoom;
    private GameObject firstRoom;
    private GameObject farthestRoom; // NEW: To track the goal room
    private int roomCount = 0;
    private int specialRoomCount = 0;

    // Connection points management
    private List<GameObject> freePassThroughObjects = new List<GameObject>();
    private bool allPassThroughPointsExhausted = false;

    // Track original positions of PassThrough points
    private Dictionary<GameObject, Vector3> passThroughOriginalPositions = new Dictionary<GameObject, Vector3>();

    // Track failed attempts for each PassThrough point
    private Dictionary<GameObject, int> passThroughFailedAttempts = new Dictionary<GameObject, int>();

    void Start()
    {
        Debug.Log("Dungeon generation started");
        InvokeRepeating(nameof(GenerateRoom), 0f, generationSpeed);
    }

    void Update()
    {
        if (allPassThroughPointsExhausted && IsInvoking(nameof(GenerateRoom)))
        {
            CancelInvoke(nameof(GenerateRoom));
            CleanupPassThroughPoints();
            FindFarthestRoom(); // NEW: Find the farthest room after cleanup
            Debug.Log("Dungeon generation completed");
        }
    }

    void GenerateRoom()
    {
        // Check if we've reached limits
        if (roomCount >= minTotalRooms || allPassThroughPointsExhausted)
        {
            // Before stopping, try to create branches from all remaining passThrough points
            CreateBranchesFromAllPassThroughPoints();
            allPassThroughPointsExhausted = true;
            return;
        }

        // Create first room if none exists
        if (firstRoom == null)
        {
            CreateFirstRoom();
            return;
        }

        // Determine room type to create
        GameObject roomPrefab = SelectRoomPrefab();
        GameObject newRoom = Instantiate(roomPrefab, Vector3.zero, Quaternion.identity, transform);

        if (!PositionAndConnectRoom(newRoom))
        {
            Destroy(newRoom);
            HandleGenerationFailure();
            return;
        }

        // Finalize room placement
        rooms.Add(newRoom);
        lastRoom = newRoom;
        roomCount++;

        // Debug log when adding a new room
        Debug.Log($"Added new room: {newRoom.name} (Room count: {roomCount})");

        // Track special rooms
        if (IsSpecialRoom(newRoom))
            specialRoomCount++;

        // Handle branch creation with increasing probability
        float branchChance = Mathf.Clamp01((float)roomCount / 15);
        if (UnityEngine.Random.value < branchChance)
        {
            CollectFreePassThroughPoints();
        }

        if (IsNormalRoom(newRoom))
        {
            Room newRoomScript = newRoom.GetComponent<Room>();
            foreach (GameObject point in newRoomScript.passThroughObjects)
            {
                if (!freePassThroughObjects.Contains(point))
                {
                    freePassThroughObjects.Add(point);
                }
            }
        }
    }

    bool IsNormalRoom(GameObject room)
    {
        foreach (GameObject normalPrefab in normalRoomPrefabs)
        {
            if (room.name.StartsWith(normalPrefab.name))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Creates branches from all remaining passThrough points before stopping generation
    /// </summary>
    void CreateBranchesFromAllPassThroughPoints()
    {
        Debug.Log("Attempting to create branches from all remaining passThrough points before stopping generation");

        // Collect all current free passThrough points
        CollectFreePassThroughPoints();

        // Keep track of how many branches we've tried to create
        int branchesCreated = 0;
        int maxBranchAttempts = freePassThroughObjects.Count * 2;
        int attempts = 0;

        // Continue creating branches until we've used all passThrough points or hit the attempt limit
        // OR until we've reached the minimum number of rooms
        while (freePassThroughObjects.Count > 0 && attempts < maxBranchAttempts && roomCount < minTotalRooms)
        {
            Debug.Log($"Creating branch from remaining passThrough points. Points left: {freePassThroughObjects.Count}");

            GameObject roomPrefab = SelectRoomPrefab();
            GameObject newRoom = Instantiate(roomPrefab, Vector3.zero, Quaternion.identity, transform);

            if (PositionAndConnectRoom(newRoom))
            {
                // Successfully created a room on this branch
                rooms.Add(newRoom);
                lastRoom = newRoom;
                roomCount++;
                branchesCreated++;

                Debug.Log($"Created branch room: {newRoom.name} (Branch #{branchesCreated})");
            }
            else
            {
                // Failed to create room on this branch, clean up
                Destroy(newRoom);
                Debug.Log("Failed to create room on branch");

                // Increment failed attempts counter for the passThrough point used in this attempt
                // We'll track which point was used in the attempt
                if (lastRoom != null)
                {
                    Room lastRoomScript = lastRoom.GetComponent<Room>();
                    if (lastRoomScript != null)
                    {
                        // Find the point that was used for this attempt and increment its counter
                        foreach (GameObject point in lastRoomScript.passThroughObjects)
                        {
                            if (freePassThroughObjects.Contains(point))
                            {
                                // Increment the failed attempts counter for this point
                                if (passThroughFailedAttempts.ContainsKey(point))
                                {
                                    passThroughFailedAttempts[point]++;
                                }
                                else
                                {
                                    passThroughFailedAttempts[point] = 1;
                                }

                                // Check if this point has exceeded the attempt limit (3 complex + all normal)
                                int complexAttempts = 3;
                                int normalAttempts = normalRoomPrefabs.Length; // All normal room prefabs
                                int maxAttempts = complexAttempts + normalAttempts;

                                if (passThroughFailedAttempts[point] >= maxAttempts)
                                {
                                    // Remove this point from all tracking
                                    RemoveUnusablePassThroughPoint(point, lastRoomScript);
                                }
                                break; // Only increment for one point per failed attempt
                            }
                        }
                    }
                }
            }

            // Collect remaining free passThrough points for next iteration
            CollectFreePassThroughPoints();
            attempts++;
        }

        Debug.Log($"Finished creating branches from passThrough points. Created {branchesCreated} additional branches.");
    }

    void CreateFirstRoom()
    {
        if (complexRoomPrefabs.Length == 0) return;

        GameObject prefab = complexRoomPrefabs[UnityEngine.Random.Range(0, complexRoomPrefabs.Length)];
        firstRoom = Instantiate(prefab, Vector3.zero, Quaternion.identity, transform);
        lastRoom = firstRoom;
        rooms.Add(firstRoom);
        roomCount++;

        // Store original positions of PassThrough points
        Room roomScript = firstRoom.GetComponent<Room>();
        foreach (GameObject point in roomScript.passThroughObjects)
        {
            passThroughOriginalPositions[point] = point.transform.position;
        }
    }

    GameObject SelectRoomPrefab()
    {
        // Check if we can create a special room
        bool canCreateSpecial = specialRoomPrefabs.Length > 0;
        bool shouldCreateSpecial = UnityEngine.Random.value < specialRoomChance;

        if (canCreateSpecial && shouldCreateSpecial)
            return specialRoomPrefabs[UnityEngine.Random.Range(0, specialRoomPrefabs.Length)];

        // Check if we should create a complex room
        bool shouldCreateComplex = UnityEngine.Random.value < complexRoomChance;
        if (shouldCreateComplex && complexRoomPrefabs.Length > 0)
            return complexRoomPrefabs[UnityEngine.Random.Range(0, complexRoomPrefabs.Length)];

        // Default to normal room
        return normalRoomPrefabs[UnityEngine.Random.Range(0, normalRoomPrefabs.Length)];
    }

    bool PositionAndConnectRoom(GameObject newRoom)
    {
        Room newRoomScript = newRoom.GetComponent<Room>();
        Room lastRoomScript = lastRoom.GetComponent<Room>();

        if (newRoomScript == null || lastRoomScript == null)
            return false;

        if (newRoomScript.passThroughObjects.Count == 0 || lastRoomScript.passThroughObjects.Count == 0)
            return false;

        // Try to connect the new room to the last room
        bool isConnected = ConnectRoomToLastRoom(newRoom, lastRoom);

        if (isConnected)
        {
            // Remove connected PassThrough points from free list
            foreach (GameObject point in newRoomScript.passThroughObjects)
            {
                if (freePassThroughObjects.Contains(point))
                {
                    freePassThroughObjects.Remove(point);
                }
            }
        }

        return isConnected;
    }

    bool ConnectRoomToLastRoom(GameObject newRoom, GameObject lastRoomObject)
    {
        Room newRoomScript = newRoom.GetComponent<Room>();
        Room lastRoomScript = lastRoomObject.GetComponent<Room>();

        // Try different rotations to find a valid connection
        for (int rotationAttempt = 0; rotationAttempt < 4; rotationAttempt++)
        {
            // Try to connect each passThrough point
            foreach (GameObject lastPoint in lastRoomScript.passThroughObjects)
            {
                Vector3 newPosition = CalculateRoomPosition(newRoom, lastRoomObject, lastPoint);
                newRoom.transform.position = newPosition;

                // Check if any connection points align
                foreach (GameObject newPoint in newRoomScript.passThroughObjects)
                {
                    if (Vector3.Distance(newPoint.transform.position, lastPoint.transform.position) <= CONNECTION_THRESHOLD)
                    {
                        // Successfully connected - rotate the new room 180 degrees around the connection point
                        // The rotation must be around the fixed point (lastPoint) to ensure the connection remains aligned.
                        RotateRoomAroundConnectionPoint(newRoom, lastPoint);

                        // Remove the used points only after successful connection and collision check
                        if (!CheckRoomCollision(newRoom))
                        {
                            RemoveConnectedPassThroughPoints(lastRoomScript, newRoomScript, lastPoint, newPoint);

                            // Reset failed attempts counter for this point since it was successful
                            if (passThroughFailedAttempts.ContainsKey(lastPoint))
                            {
                                passThroughFailedAttempts[lastPoint] = 0;
                            }

                            return true; // Success!
                        }
                        else
                        {
                            Debug.Log($"Collision detected at passThrough points: {lastPoint.name} and {newPoint.name}");
                        }
                    }
                }
            }

            // Rotate 90 degrees and try again
            newRoom.transform.Rotate(0, 0, 90);
        }

        Debug.Log("Failed to connect room to last room");
        return false; // Failed to connect
    }

    void RotateRoomAroundConnectionPoint(GameObject room, GameObject connectionPoint)
    {
        // Rotate the room 180 degrees around the connection point's origin
        Vector3 connectionPosition = connectionPoint.transform.position;
        room.transform.RotateAround(connectionPosition, Vector3.forward, 180f);
    }

    Vector3 CalculateRoomPosition(GameObject newRoom, GameObject lastRoom, GameObject connectionPoint)
    {
        Vector2 lastRoomSize = GetRoomSize(lastRoom);
        Vector2 newRoomSize = GetRoomSize(newRoom);

        Vector3 direction = connectionPoint.transform.position - lastRoom.transform.position;
        Vector3 newPosition = lastRoom.transform.position;

        if (Mathf.Abs(direction.x) > Mathf.Abs(direction.y))
        {
            // Horizontal connection
            float offset = (lastRoomSize.x + newRoomSize.x) * 0.5f;
            newPosition.x += offset * Mathf.Sign(direction.x);
        }
        else
        {
            // Vertical connection
            float offset = (lastRoomSize.y + newRoomSize.y) * 0.5f;
            newPosition.y += offset * Mathf.Sign(direction.y);
        }

        return newPosition;
    }

    void RemoveConnectedPassThroughPoints(Room roomA, Room roomB, GameObject pointA, GameObject pointB)
    {
        if (roomA == null || roomB == null || pointA == null || pointB == null)
        {
            Debug.LogWarning("Attempted to remove null passThrough points");
            return;
        }

        Debug.Log($"Removing connected points: {pointA.name} and {pointB.name}");

        // Remove pointA from roomA
        if (roomA.passThroughObjects.Contains(pointA))
        {
            roomA.passThroughObjects.Remove(pointA);
            Debug.Log($"Removed {pointA.name} from roomA");
        }
        else
        {
            Debug.LogWarning($"{pointA.name} not found in roomA's passThroughObjects");
        }

        // Remove pointB from roomB
        if (roomB.passThroughObjects.Contains(pointB))
        {
            roomB.passThroughObjects.Remove(pointB);
            Debug.Log($"Removed {pointB.name} from roomB");
        }
        else
        {
            Debug.LogWarning($"{pointB.name} not found in roomB's passThroughObjects");
        }

        // Remove from freePassThroughObjects if they're there
        if (freePassThroughObjects.Contains(pointA))
        {
            freePassThroughObjects.Remove(pointA);
            Debug.Log($"Removed {pointA.name} from freePassThroughObjects");
        }

        if (freePassThroughObjects.Contains(pointB))
        {
            freePassThroughObjects.Remove(pointB);
            Debug.Log($"Removed {pointB.name} from freePassThroughObjects");
        }

        // Remove from failed attempts tracking
        if (passThroughFailedAttempts.ContainsKey(pointA))
        {
            passThroughFailedAttempts.Remove(pointA);
        }

        if (passThroughFailedAttempts.ContainsKey(pointB))
        {
            passThroughFailedAttempts.Remove(pointB);
        }

        // Destroy only the PassThroughPoints that were used to create the connection
        Destroy(pointA);
        Destroy(pointB);
    }

    // New method to remove unusable PassThrough points
    void RemoveUnusablePassThroughPoint(GameObject point, Room roomScript)
    {
        if (point == null || roomScript == null) return;

        Debug.Log($"Removing unusable PassThrough point: {point.name} after too many failed attempts");

        // Remove from room's passThroughObjects
        if (roomScript.passThroughObjects.Contains(point))
        {
            roomScript.passThroughObjects.Remove(point);
        }

        // Remove from freePassThroughObjects
        if (freePassThroughObjects.Contains(point))
        {
            freePassThroughObjects.Remove(point);
        }

        // Remove from failed attempts tracking
        if (passThroughFailedAttempts.ContainsKey(point))
        {
            passThroughFailedAttempts.Remove(point);
        }

        // Destroy the point
        Destroy(point);
    }

    bool CheckRoomCollision(GameObject room)
    {
        Vector2 roomSize = GetRoomSize(room);

        // Temporarily disable the room's collider for self-check
        Collider2D roomCollider = room.GetComponent<Collider2D>();
        bool wasColliderEnabled = false;
        if (roomCollider != null)
        {
            wasColliderEnabled = roomCollider.enabled;
            roomCollider.enabled = false;
        }

        Collider2D[] overlaps = Physics2D.OverlapBoxAll(room.transform.position, roomSize, 0f);

        // Re-enable the collider
        if (roomCollider != null)
            roomCollider.enabled = wasColliderEnabled;

        foreach (var collider in overlaps)
        {
            if (rooms.Contains(collider.gameObject))
                return true;
        }

        return false;
    }

    Vector2 GetRoomSize(GameObject room)
    {
        BoxCollider2D boxCollider = room.GetComponent<BoxCollider2D>();
        return boxCollider != null ? boxCollider.size : fallbackRoomSize;
    }

    bool IsSpecialRoom(GameObject room)
    {
        foreach (GameObject specialPrefab in specialRoomPrefabs)
        {
            if (room.name.StartsWith(specialPrefab.name))
                return true;
        }
        return false;
    }

    void CollectFreePassThroughPoints()
    {
        freePassThroughObjects.Clear();

        Debug.Log("Collecting free passThrough points...");

        foreach (GameObject room in rooms)
        {
            if (room == null) continue;

            Room roomScript = room.GetComponent<Room>();
            if (roomScript == null) continue;

            // Create a copy of the passThroughObjects list to avoid modification during iteration
            List<GameObject> passThroughCopy = new List<GameObject>(roomScript.passThroughObjects);

            foreach (GameObject point in passThroughCopy)
            {
                // Check if the point still exists and is active
                if (point != null && point.activeInHierarchy)
                {
                    // Check if it's not already in the list
                    if (!freePassThroughObjects.Contains(point))
                    {
                        freePassThroughObjects.Add(point);
                        Debug.Log($"Collected free PassThrough point: {point.name} at position: {point.transform.position}");
                    }
                }
                else
                {
                    Debug.LogWarning($"Found missing PassThrough point in room: {room.name}");
                    // Restore the missing point
                    RestoreMissingPassThroughPoint(roomScript, point);
                }
            }
        }

        Debug.Log($"Total free PassThrough points collected: {freePassThroughObjects.Count}");
    }

    void RestoreMissingPassThroughPoint(Room roomScript, GameObject missingPoint)
    {
        if (missingPoint == null || !passThroughOriginalPositions.ContainsKey(missingPoint))
        {
            Debug.LogError("Cannot restore missing PassThrough point: original position not found");
            return;
        }

        // Create a new PassThrough point at the original position
        GameObject newPoint = new GameObject("Restored_PassThrough");
        newPoint.transform.position = passThroughOriginalPositions[missingPoint];
        newPoint.transform.parent = roomScript.gameObject.transform;

        // Add the new point to the room's passThroughObjects list
        roomScript.passThroughObjects.Add(newPoint);
        Debug.Log($"Restored missing PassThrough point in room: {roomScript.gameObject.name}");
    }

    void HandleGenerationFailure()
    {
        Debug.Log("Handling generation failure...");

        // Check if there are any free passThrough points left
        CollectFreePassThroughPoints();

        if (freePassThroughObjects.Count > 0)
        {
            // Select a random free pass-through point and try to build from there
            int randomIndex = UnityEngine.Random.Range(0, freePassThroughObjects.Count);
            GameObject pointToBuildFrom = freePassThroughObjects[randomIndex];

            // Find the room that owns the point
            GameObject ownerRoom = GetOwnerRoom(pointToBuildFrom);
            if (ownerRoom != null)
            {
                // Set the owner room as the last room to try and connect to it
                lastRoom = ownerRoom;
                Debug.Log($"Generation failed, trying to build from room: {ownerRoom.name}");
            }
            else
            {
                Debug.LogWarning("Could not find owner room for the selected pass-through point.");
            }
        }
        else
        {
            // No free points left, stop generation
            allPassThroughPointsExhausted = true;
            Debug.Log("No free pass-through points left. Stopping generation.");
        }
    }

    /// <summary>
    /// Attempts to close all remaining free PassThrough points with a normal room prefab.
    /// </summary>
    void CleanupPassThroughPoints()
    {
        Debug.Log("Starting CleanupPassThroughPoints...");

        // Re-collect all free points to ensure the list is up-to-date
        CollectFreePassThroughPoints();

        // Create a copy of the list to iterate over, as the original list will be modified
        List<GameObject> pointsToClose = new List<GameObject>(freePassThroughObjects);

        foreach (GameObject point in pointsToClose)
        {
            // Check if the point has already been closed by a previous iteration
            if (point == null) continue;

            bool closedSuccessfully = false;

            // Find the room that owns the point
            GameObject ownerRoom = GetOwnerRoom(point);
            if (ownerRoom == null)
            {
                Debug.LogWarning($"Could not find owner room for PassThrough point at position {point.transform.position}. Skipping cleanup for this point.");
                continue;
            }

            // Try to close the point with every normal room prefab
            for (int i = 0; i < normalRoomPrefabs.Length; i++)
            {
                GameObject roomPrefab = normalRoomPrefabs[i];
                GameObject newRoom = Instantiate(roomPrefab, Vector3.zero, Quaternion.identity, transform);

                // Set the owner room as the last room for the connection logic
                lastRoom = ownerRoom;

                // Attempt to connect the new room to the owner room using the standard logic
                // This logic handles rotation, positioning, collision check, and point removal.
                if (ConnectRoomToLastRoom(newRoom, ownerRoom))
                {
                    // Successfully connected and placed the closing room
                    rooms.Add(newRoom);
                    roomCount++;
                    closedSuccessfully = true;

                    // The ConnectRoomToLastRoom -> RemoveConnectedPassThroughPoints logic
                    // should have already destroyed the 'point' GameObject and removed it
                    // from the ownerRoom's list and freePassThroughObjects.

                    Debug.Log($"Successfully closed PassThrough point with room: {newRoom.name}");
                    break; // Move to the next PassThrough point
                }
                else
                {
                    // Failed to connect/collision, destroy the temporary room and try the next prefab
                    Destroy(newRoom);
                }
            }

            if (!closedSuccessfully)
            {
                Debug.LogWarning($"Could not close PassThrough point at position {point.transform.position} with any normal room prefab. Removing point.");
                // If all attempts fail, remove the point to prevent infinite loops or errors
                Room ownerRoomScript = ownerRoom.GetComponent<Room>();
                if (ownerRoomScript != null)
                {
                    RemoveUnusablePassThroughPoint(point, ownerRoomScript);
                }
            }
        }
        Debug.Log("CleanupPassThroughPoints completed.");
    }

    // Helper method to find the room that owns a PassThrough point
    GameObject GetOwnerRoom(GameObject passThroughPoint)
    {
        Transform current = passThroughPoint.transform.parent;
        while (current != null)
        {
            if (current.GetComponent<Room>() != null)
            {
                return current.gameObject;
            }
            current = current.parent;
        }
        return null;
    }

    // Helper method to find the room farthest from the first room
    void FindFarthestRoom()
    {
        if (firstRoom == null || rooms.Count == 0)
        {
            farthestRoom = null;
            return;
        }

        float maxDistance = 0f;
        farthestRoom = firstRoom; // Default to the first room

        Vector3 firstRoomPos = firstRoom.transform.position;

        foreach (GameObject room in rooms)
        {
            if (room == null) continue;

            float distance = Vector3.Distance(firstRoomPos, room.transform.position);

            if (distance > maxDistance)
            {
                maxDistance = distance;
                farthestRoom = room;
            }
        }

        if (farthestRoom != null)
        {
            Debug.Log($"Farthest room found: {farthestRoom.name} at distance: {maxDistance}");
        }
    }


    // Implement the CheckCollision method that was missing
    bool CheckCollision(GameObject newRoom)
    {
        BoxCollider2D newRoomCollider = newRoom.GetComponent<BoxCollider2D>();
        if (newRoomCollider == null) return false; // Cannot check collision without a collider

        foreach (GameObject existingRoom in rooms)
        {
            BoxCollider2D existingRoomCollider = existingRoom.GetComponent<BoxCollider2D>();
            if (existingRoomCollider == null) continue;

            // Check for overlap
            if (newRoomCollider.bounds.Intersects(existingRoomCollider.bounds))
            {
                return true; // Collision detected
            }
        }
        return false; // No collision
    }

    private void OnDrawGizmos()
    {
        // Draw first room in blue
        if (firstRoom != null)
        {
            Gizmos.color = Color.blue;
            BoxCollider2D col = firstRoom.GetComponent<BoxCollider2D>();
            Vector2 size = col != null ? col.size : fallbackRoomSize * 2f;
            Gizmos.DrawWireCube(firstRoom.transform.position, size);
        }

        // Draw farthest room in red (the goal room)
        if (farthestRoom != null && farthestRoom != firstRoom)
        {
            Gizmos.color = Color.red;
            BoxCollider2D col = farthestRoom.GetComponent<BoxCollider2D>();
            Vector2 size = col != null ? col.size : fallbackRoomSize * 2f;
            Gizmos.DrawWireCube(farthestRoom.transform.position, size);
        }

        // Draw free connection points in green
        Gizmos.color = Color.green;
        foreach (GameObject point in freePassThroughObjects)
        {
            if (point != null)
                Gizmos.DrawSphere(point.transform.position, 2f);
        }
    }
}
