using System.Diagnostics;
using System.IO;

namespace WindowProbe.Remote;

/// <summary>
/// Aplicativo que o Mac pode pedir para abrir no Windows. O <paramref name="Icon"/>
/// é opcional: quando nulo, o cliente escolhe o próprio ícone/emoji.
/// </summary>
internal sealed record LauncherApp(string Id, string Name, string Command, string? Icon = null);

/// <summary>
/// Lista fechada de aplicativos seguros e lançamento por ShellExecute. Uma
/// mensagem do Mac nunca vira linha de comando arbitrária: só ids/comandos
/// desta lista são aceitos, e o executável é sempre o comando conhecido.
/// </summary>
internal static class AppLauncher
{
    private static readonly LauncherApp[] KnownApps =
    [
        new("notepad", "Bloco de notas", "notepad.exe"),
        new("calc", "Calculadora", "calc.exe"),
        new("explorer", "Explorer de arquivos", "explorer.exe"),
        new("mspaint", "Paint", "mspaint.exe"),
        new("powershell", "PowerShell", "powershell.exe"),
        new("cmd", "Prompt de comando", "cmd.exe"),
        new("edge", "Microsoft Edge", "msedge.exe"),
    ];

    /// <summary>Aplicativos publicados ao Mac na lista de janelas.</summary>
    public static IReadOnlyList<LauncherApp> GetKnownApps() => KnownApps;

    /// <summary>
    /// Abre um aplicativo da lista segura. Aceita o id ("notepad") ou o comando
    /// ("notepad.exe"). Devolve false para entrada vazia, desconhecida ou quando
    /// o Windows recusa a inicialização; nunca lança exceção.
    /// </summary>
    public static bool Launch(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        // Caminho/argumento nunca é aceito: só id ou nome de comando da lista.
        if (cmd.Contains('/') || cmd.Contains('\\')) return false;

        LauncherApp? app = Find(cmd.Trim());
        if (app is null) return false;

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = app.Command,
                UseShellExecute = true
            });
            return process is not null;
        }
        catch (Exception)
        {
            // Aplicativo ausente, política do Windows ou sessão sem desktop:
            // a recusa não pode encerrar a sessão remota.
            return false;
        }
    }

    private static LauncherApp? Find(string value)
    {
        string name = Path.GetFileNameWithoutExtension(value);
        foreach (LauncherApp app in KnownApps)
        {
            if (string.Equals(app.Id, value, StringComparison.OrdinalIgnoreCase)) return app;
            if (string.Equals(app.Command, value, StringComparison.OrdinalIgnoreCase)) return app;
            if (string.Equals(app.Id, name, StringComparison.OrdinalIgnoreCase)) return app;
        }
        return null;
    }
}
