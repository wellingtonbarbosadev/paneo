namespace WindowProbe.Remote;

/// <summary>Downscales BGRA frames into a reused buffer while preserving aspect ratio.</summary>
internal static class VideoScaling
{
    internal static int EncodedDimension(int value) => checked(((value & ~1) + 15) & ~15);

    internal static (int X, int Y, int Width, int Height) ScaleViewport(
        (int X, int Y, int Width, int Height) viewport, int sourceWidth, int sourceHeight, int activeWidth, int activeHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || activeWidth <= 0 || activeHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Dimensões de viewport inválidas.");
        return (
            Scale(viewport.X, activeWidth, sourceWidth),
            Scale(viewport.Y, activeHeight, sourceHeight),
            Scale(viewport.Width, activeWidth, sourceWidth),
            Scale(viewport.Height, activeHeight, sourceHeight));
    }

    private static int Scale(int value, int targetSize, int sourceSize) =>
        (int)Math.Round((double)value * targetSize / sourceSize);

    internal static bool TryMapInput(double x, double y, double scaleX, double scaleY, out double mappedX, out double mappedY)
    {
        mappedX = mappedY = 0;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(scaleX) || !double.IsFinite(scaleY)
            || x < 0 || x >= 1 || y < 0 || y >= 1 || scaleX < 1 || scaleY < 1) return false;
        mappedX = x * scaleX;
        mappedY = y * scaleY;
        return mappedX < 1 && mappedY < 1;
    }

    internal static (byte[] Pixels, int Width, int Height, int Stride) Apply(
        byte[] pixels, int width, int height, int stride, int maxDimension, ref byte[]? buffer)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width is < 2 or > ushort.MaxValue || height is < 2 or > ushort.MaxValue
            || stride < (long)width * 4 || (long)stride * height > pixels.Length)
            throw new ArgumentException("Quadro BGRA inválido.");
        if (maxDimension != 0 && (maxDimension is < 320 or > 4096))
            throw new ArgumentOutOfRangeException(nameof(maxDimension));

        if (maxDimension == 0 || Math.Max(width, height) <= maxDimension)
            return (pixels, width, height, stride);

        // Media Foundation pads coded dimensions to 16; reserve that padding inside the requested cap.
        int cap = maxDimension & ~15;
        double scale = (double)cap / Math.Max(width, height);
        int scaledWidth = (int)(width * scale) & ~1;
        int scaledHeight = (int)(height * scale) & ~1;
        if (scaledWidth < 2 || scaledHeight < 2) throw new ArgumentException("Quadro pequeno demais para o encoder H.264.");
        int targetStride = checked(scaledWidth * 4);
        int bytes = checked(targetStride * scaledHeight);
        if (buffer is null || buffer.Length < bytes) buffer = new byte[bytes];

        for (int y = 0; y < scaledHeight; y++)
        {
            int sourceRow = (int)((long)y * height / scaledHeight) * stride;
            int targetRow = y * targetStride;
            for (int x = 0; x < scaledWidth; x++)
            {
                int source = sourceRow + (int)((long)x * width / scaledWidth) * 4;
                int target = targetRow + x * 4;
                buffer[target] = pixels[source];
                buffer[target + 1] = pixels[source + 1];
                buffer[target + 2] = pixels[source + 2];
                buffer[target + 3] = pixels[source + 3];
            }
        }

        return (buffer, scaledWidth, scaledHeight, targetStride);
    }
}
