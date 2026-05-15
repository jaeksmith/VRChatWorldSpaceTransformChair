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
// (which moves the seated player) and the avatar's eye height (which sets player size).
//
// MULTIPLAYER: The chair root carries a `VRC.SDK3.Components.VRCPlayerObject` so VRChat
// auto-spawns one copy per joining player, with that player as owner. Each player sits in
// their own chair (Interact gated on `Networking.IsOwner(gameObject)`). Owner writes
// chairTransform pose into [UdonSynced] fields after solving, throttled to ~10Hz max during
// motion and 0Hz when the chair is parked (no bandwidth while sitting still). Remote viewers
// lerp their local chairTransform toward the synced pose each frame; the station is in
// Immobilize mode so the seated remote avatar follows stationEnterPlayerLocation, giving
// remote-rendered seated players the correct world pose including full 3D rotation
// (the desync-station pattern; rotation is NOT yaw-clamped like TeleportTo).
// Avatar scale auto-syncs via standard player networking — no extra work needed.
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
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
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

    [Tooltip("Optional Transform of a HUD panel (e.g. the scale-display Canvas). If wired, the script auto-scales its localScale AND localPosition by the current avatar-scale ratio so the panel stays apparent-constant in the player's view as they scale — same apparent size, same apparent eye-relative position. Without the position scaling, a baseline panel at chair-local (0, 1.4, 0.7) ends up far overhead at small scales and pressed into the face at large scales.")]
    public Transform scaleDisplayPanelTransform;

    [Header("Exit behavior")]
    [Tooltip("If true (default), the avatar's eye height is restored to its on-entry value when the player exits the chair. If false, the avatar keeps whatever size it had at exit — useful for testing whether scale-related dislocation / body-bork issues persist across re-entries (exit at scale X, re-enter to start with X as the new baseline). SetManualAvatarScalingAllowed(true) and the saved radial-puppet bounds are restored regardless.")]
    public bool restoreAvatarHeightOnExit = true;

    [Header("Multiplayer sync")]
    [Tooltip("Maximum RequestSerialization rate (per second) while the chair is moving. Owner-side throttle; remote viewers lerp between received values for visual smoothness. ~10 is fine for typical use; raise for snappier remote rendering at the cost of bandwidth.")]
    public float activeUpdatesPerSecond = 10f;

    [Tooltip("Position-change threshold (meters) below which the chair counts as 'idle' for serialization purposes — no further serializes until the chair moves more than this from the last serialized value. Bounds steady-state cumulative drift to this magnitude. ~0.001 = 1mm is invisible.")]
    public float idlePosThreshold = 0.001f;

    [Tooltip("Rotation-change threshold (degrees) below which the chair counts as 'idle' (see idlePosThreshold). ~0.1 = a tenth of a degree.")]
    public float idleRotThreshold = 0.1f;

    [Tooltip("Per-frame lerp factor for remote viewers smoothing toward the synced chair pose (0 = no movement, 1 = snap). 0.2 was visually smooth at ~10Hz updates in 3-client testing.")]
    [Range(0.05f, 1f)]
    public float remoteLerp = 0.2f;

    [Header("Callbacks (public VrcWorldTx__ API)")]
    [Tooltip("Optional UdonBehaviour that will receive callback events. Fires LOCAL-ONLY on the acting (seated) player's machine, post-work. Methods looked up by name on the target (silent no-op when missing, per VRChat's SendCustomEvent semantics):\n  VrcWorldTx__Entered(), VrcWorldTx__Exited(), VrcWorldTx__TxChanged()\nBefore each SendCustomEvent the chair writes Param__ fields onto the target (see README API section for the table). The receiver opts into the optional TxChanged event via a bool field VrcWorldTx__Config__IncludeTxChangedCalls — read once on station entry (cached for the seated duration). Leave null to disable callbacks entirely.")]
    public UdonBehaviour callbackTarget;

    [Tooltip("Minimum interval between consecutive VrcWorldTx__TxChanged fires (seconds). Caps callback frequency for high-rate grip motion. Set to 0 to fire as soon as any per-frame change exceeds the epsilons below.")]
    public float txChangedMinInterval = 0.1f;

    [Tooltip("Position-change threshold (meters) below which TxChanged will NOT fire — change-only contract. ~0.001 = 1mm.")]
    public float txChangedPosEpsilon = 0.001f;

    [Tooltip("Rotation-change threshold (degrees) below which TxChanged will NOT fire — change-only contract.")]
    public float txChangedRotEpsilon = 0.1f;

    [Tooltip("Eye-height-change threshold (meters) below which TxChanged will NOT fire — change-only contract.")]
    public float txChangedEyeHeightEpsilon = 0.001f;

    [Header("Per-player layout")]
    [Tooltip("X-axis spawn offset per player ID, in meters. When this script is on a VRCPlayerObject template, all per-player copies spawn at the same template position; offsetting by playerId * this value spreads chairs along X so click-targets and visuals don't overlap. Computed deterministically on every client (no sync). Set to 0 if your world places chairs explicitly via some other mechanism.")]
    public float perPlayerXSpacing = 1.5f;

    [UdonSynced] private Vector3 _syncedChairPos;
    [UdonSynced] private Quaternion _syncedChairRot;

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

    // HUD panel auto-scale + auto-position base (captured once at start). Both are scaled
    // by the per-frame avatar ratio so the panel stays in the same eye-relative spot in the
    // player's view across the full scale range.
    private Vector3 _hudPanelBaseScale;
    private Vector3 _hudPanelBasePosition;
    private bool _hudPanelBaseCaptured;

    // Last value we passed to SetAvatarEyeHeightByMeters this seated session. The setter has
    // no documented rate limit but it IS the API surface that drives cross-client scale sync
    // via VRChat's standard player networking (the radial-puppet bound setters and
    // SetManualAvatarScalingAllowed are local-only client state, no broadcast). To avoid
    // redundant network/event traffic, only call the setter when the target actually
    // differs from the last value we set. VR grip noise generally produces a fresh value
    // each frame during an active two-hand grip, so the gate is mostly meaningful for "held
    // perfectly still" / clamped frames, but free either way.
    private float _lastSetEyeHeight;

    // Sync state — only meaningful on the owner.
    private Vector3 _lastSerializedPos;
    private Quaternion _lastSerializedRot;
    private float _lastSerializeTime;

    // Per-player offset is applied once per instance (every client computes the same
    // value from owner.playerId, so this is local-only state).
    private bool _perPlayerOffsetApplied;

    // Callback bookkeeping. Resolved on station enter, cleared on exit. All callback
    // bookkeeping is LOCAL to the acting player — remote viewers never fire callbacks
    // (cross-client propagation is the consumer's responsibility per the API contract).
    //   `_callbackHasTarget` snapshots `callbackTarget != null` once on enter so a runtime
    //     Inspector unwire mid-session doesn't suddenly throw inside the firing path.
    //   `_callbackIncludeTxChanged` reads the receiver's Config flag once on enter so the
    //     per-frame TxChanged path doesn't pay a GetProgramVariable hit every frame, and
    //     so toggling the flag mid-session has well-defined "applies from next entry" semantics.
    //   `_lastFiredTx*` + `_lastTxFireTime` drive the change-only + min-interval gating.
    private bool _callbackHasTarget;
    private bool _callbackIncludeTxChanged;
    private Vector3 _lastFiredTxPos;
    private Quaternion _lastFiredTxRot;
    private float _lastFiredTxEyeHeight;
    private float _lastTxFireTime;

    private void Start()
    {
        if (scaleDisplayPanelTransform != null)
        {
            _hudPanelBaseScale = scaleDisplayPanelTransform.localScale;
            _hudPanelBasePosition = scaleDisplayPanelTransform.localPosition;
            _hudPanelBaseCaptured = true;
        }

        // Apply the per-player root offset early so the synced-pose seed below is at
        // the correct location. Owner is normally known by Start for VRCPlayerObject
        // copies; if not, LateUpdate retries until it lands.
        ApplyPerPlayerOffsetIfNeeded();

        // Seed synced fields so remote viewers don't lerp toward (0,0,0) before the
        // owner's first serialize lands. After Start, all clients hold the chair at
        // its post-offset position and lerp from there.
        if (chairTransform != null)
        {
            _syncedChairPos = chairTransform.position;
            _syncedChairRot = chairTransform.rotation;
            _lastSerializedPos = _syncedChairPos;
            _lastSerializedRot = _syncedChairRot;
        }

        // Hide the HUD panel on remote viewers. Each player only cares about their own
        // scale data; N panels in the world is visual noise. Owners keep it.
        if (scaleDisplayPanelTransform != null && !Networking.IsOwner(gameObject))
        {
            scaleDisplayPanelTransform.gameObject.SetActive(false);
        }
    }

    public override void Interact()
    {
        if (station == null) return;
        VRCPlayerApi p = Networking.LocalPlayer;
        if (p == null) return;

        // Per-player chair: only the owner sits in their own. For VRCPlayerObject
        // instances ownership is fixed by VRChat — the SetOwner call below no-ops
        // for non-owners and IsOwner stays false. For a non-PlayerObject scene
        // chair (single-chair fallback), ownership starts with the master and
        // SetOwner grabs it on first Interact, then the gate passes.
        if (!Networking.IsOwner(gameObject))
        {
            Networking.SetOwner(p, gameObject);
            if (!Networking.IsOwner(gameObject)) return;
        }

        station.UseStation(p);
    }

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        // Late-joiner refresh — push a serialize so the new player gets current state
        // immediately rather than waiting for the next motion-driven serialize. (UdonSynced
        // also sends initial state to joiners natively, but this is cheap belt-and-braces.)
        if (player == null || player.isLocal) return;
        if (!Networking.IsOwner(gameObject)) return;
        if (chairTransform == null) return;

        _syncedChairPos = chairTransform.position;
        _syncedChairRot = chairTransform.rotation;
        _lastSerializedPos = _syncedChairPos;
        _lastSerializedRot = _syncedChairRot;
        _lastSerializeTime = Time.time;
        RequestSerialization();
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
        _lastSetEyeHeight = _baselineEyeHeight; // matches current; first SolveTwoHand frame at ratio≈1 will skip the redundant set

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

        // Callback resolution for the seated session. Snapshot the wired-ness of the target
        // once so a runtime Inspector unwire mid-session doesn't break the firing path, and
        // read the receiver's TxChanged opt-in flag once so we don't pay a GetProgramVariable
        // hit per frame. Reading absent / non-bool returns false implicitly via the default-bool
        // fallthrough — receivers without the flag don't get TxChanged callbacks.
        _callbackHasTarget = (callbackTarget != null);
        _callbackIncludeTxChanged = false;
        if (_callbackHasTarget)
        {
            object flag = callbackTarget.GetProgramVariable("VrcWorldTx__Config__IncludeTxChangedCalls");
            if (flag != null) _callbackIncludeTxChanged = (bool)flag;
        }

        // Seed the TxChanged change-detect baseline at current state so the first material
        // change after entry fires, but a no-op enter (avatar lands at the seat unchanged)
        // does not. _lastTxFireTime stays at 0 so the min-interval gate is satisfied for
        // the first fire (Time.time has long since passed 0 by the time anyone is seated).
        if (chairTransform != null)
        {
            _lastFiredTxPos = chairTransform.position;
            _lastFiredTxRot = chairTransform.rotation;
        }
        _lastFiredTxEyeHeight = _baselineEyeHeight;
        _lastTxFireTime = 0f;

        // Post-work fire of Entered. After this point all session state is set up
        // (clamps, manual-scaling lock, callback caches) so the receiver sees a fully
        // initialised chair if it calls back into us.
        FireCallback_Entered();
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
            if (restoreAvatarHeightOnExit)
            {
                _localPlayer.SetAvatarEyeHeightByMeters(_baselineEyeHeight);
                _lastSetEyeHeight = _baselineEyeHeight;
            }
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

        // Post-work fire of Exited. Done AFTER avatar height / scaling-allowed / bounds /
        // interact-collider have all been restored, so receivers observing the chair from
        // their callback see a fully-quiesced state.
        FireCallback_Exited();
        _callbackHasTarget = false;
        _callbackIncludeTxChanged = false;
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
        _lastSetEyeHeight = _baselineEyeHeight; // keep the redundant-call gate in sync after avatar swap / external scale change
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
                // Multi-line readout. Each line answers a question the user is likely asking:
                //   "What did we last pass to SetEyeHeight?" -> Target (the *requested* value;
                //                                              compare to Eye height to spot
                //                                              VRChat silently clamping our calls)
                //   "How tall is my avatar right now?"       -> Eye height (the *applied* value)
                //   "What was I when I sat down?"            -> Baseline (entry)
                //   "How small/big am I vs entry?"           -> Scale ratio
                //   "What's the script's allowed range?"     -> Clamp + source
                //   "Has the avatar drifted from the seat?"  -> Offset (player world pos vs
                //                                               station enter location)
                // Target on top — when diagnosing scaling issues, the first thing you want
                // to see is "what did we just ask the API to do" vs "what did the API
                // actually apply." A persistent gap between Target and Eye height means
                // VRChat / AvatarScalingSettings / something is overriding our calls.
                string clampSource = _clampFromWorld ? "AvatarScalingSettings" : "minScale*baseline (fallback)";
                string offsetLine;
                if (station != null && station.stationEnterPlayerLocation != null)
                {
                    Vector3 playerPos = _localPlayer.GetPosition();
                    Vector3 entryPos = station.stationEnterPlayerLocation.position;
                    Vector3 delta = playerPos - entryPos;
                    offsetLine = "Offset: " + delta.magnitude.ToString("F3") + " m  (" +
                                 delta.x.ToString("F2") + ", " + delta.y.ToString("F2") + ", " + delta.z.ToString("F2") + ")";
                }
                else
                {
                    offsetLine = "Offset: (no station / entry location wired)";
                }
                scaleDisplayText.text =
                    "Target: " + _lastSetEyeHeight.ToString("F3") + " m  (last set)\n" +
                    "Eye height: " + current.ToString("F3") + " m\n" +
                    "Baseline: " + _baselineEyeHeight.ToString("F3") + " m  (entry)\n" +
                    "Ratio: " + ratio.ToString("F3") + "x\n" +
                    "Clamp: [" + _effectiveMinEyeHeight.ToString("F3") + ", " + _effectiveMaxEyeHeight.ToString("F3") + "] m\n" +
                    "  source: " + clampSource + "\n" +
                    offsetLine;
            }

            // Auto-scale the HUD panel to keep apparent size AND eye-relative position
            // roughly constant across player scale. Without the localPosition scaling, the
            // panel ends up too high overhead at small scales and too close to face at
            // large scales — because its base local position is sized for baseline eye
            // height, not for the current scaled avatar.
            if (scaleDisplayPanelTransform != null && _hudPanelBaseCaptured)
            {
                scaleDisplayPanelTransform.localScale = _hudPanelBaseScale * ratio;
                scaleDisplayPanelTransform.localPosition = _hudPanelBasePosition * ratio;
            }
        }
        else if (scaleDisplayText != null)
        {
            scaleDisplayText.text = "(not seated)";
        }
    }

    private void LateUpdate()
    {
        // Lazy retry of the per-player offset in case Start ran before owner was assigned.
        ApplyPerPlayerOffsetIfNeeded();

        bool isOwner = Networking.IsOwner(gameObject);

        if (!isOwner)
        {
            // Remote viewer: lerp the local chairTransform toward the synced pose.
            // The station is in Immobilize mode, so the seated remote avatar follows
            // chairTransform's children (in particular stationEnterPlayerLocation),
            // giving the remote-rendered seated player the correct world pose.
            if (chairTransform != null)
            {
                Vector3 cur = chairTransform.position;
                Quaternion curR = chairTransform.rotation;
                chairTransform.SetPositionAndRotation(
                    Vector3.Lerp(cur, _syncedChairPos, remoteLerp),
                    Quaternion.Slerp(curR, _syncedChairRot, remoteLerp)
                );
            }
            return;
        }

        // === Owner path ===
        // Run the solver only when the local player is seated and in VR. Otherwise the
        // chair just holds at its current pose — no synced changes either, so remote
        // viewers see it at rest at the last solved position.
        if (_isSeatedLocal && _localPlayer != null && _localPlayer.IsUserInVR())
        {
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

        // After the solver may have mutated chairTransform, push the new pose out via
        // UdonSynced if it actually changed. Motion-gated + rate-throttled — see TrySerialize.
        TrySerialize();

        // Post-work TxChanged fire. Runs only on the local seated player (callbacks are
        // local-only per the API contract — remote viewers' lerping doesn't count as a
        // "transform change" we own). Change-only + min-interval gated inside.
        if (_isSeatedLocal)
        {
            TryFireCallback_TxChanged();
        }
    }

    // Apply a one-time per-player X offset to the root transform. Computed deterministically
    // from owner.playerId so every client agrees on the same offset and we don't have to
    // sync the root pose itself. Safe to call repeatedly — it self-guards via _perPlayerOffsetApplied.
    //
    // Why move the ROOT and not just children: BoxCollider for the Interact zone lives on
    // the root (in local space). If we moved only the children, all per-player copies'
    // colliders would stack at the template position — Interact rays then hit an arbitrary
    // stacked collider regardless of which visible mesh you point at, and the IsOwner gate
    // no-ops most attempts. Caught by 3-client testing of the V2SyncSpike rig.
    private void ApplyPerPlayerOffsetIfNeeded()
    {
        if (_perPlayerOffsetApplied || perPlayerXSpacing <= 0f) return;
        VRCPlayerApi owner = Networking.GetOwner(gameObject);
        if (owner == null) return;
        transform.position = transform.position + new Vector3(owner.playerId * perPlayerXSpacing, 0f, 0f);
        _perPlayerOffsetApplied = true;
    }

    // Owner-side: serialize the chair pose if it's changed enough since last serialize,
    // subject to a max-rate throttle. Idle (chair pose static within thresholds) means no
    // serialize at all — bandwidth drops to zero while the player sits in one spot. Late
    // joiners get the current state via UdonSynced's initial-state delivery; OnPlayerJoined
    // additionally pushes a fresh serialize to refresh.
    private void TrySerialize()
    {
        if (chairTransform == null) return;

        Vector3 newPos = chairTransform.position;
        Quaternion newRot = chairTransform.rotation;

        float posDelta = (newPos - _lastSerializedPos).magnitude;
        float rotDelta = Quaternion.Angle(newRot, _lastSerializedRot);
        if (posDelta < idlePosThreshold && rotDelta < idleRotThreshold) return;

        float now = Time.time;
        float dt = now - _lastSerializeTime;
        float minInterval = 1f / Mathf.Max(0.1f, activeUpdatesPerSecond);
        if (dt < minInterval) return;

        _syncedChairPos = newPos;
        _syncedChairRot = newRot;
        _lastSerializedPos = newPos;
        _lastSerializedRot = newRot;
        _lastSerializeTime = now;
        RequestSerialization();
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
        _lastSetEyeHeight = _eyeHeightAtGrip; // first solve frame at scaleFactor≈1 will skip the redundant set

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

        // Skip the call if the value didn't change. SetAvatarEyeHeightByMeters drives the
        // cross-client scale sync via VRChat's player networking; firing it every frame with
        // an identical value is needless traffic + needless OnAvatarEyeHeightChanged events.
        // Exact float compare is fine here — the clamp / scaleFactor math is deterministic,
        // so identical hand poses produce identical eyeHeightTarget values bit-for-bit. VR
        // grip noise during active gripping generally produces a fresh value each frame
        // anyway; the gate primarily skips frames when the value lands exactly on a clamp
        // boundary or when hands are held perfectly still.
        if (eyeHeightTarget != _lastSetEyeHeight)
        {
            _localPlayer.SetAvatarEyeHeightByMeters(eyeHeightTarget);
            _lastSetEyeHeight = eyeHeightTarget;
        }
    }

    // ---- Callback dispatch (public VrcWorldTx__ API; see project_api_conventions.md) ----
    //
    // Wire chair-internal events to a single optional receiver UdonBehaviour. All three helpers:
    //   - early-out when there's no wired target (snapshotted on enter; immune to runtime unwire)
    //   - set Param__ fields on the target via SetProgramVariable BEFORE SendCustomEvent (Udon
    //     can't pass args through SendCustomEvent, so field-set-then-send is the only path)
    //   - rely on SendCustomEvent's silent-no-op-on-missing-method semantics — receivers that
    //     don't implement an event just see it dropped, no error
    //
    // SendCustomEvent dispatches synchronously on this client only — that's the local-only
    // firing contract. Cross-client propagation is the consumer's job if they need it.

    // Entered carries the same 7 Param fields as TxChanged. Initial pose + eye-height
    // are both already captured by the time we fire (chairTransform.position/.rotation
    // is stable from prior session / scene init / sync, and _baselineEyeHeight was just
    // assigned a few lines up in OnStationEntered). To keep the "all Old + New set every
    // call" rule of the spec, we set Old = New = entry-state — the receiver sees a zero
    // delta and can diff coherently against the first TxChanged that follows. This also
    // overwrites any stale Old values left by a prior session on the same receiver.
    private void FireCallback_Entered()
    {
        if (!_callbackHasTarget) return;
        Vector3 entryPos = (chairTransform != null) ? chairTransform.position : Vector3.zero;
        Quaternion entryRot = (chairTransform != null) ? chairTransform.rotation : Quaternion.identity;
        float entryEh = _baselineEyeHeight;
        callbackTarget.SetProgramVariable("VrcWorldTx__Param__SourceStation", this);
        callbackTarget.SetProgramVariable("VrcWorldTx__Param__OldPos", entryPos);
        callbackTarget.SetProgramVariable("VrcWorldTx__Param__OldRot", entryRot);
        callbackTarget.SetProgramVariable("VrcWorldTx__Param__OldEyeHeight", entryEh);
        callbackTarget.SetProgramVariable("VrcWorldTx__Param__NewPos", entryPos);
        callbackTarget.SetProgramVariable("VrcWorldTx__Param__NewRot", entryRot);
        callbackTarget.SetProgramVariable("VrcWorldTx__Param__NewEyeHeight", entryEh);
        callbackTarget.SendCustomEvent("VrcWorldTx__Entered");
    }

    // Exited deliberately carries ONLY SourceStation. Pose and eye-height would be
    // misleading or partial:
    //   - Chair pose at exit ≠ player's pose after exit (VRChat repositions the player
    //     to the station's exit transform). Reporting chair pose as "where the character
    //     is now" would mislead consumers.
    //   - Eye-height post-restore matches the character (when restoreAvatarHeightOnExit
    //     is true), but covering eye-height-only and not pose is asymmetric — worse than
    //     covering nothing here. Receivers that need a post-exit eye-height can read it
    //     directly via Networking.LocalPlayer.GetAvatarEyeHeightAsMeters().
    private void FireCallback_Exited()
    {
        if (!_callbackHasTarget) return;
        callbackTarget.SetProgramVariable("VrcWorldTx__Param__SourceStation", this);
        callbackTarget.SendCustomEvent("VrcWorldTx__Exited");
    }

    // Change-only + min-interval gated. The change check uses chairTransform pose for pos/rot
    // (the externally observable chair surface, matching what's [UdonSynced]) and the player's
    // current eye height in meters for size. Any one of (pos, rot, eye-height) exceeding its
    // epsilon since the last fire triggers a fire that sets all seven Param fields — receivers
    // can cheaply diff old vs new on the axis they care about.
    private void TryFireCallback_TxChanged()
    {
        if (!_callbackHasTarget) return;
        if (!_callbackIncludeTxChanged) return;
        if (chairTransform == null || _localPlayer == null) return;

        // Min-interval gate first — cheaper than fetching pose + eye-height when we're
        // throttle-blocked. Set txChangedMinInterval to 0 to disable.
        float now = Time.time;
        if (txChangedMinInterval > 0f && (now - _lastTxFireTime) < txChangedMinInterval) return;

        Vector3 newPos = chairTransform.position;
        Quaternion newRot = chairTransform.rotation;
        float newEyeHeight = _localPlayer.GetAvatarEyeHeightAsMeters();

        float posDelta = (newPos - _lastFiredTxPos).magnitude;
        float rotDelta = Quaternion.Angle(newRot, _lastFiredTxRot);
        float ehDelta = Mathf.Abs(newEyeHeight - _lastFiredTxEyeHeight);

        bool posChanged = posDelta > txChangedPosEpsilon;
        bool rotChanged = rotDelta > txChangedRotEpsilon;
        bool ehChanged = ehDelta > txChangedEyeHeightEpsilon;
        if (!posChanged && !rotChanged && !ehChanged) return;

        // All seven param fields set every fire; receivers diff cheaply on the axis they care.
        callbackTarget.SetProgramVariable("VrcWorldTx__Param__SourceStation", this);
        callbackTarget.SetProgramVariable("VrcWorldTx__Param__OldPos", _lastFiredTxPos);
        callbackTarget.SetProgramVariable("VrcWorldTx__Param__OldRot", _lastFiredTxRot);
        callbackTarget.SetProgramVariable("VrcWorldTx__Param__OldEyeHeight", _lastFiredTxEyeHeight);
        callbackTarget.SetProgramVariable("VrcWorldTx__Param__NewPos", newPos);
        callbackTarget.SetProgramVariable("VrcWorldTx__Param__NewRot", newRot);
        callbackTarget.SetProgramVariable("VrcWorldTx__Param__NewEyeHeight", newEyeHeight);
        callbackTarget.SendCustomEvent("VrcWorldTx__TxChanged");

        _lastFiredTxPos = newPos;
        _lastFiredTxRot = newRot;
        _lastFiredTxEyeHeight = newEyeHeight;
        _lastTxFireTime = now;
    }
}
