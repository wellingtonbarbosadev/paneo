using WindowProbe.Remote;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

if (args is ["--inspect-installed"])
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("--inspect-installed exige Windows.");
        return 2;
    }

    AppLauncher.CatalogSnapshot installed = AppLauncher.GetCatalog();
    int count = installed.Apps.Count;
    bool opaqueIds = installed.Apps.All(app => app.Id.StartsWith("app-", StringComparison.Ordinal) &&
        app.Id.Length == 68 && app.Id.Skip(4).All(Uri.IsHexDigit) && app.Command == app.Id);
    bool appFolderTarget = installed.LaunchTargets.Values.Any(target => target.FileName == "explorer.exe" &&
        target.Argument?.StartsWith("shell:AppsFolder\\", StringComparison.Ordinal) == true);
    bool processNameAvailable = installed.Apps.Any(app => !string.IsNullOrWhiteSpace(app.ProcessName));
    Console.WriteLine($"INSTALLED count={count} opaqueIds={opaqueIds} appsFolderTarget={appFolderTarget} processNameAvailable={processNameAvailable}");
    Check(count > 7, "Catálogo instalado não excedeu os aliases básicos.");
    Check(opaqueIds, "Catálogo instalado contém ID/comando não opaco.");
    Check(processNameAvailable, "Nenhum ProcessName disponível para os destinos conhecidos.");
    return 0;
}

var fixture = new[]
{
    new AppLauncher.Entry("Editor", @"path:C:\Start Menu\Editor.lnk", "editor"),
    new AppLauncher.Entry("Editor duplicado", @"PATH:c:/start menu/editor.lnk")
};
LauncherApp first = AppLauncher.BuildCatalog(fixture).Apps.Single();
LauncherApp again = AppLauncher.BuildCatalog([new AppLauncher.Entry("Editor", @"path:c:/START MENU/EDITOR.lnk", "editor")]).Apps.Single();
Check(first.Id.StartsWith("app-", StringComparison.Ordinal) && first.Id == again.Id, "ID não opaco/estável.");
Check(first.Command == first.Id && first.Command != fixture[0].Target && first.Command.Length == 68,
    "Comando remoto expõe o caminho ou ID não opaco.");
Check(first.ProcessName == "editor", "Nome de processo opcional foi perdido.");
Check(AppLauncher.GetLegacyApps().Select(app => (app.Id, app.Command)).SequenceEqual(new[]
{
    ("notepad", "notepad.exe"), ("calc", "calc.exe"), ("explorer", "explorer.exe"),
    ("mspaint", "mspaint.exe"), ("powershell", "powershell.exe"), ("cmd", "cmd.exe"), ("edge", "msedge.exe")
}), "IDs/comandos legados foram alterados.");
Check(AppLauncher.GetKnownApps().Take(7).All(app => app.Id.StartsWith("app-", StringComparison.Ordinal) && app.Command == app.Id),
    "Catálogo novo não usa IDs opacos nos aliases internos.");
Check(AppLauncher.BuildCatalogWithFallback([], [new AppLauncher.Entry("Fallback", "aumid:fallback")]).Apps.Single().Name == "Fallback",
    "Fonte fallback não foi incluída.");
Check(!AppLauncher.Launch(@"C:\Windows\System32\calc.exe") && !AppLauncher.Launch("unknown-app") &&
    !AppLauncher.Launch("notepad.exe argument"), "Entrada desconhecida, caminho ou argumentos foram aceitos.");
Console.WriteLine("OK catálogo: ID estável, opaco e rejeição de entrada desconhecida");
return 0;
