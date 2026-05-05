using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;
using VRCStation = VRC.SDK3.Components.VRCStation;

// Drop-in "grab the world" viewing chair.
//
// While seated in the VRCStation, a VR player can hold one or both grips and translate / rotate
// (and, with both, scale) the world by moving their hands. Implemented by transforming the chair
// (which moves the seated player) and the avatar's eye height (which sets player size). v1 is
// local-only and not networked.
//
// Solver, both modes, snapshot at grip start, then per frame find the transform T that maps
// current hand pose back to anchor hand pose, apply T to:
//   - the chair's CURRENT pose (chairTransform) — rotation + translation only
//   - the avatar's eye height (two-hand only) — scale ratio applied as absolute eye height
//
// CRITICAL: we apply T to chair_NOW, not chair_at_grip. The seated player's playspace follows
// the chair (so hand world readings shift when the chair moves). If we apply T to chair_at_grip
// each frame, the chair oscillates between grip pose and grip pose * T as the playspace catches
// up — which manifests as visible jitter on translation and ~50% gain on rotation (the average
// of "full transform" and "identity" frames). With T applied to chair_now, T per frame is just
// the small residual to bring hands back to anchor; once playspace catches up, T -> identity
// and the system stays put. Stable, full gain.
//
// One-hand:  rigid transform around the gripped hand. R = anchorRot * inv(currentRot).
// Two-hand:  rigid transform on the inter-hand axis. R from FromToRotation, no scale on chair
//            (scale is the avatar eye height only — avatar scaling pivots around the head, so
//            it doesn't shift hand world positions, so it shouldn't shift the chair either).
//
// On a change in WHICH hands are gripping (none -> any, single -> two, two -> single, single L
// <-> single R), we re-snapshot anchors so the transition is jump-free.
//
// SCALING — clamp layers. Read once; this got confused early in the project's life.
//
//   `SetAvatarEyeHeightByMeters(float)` — what we use to drive scale — has no effective numeric
//   bounds at the API surface. Per VRChat docs and community testing: absolute range roughly
//   [0.01, 10000]m, "safe" rendering range [0.1, 100]m. Outside the safe range you may get
//   visual rendering issues (avatar mesh visually plateaus, IK breaks), but the API itself
//   accepts the call.
//
//   The `SetAvatarEyeHeightMinimumByMeters` / `MaximumByMeters` family is a SEPARATE concern:
//   it sets the bounds of the radial-puppet UI (player-controlled scaling). The PARAMETER you
//   pass to those setters is clamped to [0.2, 5]. Those bounds DO NOT constrain
//   `SetAvatarEyeHeightByMeters` — confirmed against current docs. This script widens them to
//   the API max [0.2, 5] on station entry as a defensive belt-and-braces (and disables the
//   manual radial puppet entirely with `SetManualAvatarScalingAllowed(false)` so the puppet
//   doesn't fight the script during the seated session). On station exit, both are restored.
//
//   So the practical clamps the user actually experiences:
//     A) THIS SCRIPT'S CLAMP — bounds we ASK for in SolveTwoHand:
//        `_effectiveMinEyeHeight` / `_effectiveMaxEyeHeight`, resolved at station entry from:
//          - `avatarScalingSettings` UB if wired (its `minimumHeight` / `maximumHeight`), or
//          - Fallback `baseline * minScale` / `baseline * maxScale`.
//        HUD shows this as `Clamp: [...]` with the source label.
//     B) WORLD `AvatarScalingSettings` UB — if present in scene, listens on
//        `OnAvatarEyeHeightChanged` and re-clamps any eye-height change to its bounds. This is
//        the final word. Wiring it into our `avatarScalingSettings` field makes A == B so they
//        don't fight, and the user only has one knob to tune.
//
//   If the avatar appears to stop scaling at some value, check the HUD's `Eye height:` line —
//   if the number keeps dropping but the avatar mesh visually plateaus, it's the avatar's
//   rendering / IK clamping, not an API clamp. Reduce `minimumHeight` on AvatarScalingSettings
//   (or the script's `minScale`) only as far as the rendering still looks coherent.
public class VRChatWorldSpaceTransformChair : UdonSharpBehaviour
{
    [Header("Wiring")]
    [Tooltip("VRCStation the player sits on. Required.")]
    public VRCStation station;

    [Tooltip("Transform that gets translated and rotated to move the seated player. Usually the prefab root, or whatever ancestor of the Station that you want to slide around. The Station and its entry/exit point should be descendants of this transform.")]
    public Transform chairTransform;

    [Tooltip("Optional Collider for the Interact zone (the trigger you click to sit). If wired, the script disables the collider when the player sits and re-enables it on exit, so the chair doesn't show the Interact-hover highlight while you're seated and small inside it.")]
    public Collider interactCollider;

    [Header("Limits")]
    [Tooltip("Optional reference to the scene's AvatarScalingSettings UdonBehaviour (the SDK sample, or any compatible UB exposing `minimumHeight` / `maximumHeight` floats). If wired, the script reads those values on station entry and uses them as the clamp bounds — single source of truth, no duplication. If null, falls back to minScale/maxScale below (relative to player's entry eye height).")]
    public UdonBehaviour avatarScalingSettings;

    [Tooltip("Fallback minimum avatar scale RELATIVE to the player's eye height on entry, used only when avatarScalingSettings is not wired. 0.1 = 10% of baseline.")]
    public float minScale = 0.1f;

    [Tooltip("Fallback maximum avatar scale relative to the player's eye height on entry, used only when avatarScalingSettings is not wired.")]
    public float maxScale = 10.0f;

    [Header("Smoothing")]
    [Tooltip("Exponential smoothing on hand positions in the solver (0 = raw input, no lag; 0.5 = noticeable lag, smoother; 0.9 = heavy lag, very smooth). Tune up if VR controller jitter is visible during held grips after the math fix.")]
    [Range(0f, 0.95f)]
    public float inputSmoothing = 0.0f;

    [Header("Visualization")]
    [Tooltip("Optional in-world UI Text that will display current eye height + scale ratio. Wire to a world-space Canvas's Text element. Updated every frame while seated. Leave null to disable.")]
    public UnityEngine.UI.Text scaleDisplayText;

    [Tooltip("Optional Transform of a HUD panel (e.g. the scale-display Canvas). If wired, the script auto-scales its localScale by the current avatar-scale ratio so the panel stays roughly apparent-constant in the player's view as they scale.")]
    public Transform scaleDisplayPanelTransform;

    private VRCPlayerApi _localPlayer;
    private bool _isSeatedLocal;
    private bool _leftGrip, _rightGrip;

    // Which hands were gripped on the last anchor snapshot. Different from _leftGrip/_rightGrip
    // means the grip set just changed and we need to re-snapshot before solving.
    private bool _anchoredLeft, _anchoredRight;

    // Eye height baseline captured on station entry, so we can convert avatar-scale ratios into
    // absolute eye heights for SetAvatarEyeHeightByMeters and clamp against min/max.
    private float _baselineEyeHeight;

    // State captured on grip start. Eye height + anchor distance feed the avatar-scale ratio.
    // We deliberately do NOT cache the chair pose at grip — the solver applies the per-frame
    // delta to chairTransform's CURRENT pose, not to a snapshot.
    private float _eyeHeightAtGrip;

    // Per-hand pose snapshots; we always capture both, the solver picks what it needs.
    private Vector3 _anchorLeftPos, _anchorRightPos;
    private Quaternion _anchorLeftRot, _anchorRightRot;

    // Two-hand-specific cache, built from the per-hand snapshots at snapshot time.
    private Vector3 _anchorMid, _anchorAxisN;
    private float _anchorDist;

    // Smoothed hand positions for the inputSmoothing EMA. Re-initialised on grip-set change.
    private Vector3 _filteredLeftPos, _filteredRightPos;
    private bool _filterPrimed;

    // Effective absolute clamp bounds (meters) — Layer 2 in the SCALING comment block above.
    // Computed on station entry from either avatarScalingSettings (if wired) or
    // baseline*minScale/baseline*maxScale (if not). _clampFromWorld records which path resolved
    // them so the HUD can show the source clearly.
    private float _effectiveMinEyeHeight;
    private float _effectiveMaxEyeHeight;
    private bool _clampFromWorld;

    // Player's prior eye-height-bounds before we widened them on station entry. Restored on exit.
    // We widen to VRChat's max API range [0.2, 5] so the player-bounds layer doesn't silently
    // clamp our SetAvatarEyeHeightByMeters calls below 0.5m (a common default min) etc.
    private float _savedPlayerMinHeight;
    private float _savedPlayerMaxHeight;
    private bool _playerBoundsWidened;

    // HUD panel auto-scale base (captured once at start).
    private Vector3 _hudPanelBaseScale;
    private bool _hudPanelBaseCaptured;

    private void Start()
    {
        if (scaleDisplayPanelTransform != null)
        {
            _hudPanelBaseScale = scaleDisplayPanelTransform.localScale;
            _hudPanelBaseCaptured = true;
        }
    }

    public override void Interact()
    {
        if (station == null) return;
        VRCPlayerApi p = Networking.LocalPlayer;
        if (p == null) return;
        station.UseStation(p);
    }

    public override void OnStationEntered(VRCPlayerApi player)
    {
        if (player == null || !player.isLocal) return;
        _localPlayer = player;
        _isSeatedLocal = true;
        _leftGrip = false;
        _rightGrip = false;
        _anchoredLeft = false;
        _anchoredRight = false;

        _baselineEyeHeight = _localPlayer.GetAvatarEyeHeightAsMeters();

        // Stop the user's radial-puppet scale from fighting the script while they're seated.
        _localPlayer.SetManualAvatarScalingAllowed(false);

        // Hide the Interact hover-highlight on the chair's trigger collider while seated. When
        // the player is seated and shrinks, their interact ray often re-hits the trigger from
        // inside, showing the highlight at random scales. Disabling here suppresses that.
        if (interactCollider != null) interactCollider.enabled = false;

        // Save the player's existing eye-height bounds, then widen to VRChat's API max range
        // so the player-level clamp doesn't silently floor our SetAvatarEyeHeightByMeters
        // calls (e.g. defaults can sit at min=0.5, capping us before our own clamp engages).
        _savedPlayerMinHeight = _localPlayer.GetAvatarEyeHeightMinimumAsMeters();
        _savedPlayerMaxHeight = _localPlayer.GetAvatarEyeHeightMaximumAsMeters();
        _localPlayer.SetAvatarEyeHeightMinimumByMeters(0.2f); // VRChat API floor
        _localPlayer.SetAvatarEyeHeightMaximumByMeters(5.0f); // VRChat API ceiling
        _playerBoundsWidened = true;

        // Resolve our effective clamp bounds (Layer 2). If the world has an AvatarScalingSettings
        // UdonBehaviour and the user wired it into our field, read its minimumHeight /
        // maximumHeight as the absolute-meter source of truth so layers 2 and 3 agree. Otherwise
        // fall back to script-relative bounds (baseline * minScale / maxScale).
        _clampFromWorld = false;
        if (avatarScalingSettings != null)
        {
            object minObj = avatarScalingSettings.GetProgramVariable("minimumHeight");
            object maxObj = avatarScalingSettings.GetProgramVariable("maximumHeight");
            if (minObj != null && maxObj != null)
            {
                _effectiveMinEyeHeight = (float)minObj;
                _effectiveMaxEyeHeight = (float)maxObj;
                _clampFromWorld = true;
            }
            else
            {
                _effectiveMinEyeHeight = _baselineEyeHeight * minScale;
                _effectiveMaxEyeHeight = _baselineEyeHeight * maxScale;
            }
        }
        else
        {
            _effectiveMinEyeHeight = _baselineEyeHeight * minScale;
            _effectiveMaxEyeHeight = _baselineEyeHeight * maxScale;
        }
    }

    public override void OnStationExited(VRCPlayerApi player)
    {
        if (player == null || !player.isLocal) return;

        _leftGrip = false;
        _rightGrip = false;
        _anchoredLeft = false;
        _anchoredRight = false;
        _isSeatedLocal = false;

        if (_localPlayer != null && _baselineEyeHeight > 0f)
        {
            _localPlayer.SetAvatarEyeHeightByMeters(_baselineEyeHeight);
            _localPlayer.SetManualAvatarScalingAllowed(true);

            // Restore the player's eye-height bounds we widened on entry.
            if (_playerBoundsWidened)
            {
                _localPlayer.SetAvatarEyeHeightMinimumByMeters(_savedPlayerMinHeight);
                _localPlayer.SetAvatarEyeHeightMaximumByMeters(_savedPlayerMaxHeight);
                _playerBoundsWidened = false;
            }
        }

        // Re-enable the Interact zone so the chair is clickable again.
        if (interactCollider != null) interactCollider.enabled = true;
    }

    public override void InputGrab(bool value, UdonInputEventArgs args)
    {
        if (!_isSeatedLocal) return;
        if (args.handType == HandType.LEFT) _leftGrip = value;
        else if (args.handType == HandType.RIGHT) _rightGrip = value;
    }

    public override void OnAvatarEyeHeightChanged(VRCPlayerApi player, float prevEyeHeightAsMeters)
    {
        if (player == null || !player.isLocal) return;
        // Fires for any eye-height change, including ones we drive ourselves while gripping.
        // Ignore in-grip events; treat out-of-grip events (avatar swap, world override, etc.) as
        // a request to re-baseline so the next grip starts fresh.
        if (_anchoredLeft || _anchoredRight) return;
        if (_localPlayer == null) _localPlayer = player;
        _baselineEyeHeight = _localPlayer.GetAvatarEyeHeightAsMeters();
    }

    private void Update()
    {
        // Visualization update — independent of solver. Runs whether gripping or not.
        if (_localPlayer != null && _isSeatedLocal)
        {
            float current = _localPlayer.GetAvatarEyeHeightAsMeters();
            float ratio = (_baselineEyeHeight > 1e-5f) ? (current / _baselineEyeHeight) : 1f;

            if (scaleDisplayText != null)
            {
                // 4-line readout. Each line answers a question the user is likely asking:
                //   "How tall is my avatar right now?"      -> Eye height
                //   "What was I when I sat down?"           -> Baseline (entry)
                //   "How small/big am I vs entry?"          -> Scale ratio
                //   "What's the script's allowed range?"    -> Clamp + source
                string clampSource = _clampFromWorld ? "AvatarScalingSettings" : "minScale*baseline (fallback)";
                scaleDisplayText.text =
                    "Eye height: " + current.ToString("F3") + " m\n" +
                    "Baseline:   " + _baselineEyeHeight.ToString("F3") + " m  (entry)\n" +
                    "Ratio:      " + ratio.ToString("F3") + "x\n" +
                    "Clamp: [" + _effectiveMinEyeHeight.ToString("F3") + ", " + _effectiveMaxEyeHeight.ToString("F3") + "] m\n" +
                    "  source: " + clampSource;
            }

            // Auto-scale the HUD panel to keep apparent size roughly constant across player scale.
            if (scaleDisplayPanelTransform != null && _hudPanelBaseCaptured)
            {
                scaleDisplayPanelTransform.localScale = _hudPanelBaseScale * ratio;
            }
        }
        else if (scaleDisplayText != null)
        {
            scaleDisplayText.text = "(not seated)";
        }
    }

    private void LateUpdate()
    {
        if (!_isSeatedLocal || _localPlayer == null) return;
        if (!_localPlayer.IsUserInVR()) return;

        bool wantGrip = _leftGrip || _rightGrip;
        bool gripSetChanged = (_leftGrip != _anchoredLeft) || (_rightGrip != _anchoredRight);

        if (wantGrip && gripSetChanged)
        {
            SnapshotAnchors();
        }

        _anchoredLeft = _leftGrip;
        _anchoredRight = _rightGrip;

        if (_anchoredLeft && _anchoredRight) SolveTwoHand();
        else if (_anchoredLeft || _anchoredRight) SolveOneHand();
    }

    private void SnapshotAnchors()
    {
        VRCPlayerApi.TrackingData lh = _localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.LeftHand);
        VRCPlayerApi.TrackingData rh = _localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.RightHand);

        _anchorLeftPos = lh.position;
        _anchorRightPos = rh.position;
        _anchorLeftRot = lh.rotation;
        _anchorRightRot = rh.rotation;

        _anchorMid = (_anchorLeftPos + _anchorRightPos) * 0.5f;
        Vector3 axis = _anchorRightPos - _anchorLeftPos;
        _anchorDist = axis.magnitude;
        _anchorAxisN = (_anchorDist > 1e-5f) ? (axis / _anchorDist) : Vector3.right;

        _eyeHeightAtGrip = _localPlayer.GetAvatarEyeHeightAsMeters();

        // Prime the EMA filters at the raw current values so the first solver frame doesn't
        // lerp from stale state.
        _filteredLeftPos = _anchorLeftPos;
        _filteredRightPos = _anchorRightPos;
        _filterPrimed = true;
    }

    // Apply the EMA filter to a raw hand position; pass-through when smoothing is 0.
    // Updates the per-hand filter state in place.
    private Vector3 FilterHandPos(Vector3 raw, bool isLeft)
    {
        if (inputSmoothing <= 0f || !_filterPrimed) return raw;
        float a = 1f - inputSmoothing;
        if (isLeft)
        {
            _filteredLeftPos = Vector3.Lerp(_filteredLeftPos, raw, a);
            return _filteredLeftPos;
        }
        else
        {
            _filteredRightPos = Vector3.Lerp(_filteredRightPos, raw, a);
            return _filteredRightPos;
        }
    }

    private void SolveOneHand()
    {
        bool useLeft = _leftGrip;
        VRCPlayerApi.TrackingDataType type = useLeft ? VRCPlayerApi.TrackingDataType.LeftHand : VRCPlayerApi.TrackingDataType.RightHand;
        VRCPlayerApi.TrackingData h = _localPlayer.GetTrackingData(type);

        Vector3 currentPos = FilterHandPos(h.position, useLeft);
        Quaternion currentRot = h.rotation;
        Vector3 anchorPos = useLeft ? _anchorLeftPos : _anchorRightPos;
        Quaternion anchorRot = useLeft ? _anchorLeftRot : _anchorRightRot;

        // Rigid transform mapping current hand pose -> anchor hand pose, applied to the CHAIR'S
        // CURRENT POSE. Each frame T is the residual delta; once the playspace catches up T -> I.
        // T(p) = R * (p - currentPos) + anchorPos, with R = anchorRot * inv(currentRot).
        Quaternion R = anchorRot * Quaternion.Inverse(currentRot);

        if (chairTransform != null)
        {
            Vector3 chairPosNow = chairTransform.position;
            Quaternion chairRotNow = chairTransform.rotation;
            Vector3 offset = chairPosNow - currentPos;
            Vector3 newPos = R * offset + anchorPos;
            Quaternion newRot = R * chairRotNow;
            chairTransform.SetPositionAndRotation(newPos, newRot);
        }
        // No scale change in single-hand mode.
    }

    private void SolveTwoHand()
    {
        VRCPlayerApi.TrackingData lh = _localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.LeftHand);
        VRCPlayerApi.TrackingData rh = _localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.RightHand);

        Vector3 currentLeft = FilterHandPos(lh.position, true);
        Vector3 currentRight = FilterHandPos(rh.position, false);
        Vector3 currentMid = (currentLeft + currentRight) * 0.5f;

        Vector3 currentAxis = currentRight - currentLeft;
        float currentDist = currentAxis.magnitude;
        if (currentDist < 1e-5f) return; // hands coincident, solve undefined for this frame
        Vector3 currentAxisN = currentAxis / currentDist;

        // Eye height drives avatar scale. Clamp against the effective bounds resolved on
        // station entry (from avatarScalingSettings if wired, else baseline*minScale/maxScale).
        // Clamping against entry bounds rather than current size prevents re-grips from
        // accumulating past the limits across multiple grip cycles.
        float scaleFactor = _anchorDist / currentDist;
        float eyeHeightTarget = Mathf.Clamp(_eyeHeightAtGrip * scaleFactor, _effectiveMinEyeHeight, _effectiveMaxEyeHeight);

        Quaternion R = Quaternion.FromToRotation(currentAxisN, _anchorAxisN);

        // Rigid transform (rotation + translation, NO scale) applied to the chair's CURRENT
        // pose. Scale lives only on the avatar eye height — avatar scaling pivots around the
        // head, so it doesn't shift hand world positions, so it shouldn't shift the chair.
        // T(p) = R * (p - currentMid) + anchorMid.
        if (chairTransform != null)
        {
            Vector3 chairPosNow = chairTransform.position;
            Quaternion chairRotNow = chairTransform.rotation;
            Vector3 offset = chairPosNow - currentMid;
            Vector3 newPos = R * offset + _anchorMid;
            Quaternion newRot = R * chairRotNow;
            chairTransform.SetPositionAndRotation(newPos, newRot);
        }

        _localPlayer.SetAvatarEyeHeightByMeters(eyeHeightTarget);
    }
}
