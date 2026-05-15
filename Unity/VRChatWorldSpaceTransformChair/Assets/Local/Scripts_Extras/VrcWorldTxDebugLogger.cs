using UdonSharp;
using UnityEngine;
using VRC.Udon;

// Drop-in receiver for the VrcWorldTx callback API. Logs each event to the Debug console
// with a configurable prefix tag, per-event opt-out checkboxes, and a single toggle for
// whether the station should fire TxChanged at all.
//
// Use this as a smoke-test target for new chair/station setups, or as a copy-paste
// starting point for your own receiver. See README "API — callbacks for receiver scripts"
// for the contract; this script implements every event in the table.
public class VrcWorldTxDebugLogger : UdonSharpBehaviour
{
    [Header("Logging")]
    [Tooltip("Prefix wrapped in [brackets] and prepended to each log line. e.g. logTag='LobbyChair' -> '[LobbyChair] '. Leave empty for no prefix. Helpful when multiple stations in a scene all log to the same console.")]
    public string logTag = "VrcWorldTx";

    [Tooltip("Log VrcWorldTx__Entered events.")]
    public bool logEntered = true;

    [Tooltip("Log VrcWorldTx__Exited events.")]
    public bool logExited = true;

    [Tooltip("Log VrcWorldTx__TxChanged events. Note: also requires 'Subscribe To TxChanged' below — without that, the station never fires TxChanged at all and this checkbox is moot.")]
    public bool logTxChanged = true;

    [Header("TxChanged subscription")]
    [Tooltip("Tells the station to fire VrcWorldTx__TxChanged on us. UI mirror of VrcWorldTx__Config__IncludeTxChangedCalls (copied to that API field in Start). Toggling at runtime takes effect on the NEXT station entry — either toggle before sitting, or exit and re-sit.")]
    public bool subscribeToTxChanged = true;

    // ---- Callback API surface (set by the station via SetProgramVariable) ----
    // Public so the station can write via SetProgramVariable. These appear in the Inspector
    // as live "what was the last payload" diagnostic readouts — useful while debugging.
    private UdonBehaviour VrcWorldTx__Param__SourceStation;
    private Vector3 VrcWorldTx__Param__OldPos;
    private Quaternion VrcWorldTx__Param__OldRot;
    private float VrcWorldTx__Param__OldEyeHeight;
    private Vector3 VrcWorldTx__Param__NewPos;
    private Quaternion VrcWorldTx__Param__NewRot;
    private float VrcWorldTx__Param__NewEyeHeight;

    // The opt-in flag the station reads on entry. Hidden from Inspector because the user-facing
    // toggle is `subscribeToTxChanged` above; we copy it across in Start so the API name stays
    // exactly as the contract requires while the Inspector gets a readable label.
    private bool VrcWorldTx__Config__IncludeTxChangedCalls;

    private void Start()
    {
        // Copy the UI-friendly checkbox into the API-named field that the station's
        // OnStationEntered reads via GetProgramVariable. Runs once at scene load,
        // before any player could possibly be sitting in the station.
        VrcWorldTx__Config__IncludeTxChangedCalls = subscribeToTxChanged;
    }

    public void VrcWorldTx__Entered()
    {
        if (!logEntered) return;
        // Entered carries entry pose + eye-height (Old=New per the contract). Show the
        // entry-state values so the log reads as a useful "session started at X" line.
        Debug.Log(Prefix() + "Entered" +
            "  pos=" + VrcWorldTx__Param__NewPos.ToString("F3") +
            "  eh=" + VrcWorldTx__Param__NewEyeHeight.ToString("F3") + "m" +
            "  source=" + DescribeSource());
    }

    public void VrcWorldTx__Exited()
    {
        if (!logExited) return;
        Debug.Log(Prefix() + "Exited  source=" + DescribeSource());
    }

    public void VrcWorldTx__TxChanged()
    {
        if (!logTxChanged) return;
        float dPos = (VrcWorldTx__Param__NewPos - VrcWorldTx__Param__OldPos).magnitude;
        float dRot = Quaternion.Angle(VrcWorldTx__Param__OldRot, VrcWorldTx__Param__NewRot);
        float dEh = VrcWorldTx__Param__NewEyeHeight - VrcWorldTx__Param__OldEyeHeight;
        Debug.Log(Prefix() + "TxChanged" +
            "  dPos=" + dPos.ToString("F3") + "m" +
            "  dRot=" + dRot.ToString("F2") + "deg" +
            "  dEH=" + dEh.ToString("F3") + "m" +
            "  newEH=" + VrcWorldTx__Param__NewEyeHeight.ToString("F3") + "m" +
            "  source=" + DescribeSource());
    }

    private string Prefix()
    {
        if (logTag == null || logTag.Length == 0) return "";
        return "[" + logTag + "] ";
    }

    private string DescribeSource()
    {
        if (VrcWorldTx__Param__SourceStation == null) return "(null)";
        return VrcWorldTx__Param__SourceStation.name;
    }
}
