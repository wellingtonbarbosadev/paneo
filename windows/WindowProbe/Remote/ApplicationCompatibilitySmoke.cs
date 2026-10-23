using System.Diagnostics;
using System.Collections;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using WindowProbe.Capture;
using WindowProbe.Interop;
using WindowProbe.Models;

namespace WindowProbe.Remote;

/// <summary>
/// Matriz opt-in contra janelas novas de aplicativos reais. Nunca escolhe HWNDs
/// presentes no snapshot inicial e fecha apenas o HWND descartável que criou.
/// </summary>
internal static class ApplicationCompatibilitySmoke
{
    private const uint WmClose = 0x0010;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsCaption = 0x00C00000;
    private const long WsExAppWindow = 0x00040000;
    private const long WsExToolWindow = 0x00000080;
    private const uint GwOwner = 4;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr state);
    private sealed record Candidate(IntPtr Hwnd, uint Pid, string ProcessName, string Title, string ClassName, bool EnumeratorListed);
    private sealed record AppSpec(string Name, string ProcessName, Func<string, Process?> Launch,
        Func<Candidate, string, int?, bool> Match, bool SkipIfProcessExists);

    internal static async Task<int> RunAsync(string directory)
    {
        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        string reportPath = Path.Combine(root, "application-compatibility-result.json");
        string privateRoot = Path.Combine(Path.GetTempPath(), "Paneo-AppCompat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(privateRoot);
        var apps = new List<object>();
        bool allRequiredPassed = true;
        try
        {
            AppSpec[] specs = CreateSpecs();
            foreach (AppSpec spec in specs)
            {
                object result = await RunAppAsync(spec, privateRoot, root);
                apps.Add(result);
                using JsonDocument parsed = JsonDocument.Parse(JsonSerializer.Serialize(result));
                if (parsed.RootElement.TryGetProperty("status", out JsonElement status) && status.GetString() == "FAIL")
                    allRequiredPassed = false;
            }
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                success = allRequiredPassed,
                scope = "compatibilidade de aplicativos reais; não é soak de 30 minutos",
                apps
            }, new JsonSerializerOptions { WriteIndented = true }));
            return allRequiredPassed ? 0 : 1;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { success = false, error = ex.GetType().Name }));
            return 1;
        }
        finally
        {
            try { Directory.Delete(privateRoot, recursive: true); } catch { }
        }
    }

    private static async Task<object> RunAppAsync(AppSpec spec, string privateRoot, string outputRoot)
    {
        var scenarios = new List<object>();
        bool failed = false;
        HashSet<IntPtr> before = Snapshot().Select(item => item.Hwnd).ToHashSet();
        Process? launcher = null;
        Candidate? target = null;
        Candidate? calculatorCleanupTarget = null;
        string fixtureFolderName = "Paneo " + spec.Name + " fixture " + Guid.NewGuid().ToString("N");
        string appRoot = Path.Combine(privateRoot, fixtureFolderName);
        string captureRoot = Path.Combine(outputRoot, "captures", spec.Name);
        var captureEvidence = new List<object>();
        var candidateDiagnostics = new List<object>();
        var diagnosticHandles = new HashSet<IntPtr>();
        object? explorerDiagnostics = null;
        Directory.CreateDirectory(appRoot);
        string exeVersion = ResolveVersion(spec.ProcessName);
        try
        {
            if (spec.SkipIfProcessExists && HasVisibleAppWindow(spec.ProcessName))
                return new { app = spec.Name, version = exeVersion, status = "SKIP", reason = "Já havia uma janela visível do aplicativo; nenhuma janela foi reutilizada.", scenarios = SkippedScenarios("Janela existente") };

            launcher = spec.Launch(appRoot);
            if (launcher is null)
                return new { app = spec.Name, version = exeVersion, status = "SKIP", reason = "Não foi possível iniciar uma instância isolada neste Windows.", scenarios = SkippedScenarios("Aplicativo indisponível") };

            int? launcherPid = null;
            try { if (launcher is not null) launcherPid = launcher.Id; } catch { }
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                Candidate[] newWindows = SnapshotWithEnumerator().Where(item => !before.Contains(item.Hwnd)).ToArray();
                if (spec.Name == "Calculator")
                {
                    foreach (Candidate item in newWindows.Where(item => item.ProcessName.Equals("CalculatorApp", StringComparison.OrdinalIgnoreCase)
                        || item.ClassName == "ApplicationFrameWindow"))
                    {
                        if (diagnosticHandles.Add(item.Hwnd))
                            candidateDiagnostics.Add(CandidateMetadata(item, item.EnumeratorListed));
                    }
                }
                if (spec.Name == "Explorer")
                {
                    Candidate[] cabinets = newWindows.Where(item => item.ClassName == "CabinetWClass").ToArray();
                    bool[] titleMatches = cabinets.Select(item => item.Title.Contains(Path.GetFileName(appRoot), StringComparison.OrdinalIgnoreCase)).ToArray();
                    bool[] shellMatches = cabinets.Select(item => ExplorerWindowMatches(item.Hwnd, appRoot)).ToArray();
                    explorerDiagnostics = new
                    {
                        newCabinetWindowCount = cabinets.Length,
                        titleMatchesFixture = titleMatches.Any(value => value),
                        shellPathMatchesFixture = shellMatches.Any(value => value),
                        candidates = cabinets.Select((item, index) => CandidateMetadata(item, item.EnumeratorListed, titleMatches[index], shellMatches[index])).ToArray()
                    };
                }
                Candidate[] candidates = newWindows.Where(item => spec.Match(item, appRoot, launcherPid)).ToArray();
                foreach (Candidate candidate in candidates)
                {
                    await Task.Delay(1000);
                    Candidate? stable = SnapshotWithEnumerator().FirstOrDefault(item => item.Hwnd == candidate.Hwnd && item.Pid == candidate.Pid
                        && spec.Match(item, appRoot, launcherPid));
                    if (spec.Name == "Calculator" && stable is not null) calculatorCleanupTarget = stable;
                    if (stable?.EnumeratorListed == true) target = stable;
                    if (diagnosticHandles.Add(candidate.Hwnd))
                        candidateDiagnostics.Add(CandidateMetadata(stable ?? candidate, stable?.EnumeratorListed ?? false));
                    if (target is not null) break;
                }
                if (target is not null) break;
                await Task.Delay(250);
            }
            if (target is null)
            {
                foreach (Candidate candidate in SnapshotWithEnumerator().Where(item => !before.Contains(item.Hwnd) && spec.Match(item, appRoot, launcherPid)))
                    if (diagnosticHandles.Add(candidate.Hwnd)) candidateDiagnostics.Add(CandidateMetadata(candidate, candidate.EnumeratorListed));
                return new { app = spec.Name, version = exeVersion, status = "SKIP", reason = "Não foi possível identificar uma janela nova, estável e exclusiva do aplicativo.", scenarios = SkippedScenarios("HWND novo não identificado"), candidateDiagnostics, explorerDiagnostics };
            }

            using var commands = new WindowCommands(target.Hwnd, target.Pid);
            Require(Execute(commands, "restore"), "Não foi possível normalizar a janela para o teste.");
            Require(WaitUntil(() => !commands.State.Minimized && !commands.State.Maximized, TimeSpan.FromSeconds(5)),
                "A janela não chegou ao estado normal.");
            RECT original;
            if (!GetWindowRect(target.Hwnd, out original)) throw new InvalidOperationException("Não foi possível ler a geometria inicial.");

            Check("minimize-restore", () =>
            {
                Require(Execute(commands, "minimize"), "minimize falhou");
                Require(commands.State.Minimized, "estado minimizado não foi observado");
                Require(Execute(commands, "restore"), "restore após minimizar falhou");
                Require(!commands.State.Minimized, "janela continuou minimizada");
            });
            if (commands.State.CanResize) Check("maximize-restore", () =>
            {
                Require(Execute(commands, "maximize"), "maximize falhou");
                Require(commands.State.Maximized, "estado maximizado não foi observado");
                Require(Execute(commands, "restore"), "restore após maximizar falhou");
                Require(!commands.State.Maximized && !commands.State.Minimized, "janela não voltou ao estado normal");
            });
            else scenarios.Add(new { scenario = "maximize-restore", status = "SKIP", detail = "Janela sem capacidade de redimensionamento." });
            if (commands.State.CanResize) Check("reading-resize-restores-geometry", () =>
            {
                Require(Execute(commands, "reading", enabled: true, width: 640, height: 480), "modo leitura falhou");
                Require(commands.State.Reading, "estado leitura não foi observado");
                Require(WaitUntil(() => GetWindowRect(target.Hwnd, out RECT resized) && !SameSize(original, resized), TimeSpan.FromSeconds(5)),
                    "Janela não mudou de tamanho ao ativar modo leitura.");
                Require(Execute(commands, "reading", enabled: false), "restauração do modo leitura falhou");
                Require(!commands.State.Reading, "estado leitura continuou ativo");
                Require(WaitUntil(() => GetWindowRect(target.Hwnd, out RECT restored) && SameRect(original, restored), TimeSpan.FromSeconds(5)),
                    "Geometria original não foi restaurada dentro do prazo.");
            });
            else scenarios.Add(new { scenario = "reading-resize-restores-geometry", status = "SKIP", detail = "Janela sem capacidade de redimensionamento." });

            RemoteWindowMenus menus = commands.GetMenus();
            scenarios.Add(menus.Supported
                ? new { scenario = "native-menu-enumeration", status = "PASS", detail = "Menu Win32 nativo enumerado." }
                : new { scenario = "native-menu-enumeration", status = "SKIP", detail = "Fallback explícito: " + (menus.Reason ?? "menu indisponível") });

            await CheckAsync("frame-received", async () =>
            {
                var capture = new WindowCaptureService();
                Directory.CreateDirectory(captureRoot);
                string image = Path.Combine(captureRoot, "frame.png");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var frame = await capture.CaptureAsync(target.Hwnd, image, TimeSpan.FromSeconds(8), null, cts.Token);
                Require(frame.Bytes > 0 && frame.Width > 0 && frame.Height > 0 && File.Exists(image), "captura não produziu frame válido");
                captureEvidence.Add(new { capture = "frame", width = frame.Width, height = frame.Height, bytes = frame.Bytes, path = Path.GetRelativePath(outputRoot, image) });
            });
            await CheckAsync("occluded-frame-received", async () =>
            {
                GetWindowRect(target.Hwnd, out RECT bounds);
                var overlay = new Window
                {
                    Title = "Paneo temporary occlusion test",
                    Width = Math.Max(240, bounds.Right - bounds.Left), Height = Math.Max(120, bounds.Bottom - bounds.Top),
                    Left = bounds.Left, Top = bounds.Top, Topmost = true, ShowActivated = false,
                    WindowStyle = WindowStyle.None, Content = new Border { Background = System.Windows.Media.Brushes.DarkSlateGray,
                        Child = new TextBlock { Text = "Paneo occlusion test", Foreground = System.Windows.Media.Brushes.White,
                            FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } }
                };
                try
                {
                    overlay.Show();
                    await Task.Delay(300);
                    var capture = new WindowCaptureService();
                    Directory.CreateDirectory(captureRoot);
                    string image = Path.Combine(captureRoot, "occluded-frame.png");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    var frame = await capture.CaptureAsync(target.Hwnd, image, TimeSpan.FromSeconds(8), null, cts.Token);
                    Require(frame.Bytes > 0, "captura ocluída não produziu frame");
                    captureEvidence.Add(new { capture = "occluded-frame", width = frame.Width, height = frame.Height, bytes = frame.Bytes, path = Path.GetRelativePath(outputRoot, image) });
                }
                finally { overlay.Close(); }
            });

            var targetIdentity = new { className = target.ClassName, enumeratorListed = target.EnumeratorListed };
            return new { app = spec.Name, version = exeVersion, status = failed ? "FAIL" : "PASS", target = targetIdentity, scenarios, captures = captureEvidence };
        }
        catch (Exception ex)
        {
            scenarios.Add(new { scenario = "application-run", status = "FAIL", detail = ex.GetType().Name });
            return new { app = spec.Name, version = exeVersion, status = "FAIL",
                target = target is null ? null : new { className = target.ClassName, enumeratorListed = target.EnumeratorListed }, scenarios, captures = captureEvidence };
        }
        finally
        {
            Candidate? closeCandidate = target ?? (spec.Name == "Calculator" ? calculatorCleanupTarget : null);
            if (closeCandidate is not null && IsWindowVisible(closeCandidate.Hwnd))
            {
                _ = PostMessage(closeCandidate.Hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                await Task.Delay(750);
            }
            if (launcher is not null) launcher.Dispose();
        }

        void Check(string name, Action action)
        {
            try { action(); scenarios.Add(new { scenario = name, status = "PASS" }); }
            catch (Exception ex) { failed = true; scenarios.Add(new { scenario = name, status = "FAIL", detail = ex.GetType().Name }); }
        }

        async Task CheckAsync(string name, Func<Task> action)
        {
            try { await action(); scenarios.Add(new { scenario = name, status = "PASS" }); }
            catch (Exception ex) { failed = true; scenarios.Add(new { scenario = name, status = "FAIL", detail = SafeFailureDetail(ex) }); }
        }
    }

    private static AppSpec[] CreateSpecs() =>
    [
        new("Notepad", "notepad", directory =>
        {
            string document = Path.Combine(directory, Path.GetFileName(directory) + ".txt");
            File.WriteAllText(document, "Paneo disposable compatibility fixture.");
            return Start("notepad.exe", document);
        }, (candidate, directory, _) => candidate.Title.Contains(Path.GetFileName(directory), StringComparison.OrdinalIgnoreCase), true),
        new("Explorer", "explorer", StartExplorer,
            (candidate, directory, _) => candidate.ClassName == "CabinetWClass"
                && (candidate.Title.Contains(Path.GetFileName(directory), StringComparison.OrdinalIgnoreCase)
                    || ExplorerWindowMatches(candidate.Hwnd, directory)), false),
        new("Edge", "msedge", directory =>
        {
            string profile = Path.Combine(directory, "Edge isolated profile");
            string? executable = FindEdgeExecutable();
            return executable is null ? null : Start(executable, "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check", "about:blank");
        }, (candidate, _, launcherPid) => launcherPid.HasValue && candidate.Pid == (uint)launcherPid.Value
            && candidate.Title.Length > 0 && GetProcessName(candidate.Pid).Equals("msedge", StringComparison.OrdinalIgnoreCase), true),
        new("Calculator", "CalculatorApp", directory => Start("calc.exe"),
            (candidate, _, _) => candidate.ClassName == "ApplicationFrameWindow"
                && candidate.EnumeratorListed
                && candidate.Title.Length > 0
                && HasDescendantProcess(candidate.Hwnd, "CalculatorApp"), true)
    ];

    private static Process? Start(string file, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(file) { UseShellExecute = true };
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
            return Process.Start(info);
        }
        catch { return null; }
    }

    private static Process? StartExplorer(string directory)
    {
        try
        {
            var info = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                Arguments = $"/n,/e,\"{directory}\""
            };
            return Process.Start(info);
        }
        catch { return null; }
    }

    private static List<Candidate> Snapshot()
    {
        var result = new List<Candidate>();
        _ = EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var title = new System.Text.StringBuilder(512);
            int length = GetWindowText(hwnd, title, title.Capacity);
            if (length <= 0) return true;
            uint threadId = GetWindowThreadProcessId(hwnd, out uint pid);
            if (threadId == 0) return true;
            var className = new System.Text.StringBuilder(256);
            _ = GetClassName(hwnd, className, className.Capacity);
            result.Add(new(hwnd, pid, GetProcessName(pid), title.ToString(), className.ToString(), false));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static List<Candidate> SnapshotWithEnumerator()
    {
        HashSet<IntPtr> enumerated = WindowEnumerator.GetVisibleWindows().Select(item => item.Handle).ToHashSet();
        return Snapshot().Select(item => item with { EnumeratorListed = enumerated.Contains(item.Hwnd) }).ToList();
    }

    private static object CandidateMetadata(Candidate candidate, bool enumeratorListed, bool? titleMatchesFixture = null, bool? shellPathMatchesFixture = null)
    {
        long style = GetWindowLongPtr(candidate.Hwnd, GwlStyle).ToInt64();
        long exStyle = GetWindowLongPtr(candidate.Hwnd, GwlExStyle).ToInt64();
        _ = NativeMethods.TryGetWindowSize(candidate.Hwnd, out int width, out int height);
        return new
        {
            className = candidate.ClassName,
            processName = candidate.ProcessName,
            titlePresent = candidate.Title.Length > 0,
            caption = (style & WsCaption) != 0,
            appWindow = (exStyle & WsExAppWindow) != 0,
            toolWindow = (exStyle & WsExToolWindow) != 0,
            ownerPresent = GetWindow(candidate.Hwnd, GwOwner) != IntPtr.Zero,
            hasCalculatorDescendant = candidate.ClassName == "ApplicationFrameWindow" && HasDescendantProcess(candidate.Hwnd, "CalculatorApp"),
            cloaked = NativeMethods.IsWindowCloaked(candidate.Hwnd),
            width,
            height,
            enumeratorListed,
            titleMatchesFixture,
            shellPathMatchesFixture
        };
    }

    private static bool Execute(WindowCommands commands, string action, bool? enabled = null, int? width = null, int? height = null)
    {
        var payload = new Dictionary<string, object?> { ["action"] = action };
        if (enabled.HasValue) payload["enabled"] = enabled.Value;
        if (width.HasValue) payload["width"] = width.Value;
        if (height.HasValue) payload["height"] = height.Value;
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return commands.Execute(json.RootElement).Success;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string SafeFailureDetail(Exception exception) => exception is CaptureException
        ? exception.Message
        : exception.GetType().Name;

    private static bool SameRect(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
    private static bool SameSize(RECT a, RECT b) => a.Right - a.Left == b.Right - b.Left && a.Bottom - a.Top == b.Bottom - b.Top;

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    private static object[] SkippedScenarios(string reason) =>
    [
        new { scenario = "minimize-restore", status = "SKIP", detail = reason },
        new { scenario = "maximize-restore", status = "SKIP", detail = reason },
        new { scenario = "reading-resize-restores-geometry", status = "SKIP", detail = reason },
        new { scenario = "native-menu-enumeration", status = "SKIP", detail = reason },
        new { scenario = "frame-received", status = "SKIP", detail = reason },
        new { scenario = "occluded-frame-received", status = "SKIP", detail = reason }
    ];

    private static string ResolveVersion(string processName)
    {
        Process[] matchingProcesses = Process.GetProcessesByName(processName);
        string? path;
        try
        {
            path = matchingProcesses.Select(process =>
            {
                try { return process.MainModule?.FileName; } catch { return null; }
            }).FirstOrDefault(value => value is not null);
        }
        finally { foreach (Process process in matchingProcesses) process.Dispose(); }
        if (path is null)
        {
            string[] candidates = processName switch
            {
                "notepad" => [Path.Combine(Environment.SystemDirectory, "notepad.exe")],
                "explorer" => [Path.Combine(Environment.SystemDirectory, "explorer.exe")],
                "msedge" => [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft/Edge/Application/msedge.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft/Edge/Application/msedge.exe")],
                _ => [Path.Combine(Environment.SystemDirectory, "calc.exe")]
            };
            path = candidates.FirstOrDefault(File.Exists);
        }
        try { return path is null ? "indisponível" : FileVersionInfo.GetVersionInfo(path).FileVersion ?? "desconhecida"; }
        catch { return "desconhecida"; }
    }

    private static string? FindEdgeExecutable()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft/Edge/Application/msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft/Edge/Application/msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs/Microsoft Edge/Application/msedge.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool ExplorerWindowMatches(IntPtr hwnd, string expectedPath)
    {
        object? shell = null, windows = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application", throwOnError: false);
            if (shellType is null) return false;
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return false;
            windows = ((dynamic)shell).Windows();
            foreach (object item in (IEnumerable)windows)
            {
                object? document = null, folder = null, self = null;
                try
                {
                    dynamic explorer = item;
                    if (Convert.ToInt64(explorer.HWND) != hwnd.ToInt64()) continue;
                    document = explorer.Document;
                    folder = ((dynamic)document).Folder;
                    self = ((dynamic)folder).Self;
                    string actualPath = (string)((dynamic)self).Path;
                    return string.Equals(Path.GetFullPath(actualPath).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(expectedPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
                }
                catch { }
                finally
                {
                    ReleaseCom(self); ReleaseCom(folder); ReleaseCom(document); ReleaseCom(item);
                }
            }
        }
        catch { }
        finally { ReleaseCom(windows); ReleaseCom(shell); }
        return false;
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.ReleaseComObject(value); } catch { }
        }
    }

    private static string GetProcessName(uint pid)
    {
        try { using Process process = Process.GetProcessById((int)pid); return process.ProcessName; }
        catch { return ""; }
    }

    private static bool HasVisibleAppWindow(string processName)
    {
        foreach (Candidate topLevel in Snapshot())
        {
            if (topLevel.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)) return true;
            if (HasDescendantProcess(topLevel.Hwnd, processName)) return true;
        }
        return false;
    }

    private static bool HasDescendantProcess(IntPtr parent, string processName)
    {
        bool found = false;
        _ = EnumChildWindows(parent, (child, _) =>
        {
            GetWindowThreadProcessId(child, out uint pid);
            if (GetProcessName(pid).Equals(processName, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
