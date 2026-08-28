using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;

/// <summary>
/// Per-player transport for the sync system. Put a small pool of these in the
/// scene (8-16 GameObjects is plenty); each player claims one and sends only the
/// objects that player owns.
///
/// WHY A POOL INSTEAD OF ONE CENTRAL SENDER
/// VRChat ownership is per-GameObject, and only the owner of a behaviour may write
/// its synced variables. Objects are owned by whoever grabbed them, so no single
/// behaviour can legally serialize the whole world. The alternative -- relaying
/// every non-owner's state through the master with network events -- adds a round
/// trip and spends the same rate limit twice. One channel per player is the only
/// shape that keeps each sender authoritative over exactly what it owns.
///
/// The channel is pure transport. It holds no game state and does no packing:
/// SmartSyncManager builds and parses the bytes.
///
/// This behaviour has no Update. The manager ticks the local channel on its send
/// timer, so unclaimed and remote channels cost nothing.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class SmartSyncChannel : UdonSharpBehaviour
{
    [Header("Wiring (optional - resolved automatically if left empty)")]
    [Tooltip("The scene's SmartSyncManager.")]
    public SmartSyncManager manager;

    [Tooltip("Scene object holding the SmartSyncManager. Only used when manager is left empty.")]
    public string managerObjectName = "SyncSystem";

    [Header("Packet budget")]
    [Tooltip("Maximum bytes per packet. VRChat allows far more, but small frequent packets beat large rare ones for responsiveness.")]
    public int maxPacketBytes = 1024;

    // ------------------------------------------------------------------
    // Synced state
    // ------------------------------------------------------------------

    // Who owns this channel. -1 means free. Synced separately from the packet so
    // players can see which channels are available before trying to claim one.
    [UdonSynced] private int claimedPlayerId = -1;

    // The packet itself. Udon serializes the entire array, so this is resized to
    // exactly the bytes used -- a fixed 1 KB array would spend 1 KB of bandwidth
    // on padding every single send.
    [UdonSynced] private byte[] packet;

    // ------------------------------------------------------------------
    // Local state
    // ------------------------------------------------------------------

    private byte[] scratch;          // reusable staging buffer; packing never allocates
    private bool isLocalChannel;
    private bool initialized;

    // Debug counters, read by the overlay.
    private int lastPacketBytes;
    private int windowBytes;
    private float windowStart;
    private float bytesPerSecond;
    private int sendFailures;

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    void Start()
    {
        EnsureInitialized();

        if (manager == null && managerObjectName.Length > 0)
        {
            GameObject holder = GameObject.Find(managerObjectName);
            if (holder != null) manager = holder.GetComponent<SmartSyncManager>();
        }

        if (manager == null)
        {
            Debug.LogError("[SmartSyncChannel] No SmartSyncManager found. Assign one, or name the holder '" + managerObjectName + "'.");
            return;
        }

        manager._RegisterChannel(this);
    }

    private void EnsureInitialized()
    {
        if (initialized) return;
        if (maxPacketBytes < 64) maxPacketBytes = 64;
        scratch = new byte[maxPacketBytes];
        windowStart = Time.time;
        initialized = true;
    }

    // ------------------------------------------------------------------
    // Claiming
    // ------------------------------------------------------------------

    /// <summary>
    /// True when no present player holds this channel. A channel whose claimant
    /// has left counts as free: players leaving is the normal way channels are
    /// recycled, and waiting for the master to tidy up would strand them.
    /// </summary>
    public bool _IsFree()
    {
        if (claimedPlayerId < 0) return true;
        VRCPlayerApi claimant = VRCPlayerApi.GetPlayerById(claimedPlayerId);
        return claimant == null || !Utilities.IsValid(claimant);
    }

    public bool _IsLocalChannel()
    {
        return isLocalChannel && Networking.IsOwner(gameObject);
    }

    /// <summary>
    /// Attempts to take this channel for the local player. Two players can race
    /// here; the loser finds out in OnDeserialization or OnOwnershipTransferred
    /// and goes looking for another channel.
    /// </summary>
    public void _ClaimForLocalPlayer()
    {
        EnsureInitialized();

        VRCPlayerApi local = Networking.LocalPlayer;
        if (local == null || !Utilities.IsValid(local)) return;

        if (!Networking.IsOwner(gameObject))
        {
            Networking.SetOwner(local, gameObject);
        }

        claimedPlayerId = local.playerId;
        isLocalChannel = true;
        RequestSerialization();
    }

    /// <summary>Releases the channel back to the pool. Only the owner may do this.</summary>
    public void _Release()
    {
        if (!Networking.IsOwner(gameObject)) return;
        claimedPlayerId = -1;
        isLocalChannel = false;
        packet = null;
        RequestSerialization();
    }

    // ------------------------------------------------------------------
    // Sending
    // ------------------------------------------------------------------

    /// <summary>
    /// Packs whatever is dirty and locally owned, then requests serialization.
    /// Returns true if a packet went out. The manager calls this on its send
    /// timer; there is deliberately no Update here.
    /// </summary>
    public bool _TrySend()
    {
        if (!initialized || manager == null) return false;
        if (!_IsLocalChannel()) return false;

        int written = manager._WritePacket(scratch, maxPacketBytes);
        if (written <= 0) return false; // nothing dirty: stay silent, send no packet

        // Exact-size copy. This allocates, but only a few hundred bytes at the
        // send rate (~10 Hz), and it is the difference between paying for the
        // bytes used and paying for the whole buffer every time.
        if (packet == null || packet.Length != written)
        {
            packet = new byte[written];
        }
        for (int i = 0; i < written; i++) packet[i] = scratch[i];

        RequestSerialization();
        return true;
    }

    // ------------------------------------------------------------------
    // VRChat networking callbacks
    // ------------------------------------------------------------------

    public override void OnDeserialization()
    {
        if (manager == null || packet == null) return;

        // Our own channel echoes nothing back to us, but a channel we just lost
        // ownership of might still deliver one late packet. Ignore it.
        if (isLocalChannel && Networking.IsOwner(gameObject)) return;

        manager._ReadPacket(packet, packet.Length);
    }

    public override void OnPostSerialization(SerializationResult result)
    {
        if (!result.success)
        {
            // Almost always the manual-sync rate limit. The packed objects were
            // already marked clean, so without this they would never be resent --
            // fine for something still moving, fatal for an object whose final
            // resting pose was in the lost packet. Put them back in the queue.
            sendFailures++;
            if (manager != null) manager._RequeueLastPacket();
            return;
        }

        lastPacketBytes = result.byteCount;
        windowBytes += result.byteCount;

        float elapsed = Time.time - windowStart;
        if (elapsed >= 1f)
        {
            bytesPerSecond = windowBytes / elapsed;
            windowBytes = 0;
            windowStart = Time.time;
        }
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        // Lost the race, or someone took the channel. Stop treating it as ours;
        // the manager will notice it has no channel and claim another.
        if (player == null || !player.isLocal)
        {
            isLocalChannel = false;
        }
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        if (player == null) return;
        if (player.playerId != claimedPlayerId) return;

        // Recycle the abandoned channel. Master does it so exactly one client acts.
        VRCPlayerApi local = Networking.LocalPlayer;
        if (local == null || !local.isMaster) return;

        Networking.SetOwner(local, gameObject);
        claimedPlayerId = -1;
        isLocalChannel = false;
        packet = null;
        RequestSerialization();
    }

    // ------------------------------------------------------------------
    // Debug accessors
    // ------------------------------------------------------------------

    public int _GetClaimedPlayerId() { return claimedPlayerId; }
    public int _GetLastPacketBytes() { return lastPacketBytes; }
    public float _GetBytesPerSecond() { return bytesPerSecond; }
    public int _GetSendFailures() { return sendFailures; }
}
