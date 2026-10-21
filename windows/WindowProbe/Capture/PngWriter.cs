using System.IO;
using WindowProbe.Interop;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace WindowProbe.Capture;

internal static class PngWriter
{
    /// <summary>
    /// Codifica o SoftwareBitmap em PNG na memória e grava no caminho escolhido.
    /// Não cria diretórios e não persiste nada além do arquivo pedido.
    /// </summary>
    public static async Task<long> SaveAsync(
        SoftwareBitmap bitmap,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new CaptureException($"Diretório de destino inexistente: {directory ?? "(vazio)"}");
        }

        byte[] bytes = await EncodeAsync(bitmap, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken).ConfigureAwait(false);

        return bytes.Length;
    }

    /// <summary>
    /// Codifica o SoftwareBitmap em PNG inteiramente na memória. O array
    /// retornado pertence ao chamador; nenhum arquivo é criado.
    /// </summary>
    public static async Task<byte[]> EncodeAsync(
        SoftwareBitmap bitmap,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var stream = new InMemoryRandomAccessStream();
        DataReader? reader = null;
        SoftwareBitmap? converted = null;

        try
        {
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(cancellationToken);

            SoftwareBitmap source = bitmap;
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                bitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
            {
                converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                source = converted;
            }

            encoder.SetSoftwareBitmap(source);
            await encoder.FlushAsync().AsTask(cancellationToken);

            stream.Seek(0);

            if (stream.Size == 0)
            {
                throw new CaptureException("O codificador PNG produziu um fluxo vazio.");
            }

            if (stream.Size > int.MaxValue)
            {
                throw new CaptureException("A imagem excede o limite de memória deste protótipo.");
            }

            uint size = (uint)stream.Size;
            byte[] bytes = new byte[size];

            reader = new DataReader(stream.GetInputStreamAt(0));
            uint loaded = await reader.LoadAsync(size).AsTask(cancellationToken);
            if (loaded != size)
            {
                throw new CaptureException($"Leitura incompleta do PNG: {loaded} de {size} bytes.");
            }

            reader.ReadBytes(bytes);

            cancellationToken.ThrowIfCancellationRequested();
            return bytes;
        }
        finally
        {
            WinRtInterop.CloseQuietly(converted);
            WinRtInterop.CloseQuietly(reader);
            WinRtInterop.CloseQuietly(stream);
        }
    }
}
