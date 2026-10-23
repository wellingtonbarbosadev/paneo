using System.IO;
using System.Text;

namespace WindowProbe.Remote;

/// <summary>
/// Recebe arquivos enviados pelo Mac em blocos e grava em disco.
///
/// O nome vem do cliente e nunca é usado como caminho: componentes de diretório
/// (Windows e POSIX) são descartados, caracteres inválidos viram <c>_</c> e
/// nomes reservados do Windows recebem um prefixo. Assim um nome como
/// <c>../../Windows/System32/evil.exe</c> vira apenas <c>evil.exe</c> dentro da
/// pasta de destino.
///
/// A pasta padrão é <c>Downloads</c> do usuário; <c>customDir</c> existe para o
/// chamador (host ou testes) escolher outro destino. O cliente remoto não
/// informa diretório: isso evitaria escrita arbitrária no PC.
/// </summary>
internal sealed class FileReceiver : IDisposable
{
    /// <summary>
    /// Tamanho máximo de um bloco. O campo <c>data</c> vai em Base64 dentro de
    /// uma mensagem de controle, limitada a 8 KiB no cliente -> host.
    /// </summary>
    internal const int MaxChunkBytes = 4 * 1024;

    /// <summary>Limite do nome higienizado, sem contar o sufixo de unicidade.</summary>
    internal const int MaxNameLength = 128;

    /// <summary>Nome usado quando o cliente manda vazio ou só caracteres inválidos.</summary>
    internal const string FallbackName = "arquivo";

    /// <summary>Caracteres inválidos no Windows, aplicados também fora dele.</summary>
    private static readonly char[] WindowsInvalidChars =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    private readonly Func<string> downloadDirectory;
    private readonly object gate = new();

    /// <summary>Transferências abertas, indexadas por pasta + nome higienizado.</summary>
    private readonly Dictionary<string, Transfer> transfers = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Transfer(string Path, FileStream Stream);

    /// <summary>
    /// <paramref name="downloadDirectory"/> permite injetar a pasta em testes;
    /// em produção o padrão é <c>Downloads</c> do usuário.
    /// </summary>
    public FileReceiver(Func<string>? downloadDirectory = null)
    {
        this.downloadDirectory = downloadDirectory ?? DefaultDirectory;
    }

    /// <summary>Pasta usada quando o chamador não passa <c>customDir</c>.</summary>
    public static string DefaultDirectory()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) home = Path.GetTempPath();
        return Path.Combine(home, "Downloads");
    }

    /// <summary>
    /// Grava um bloco do arquivo <paramref name="name"/> e devolve o caminho
    /// completo salvo. <paramref name="isFirst"/> começa uma transferência nova
    /// (nome único, sem sobrescrever o que já existe); <paramref name="isLast"/>
    /// fecha o arquivo. Blocos intermediários são acrescentados na ordem em que
    /// chegam.
    ///
    /// Um bloco recebido sem <c>isFirst</c> abre a transferência de qualquer
    /// forma, para que uma mensagem perdida não impeça o restante do arquivo.
    /// </summary>
    public string SaveChunk(string? name, byte[] data, bool isFirst, bool isLast, string? customDir = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > MaxChunkBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "Bloco de arquivo acima do limite.");
        }

        string directory = ResolveDirectory(customDir);
        string safeName = SanitizeName(name);

        lock (gate)
        {
            Directory.CreateDirectory(directory);
            string key = Path.Combine(directory, safeName);

            if (isFirst)
            {
                // fileStart já abriu a transferência: o primeiro bloco do
                // cliente pode repetir isFirst e deve continuar nesse arquivo,
                // em vez de criar outro com sufixo " (1)". Se já há bytes, é
                // um recomeço de verdade e o parcial é descartado.
                if (transfers.TryGetValue(key, out Transfer? open) && open.Stream.Length > 0)
                {
                    CloseTransfer(key);
                }
            }

            if (!transfers.TryGetValue(key, out Transfer? transfer))
            {
                string fullPath = UniquePath(directory, safeName);
                var stream = new FileStream(
                    fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                transfer = new Transfer(fullPath, stream);
                transfers[key] = transfer;
            }

            try
            {
                if (data.Length > 0) transfer.Stream.Write(data, 0, data.Length);
                transfer.Stream.Flush();
            }
            catch
            {
                // Disco cheio/removido: descarta o estado parcial para que o
                // próximo isFirst comece limpo.
                CloseTransfer(key);
                throw;
            }

            if (isLast) CloseTransfer(key);
            return transfer.Path;
        }
    }

    /// <summary>
    /// Higieniza o nome recebido do Mac: remove diretórios, caracteres
    /// inválidos, controles, espaços/pontos nas pontas e nomes reservados.
    /// Nunca devolve vazio.
    /// </summary>
    public static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return FallbackName;

        // O separador do Windows não é separador no macOS, então é trocado
        // antes de qualquer Path.GetFileName.
        string value = name.Replace('\\', '/');
        value = value[(value.LastIndexOf('/') + 1)..];
        value = Path.GetFileName(value);

        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (character is < ' ' or '\u007f') continue;
            if (Array.IndexOf(WindowsInvalidChars, character) >= 0 ||
                Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0)
            {
                builder.Append('_');
                continue;
            }
            builder.Append(character);
        }

        value = builder.ToString().Trim().TrimEnd('.', ' ');
        if (value.Length == 0) return FallbackName;

        // Nomes de dispositivo do Windows são reservados mesmo com extensão.
        string baseName = Path.GetFileNameWithoutExtension(value);
        if (ReservedNames.Contains(baseName, StringComparer.OrdinalIgnoreCase))
        {
            value = "_" + value;
        }

        if (value.Length > MaxNameLength)
        {
            string extension = Path.GetExtension(value);
            if (extension.Length > 16) extension = "";
            int keep = Math.Max(1, MaxNameLength - extension.Length);
            value = value[..keep] + extension;
        }

        return value.Length == 0 ? FallbackName : value;
    }

    private string ResolveDirectory(string? customDir) =>
        string.IsNullOrWhiteSpace(customDir)
            ? Path.GetFullPath(downloadDirectory())
            : Path.GetFullPath(customDir);

    /// <summary>Primeiro caminho livre: "nome (1).ext", "nome (2).ext"...</summary>
    private static string UniquePath(string directory, string safeName)
    {
        string candidate = Path.Combine(directory, safeName);
        if (!File.Exists(candidate)) return candidate;

        string extension = Path.GetExtension(safeName);
        string stem = Path.GetFileNameWithoutExtension(safeName);
        for (int index = 1; index < 1000; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    private void CloseTransfer(string key)
    {
        if (!transfers.Remove(key, out Transfer? transfer)) return;
        try { transfer.Stream.Dispose(); } catch (Exception) { /* já indisponível */ }
    }

    /// <summary>Fecha as transferências em andamento (o arquivo parcial fica no disco).</summary>
    public void Dispose()
    {
        lock (gate)
        {
            foreach (string key in transfers.Keys.ToArray()) CloseTransfer(key);
        }
    }
}
