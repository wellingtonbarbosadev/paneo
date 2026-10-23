using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace WindowProbe.Remote;

internal sealed record LauncherApp(string Id, string Name, string Command, string? Icon = null, string? ProcessName = null);

internal static class AppLauncher
{
    internal sealed record Entry(string Name, string Target, string? ProcessName = null,
        string? FileName = null, string? Argument = null);
    internal sealed record CatalogSnapshot(IReadOnlyList<LauncherApp> Apps,
        IReadOnlyDictionary<string, (string FileName, string? Argument)> LaunchTargets);

    private static readonly TimeSpan CatalogTtl = TimeSpan.FromMinutes(1);
    private static readonly object CatalogGate = new();
    private static DateTime catalogExpires;
    private static CatalogSnapshot? catalog;
    private static readonly LauncherApp[] LegacyApps =
    [
        new("notepad", "Bloco de notas", "notepad.exe", ProcessName: "notepad"),
        new("calc", "Calculadora", "calc.exe", ProcessName: "CalculatorApp"),
        new("explorer", "Explorer de arquivos", "explorer.exe", ProcessName: "explorer"),
        new("mspaint", "Paint", "mspaint.exe", ProcessName: "mspaint"),
        new("powershell", "PowerShell", "powershell.exe", ProcessName: "powershell"),
        new("cmd", "Prompt de comando", "cmd.exe", ProcessName: "cmd"),
        new("edge", "Microsoft Edge", "msedge.exe", ProcessName: "msedge")
    ];
    private static readonly Entry[] BuiltInEntries =
    [
        new("Bloco de notas", "exe:notepad.exe", "notepad", "notepad.exe"),
        new("Calculadora", "exe:calc.exe", "CalculatorApp", "calc.exe"),
        new("Explorer de arquivos", "exe:explorer.exe", "explorer", "explorer.exe"),
        new("Paint", "exe:mspaint.exe", "mspaint", "mspaint.exe"),
        new("PowerShell", "exe:powershell.exe", "powershell", "powershell.exe"),
        new("Prompt de comando", "exe:cmd.exe", "cmd", "cmd.exe"),
        new("Microsoft Edge", "exe:msedge.exe", "msedge", "msedge.exe")
    ];

    public static IReadOnlyList<LauncherApp> GetKnownApps() => GetCatalog().Apps;
    public static IReadOnlyList<LauncherApp> GetLegacyApps() => Array.AsReadOnly(LegacyApps);

    public static bool Launch(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd) || cmd.Contains('/') || cmd.Contains('\\') || cmd.Any(char.IsWhiteSpace)) return false;
        string value = cmd.Trim();
        LauncherApp? legacy = LegacyApps.FirstOrDefault(app =>
            string.Equals(app.Id, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(app.Command, value, StringComparison.OrdinalIgnoreCase));
        if (legacy is not null) return Start(legacy.Command, null, shell: true);

        CatalogSnapshot snapshot = GetCatalog();
        LauncherApp? app = snapshot.Apps.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, value, StringComparison.OrdinalIgnoreCase));
        if (app is null || !snapshot.LaunchTargets.TryGetValue(app.Id, out var target)) return false;
        return Start(target.FileName, target.Argument, shell: target.Argument is null);
    }

    internal static CatalogSnapshot BuildCatalog(IEnumerable<Entry> entries)
    {
        var apps = new List<LauncherApp>();
        var targets = new Dictionary<string, (string FileName, string? Argument)>(StringComparer.OrdinalIgnoreCase);
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Entry entry in entries)
        {
            string identity = NormalizeIdentity(entry.Target);
            string id = "app-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            if (ids.TryGetValue(id, out string? existing))
            {
                if (string.Equals(existing, identity, StringComparison.OrdinalIgnoreCase)) continue;
                throw new InvalidOperationException("Colisão de ID no catálogo de aplicativos.");
            }
            ids.Add(id, identity);
            apps.Add(new LauncherApp(id, entry.Name, id, ProcessName: entry.ProcessName));
            targets.Add(id, (entry.FileName ?? entry.Target, entry.Argument));
        }
        return new CatalogSnapshot(Array.AsReadOnly(apps.ToArray()),
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, (string FileName, string? Argument)>(targets));
    }

    internal static CatalogSnapshot BuildCatalogWithFallback(IEnumerable<Entry> primary, IEnumerable<Entry> fallback) =>
        BuildCatalog([.. primary, .. fallback]);

    internal static CatalogSnapshot GetCatalog()
    {
        lock (CatalogGate)
        {
            if (catalog is not null && DateTime.UtcNow < catalogExpires) return catalog;
            try
            {
                catalog = BuildCatalogWithFallback(BuiltInEntries, [.. EnumerateAppsFolder(), .. EnumerateStartMenu()]);
            }
            catch (Exception) { catalog = BuildCatalogWithFallback(BuiltInEntries, EnumerateStartMenu()); }
            catalogExpires = DateTime.UtcNow + CatalogTtl;
            return catalog;
        }
    }

    private static bool Start(string fileName, string? argument, bool shell)
    {
        try
        {
            var start = new ProcessStartInfo { FileName = fileName, UseShellExecute = shell };
            if (argument is not null) start.ArgumentList.Add(argument);
            using Process? process = Process.Start(start);
            // O Shell pode reutilizar uma instância sem devolver um novo processo.
            return shell || process is not null;
        }
        catch (Exception) { return false; }
    }

    private static IEnumerable<Entry> EnumerateAppsFolder() =>
        OperatingSystem.IsWindows() ? EnumerateAppsFolderWindows() : [];

    [SupportedOSPlatform("windows")]
    private static IEnumerable<Entry> EnumerateAppsFolderWindows()
    {
        var entries = new ConcurrentQueue<Entry>();
        var thread = new Thread(() =>
        {
            object? shell = null, folder = null, items = null;
            try
            {
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is null) return;
                dynamic shellObject = shell = Activator.CreateInstance(shellType)!;
                dynamic appsFolder = folder = shellObject.Namespace("shell:AppsFolder");
                if (appsFolder is null) return;
                dynamic shellItems = items = appsFolder.Items();
                foreach (dynamic item in shellItems)
                {
                    try
                    {
                        string appId = Convert.ToString(item.ExtendedProperty("System.AppUserModel.ID")) ?? Convert.ToString(item.Path) ?? "";
                        string name = Convert.ToString(item.Name) ?? "";
                        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name)) continue;
                        string targetPath = Convert.ToString(item.ExtendedProperty("System.Link.TargetParsingPath")) ?? Convert.ToString(item.Path) ?? "";
                        string? processName = string.Equals(Path.GetExtension(targetPath), ".exe", StringComparison.OrdinalIgnoreCase)
                            ? Path.GetFileNameWithoutExtension(targetPath) : null;
                        entries.Enqueue(new Entry(name, "aumid:" + appId, processName, "explorer.exe", "shell:AppsFolder\\" + appId));
                    }
                    catch (Exception) { }
                    finally { if (Marshal.IsComObject(item)) try { Marshal.FinalReleaseComObject(item); } catch (Exception) { } }
                }
            }
            catch (Exception) { }
            finally
            {
                foreach (object? value in new[] { items, folder, shell })
                    if (value is not null && Marshal.IsComObject(value)) try { Marshal.FinalReleaseComObject(value); } catch (Exception) { }
            }
        }) { IsBackground = true, Name = "Paneo AppsFolder discovery" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (thread.Join(TimeSpan.FromSeconds(5)))
            while (entries.TryDequeue(out Entry? entry)) yield return entry;
    }

    private static IEnumerable<Entry> EnumerateStartMenu() =>
        OperatingSystem.IsWindows() ? EnumerateStartMenuWindows() : [];

    [SupportedOSPlatform("windows")]
    private static IEnumerable<Entry> EnumerateStartMenuWindows()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        ];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> found;
            try { found = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToArray(); }
            catch (Exception) { continue; }
            foreach (string path in found) paths.Add(path);
        }

        var entries = new ConcurrentQueue<Entry>();
        var thread = new Thread(() =>
        {
            object? shell = null;
            try
            {
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is null) return;
                dynamic shellObject = shell = Activator.CreateInstance(shellType)!;
                foreach (string path in paths)
                {
                    object? folder = null, item = null;
                    try
                    {
                        dynamic directory = folder = shellObject.Namespace(Path.GetDirectoryName(path));
                        if (directory is null) continue;
                        dynamic shortcut = item = directory.ParseName(Path.GetFileName(path));
                        if (shortcut is null) continue;
                        string name = Convert.ToString(shortcut.Name) ?? Path.GetFileNameWithoutExtension(path);
                        string target = Convert.ToString(shortcut.ExtendedProperty("System.Link.TargetParsingPath")) ?? "";
                        string? processName = string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase)
                            ? Path.GetFileNameWithoutExtension(target) : null;
                        entries.Enqueue(new Entry(name, "path:" + path, processName, path));
                    }
                    catch (Exception) { entries.Enqueue(new Entry(Path.GetFileNameWithoutExtension(path), "path:" + path, null, path)); }
                    finally
                    {
                        foreach (object? value in new[] { item, folder })
                            if (value is not null && Marshal.IsComObject(value)) try { Marshal.FinalReleaseComObject(value); } catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            finally { if (shell is not null && Marshal.IsComObject(shell)) try { Marshal.FinalReleaseComObject(shell); } catch (Exception) { } }
        }) { IsBackground = true, Name = "Paneo Start Menu discovery" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(TimeSpan.FromSeconds(5)) ? entries.ToArray() : [];
    }

    private static string NormalizeIdentity(string value)
    {
        string identity = value.StartsWith("path:", StringComparison.OrdinalIgnoreCase)
            ? "path:" + value[5..].Replace('/', '\\')
            : value;
        return identity.ToUpperInvariant();
    }
}
