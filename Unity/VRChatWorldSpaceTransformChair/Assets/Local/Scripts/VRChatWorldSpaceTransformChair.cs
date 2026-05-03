using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
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
public class VRChatWorldSpaceTransformChair : UdonSharpBehaviour
{
    [Header("Wiring")]
    [Tooltip("VRCStation the player sits on. Required.")]
    public VRCStation station;

    [Tooltip("Transform that gets translated and rotated to move the seated player. Usually the prefab root, or whatever ancestor of the Station that you want to slide around. The Station and its entry/exit point should be descendants of this transform.")]
    public Transform chairTransform;

    [Header("Limits")]
    [Tooltip("Minimum avatar scale relative to the player's eye height on station entry. Hard-clamps so the player can't end up microscopic.")]
    public float minScale = 0.1f;

    [Tooltip("Maximum avatar scale relative to the player's eye height on station entry.")]
    public float maxScale = 10.0f;

    [Header("Smoothing")]
    [Tooltip("Exponential smoothing on hand positions in the solver (0 = raw input, no lag; 0.5 = noticeable lag, smoother; 0.9 = heavy lag, very smooth). Tune up if VR controller jitter is visible during held grips after the math fix.")]
    [Range(0f, 0.95f)]
    public float inputSmoothing = 0.0f;

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
        }
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

        // Eye height drives avatar scale. Clamp against the player's initial size, not against
        // the current size, so re-grips don't accumulate past the limits.
        float scaleFactor = _anchorDist / currentDist;
        float minEyeHeight = _baselineEyeHeight * minScale;
        float maxEyeHeight = _baselineEyeHeight * maxScale;
        float eyeHeightTarget = Mathf.Clamp(_eyeHeightAtGrip * scaleFactor, minEyeHeight, maxEyeHeight);

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
