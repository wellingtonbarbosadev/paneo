using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace WindowProbe.Remote;

/// <summary>
/// Encoder H.264 por Media Foundation (hardware quando existir, software como
/// fallback). Entrada em BGRA (memória do sistema), conversão NV12 na CPU para
/// evitar interop D3D11 adicional nesta primeira versão. Sem B-frames, CBR e
/// keyframe sob demanda (docs/VIDEO.md).
/// </summary>
internal sealed class MfH264Encoder : IDisposable
{
    private const int MF_VERSION = 0x20070;
    private const uint MFSTARTUP_FULL = 0;
    private const uint MFT_ENUM_FLAG_HARDWARE = 0x4;
    private const uint MFT_ENUM_FLAG_SYNCMFT = 0x1;
    private const uint MFT_ENUM_FLAG_ASYNCMFT = 0x2;
    private const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x40;
    private const uint MFT_ENUM_FLAG_TRANSCODE_ONLY = 0x8;

    private const uint MF_E_TRANSFORM_NEED_MORE_INPUT = 0xC00D6D72;
    private const uint MF_E_TRANSFORM_STREAM_CHANGE = 0xC00D6D61;

    private const int MFVideoInterlace_Progressive = 2;

    private readonly int width;
    private readonly int height;
    private readonly int sourceWidth;
    private readonly int sourceHeight;
    private readonly int fps;
    private readonly object gate = new();
    private IMFTransform? transform;
    private bool disposed;
    private long frameIndex;
    private uint outputStreamFlags;
    private uint outputBufferSize;

    private static string lastDiagnostic = "";
    public static string LastDiagnostic => lastDiagnostic;
    public string Name { get; } = "desconhecido";
    /// <summary>Largura realmente codificada (alinhada em 16).</summary>
    public int Width => width;
    /// <summary>Altura realmente codificada (alinhada em 16).</summary>
    public int Height => height;
    /// <summary>Largura da janela capturada, antes do alinhamento.</summary>
    public int SourceWidth => sourceWidth;
    /// <summary>Altura da janela capturada, antes do alinhamento.</summary>
    public int SourceHeight => sourceHeight;
    public bool IsHardware { get; }
    public int Bitrate { get; }

    public MfH264Encoder(int width, int height, int fps, int bitrate)
    {
        if (width <= 0 || height <= 0 || fps <= 0 || bitrate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Parâmetros de vídeo inválidos.");
        }

        this.sourceWidth = width & ~1;
        this.sourceHeight = height & ~1;
        this.width = Align16(this.sourceWidth);
        this.height = Align16(this.sourceHeight);
        this.fps = fps;
        Bitrate = bitrate;

        int hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
        if (hr < 0) throw new InvalidOperationException("MFStartup falhou: 0x" + hr.ToString("X8"));

        try
        {
            (transform, Name, IsHardware) = CreateTransform(this.width, this.height, fps, bitrate);
            Configure(this.transform, this.width, this.height, fps, bitrate);
            if (transform.GetOutputStreamInfo(0, out MFTOutputStreamInfo info) >= 0)
            {
                outputStreamFlags = info.Flags;
                outputBufferSize = info.Size;
            }
            transform.ProcessMessage(MFTMessageType.NotifyBeginStreaming, IntPtr.Zero);
            int started = transform.ProcessMessage(MFTMessageType.NotifyStartOfStream, IntPtr.Zero);
            if (started < 0) throw new InvalidOperationException("StartOfStream falhou: 0x" + started.ToString("X8"));
            RequestKeyframe();
        }
        catch
        {
            // Sem este balanço, uma tentativa falha deixa o MF iniciado e um
            // MFT pendurado a cada resize da janela.
            if (transform is not null)
            {
                try { Marshal.ReleaseComObject(transform); } catch { }
                transform = null;
            }
            try { MFShutdown(); } catch { }
            throw;
        }
    }

    /// <summary>Codifica um quadro BGRA. Devolve zero ou mais access units Annex-B.</summary>
    public List<EncodedAccessUnit> Encode(ReadOnlySpan<byte> bgra, int stride, long ptsMs)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            byte[] nv12 = BgraToNv12(bgra, stride, sourceWidth, sourceHeight, width, height);
            IMFMediaBuffer buffer = CreateBuffer(nv12.Length);
            try
            {
                buffer.Lock(out IntPtr pointer, out _, out _);
                try { Marshal.Copy(nv12, 0, pointer, nv12.Length); }
                finally { buffer.Unlock(); }

                buffer.SetCurrentLength((uint)nv12.Length);

                IntPtr samplePointer = CreateSample();
                try
                {
                    var sample = new MfSample(samplePointer);
                    IntPtr bufferUnknown = Marshal.GetIUnknownForObject(buffer);
                    try { sample.AddBuffer(bufferUnknown); }
                    finally { Marshal.Release(bufferUnknown); }
                    sample.SetSampleTime(ptsMs * 10_000);
                    sample.SetSampleDuration(10_000_000L / fps);
                    int hr = transform!.ProcessInput(0, samplePointer, 0);
                    if (hr < 0) throw new InvalidOperationException("ProcessInput falhou: 0x" + hr.ToString("X8"));
                }
                finally { Marshal.Release(samplePointer); }
            }
            finally { Marshal.ReleaseComObject(buffer); }

            frameIndex++;
            return DrainOutputs();
        }
    }

    /// <summary>Entrega os quadros retidos pelo encoder ao finalizar a entrada.</summary>
    public List<EncodedAccessUnit> Complete()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Marshal.ThrowExceptionForHR(transform!.ProcessMessage(MFTMessageType.NotifyEndOfStream, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(transform.ProcessMessage(MFTMessageType.CommandDrain, IntPtr.Zero));
            return DrainOutputs();
        }
    }

    public void RequestKeyframe()
    {
        lock (gate)
        {
            if (disposed) return;
            // A API de codec é opcional: o MFT ignora se não suportar.
            if (transform is not ICodecAPI codecApi) return;
            // AVEncVideoForceKeyFrame exige ULONG (VT_UI4 = 19).
            SetVariant(codecApi, ref CodecApiGuids.AVEncVideoForceKeyFrame, 19 /* VT_UI4 */, 1);
        }
    }

    private List<EncodedAccessUnit> DrainOutputs()
    {
        var result = new List<EncodedAccessUnit>();
        while (true)
        {
            // MFTs sem MFT_OUTPUT_STREAM_PROVIDES_SAMPLES exigem que o chamador
            // forneça o sample de saída; sem isso ProcessOutput devolve E_INVALIDARG.
            bool providesSamples = (outputStreamFlags & 0x100) != 0;
            IntPtr providedSample = IntPtr.Zero;
            IntPtr providedBuffer = IntPtr.Zero;
            var output = default(MFTOutputDataBuffer);
            if (!providesSamples)
            {
                uint size = Math.Max(outputBufferSize, 1024u * 1024u);
                if (MFCreateMemoryBuffer(size, out IMFMediaBuffer buffer) >= 0)
                {
                    providedBuffer = Marshal.GetIUnknownForObject(buffer);
                    Marshal.ReleaseComObject(buffer);
                    providedSample = CreateSample();
                    var wrapper = new MfSample(providedSample);
                    wrapper.AddBuffer(providedBuffer);
                    output.Sample = providedSample;
                }
            }

            int hr;
            try { hr = transform!.ProcessOutput(0, 1, ref output, out _); }
            finally { if (output.Events != IntPtr.Zero) Marshal.Release(output.Events); }
            if (hr == unchecked((int)MF_E_TRANSFORM_NEED_MORE_INPUT))
            {
                if (providedSample != IntPtr.Zero) Marshal.Release(providedSample);
                if (providedBuffer != IntPtr.Zero) Marshal.Release(providedBuffer);
                break;
            }
            if (hr == unchecked((int)MF_E_TRANSFORM_STREAM_CHANGE))
            {
                if (providedSample != IntPtr.Zero) Marshal.Release(providedSample);
                if (providedBuffer != IntPtr.Zero) Marshal.Release(providedBuffer);
                NegotiateOutputType();
                continue;
            }
            if (hr < 0)
            {
                if (providedSample != IntPtr.Zero) Marshal.Release(providedSample);
                if (providedBuffer != IntPtr.Zero) Marshal.Release(providedBuffer);
                throw new InvalidOperationException("ProcessOutput falhou: 0x" + hr.ToString("X8"));
            }
            if (output.Sample == IntPtr.Zero)
            {
                if (providedSample != IntPtr.Zero) Marshal.Release(providedSample);
                if (providedBuffer != IntPtr.Zero) Marshal.Release(providedBuffer);
                break;
            }

            try
            {
                var sample = new MfSample(output.Sample);
                uint count = sample.BufferCount;
                if (count == 0) continue;
                IntPtr bufferPointer = sample.BufferAt(0);
                if (bufferPointer == IntPtr.Zero) continue;
                try
                {
                    var buffer = (IMFMediaBuffer)Marshal.GetObjectForIUnknown(bufferPointer);
                    try
                    {
                        buffer.Lock(out IntPtr pointer, out _, out uint current);
                        byte[] data = new byte[current];
                        Marshal.Copy(pointer, data, 0, (int)current);
                        buffer.Unlock();
                        // O atributo de clean point é opcional: a detecção pelo
                        // NAL IDR garante a marcação de keyframe mesmo sem ele.
                        bool keyframe = sample.IsKeyframe || ContainsIdr(data);
                        result.Add(new EncodedAccessUnit(data, keyframe, sample.SampleTime / 10_000));
                    }
                    finally { Marshal.ReleaseComObject(buffer); }
                }
                finally { Marshal.Release(bufferPointer); }
            }
            finally
            {
                if (output.Sample != IntPtr.Zero) Marshal.Release(output.Sample);
                if (providedBuffer != IntPtr.Zero) Marshal.Release(providedBuffer);
            }
        }
        return result;
    }

    /// <summary>
    /// Detecta um NAL IDR (tipo 5) num fluxo Annex-B. Sem isso, um MFT que
    /// não preenche o atributo de clean point nunca marcaria keyframe e o
    /// cliente descartaria todos os quadros.
    /// </summary>
    internal static bool ContainsIdr(ReadOnlySpan<byte> annexB)
    {
        int index = 0;
        while (index + 3 < annexB.Length)
        {
            // Procura o próximo start code (00 00 01 ou 00 00 00 01).
            if (annexB[index] == 0 && annexB[index + 1] == 0 && annexB[index + 2] == 1)
            {
                int payload = index + 3;
                if (payload < annexB.Length && (annexB[payload] & 0x1F) == 5) return true;
                index = payload;
                continue;
            }
            if (annexB[index] == 0 && annexB[index + 1] == 0 && annexB[index + 2] == 0
                && index + 3 < annexB.Length && annexB[index + 3] == 1)
            {
                int payload = index + 4;
                if (payload < annexB.Length && (annexB[payload] & 0x1F) == 5) return true;
                index = payload;
                continue;
            }
            index++;
        }
        return false;
    }

    private void NegotiateOutputType()
    {        for (uint index = 0; ; index++)
        {
            int hr = transform!.GetOutputAvailableType(0, index, out IMFMediaType type);
            if (hr < 0) throw new InvalidOperationException("Encoder sem tipo de saída H.264.");
            try
            {
                bool isH264 = type.GetGUID(ref MfGuids.MF_MT_SUBTYPE, out Guid subtype) >= 0 && subtype == MfGuids.MFVideoFormat_H264;
                if (!isH264) continue;
                int set = transform.SetOutputType(0, type, 0);
                if (set < 0) throw new InvalidOperationException("SetOutputType falhou: 0x" + set.ToString("X8"));
                return;
            }
            finally { Marshal.ReleaseComObject(type); }
        }
    }

    private static void Configure(IMFTransform transform, int width, int height, int fps, int bitrate)
    {
        if (transform.GetAttributes(out IMFAttributes attributes) >= 0 && attributes is not null)
        {
            try { attributes.SetUINT32(ref MfGuids.MF_LOW_LATENCY, 1); }
            finally { Marshal.ReleaseComObject(attributes); }
        }

        // Encoders de H.264 costumam aceitar o tipo de saída antes da entrada.
        if (!TrySetOutputType(transform, width, height, fps, bitrate))
        {
            throw new InvalidOperationException("Encoder recusou o tipo de saída H.264. " + LastDiagnostic);
        }

        if (!TrySetInputType(transform, width, height, fps))
        {
            throw new InvalidOperationException("Encoder recusou entrada NV12. " + LastDiagnostic);
        }

        if (transform is ICodecAPI codecApi)
        {
            SetVariant(codecApi, ref CodecApiGuids.AVEncCommonRateControlMode, 19 /* VT_UI4 */, 0); // CBR
            SetVariant(codecApi, ref CodecApiGuids.AVEncCommonMeanBitRate, 19, (uint)bitrate);
            SetVariant(codecApi, ref CodecApiGuids.AVEncCommonQualityVsSpeed, 19, 50);
            SetVariant(codecApi, ref CodecApiGuids.AVEncMPVDefaultBPictureCount, 19, 0);
            SetVariant(codecApi, ref CodecApiGuids.AVEncCommonLowLatency, 11 /* VT_BOOL */, 1);
            SetVariant(codecApi, ref CodecApiGuids.CODECAPI_AVLowLatencyMode, 11 /* VT_BOOL */, 1);
            SetVariant(codecApi, ref CodecApiGuids.AVEncMPVGOPSize, 19 /* VT_UI4 */, 30);
        }
    }

    private static bool TrySetOutputType(IMFTransform transform, int width, int height, int fps, int bitrate)
    {
        for (uint index = 0; ; index++)
        {
            int hr = transform.GetOutputAvailableType(0, index, out IMFMediaType available);
            if (hr < 0) break;
            try
            {
                bool h264 = available.GetGUID(ref MfGuids.MF_MT_SUBTYPE, out Guid subtype) >= 0 && subtype == MfGuids.MFVideoFormat_H264;
                if (!h264) continue;

                available.SetUINT32(ref MfGuids.MF_MT_AVG_BITRATE, (uint)bitrate);
                MFSetAttributeSize(available, ref MfGuids.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
                MFSetAttributeRatio(available, ref MfGuids.MF_MT_FRAME_RATE, (uint)fps, 1);
                int set = transform.SetOutputType(0, available, 0);
                if (set >= 0)
                {
                    return true;
                }
                lastDiagnostic += $" Saída[{index}] recusada: 0x{set:X8}.";
            }
            finally { Marshal.ReleaseComObject(available); }
        }

        // Nenhum tipo disponível: monta do zero.
        int created = MFCreateMediaType(out IMFMediaType outputType);
        if (created < 0) return false;
        try
        {
            outputType.SetGUID(ref MfGuids.MF_MT_MAJOR_TYPE, ref MfGuids.MFMediaType_Video);
            outputType.SetGUID(ref MfGuids.MF_MT_SUBTYPE, ref MfGuids.MFVideoFormat_H264);
            outputType.SetUINT32(ref MfGuids.MF_MT_AVG_BITRATE, (uint)bitrate);
            outputType.SetUINT32(ref MfGuids.MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            MFSetAttributeSize(outputType, ref MfGuids.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            MFSetAttributeRatio(outputType, ref MfGuids.MF_MT_FRAME_RATE, (uint)fps, 1);
            MFSetAttributeRatio(outputType, ref MfGuids.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            int set = transform.SetOutputType(0, outputType, 0);
            if (set >= 0)
            {
                return true;
            }
            lastDiagnostic += $" Saída montada recusada: 0x{set:X8}.";
            return false;
        }
        finally { Marshal.ReleaseComObject(outputType); }
    }

    private static bool TrySetInputType(IMFTransform transform, int width, int height, int fps)
    {
        for (uint index = 0; ; index++)
        {
            int hr = transform.GetInputAvailableType(0, index, out IMFMediaType available);
            if (hr < 0) break;
            try
            {
                bool nv12 = available.GetGUID(ref MfGuids.MF_MT_SUBTYPE, out Guid subtype) >= 0 && subtype == MfGuids.MFVideoFormat_NV12;
                if (!nv12) continue;

                MFSetAttributeSize(available, ref MfGuids.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
                MFSetAttributeRatio(available, ref MfGuids.MF_MT_FRAME_RATE, (uint)fps, 1);
                available.SetUINT32(ref MfGuids.MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
                int set = transform.SetInputType(0, available, 0);
                if (set >= 0)
                {
                    return true;
                }
                lastDiagnostic += $" Entrada[{index}] recusada: 0x{set:X8}.";
            }
            finally { Marshal.ReleaseComObject(available); }
        }

        int created = MFCreateMediaType(out IMFMediaType inputType);
        if (created < 0) return false;
        try
        {
            inputType.SetGUID(ref MfGuids.MF_MT_MAJOR_TYPE, ref MfGuids.MFMediaType_Video);
            inputType.SetGUID(ref MfGuids.MF_MT_SUBTYPE, ref MfGuids.MFVideoFormat_NV12);
            inputType.SetUINT32(ref MfGuids.MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            MFSetAttributeSize(inputType, ref MfGuids.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            MFSetAttributeRatio(inputType, ref MfGuids.MF_MT_FRAME_RATE, (uint)fps, 1);
            MFSetAttributeRatio(inputType, ref MfGuids.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            int set = transform.SetInputType(0, inputType, 0);
            if (set >= 0) return true;
            lastDiagnostic += $" Entrada montada recusada: 0x{set:X8}.";
            return false;
        }
        finally { Marshal.ReleaseComObject(inputType); }
    }

    private static void SetVariant(ICodecAPI api, ref Guid guid, ushort variantType, uint value)
    {
        IntPtr pointer = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.Copy(new byte[24], 0, pointer, 24);
            Marshal.WriteInt16(pointer, 0, unchecked((short)variantType));
            if (variantType == 11) Marshal.WriteInt16(pointer, 8, -1); // VARIANT_TRUE
            else Marshal.WriteInt32(pointer, 8, unchecked((int)value));
            api.SetValue(ref guid, pointer);
        }
        catch (Exception) { /* melhor esforço */ }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static (IMFTransform Transform, string Name, bool Hardware) CreateTransform(int width, int height, int fps, int bitrate)
    {
        lastDiagnostic = "";
        foreach (uint flags in new[] { MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER, MFT_ENUM_FLAG_SYNCMFT })
        {
            var outputType = new MFTRegisterTypeInfo { MajorType = MfGuids.MFMediaType_Video, SubType = MfGuids.MFVideoFormat_H264 };
            int hr = MFTEnumEx(MfGuids.MFT_CATEGORY_VIDEO_ENCODER, flags, IntPtr.Zero, ref outputType, out IntPtr activates, out uint count);
            if (hr < 0 || count == 0) { lastDiagnostic += $" Enumerar 0x{flags:X}: hr=0x{hr:X8} count={count}."; if (activates != IntPtr.Zero) Marshal.FreeCoTaskMem(activates); continue; }
            try
            {
                for (uint index = 0; index < count; index++)
                {
                    IntPtr pointer = Marshal.ReadIntPtr(activates, (int)index * IntPtr.Size);
                    if (pointer == IntPtr.Zero) continue;
                    var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pointer);
                    try
                    {
                        string name = ReadName(activate);
                        var iid = typeof(IMFTransform).GUID;
                        int activated = activate.ActivateObject(ref iid, out IntPtr transformPointer);
                        if (activated < 0 || transformPointer == IntPtr.Zero) continue;
                        var transform = (IMFTransform)Marshal.GetObjectForIUnknown(transformPointer);
                        Marshal.Release(transformPointer);
                        if (TryConfigure(transform, width, height, fps, bitrate, out string error))
                        {
                            bool hardware = (flags & MFT_ENUM_FLAG_HARDWARE) != 0;
                            return (transform, name, hardware);
                        }
                        Marshal.ReleaseComObject(transform);
                    }
                    finally { Marshal.ReleaseComObject(activate); }
                    _ = 0;
                }
            }
            finally { Marshal.FreeCoTaskMem(activates); }
        }

        throw new InvalidOperationException("Nenhum encoder H.264 disponível.");
    }

    /// <summary>Lista os encoders H.264 registrados (hardware e software).</summary>
    public static List<string> Describe()
    {
        var lines = new List<string>();
        foreach (var (flags, label) in new[] {
            (MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER, "hardware"),
            (MFT_ENUM_FLAG_SYNCMFT, "software") })
        {
            var outputType = new MFTRegisterTypeInfo { MajorType = MfGuids.MFMediaType_Video, SubType = MfGuids.MFVideoFormat_H264 };
            int hr = MFTEnumEx(MfGuids.MFT_CATEGORY_VIDEO_ENCODER, flags, IntPtr.Zero, ref outputType, out IntPtr activates, out uint count);
            lines.Add($"enum {label}: hr=0x{hr:X8} count={count}");
            if (hr < 0 || activates == IntPtr.Zero) { if (activates != IntPtr.Zero) Marshal.FreeCoTaskMem(activates); continue; }
            try
            {
                for (uint index = 0; index < count; index++)
                {
                    IntPtr pointer = Marshal.ReadIntPtr(activates, (int)index * IntPtr.Size);
                    if (pointer == IntPtr.Zero) continue;
                    var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pointer);
                    try
                    {
                        var attributes = (IMFAttributes)activate;
                        bool async = attributes.GetUINT32(ref MfGuids.MF_TRANSFORM_ASYNC, out uint asyncValue) >= 0 && asyncValue != 0;
                        lines.Add($"{label}[{index}] \"{ReadName(activate)}\" async={async}");
                    }
                    finally { Marshal.ReleaseComObject(activate); }
                }
            }
            finally { Marshal.FreeCoTaskMem(activates); }
        }
        return lines;
    }

    /// <summary>Testa se o encoder aceita NV12 na resolução pedida.</summary>
    private static bool TryConfigure(IMFTransform transform, int width, int height, int fps, int bitrate, out string error)
    {
        try
        {
            Configure(transform, width, height, fps, bitrate);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            lastDiagnostic += " [" + ex.Message + "]";
            return false;
        }
    }

    private static string ReadName(IMFActivate activate)
    {
        var attributes = (IMFAttributes)activate;
        if (attributes.GetAllocatedString(ref MfGuids.MFT_FRIENDLY_NAME, out IntPtr pointer, out _) >= 0 && pointer != IntPtr.Zero)
        {
            try
            {
                string? name = Marshal.PtrToStringUni(pointer);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }
        return "encoder H.264";
    }

    private static IMFMediaBuffer CreateBuffer(int size)
    {
        int hr = MFCreateMemoryBuffer((uint)size, out IMFMediaBuffer buffer);
        if (hr < 0) throw new InvalidOperationException("MFCreateMemoryBuffer falhou: 0x" + hr.ToString("X8"));
        return buffer;
    }

    private static IntPtr CreateSample()
    {
        int hr = MFCreateSample(out IntPtr sample);
        if (hr < 0) throw new InvalidOperationException("MFCreateSample falhou: 0x" + hr.ToString("X8"));
        return sample;
    }

    private static int Align16(int value)
    {
        int aligned = (value + 15) & ~15;
        return aligned < 16 ? 16 : aligned;
    }

    /// <summary>BGRA → NV12 (I420 com UV intercalado) na CPU.</summary>
    internal static byte[] BgraToNv12(
        ReadOnlySpan<byte> bgra,
        int stride,
        int sourceWidth,
        int sourceHeight,
        int dstWidth,
        int dstHeight)
    {
        if (stride < sourceWidth * 4) throw new ArgumentException("Stride inválido.", nameof(stride));
        if (dstWidth < sourceWidth || dstHeight < sourceHeight)
        {
            throw new ArgumentException("Dimensões de destino menores que a origem.", nameof(dstWidth));
        }

        int ySize = dstWidth * dstHeight;
        byte[] nv12 = new byte[ySize + ySize / 2];

        for (int row = 0; row < dstHeight; row++)
        {
            int sourceRow = Math.Min(row, sourceHeight - 1);
            ReadOnlySpan<byte> source = bgra.Slice(sourceRow * stride, sourceWidth * 4);
            int yOffset = row * dstWidth;
            for (int col = 0; col < dstWidth; col++)
            {
                int sourceCol = Math.Min(col, sourceWidth - 1);
                int at = sourceCol * 4;
                int b = source[at], g = source[at + 1], r = source[at + 2];
                // BT.709 limitado, coerente com o que o VideoToolbox espera do NV12 vídeo-range.
                int y = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                nv12[yOffset + col] = (byte)Math.Clamp(y, 16, 235);
            }
        }

        for (int row = 0; row < dstHeight; row += 2)
        {
            int uvRow = row / 2;
            int uvOffset = ySize + uvRow * dstWidth;
            for (int col = 0; col < dstWidth; col += 2)
            {
                int sumU = 0, sumV = 0;
                for (int dy = 0; dy < 2; dy++)
                {
                    int sampleRow = Math.Min(row + dy, sourceHeight - 1);
                    ReadOnlySpan<byte> source = bgra.Slice(sampleRow * stride, sourceWidth * 4);
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int sampleCol = Math.Min(col + dx, sourceWidth - 1);
                        int at = sampleCol * 4;
                        int b = source[at], g = source[at + 1], r = source[at + 2];
                        sumU += ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                        sumV += ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                    }
                }
                nv12[uvOffset + col] = (byte)Math.Clamp(sumU / 4, 16, 240);
                nv12[uvOffset + col + 1] = (byte)Math.Clamp(sumV / 4, 16, 240);
            }
        }

        return nv12;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (transform is not null)
                {
                    transform.ProcessMessage(MFTMessageType.NotifyEndOfStream, IntPtr.Zero);
                    transform.ProcessMessage(MFTMessageType.NotifyEndStreaming, IntPtr.Zero);
                    Marshal.ReleaseComObject(transform);
                    transform = null;
                }
            }
            catch (Exception) { }
            try { MFShutdown(); } catch (Exception) { }
        }
    }

    // ---- interop mínimo de Media Foundation ----

    private enum MFTMessageType : uint
    {
        CommandFlush = 0x00000000,
        CommandDrain = 0x00000001,
        SetD3DManager = 0x00000002,
        DropSamples = 0x00000003,
        NotifyBeginStreaming = 0x10000000,
        NotifyEndStreaming = 0x10000001,
        NotifyEndOfStream = 0x10000002,
        NotifyStartOfStream = 0x10000003,
        NotifyReleaseResources = 0x10000004,
        NotifyReacquireResources = 0x10000005,
        Marker = 0x10000006,
        CommandSetOutputStreamProvider = 0x10000007,
        NotifySampleReady = 0x10000008,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFTOutputDataBuffer
    {
        public uint StreamId;
        public IntPtr Sample;
        public uint Status;
        public IntPtr Events;
    }

    [ComImport, Guid("BF94C121-5B05-4E6F-8000-BA598961414D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFTransform
    {
        [PreserveSig] int GetStreamLimits(out uint inputMin, out uint inputMax, out uint outputMin, out uint outputMax);
        [PreserveSig] int GetStreamCount(out uint inputCount, out uint outputCount);
        [PreserveSig] int GetStreamIDs(uint inputIdArraySize, [Out] uint[] inputIds, uint outputIdArraySize, [Out] uint[] outputIds);
        [PreserveSig] int GetInputStreamInfo(uint inputStreamId, out MFTInputStreamInfo info);
        [PreserveSig] int GetOutputStreamInfo(uint outputStreamId, out MFTOutputStreamInfo info);
        [PreserveSig] int GetAttributes(out IMFAttributes attributes);
        [PreserveSig] int GetInputStreamAttributes(uint inputStreamId, out IMFAttributes attributes);
        [PreserveSig] int GetOutputStreamAttributes(uint outputStreamId, out IMFAttributes attributes);
        [PreserveSig] int DeleteInputStream(uint streamId);
        [PreserveSig] int AddInputStreams(uint streamCount, [In] uint[] streamIds);
        [PreserveSig] int GetInputAvailableType(uint inputStreamId, uint typeIndex, out IMFMediaType type);
        [PreserveSig] int GetOutputAvailableType(uint outputStreamId, uint typeIndex, out IMFMediaType type);
        [PreserveSig] int SetInputType(uint inputStreamId, IMFMediaType type, uint flags);
        [PreserveSig] int SetOutputType(uint outputStreamId, IMFMediaType type, uint flags);
        [PreserveSig] int GetInputCurrentType(uint inputStreamId, out IMFMediaType type);
        [PreserveSig] int GetOutputCurrentType(uint outputStreamId, out IMFMediaType type);
        [PreserveSig] int GetInputStatus(uint inputStreamId, out uint flags);
        [PreserveSig] int GetOutputStatus(out uint flags);
        [PreserveSig] int SetOutputBounds(long hnsLowerBound, long hnsUpperBound);
        [PreserveSig] int ProcessEvent(uint inputStreamId, IntPtr mediaEvent);
        [PreserveSig] int ProcessMessage(MFTMessageType message, IntPtr data);
        [PreserveSig] int ProcessInput(uint inputStreamId, IntPtr sample, uint flags);
        [PreserveSig] int ProcessOutput(uint flags, uint outputBufferCount, ref MFTOutputDataBuffer outputData, out uint status);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFTInputStreamInfo { public long MaxLatency; public uint Flags; public uint Size; public uint MaxLookahead; public uint Alignment; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFTOutputStreamInfo { public uint Flags; public uint Size; public uint Alignment; }

    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [Out] char[] value, uint bufferSize, out uint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, [Out] byte[] buffer, uint bufferSize, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, string value);
        [PreserveSig] int SetBlob(ref Guid key, byte[] buffer, uint bufferSize);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint items);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
    }

    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType // herda IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [Out] char[] value, uint bufferSize, out uint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, [Out] byte[] buffer, uint bufferSize, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, string value);
        [PreserveSig] int SetBlob(ref Guid key, byte[] buffer, uint bufferSize);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint items);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
        [PreserveSig] int GetMajorType(out Guid majorType);
        [PreserveSig] int IsCompressedFormat(out int compressed);
        [PreserveSig] int IsEqual(IMFMediaType theirs, out uint flags);
        [PreserveSig] int GetRepresentation(ref Guid representation, out IntPtr data);
        [PreserveSig] int FreeRepresentation(ref Guid representation, IntPtr data);
    }

    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint length);
        [PreserveSig] int SetCurrentLength(uint length);
        [PreserveSig] int GetMaxLength(out uint length);
    }

    /// <summary>
    /// Acesso ao IMFSample pela vtable. O objeto devolvido por MFCreateSample
    /// nesta build do Windows não aceita QI pelo IID clássico de IMFSample, mas
    /// a ordem dos métodos é a documentada (IMFAttributes ocupa 33 slots).
    /// </summary>
    internal readonly unsafe struct MfSample
    {
        private readonly IntPtr self;
        public MfSample(IntPtr pointer) { self = pointer; }

        private IntPtr Slot(int index) => *(*(IntPtr**)self + index);

        private int Call(int index, IntPtr argument)
        {
            if (self == IntPtr.Zero) return unchecked((int)0x80004003);
            var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(index);
            return method(self, argument);
        }

        private int CallLong(int index, long value)
        {
            if (self == IntPtr.Zero) return unchecked((int)0x80004003);
            var method = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(index);
            return method(self, value);
        }

        public int AddBuffer(IntPtr buffer) => Call(42, buffer);
        public int SetSampleTime(long value) => CallLong(36, value);
        public int SetSampleDuration(long value) => CallLong(38, value);
        public long SampleTime { get { if (self == IntPtr.Zero) return 0; long value = 0; var method = (delegate* unmanaged[Stdcall]<IntPtr, long*, int>)Slot(35); method(self, &value); return value; } }
        public uint BufferCount { get { if (self == IntPtr.Zero) return 0; uint value = 0; var method = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(39); method(self, &value); return value; } }
        public IntPtr BufferAt(uint index) { if (self == IntPtr.Zero) return IntPtr.Zero; IntPtr value = IntPtr.Zero; var method = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(40); method(self, index, &value); return value; }
        public bool IsKeyframe { get { if (self == IntPtr.Zero) return false; Guid key = MfGuids.MFSampleExtension_CleanPoint; uint value = 0; var method = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Slot(7); method(self, &key, &value); return value != 0; } }
    }

    /// <summary>
    /// ICodecAPI na ordem documentada da vtable (icodecapi.h):
    /// IsSupported, IsModifiable, GetParameterRange, GetParameterValues,
    /// GetDefaultValue, GetValue, SetValue. Omitir GetDefaultValue faz SetValue
    /// chamar GetValue, impedindo a configuração correta das propriedades.
    /// </summary>
    [ComImport, Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecAPI
    {
        [PreserveSig] int IsSupported(ref Guid api);
        [PreserveSig] int IsModifiable(ref Guid api);
        [PreserveSig] int GetParameterRange(ref Guid api, IntPtr minValue, IntPtr maxValue, IntPtr steppingDelta);
        [PreserveSig] int GetParameterValues(ref Guid api, ref IntPtr values, ref uint valuesCount);
        [PreserveSig] int GetDefaultValue(ref Guid api, IntPtr value);
        [PreserveSig] int GetValue(ref Guid api, IntPtr value);
        [PreserveSig] int SetValue(ref Guid api, IntPtr value);
    }

    [ComImport, Guid("7FEE9E9A-4A89-47A6-899C-B6A53A70FB67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFActivate // herda IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [Out] char[] value, uint bufferSize, out uint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, [Out] byte[] buffer, uint bufferSize, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, string value);
        [PreserveSig] int SetBlob(ref Guid key, byte[] buffer, uint bufferSize);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint items);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
        [PreserveSig] int ActivateObject(ref Guid iid, out IntPtr obj);
        [PreserveSig] int ShutdownObject();
        [PreserveSig] int DetachObject();
    }

    private static class MfGuids
    {
        public static Guid MFT_CATEGORY_VIDEO_ENCODER = new("F79EAC7D-E545-4387-BDEE-D647D7BDE42A");
        public static Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
        public static Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
        public static Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
        public static Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
        public static Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
        public static Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
        public static Guid MFT_FRIENDLY_NAME = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
        public static Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
        public static Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
        public static Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    }

    private static class CodecApiGuids
    {
        public static Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
        public static Guid AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
        public static Guid AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
        public static Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
        public static Guid AVEncCommonLowLatency = new("9d3ecd55-89e8-490a-970a-0c9548d5a56e");
        public static Guid CODECAPI_AVLowLatencyMode = new("9c387036-1440-4168-9f31-aab98966fd3a");
        public static Guid AVEncMPVGOPSize = new("95f31495-6dd9-4f6e-be15-309f69411c86");
        public static Guid AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    }

    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(int version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);
    /// <summary>MF_MT_FRAME_SIZE é um UINT64 empacotado (largura &lt;&lt; 32 | altura).</summary>
    private static void MFSetAttributeSize(IMFMediaType attributes, ref Guid key, uint width, uint height) =>
        attributes.SetUINT64(ref key, ((ulong)width << 32) | height);

    /// <summary>MF_MT_FRAME_RATE e assemelhados: numerador &lt;&lt; 32 | denominador.</summary>
    private static void MFSetAttributeRatio(IMFMediaType attributes, ref Guid key, uint numerator, uint denominator) =>
        attributes.SetUINT64(ref key, ((ulong)numerator << 32) | denominator);
    [StructLayout(LayoutKind.Sequential)]
    private struct MFTRegisterTypeInfo { public Guid MajorType; public Guid SubType; }

    // MFTEnumEx(GUID, UINT32, const MFT_REGISTER_TYPE_INFO* pInputType,
    //           const MFT_REGISTER_TYPE_INFO* pOutputType, IMFActivate***, UINT32*)
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFTEnumEx(Guid category, uint flags, IntPtr inputType, ref MFTRegisterTypeInfo outputType, out IntPtr activates, out uint count);
}

/// <summary>Access unit H.264 (Annex-B) pronto para o protocolo.</summary>
internal sealed record EncodedAccessUnit(byte[] Data, bool Keyframe, long PtsMs);
