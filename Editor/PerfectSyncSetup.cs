using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using TMPro;
using UdonSharpEditor;

/// <summary>
/// Creates the PerfectSync debug canvas.
///
/// This is a plain Editor script, NOT an UdonSharpBehaviour. It lives in an Editor
/// folder so it never ships in a world build. Udon has no AddComponent and no
/// runtime GameObject creation, so building the canvas can only happen here.
///
/// Tools -> PerfectSync -> Create Debug Canvas
/// </summary>
public static class PerfectSyncSetup
{
    private const string OVERLAY_NAME = "SyncDebugOverlay";

    [MenuItem("Tools/PerfectSync/Create Debug Canvas")]
    public static void CreateDebugCanvas()
    {
        GameObject existing = GameObject.Find(OVERLAY_NAME);
        if (existing != null)
        {
            Selection.activeGameObject = existing;
            EditorGUIUtility.PingObject(existing);
            Debug.Log("[PerfectSync] '" + OVERLAY_NAME + "' already exists in this scene.");
            return;
        }

        GameObject canvasGo = new GameObject(OVERLAY_NAME);
        Undo.RegisterCreatedObjectUndo(canvasGo, "Create Debug Canvas");

        Canvas canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasGo.AddComponent<CanvasScaler>();
        canvasGo.AddComponent<GraphicRaycaster>();

        // World-space canvases are authored at pixel scale and then shrunk,
        // otherwise the text ends up meters tall.
        RectTransform canvasRect = canvasGo.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(600f, 760f);
        canvasRect.localScale = Vector3.one * 0.002f;
        canvasRect.position = new Vector3(0f, 2f, 4f);

        GameObject textGo = new GameObject("Readout");
        Undo.RegisterCreatedObjectUndo(textGo, "Create Debug Canvas");
        textGo.transform.SetParent(canvasGo.transform, false);

        TextMeshProUGUI tmp = textGo.AddComponent<TextMeshProUGUI>();
        tmp.fontSize = 24f;
        tmp.alignment = TextAlignmentOptions.TopLeft;
        tmp.text = "PerfectSync";

        RectTransform textRect = tmp.rectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(16f, 16f);
        textRect.offsetMax = new Vector2(-16f, -16f);

        SmartSyncDebugOverlay overlay = UdonSharpUndo.AddComponent<SmartSyncDebugOverlay>(canvasGo);
        overlay.tmpText = tmp;

        // The manager reference is left empty on purpose: the overlay resolves it
        // at runtime by name, so this works whether or not the rig exists yet.
        SmartSyncManager manager = Object.FindObjectOfType<SmartSyncManager>();
        if (manager != null)
        {
            overlay.manager = manager;
            overlay.grid = manager.grid;
        }

        EditorUtility.SetDirty(overlay);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Selection.activeGameObject = canvasGo;
        Debug.Log("[PerfectSync] Created '" + OVERLAY_NAME + "'.");
    }
}
