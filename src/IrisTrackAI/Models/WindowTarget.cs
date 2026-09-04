namespace IrisTrackAI.Models;

public sealed record WindowTarget(nint Hwnd, string Title, uint ProcessId)
{
    public string DisplayName => $"{Title}  [PID {ProcessId}]";
    public override string ToString() => DisplayName;
}
