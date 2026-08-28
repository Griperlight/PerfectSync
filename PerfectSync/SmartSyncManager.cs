using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SmartSyncManager : UdonSharpBehaviour
{

    [Header("Wiring")]
    [Tooltip("Spatial index used for interest management. Required.")]
    public SpatialGrid grid;

    [Tooltip("Objects registered in the scene at edit time. Objects may also register themselves at runtime.")]
    public SmartSyncObject[] preRegisteredObjects;

    [Tooltip("Transport pool. Needs at least as many channels as expected concurrent players; 16 covers a full instance.")]
    public SmartSyncChannel[] channels;

    [Header("Send rate")]
    [Tooltip("Seconds between packets. 0.1 (10 Hz) is a good default: faster burns the manual-sync rate limit, slower feels laggy on thrown objects.")]
    public float sendInterval = 0.1f;

    [Header("Capacity")]
    [Tooltip("Maximum object slots. Must be >= the grid's maxObjects.")]
    public int maxObjects = 1024;

    [Header("Interest management")]
    [Tooltip("Radius (m) around each player treated as relevant. Objects outside every player's radius are allowed to sleep.")]
    public float interestRadius = 24f;

    [Tooltip("Seconds between interest re-evaluations. Interest changes far slower than physics, so this does not need to be per-frame.")]
    public float interestInterval = 0.5f;

    private const int NONE = -1;

    private SmartSyncObject[] objects;
    private int registeredCount;

    private int[] awakeSet;
    private int[] awakeSlot;
    private int awakeCount;

    private int[] dirtySet;
    private int[] dirtySlot;
    private int dirtyCount;

    private int[] queryResults;
    private const int QUERY_BUFFER_SIZE = 512;

    private VRCPlayerApi[] playerBuffer;
    private const int MAX_PLAYERS = 96;

    private float nextInterestTime;
    private float nextSendTime;
    private SmartSyncChannel localChannel;
    private bool initialized;

    void Start()
    {
        EnsureInitialized();

        if (preRegisteredObjects != null)
        {
            for (int i = 0; i < preRegisteredObjects.Length; i++)
            {
                _Register(preRegisteredObjects[i]);
            }
        }
    }

    private void EnsureInitialized()
    {
        if (initialized) return;

        if (maxObjects <= 0) maxObjects = 1024;

        objects = new SmartSyncObject[maxObjects];
        awakeSet = new int[maxObjects];
        awakeSlot = new int[maxObjects];
        dirtySet = new int[maxObjects];
        dirtySlot = new int[maxObjects];
        queryResults = new int[QUERY_BUFFER_SIZE];
        playerBuffer = new VRCPlayerApi[MAX_PLAYERS];
        lastPackedIds = new int[maxObjects];

        if (channels == null || channels.Length == 0) channels = new SmartSyncChannel[16];

        for (int i = 0; i < maxObjects; i++)
        {
            awakeSlot[i] = NONE;
            dirtySlot[i] = NONE;
        }

        awakeCount = 0;
        dirtyCount = 0;
        registeredCount = 0;

        if (grid != null)
        {
            grid.EnsureInitialized();
        }
        else
        {
            Debug.LogError("[SmartSyncManager] No SpatialGrid assigned. Interest management is disabled.");
        }

        initialized = true;
    }

    public void _Register(SmartSyncObject obj)
    {
        EnsureInitialized();
        if (obj == null) return;
        if (obj._IsRegistered()) return;

        if (registeredCount >= maxObjects)
        {
            Debug.LogError("[SmartSyncManager] Registry full at " + maxObjects + " objects. Raise maxObjects.");
            return;
        }

        int id = registeredCount;
        registeredCount++;

        objects[id] = obj;
        obj._OnRegistered(this, id);

        if (grid != null) grid.Add(id, obj._GetPosition());
    }

    public void _Unregister(SmartSyncObject obj)
    {
        if (!initialized || obj == null) return;
        int id = obj.syncId;
        if (id < 0 || id >= maxObjects || objects[id] != obj) return;

        RemoveFromAwakeSet(id);
        RemoveFromDirtySet(id);
        if (grid != null) grid.Remove(id);
        objects[id] = null;
    }

    public void _OnObjectWoke(SmartSyncObject obj)
    {
        if (!initialized || obj == null) return;
        AddToAwakeSet(obj.syncId);
    }

    public void _OnObjectSleeping(SmartSyncObject obj)
    {
        if (!initialized || obj == null) return;
        RemoveFromAwakeSet(obj.syncId);
    }

    public void _OnObjectDirty(SmartSyncObject obj)
    {
        if (!initialized || obj == null) return;
        AddToDirtySet(obj.syncId);
    }

    public void _OnObjectClean(SmartSyncObject obj)
    {
        if (!initialized || obj == null) return;
        RemoveFromDirtySet(obj.syncId);
    }

    void Update()
    {
        if (!initialized) return;

        float deltaTime = Time.deltaTime;

        for (int i = awakeCount - 1; i >= 0; i--)
        {
            int id = awakeSet[i];
            SmartSyncObject obj = objects[id];
            if (obj == null) continue;

            obj._Tick(deltaTime);

            if (grid != null) grid.UpdatePosition(id, obj._GetPosition());
        }

        if (Time.time >= nextInterestTime)
        {
            nextInterestTime = Time.time + interestInterval;
            UpdateInterest();
            MaintainChannel();
        }

        if (Time.time >= nextSendTime)
        {
            nextSendTime = Time.time + sendInterval;
            if (localChannel != null) localChannel._TrySend();
        }
    }

    public void _RegisterChannel(SmartSyncChannel channel)
    {
        EnsureInitialized();
        if (channel == null || channels == null) return;

        for (int i = 0; i < channels.Length; i++)
        {
            if (channels[i] == channel) return;
            if (channels[i] == null)
            {
                channels[i] = channel;
                return;
            }
        }
    }

    private void MaintainChannel()
    {
        if (channels == null || channels.Length == 0) return;

        if (localChannel != null && localChannel._IsLocalChannel()) return;

        localChannel = null;
        for (int i = 0; i < channels.Length; i++)
        {
            SmartSyncChannel channel = channels[i];
            if (channel == null) continue;

            if (channel._IsLocalChannel())
            {
                localChannel = channel;
                return;
            }
        }

        for (int i = 0; i < channels.Length; i++)
        {
            SmartSyncChannel channel = channels[i];
            if (channel == null || !channel._IsFree()) continue;

            channel._ClaimForLocalPlayer();
            localChannel = channel;
            return;
        }

        Debug.LogWarning("[SmartSyncManager] No free sync channel. Add more channels to the pool.");
    }

    private void UpdateInterest()
    {
        if (grid == null) return;

        int playerCount = VRCPlayerApi.GetPlayerCount();
        if (playerCount > MAX_PLAYERS) playerCount = MAX_PLAYERS;
        VRCPlayerApi.GetPlayers(playerBuffer);

        for (int p = 0; p < playerCount; p++)
        {
            VRCPlayerApi player = playerBuffer[p];
            if (player == null || !Utilities.IsValid(player)) continue;

            int found = grid.QueryRadius(player.GetPosition(), interestRadius, queryResults, QUERY_BUFFER_SIZE);
            for (int i = 0; i < found; i++)
            {
                int id = queryResults[i];
                SmartSyncObject obj = objects[id];
                if (obj == null) continue;

                if (obj.isDirty && !obj.isAwake) obj._ForceWake();
            }
        }
    }

    public void _WakeNear(Vector3 position, float radius)
    {
        if (!initialized || grid == null || radius <= 0f) return;

        int found = grid.QueryRadius(position, radius, queryResults, QUERY_BUFFER_SIZE);
        for (int i = 0; i < found; i++)
        {
            SmartSyncObject obj = objects[queryResults[i]];
            if (obj == null || obj.isAwake) continue;
            if (!Networking.IsOwner(obj.gameObject)) continue;

            if (obj.body != null && !obj.body.isKinematic) obj.body.WakeUp();
            obj._ForceWake();
        }
    }

    private void AddToAwakeSet(int id)
    {
        if (id < 0 || id >= maxObjects) return;
        if (awakeSlot[id] != NONE) return;
        awakeSet[awakeCount] = id;
        awakeSlot[id] = awakeCount;
        awakeCount++;
    }

    private void RemoveFromAwakeSet(int id)
    {
        if (id < 0 || id >= maxObjects) return;
        int index = awakeSlot[id];
        if (index == NONE) return;

        int last = awakeCount - 1;
        int moved = awakeSet[last];
        awakeSet[index] = moved;
        awakeSlot[moved] = index;

        awakeSlot[id] = NONE;
        awakeCount = last;
    }

    private void AddToDirtySet(int id)
    {
        if (id < 0 || id >= maxObjects) return;
        if (dirtySlot[id] != NONE) return;
        dirtySet[dirtyCount] = id;
        dirtySlot[id] = dirtyCount;
        dirtyCount++;
    }

    private void RemoveFromDirtySet(int id)
    {
        if (id < 0 || id >= maxObjects) return;
        int index = dirtySlot[id];
        if (index == NONE) return;

        int last = dirtyCount - 1;
        int moved = dirtySet[last];
        dirtySet[index] = moved;
        dirtySlot[moved] = index;

        dirtySlot[id] = NONE;
        dirtyCount = last;
    }

    private const byte PROTOCOL_VERSION = 1;

    private const int HEADER_BYTES = 4;
    private const int RECORD_HEADER_BYTES = 3;

    private const int FLAG_STATE_MASK = 0x07;
    private const int FLAG_DISCONTINUOUS = 0x08;
    private const int FLAG_LEFT_HAND = 0x10;
    private const int FLAG_HAS_VELOCITY = 0x20;
    private const int FLAG_FULL_SNAPSHOT = 0x40;

    [Header("Quantization bounds")]
    [Tooltip("Center of the syncable world volume.")]
    public Vector3 worldCenter = Vector3.zero;

    [Tooltip("Half-size (m) of the syncable world volume. Positions are 16-bit per axis across this range: 500 gives ~1.5 cm precision.")]
    public float worldExtent = 500f;

    [Tooltip("Maximum encodable speed (m/s). Velocities are 16-bit per axis across +/- this value.")]
    public float maxSpeed = 32f;

    [Tooltip("Maximum encodable held offset (m) from the hand bone.")]
    public float maxHeldOffset = 2f;

    [Tooltip("Speed (m/s) below which velocity is omitted from the packet entirely.")]
    public float velocityCutoff = 0.05f;

    private const float SMALLEST_THREE_RANGE = 0.7071068f;

    private byte sequenceNumber;

    private int[] lastPackedIds;
    private int lastPackedCount;

    public int _WritePacket(byte[] buffer, int capacity)
    {
        if (!initialized || buffer == null) return 0;
        if (capacity > buffer.Length) capacity = buffer.Length;
        if (capacity < HEADER_BYTES + RECORD_HEADER_BYTES) return 0;

        int offset = HEADER_BYTES;
        int records = 0;
        lastPackedCount = 0;

        while (dirtyCount > 0)
        {
            int id = dirtySet[0];
            SmartSyncObject obj = objects[id];

            if (obj == null)
            {
                RemoveFromDirtySet(id);
                continue;
            }

            if (!Networking.IsOwner(obj.gameObject))
            {
                obj._ClearDirty();
                RemoveFromDirtySet(id);
                continue;
            }

            int needed = MeasureRecord(obj);
            if (offset + needed > capacity) break;

            offset = WriteRecord(buffer, offset, id, obj);
            records++;

            obj._OnPacked();
            RemoveFromDirtySet(id);

            lastPackedIds[lastPackedCount] = id;
            lastPackedCount++;
        }

        if (records == 0) return 0;

        buffer[0] = PROTOCOL_VERSION;
        buffer[1] = sequenceNumber;
        WriteUShort(buffer, 2, records);
        sequenceNumber = (byte)((sequenceNumber + 1) & 0xFF);

        return offset;
    }

    private int MeasureRecord(SmartSyncObject obj)
    {
        int size = RECORD_HEADER_BYTES;
        if (obj.state == SmartSyncObject.STATE_HELD) return size + 12;

        size += 10;
        if (obj.state == SmartSyncObject.STATE_PHYSICS &&
            obj._GetVelocity().sqrMagnitude > velocityCutoff * velocityCutoff)
        {
            size += 6;
        }
        return size;
    }

    private int WriteRecord(byte[] buffer, int offset, int id, SmartSyncObject obj)
    {
        int state = obj.state;
        int flags = state & FLAG_STATE_MASK;
        if (obj.discontinuous) flags |= FLAG_DISCONTINUOUS;
        if (obj.needsFullSnapshot) flags |= FLAG_FULL_SNAPSHOT;

        if (state == SmartSyncObject.STATE_HELD)
        {
            if (obj.heldInLeftHand) flags |= FLAG_LEFT_HAND;

            WriteUShort(buffer, offset, id);
            buffer[offset + 2] = (byte)flags;
            offset += RECORD_HEADER_BYTES;

            WriteUShort(buffer, offset, obj.heldPlayerId < 0 ? 0xFFFF : obj.heldPlayerId);
            offset += 2;
            offset = WriteHeldOffset(buffer, offset, obj.heldLocalPosition);
            offset = WriteRotation(buffer, offset, obj.heldLocalRotation);
            return offset;
        }

        Vector3 velocity = obj._GetVelocity();
        bool sendVelocity = state == SmartSyncObject.STATE_PHYSICS &&
                            velocity.sqrMagnitude > velocityCutoff * velocityCutoff;
        if (sendVelocity) flags |= FLAG_HAS_VELOCITY;

        WriteUShort(buffer, offset, id);
        buffer[offset + 2] = (byte)flags;
        offset += RECORD_HEADER_BYTES;

        offset = WritePosition(buffer, offset, obj._GetPosition());
        offset = WriteRotation(buffer, offset, obj._GetRotation());
        if (sendVelocity) offset = WriteVelocity(buffer, offset, velocity);

        return offset;
    }

    public void _ReadPacket(byte[] buffer, int length)
    {
        if (!initialized || buffer == null || length < HEADER_BYTES) return;
        if (buffer[0] != PROTOCOL_VERSION) return;

        int records = ReadUShort(buffer, 2);
        int offset = HEADER_BYTES;

        for (int r = 0; r < records; r++)
        {
            if (offset + RECORD_HEADER_BYTES > length) return;

            int id = ReadUShort(buffer, offset);
            int flags = buffer[offset + 2];
            offset += RECORD_HEADER_BYTES;

            int state = flags & FLAG_STATE_MASK;
            bool discontinuous = (flags & FLAG_DISCONTINUOUS) != 0;

            SmartSyncObject obj = (id >= 0 && id < maxObjects) ? objects[id] : null;
            bool apply = obj != null && !Networking.IsOwner(obj.gameObject);

            if (state == SmartSyncObject.STATE_HELD)
            {
                if (offset + 12 > length) return;

                int playerId = ReadUShort(buffer, offset);
                offset += 2;
                Vector3 localPosition = ReadHeldOffset(buffer, offset);
                offset += 6;
                Quaternion localRotation = ReadRotation(buffer, offset);
                offset += 4;

                if (apply)
                {
                    obj._ApplyHeldState(playerId == 0xFFFF ? -1 : playerId,
                                        (flags & FLAG_LEFT_HAND) != 0,
                                        localPosition, localRotation);
                }
                continue;
            }

            if (offset + 10 > length) return;

            Vector3 position = ReadPosition(buffer, offset);
            offset += 6;
            Quaternion rotation = ReadRotation(buffer, offset);
            offset += 4;

            Vector3 velocity = Vector3.zero;
            if ((flags & FLAG_HAS_VELOCITY) != 0)
            {
                if (offset + 6 > length) return;
                velocity = ReadVelocity(buffer, offset);
                offset += 6;
            }

            if (apply)
            {
                obj._ApplyNetworkState(state, position, rotation, velocity, Vector3.zero, discontinuous);
            }
        }
    }

    public void _RequeueLastPacket()
    {
        if (!initialized) return;

        for (int i = 0; i < lastPackedCount; i++)
        {
            int id = lastPackedIds[i];
            SmartSyncObject obj = objects[id];
            if (obj == null) continue;
            if (!Networking.IsOwner(obj.gameObject)) continue;

            obj._Requeue();
            AddToDirtySet(id);
        }

        lastPackedCount = 0;
    }

    public void _QueueFullSnapshot()
    {
        if (!initialized) return;

        for (int id = 0; id < registeredCount; id++)
        {
            SmartSyncObject obj = objects[id];
            if (obj == null) continue;
            if (!Networking.IsOwner(obj.gameObject)) continue;

            obj.needsFullSnapshot = true;
            obj.isDirty = true;
            AddToDirtySet(id);
        }
    }

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        if (player == null || player.isLocal) return;
        _QueueFullSnapshot();
    }

    private int WritePosition(byte[] buffer, int offset, Vector3 position)
    {
        float min = -worldExtent;
        float range = worldExtent * 2f;
        WriteUShort(buffer, offset, Quantize16(position.x - worldCenter.x, min, range));
        WriteUShort(buffer, offset + 2, Quantize16(position.y - worldCenter.y, min, range));
        WriteUShort(buffer, offset + 4, Quantize16(position.z - worldCenter.z, min, range));
        return offset + 6;
    }

    private Vector3 ReadPosition(byte[] buffer, int offset)
    {
        float min = -worldExtent;
        float range = worldExtent * 2f;
        return new Vector3(
            worldCenter.x + Dequantize16(ReadUShort(buffer, offset), min, range),
            worldCenter.y + Dequantize16(ReadUShort(buffer, offset + 2), min, range),
            worldCenter.z + Dequantize16(ReadUShort(buffer, offset + 4), min, range));
    }

    private int WriteVelocity(byte[] buffer, int offset, Vector3 velocity)
    {
        float min = -maxSpeed;
        float range = maxSpeed * 2f;
        WriteUShort(buffer, offset, Quantize16(velocity.x, min, range));
        WriteUShort(buffer, offset + 2, Quantize16(velocity.y, min, range));
        WriteUShort(buffer, offset + 4, Quantize16(velocity.z, min, range));
        return offset + 6;
    }

    private Vector3 ReadVelocity(byte[] buffer, int offset)
    {
        float min = -maxSpeed;
        float range = maxSpeed * 2f;
        return new Vector3(
            Dequantize16(ReadUShort(buffer, offset), min, range),
            Dequantize16(ReadUShort(buffer, offset + 2), min, range),
            Dequantize16(ReadUShort(buffer, offset + 4), min, range));
    }

    private int WriteHeldOffset(byte[] buffer, int offset, Vector3 localPosition)
    {
        float min = -maxHeldOffset;
        float range = maxHeldOffset * 2f;
        WriteUShort(buffer, offset, Quantize16(localPosition.x, min, range));
        WriteUShort(buffer, offset + 2, Quantize16(localPosition.y, min, range));
        WriteUShort(buffer, offset + 4, Quantize16(localPosition.z, min, range));
        return offset + 6;
    }

    private Vector3 ReadHeldOffset(byte[] buffer, int offset)
    {
        float min = -maxHeldOffset;
        float range = maxHeldOffset * 2f;
        return new Vector3(
            Dequantize16(ReadUShort(buffer, offset), min, range),
            Dequantize16(ReadUShort(buffer, offset + 2), min, range),
            Dequantize16(ReadUShort(buffer, offset + 4), min, range));
    }

    private int WriteRotation(byte[] buffer, int offset, Quaternion rotation)
    {
        float x = rotation.x, y = rotation.y, z = rotation.z, w = rotation.w;

        int largest = 0;
        float largestValue = Mathf.Abs(x);
        if (Mathf.Abs(y) > largestValue) { largest = 1; largestValue = Mathf.Abs(y); }
        if (Mathf.Abs(z) > largestValue) { largest = 2; largestValue = Mathf.Abs(z); }
        if (Mathf.Abs(w) > largestValue) { largest = 3; largestValue = Mathf.Abs(w); }

        float sign = 1f;
        if (largest == 0 && x < 0f) sign = -1f;
        else if (largest == 1 && y < 0f) sign = -1f;
        else if (largest == 2 && z < 0f) sign = -1f;
        else if (largest == 3 && w < 0f) sign = -1f;

        float a, b, c;
        if (largest == 0) { a = y * sign; b = z * sign; c = w * sign; }
        else if (largest == 1) { a = x * sign; b = z * sign; c = w * sign; }
        else if (largest == 2) { a = x * sign; b = y * sign; c = w * sign; }
        else { a = x * sign; b = y * sign; c = z * sign; }

        int packed = (largest << 30)
                   | (Quantize10(a) << 20)
                   | (Quantize10(b) << 10)
                   | Quantize10(c);

        buffer[offset] = (byte)(packed & 0xFF);
        buffer[offset + 1] = (byte)((packed >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((packed >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((packed >> 24) & 0xFF);
        return offset + 4;
    }

    private Quaternion ReadRotation(byte[] buffer, int offset)
    {
        int packed = buffer[offset]
                   | (buffer[offset + 1] << 8)
                   | (buffer[offset + 2] << 16)
                   | (buffer[offset + 3] << 24);

        int largest = (packed >> 30) & 0x3;
        float a = Dequantize10((packed >> 20) & 0x3FF);
        float b = Dequantize10((packed >> 10) & 0x3FF);
        float c = Dequantize10(packed & 0x3FF);

        float squared = 1f - (a * a + b * b + c * c);
        float d = squared > 0f ? Mathf.Sqrt(squared) : 0f;

        if (largest == 0) return new Quaternion(d, a, b, c);
        if (largest == 1) return new Quaternion(a, d, b, c);
        if (largest == 2) return new Quaternion(a, b, d, c);
        return new Quaternion(a, b, c, d);
    }

    private int Quantize16(float value, float min, float range)
    {
        float t = Mathf.Clamp01((value - min) / range);
        return (int)(t * 65535f + 0.5f);
    }

    private float Dequantize16(int quantized, float min, float range)
    {
        return min + (quantized / 65535f) * range;
    }

    private int Quantize10(float value)
    {
        float t = Mathf.Clamp01((value / SMALLEST_THREE_RANGE) * 0.5f + 0.5f);
        return (int)(t * 1023f + 0.5f);
    }

    private float Dequantize10(int quantized)
    {
        return ((quantized / 1023f) * 2f - 1f) * SMALLEST_THREE_RANGE;
    }

    private void WriteUShort(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private int ReadUShort(byte[] buffer, int offset)
    {
        return buffer[offset] | (buffer[offset + 1] << 8);
    }

    public int _GetRegisteredCount() { return registeredCount; }
    public int _GetAwakeCount() { return awakeCount; }
    public int _GetDirtyCount() { return dirtyCount; }
    public SmartSyncChannel _GetLocalChannel() { return localChannel; }

    public float _GetBytesPerSecond()
    {
        if (localChannel == null) return 0f;
        return localChannel._GetBytesPerSecond();
    }

    public SmartSyncObject _GetObject(int id)
    {
        if (!initialized || id < 0 || id >= maxObjects) return null;
        return objects[id];
    }
}
