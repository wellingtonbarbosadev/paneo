namespace WindowProbe.Capture;

public class CaptureException : Exception
{
    public CaptureException(string message)
        : base(message)
    {
    }

    public CaptureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class CaptureTimeoutException : CaptureException
{
    public CaptureTimeoutException(string message)
        : base(message)
    {
    }
}

public sealed record CaptureResult(
    string Path,
    long Bytes,
    int Width,
    int Height,
    TimeSpan Duration);
