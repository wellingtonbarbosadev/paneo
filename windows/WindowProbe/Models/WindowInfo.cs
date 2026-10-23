namespace WindowProbe.Models;

public sealed record WindowInfo(
    IntPtr Handle,
    uint ProcessId,
    string ProcessName,
    string Title,
    int Width,
    int Height,
    string? IconBase64 = null)
{
    public string HandleText => $"0x{Handle.ToInt64():X}";

    public string DimensionsText => $"{Width} × {Height}";

    public string Summary => $"{ProcessName} — {Title}";

    public override string ToString() => Summary;
}
