using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SmartSyncObject : UdonSharpBehaviour
{

    public const int STATE_SLEEPING = 0;
    public const int STATE_HELD = 1;
    public const int STATE_PHYSICS = 2;
    public const int STATE_ATTACHED = 3;
    public const int STATE_TELEPORT = 4;

    [Header("Wiring")]
    [Tooltip("The scene's SmartSyncManager. Leave empty to have it found at registration time.")]
    public SmartSyncManager manager;

    [Tooltip("Optional. Auto-fetched from this GameObject if left empty.")]
    public Rigidbody body;

    [Tooltip("Optional. Auto-fetched from this GameObject if left empty. Null is fine for non-pickups.")]
    public VRC_Pickup pickup;

    [Header("Sleep / wake thresholds")]
    [Tooltip("Speed (m/s) below which the object is a candidate for sleeping.")]
    public float sleepLinearThreshold = 0.02f;

    [Tooltip("Speed (m/s) that wakes a sleeping object. Must be > sleepLinearThreshold: the gap IS the hysteresis.")]
    public float wakeLinearThreshold = 0.06f;

    [Tooltip("Angular speed (rad/s) below which the object is a candidate for sleeping.")]
    public float sleepAngularThreshold = 0.05f;

    [Tooltip("Angular speed (rad/s) that wakes a sleeping object.")]
    public float wakeAngularThreshold = 0.15f;

    [Tooltip("Consecutive slow ticks required before sleeping. At 90fps, 30 is about a third of a second.")]
    public int sleepTickCount = 30;

    [Tooltip("Minimum time (s) an object stays awake after waking. Stops pickup/drop flicker.")]
    public float minAwakeTime = 0.25f;

    [Tooltip("Radius (m) searched for sleeping neighbours when this object is grabbed. Covers stacks whose upper objects are owned by another player. 0 disables.")]
    public float neighbourWakeRadius = 1.5f;

    [Header("Significant-change thresholds")]
    [Tooltip("Position change (m) since the last sent state that marks this object dirty.")]
    public float positionDeltaThreshold = 0.01f;

    [Tooltip("Rotation change (degrees) since the last sent state that marks this object dirty.")]
    public float rotationDeltaThreshold = 0.5f;

    [Header("Remote playback")]
    [Tooltip("How fast remote objects converge on the networked pose. Higher is snappier and jerkier.")]
    public float remoteSmoothing = 12f;

    [Tooltip("Extrapolate remote physics objects along their last known velocity between packets.")]
    public bool remoteExtrapolation = true;

    [System.NonSerialized] public int syncId = -1;
    [System.NonSerialized] public int state = STATE_SLEEPING;
    [System.NonSerialized] public bool isAwake;
    [System.NonSerialized] public bool isDirty;
    [System.NonSerialized] public bool needsFullSnapshot;
    [System.NonSerialized] public bool discontinuous;

    [System.NonSerialized] public int heldPlayerId = -1;
    [System.NonSerialized] public bool heldInLeftHand;
    [System.NonSerialized] public Vector3 heldLocalPosition;
    [System.NonSerialized] public Quaternion heldLocalRotation = Quaternion.identity;

    private Vector3 lastSentPosition;
    private Quaternion lastSentRotation = Quaternion.identity;

    private Vector3 netPosition;
    private Quaternion netRotation = Quaternion.identity;
    private Vector3 netVelocity;
    private Vector3 netAngularVelocity;
    private float netReceiveTime;

    private int slowTicks;
    private float awakeUntil;
    private bool bodyWasKinematic;
    private bool bodyUsedGravity;
    private bool remotePhysicsSuspended;
    private bool registered;

    private bool packedFullSnapshot;
    private bool packedDiscontinuous;

    void Start()
    {
        if (body == null) body = GetComponent<Rigidbody>();
        if (pickup == null) pickup = GetComponent<VRC_Pickup>();

        if (body != null)
        {
            bodyWasKinematic = body.isKinematic;
            bodyUsedGravity = body.useGravity;
        }

        if (wakeLinearThreshold <= sleepLinearThreshold) wakeLinearThreshold = sleepLinearThreshold * 3f;
        if (wakeAngularThreshold <= sleepAngularThreshold) wakeAngularThreshold = sleepAngularThreshold * 3f;

        lastSentPosition = transform.position;
        lastSentRotation = transform.rotation;
        netPosition = lastSentPosition;
        netRotation = lastSentRotation;

        if (manager != null) manager._Register(this);
    }

    public void _OnRegistered(SmartSyncManager owningManager, int assignedId)
    {
        manager = owningManager;
        syncId = assignedId;
        registered = true;
    }

    public void _Tick(float deltaTime)
    {
        if (Networking.IsOwner(gameObject)) TickOwner(deltaTime);
        else TickRemote(deltaTime);
    }

    private void TickOwner(float deltaTime)
    {
        if (state == STATE_HELD)
        {
            UpdateHeldPose();
            MarkDirty();
            return;
        }

        if (HasMovedSignificantly()) MarkDirty();

        if (Time.time < awakeUntil) return;

        float speed = 0f;
        float angularSpeed = 0f;
        if (body != null && !body.isKinematic)
        {
            speed = body.velocity.magnitude;
            angularSpeed = body.angularVelocity.magnitude;
        }

        bool slow = speed < sleepLinearThreshold && angularSpeed < sleepAngularThreshold;
        if (slow) slowTicks++;
        else slowTicks = 0;

        bool bodyAsleep = body != null && body.IsSleeping();

        if (slowTicks >= sleepTickCount || (bodyAsleep && slow))
        {
            GoToSleep();
        }
    }

    private void TickRemote(float deltaTime)
    {
        if (state == STATE_HELD)
        {
            FollowHolderHand();
            return;
        }

        if (state == STATE_SLEEPING) return;

        Vector3 targetPosition = netPosition;
        if (remoteExtrapolation && state == STATE_PHYSICS)
        {
            targetPosition += netVelocity * (Time.time - netReceiveTime);
        }

        if (discontinuous)
        {
            transform.SetPositionAndRotation(targetPosition, netRotation);
            discontinuous = false;
            return;
        }

        float t = 1f - Mathf.Exp(-remoteSmoothing * deltaTime);
        transform.SetPositionAndRotation(
            Vector3.Lerp(transform.position, targetPosition, t),
            Quaternion.Slerp(transform.rotation, netRotation, t));
    }

    public void _ForceWake()
    {
        awakeUntil = Time.time + minAwakeTime;
        slowTicks = 0;

        if (isAwake) return;
        isAwake = true;

        if (state == STATE_SLEEPING) state = STATE_PHYSICS;

        if (manager != null) manager._OnObjectWoke(this);
        MarkDirty();
    }

    public void _ForceWakeFullSnapshot()
    {
        needsFullSnapshot = true;
        _ForceWake();
    }

    public void _FlagDiscontinuity()
    {
        discontinuous = true;
        _ForceWakeFullSnapshot();
    }

    private void GoToSleep()
    {
        if (!isAwake) return;

        isAwake = false;
        state = STATE_SLEEPING;
        slowTicks = 0;

        MarkDirty();

        if (manager != null) manager._OnObjectSleeping(this);
    }

    private void MarkDirty()
    {
        if (isDirty) return;

        if (!Networking.IsOwner(gameObject)) return;

        isDirty = true;
        if (manager != null) manager._OnObjectDirty(this);
    }

    public void _ClearDirty()
    {
        if (!isDirty) return;
        isDirty = false;
        if (manager != null) manager._OnObjectClean(this);
    }

    public void _OnPacked()
    {
        packedFullSnapshot = needsFullSnapshot;
        packedDiscontinuous = discontinuous;

        isDirty = false;
        needsFullSnapshot = false;

        discontinuous = false;

        lastSentPosition = transform.position;
        lastSentRotation = transform.rotation;
    }

    public void _Requeue()
    {
        isDirty = true;
        if (packedFullSnapshot) needsFullSnapshot = true;
        if (packedDiscontinuous) discontinuous = true;
    }

    private bool HasMovedSignificantly()
    {
        Vector3 delta = transform.position - lastSentPosition;
        if (delta.sqrMagnitude > positionDeltaThreshold * positionDeltaThreshold) return true;
        return Quaternion.Angle(transform.rotation, lastSentRotation) > rotationDeltaThreshold;
    }

    private void UpdateHeldPose()
    {
        if (pickup == null) return;

        VRCPlayerApi holder = pickup.currentPlayer;
        if (holder == null || !Utilities.IsValid(holder)) return;

        heldPlayerId = holder.playerId;
        heldInLeftHand = pickup.currentHand == VRC_Pickup.PickupHand.Left;

        Vector3 bonePosition = GetHandPosition(holder, heldInLeftHand);
        Quaternion boneRotation = GetHandRotation(holder, heldInLeftHand);

        Quaternion inverse = Quaternion.Inverse(boneRotation);
        heldLocalPosition = inverse * (transform.position - bonePosition);
        heldLocalRotation = inverse * transform.rotation;
    }

    private void FollowHolderHand()
    {
        if (heldPlayerId < 0) return;

        VRCPlayerApi holder = VRCPlayerApi.GetPlayerById(heldPlayerId);
        if (holder == null || !Utilities.IsValid(holder)) return;

        Vector3 bonePosition = GetHandPosition(holder, heldInLeftHand);
        Quaternion boneRotation = GetHandRotation(holder, heldInLeftHand);

        transform.SetPositionAndRotation(
            bonePosition + boneRotation * heldLocalPosition,
            boneRotation * heldLocalRotation);
    }

    private Vector3 GetHandPosition(VRCPlayerApi player, bool leftHand)
    {
        HumanBodyBones bone = leftHand ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
        Vector3 position = player.GetBonePosition(bone);
        if (position.sqrMagnitude < 0.0001f) return player.GetPosition();
        return position;
    }

    private Quaternion GetHandRotation(VRCPlayerApi player, bool leftHand)
    {
        HumanBodyBones bone = leftHand ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
        Vector3 position = player.GetBonePosition(bone);
        if (position.sqrMagnitude < 0.0001f) return player.GetRotation();
        return player.GetBoneRotation(bone);
    }

    public override void OnPickup()
    {
        state = STATE_HELD;
        SuspendRemotePhysics(false);
        UpdateHeldPose();
        _ForceWakeFullSnapshot();

        if (manager != null && neighbourWakeRadius > 0f)
        {
            manager._WakeNear(transform.position, neighbourWakeRadius);
        }
    }

    public override void OnDrop()
    {
        state = STATE_PHYSICS;
        heldPlayerId = -1;
        awakeUntil = Time.time + Mathf.Max(minAwakeTime, 1f);
        slowTicks = 0;
        _ForceWakeFullSnapshot();
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        bool localOwns = player != null && player.isLocal;
        SuspendRemotePhysics(!localOwns);

        if (localOwns)
        {
            _ForceWakeFullSnapshot();
        }
        else
        {
            _ClearDirty();
            _ForceWake();
        }
    }

    void OnCollisionExit(Collision collision)
    {
        if (!Networking.IsOwner(gameObject)) return;

        if (body != null && !body.isKinematic) body.WakeUp();

        _ForceWake();
    }

    void OnCollisionEnter(Collision collision)
    {
        if (!Networking.IsOwner(gameObject)) return;
        if (isAwake)
        {
            awakeUntil = Time.time + minAwakeTime;
            slowTicks = 0;
            return;
        }

        if (collision.relativeVelocity.sqrMagnitude > wakeLinearThreshold * wakeLinearThreshold)
        {
            _ForceWake();
        }
    }

    public void _ApplyNetworkState(int newState, Vector3 position, Quaternion rotation,
                                   Vector3 velocity, Vector3 angularVelocity, bool isDiscontinuous)
    {
        state = newState;
        netPosition = position;
        netRotation = rotation;
        netVelocity = velocity;
        netAngularVelocity = angularVelocity;
        netReceiveTime = Time.time;
        discontinuous = discontinuous || isDiscontinuous;

        SuspendRemotePhysics(true);

        if (newState == STATE_SLEEPING)
        {
            transform.SetPositionAndRotation(position, rotation);
            isAwake = false;
            if (manager != null) manager._OnObjectSleeping(this);
            return;
        }

        if (!isAwake)
        {
            isAwake = true;
            if (manager != null) manager._OnObjectWoke(this);
        }
    }

    public void _ApplyHeldState(int playerId, bool leftHand, Vector3 localPosition, Quaternion localRotation)
    {
        bool justGrabbed = state != STATE_HELD;

        state = STATE_HELD;
        heldPlayerId = playerId;
        heldInLeftHand = leftHand;
        heldLocalPosition = localPosition;
        heldLocalRotation = localRotation;

        SuspendRemotePhysics(true);
        FollowHolderHand();

        if (!isAwake)
        {
            isAwake = true;
            if (manager != null) manager._OnObjectWoke(this);
        }

        if (justGrabbed && manager != null && neighbourWakeRadius > 0f)
        {
            manager._WakeNear(transform.position, neighbourWakeRadius);
        }
    }

    private void SuspendRemotePhysics(bool suspend)
    {
        if (body == null || remotePhysicsSuspended == suspend) return;
        remotePhysicsSuspended = suspend;

        if (suspend)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
            body.useGravity = false;
        }
        else
        {
            body.isKinematic = bodyWasKinematic;
            body.useGravity = bodyUsedGravity;
        }
    }

    public Vector3 _GetPosition() { return transform.position; }
    public Quaternion _GetRotation() { return transform.rotation; }

    public Vector3 _GetVelocity()
    {
        if (body == null || body.isKinematic) return Vector3.zero;
        return body.velocity;
    }

    public Vector3 _GetAngularVelocity()
    {
        if (body == null || body.isKinematic) return Vector3.zero;
        return body.angularVelocity;
    }

    public bool _IsRegistered() { return registered; }
}
