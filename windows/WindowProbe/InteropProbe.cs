using System.Runtime.InteropServices;
using System.Text.Json;

namespace WindowProbe;

/// <summary>
/// Sonda de interoperabilidade COM do Media Foundation: descobre quais
/// interfaces o objeto de sample realmente suporta e como a vtable está
/// organizada. Só leitura; não altera o produto.
///
/// Uso: WindowProbe.exe --interop-probe &lt;arquivo.json&gt;
/// </summary>
internal static class InteropProbe
{
    private static readonly Guid IidIMFSample = new("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4");
    private static readonly Guid IidIMFMediaSample = new("3CBC86E0-2E1C-4DB6-BF07-C5D4B7C8C0AC");
    private static readonly Guid IidIMFMediaBuffer = new("045FA593-8799-42B8-BC8D-8968C6453507");
    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");

    internal static async Task<int> RunAsync(string outputPath)
    {
        int startup = MFStartup(0x20070, 0);
        var report = new Dictionary<string, object?>();

        try
        {
            int created = MFCreateSample(out IntPtr sample);
            report["MFCreateSample"] = "0x" + created.ToString("X8");
            report["samplePointer"] = "0x" + sample.ToString("X");

            report["QI_IMFSample"] = DescribeQi(sample, IidIMFSample);
            report["QI_IMFMediaSample"] = DescribeQi(sample, IidIMFMediaSample);
            report["QI_IUnknown"] = DescribeQi(sample, IidIUnknown);

            int buffer = MFCreateMemoryBuffer(1024, out IntPtr mediaBuffer);
            report["MFCreateMemoryBuffer"] = "0x" + buffer.ToString("X8");
            report["QI_IMFMediaBuffer"] = DescribeQi(mediaBuffer, IidIMFMediaBuffer);

            report["vtableSample"] = DescribeSlots(sample, 48);
        }
        catch (Exception ex)
        {
            report["error"] = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            try { MFShutdown(); } catch { }
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static object DescribeQi(IntPtr pointer, Guid iid)
    {
        int hr = Marshal.QueryInterface(pointer, in iid, out IntPtr result);
        string text = "0x" + hr.ToString("X8") + " -> 0x" + result.ToString("X");
        if (hr == 0 && result != IntPtr.Zero) Marshal.Release(result);
        return text;
    }

    /// <summary>Lista os endereços dos slots da vtable para comparação.</summary>
    private static object DescribeSlots(IntPtr pointer, int count)
    {
        var slots = new List<string>();
        unsafe
        {
            IntPtr vtable = *(IntPtr*)pointer;
            for (int index = 0; index < count; index++)
            {
                IntPtr slot = *(IntPtr*)(vtable + index * IntPtr.Size);
                slots.Add(index + ":0x" + slot.ToString("X"));
            }
        }
        return slots;
    }

    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(int version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMemoryBuffer(uint maxLength, out IntPtr buffer);
}
