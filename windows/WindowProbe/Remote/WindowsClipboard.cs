using System.Runtime.InteropServices;
using System.Text;

namespace WindowProbe.Remote;

/// <summary>
/// Acesso isolado ao clipboard de texto do Windows (CF_UNICODETEXT).
///
/// O clipboard é um recurso global e exclusivo: enquanto uma janela o mantém
/// aberto, nenhuma outra leitura ou escrita é aceita. Por isso todas as
/// operações passam por um único lock e <c>OpenClipboard</c> é repetido algumas
/// vezes antes de desistir. As chamadas Win32 ficam confinadas neste arquivo;
/// o servidor de transporte só conhece <see cref="SequenceNumber"/>,
/// <see cref="TryGetText"/> e <see cref="SetText"/>.
///
/// Fora do Windows (os testes de transporte também rodam no Mac) as operações
/// viram no-op, sem carregar user32.dll.
/// </summary>
internal sealed class WindowsClipboard
{
    /// <summary>CF_UNICODETEXT.</summary>
    internal const int CfUnicodeText = 13;

    /// <summary>Limite do campo <c>text</c> da mensagem <c>clipboard</c>.</summary>
    internal const int MaxTextBytes = 64 * 1024;

    /// <summary>GMEM_MOVEABLE: memória que o clipboard assume por handle.</summary>
    private const uint GmemMoveable = 0x0002;

    /// <summary>Tentativas de abrir o clipboard antes de desistir.</summary>
    private const int OpenAttempts = 10;
    private const int OpenRetryDelayMs = 20;

    /// <summary>Instância usada pelo host; o lock interno é compartilhado.</summary>
    public static WindowsClipboard Shared { get; } = new();

    // Um único lock serializa OpenClipboard/EmptyClipboard/SetClipboardData.
    // Sem ele duas sessões poderiam abrir o clipboard ao mesmo tempo e corromper
    // a troca de conteúdo.
    private static readonly object Gate = new();

    private readonly Func<uint> sequenceNumber;
    private readonly Func<(bool Success, string Text)> read;
    private readonly Func<string, bool> write;

    /// <summary>
    /// Os delegates existem para os testes de transporte exercitarem o fluxo
    /// sem depender de uma sessão interativa do Windows.
    /// </summary>
    public WindowsClipboard(
        Func<uint>? sequenceNumber = null,
        Func<(bool Success, string Text)>? read = null,
        Func<string, bool>? write = null)
    {
        this.sequenceNumber = sequenceNumber ?? ReadSequenceNumber;
        this.read = read ?? ReadText;
        this.write = write ?? WriteText;
    }

    /// <summary>
    /// Número de sequência do clipboard. Muda a cada alteração, inclusive as
    /// feitas por outro aplicativo, e permite detectar mudanças sem polling de
    /// conteúdo.
    /// </summary>
    public uint SequenceNumber
    {
        get
        {
            try { return sequenceNumber(); }
            catch (Exception) { return 0; }
        }
    }

    /// <summary>Lê o texto Unicode atual; devolve false quando não há texto.</summary>
    public bool TryGetText(out string text)
    {
        (bool success, string value) = read();
        text = success ? value : string.Empty;
        return success;
    }

    /// <summary>
    /// Substitui o clipboard por <paramref name="text"/>. O limite é medido em
    /// UTF-8, o mesmo do campo JSON; acima disso nada é alterado.
    /// </summary>
    public bool SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return write(text);
    }

    private static uint ReadSequenceNumber()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try { return GetClipboardSequenceNumber(); }
        catch (DllNotFoundException) { return 0; }
        catch (EntryPointNotFoundException) { return 0; }
    }

    private static (bool Success, string Text) ReadText()
    {
        if (!OperatingSystem.IsWindows()) return (false, string.Empty);

        lock (Gate)
        {
            if (!TryOpen()) return (false, string.Empty);
            try
            {
                IntPtr handle = GetClipboardData(CfUnicodeText);
                if (handle == IntPtr.Zero) return (false, string.Empty);

                IntPtr pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) return (false, string.Empty);
                try
                {
                    string? value = Marshal.PtrToStringUni(pointer);
                    return value is null ? (false, string.Empty) : (true, value);
                }
                finally
                {
                    // O handle pertence ao clipboard: GlobalUnlock, nunca GlobalFree.
                    _ = GlobalUnlock(handle);
                }
            }
            finally { CloseClipboard(); }
        }
    }

    private static bool WriteText(string text)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (Encoding.UTF8.GetByteCount(text) > MaxTextBytes) return false;

        // CF_UNICODETEXT é UTF-16 terminado em NUL.
        byte[] payload = Encoding.Unicode.GetBytes(text);

        lock (Gate)
        {
            if (!TryOpen()) return false;

            IntPtr memory = IntPtr.Zero;
            try
            {
                if (!EmptyClipboard()) return false;

                memory = GlobalAlloc(GmemMoveable, (nuint)(payload.Length + sizeof(char)));
                if (memory == IntPtr.Zero) return false;

                IntPtr target = GlobalLock(memory);
                if (target == IntPtr.Zero) return false;
                try
                {
                    Marshal.Copy(payload, 0, target, payload.Length);
                    Marshal.WriteInt16(target, payload.Length, 0);
                }
                finally { _ = GlobalUnlock(memory); }

                if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero) return false;

                // A propriedade da memória passou para o clipboard.
                memory = IntPtr.Zero;
                return true;
            }
            finally
            {
                if (memory != IntPtr.Zero) GlobalFree(memory);
                CloseClipboard();
            }
        }
    }

    /// <summary>
    /// Tenta abrir o clipboard; outro processo pode mantê-lo aberto por alguns
    /// milissegundos.
    /// </summary>
    private static bool TryOpen()
    {
        for (int attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(OpenRetryDelayMs);
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
