using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SmartSyncStressTest : UdonSharpBehaviour
{
    [Header("Wiring (optional - resolved automatically if left empty)")]
    public SmartSyncManager manager;

    [Tooltip("Scene object holding the SmartSyncManager. Only used when manager is left empty.")]
    public string managerObjectName = "SyncSystem";

    [Header("Scatter area")]
    [Tooltip("Center of the volume objects are scattered into.")]
    public Vector3 areaCenter = Vector3.zero;

    [Tooltip("Size of the scatter volume. Keep it inside the manager's worldExtent or positions will clamp.")]
    public Vector3 areaSize = new Vector3(40f, 8f, 40f);

    [Header("Load")]
    [Tooltip("Objects touched per operation. Keeps ownership transfers under VRChat's rate limit.")]
    public int batchSize = 25;

    [Tooltip("Impulse strength (m/s) applied by the churn and impulse operations.")]
    public float impulseStrength = 4f;

    [Header("Continuous churn")]
    [Tooltip("Repeatedly impulse random objects to simulate sustained activity.")]
    public bool churnEnabled;

    [Tooltip("Seconds between churn bursts.")]
    public float churnInterval = 1f;

    [Tooltip("Objects impulsed per burst.")]
    public int churnCount = 10;

    private int cursor;
    private float nextChurnTime;

    void Start()
    {
        if (manager == null && managerObjectName.Length > 0)
        {
            GameObject holder = GameObject.Find(managerObjectName);
            if (holder != null) manager = holder.GetComponent<SmartSyncManager>();
        }

        if (manager == null)
        {
            Debug.LogError("[SmartSyncStressTest] No SmartSyncManager found. Assign one, or name the holder '" + managerObjectName + "'.");
            enabled = false;
        }
    }

    void Update()
    {
        if (!churnEnabled || manager == null) return;
        if (Time.time < nextChurnTime) return;
        nextChurnTime = Time.time + churnInterval;

        ImpulseBatch(churnCount);
    }

    public void _Scatter()
    {
        int count = manager._GetRegisteredCount();
        if (count == 0) return;

        Vector3 half = areaSize * 0.5f;
        int touched = 0;

        for (int i = 0; i < count && touched < batchSize; i++)
        {
            SmartSyncObject obj = NextObject(count);
            if (obj == null) continue;
            if (!TakeOwnership(obj)) continue;

            Vector3 position = areaCenter + new Vector3(
                Random.Range(-half.x, half.x),
                Random.Range(-half.y, half.y),
                Random.Range(-half.z, half.z));

            obj.transform.position = position;
            obj.transform.rotation = Random.rotation;
            obj._FlagDiscontinuity();
            touched++;
        }
    }

    public void _Impulse()
    {
        ImpulseBatch(batchSize);
    }

    private void ImpulseBatch(int amount)
    {
        int count = manager._GetRegisteredCount();
        if (count == 0) return;

        int touched = 0;
        for (int i = 0; i < count && touched < amount; i++)
        {
            SmartSyncObject obj = NextObject(count);
            if (obj == null || obj.body == null) continue;
            if (!TakeOwnership(obj)) continue;

            Vector3 impulse = new Vector3(
                Random.Range(-1f, 1f),
                Random.Range(0.4f, 1f),
                Random.Range(-1f, 1f)).normalized * impulseStrength;

            obj.body.isKinematic = false;
            obj.body.velocity = impulse;
            obj.body.angularVelocity = Random.insideUnitSphere * impulseStrength;
            obj._ForceWake();
            touched++;
        }
    }

    public void _Settle()
    {
        int count = manager._GetRegisteredCount();
        if (count == 0) return;

        int touched = 0;
        for (int i = 0; i < count && touched < batchSize; i++)
        {
            SmartSyncObject obj = NextObject(count);
            if (obj == null) continue;
            if (!TakeOwnership(obj)) continue;

            if (obj.body != null)
            {
                obj.body.velocity = Vector3.zero;
                obj.body.angularVelocity = Vector3.zero;
                obj.body.Sleep();
            }
            touched++;
        }
    }

    public void _ToggleChurn()
    {
        churnEnabled = !churnEnabled;
        nextChurnTime = 0f;
    }

    public override void Interact()
    {
        _Impulse();
    }

    private SmartSyncObject NextObject(int count)
    {
        SmartSyncObject obj = manager._GetObject(cursor);
        cursor++;
        if (cursor >= count) cursor = 0;
        return obj;
    }

    private bool TakeOwnership(SmartSyncObject obj)
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        if (local == null || !Utilities.IsValid(local)) return false;

        if (Networking.IsOwner(local, obj.gameObject)) return true;

        Networking.SetOwner(local, obj.gameObject);
        return Networking.IsOwner(local, obj.gameObject);
    }
}
