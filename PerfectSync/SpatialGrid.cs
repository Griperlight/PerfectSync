using UdonSharp;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SpatialGrid : UdonSharpBehaviour
{

    [Tooltip("Edge length of one grid cell, in meters. 6-12 m suits most worlds; start at 8.")]
    public float cellSize = 8f;

    [Tooltip("Number of object slots. Ids passed to Add/Remove/UpdatePosition must be 0..maxObjects-1.")]
    public int maxObjects = 1024;

    [Tooltip("XZ-only mode: ignores height. Queries touch 9 cells instead of 27, so it is roughly 3x cheaper. Use it when gameplay is mostly horizontal.")]
    public bool use2D = false;

    private const int HASH_SIZE = 2048;
    private const int HASH_MASK = HASH_SIZE - 1;

    private const int PRIME_X = 73856093;
    private const int PRIME_Y = 19349663;
    private const int PRIME_Z = 83492791;

    private const int NONE = -1;

    private int[] bucketHead;
    private int[] nextIndex;
    private int[] prevIndex;

    private bool[] slotUsed;
    private int[] slotBucket;
    private int[] cellX;
    private int[] cellY;
    private int[] cellZ;

    private float[] posX;
    private float[] posY;
    private float[] posZ;

    private float invCellSize;
    private int registeredCount;
    private bool initialized;

    [System.NonSerialized] public bool lastQueryOverflowed;

    void Start()
    {
        EnsureInitialized();
    }

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

        if (cx == cellX[id] && cy == cellY[id] && cz == cellZ[id]) return;

        UnlinkFromBucket(id);
        cellX[id] = cx;
        cellY[id] = cy;
        cellZ[id] = cz;
        LinkIntoBucket(id, HashCell(cx, cy, cz));
    }

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

    public float GetRequeryDistance()
    {
        return cellSize * 0.5f;
    }

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

    private int CellCoord(float worldCoord)
    {
        return Mathf.FloorToInt(worldCoord * invCellSize);
    }

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
