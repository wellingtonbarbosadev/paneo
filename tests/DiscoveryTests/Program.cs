using WindowProbe.Remote;

if (args.Length != 3 || args[0] != "--announce" || !int.TryParse(args[1], out int port) ||
    !int.TryParse(args[2], out int durationMs) || port is < 1 or > 65535 || durationMs is < 1 or > 30000)
{
    Console.Error.WriteLine("Uso: DiscoveryTests --announce <porta> <duração-ms>");
    return 2;
}

try
{
    var discovery = new HostDiscovery(port);
    await discovery.Registered;
    Console.WriteLine($"ANNOUNCED {Environment.MachineName} {port}");
    await Task.Delay(durationMs);
    await discovery.StopAsync();
    Console.WriteLine("STOPPED");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"DISCOVERY ERROR {error.GetType().Name}: {error.Message}");
    return 1;
}
