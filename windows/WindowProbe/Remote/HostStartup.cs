using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace WindowProbe.Remote;

/// <summary>Preferências locais sem credenciais. O PIN continua somente no PairingStore.</summary>
internal sealed record HostSettings(int Port = 9443, bool AllowHover = true)
{
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteDesktop", "host-settings.json");

    internal static HostSettings Load(string? path = null)
    {
        path ??= SettingsPath;
        if (!File.Exists(path)) return new();
        HostSettings settings = JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Preferências vazias.");
        Validate(settings);
        return settings;
    }

    internal void Save(string? path = null)
    {
        Validate(this);
        path ??= SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static void Validate(HostSettings settings)
    {
        if (settings.Port is < 1 or > 65535)
            throw new InvalidDataException("Porta salva inválida.");
    }
}

/// <summary>Inicialização opt-in, apenas após o login da conta Windows atual.</summary>
internal static class HostStartup
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "Paneo";

    [SupportedOSPlatform("windows")]
    internal static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string command &&
            MatchesExecutable(command, CurrentExecutable());
    }

    [SupportedOSPlatform("windows")]
    internal static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using RegistryKey? existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (existing?.GetValue(ValueName) is string registered && MatchesExecutable(registered, CurrentExecutable()))
                existing.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string command = BuildCommand(CurrentExecutable());
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new IOException("Não foi possível abrir a inicialização do usuário.");
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    private static string CurrentExecutable() => Environment.ProcessPath
        ?? throw new InvalidOperationException("Executável atual indisponível.");

    internal static bool MatchesExecutable(string command, string executable) =>
        string.Equals(command, BuildCommand(executable), StringComparison.OrdinalIgnoreCase);

    internal static string BuildCommand(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"') ||
            !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Execute o WindowProbe.exe instalado para configurar a inicialização.");

        string command = $"\"{executable}\" --host --autostart";
        // O Windows limita as linhas de comando das chaves Run a 260 caracteres.
        if (command.Length > 260)
            throw new InvalidOperationException("O caminho do aplicativo é longo demais para iniciar com o Windows.");
        return command;
    }
}
