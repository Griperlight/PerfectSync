using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>
/// Lightweight per-object sync component. One of these goes on every networked
/// object; SmartSyncManager owns the networking.
///
/// THE IMPORTANT DESIGN DECISION: this behaviour has no Update, FixedUpdate or
/// LateUpdate. Unity only pays the call cost for messages a script actually
/// declares, so 1000 sleeping objects cost literally zero frame time. The manager
/// drives _Tick() on the objects in its awake set and nothing else. Disabling the
/// behaviour instead would have been worse: a disabled behaviour stops receiving
/// OnCollisionEnter, so it could never wake itself back up.
///
/// Wake sources that survive with no Update:
///   - OnPickup / OnDrop            (VRC events)
///   - OnOwnershipTransferred       (VRC event)
///   - OnCollisionEnter             (Unity, needs the behaviour enabled - it is)
///   - OnCollisionExit              (support removed from under a stack)
///   - _ForceWake()                 (manager or any other system)
///   - manager._WakeNear()          (a neighbour was grabbed)
///
/// The object never serializes anything itself. It reports state, and the manager
/// decides what goes into the next packet.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SmartSyncObject : UdonSharpBehaviour
{
    // ------------------------------------------------------------------
    // State machine
    //
    // int constants rather than an enum: UdonSharp inlines them, they pack
    // straight into the state-flag nibble, and they cross behaviour boundaries
    // without any marshalling.
    // ------------------------------------------------------------------

    public const int STATE_SLEEPING = 0;   // no network traffic, no tick
    public const int STATE_HELD = 1;       // relative hand pose only
    public const int STATE_PHYSICS = 2;    // quantized position + rotation + velocity
    public const int STATE_ATTACHED = 3;   // parent reference + local offset
    public const int STATE_TELEPORT = 4;   // discontinuity: snap, do not interpolate

    // ------------------------------------------------------------------
    // Inspector configuration
    // ------------------------------------------------------------------

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

    // ------------------------------------------------------------------
    // Runtime state (never serialized by Unity, never synced by Udon)
    // ------------------------------------------------------------------

    [System.NonSerialized] public int syncId = -1;      // slot index in the manager and the grid
    [System.NonSerialized] public int state = STATE_SLEEPING;
    [System.NonSerialized] public bool isAwake;
    [System.NonSerialized] public bool isDirty;         // has changed since the manager last packed it
    [System.NonSerialized] public bool needsFullSnapshot; // ownership change / teleport: send everything
    [System.NonSerialized] public bool discontinuous;   // remotes must snap, not interpolate

    // Held pose, expressed relative to the holding hand bone. Far cheaper to send
    // than world space, and it stays glued to the hand on the remote side.
    [System.NonSerialized] public int heldPlayerId = -1;
    [System.NonSerialized] public bool heldInLeftHand;
    [System.NonSerialized] public Vector3 heldLocalPosition;
    [System.NonSerialized] public Quaternion heldLocalRotation = Quaternion.identity;

    // Last pose handed to the manager, used for significant-change detection.
    private Vector3 lastSentPosition;
    private Quaternion lastSentRotation = Quaternion.identity;

    // Networked pose most recently received, used by remote playback.
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

    // One-shot flags as they stood when the object was last packed, so a failed
    // send can put them back.
    private bool packedFullSnapshot;
    private bool packedDiscontinuous;

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    void Start()
    {
        if (body == null) body = GetComponent<Rigidbody>();
        if (pickup == null) pickup = GetComponent<VRC_Pickup>();

        if (body != null)
        {
            bodyWasKinematic = body.isKinematic;
            bodyUsedGravity = body.useGravity;
        }

        // Guard the hysteresis config: equal thresholds would let the object
        // oscillate between awake and asleep on a single frame of noise.
        if (wakeLinearThreshold <= sleepLinearThreshold) wakeLinearThreshold = sleepLinearThreshold * 3f;
        if (wakeAngularThreshold <= sleepAngularThreshold) wakeAngularThreshold = sleepAngularThreshold * 3f;

        lastSentPosition = transform.position;
        lastSentRotation = transform.rotation;
        netPosition = lastSentPosition;
        netRotation = lastSentRotation;

        if (manager != null) manager._Register(this);
    }

    /// <summary>Called by the manager when it hands out this object's slot index.</summary>
    public void _OnRegistered(SmartSyncManager owningManager, int assignedId)
    {
        manager = owningManager;
        syncId = assignedId;
        registered = true;
    }

    // ------------------------------------------------------------------
    // Manager-driven tick. Only runs while awake.
    // ------------------------------------------------------------------

    /// <summary>
    /// One frame of work. The manager calls this for awake objects only, which is
    /// what keeps the sleeping majority free.
    /// </summary>
    public void _Tick(float deltaTime)
    {
        if (Networking.IsOwner(gameObject)) TickOwner(deltaTime);
        else TickRemote(deltaTime);
    }

    private void TickOwner(float deltaTime)
    {
        if (state == STATE_HELD)
        {
            // Held objects never sleep and are always dirty: the hand moves every
            // frame, and the relative pose is only a few bytes.
            UpdateHeldPose();
            MarkDirty();
            return;
        }

        // Track the pose so the manager can pack it.
        if (HasMovedSignificantly()) MarkDirty();

        // Sleep evaluation. Two thresholds plus a consecutive-tick counter plus a
        // minimum awake time: three layers of hysteresis, because a single one
        // still lets objects flicker when they settle on uneven geometry.
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

        // Unity's own solver sleep is a strong signal: trust it and skip the wait.
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
            // Dead-reckon along the last known velocity so the object keeps moving
            // between packets instead of stepping.
            targetPosition += netVelocity * (Time.time - netReceiveTime);
        }

        if (discontinuous)
        {
            // Teleport: snap. Interpolating here is what makes objects sail across
            // the room when they respawn.
            transform.SetPositionAndRotation(targetPosition, netRotation);
            discontinuous = false;
            return;
        }

        float t = 1f - Mathf.Exp(-remoteSmoothing * deltaTime); // framerate-independent lerp
        transform.SetPositionAndRotation(
            Vector3.Lerp(transform.position, targetPosition, t),
            Quaternion.Slerp(transform.rotation, netRotation, t));
    }

    // ------------------------------------------------------------------
    // Wake / sleep
    // ------------------------------------------------------------------

    /// <summary>
    /// Wakes the object and tells the manager to start ticking and syncing it.
    /// Cheap and idempotent, so it is safe to call from collision handlers.
    /// </summary>
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

    /// <summary>Wakes the object and forces a complete state resend, not a delta.</summary>
    public void _ForceWakeFullSnapshot()
    {
        needsFullSnapshot = true;
        _ForceWake();
    }

    /// <summary>
    /// Marks the next update as a teleport so remotes snap instead of sliding.
    /// The FlagDiscontinuity equivalent.
    /// </summary>
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

        // Send one final resting pose, otherwise remotes keep whatever half-settled
        // position the last packet happened to carry.
        MarkDirty();

        if (manager != null) manager._OnObjectSleeping(this);
    }

    private void MarkDirty()
    {
        if (isDirty) return;

        // Only the owner may publish state. Queueing anything else fills the
        // manager's dirty set with entries no send can ever consume, and every
        // later packet then pays to scan past them.
        if (!Networking.IsOwner(gameObject)) return;

        isDirty = true;
        if (manager != null) manager._OnObjectDirty(this);
    }

    /// <summary>
    /// Drops any queued state. Used when ownership moves away: whatever was
    /// pending is no longer ours to send.
    /// </summary>
    public void _ClearDirty()
    {
        if (!isDirty) return;
        isDirty = false;
        if (manager != null) manager._OnObjectClean(this);
    }

    /// <summary>Called by the manager once this object has been packed into a packet.</summary>
    public void _OnPacked()
    {
        // Remembered so a failed send can restore them. Both are one-shot flags:
        // losing them to a dropped packet would silently downgrade the resend.
        packedFullSnapshot = needsFullSnapshot;
        packedDiscontinuous = discontinuous;

        isDirty = false;
        needsFullSnapshot = false;

        // Owner-side the flag is consumed by the packet. Leaving it set made every
        // subsequent update carry it, so remotes snapped forever instead of
        // interpolating once.
        discontinuous = false;

        lastSentPosition = transform.position;
        lastSentRotation = transform.rotation;
    }

    /// <summary>
    /// Puts this object back in the send queue after a failed serialization,
    /// restoring the one-shot flags the packet had consumed.
    /// </summary>
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

    // ------------------------------------------------------------------
    // Held state
    // ------------------------------------------------------------------

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

        // Reconstructed straight from the hand every frame, so it stays locked to
        // the holder with no interpolation and no extra packets.
        transform.SetPositionAndRotation(
            bonePosition + boneRotation * heldLocalPosition,
            boneRotation * heldLocalRotation);
    }

    /// <summary>
    /// Hand bone position, falling back to the player root. GetBonePosition returns
    /// zero when the bone is unavailable (some avatars, some tracking states), and
    /// blindly trusting that drops objects at the world origin.
    /// </summary>
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

    // ------------------------------------------------------------------
    // VRChat events
    // ------------------------------------------------------------------

    public override void OnPickup()
    {
        state = STATE_HELD;
        SuspendRemotePhysics(false);
        UpdateHeldPose();
        _ForceWakeFullSnapshot();

        // Anything we own that was resting on this needs to start falling.
        if (manager != null && neighbourWakeRadius > 0f)
        {
            manager._WakeNear(transform.position, neighbourWakeRadius);
        }
    }

    public override void OnDrop()
    {
        // Straight into physics with a guaranteed awake window, so the throw arc
        // is actually transmitted instead of being swallowed by an early sleep.
        state = STATE_PHYSICS;
        heldPlayerId = -1;
        awakeUntil = Time.time + Mathf.Max(minAwakeTime, 1f);
        slowTicks = 0;
        _ForceWakeFullSnapshot();
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        // The new owner runs physics locally; everyone else plays back the network
        // pose. Getting this wrong is the classic source of objects fighting
        // themselves and jittering.
        bool localOwns = player != null && player.isLocal;
        SuspendRemotePhysics(!localOwns);

        if (localOwns)
        {
            // Spec rule: ownership transfer forces a full state snapshot.
            _ForceWakeFullSnapshot();
        }
        else
        {
            // No longer ours to publish, so discard anything still queued before
            // waking it for remote playback.
            _ClearDirty();
            _ForceWake();
        }
    }

    /// <summary>
    /// Losing a contact almost always means whatever was holding this object up
    /// just moved away.
    ///
    /// This is the wake source that stacks depend on. Unity does NOT wake a
    /// sleeping rigidbody when a kinematic support is moved out from under it, and
    /// a remotely-owned object is kinematic on this client, so pulling the bottom
    /// cube out of a stack leaves the rest hanging in mid-air with nothing to
    /// notice. OnCollisionEnter cannot cover it: that only fires on gaining
    /// contact, which here happens after the fall, far too late.
    /// </summary>
    void OnCollisionExit(Collision collision)
    {
        if (!Networking.IsOwner(gameObject)) return;

        // Unity's solver may still have it asleep, so waking our own state is not
        // enough - the body has to be told to simulate again.
        if (body != null && !body.isKinematic) body.WakeUp();

        _ForceWake();
    }

    void OnCollisionEnter(Collision collision)
    {
        // Only the owner's simulation is authoritative, so only the owner turns a
        // collision into a wake. Remotes are just replaying a pose.
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

    // ------------------------------------------------------------------
    // Network state applied by the manager
    // ------------------------------------------------------------------

    /// <summary>Applies a physics-state update received from the owner.</summary>
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
            // Final resting pose: place it exactly and stop ticking.
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

    /// <summary>Applies a held-state update received from the owner.</summary>
    public void _ApplyHeldState(int playerId, bool leftHand, Vector3 localPosition, Quaternion localRotation)
    {
        // Fire once, on the transition into Held. Someone just picked this up, so
        // whatever we own that was stacked on it has to wake and fall -- the
        // grabber's client cannot do that for objects it does not own.
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

    /// <summary>
    /// Turns the local rigidbody off on remotes so it cannot fight the incoming
    /// network pose, and restores the authored settings when ownership comes back.
    /// </summary>
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

    // ------------------------------------------------------------------
    // Accessors used by the manager when packing
    // ------------------------------------------------------------------

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
