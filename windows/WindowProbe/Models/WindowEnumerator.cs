using System.Diagnostics;
using WindowProbe.Interop;

namespace WindowProbe.Models;

public static class WindowEnumerator
{
    /// <summary>
    /// Lista janelas de nível superior visíveis, não minimizadas, com título,
    /// de outros processos. Janelas "cloaked" (outro desktop virtual) são ignoradas.
    /// </summary>
    public static IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        var windows = new List<WindowInfo>();
        uint ownProcessId = (uint)Environment.ProcessId;

        NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                if (NativeMethods.IsIconic(hwnd))
                {
                    return true;
                }

                if (NativeMethods.IsWindowCloaked(hwnd))
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
                if (processId == 0 || processId == ownProcessId)
                {
                    return true;
                }

                string title = NativeMethods.GetWindowTitle(hwnd);
                if (title.Length == 0)
                {
                    return true;
                }

                if (IsAuxiliaryWindow(hwnd, title))
                {
                    return true;
                }

                if (!NativeMethods.TryGetWindowSize(hwnd, out int width, out int height))
                {
                    return true;
                }

                // Evita ruído de janelas auxiliares minúsculas.
                if (width < 16 || height < 16)
                {
                    return true;
                }

                string processName = GetProcessName(processId);
                windows.Add(new WindowInfo(
                    hwnd,
                    processId,
                    processName,
                    title,
                    width,
                    height,
                    WindowIconExtractor.GetIconBase64(hwnd, processId, processName)));
            }
            catch
            {
                // Uma janela problemática não deve abortar a enumeração inteira.
            }

            return true;
        };

        NativeMethods.EnumWindows(callback, IntPtr.Zero);

        windows.Sort(static (left, right) =>
        {
            int byProcess = string.Compare(
                left.ProcessName,
                right.ProcessName,
                StringComparison.CurrentCultureIgnoreCase);

            return byProcess != 0
                ? byProcess
                : string.Compare(left.Title, right.Title, StringComparison.CurrentCultureIgnoreCase);
        });

        return windows;
    }

    private static readonly HashSet<string> AuxiliaryClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "XamlExplorerHostIslandWindow", "Windows.UI.Core.CoreWindow", "#32768"
    };

    /// <summary>
    /// Janelas auxiliares do shell (área de trabalho, barra de tarefas, hosts de
    /// popup XAML) e janelas de ferramenta/pertencentes a outra janela não são
    /// aplicativos que o usuário queira escolher.
    /// </summary>
    private static bool IsAuxiliaryWindow(IntPtr hwnd, string title)
    {
        if (title is "PopupHost" or "Program Manager") return true;

        var name = new System.Text.StringBuilder(128);
        string className = GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
        // Janelas do Explorador de Arquivos são sempre aplicativos reais.
        if (className == "CabinetWClass") return false;
        if (AuxiliaryClasses.Contains(className)) return true;

        long exStyle = GetWindowLongPtr(hwnd, -20).ToInt64();
        long style = GetWindowLongPtr(hwnd, -16).ToInt64();
        const long WsExToolWindow = 0x80, WsExAppWindow = 0x40000, WsCaption = 0xC00000;
        if ((exStyle & WsExAppWindow) != 0) return false;
        if ((exStyle & WsExToolWindow) != 0) return true;
        // Janela com barra de título é aplicativo, mesmo se tiver dono.
        if ((style & WsCaption) == WsCaption) return false;
        return GetWindow(hwnd, 4) != IntPtr.Zero; // GW_OWNER
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder text, int count);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    private static string GetProcessName(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return $"pid {processId}";
        }
    }
}
