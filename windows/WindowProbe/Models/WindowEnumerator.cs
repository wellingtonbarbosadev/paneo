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

                if (!NativeMethods.TryGetWindowSize(hwnd, out int width, out int height))
                {
                    return true;
                }

                // Evita ruído de janelas auxiliares minúsculas.
                if (width < 16 || height < 16)
                {
                    return true;
                }

                windows.Add(new WindowInfo(hwnd, processId, GetProcessName(processId), title, width, height));
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
