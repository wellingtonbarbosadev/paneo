using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WindowProbe.Remote;

internal sealed record RemoteWindowState(long WindowId, bool Minimized, bool Maximized, bool Reading, bool CanResize);
internal sealed record RemoteWindowCommandResult(string Action, bool Success, string Message);
internal sealed record RemoteWindowMenuItem(string Id, string Title, bool Enabled, bool Checked, bool Separator, IReadOnlyList<RemoteWindowMenuItem> Children);
internal sealed record RemoteWindowMenus(bool Supported, IReadOnlyList<RemoteWindowMenuItem> Items, string? Reason = null);

internal interface IWindowCommands : IDisposable
{
    RemoteWindowState State { get; }
    RemoteWindowCommandResult Execute(JsonElement command);
    RemoteWindowMenus GetMenus();
    RemoteWindowCommandResult InvokeMenu(string id);
}

internal sealed class UnavailableWindowCommands(long windowId) : IWindowCommands
{
    public RemoteWindowState State => new(windowId, false, false, false, false);
    public RemoteWindowCommandResult Execute(JsonElement command) => new(
        command.TryGetProperty("action", out JsonElement action) ? action.GetString() ?? "" : "", false, "Controles de janela indisponíveis neste ambiente.");
    public RemoteWindowMenus GetMenus() => new(false, [], "Controles de janela indisponíveis neste ambiente.");
    public RemoteWindowCommandResult InvokeMenu(string id) => new("menu", false, "Controles de janela indisponíveis neste ambiente.");
    public void Dispose() { }
}

/// <summary>Controles Win32 limitados ao HWND e PID selecionados na sessão.</summary>
internal sealed class WindowCommands : IWindowCommands
{
    private const int GwlStyle = -16, GwlExStyle = -20;
    private const long WsThickFrame = 0x00040000;
    private const uint SwMinimize = 6, SwMaximize = 3, SwRestore = 9;
    private const uint WmClose = 0x0010, WmCommand = 0x0111;
    private const uint WmInitMenu = 0x0116, WmInitMenuPopup = 0x0117, WmMenuCommand = 0x0126;
    private const uint SmtoBlock = 0x0001, SmtoAbortIfHung = 0x0002;
    private const uint MfsDisabled = 0x0003, MfsChecked = 0x0008, MftSeparator = 0x0800, MftOwnerDraw = 0x0100, MiimState = 0x0001, MiimId = 0x0002, MiimSubMenu = 0x0004, MiimFType = 0x0100, MiimString = 0x0040;
    private const uint MnsNotifyByPos = 0x08000000, MimStyle = 0x00000010;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpAsyncWindowPos = 0x4000;
    private const uint WpfAsyncWindowPlacement = 0x0004;
    private const int MaxMenuItems = 128, MaxMenuDepth = 4;
    private const long MenuInitBudgetMs = 500;
    private static readonly ConcurrentDictionary<(IntPtr Hwnd, uint Pid), WindowCommands> ReadingOwners = new();

    private readonly IntPtr hwnd;
    private readonly uint processId;
    private readonly object gate = new();
    private WINDOWPLACEMENT? originalPlacement;
    private Dictionary<string, MenuCommand>? menuCommands;
    private string menuSnapshot = string.Empty;
    private bool disposed;

    public WindowCommands(IntPtr hwnd, uint expectedProcessId = 0)
    {
        this.hwnd = hwnd;
        if (!TryGetProcessId(hwnd, out uint actual) || expectedProcessId != 0 && actual != expectedProcessId)
            throw new InvalidOperationException("A janela autorizada não está mais disponível.");
        processId = actual;
    }

    public RemoteWindowState State
    {
        get
        {
            lock (gate)
            {
                bool valid = IsTargetValid();
                return new(hwnd.ToInt64(), valid && IsIconic(hwnd), valid && IsZoomed(hwnd), originalPlacement.HasValue, valid && CanResizeCore());
            }
        }
    }

    public RemoteWindowCommandResult Execute(JsonElement command)
    {
        string action = command.TryGetProperty("action", out JsonElement actionElement) && actionElement.ValueKind == JsonValueKind.String
            ? actionElement.GetString() ?? "" : "";
        lock (gate)
        {
            try
            {
                EnsureTarget();
                switch (action)
                {
                    case "minimize":
                        Ensure(ShowAndVerify(SwMinimize, IsIconic), "Windows recusou minimizar a janela.");
                        break;
                    case "maximize":
                    case "restore":
                        Ensure(RestoreOriginalPlacement(), "Não foi possível restaurar o tamanho original antes da ação.");
                        Ensure(ShowAndVerify(action == "maximize" ? SwMaximize : SwRestore, action == "maximize" ? IsZoomed : window => !IsIconic(window) && !IsZoomed(window)), "Windows recusou alterar o estado da janela.");
                        break;
                    case "close":
                        Ensure(RestoreOriginalPlacement(), "Não foi possível restaurar o tamanho original antes de fechar.");
                        Ensure(PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero), "Windows não aceitou o pedido de fechamento.");
                        break;
                    case "reading":
                        if (!command.TryGetProperty("enabled", out JsonElement enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            return Result(action, false, "Estado de leitura inválido.");
                        if (enabled.GetBoolean())
                        {
                            if (!command.TryGetProperty("width", out JsonElement width) || !command.TryGetProperty("height", out JsonElement height)
                                || width.ValueKind != JsonValueKind.Number || height.ValueKind != JsonValueKind.Number
                                || !width.TryGetInt32(out int w) || !height.TryGetInt32(out int h) || w <= 0 || h <= 0)
                                return Result(action, false, "Tamanho solicitado inválido.");
                            bool acquiredHere = false;
                            if (!originalPlacement.HasValue)
                            {
                                if (!CanResizeCore()) return Result(action, false, "Esta janela não permite redimensionamento.");
                                if (!GetPlacement(hwnd, out WINDOWPLACEMENT placement)) return Result(action, false, "Não foi possível guardar a geometria original.");
                                if (!ReadingOwners.TryAdd((hwnd, processId), this)) return Result(action, false, "Outra sessão já está redimensionando esta janela.");
                                originalPlacement = placement;
                                acquiredHere = true;
                            }
                            if (IsZoomed(hwnd) && !ShowAndVerify(SwRestore, window => !IsZoomed(window) && !IsIconic(window)))
                            {
                                if (acquiredHere) _ = RestoreOriginalPlacement();
                                return Result(action, false, "Windows não restaurou a janela para redimensionar.");
                            }
                            if (!ResizeClientArea(w, h))
                            {
                                if (acquiredHere) _ = RestoreOriginalPlacement();
                                return Result(action, false, "Windows recusou o tamanho solicitado.");
                            }
                        }
                        else Ensure(RestoreOriginalPlacement(), "Não foi possível restaurar a geometria original.");
                        break;
                    case "state":
                        break;
                    default:
                        return Result(action, false, "Comando de janela desconhecido.");
                }
                return Result(action, true, "OK");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                return Result(action, false, ex is InvalidOperationException ? ex.Message : "Falha ao controlar a janela.");
            }
        }
    }

    public RemoteWindowMenus GetMenus()
    {
        lock (gate)
        {
            menuCommands = null;
            menuSnapshot = Guid.NewGuid().ToString("N");
            if (!IsTargetValid()) return new(false, [], "Janela indisponível.");
            IntPtr menu = GetMenu(hwnd);
            if (menu == IntPtr.Zero) return new(false, [], "O aplicativo não expõe um menu Win32 padrão.");
            var commands = new Dictionary<string, MenuCommand>();
            int count = 0;
            try
            {
                if (!InitializeMenu(menu)) return new(false, [], "O aplicativo não respondeu ao menu dentro do prazo.");
                IReadOnlyList<RemoteWindowMenuItem> roots = ReadMenu(menu, 0, true, commands, ref count);
                menuCommands = commands;
                return new(true, roots);
            }
            catch (NotSupportedException ex) { return new(false, [], ex.Message); }
            catch { return new(false, [], "Não foi possível ler o menu Win32 com segurança."); }
        }
    }

    public RemoteWindowCommandResult InvokeMenu(string id)
    {
        lock (gate)
        {
            if (!IsTargetValid()) return new("menu", false, "Janela indisponível.");
            if (id.Length > 64 || menuCommands is null || !id.StartsWith(menuSnapshot + ":", StringComparison.Ordinal) || !menuCommands.TryGetValue(id, out MenuCommand? command))
                return new("menu", false, "Este item de menu expirou; atualize a lista.");
            if (!command.Enabled || command.Separator) return new("menu", false, "Este item de menu não está habilitado.");
            var current = new MENUITEMINFO { Size = (uint)Marshal.SizeOf<MENUITEMINFO>(), Mask = MiimState | MiimId | MiimSubMenu | MiimFType };
            if (!IsMenuAttachedAndEnabled(command.Menu, out bool ancestorsEnabled) || !ancestorsEnabled
                || !GetMenuItemInfo(command.Menu, command.Position, true, ref current)
                || current.Id != command.CommandId || current.SubMenu != IntPtr.Zero
                || (current.State & MfsDisabled) != 0 || (current.Type & (MftSeparator | MftOwnerDraw)) != 0)
                return new("menu", false, "Este item de menu mudou; atualize a lista.");
            uint message = command.NotifyByPosition ? WmMenuCommand : WmCommand;
            UIntPtr wParam = command.NotifyByPosition ? (UIntPtr)command.Position : (UIntPtr)command.CommandId;
            IntPtr lParam = command.NotifyByPosition ? command.Menu : IntPtr.Zero;
            bool success = PostMessage(hwnd, message, (IntPtr)wParam, lParam);
            return new("menu", success, success ? "OK" : "O Windows recusou o comando de menu.");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            try { _ = RestoreOriginalPlacement(); }
            finally
            {
                originalPlacement = null;
                ReadingOwners.TryRemove(new KeyValuePair<(IntPtr Hwnd, uint Pid), WindowCommands>((hwnd, processId), this));
            }
            disposed = true;
            menuCommands = null;
        }
    }

    private IReadOnlyList<RemoteWindowMenuItem> ReadMenu(IntPtr menu, int depth, bool ancestorsEnabled, Dictionary<string, MenuCommand> commands, ref int count)
    {
        if (depth >= MaxMenuDepth) throw new NotSupportedException("O menu excede a profundidade suportada.");
        var info = new MENUINFO { Size = (uint)Marshal.SizeOf<MENUINFO>(), Mask = MimStyle };
        if (!GetMenuInfo(menu, ref info)) info.Style = 0;
        bool notifyByPosition = (info.Style & MnsNotifyByPos) != 0;
        int itemCount = GetMenuItemCount(menu);
        if (itemCount < 0 || itemCount > MaxMenuItems - count) throw new NotSupportedException("O menu excede o limite de itens suportado.");
        var items = new List<RemoteWindowMenuItem>(itemCount);
        for (uint position = 0; position < itemCount; position++)
        {
            var native = new MENUITEMINFO { Size = (uint)Marshal.SizeOf<MENUITEMINFO>(), Mask = MiimState | MiimId | MiimSubMenu | MiimFType };
            if (!GetMenuItemInfo(menu, position, true, ref native)) continue;
            if ((native.Type & MftOwnerDraw) != 0) throw new NotSupportedException("O menu contém itens desenhados pelo aplicativo.");
            var title = new char[257];
            native.TypeData = Marshal.AllocHGlobal(title.Length * sizeof(char));
            native.Mask |= MiimString;
            native.Cch = (uint)title.Length;
            try
            {
                Marshal.Copy(title, 0, native.TypeData, title.Length);
                if (!GetMenuItemInfo(menu, position, true, ref native)) { native.Cch = 0; }
                Marshal.Copy(native.TypeData, title, 0, title.Length);
            }
            finally { Marshal.FreeHGlobal(native.TypeData); }
            count++;
            bool separator = (native.Type & MftSeparator) != 0;
            string label = NormalizeMenuText(new string(title, 0, (int)Math.Min(native.Cch, (uint)title.Length)).TrimEnd('\0'));
            bool enabled = ancestorsEnabled && (native.State & MfsDisabled) == 0 && !separator;
            var children = native.SubMenu == IntPtr.Zero ? [] : ReadMenu(native.SubMenu, depth + 1, enabled, commands, ref count);
            string id = native.SubMenu == IntPtr.Zero ? menuSnapshot + ":" + commands.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            if (id.Length != 0) commands.Add(id, new(menu, position, native.Id, enabled, separator, notifyByPosition));
            items.Add(new(id, label, enabled, (native.State & MfsChecked) != 0, separator, children));
        }
        return items;
    }

    private bool InitializeMenu(IntPtr menu)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(MenuInitBudgetMs / 1000d * Stopwatch.Frequency);
        if (!SendInitMessage(WmInitMenu, menu, IntPtr.Zero, deadline)) return false;
        int visited = 0;
        return InitializeSubmenus(menu, 0, deadline, ref visited);
    }

    private bool InitializeSubmenus(IntPtr menu, int depth, long deadline, ref int visited)
    {
        if (depth >= MaxMenuDepth - 1) return true;
        int count = GetMenuItemCount(menu);
        if (count < 0 || count > MaxMenuItems - visited)
            throw new NotSupportedException("O menu excede o limite de itens suportado.");
        visited += count;
        for (uint i = 0; i < count; i++)
        {
            IntPtr child = GetSubMenu(menu, (int)i);
            if (child == IntPtr.Zero) continue;
            if (!SendInitMessage(WmInitMenuPopup, child, (IntPtr)i, deadline)
                || !InitializeSubmenus(child, depth + 1, deadline, ref visited)) return false;
        }
        return true;
    }

    private bool SendInitMessage(uint message, IntPtr wParam, IntPtr lParam, long deadline)
    {
        long remaining = (deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
        if (remaining <= 0) return false;
        UIntPtr result;
        return SendMessageTimeout(hwnd, message, wParam, lParam, SmtoBlock | SmtoAbortIfHung, (uint)Math.Min(remaining, 100), out result) != IntPtr.Zero;
    }

    private bool IsMenuAttachedAndEnabled(IntPtr candidate, out bool ancestorsEnabled)
    {
        int visited = 0;
        return FindMenuPath(GetMenu(hwnd), candidate, 0, true, ref visited, out ancestorsEnabled);
    }

    private static bool FindMenuPath(IntPtr menu, IntPtr candidate, int depth, bool enabledPath, ref int visited, out bool ancestorsEnabled)
    {
        ancestorsEnabled = false;
        if (menu == IntPtr.Zero || depth >= MaxMenuDepth) return false;
        if (menu == candidate)
        {
            ancestorsEnabled = enabledPath;
            return true;
        }
        int count = GetMenuItemCount(menu);
        if (count < 0 || count > MaxMenuItems - visited) return false;
        visited += count;
        for (uint position = 0; position < count; position++)
        {
            IntPtr child = GetSubMenu(menu, (int)position);
            if (child == IntPtr.Zero) continue;
            var item = new MENUITEMINFO { Size = (uint)Marshal.SizeOf<MENUITEMINFO>(), Mask = MiimState | MiimSubMenu | MiimFType };
            if (!GetMenuItemInfo(menu, position, true, ref item) || item.SubMenu != child) continue;
            bool itemEnabled = enabledPath && (item.State & MfsDisabled) == 0 && (item.Type & (MftSeparator | MftOwnerDraw)) == 0;
            if (FindMenuPath(child, candidate, depth + 1, itemEnabled, ref visited, out ancestorsEnabled)) return true;
        }
        return false;
    }

    private bool ResizeClientArea(int width, int height)
    {
        if (!originalPlacement.HasValue || !CanResizeCore() || IsIconic(hwnd) || width <= 0 || height <= 0) return false;
        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) return false;
        width = (int)Math.Clamp(Math.Round(width * (dpi / 96d)), 1d, 32768d);
        height = (int)Math.Clamp(Math.Round(height * (dpi / 96d)), 1d, 32768d);
        uint style = unchecked((uint)GetWindowLongPtr(hwnd, GwlStyle).ToInt64());
        uint exStyle = unchecked((uint)GetWindowLongPtr(hwnd, GwlExStyle).ToInt64());
        var frame = new RECT { Right = width, Bottom = height };
        if (!AdjustWindowRectExForDpi(ref frame, style, GetMenu(hwnd) != IntPtr.Zero, exStyle, dpi)) return false;
        int outerWidth = frame.Right - frame.Left, outerHeight = frame.Bottom - frame.Top;
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var monitorInfo = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return false;
        int maxOuterWidth = monitorInfo.Work.Right - monitorInfo.Work.Left;
        int maxOuterHeight = monitorInfo.Work.Bottom - monitorInfo.Work.Top;
        outerWidth = Math.Min(outerWidth, maxOuterWidth);
        outerHeight = Math.Min(outerHeight, maxOuterHeight);
        if (!GetWindowRect(hwnd, out RECT current)) return false;
        int x = Math.Clamp(current.Left, monitorInfo.Work.Left, monitorInfo.Work.Right - outerWidth);
        int y = Math.Clamp(current.Top, monitorInfo.Work.Top, monitorInfo.Work.Bottom - outerHeight);
        return SetWindowPos(hwnd, IntPtr.Zero, x, y, outerWidth, outerHeight, SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos);
    }

    private bool RestoreOriginalPlacement()
    {
        if (!originalPlacement.HasValue) return true;
        bool restored = !IsTargetValid();
        if (IsTargetValid())
        {
            WINDOWPLACEMENT placement = originalPlacement.Value;
            placement.Flags |= WpfAsyncWindowPlacement;
            restored = SetWindowPlacement(hwnd, ref placement);
        }
        if (!restored) return false;
        originalPlacement = null;
        ReadingOwners.TryRemove(new KeyValuePair<(IntPtr Hwnd, uint Pid), WindowCommands>((hwnd, processId), this));
        return true;
    }

    private bool CanResizeCore() => (unchecked((uint)GetWindowLongPtr(hwnd, GwlStyle).ToInt64()) & WsThickFrame) != 0;
    private bool IsTargetValid() => TryGetProcessId(hwnd, out uint actual) && actual == processId;
    private void EnsureTarget() { if (disposed || !IsTargetValid()) throw new InvalidOperationException("A janela autorizada não está mais disponível."); }
    private static RemoteWindowCommandResult Result(string action, bool success, string message) => new(action, success, message);
    private static void Ensure(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private bool ShowAndVerify(uint command, Func<IntPtr, bool> expected)
    {
        _ = ShowWindowAsync(hwnd, command);
        for (int i = 0; i < 25; i++)
        {
            if (expected(hwnd)) return true;
            Thread.Sleep(10);
        }
        return expected(hwnd);
    }

    private static bool TryGetProcessId(IntPtr window, out uint pid)
    {
        pid = 0;
        return window != IntPtr.Zero && IsWindow(window) && GetWindowThreadProcessId(window, out pid) != 0 && pid != 0;
    }

    private static bool GetPlacement(IntPtr window, out WINDOWPLACEMENT placement)
    {
        placement = new WINDOWPLACEMENT { Length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        return GetWindowPlacement(window, ref placement);
    }

    private static IntPtr GetWindowLongPtr(IntPtr window, int index) => IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : new IntPtr(GetWindowLong32(window, index));
    internal static string NormalizeMenuText(string text)
    {
        var label = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '&') { label.Append(text[i]); continue; }
            if (i + 1 < text.Length && text[i + 1] == '&') { label.Append('&'); i++; }
            else if (i + 1 == text.Length) label.Append('&');
        }
        return label.ToString();
    }
    private sealed record MenuCommand(IntPtr Menu, uint Position, uint CommandId, bool Enabled, bool Separator, bool NotifyByPosition);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WINDOWPLACEMENT { public uint Length, Flags, ShowCmd; public POINT MinPosition, MaxPosition; public RECT NormalPosition; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MENUITEMINFO { public uint Size, Mask, Type, State, Id; public IntPtr SubMenu, CheckedBitmap, UncheckedBitmap, ItemData, TypeData; public uint Cch; public IntPtr ItemBitmap; }
    [StructLayout(LayoutKind.Sequential)] private struct MENUINFO { public uint Size, Mask, Style, MaxHeight; public IntPtr Background; public uint ContextHelpId; public UIntPtr MenuData; }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindowAsync(IntPtr hwnd, uint command);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AdjustWindowRectExForDpi(ref RECT rect, uint style, bool menu, uint exStyle, uint dpi);
    [DllImport("user32.dll")] private static extern IntPtr GetMenu(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr menu, int position);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMenuItemInfo(IntPtr menu, uint item, bool byPosition, ref MENUITEMINFO info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetMenuInfo(IntPtr menu, ref MENUINFO info);
}
