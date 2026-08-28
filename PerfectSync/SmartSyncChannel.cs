using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;

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

    [UdonSynced] private int claimedPlayerId = -1;

    [UdonSynced] private byte[] packet;

    private byte[] scratch;
    private bool isLocalChannel;
    private bool initialized;

    private int lastPacketBytes;
    private int windowBytes;
    private float windowStart;
    private float bytesPerSecond;
    private int sendFailures;

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

    public void _Release()
    {
        if (!Networking.IsOwner(gameObject)) return;
        claimedPlayerId = -1;
        isLocalChannel = false;
        packet = null;
        RequestSerialization();
    }

    public bool _TrySend()
    {
        if (!initialized || manager == null) return false;
        if (!_IsLocalChannel()) return false;

        int written = manager._WritePacket(scratch, maxPacketBytes);
        if (written <= 0) return false;

        if (packet == null || packet.Length != written)
        {
            packet = new byte[written];
        }
        for (int i = 0; i < written; i++) packet[i] = scratch[i];

        RequestSerialization();
        return true;
    }

    public override void OnDeserialization()
    {
        if (manager == null || packet == null) return;

        if (isLocalChannel && Networking.IsOwner(gameObject)) return;

        manager._ReadPacket(packet, packet.Length);
    }

    public override void OnPostSerialization(SerializationResult result)
    {
        if (!result.success)
        {
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
        if (player == null || !player.isLocal)
        {
            isLocalChannel = false;
        }
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        if (player == null) return;
        if (player.playerId != claimedPlayerId) return;

        VRCPlayerApi local = Networking.LocalPlayer;
        if (local == null || !local.isMaster) return;

        Networking.SetOwner(local, gameObject);
        claimedPlayerId = -1;
        isLocalChannel = false;
        packet = null;
        RequestSerialization();
    }

    public int _GetClaimedPlayerId() { return claimedPlayerId; }
    public int _GetLastPacketBytes() { return lastPacketBytes; }
    public float _GetBytesPerSecond() { return bytesPerSecond; }
    public int _GetSendFailures() { return sendFailures; }
}
