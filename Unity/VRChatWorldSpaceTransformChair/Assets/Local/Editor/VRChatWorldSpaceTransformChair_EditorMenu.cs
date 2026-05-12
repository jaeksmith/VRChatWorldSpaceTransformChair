using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// Editor menu for VRChatWorldSpaceTransformChair.
//
// SAFETY NOTE: this tool ONLY creates new GameObjects. It never modifies any existing chair or
// existing GameObject in the scene/prefab. If you have a working chair you want to preserve,
// it is left strictly alone.
//
// The single menu builds a complete fresh chair TEMPLATE in the scene, with VRCPlayerObject
// (so VRChat auto-spawns one per joining player at runtime), VRCStation, trigger collider,
// Seat/Exit transforms, the UdonSharpBehaviour, the scale display HUD, and auto-wired
// serialized fields. The resulting GameObject can be dragged into a Prefabs folder to make
// it a reusable prefab.
public static class VRChatWorldSpaceTransformChair_EditorMenu
{
    private const string MenuRoot = "Tools/VRChat World-Space Transform Chair/";

    [MenuItem(MenuRoot + "Create New Chair Instance in Scene")]
    public static void CreateNewChairInstance()
    {
        // Pre-flight: make sure UdonSharp has compiled the chair's program asset, otherwise
        // UdonSharpUndo.AddComponent silently produces a non-functional component.
        if (!EnsureChairProgramAssetCompiled()) return;

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Create VRChatWorldSpaceTransformChair");
        int undoGroup = Undo.GetCurrentGroup();

        // 1. Root GameObject. Position 2m to the right of selection (so it's visible without
        //    overlapping a previous chair), or world origin if nothing is selected.
        var rootGO = new GameObject("VRChatWorldSpaceTransformChair (auto)");
        Undo.RegisterCreatedObjectUndo(rootGO, "Create Chair Root");
        var rootT = rootGO.transform;
        rootT.position = (Selection.activeGameObject != null)
            ? Selection.activeGameObject.transform.position + Vector3.right * 2f
            : Vector3.zero;
        rootT.rotation = Quaternion.identity;
        rootT.localScale = Vector3.one;

        // 2. VRCPlayerObject — auto-spawns one copy per joining player at runtime. The template
        //    here is disabled at runtime; copies appear at the same parent. NetworkIDs for the
        //    UdonSynced fields on the chair behaviour are auto-assigned at build time.
        Undo.AddComponent<VRC.SDK3.Components.VRCPlayerObject>(rootGO);

        // 3. BoxCollider (trigger) — the Interact zone. Size matches sample VRCChair3. Lives on
        //    the root so per-player offset (which moves the root) keeps the click-target aligned
        //    with the visible chair.
        var box = Undo.AddComponent<BoxCollider>(rootGO);
        box.isTrigger = true;
        box.size = new Vector3(1f, 1.5f, 1f);
        box.center = new Vector3(0f, 0.75f, 0f);

        // 4. VRCStation. Component is added now; field configuration happens in step 6 once
        //    the seat / exit transforms exist to wire into stationEnterPlayerLocation /
        //    stationExitPlayerLocation.
        var station = Undo.AddComponent<VRC.SDK3.Components.VRCStation>(rootGO);

        // 5. Seat (entry point) and Exit (where the player lands on Jump-out).
        var seatGO = new GameObject("Seat");
        Undo.RegisterCreatedObjectUndo(seatGO, "Create Seat Transform");
        seatGO.transform.SetParent(rootT, worldPositionStays: false);
        seatGO.transform.localPosition = Vector3.zero;
        seatGO.transform.localRotation = Quaternion.identity;

        var exitGO = new GameObject("Exit");
        Undo.RegisterCreatedObjectUndo(exitGO, "Create Exit Transform");
        exitGO.transform.SetParent(rootT, worldPositionStays: false);
        exitGO.transform.localPosition = new Vector3(0f, 0f, 0.6f);
        exitGO.transform.localRotation = Quaternion.identity;

        // 6. Configure VRCStation. Critical fields:
        //    - PlayerMobility = Immobilize so locomotion is locked while seated.
        //    - disableStationExit = false so Jump triggers OnStationExited.
        //    - seated = true matches the SDK sample VRCChair3; user can flip to false in
        //      Inspector for a "standing pose" feel since the player isn't really sitting.
        Undo.RecordObject(station, "Configure VRCStation");
        station.PlayerMobility = VRC.SDKBase.VRCStation.Mobility.Immobilize;
        station.canUseStationFromStation = true;
        station.disableStationExit = false;
        station.seated = true;
        station.stationEnterPlayerLocation = seatGO.transform;
        station.stationExitPlayerLocation = exitGO.transform;

        // 7. Add the UdonSharpBehaviour via UdonSharpUndo.AddComponent — NOT plain AddComponent.
        //    Plain AddComponent looks like it works but the proxy<->UdonBehaviour link isn't
        //    initialised, and the next CopyProxyToUdon throws ArgumentNullException with a
        //    "Value cannot be null. Parameter name: key" deep in the UdonSharp formatter.
        var chair = UdonSharpUndo.AddComponent<VRChatWorldSpaceTransformChair>(rootGO);
        if (chair == null)
        {
            EditorUtility.DisplayDialog(
                "Create New Chair",
                "UdonSharpUndo.AddComponent returned null when adding VRChatWorldSpaceTransformChair. " +
                "This usually means the .cs or .asset has a compile error. Check the console.",
                "OK");
            Undo.RevertAllInCurrentGroup();
            return;
        }

        // 8. Build the scale display HUD as a child of the chair root.
        BuildScaleDisplayPanel(rootT, out var panelT, out var textComp);

        // 9. Wire chair fields. Mutate the proxy, then push to UdonBehaviour heap via
        //    CopyProxyToUdon so values persist into the Udon runtime.
        Undo.RecordObject(chair, "Wire Chair Fields");
        chair.station = station;
        chair.chairTransform = rootT;
        chair.interactCollider = box;
        chair.scaleDisplayText = textComp;
        chair.scaleDisplayPanelTransform = panelT;

        // 10. Best-effort: find a scene AvatarScalingSettings UB and wire it as the world-clamp
        //     source. If found, our script will read its minimumHeight/maximumHeight on station
        //     entry instead of using the relative minScale/maxScale fallback.
        var asUB = FindAvatarScalingSettingsInScene();
        if (asUB != null) chair.avatarScalingSettings = asUB;

        UdonSharpEditorUtility.CopyProxyToUdon(chair);
        EditorUtility.SetDirty(chair);
        EditorUtility.SetDirty(rootGO);

        Undo.CollapseUndoOperations(undoGroup);
        Selection.activeGameObject = rootGO;

        EditorUtility.DisplayDialog(
            "Create New Chair",
            "Created '" + rootGO.name + "' at " + rootT.position + ".\n\n" +
            "This is a VRCPlayerObject TEMPLATE — at runtime VRChat disables this GameObject\n" +
            "and instantiates one copy per joining player, with that player as owner. Each\n" +
            "player can only sit in their own copy (Interact is gated on IsOwner).\n\n" +
            "Wiring summary:\n" +
            "  VRCPlayerObject: ✓ (auto-spawn per player)\n" +
            "  station: ✓\n" +
            "  chairTransform: ✓ (root)\n" +
            "  interactCollider: ✓ (BoxCollider; auto-disabled while seated)\n" +
            "  scaleDisplayText: " + (textComp != null ? "✓" : "MISSING") + "\n" +
            "  scaleDisplayPanelTransform: ✓ (auto-hidden on remote viewers)\n" +
            "  avatarScalingSettings: " + (asUB != null
                ? ("✓ wired to '" + asUB.gameObject.name + "'")
                : "(none found in scene; left null — script falls back to minScale/maxScale)") + "\n\n" +
            "Sync defaults (tunable on the chair Inspector):\n" +
            "  activeUpdatesPerSecond: 10 (max RequestSerialization rate during motion)\n" +
            "  idle thresholds: 1mm position / 0.1° rotation (no serialize while parked)\n" +
            "  remoteLerp: 0.2 (per-frame smoothing for remote viewers)\n" +
            "  perPlayerXSpacing: 1.5 (per-playerId X offset to spread chairs at spawn)\n\n" +
            "VRCStation defaults:\n" +
            "  PlayerMobility: Immobilize  |  Disable Station Exit: FALSE  |  seated: TRUE\n\n" +
            "If this works in VR, drag the GameObject into Assets/Local/Prefabs to make it a prefab.\n" +
            "This menu did NOT modify any existing chair or other scene GameObject.",
            "OK");
    }

    // -- helpers --

    private static bool EnsureChairProgramAssetCompiled()
    {
        var guids = AssetDatabase.FindAssets("t:UdonSharpProgramAsset");
        bool found = false;
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var pa = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(path);
            if (pa == null || pa.sourceCsScript == null) continue;
            if (pa.sourceCsScript.GetClass() != typeof(VRChatWorldSpaceTransformChair)) continue;
            found = true;
            if (pa.GetSerializedUdonProgramAsset() == null)
            {
                EditorUtility.DisplayDialog(
                    "Create New Chair",
                    "UdonSharpProgramAsset for VRChatWorldSpaceTransformChair hasn't finished compiling at:\n  " + path + "\n\n" +
                    "Wait for Unity's compile to finish (no spinner in the bottom-right) and re-run this menu.",
                    "OK");
                return false;
            }
        }
        if (!found)
        {
            EditorUtility.DisplayDialog(
                "Create New Chair",
                "Couldn't find a UdonSharpProgramAsset for VRChatWorldSpaceTransformChair. " +
                "Ensure VRChatWorldSpaceTransformChair.cs and the matching .asset are in the project.",
                "OK");
            return false;
        }
        return true;
    }

    // Build the scale-display HUD: world-space Canvas at chair-local front + Text child.
    //
    // CRITICAL: create both GameObjects with `new GameObject(name, typeof(RectTransform))` so
    // the RectTransform is the ORIGINAL transform component, not added-and-swapped from
    // Transform. The Transform→RectTransform swap that AddComponent<RectTransform> performs has
    // been observed to break the parent relationship — DisplayText ended up at scene root
    // instead of under ScaleDisplayPanel. Sidestepped entirely by creating with RectTransform
    // from the start.
    //
    // Sizing values started from an empirically-verified working configuration the user landed
    // on by hand (Round 5 — 100 x 70), then bumped iteratively to fit added diagnostic lines:
    //   - +20 height (90) for the Offset line
    //   - widened 1.7x (170) + +20 height (110) and centered text once the Target line was
    //     added (7 lines total now, including Target / Eye height / Baseline / Ratio / Clamp /
    //     source / Offset; long Offset line wraps to ~8 visual lines at 170 wide)
    //   panel: localScale 0.003, sizeDelta 170 x 110 → world footprint ~0.51 x 0.33 m
    //   CanvasScaler.dynamicPixelsPerUnit 50 (text crispness)
    //   text: stretch-to-fill, fontSize 12, center-aligned, wrap on (per-line readability)
    private static void BuildScaleDisplayPanel(Transform parent, out Transform panelT, out Text textComp)
    {
        // Panel root with RectTransform from creation — no Transform swap.
        var panelGO = new GameObject("ScaleDisplayPanel", typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(panelGO, "Create Scale Display Panel");
        panelGO.transform.SetParent(parent, worldPositionStays: false);
        panelT = panelGO.transform;
        var rt = (RectTransform)panelT;

        // Pose: chair-local +Z, identity rotation. Canvas's readable face is local -Z (per
        // skill), so this orientation puts the readable face naturally back at the seated player.
        rt.localPosition = new Vector3(0f, 1.4f, 0.7f);
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one * 0.003f;
        rt.sizeDelta = new Vector2(170f, 110f);

        // UI components on the panel.
        var canvas = panelGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        var scaler = panelGO.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 50f;
        scaler.referencePixelsPerUnit = 100f;

        panelGO.AddComponent<GraphicRaycaster>();
        // VRC.SDK3.Components.VRCUiShape — concrete; the abstract VRC_UiShape base errors with
        // "The script class can't be abstract!" if used directly.
        panelGO.AddComponent<VRC.SDK3.Components.VRCUiShape>();

        // Re-apply RectTransform values in case Canvas init reset them.
        rt.localPosition = new Vector3(0f, 1.4f, 0.7f);
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one * 0.003f;
        rt.sizeDelta = new Vector2(170f, 110f);

        // Text child — same RectTransform-from-creation pattern.
        var textGO = new GameObject("DisplayText", typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(textGO, "Create Display Text");
        textGO.transform.SetParent(panelT, worldPositionStays: false);
        var textRT = (RectTransform)textGO.transform;

        // Stretch to fill the parent panel. Anchors at the corners, zero offsets, identity scale.
        textRT.anchorMin = Vector2.zero;
        textRT.anchorMax = Vector2.one;
        textRT.offsetMin = Vector2.zero;
        textRT.offsetMax = Vector2.zero;
        textRT.localPosition = Vector3.zero;
        textRT.localRotation = Quaternion.identity;
        textRT.localScale = Vector3.one;

        textComp = textGO.AddComponent<Text>();

        // Default font. Unity 2022.3 ships LegacyRuntime.ttf as the renamed legacy default;
        // older / editor-extras paths still expose Arial.ttf.
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null) font = AssetDatabase.GetBuiltinExtraResource<Font>("Arial.ttf");
        if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (font != null) textComp.font = font;

        textComp.fontSize = 12;
        textComp.alignment = TextAnchor.MiddleCenter;
        textComp.color = Color.white;
        textComp.horizontalOverflow = HorizontalWrapMode.Wrap;
        textComp.verticalOverflow = VerticalWrapMode.Overflow;
        textComp.text = "(scale display)";
    }

    private static VRC.Udon.UdonBehaviour FindAvatarScalingSettingsInScene()
    {
        var allUB = Object.FindObjectsOfType<VRC.Udon.UdonBehaviour>();
        foreach (var ub in allUB)
        {
            if (ub == null) continue;
            // Match by program asset name OR by GameObject name.
            var pa = ub.programSource;
            if (pa != null && pa.name == "AvatarScalingSettings") return ub;
            if (ub.gameObject.name == "AvatarScalingSettings") return ub;
        }
        return null;
    }
}
