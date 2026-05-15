using UdonSharp;
using UnityEngine;
using VRC.Udon;

// Fan-out receiver for the VrcWorldTx callback API. Wire this into the station's
// callbackTarget slot, then list any number of downstream UdonBehaviours in `targets`
// to have each VrcWorldTx__Entered / Exited / TxChanged event mirrored onto every
// target in array order. Null entries are skipped (safe — useful for sparse arrays
// that you populate selectively).
//
// Each downstream target sees the SAME Param fields the station originally set on us —
// SourceStation points at the original station UB, NOT at this rebroadcaster. So
// receivers downstream of the rebroadcaster behave identically to receivers wired
// directly into the station, modulo per-target Config opt-in (see below).
//
// CAVEAT — Config flags are NOT re-checked per target. The rebroadcaster's own
// VrcWorldTx__Config__IncludeTxChangedCalls determines whether the station fires
// TxChanged on us. Once we receive TxChanged, we forward it to ALL targets
// unconditionally — we do NOT inspect each downstream target's own opt-in flag.
// (a) The station only checked the flag on the directly-wired UB, which is us.
// (b) SendCustomEvent on a target that doesn't implement the method is a silent
//     no-op anyway, so the cost is one wasted SetProgramVariable burst + send per
//     target that didn't want it. Fine for the typical small-fanout use case.
//
// This is the propagator component referenced as "build only if demand surfaces" in
// project_roadmap.md / project_api_conventions.md — minimal first cut. Extend later
// if real use surfaces a need for per-target opt-in.
public class VrcWorldTxRebroadcaster : UdonSharpBehaviour
{
    [Header("Fan-out targets")]
    [Tooltip("UdonBehaviour receivers to forward VrcWorldTx callbacks to. Each gets the same Param fields written + the same SendCustomEvent fired, in array order. Null entries are skipped.")]
    public UdonBehaviour[] targets;

    [Header("TxChanged subscription")]
    [Tooltip("Tells the upstream station to fire VrcWorldTx__TxChanged on us (which we then forward to targets). UI mirror of VrcWorldTx__Config__IncludeTxChangedCalls (copied to that API field in Start). Toggling at runtime takes effect on the NEXT station entry.")]
    public bool subscribeToTxChanged = true;

    // ---- Callback API surface (set by the upstream station via SetProgramVariable) ----
    // Private: SetProgramVariable / GetProgramVariable still reach private fields on a
    // U# behaviour, and keeping them private avoids Inspector clutter without the
    // [HideInInspector] dance. (Same pattern the sibling VrcWorldTxDebugLogger uses;
    // empirically verified 2026-05-15.)
    private UdonBehaviour VrcWorldTx__Param__SourceStation;
    private Vector3 VrcWorldTx__Param__OldPos;
    private Quaternion VrcWorldTx__Param__OldRot;
    private float VrcWorldTx__Param__OldEyeHeight;
    private Vector3 VrcWorldTx__Param__NewPos;
    private Quaternion VrcWorldTx__Param__NewRot;
    private float VrcWorldTx__Param__NewEyeHeight;

    private bool VrcWorldTx__Config__IncludeTxChangedCalls;

    private void Start()
    {
        VrcWorldTx__Config__IncludeTxChangedCalls = subscribeToTxChanged;
    }

    // Entered carries the same 7 Param fields as TxChanged (Old = New = entry-state).
    // Forward all of them so downstream targets see the entry pose / eye-height.
    public void VrcWorldTx__Entered()
    {
        if (targets == null) return;
        for (int i = 0; i < targets.Length; i++)
        {
            UdonBehaviour t = targets[i];
            if (t == null) continue;
            t.SetProgramVariable("VrcWorldTx__Param__SourceStation", VrcWorldTx__Param__SourceStation);
            t.SetProgramVariable("VrcWorldTx__Param__OldPos", VrcWorldTx__Param__OldPos);
            t.SetProgramVariable("VrcWorldTx__Param__OldRot", VrcWorldTx__Param__OldRot);
            t.SetProgramVariable("VrcWorldTx__Param__OldEyeHeight", VrcWorldTx__Param__OldEyeHeight);
            t.SetProgramVariable("VrcWorldTx__Param__NewPos", VrcWorldTx__Param__NewPos);
            t.SetProgramVariable("VrcWorldTx__Param__NewRot", VrcWorldTx__Param__NewRot);
            t.SetProgramVariable("VrcWorldTx__Param__NewEyeHeight", VrcWorldTx__Param__NewEyeHeight);
            t.SendCustomEvent("VrcWorldTx__Entered");
        }
    }

    // Exited only carries SourceStation by design (see project_api_conventions.md / README).
    public void VrcWorldTx__Exited()
    {
        if (targets == null) return;
        for (int i = 0; i < targets.Length; i++)
        {
            UdonBehaviour t = targets[i];
            if (t == null) continue;
            t.SetProgramVariable("VrcWorldTx__Param__SourceStation", VrcWorldTx__Param__SourceStation);
            t.SendCustomEvent("VrcWorldTx__Exited");
        }
    }

    public void VrcWorldTx__TxChanged()
    {
        if (targets == null) return;
        for (int i = 0; i < targets.Length; i++)
        {
            UdonBehaviour t = targets[i];
            if (t == null) continue;
            t.SetProgramVariable("VrcWorldTx__Param__SourceStation", VrcWorldTx__Param__SourceStation);
            t.SetProgramVariable("VrcWorldTx__Param__OldPos", VrcWorldTx__Param__OldPos);
            t.SetProgramVariable("VrcWorldTx__Param__OldRot", VrcWorldTx__Param__OldRot);
            t.SetProgramVariable("VrcWorldTx__Param__OldEyeHeight", VrcWorldTx__Param__OldEyeHeight);
            t.SetProgramVariable("VrcWorldTx__Param__NewPos", VrcWorldTx__Param__NewPos);
            t.SetProgramVariable("VrcWorldTx__Param__NewRot", VrcWorldTx__Param__NewRot);
            t.SetProgramVariable("VrcWorldTx__Param__NewEyeHeight", VrcWorldTx__Param__NewEyeHeight);
            t.SendCustomEvent("VrcWorldTx__TxChanged");
        }
    }
}
