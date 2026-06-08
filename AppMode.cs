namespace SDRIQStreamer.App;

/// <summary>
/// Top-level operating mode selected on the Launch tab (issue #28). Determines
/// which working tabs are shown. CW Mode = the existing CW Skimmer tabs
/// (unchanged); Digital Mode = the WSJT-X / JTDX screens. Hard modes: one family
/// at a time (see PLAN-issue28-multimode.md).
/// </summary>
public enum AppMode
{
    Cw,
    Digital,
}
