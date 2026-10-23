using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace WindowProbe.Models;

/// <summary>
/// Extrai o ícone do aplicativo dono de uma janela e devolve PNG em Base64
/// para entrar no payload <c>windows</c> do protocolo v2.
///
/// O caminho principal é o ícone associado ao executável
/// (<see cref="Icon.ExtractAssociatedIcon(string)"/>); quando o executável não
/// é acessível (processo elevado, pacote da Store) ou não tem ícone, cai para
/// o ícone que o próprio Windows mantém na janela (<c>WM_GETICON</c> e
/// <c>GetClassLongPtr</c>).
///
/// O cache é por nome de processo porque o ícone pertence ao aplicativo, não à
/// janela: a lista é reenumerada a cada atualização e a cada resolução de
/// janela, e sem cache cada uma dessas chamadas pagaria a extração de novo.
/// Processos sem ícone também são memorizados (sentinela vazia) para não
/// repetir o custo de um caminho que já falhou.
/// </summary>
internal static class WindowIconExtractor
{
    /// <summary>Lado do PNG entregue ao Mac (o card usa 38 pt).</summary>
    private const int IconSize = 32;

    /// <summary>Tamanho de contingência quando o PNG em 32 px fica grande demais.</summary>
    private const int SmallIconSize = 16;

    /// <summary>
    /// Limite por ícone. O controle host -> Mac é limitado a 64 KiB; a lista
    /// carrega vários ícones e precisa sobrar espaço para títulos e metadados.
    /// </summary>
    private const int MaxBase64Chars = 3072;

    /// <summary>Sentinela de "este processo não tem ícone extraível".</summary>
    private const string NoIcon = "";

    private const int WM_GETICON = 0x007F;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL2 = 2;
    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;

    private static readonly ConcurrentDictionary<string, string> cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Devolve o PNG do ícone em Base64, ou <c>null</c> quando não há ícone
    /// utilizável. Falhas são contidas: a listagem de janelas nunca deixa de
    /// acontecer por causa de um ícone.
    /// </summary>
    public static string? GetIconBase64(IntPtr window, uint processId, string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        if (cache.TryGetValue(processName, out string? cached))
        {
            return cached.Length == 0 ? null : cached;
        }

        string encoded = Extract(window, processId) ?? NoIcon;
        // Duas janelas do mesmo processo podem extrair ao mesmo tempo; ambas
        // gravam o mesmo valor, então a última escrita é inofensiva.
        cache[processName] = encoded;
        return encoded.Length == 0 ? null : encoded;
    }

    private static string? Extract(IntPtr window, uint processId) =>
        EncodeFromExecutable(processId) ?? EncodeFromWindow(window);

    /// <summary>Ícone gravado no .exe, via <see cref="Icon.ExtractAssociatedIcon(string)"/>.</summary>
    private static string? EncodeFromExecutable(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            string? modulePath = process.MainModule?.FileName;
            if (modulePath is null || modulePath.Length == 0 || !File.Exists(modulePath))
            {
                return null;
            }

            using Icon? associated = Icon.ExtractAssociatedIcon(modulePath);
            return associated is null ? null : EncodePng(associated);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Processo encerrado, elevado ou inacessível: tenta a janela.
            return null;
        }
    }

    /// <summary>Ícone mantido pela janela (WM_GETICON e, depois, a classe).</summary>
    private static string? EncodeFromWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return null;
        }

        foreach (int kind in new[] { ICON_BIG, ICON_SMALL2, ICON_SMALL })
        {
            string? encoded = EncodeFromHandle(SendMessage(window, WM_GETICON, (IntPtr)kind, IntPtr.Zero));
            if (encoded is not null) return encoded;
        }

        foreach (int index in new[] { GCLP_HICON, GCLP_HICONSM })
        {
            string? encoded = EncodeFromHandle(GetClassLongPtr(window, index));
            if (encoded is not null) return encoded;
        }

        return null;
    }

    private static string? EncodeFromHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            // FromHandle não assume a posse do HICON: o handle pertence à janela
            // (ou à classe) e não pode ser destruído por nós.
            using Icon icon = Icon.FromHandle(handle);
            return EncodePng(icon);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            return null;
        }
    }

    /// <summary>
    /// Converte o ícone para PNG em Base64, reduzindo para 16 px se o PNG de
    /// 32 px passar do limite. GDI+ pode recusar ícones exóticos: nesse caso o
    /// resultado é <c>null</c> e o Mac usa o card de fallback.
    /// </summary>
    private static string? EncodePng(Icon icon)
    {
        foreach (int size in new[] { IconSize, SmallIconSize })
        {
            try
            {
                string encoded = Convert.ToBase64String(RenderPng(icon, size));
                if (encoded.Length <= MaxBase64Chars)
                {
                    return encoded;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
            {
                return null;
            }
        }

        return null;
    }

    private static byte[] RenderPng(Icon icon, int size)
    {
        using var target = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;

            // new Icon(icon, w, h) escolhe o quadro mais próximo antes de pintar.
            using var scaled = new Icon(icon, size, size);
            using Bitmap source = scaled.ToBitmap();
            graphics.DrawImage(source, new Rectangle(0, 0, size, size));
        }

        using var stream = new MemoryStream();
        target.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static IntPtr GetClassLongPtr(IntPtr window, int index) =>
        IntPtr.Size == 8
            ? GetClassLongPtr64(window, index)
            : (IntPtr)GetClassLong32(window, index);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", ExactSpelling = true)]
    private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetClassLongW", ExactSpelling = true)]
    private static extern uint GetClassLong32(IntPtr hWnd, int nIndex);
}
