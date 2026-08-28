using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>
/// Central sync manager: registry, spatial index, awake set, dirty set, the
/// per-frame tick, interest management, and the packet codec.
///
/// WHY A CENTRAL MANAGER
/// Hundreds of Continuous Sync behaviours means hundreds of independent packet
/// streams competing for the same ~11 KB/s of Udon bandwidth. One manager batching
/// only the objects that are actually awake and actually relevant is the only
/// shape that reaches 500+ objects.
///
/// It builds and parses packets but sends nothing itself. SmartSyncChannel owns
/// the wire, one channel per player, because VRChat ownership is per-GameObject
/// and only an object's owner may legally publish its state.
///
/// SET MANAGEMENT
/// The awake set and dirty set are dense int arrays with swap-remove and a
/// slot -> position lookup, so add and remove are both O(1) and iteration is a
/// straight walk over contiguous ints. No List, no allocation, no LINQ.
/// </summary>
// The manager itself syncs nothing: it builds and parses packets, and a separate
// per-player transport behaviour owns the synced buffer.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SmartSyncManager : UdonSharpBehaviour
{
    // ------------------------------------------------------------------
    // Configuration
    // ------------------------------------------------------------------

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

    // ------------------------------------------------------------------
    // Registry
    // ------------------------------------------------------------------

    private SmartSyncObject[] objects;   // slot id -> object
    private int registeredCount;

    // Awake set: dense list of slot ids currently ticking.
    private int[] awakeSet;
    private int[] awakeSlot;             // slot id -> index in awakeSet, or NONE
    private int awakeCount;

    // Dirty set: dense list of slot ids with unsent changes.
    private int[] dirtySet;
    private int[] dirtySlot;             // slot id -> index in dirtySet, or NONE
    private int dirtyCount;

    // Scratch buffers. Pre-allocated: neither queries nor the player sweep may
    // allocate, since both run on a timer forever.
    private int[] queryResults;
    // Sized for the worst case: every object in the world inside one player's
    // interest radius. A query that fills this buffer truncates, and the objects
    // past the cutoff are silently dropped from the relevant set -- so headroom
    // here is far cheaper than the bug it prevents. 512 ints is 2 KB.
    private const int QUERY_BUFFER_SIZE = 512;

    private VRCPlayerApi[] playerBuffer;
    private const int MAX_PLAYERS = 96; // above VRChat's hard instance cap

    private float nextInterestTime;
    private float nextSendTime;
    private SmartSyncChannel localChannel;
    private bool initialized;

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

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

        // Channels self-register in their Start(), so an unwired inspector array
        // still works. 16 slots covers a full VRChat instance.
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
            // Udon does not guarantee Start() ordering, so the grid may not have
            // run its own Start() yet.
            grid.EnsureInitialized();
        }
        else
        {
            Debug.LogError("[SmartSyncManager] No SpatialGrid assigned. Interest management is disabled.");
        }

        initialized = true;
    }

    // ------------------------------------------------------------------
    // Registration
    // ------------------------------------------------------------------

    /// <summary>
    /// Assigns a slot to an object and inserts it into the spatial grid. Slot ids
    /// are dense and handed out in registration order, which is what lets the grid
    /// use them directly as array indices.
    /// </summary>
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

    /// <summary>
    /// Removes an object from the awake/dirty sets and the grid. The slot itself is
    /// left in place: ids are baked into pending packets, so recycling them mid-run
    /// would make remotes apply state to the wrong object.
    /// </summary>
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

    // ------------------------------------------------------------------
    // Notifications from SmartSyncObject
    // ------------------------------------------------------------------

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

    /// <summary>Object dropped its queued state, usually because it lost ownership.</summary>
    public void _OnObjectClean(SmartSyncObject obj)
    {
        if (!initialized || obj == null) return;
        RemoveFromDirtySet(obj.syncId);
    }

    // ------------------------------------------------------------------
    // Per-frame work
    //
    // The manager is the only behaviour in the system with an Update. Its cost is
    // proportional to the awake count, not the total object count.
    // ------------------------------------------------------------------

    void Update()
    {
        if (!initialized) return;

        float deltaTime = Time.deltaTime;

        // Iterate backwards: objects going to sleep swap-remove themselves out of
        // awakeSet mid-loop, and a backward walk stays correct when that happens.
        for (int i = awakeCount - 1; i >= 0; i--)
        {
            int id = awakeSet[i];
            SmartSyncObject obj = objects[id];
            if (obj == null) continue;

            obj._Tick(deltaTime);

            // Only awake objects move, so only awake objects need reindexing.
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

    // ------------------------------------------------------------------
    // Transport pool
    // ------------------------------------------------------------------

    /// <summary>Channels announce themselves here so inspector wiring is optional.</summary>
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

    /// <summary>
    /// Makes sure the local player holds exactly one channel. Re-checked on the
    /// interest timer rather than once at join, because claims can be lost to a
    /// race with another player and channels free up as people leave.
    /// </summary>
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

    /// <summary>
    /// Walks every player, queries the grid around them, and force-wakes relevant
    /// objects that had gone dormant. Objects nobody is near are simply left alone
    /// to sleep, which is where nearly all of the savings come from.
    /// </summary>
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

                // Relevance alone does not wake an object that is genuinely at
                // rest; it only makes sure a dirty one gets ticked and sent.
                if (obj.isDirty && !obj.isAwake) obj._ForceWake();
            }
        }
    }

    /// <summary>
    /// Wakes locally-owned objects near a point.
    ///
    /// Runs on every client, which is the point: when someone grabs the bottom of
    /// a stack, the cubes above it are usually owned by a DIFFERENT player, and
    /// only that player's client can wake and publish them. The grabber cannot do
    /// it on their behalf.
    /// </summary>
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

    // ------------------------------------------------------------------
    // Dense set helpers (O(1) add and remove via swap-remove)
    // ------------------------------------------------------------------

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

    // ==================================================================
    // PACKET LAYER
    //
    // The manager builds and parses packets; a transport behaviour owns the
    // synced byte[] and calls RequestSerialization. Keeping the two apart is what
    // lets several per-player transports share one packing implementation.
    //
    // WIRE FORMAT
    //   [0]     protocol version
    //   [1]     sequence number (wraps; used only for logging/debug)
    //   [2..3]  record count (ushort)
    //   then, per record:
    //     [0..1] object id      (ushort)
    //     [2]    flags          (byte)
    //     payload, by state:
    //       SLEEPING / TELEPORT : pos(6) rot(4)              = 10
    //       PHYSICS             : pos(6) rot(4) [vel(6)]     = 10 or 16
    //       HELD                : player(2) localPos(6) rot(4) = 12
    //
    // Flags byte:
    //   bits 0-2  state
    //   bit  3    discontinuity
    //   bit  4    held in left hand
    //   bit  5    velocity present
    //   bit  6    full snapshot
    // ==================================================================

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

    private const float SMALLEST_THREE_RANGE = 0.7071068f; // 1/sqrt(2): bound on the non-largest components

    private byte sequenceNumber;

    // Ids written into the most recent packet, kept so a refused serialization can
    // be retried instead of silently lost.
    private int[] lastPackedIds;
    private int lastPackedCount;

    // ------------------------------------------------------------------
    // Writing
    // ------------------------------------------------------------------

    /// <summary>
    /// Packs as many locally-owned dirty objects as fit into <paramref name="buffer"/>
    /// and returns the byte count (0 when there is nothing to send).
    ///
    /// Only objects the local player owns are written. VRChat ownership is
    /// per-GameObject, so sending anyone else's state would be a client asserting
    /// authority it does not have.
    ///
    /// Packed objects are removed from the dirty set as they are consumed. Because
    /// removal is a swap from the tail, repeated calls naturally rotate through a
    /// backlog instead of starving the same objects every time.
    /// </summary>
    public int _WritePacket(byte[] buffer, int capacity)
    {
        if (!initialized || buffer == null) return 0;
        if (capacity > buffer.Length) capacity = buffer.Length;
        if (capacity < HEADER_BYTES + RECORD_HEADER_BYTES) return 0;

        int offset = HEADER_BYTES;
        int records = 0;
        lastPackedCount = 0;

        // Always drain from the head. Every entry examined is either packed or
        // discarded, so the set cannot accumulate objects no send will consume --
        // an earlier version skipped past non-owned entries and left them queued
        // forever, which clogged the set and made each packet pay to scan them.
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
                // Not ours to publish. Drop it; the real owner queues its own copy.
                obj._ClearDirty();
                RemoveFromDirtySet(id);
                continue;
            }

            int needed = MeasureRecord(obj);
            if (offset + needed > capacity) break; // full: the rest waits for the next packet

            offset = WriteRecord(buffer, offset, id, obj);
            records++;

            obj._OnPacked();
            RemoveFromDirtySet(id);

            // Remembered so a failed serialization can put them back.
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

        size += 10; // position + rotation
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

    // ------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------

    /// <summary>
    /// Parses a packet and applies it. Records for objects the local player owns
    /// are parsed but discarded: we are authoritative over those, and applying a
    /// stale remote copy is what makes held objects snap backwards.
    /// </summary>
    public void _ReadPacket(byte[] buffer, int length)
    {
        if (!initialized || buffer == null || length < HEADER_BYTES) return;
        if (buffer[0] != PROTOCOL_VERSION) return;

        int records = ReadUShort(buffer, 2);
        int offset = HEADER_BYTES;

        for (int r = 0; r < records; r++)
        {
            if (offset + RECORD_HEADER_BYTES > length) return; // truncated

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

    // ------------------------------------------------------------------
    // Snapshots
    // ------------------------------------------------------------------

    /// <summary>
    /// Puts the objects from the most recent packet back in the send queue.
    ///
    /// Called when serialization is refused. Those objects were already marked
    /// clean, and for a moving object that is harmless -- the next packet carries
    /// fresher state. It is NOT harmless for an object that just went to sleep:
    /// that final resting pose is the last thing it will ever send, so a dropped
    /// packet leaves it frozen at a stale position on every remote, forever.
    /// </summary>
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

    /// <summary>
    /// Marks every locally-owned object for a full resend. Call this when a player
    /// joins: a late joiner has no history, so deltas and sleeping objects that
    /// never send would leave their world half-empty.
    /// </summary>
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

    // ------------------------------------------------------------------
    // Quantization
    //
    // Precision at the defaults: position ~1.5 cm over a 1 km cube, rotation
    // ~0.1 degrees, velocity ~1 mm/s. All well under what is visible at normal
    // viewing distance, which is the only bar that matters here.
    // ------------------------------------------------------------------

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

    /// <summary>
    /// Smallest-three quaternion encoding in 4 bytes: 2 bits naming the largest
    /// component, then the other three at 10 bits each. The largest component is
    /// recovered from the unit-length constraint, and negating the quaternion when
    /// it is negative (q and -q are the same rotation) means its sign never has to
    /// be sent.
    /// </summary>
    private int WriteRotation(byte[] buffer, int offset, Quaternion rotation)
    {
        float x = rotation.x, y = rotation.y, z = rotation.z, w = rotation.w;

        int largest = 0;
        float largestValue = Mathf.Abs(x);
        if (Mathf.Abs(y) > largestValue) { largest = 1; largestValue = Mathf.Abs(y); }
        if (Mathf.Abs(z) > largestValue) { largest = 2; largestValue = Mathf.Abs(z); }
        if (Mathf.Abs(w) > largestValue) { largest = 3; largestValue = Mathf.Abs(w); }

        // Force the dropped component positive so it can be reconstructed as a
        // plain square root.
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

    // ------------------------------------------------------------------
    // Debug / stress-test accessors
    // ------------------------------------------------------------------

    public int _GetRegisteredCount() { return registeredCount; }
    public int _GetAwakeCount() { return awakeCount; }
    public int _GetDirtyCount() { return dirtyCount; }
    public SmartSyncChannel _GetLocalChannel() { return localChannel; }

    /// <summary>Outbound bytes/sec on the local channel, for the debug overlay.</summary>
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
