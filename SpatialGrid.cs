using UdonSharp;
using UnityEngine;

/// <summary>
/// Uniform spatial hash grid used purely for interest management.
///
/// DESIGN NOTES
/// - Uniform grid / spatial hash instead of an octree: predictable cost, trivial
///   updates, and it stays allocation-free, which matters far more in Udon than
///   the tighter culling an octree would give.
/// - This class knows NOTHING about networking. It only answers
///   "which object ids are near this point?". SmartSyncManager turns that answer
///   into a relevant set.
/// - Ids are slot indices assigned by the caller and must be in [0, maxObjects).
///   Storing ids (not GameObject/component references) keeps every inner loop on
///   int/float arrays, which is the fastest thing Udon can do.
/// - Zero allocations after Initialize(). Queries write into a caller-owned
///   buffer, so no garbage is produced per frame.
///
/// TUNING (cellSize)
///   Small dense rooms    4 - 6 m
///   Medium social worlds 8 - 10 m
///   Large open maps      12 - 16 m
/// Start at 8 m and tune from the average object density.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SpatialGrid : UdonSharpBehaviour
{
    // ------------------------------------------------------------------
    // Configuration
    // ------------------------------------------------------------------

    [Tooltip("Edge length of one grid cell, in meters. 6-12 m suits most worlds; start at 8.")]
    public float cellSize = 8f;

    [Tooltip("Number of object slots. Ids passed to Add/Remove/UpdatePosition must be 0..maxObjects-1.")]
    public int maxObjects = 1024;

    [Tooltip("XZ-only mode: ignores height. Queries touch 9 cells instead of 27, so it is roughly 3x cheaper. Use it when gameplay is mostly horizontal.")]
    public bool use2D = false;

    // ------------------------------------------------------------------
    // Constants
    // ------------------------------------------------------------------

    // Power-of-two bucket count so the modulo collapses into a bitmask.
    private const int HASH_SIZE = 2048;
    private const int HASH_MASK = HASH_SIZE - 1;

    // Standard spatial-hash primes (Teschner et al.).
    private const int PRIME_X = 73856093;
    private const int PRIME_Y = 19349663;
    private const int PRIME_Z = 83492791;

    private const int NONE = -1;

    // ------------------------------------------------------------------
    // Storage
    //
    // Buckets hold an intrusive doubly-linked list of slot ids. "Doubly" is what
    // makes Remove/UpdatePosition O(1) instead of O(chain length), which matters
    // because UpdatePosition runs for every awake object.
    // ------------------------------------------------------------------

    private int[] bucketHead;   // bucketHead[b] = first slot id in bucket b, or NONE
    private int[] nextIndex;    // nextIndex[id] = next slot id in the same bucket, or NONE
    private int[] prevIndex;    // prevIndex[id] = previous slot id in the same bucket, or NONE

    private bool[] slotUsed;    // is this slot registered?
    private int[] slotBucket;   // cached bucket the slot currently lives in
    private int[] cellX;        // cached cell coordinates, used to reject hash collisions
    private int[] cellY;
    private int[] cellZ;

    private float[] posX;       // last known world position, for exact radius tests
    private float[] posY;
    private float[] posZ;

    private float invCellSize;
    private int registeredCount;
    private bool initialized;

    /// <summary>
    /// True when the most recent query filled the caller's buffer and had to stop
    /// early. Grow the buffer (or shrink cellSize) if this keeps happening.
    /// </summary>
    [System.NonSerialized] public bool lastQueryOverflowed;

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    void Start()
    {
        EnsureInitialized();
    }

    /// <summary>
    /// Allocates the grid. Idempotent, and safe to call from another behaviour's
    /// Start(). Udon does not guarantee Start() ordering, so the manager should
    /// call this before its first Add().
    /// </summary>
    public void EnsureInitialized()
    {
        if (initialized) return;

        if (cellSize <= 0f)
        {
            Debug.LogError("[SpatialGrid] cellSize must be > 0. Falling back to 8.");
            cellSize = 8f;
        }
        if (maxObjects <= 0)
        {
            Debug.LogError("[SpatialGrid] maxObjects must be > 0. Falling back to 1024.");
            maxObjects = 1024;
        }

        invCellSize = 1f / cellSize;

        bucketHead = new int[HASH_SIZE];
        nextIndex = new int[maxObjects];
        prevIndex = new int[maxObjects];
        slotUsed = new bool[maxObjects];
        slotBucket = new int[maxObjects];
        cellX = new int[maxObjects];
        cellY = new int[maxObjects];
        cellZ = new int[maxObjects];
        posX = new float[maxObjects];
        posY = new float[maxObjects];
        posZ = new float[maxObjects];

        for (int i = 0; i < HASH_SIZE; i++) bucketHead[i] = NONE;
        for (int i = 0; i < maxObjects; i++)
        {
            nextIndex[i] = NONE;
            prevIndex[i] = NONE;
            slotBucket[i] = NONE;
        }

        registeredCount = 0;
        initialized = true;
    }

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Registers an object id at a position. Re-adding an existing id just moves it.
    /// </summary>
    public void Add(int id, Vector3 position)
    {
        EnsureInitialized();
        if (id < 0 || id >= maxObjects)
        {
            Debug.LogError("[SpatialGrid] Add: id out of range: " + id);
            return;
        }

        if (slotUsed[id])
        {
            UpdatePosition(id, position);
            return;
        }

        int cx = CellCoord(position.x);
        int cy = use2D ? 0 : CellCoord(position.y);
        int cz = CellCoord(position.z);

        slotUsed[id] = true;
        cellX[id] = cx;
        cellY[id] = cy;
        cellZ[id] = cz;
        posX[id] = position.x;
        posY[id] = position.y;
        posZ[id] = position.z;

        LinkIntoBucket(id, HashCell(cx, cy, cz));
        registeredCount++;
    }

    /// <summary>Unregisters an object id. Safe to call on an id that is not registered.</summary>
    public void Remove(int id)
    {
        if (!initialized) return;
        if (id < 0 || id >= maxObjects) return;
        if (!slotUsed[id]) return;

        UnlinkFromBucket(id);
        slotUsed[id] = false;
        slotBucket[id] = NONE;
        registeredCount--;
    }

    /// <summary>
    /// Moves an object. The position is always stored, but the object is only
    /// rehashed when it actually crosses a cell boundary. That early-out is what
    /// keeps this cheap enough to call for every awake object every frame.
    /// </summary>
    public void UpdatePosition(int id, Vector3 position)
    {
        if (!initialized) return;
        if (id < 0 || id >= maxObjects) return;

        if (!slotUsed[id])
        {
            Add(id, position);
            return;
        }

        posX[id] = position.x;
        posY[id] = position.y;
        posZ[id] = position.z;

        int cx = CellCoord(position.x);
        int cy = use2D ? 0 : CellCoord(position.y);
        int cz = CellCoord(position.z);

        // Same cell: nothing to rehash. This is the common case.
        if (cx == cellX[id] && cy == cellY[id] && cz == cellZ[id]) return;

        UnlinkFromBucket(id);
        cellX[id] = cx;
        cellY[id] = cy;
        cellZ[id] = cz;
        LinkIntoBucket(id, HashCell(cx, cy, cz));
    }

    /// <summary>
    /// Collects the ids in the Moore neighborhood around center (3x3x3 cells, or
    /// 3x3 in 2D mode) into results. Returns the number written. No allocations,
    /// no duplicates.
    ///
    /// This covers everything within cellSize of the center, plus some corner
    /// slop. Use QueryRadius when an exact distance bound is needed.
    /// </summary>
    public int QueryNearby(Vector3 center, int[] results, int maxResults)
    {
        lastQueryOverflowed = false;
        if (!initialized || results == null || maxResults <= 0) return 0;

        int ccx = CellCoord(center.x);
        int ccy = use2D ? 0 : CellCoord(center.y);
        int ccz = CellCoord(center.z);

        int yMin = use2D ? 0 : -1;
        int yMax = use2D ? 0 : 1;

        int count = 0;
        for (int dx = -1; dx <= 1; dx++)
        {
            int cx = ccx + dx;
            for (int dy = yMin; dy <= yMax; dy++)
            {
                int cy = ccy + dy;
                for (int dz = -1; dz <= 1; dz++)
                {
                    int cz = ccz + dz;

                    int e = bucketHead[HashCell(cx, cy, cz)];
                    while (e != NONE)
                    {
                        // Different cells can share a bucket, so verify the cell
                        // coordinates. Each object lives in exactly one cell, which
                        // is also why the 27-cell sweep cannot produce duplicates.
                        if (cellX[e] == cx && cellY[e] == cy && cellZ[e] == cz)
                        {
                            if (count >= maxResults)
                            {
                                lastQueryOverflowed = true;
                                return count;
                            }
                            results[count] = e;
                            count++;
                        }
                        e = nextIndex[e];
                    }
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Collects the ids within radius of center. Sweeps exactly the cells the
    /// sphere touches and then does a squared-distance test, so the result is
    /// tight. In 2D mode the distance test ignores height. Returns the number
    /// written.
    /// </summary>
    public int QueryRadius(Vector3 center, float radius, int[] results, int maxResults)
    {
        lastQueryOverflowed = false;
        if (!initialized || results == null || maxResults <= 0 || radius <= 0f) return 0;

        int span = Mathf.CeilToInt(radius * invCellSize);
        float sqrRadius = radius * radius;

        int ccx = CellCoord(center.x);
        int ccy = use2D ? 0 : CellCoord(center.y);
        int ccz = CellCoord(center.z);

        int ySpan = use2D ? 0 : span;

        int count = 0;
        for (int dx = -span; dx <= span; dx++)
        {
            int cx = ccx + dx;
            for (int dy = -ySpan; dy <= ySpan; dy++)
            {
                int cy = ccy + dy;
                for (int dz = -span; dz <= span; dz++)
                {
                    int cz = ccz + dz;

                    int e = bucketHead[HashCell(cx, cy, cz)];
                    while (e != NONE)
                    {
                        if (cellX[e] == cx && cellY[e] == cy && cellZ[e] == cz)
                        {
                            float ox = posX[e] - center.x;
                            float oz = posZ[e] - center.z;
                            float sqrDist = ox * ox + oz * oz;
                            if (!use2D)
                            {
                                float oy = posY[e] - center.y;
                                sqrDist += oy * oy;
                            }

                            if (sqrDist <= sqrRadius)
                            {
                                if (count >= maxResults)
                                {
                                    lastQueryOverflowed = true;
                                    return count;
                                }
                                results[count] = e;
                                count++;
                            }
                        }
                        e = nextIndex[e];
                    }
                }
            }
        }
        return count;
    }

    /// <summary>Unregisters everything but keeps the arrays allocated.</summary>
    public void Clear()
    {
        if (!initialized) return;

        for (int i = 0; i < HASH_SIZE; i++) bucketHead[i] = NONE;
        for (int i = 0; i < maxObjects; i++)
        {
            slotUsed[i] = false;
            nextIndex[i] = NONE;
            prevIndex[i] = NONE;
            slotBucket[i] = NONE;
        }
        registeredCount = 0;
    }

    /// <summary>
    /// Changes the cell size and rehashes every registered object. Not cheap.
    /// This is a tuning/setup call, not a per-frame one.
    /// </summary>
    public void SetCellSize(float newCellSize)
    {
        if (newCellSize <= 0f)
        {
            Debug.LogError("[SpatialGrid] SetCellSize: value must be > 0.");
            return;
        }
        EnsureInitialized();
        if (Mathf.Approximately(newCellSize, cellSize)) return;

        cellSize = newCellSize;
        invCellSize = 1f / cellSize;

        for (int i = 0; i < HASH_SIZE; i++) bucketHead[i] = NONE;
        for (int i = 0; i < maxObjects; i++)
        {
            nextIndex[i] = NONE;
            prevIndex[i] = NONE;
            slotBucket[i] = NONE;
        }

        for (int i = 0; i < maxObjects; i++)
        {
            if (!slotUsed[i]) continue;
            int cx = CellCoord(posX[i]);
            int cy = use2D ? 0 : CellCoord(posY[i]);
            int cz = CellCoord(posZ[i]);
            cellX[i] = cx;
            cellY[i] = cy;
            cellZ[i] = cz;
            LinkIntoBucket(i, HashCell(cx, cy, cz));
        }
    }

    // ------------------------------------------------------------------
    // Accessors
    // ------------------------------------------------------------------

    public bool Contains(int id)
    {
        if (!initialized || id < 0 || id >= maxObjects) return false;
        return slotUsed[id];
    }

    public int GetRegisteredCount()
    {
        return registeredCount;
    }

    public Vector3 GetPosition(int id)
    {
        if (!initialized || id < 0 || id >= maxObjects || !slotUsed[id]) return Vector3.zero;
        return new Vector3(posX[id], posY[id], posZ[id]);
    }

    public float GetCellSize()
    {
        return cellSize;
    }

    /// <summary>
    /// Distance a player can move before their cached query result should be
    /// considered stale. Half a cell is the usual compromise between re-query cost
    /// and missing an object that just came into range.
    /// </summary>
    public float GetRequeryDistance()
    {
        return cellSize * 0.5f;
    }

    // ------------------------------------------------------------------
    // Debug / tuning helpers
    // ------------------------------------------------------------------

    /// <summary>Number of hash buckets currently holding at least one object.</summary>
    public int GetOccupiedBucketCount()
    {
        if (!initialized) return 0;
        int n = 0;
        for (int i = 0; i < HASH_SIZE; i++)
        {
            if (bucketHead[i] != NONE) n++;
        }
        return n;
    }

    /// <summary>
    /// Longest bucket chain. If this climbs far above registeredCount / HASH_SIZE,
    /// the objects are clustered into very few cells and cellSize is too large.
    /// </summary>
    public int GetLongestChainLength()
    {
        if (!initialized) return 0;
        int longest = 0;
        for (int i = 0; i < HASH_SIZE; i++)
        {
            int len = 0;
            int e = bucketHead[i];
            while (e != NONE)
            {
                len++;
                e = nextIndex[e];
            }
            if (len > longest) longest = len;
        }
        return longest;
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private int CellCoord(float worldCoord)
    {
        return Mathf.FloorToInt(worldCoord * invCellSize);
    }

    /// <summary>
    /// Triple XOR/multiply spatial hash. Udon int arithmetic wraps silently, so the
    /// overflow here is intended and needs no unchecked block. Masking with
    /// HASH_MASK (a power of two minus one) also clears the sign bit, so the result
    /// is always a valid bucket index.
    /// </summary>
    private int HashCell(int cx, int cy, int cz)
    {
        int h = (cx * PRIME_X) ^ (cy * PRIME_Y) ^ (cz * PRIME_Z);
        return h & HASH_MASK;
    }

    private void LinkIntoBucket(int id, int bucket)
    {
        int head = bucketHead[bucket];
        nextIndex[id] = head;
        prevIndex[id] = NONE;
        if (head != NONE) prevIndex[head] = id;
        bucketHead[bucket] = id;
        slotBucket[id] = bucket;
    }

    private void UnlinkFromBucket(int id)
    {
        int bucket = slotBucket[id];
        if (bucket == NONE) return;

        int p = prevIndex[id];
        int n = nextIndex[id];

        if (p != NONE) nextIndex[p] = n;
        else bucketHead[bucket] = n;

        if (n != NONE) prevIndex[n] = p;

        nextIndex[id] = NONE;
        prevIndex[id] = NONE;
    }
}
