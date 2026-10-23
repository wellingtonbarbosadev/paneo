using System.Text.Json;
using WindowProbe.Remote;

string directory = Path.Combine(Path.GetTempPath(), "paneo-startup-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string settingsPath = Path.Combine(directory, "host-settings.json");
    Require(HostSettings.Load(settingsPath) == new HostSettings(9443, true), "Preferências iniciais");
    new HostSettings(9544, false).Save(settingsPath);
    Require(HostSettings.Load(settingsPath) == new HostSettings(9544, false), "Porta e hover preservados");
    new HostSettings(9644, true).Save(settingsPath);
    Require(HostSettings.Load(settingsPath) == new HostSettings(9644, true), "Atualização das preferências");
    Require(Directory.GetFiles(directory).Length == 1, "Sem arquivo temporário residual");
    string json = File.ReadAllText(settingsPath);
    using (JsonDocument document = JsonDocument.Parse(json))
        Require(document.RootElement.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "AllowHover", "Port" }),
            "Preferências contêm apenas porta e hover");

    foreach (int invalidPort in new[] { 0, -1, 65536 })
    {
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new HostSettings(invalidPort, false)));
        Reject<InvalidDataException>(() => HostSettings.Load(settingsPath), "Porta inválida rejeitada ao carregar");
        Reject<InvalidDataException>(() => new HostSettings(invalidPort).Save(settingsPath), "Porta inválida rejeitada ao salvar");
    }
    File.WriteAllText(settingsPath, "null");
    Reject<InvalidDataException>(() => HostSettings.Load(settingsPath), "Estado vazio rejeitado");
    File.WriteAllText(settingsPath, "{corrupto");
    Reject<JsonException>(() => HostSettings.Load(settingsPath), "JSON corrompido rejeitado");

    string executable = Path.Combine(directory, "Programs With Spaces", "WindowProbe.exe");
    Require(HostStartup.BuildCommand(executable) == $"\"{executable}\" --host --autostart", "Caminho com espaços protegido e argumentos corretos");
    Require(HostStartup.MatchesExecutable(HostStartup.BuildCommand(executable).ToUpperInvariant(), executable), "Identificação ignora caixa no Windows");
    Require(!HostStartup.MatchesExecutable(HostStartup.BuildCommand(Path.Combine(directory, "New Install", "WindowProbe.exe")), executable),
        "Desinstalação antiga não remove inicialização de outra instalação");
    Require(!HostStartup.MatchesExecutable($"\"{executable}\" --probe", executable), "Somente comando próprio pode ser removido");
    foreach (string invalid in new[]
    {
        "WindowProbe.exe", Path.Combine(directory, "dotnet.exe"),
        Path.Combine(directory, "WindowProbe.dll"), executable + "\" --probe",
        Path.Combine(directory, new string('x', 261) + ".exe")
    }) Reject<InvalidOperationException>(() => HostStartup.BuildCommand(invalid), "Comando inseguro ou não suportado rejeitado");

    Console.WriteLine("PASS: preferências, persistência, corrupção, validação e comando de inicialização (sem alterar registro real).");
}
finally { Directory.Delete(directory, recursive: true); }

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Reject<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception(message);
}
