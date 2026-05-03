# VRChatWorldSpaceTransformChair — Implementation Spec

## Goal

Create a reusable VRChat prefab: an interactive "chair" (VRC Station) that, when sat in, lets the player manipulate their view of the surrounding world by grabbing the air with their VR controllers. Hand gestures translate into avatar scale, chair rotation, and chair position changes such that the player's hands appear to stay anchored in world space — creating the illusion of grabbing and manipulating the world itself.

This is a standalone, reusable component intended for use across multiple VRChat worlds/projects. The first integration target is BoardBound (a Demeo-style board game in VRChat), where it will let players view the board from any angle/scale, but the prefab is designed to drop into any world that benefits from a "grab the world" viewing chair.

## Context

- **Platform:** VRChat, Udon (UdonSharp preferred)
- **Players:** Mixed — VR, PC desktop, and mobile (Android/iOS)
- **Use case:** A player wants to view content (e.g., a board game, a map, a model) from any angle/scale without rotating the actual world (which doesn't affect other avatars). Example: lying in bed and rotating the board overhead.
- **Reusability:** This is a self-contained prefab + script package. It should be usable in any VRChat world by dropping the prefab into the scene.

## Functional Requirements

1. A button (Interact) puts the player into the chair (VRC Station).
2. While seated, **VR players** can:
   - **Grip both controllers**, then move hands apart/together to scale the world (effectively scaling the player smaller/larger).
   - **Grip both controllers**, then rotate hands to rotate the chair around the player.
   - **Grip both controllers**, then translate hands to slide the chair in space.
3. While seated, **PC desktop players** get a fallback control scheme (e.g., keys or on-screen UI) for scale and rotation. (Phase 2 — VR is priority.)
4. While seated, **mobile players** get either a fixed preset position OR simple UI buttons. They do not get hand gesture controls.
5. Jump (or a dedicated Exit interact) returns the player to normal locomotion.
6. State resets cleanly on exit (chair scale/rotation should NOT permanently mutate; player should return to normal scale).

## Technical Approach: Hand Constraint Solver

The core idea: **when the player is "gripping," their hand positions in world space become anchors.** Each frame, the script reads where the hands physically are (from tracking) and computes what avatar scale, chair rotation, and chair position would put the *expected* (anchored) hand positions back at the *actual* hand positions.

### State machine

- **Idle** — seated, not gripping. Read hand positions but don't transform anything.
- **Gripping** — both grips held. On entry, snapshot:
  - `anchorLeft` = current left hand world position
  - `anchorRight` = current right hand world position
  - `anchorMidpoint` = (anchorLeft + anchorRight) / 2
  - `anchorDistance` = |anchorRight - anchorLeft|
  - `anchorForward` = normalized (anchorRight - anchorLeft) projected onto horizontal plane (or full 3D if you want roll)
  - `chairStateAtGrip` = current chair position, rotation, and avatar scale
- **Released** — one or both grips released. Lock in the new chair/avatar state as the new baseline.

### Each frame in Gripping state

```
currentLeft  = local player left hand world position
currentRight = local player right hand world position
currentMid   = (currentLeft + currentRight) / 2
currentDist  = |currentRight - currentLeft|
currentForward = normalized (currentRight - currentLeft)

scaleFactor = anchorDistance / currentDist
   // hands moved apart  -> currentDist > anchor -> scaleFactor < 1 -> shrink player
   // hands moved together -> scaleFactor > 1 -> grow player

rotationDelta = Quaternion.FromToRotation(currentForward, anchorForward)
   // rotation needed to bring current hand axis back to anchored axis

translationDelta = anchorMidpoint - rotationDelta * (currentMid * scaleFactor)
   // approximate; tune so the midpoint of hands stays anchored
```

Apply:
- `newAvatarScale = chairStateAtGrip.avatarScale * scaleFactor`
- `chair.rotation = rotationDelta * chairStateAtGrip.rotation` (around the midpoint)
- `chair.position = chairStateAtGrip.position + translationDelta`

**Note:** Because rotation, scale, and translation interact, the math should be applied as a single composite transform around the anchor midpoint. Build it as: translate so midpoint is at origin → scale → rotate → translate back to anchor midpoint. Do this in `LateUpdate` so it runs after VRChat updates tracking.

### Two-hand grip averaging for rotation

If the player rotates each hand independently, average the rotational delta. The simplest robust approach: derive rotation from the *axis between the hands* changing direction, not from each hand's individual rotation. That gives natural yaw + pitch + roll from hand displacement, which is what most "grab the world" interactions actually use (Demeo, Half-Life: Alyx, etc.). Individual hand rotation can be ignored for v1.

## VRChat APIs to Use

| Need | API |
|---|---|
| Sit/exit station | `VRCStation` component, `UseStation()` / `ExitStation()` |
| Detect VR vs not | `Networking.LocalPlayer.IsUserInVR()` |
| Detect Android/mobile | `OnInputMethodChanged(VRCInputMethod)` event; check for `Touch` |
| Get hand world position/rotation | `localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.LeftHand)` and `RightHand` |
| Detect grip input | `InputGrab(bool value, UdonInputEventArgs args)` — `args.handType` distinguishes left/right; or read `Input.GetAxis` via Udon input axis events |
| Set avatar scale | `localPlayer.SetAvatarEyeHeightByMeters(float)` (preferred) or `SetAvatarScaleEnforced` patterns; check current avatar scaling settings |
| Detect avatar scale changes | `OnAvatarEyeHeightChanged` event |
| Teleport / station entry transform | Move the station's entry point transform; the seated player follows it |

Reference docs:
- https://creators.vrchat.com/worlds/udon/players/player-positions/
- https://creators.vrchat.com/worlds/udon/players/player-avatar-scaling/
- https://creators.vrchat.com/worlds/udon/input-events/
- https://creators.vrchat.com/worlds/udon/cross-platform-content/

## Platform Branching

```
OnStationEntered(player):
  if not local player: return

  if localPlayer.IsUserInVR():
      mode = HAND_GESTURE_MODE
  else if isMobile (OnInputMethodChanged saw Touch):
      mode = FIXED_PRESET_MODE  // or simple UI buttons
  else:
      mode = DESKTOP_UI_MODE     // optional fallback; can stub for v1
```

Only `HAND_GESTURE_MODE` runs the per-frame solver. Other modes do nothing dynamic in v1.

## Prefab Structure

```
VRChatWorldSpaceTransformChair (root, GameObject)
├── ChairMesh (visual model — optional, can be invisible/minimal)
├── EnterButton (Collider + Interact → calls UseStation on local player)
├── Station (VRCStation component)
│   └── EntryExitPoint (Transform — this is what gets moved/rotated)
└── UdonBehaviour (the script described below)
    Serialized fields:
    - VRCStation station
    - Transform chairTransform (the thing we move/rotate; usually = root)
    - float minScale = 0.1
    - float maxScale = 10.0
    - bool requireBothGrips = true
```

## Script Skeleton (UdonSharp)

```csharp
public class VRChatWorldSpaceTransformChair : UdonSharpBehaviour
{
    [SerializeField] VRCStation station;
    [SerializeField] Transform chairTransform;
    [SerializeField] float minScale = 0.1f;
    [SerializeField] float maxScale = 10.0f;

    VRCPlayerApi seatedPlayer;
    bool isSeatedLocal;
    bool leftGrip, rightGrip;
    bool gripping;

    // Anchor snapshots (taken when gripping starts)
    Vector3 anchorLeft, anchorRight, anchorMidpoint;
    float anchorDistance;
    Vector3 anchorAxis;
    Vector3 chairPosAtGrip;
    Quaternion chairRotAtGrip;
    float avatarScaleAtGrip;

    public override void Interact()        { /* UseAttachedStation -> seat local player */ }
    public override void OnStationEntered(VRCPlayerApi p) { /* set isSeatedLocal, branch by platform */ }
    public override void OnStationExited(VRCPlayerApi p)  { /* reset state, restore scale */ }
    public override void InputGrab(bool v, UdonInputEventArgs a) { /* update leftGrip/rightGrip */ }

    void LateUpdate()
    {
        if (!isSeatedLocal || !Networking.LocalPlayer.IsUserInVR()) return;

        bool shouldGrip = leftGrip && rightGrip; // tune: requireBothGrips
        if (shouldGrip && !gripping) SnapshotAnchors();
        if (!shouldGrip && gripping) CommitAndRelease();
        gripping = shouldGrip;

        if (gripping) SolveAndApply();
    }

    // SnapshotAnchors / SolveAndApply / CommitAndRelease implement the math above.
}
```

## Edge Cases & Gotchas

1. **Avatar swap mid-session.** `OnAvatarEyeHeightChanged` fires; re-snapshot or rebase scale to avoid jumps.
2. **Player exits while gripping.** Force-release on `OnStationExited`, restore avatar scale to 1.0 (or the world default).
3. **Scale clamping.** Clamp final avatar scale to `[minScale, maxScale]` to prevent the player getting trapped microscopic or kilometer-tall.
4. **World-authoritative vs player-controlled scaling.** Use `SetManualAvatarScalingAllowed(false)` on entry, restore on exit, so the radial puppet doesn't fight the script.
5. **Networking.** v1 is local-only — other players will not see your chair rotate or you scale (well, they'll see scale because that's networked). That's fine for the test. If you sync the chair transform later, use manual sync with the seated player as owner.
6. **Mobile fallback.** For v1, just teleport the player to a fixed "good viewing angle" entry point on station entry and skip the gesture solver. UI buttons can come later.
7. **Hand tracking jitter.** Smooth hand positions with a one-euro filter or simple lerp if the solver feels twitchy. Probably not needed for v1.
8. **Re-grip drift.** Each new grip re-snapshots anchors against the *current* chair state, so drift accumulates only if the user wants it to (which is the desired behavior — like grabbing, releasing, regrabbing in Alyx).

## What "Done" Looks Like for v1

- [ ] Drop the prefab in a scene, hit Play in VR.
- [ ] Click the Interact button → seated.
- [ ] Squeeze both grips, move hands apart → world appears to grow (player shrinks).
- [ ] Move hands together → world shrinks (player grows).
- [ ] Rotate hand axis (turn your hands like a steering wheel) → view rotates around you.
- [ ] Translate both hands together → view pans.
- [ ] Release grips → state holds.
- [ ] Press Jump → exit station, return to normal scale.
- [ ] Mobile player joins, sits → ends up at a fixed nice viewing position, no errors.

## Out of Scope for v1

- Networked sync of chair transform across players
- Desktop button/UI control scheme
- Mobile UI button control scheme
- Per-hand independent rotation contributions (axis-only is sufficient)
- Smoothing/filtering of hand input
- Visual feedback for grip state (can add later)
