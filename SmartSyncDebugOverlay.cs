using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Readout for the sync system: awake count, bandwidth, grid health, frame cost.
///
/// Point it at either a UI Text or a TextMeshProUGUI (whichever you have) and it
/// writes to the one that is assigned.
///
/// String concatenation is genuinely expensive in Udon, so the text is rebuilt on
/// a timer rather than every frame. Frame timing is still sampled every frame,
/// because a value sampled 4x a second tells you nothing about hitches.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SmartSyncDebugOverlay : UdonSharpBehaviour
{
    [Header("Wiring (all optional - resolved automatically if left empty)")]
    public SmartSyncManager manager;
    public SpatialGrid grid;

    [Tooltip("Scene object holding the SmartSyncManager. Only used when manager is left empty.")]
    public string managerObjectName = "SyncSystem";

    [Tooltip("Assign one of these, or leave both empty to use the first text component found in children. TextMeshPro wins if both are set.")]
    public Text uiText;
    public TextMeshProUGUI tmpText;

    [Header("Display")]
    [Tooltip("Seconds between text rebuilds. 0.25 is readable without burning CPU on string building.")]
    public float refreshInterval = 0.25f;

    [Tooltip("Toggle the readout by interacting with this object.")]
    public bool toggleOnInteract = true;

    public bool visible = true;

    // Frame timing, sampled every frame and smoothed. Both the average and the
    // worst recent frame are shown: the average hides exactly the hitches you are
    // usually hunting for.
    private float smoothedDelta = 0.011f;
    private float worstDelta;
    private float worstResetTime;

    private float nextRefresh;

    void Start()
    {
        // Resolve everything that was left empty. Udon cannot build UI at runtime,
        // but it can find what already exists, which removes every inspector
        // reference from the setup steps.
        if (manager == null && managerObjectName.Length > 0)
        {
            GameObject holder = GameObject.Find(managerObjectName);
            if (holder != null) manager = holder.GetComponent<SmartSyncManager>();
        }

        if (manager == null)
        {
            Debug.LogError("[SmartSyncDebugOverlay] No SmartSyncManager found. Assign one, or name the holder '" + managerObjectName + "'.");
            enabled = false;
            return;
        }

        if (grid == null) grid = manager.grid;

        if (tmpText == null) tmpText = GetComponentInChildren<TextMeshProUGUI>();
        if (tmpText == null && uiText == null) uiText = GetComponentInChildren<Text>();

        if (tmpText == null && uiText == null)
        {
            Debug.LogError("[SmartSyncDebugOverlay] No Text or TextMeshProUGUI found in children.");
            enabled = false;
            return;
        }

        worstResetTime = Time.time;
    }

    void Update()
    {
        float delta = Time.unscaledDeltaTime;
        smoothedDelta = Mathf.Lerp(smoothedDelta, delta, 0.05f);
        if (delta > worstDelta) worstDelta = delta;

        if (!visible || Time.time < nextRefresh) return;
        nextRefresh = Time.time + refreshInterval;

        Rebuild();

        // Decay the worst-frame marker so an old spike does not sit there forever.
        if (Time.time - worstResetTime > 3f)
        {
            worstDelta = delta;
            worstResetTime = Time.time;
        }
    }

    private void Rebuild()
    {
        int registered = manager._GetRegisteredCount();
        int awake = manager._GetAwakeCount();
        int dirty = manager._GetDirtyCount();

        // The headline number. The whole design exists to keep this small: a few
        // dozen awake out of several hundred registered is the target.
        string text = "<b>PerfectSync</b>\n";
        text += "objects   " + awake + " awake / " + registered + " total\n";
        text += "dirty     " + dirty + "\n";

        if (registered > 0)
        {
            int percent = (int)((awake * 100f) / registered);
            text += "awake %   " + percent + "\n";
        }

        SmartSyncChannel channel = manager._GetLocalChannel();
        if (channel != null)
        {
            text += "\n<b>network</b>\n";
            text += "channel   player " + channel._GetClaimedPlayerId() + "\n";
            text += "rate      " + Mathf.RoundToInt(channel._GetBytesPerSecond()) + " B/s\n";
            text += "packet    " + channel._GetLastPacketBytes() + " B\n";

            int failures = channel._GetSendFailures();
            // Failures are the manual-sync rate limit pushing back. A climbing
            // count means sendInterval is too aggressive for the packet size.
            if (failures > 0) text += "failed    " + failures + "\n";
        }
        else
        {
            text += "\n<b>network</b>\nno channel claimed\n";
        }

        if (grid != null)
        {
            text += "\n<b>grid</b>\n";
            text += "cell      " + grid.GetCellSize() + " m" + (grid.use2D ? " (2D)" : "") + "\n";
            text += "indexed   " + grid.GetRegisteredCount() + "\n";
            text += "buckets   " + grid.GetOccupiedBucketCount() + "\n";

            // Long chains mean objects are piling into few cells: cellSize is too
            // large for the actual object density.
            text += "longest   " + grid.GetLongestChainLength() + "\n";
            if (grid.lastQueryOverflowed) text += "<b>query buffer overflow</b>\n";
        }

        text += "\n<b>frame</b>\n";
        // Rounded through ints: Udon does not reliably expose ToString(format).
        text += "avg       " + (Mathf.Round(smoothedDelta * 10000f) / 10f) + " ms (" + Mathf.RoundToInt(1f / smoothedDelta) + " fps)\n";
        text += "worst     " + (Mathf.Round(worstDelta * 10000f) / 10f) + " ms\n";

        Write(text);
    }

    private void Write(string text)
    {
        if (tmpText != null) tmpText.text = text;
        else if (uiText != null) uiText.text = text;
    }

    public override void Interact()
    {
        if (!toggleOnInteract) return;
        visible = !visible;
        if (!visible) Write("");
    }
}
