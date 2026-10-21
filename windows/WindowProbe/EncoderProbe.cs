using System.Text.Json;
using WindowProbe.Remote;

namespace WindowProbe;

/// <summary>
/// Enumera os encoders H.264 disponíveis (hardware e software) e mede o
/// tempo de codificação de quadros sintéticos 1920×1080. Não usa WGC.
///
/// Uso: WindowProbe.exe --encoder-probe &lt;arquivo.json&gt;
/// </summary>
internal static class EncoderProbe
{
    internal static async Task<int> RunAsync(string outputPath)
    {
        var lines = new List<string>();
        bool any = false;

        foreach (string description in MfH264Encoder.Describe())
        {
            lines.Add(description);
        }

        // Perfis práticos: 1080p a 30 fps e 720p a 30 fps.
        var results = new List<object>();
        foreach ((int width, int height, int bitrate) in new[] {
            (1920, 1080, 8_000_000),
            (1280, 720, 6_000_000),
            (640, 360, 4_000_000),
            (640, 352, 4_000_000),
            (626, 353, 4_000_000) })
        {
            try
            {
                using var encoder = new MfH264Encoder(width, height, 30, bitrate);
                byte[] frame = SyntheticFrame(width, height, 64);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                int units = 0;
                int keyframes = 0;
                long bytes = 0;
                for (int index = 0; index < 30; index++)
                {
                    foreach (EncodedAccessUnit unit in encoder.Encode(frame, width * 4, index * 1000L / 30))
                    {
                        units++;
                        bytes += unit.Data.Length;
                        if (unit.Keyframe) keyframes++;
                    }
                }
                watch.Stop();
                any = true;
                results.Add(new
                {
                    success = true,
                    encoder = encoder.Name,
                    hardware = encoder.IsHardware,
                    requestedWidth = width,
                    requestedHeight = height,
                    encodedWidth = encoder.Width,
                    encodedHeight = encoder.Height,
                    accessUnits = units,
                    keyframes,
                    bytes,
                    averageEncodeMs = Math.Round(watch.Elapsed.TotalMilliseconds / Math.Max(1, units), 2),
                    effectiveFps = Math.Round(units / Math.Max(0.001, watch.Elapsed.TotalSeconds), 1),
                    bitrateKbps = (int)Math.Round(bytes * 8.0 / Math.Max(0.001, watch.Elapsed.TotalSeconds) / 1000.0)
                });
            }
            catch (Exception ex)
            {
                results.Add(new
                {
                    success = false,
                    requestedWidth = width,
                    requestedHeight = height,
                    error = ex.GetType().Name,
                    detail = ex.Message
                });
            }
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
        {
            success = any,
            encoders = lines,
            results
        }));
        return any ? 0 : 1;
    }

    private static byte[] SyntheticFrame(int width, int height, int blockSize)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = (y * width + x) * 4;
                bool inBlock = (x / blockSize + y / blockSize) % 2 == 0;
                pixels[at + 0] = inBlock ? (byte)220 : (byte)40;
                pixels[at + 1] = (byte)((x / 4) & 0xFF);
                pixels[at + 2] = (byte)((y / 4) & 0xFF);
                pixels[at + 3] = 0xFF;
            }
        }
        return pixels;
    }
}
