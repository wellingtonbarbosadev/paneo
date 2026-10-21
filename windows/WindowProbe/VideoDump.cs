using System.Diagnostics;
using System.Text.Json;
using WindowProbe.Remote;

namespace WindowProbe;

/// <summary>
/// Gera um arquivo de mídia no enquadramento do protocolo v2 a partir do
/// encoder H.264 real do Windows (Media Foundation), sem depender da captura
/// WGC. Serve para validar o decodificador do Mac (VideoToolbox) com bytes
/// produzidos pelo host.
///
/// Uso: WindowProbe.exe --video-dump &lt;pasta&gt;
/// Gera "video-media.bin" (enquadramento v2) e "video-dump.json" (métricas).
/// </summary>
internal static class VideoDump
{
    private const int Width = 640;
    private const int Height = 360;
    private const int Frames = 60;
    private const int Fps = 30;

    internal static Task<int> RunAsync(string directory, int width = Width, int height = Height, bool requestKeyframe = true) =>
        // Os samples usam ponteiros COM nativos. Criar e usar o encoder no MTA
        // evita atravessar o STA do WPF após uma gravação assíncrona no arquivo.
        Task.Run(() => RunCoreAsync(directory, width, height, requestKeyframe));

    private static async Task<int> RunCoreAsync(string directory, int width, int height, bool requestKeyframe)
    {
        Directory.CreateDirectory(directory);
        string mediaPath = Path.Combine(directory, "video-media.bin");
        string reportPath = Path.Combine(directory, "video-dump.json");

        try
        {
            using var encoder = new MfH264Encoder(width, height, Fps, 8_000_000);
            if (requestKeyframe) encoder.RequestKeyframe();

            await using var media = new FileStream(
                mediaPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true);

            long bytes = 0;
            int accessUnits = 0;
            int keyframes = 0;
            bool annexB = false;
            double encodeMs = 0;
            var clock = Stopwatch.StartNew();

            for (int index = 0; index < Frames; index++)
            {
                if (requestKeyframe && index == Frames / 2) encoder.RequestKeyframe();
                byte[] bgra = SyntheticFrame(index, width, height);
                long start = Stopwatch.GetTimestamp();
                List<EncodedAccessUnit> units = encoder.Encode(bgra, width * 4, index * 1000L / Fps);
                encodeMs += (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

                await WriteUnitsAsync(units).ConfigureAwait(false);
            }
            await WriteUnitsAsync(encoder.Complete()).ConfigureAwait(false);

            async Task WriteUnitsAsync(List<EncodedAccessUnit> units)
            {
                foreach (EncodedAccessUnit unit in units)
                {
                    byte[] framed = ProtocolV2.FrameMedia(
                        ProtocolV2.CodecH264,
                        unit.Keyframe,
                        (uint)(accessUnits + 1),
                        (uint)Math.Max(0, unit.PtsMs),
                        (ushort)encoder.Width,
                        (ushort)encoder.Height,
                        unit.Data);

                    await media.WriteAsync(framed).ConfigureAwait(false);
                    bytes += unit.Data.Length;
                    accessUnits++;
                    if (unit.Keyframe)
                    {
                        keyframes++;
                        if (!annexB) annexB = LooksLikeAnnexB(unit.Data);
                    }
                }
            }

            clock.Stop();
            double seconds = Math.Max(0.001, clock.Elapsed.TotalSeconds);
            bool success = accessUnits == Frames && keyframes >= (requestKeyframe ? 2 : 1) && annexB;

            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                success,
                encoder = encoder.Name,
                hardware = encoder.IsHardware,
                width = width,
                height = height,
                encodedWidth = encoder.Width,
                encodedHeight = encoder.Height,
                frames = Frames,
                accessUnits,
                keyframes,
                bytes,
                annexB,
                averageEncodeMs = accessUnits > 0 ? Math.Round(encodeMs / accessUnits, 2) : 0,
                effectiveFps = Math.Round(accessUnits / seconds, 1),
                bitrateKbps = (int)Math.Round(bytes * 8.0 / (Frames / (double)Fps) / 1000.0),
                mediaFile = Path.GetFileName(mediaPath)
            }));

            return success ? 0 : 1;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.GetType().Name,
                detail = ex.Message,
                diagnostic = MfH264Encoder.LastDiagnostic
            }));
            return 1;
        }
    }

    /// <summary>
    /// Quadro sintético em BGRA com gradiente e um bloco que se move: conteúdo
    /// real para o encoder, sem tela do usuário.
    /// </summary>
    private static byte[] SyntheticFrame(int index, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        int blockX = index * 6 % Math.Max(1, width - 64);
        int blockY = index * 3 % Math.Max(1, height - 64);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = (y * width + x) * 4;
                bool inBlock = x >= blockX && x < blockX + 64 && y >= blockY && y < blockY + 64;
                pixels[at + 0] = inBlock ? (byte)255 : (byte)((x + index) & 0xFF);        // B
                pixels[at + 1] = inBlock ? (byte)64 : (byte)((y * 2) & 0xFF);         // G
                pixels[at + 2] = inBlock ? (byte)32 : (byte)(((x + y) / 4) & 0xFF);   // R
                pixels[at + 3] = 0xFF;                                                // A
            }
        }

        return pixels;
    }

    private static bool LooksLikeAnnexB(byte[] data)
    {
        if (data.Length < 4) return false;
        bool four = data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1;
        bool three = data[0] == 0 && data[1] == 0 && data[2] == 1;
        return four || three;
    }
}
