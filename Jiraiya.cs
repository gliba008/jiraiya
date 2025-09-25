// .NET 6/7/8 Console – Jiraiya for Windows 11 (all monitors)
// Listens for window show/hide/move/minimize/maximize events and tiles candidates on all monitors
// Fixed three-slot grid layout per monitor:
//   A: 50% width, 100% height (left half)
//   B: 50% width, 50% height (right half, top)
//   C: 50% width, 50% height (right half, bottom)
// Window assignment rules per monitor:
//   1 window → fills entire work area (A+B+C)
//   2 windows → w1 → A, w2 → B+C (right half entire height)
//   3 windows → w1 → A, w2 → B, w3 → C
//   >3 windows → same as 3 windows, extra windows stack into slot C
// Each monitor is managed independently. Logs actions to console.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Timers;
using System.Windows.Forms;
using Microsoft.Win32;

class Program
{
    // --- Config ---
    const int DefaultDebounceMs = 120; // fallback debounce delay if config fails early

    // WinEvent hooks
    static IntPtr _hookShowHide = IntPtr.Zero;
    static IntPtr _hookForeground = IntPtr.Zero;
    static IntPtr _hookMinMax = IntPtr.Zero;

    // Per-monitor tracking
    static readonly Dictionary<IntPtr, HashSet<IntPtr>> _candidatesPerMonitor = new();
    static readonly Dictionary<IntPtr, RECT> _monitorWorkAreas = new();
    static readonly Dictionary<IntPtr, List<IntPtr>> _orderedPerMonitor = new();
    static readonly List<IntPtr> _allMonitors = new();
    static IntPtr _focusedWindow = IntPtr.Zero;
    static uint _accentArgb = 0xFF3388FF; // default fallback (ARGB)
    static bool _isActive = true;

    static readonly System.Timers.Timer _debounce = new(DefaultDebounceMs) { AutoReset = false, Enabled = false };
    static Form? _hiddenForm;
    static IntPtr _keyboardHook = IntPtr.Zero;
    static LowLevelKeyboardProc? _keyboardProc;
    static IntPtr _hookMoveSize = IntPtr.Zero;
    static bool _winPressed;
    static bool _shiftPressed;
    static bool _altPressed;
    static bool _altComboInjected;
    static bool _suppressMoveEvents;
    static readonly HashSet<int> _swallowedKeys = new();
    static readonly HashSet<IntPtr> _programmaticMoveWindows = new();
    static IntPtr _draggingWindow = IntPtr.Zero;
    static int _activeWinKeyCode;
    static Icon? _appIcon;
    static readonly string _frogIconPath = Path.Combine(AppContext.BaseDirectory, "frog.ico");
    static readonly Dictionary<IntPtr, FullscreenToggleState> _fullscreenToggles = new();
    static AppConfiguration _config = null!;
    static readonly HashSet<IntPtr> _ignoredWindows = new();
    static readonly HashSet<IntPtr> _centeredIgnoredWindows = new();
    static readonly Dictionary<uint, string?> _processPathCache = new();

    enum GridSlot { A, B, C }
    enum Direction { Left, Right, Up, Down }
    enum IgnoreReason { None, ListedApp, Dialog }

    sealed class FullscreenToggleState
    {
        public FullscreenToggleState(IntPtr monitor, Dictionary<IntPtr, RECT> windowRects, List<IntPtr> orderedHandles)
        {
            Monitor = monitor;
            WindowRects = windowRects;
            OrderedHandles = orderedHandles;
        }

        public IntPtr Monitor { get; }
        public Dictionary<IntPtr, RECT> WindowRects { get; }
        public List<IntPtr> OrderedHandles { get; }
    }

    sealed class AppConfiguration
    {
        [JsonPropertyName("ignore_apps")]
        public string[]? IgnoreAppsRaw { get; set; }

        [JsonPropertyName("ignore_dialogs")]
        public bool? IgnoreDialogsRaw { get; set; }

        [JsonPropertyName("center_ignored_windows")]
        public bool? CenterIgnoredWindowsRaw { get; set; }

        [JsonPropertyName("debounce_in_ms")]
        public int? DebounceInMsRaw { get; set; }

        string[] _ignoreApps = Array.Empty<string>();

        public IReadOnlyList<string> IgnoreApps => _ignoreApps;
        public bool IgnoreDialogs { get; private set; }
        public bool CenterIgnoredWindows { get; private set; }
        public int DebounceInMs { get; private set; }

        public void Validate()
        {
            if (IgnoreAppsRaw is null)
            {
                throw new InvalidOperationException("Configuration value 'ignore_apps' is missing.");
            }
            if (IgnoreDialogsRaw is null)
            {
                throw new InvalidOperationException("Configuration value 'ignore_dialogs' is missing.");
            }
            if (CenterIgnoredWindowsRaw is null)
            {
                throw new InvalidOperationException("Configuration value 'center_ignored_windows' is missing.");
            }
            if (DebounceInMsRaw is null)
            {
                throw new InvalidOperationException("Configuration value 'debounce_in_ms' is missing.");
            }

            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            List<string> cleaned = new();
            foreach (var entry in IgnoreAppsRaw)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                string normalized = entry.Trim();
                if (seen.Add(normalized))
                {
                    cleaned.Add(normalized);
                }
            }

            _ignoreApps = cleaned.ToArray();
            IgnoreDialogs = IgnoreDialogsRaw.Value;
            CenterIgnoredWindows = CenterIgnoredWindowsRaw.Value;
            int debounce = DebounceInMsRaw.Value;
            if (debounce <= 0)
            {
                throw new InvalidOperationException("Configuration value 'debounce_in_ms' must be greater than 0.");
            }
            DebounceInMs = debounce;
        }
    }

    // DRŽI delegata u static polju (+ pravilna konvencija poziva)
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime
    );
    static WinEventDelegate _cb = WinEventCallback;

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Jiraiya - multi-monitor window tiling\n");

        try
        {
            _config = LoadConfiguration();
            _debounce.Interval = _config.DebounceInMs;
            Console.WriteLine($"[i] Configuration loaded (ignore_apps={_config.IgnoreApps.Count}, ignore_dialogs={_config.IgnoreDialogs}, center_ignored_windows={_config.CenterIgnoredWindows}, debounce_in_ms={_config.DebounceInMs})");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[!] Configuration error: " + ex.Message);
            Environment.Exit(1);
            return;
        }

        if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
        {
            Console.WriteLine("[!] SetProcessDpiAwarenessContext failed; coordinate scaling may be incorrect.");
        }

        LoadAppIcon();

        if (!DiscoverAllMonitors())
        {
            Console.WriteLine("[!] No monitors detected. Exiting.");
            return;
        }

        Console.WriteLine($"[i] Managing {_allMonitors.Count} monitor(s)");
        foreach (var mon in _allMonitors)
        {
            var work = _monitorWorkAreas[mon];
            Console.WriteLine($"    Monitor: work=({work.left},{work.top})→({work.right},{work.bottom})");
        }

        // initial scan
        ScanAllCandidates();
        ApplyAllLayouts();

        LoadAccentColor();
        InstallKeyboardHook();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        HandleForegroundChanged(GetForegroundWindow());

        _debounce.Elapsed += (_, __) => ApplyAllLayouts();

        // set hooks with proper flags for better performance
        const uint hookFlags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        _hookShowHide = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE, IntPtr.Zero, _cb, 0, 0, hookFlags);
        _hookForeground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _cb, 0, 0, hookFlags);
        _hookMinMax = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _cb, 0, 0, hookFlags);
        _hookMoveSize = SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _cb, 0, 0, hookFlags);

        Console.WriteLine($"[i] Hooks: show/hide=0x{_hookShowHide.ToInt64():X}, fg=0x{_hookForeground.ToInt64():X}, minmax=0x{_hookMinMax.ToInt64():X}, move=0x{_hookMoveSize.ToInt64():X}");
        if (_hookShowHide == IntPtr.Zero || _hookForeground == IntPtr.Zero || _hookMinMax == IntPtr.Zero || _hookMoveSize == IntPtr.Zero)
        {
            Console.WriteLine("[!] At least one hook failed to register (IntPtr.Zero). Check signatures and UAC.");
        }

        Console.WriteLine("[i] Hooks active. Press Ctrl+C to exit.");

        // Setup console cancel handler for clean shutdown
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n[i] Shutting down...");
            CleanupHooks();
            DisposeAppIcon();
            if (_hiddenForm != null)
            {
                _hiddenForm.Invoke(new Action(() => Application.Exit()));
            }
            else
            {
                Application.Exit();
            }
        };

        // Use proper Windows message loop instead of Thread.Sleep
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        
        // Create invisible form to handle message loop
        _hiddenForm = new Form()
        {
            WindowState = FormWindowState.Minimized,
            ShowInTaskbar = false,
            Visible = false
        };
        if (_appIcon != null)
        {
            _hiddenForm.Icon = _appIcon;
        }
        _hiddenForm.FormClosed += (_, __) => DisposeAppIcon();
        _hiddenForm.Shown += (_, __) => ShowStatusToast(true);

        GC.KeepAlive(_cb); // drži delegata živim
        Application.Run(_hiddenForm);
    }

    static void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Invoke on UI thread to avoid threading issues
        if (_hiddenForm != null && _hiddenForm.InvokeRequired)
        {
            _hiddenForm.BeginInvoke(new Action(() => ProcessWinEvent(eventType, hwnd, idObject, idChild)));
        }
        else
        {
            ProcessWinEvent(eventType, hwnd, idObject, idChild);
        }
    }

    static void ProcessWinEvent(uint eventType, IntPtr hwnd, int idObject, int idChild)
    {
        Console.WriteLine($"CALLBACK event=0x{eventType:X} hwnd=0x{hwnd.ToInt64():X}");

        if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW)
        {
            return;
        }

        switch (eventType)
        {
            case EVENT_OBJECT_SHOW:
            case EVENT_OBJECT_HIDE:
            case EVENT_SYSTEM_MINIMIZESTART: // window minimized
            case EVENT_SYSTEM_MINIMIZEEND: // window restored from minimized
                ScanAllCandidates();
                Debounce();
                break;
            case EVENT_SYSTEM_FOREGROUND:
                HandleForegroundChanged(hwnd);
                break;
            case EVENT_SYSTEM_MOVESIZESTART:
                HandleMoveSizeStart(hwnd);
                break;
            case EVENT_SYSTEM_MOVESIZEEND:
                HandleMoveSizeEnd(hwnd);
                break;
        }
    }

    static void Debounce()
    {
        try { _debounce.Stop(); _debounce.Start(); } catch { }
    }

    static void ScanAllCandidates()
    {
        var allWindows = new List<IntPtr>();
        _ = EnumWindows((IntPtr h, IntPtr l) => { allWindows.Add(h); return true; }, IntPtr.Zero);

        // Reverse so we process bottom-to-top which better matches taskbar ordering
        allWindows.Reverse();

        var enumeratedHandles = new HashSet<IntPtr>(allWindows);
        var perMonitorCandidates = new Dictionary<IntPtr, HashSet<IntPtr>>();
        var perMonitorOrder = new Dictionary<IntPtr, List<IntPtr>>();
        var seenWindows = new HashSet<IntPtr>();

        foreach (var h in allWindows)
        {
            if (!IsWindowValidForLayout(h)) continue;

            var monitor = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) continue;

            EnsureMonitorStructures(monitor);

            if (!perMonitorCandidates.TryGetValue(monitor, out var set))
            {
                set = new HashSet<IntPtr>();
                perMonitorCandidates[monitor] = set;
            }
            if (!perMonitorOrder.TryGetValue(monitor, out var list))
            {
                list = new List<IntPtr>();
                perMonitorOrder[monitor] = list;
            }

            set.Add(h);
            list.Add(h);
            seenWindows.Add(h);
        }

        foreach (var monitor in _allMonitors.ToList())
        {
            var candidates = perMonitorCandidates.TryGetValue(monitor, out var set) ? set : new HashSet<IntPtr>();
            var orderedFromTaskbar = perMonitorOrder.TryGetValue(monitor, out var list) ? list : new List<IntPtr>();

            _candidatesPerMonitor[monitor] = candidates;

            if (MonitorHasFullscreenWindow(monitor))
            {
                Console.WriteLine($"    [!] fullscreen active on monitor {monitor.ToInt64():X}, preserving order");
                continue;
            }

            var existingOrder = _orderedPerMonitor.TryGetValue(monitor, out var existing) ? existing : new List<IntPtr>();
            var merged = new List<IntPtr>();

            foreach (var win in existingOrder)
            {
                if (orderedFromTaskbar.Contains(win))
                {
                    merged.Add(win);
                }
            }

            foreach (var win in orderedFromTaskbar)
            {
                if (!merged.Contains(win))
                {
                    merged.Add(win);
                }
            }

            _orderedPerMonitor[monitor] = merged;
        }

        if (_focusedWindow != IntPtr.Zero && !seenWindows.Contains(_focusedWindow))
        {
            ClearFocusHighlight();
            _focusedWindow = IntPtr.Zero;
        }

        foreach (var hwnd in _programmaticMoveWindows.Where(h => !seenWindows.Contains(h)).ToList())
        {
            _programmaticMoveWindows.Remove(hwnd);
        }

        foreach (var kvp in _fullscreenToggles.ToList())
        {
            if (!IsWindow(kvp.Key))
            {
                _fullscreenToggles.Remove(kvp.Key);
                continue;
            }

            var state = kvp.Value;
            foreach (var stale in state.WindowRects.Keys.Where(h => !IsWindow(h)).ToList())
            {
                state.WindowRects.Remove(stale);
            }
            state.OrderedHandles.RemoveAll(h => !IsWindow(h));
        }

        foreach (var staleIgnored in _ignoredWindows.Where(h => !enumeratedHandles.Contains(h)).ToList())
        {
            RemoveIgnoredTracking(staleIgnored);
        }
    }

    static void ApplyAllLayouts()
    {
        if (!_isActive) return;
        foreach (var monitor in _allMonitors)
        {
            ApplyLayoutForMonitor(monitor);
        }
    }

    static void ApplyLayoutForMonitor(IntPtr monitor)
    {
        EnsureMonitorStructures(monitor);
        bool previousSuppress = _suppressMoveEvents;
        _suppressMoveEvents = true;
        try
        {
            // Re-evaluate work area for this monitor
            MONITORINFO mi = new() { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref mi))
            {
                _monitorWorkAreas[monitor] = mi.rcWork;
            }

            if (!_monitorWorkAreas.TryGetValue(monitor, out var workArea))
            {
                return;
            }

            var candidates = _candidatesPerMonitor.TryGetValue(monitor, out var currentCandidates)
                ? currentCandidates
                : new HashSet<IntPtr>();
            if (!_candidatesPerMonitor.ContainsKey(monitor))
            {
                _candidatesPerMonitor[monitor] = candidates;
            }

            if (!_orderedPerMonitor.TryGetValue(monitor, out var ordered))
            {
                ordered = new List<IntPtr>();
                _orderedPerMonitor[monitor] = ordered;
            }

            // Ensure all candidates are represented in ordered list
            foreach (var candidate in candidates)
            {
                if (!ordered.Contains(candidate))
                {
                    ordered.Add(candidate);
                }
            }

            ordered = ordered.Where(candidates.Contains)
                             .Where(IsWindowValidForLayout)
                             .Where(h => MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) == monitor)
                             .Distinct()
                             .ToList();

            _orderedPerMonitor[monitor] = ordered;
            var wins = ordered;

            int count = wins.Count;
            Console.WriteLine($"[∴] Monitor layout: {count} window(s)");
            if (count == 0) return;

            if (MonitorHasFullscreenWindow(monitor))
            {
                Console.WriteLine("    ↳ fullscreen detected, skipping layout");
                ReapplyFocusHighlight();
                return;
            }

            var rects = CalculateStableGrid(workArea, count);
            for (int i = 0; i < count; i++)
            {
                string label = GetSlotLabel(count, i);
                SetToRect(wins[i], rects[i], label);
            }

            ReapplyFocusHighlight();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[!] Layout error: " + ex.Message);
        }
        finally
        {
            _suppressMoveEvents = previousSuppress;
        }
    }

    static bool IsWindowValidForLayout(IntPtr hwnd)
    {
        if (!PassesBasicWindowChecks(hwnd, out long style, out long exStyle))
        {
            RemoveIgnoredTracking(hwnd);
            return false;
        }

        var ignoreReason = GetIgnoreReason(hwnd, style, exStyle);
        if (ignoreReason != IgnoreReason.None)
        {
            HandleIgnoredWindow(hwnd, ignoreReason);
            return false;
        }

        if (!IsLayoutStyleEligible(style, exStyle))
        {
            RemoveIgnoredTracking(hwnd);
            return false;
        }

        RemoveIgnoredTracking(hwnd);
        return true;
    }

    static bool PassesBasicWindowChecks(IntPtr hwnd, out long style, out long exStyle)
    {
        style = 0;
        exStyle = 0;

        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
        {
            return false;
        }

        style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();

        // exclude cloaked (UWP)
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        if (GetWindowRect(hwnd, out RECT windowRect))
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                MONITORINFO mi = new() { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref mi))
                {
                    if (IsFullscreenRect(windowRect, mi.rcMonitor))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    static bool IsLayoutStyleEligible(long style, long exStyle)
    {
        const long WS_OVERLAPPEDWINDOW = 0x00CF0000;
        const long WS_EX_TOOLWINDOW = 0x00000080;

        if ((exStyle & WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        if ((style & WS_OVERLAPPEDWINDOW) == 0)
        {
            return false;
        }

        return true;
    }

    static IgnoreReason GetIgnoreReason(IntPtr hwnd, long style, long exStyle)
    {
        if (IsIgnoredApplicationWindow(hwnd))
        {
            return IgnoreReason.ListedApp;
        }

        if (_config.IgnoreDialogs && IsDialogWindow(hwnd, style, exStyle))
        {
            return IgnoreReason.Dialog;
        }

        return IgnoreReason.None;
    }

    static bool IsIgnoredApplicationWindow(IntPtr hwnd)
    {
        if (_config.IgnoreApps.Count == 0)
        {
            return false;
        }

        uint pid = GetWindowProcessId(hwnd);
        if (pid == 0)
        {
            return false;
        }

        string? exePath = TryGetProcessImagePath(pid);
        string? exeFileName = null;

        if (!string.IsNullOrEmpty(exePath))
        {
            exeFileName = Path.GetFileName(exePath);
        }
        else
        {
            try
            {
                using Process proc = Process.GetProcessById((int)pid);
                if (!string.IsNullOrEmpty(proc.ProcessName))
                {
                    exeFileName = proc.ProcessName + ".exe";
                }
            }
            catch
            {
                // ignored – process may have exited or access denied
            }
        }

        foreach (var entry in _config.IgnoreApps)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;

            if (!string.IsNullOrEmpty(exePath) && string.Equals(exePath, entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var entryName = Path.GetFileName(entry);
            if (!string.IsNullOrEmpty(entryName) && !string.IsNullOrEmpty(exeFileName) &&
                string.Equals(entryName, exeFileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    static bool IsDialogWindow(IntPtr hwnd, long style, long exStyle)
    {
        const long WS_POPUP = unchecked((long)0x80000000);
        const long WS_THICKFRAME = 0x00040000;
        const long WS_DLGFRAME = 0x00400000;
        const long WS_MINIMIZEBOX = 0x00020000;
        const long WS_MAXIMIZEBOX = 0x00010000;
        const long WS_CAPTION = 0x00C00000;
        const long WS_EX_DLGMODALFRAME = 0x00000001;

        var classNameBuilder = new StringBuilder(256);
        bool classIsDialog = GetClassName(hwnd, classNameBuilder, classNameBuilder.Capacity) > 0 &&
                             classNameBuilder.ToString().Equals("#32770", StringComparison.Ordinal);

        if (classIsDialog)
        {
            return true;
        }

        if ((exStyle & WS_EX_DLGMODALFRAME) != 0)
        {
            return true;
        }

        bool lacksResizeBorder = (style & WS_THICKFRAME) == 0;
        bool lacksMinimize = (style & WS_MINIMIZEBOX) == 0;
        bool lacksMaximize = (style & WS_MAXIMIZEBOX) == 0;
        bool hasDlgFrame = (style & WS_DLGFRAME) != 0;
        bool hasCaption = (style & WS_CAPTION) != 0;
        bool isPopup = (style & WS_POPUP) != 0;

        if (hasDlgFrame && lacksResizeBorder)
        {
            return true;
        }

        if (lacksResizeBorder && lacksMinimize && lacksMaximize && hasCaption)
        {
            return true;
        }

        if (isPopup && lacksResizeBorder && lacksMinimize && lacksMaximize)
        {
            return true;
        }

        return false;
    }

    static void HandleIgnoredWindow(IntPtr hwnd, IgnoreReason reason)
    {
        bool firstObservation = _ignoredWindows.Add(hwnd);
        if (firstObservation)
        {
            Console.WriteLine($"[ign] Skipping hwnd=0x{hwnd.ToInt64():X} reason={reason}{DescribeWindowForLogs(hwnd)}");
        }

        if (_config.CenterIgnoredWindows && _centeredIgnoredWindows.Add(hwnd))
        {
            if (CenterIgnoredWindow(hwnd))
            {
                Console.WriteLine("    ↳ centered ignored window");
            }
            else
            {
                Console.WriteLine("    ↳ centering ignored window failed");
            }
        }
    }

    static void RemoveIgnoredTracking(IntPtr hwnd)
    {
        _ignoredWindows.Remove(hwnd);
        _centeredIgnoredWindows.Remove(hwnd);
    }

    static bool CenterIgnoredWindow(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT rect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        MONITORINFO info = new() { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        RECT work = info.rcWork;
        int width = rect.right - rect.left;
        int height = rect.bottom - rect.top;
        int workWidth = work.right - work.left;
        int workHeight = work.bottom - work.top;

        if (width <= 0 || height <= 0 || workWidth <= 0 || workHeight <= 0)
        {
            return false;
        }

        int x = work.left + (workWidth - width) / 2;
        int y = work.top + (workHeight - height) / 2;

        uint flags = SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_NOSIZE;
        return SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, flags);
    }

    static string DescribeWindowForLogs(IntPtr hwnd)
    {
        var builder = new StringBuilder();
        uint pid = GetWindowProcessId(hwnd);
        if (pid != 0)
        {
            builder.Append(' ');
            builder.Append("pid=");
            builder.Append(pid);
        }

        string? exe = TryGetProcessImagePath(pid);
        if (!string.IsNullOrEmpty(exe))
        {
            builder.Append(' ');
            builder.Append("exe=\"");
            builder.Append(exe);
            builder.Append('"');
        }

        string title = GetWindowTitle(hwnd);
        if (!string.IsNullOrEmpty(title))
        {
            builder.Append(' ');
            builder.Append("title=\"");
            builder.Append(title);
            builder.Append('"');
        }

        return builder.ToString();
    }

    static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        return GetWindowText(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    static uint GetWindowProcessId(IntPtr hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    static string? TryGetProcessImagePath(uint pid)
    {
        if (pid == 0)
        {
            return null;
        }

        if (_processPathCache.TryGetValue(pid, out var cached))
        {
            return cached;
        }

        IntPtr processHandle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (processHandle == IntPtr.Zero)
        {
            _processPathCache[pid] = null;
            return null;
        }

        try
        {
            int capacity = 260;
            while (true)
            {
                var pathBuilder = new StringBuilder(capacity);
                int size = pathBuilder.Capacity;
                if (QueryFullProcessImageName(processHandle, 0, pathBuilder, ref size))
                {
                    string path = pathBuilder.ToString(0, size);
                    _processPathCache[pid] = path;
                    return path;
                }

                int error = Marshal.GetLastWin32Error();
                const int ERROR_INSUFFICIENT_BUFFER = 122;
                if (error == ERROR_INSUFFICIENT_BUFFER)
                {
                    capacity *= 2;
                    continue;
                }

                break;
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }

        _processPathCache[pid] = null;
        return null;
    }

    static List<RECT> CalculateStableGrid(RECT workArea, int count)
    {
        var rects = new List<RECT>(count);
        if (count <= 0) return rects;

        var slotA = GetSlotA(workArea);
        var slotB = GetSlotB(workArea);
        var slotC = GetSlotC(workArea);
        var slotBC = MergeRects(slotB, slotC);

        if (count == 1)
        {
            rects.Add(workArea);
            return rects;
        }

        if (count == 2)
        {
            rects.Add(slotA);
            rects.Add(slotBC);
            return rects;
        }

        // count >= 3
        rects.Add(slotA);
        rects.Add(slotB);
        rects.Add(slotC);

        for (int i = 3; i < count; i++)
        {
            rects.Add(slotC);
        }

        return rects;
    }

    static RECT GetSlotA(RECT workArea)
    {
        int midX = workArea.left + ((workArea.right - workArea.left) / 2);
        return new RECT { left = workArea.left, top = workArea.top, right = midX, bottom = workArea.bottom };
    }

    static RECT GetSlotB(RECT workArea)
    {
        int midX = workArea.left + ((workArea.right - workArea.left) / 2);
        int midY = workArea.top + ((workArea.bottom - workArea.top) / 2);
        return new RECT { left = midX, top = workArea.top, right = workArea.right, bottom = midY };
    }

    static RECT GetSlotC(RECT workArea)
    {
        int midX = workArea.left + ((workArea.right - workArea.left) / 2);
        int midY = workArea.top + ((workArea.bottom - workArea.top) / 2);
        return new RECT { left = midX, top = midY, right = workArea.right, bottom = workArea.bottom };
    }

    static RECT MergeRects(RECT first, RECT second)
    {
        return new RECT
        {
            left = Math.Min(first.left, second.left),
            top = Math.Min(first.top, second.top),
            right = Math.Max(first.right, second.right),
            bottom = Math.Max(first.bottom, second.bottom)
        };
    }

    static string GetSlotLabel(int totalWindows, int index)
    {
        if (totalWindows <= 0) return "";

        if (totalWindows == 1)
        {
            return "A+B+C";
        }

        if (totalWindows == 2)
        {
            return index == 0 ? "A" : "B+C";
        }

        if (index == 0) return "A";
        if (index == 1) return "B";
        return "C";
    }

    static void SetToRect(IntPtr hwnd, RECT r, string label)
    {
        if (!IsWindow(hwnd)) return;

        if (GetWindowRect(hwnd, out RECT current) && AreRectsClose(current, r))
        {
            return;
        }

        _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        _programmaticMoveWindows.Add(hwnd);
        bool ok = SetWindowPos(hwnd, IntPtr.Zero, r.left, r.top, r.right - r.left, r.bottom - r.top,
            SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        if (!ok)
        {
            _programmaticMoveWindows.Remove(hwnd);
        }
        Console.WriteLine($"    → {label}: {(ok ? "OK" : "FAIL")} hwnd=0x{hwnd.ToInt64():X}");
    }

    static bool AreRectsClose(RECT a, RECT b)
    {
        const int Tolerance = 1;
        return Math.Abs(a.left - b.left) <= Tolerance &&
               Math.Abs(a.top - b.top) <= Tolerance &&
               Math.Abs(a.right - b.right) <= Tolerance &&
               Math.Abs(a.bottom - b.bottom) <= Tolerance;
    }

    static void LoadAccentColor()
    {
        if (TryGetExplorerAccent(out uint explorerArgb))
        {
            _accentArgb = explorerArgb | 0xFF000000;
            return;
        }

        if (DwmGetColorizationColor(out uint argb, out bool _) == 0)
        {
            _accentArgb = argb | 0xFF000000;
            return;
        }

        // default to calm blue if nothing else
        _accentArgb = 0xFF1E90FF;
    }

    static bool TryGetExplorerAccent(out uint argb)
    {
        argb = 0;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Accent");
            if (key == null) return false;

            object? value = key.GetValue("AccentColorMenu") ?? key.GetValue("AccentColor");
            if (value is int intVal)
            {
                argb = unchecked((uint)intVal);
                return true;
            }
            if (value is uint uintVal)
            {
                argb = uintVal;
                return true;
            }
        }
        catch
        {
            // ignore issues, fall back to dwm/default color
        }
        return false;
    }

    static void ToggleActive()
    {
        SetActive(!_isActive);
    }

    static void SetActive(bool active)
    {
        if (_isActive == active) return;

        _isActive = active;
        if (_isActive)
        {
            Console.WriteLine("[i] Jiraiya resumed (Win+Alt+J)");
            ScanAllCandidates();
            ApplyAllLayouts();
            ReapplyFocusHighlight();
            ShowStatusToast(true);
        }
        else
        {
            Console.WriteLine("[i] Jiraiya paused (Win+Alt+J)");
            ClearFocusHighlight();
            ShowStatusToast(false);
        }
    }

    static void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Console.WriteLine("[i] Display change detected – refreshing layout");
        DiscoverAllMonitors();
        ScanAllCandidates();
        ApplyAllLayouts();
    }

    static uint GetFocusBorderArgb()
    {
        uint argb = _accentArgb;
        if ((argb & 0xFF000000) == 0)
        {
            argb |= 0xFF000000;
        }
        return argb;
    }

    static int ArgbToBgr(uint argb)
    {
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte b = (byte)(argb & 0xFF);
        return b | (g << 8) | (r << 16);
    }

    static void ShowStatusToast(bool enabled)
    {
        string state = enabled ? "Enabled" : "Disabled";
        ShowToast("Jiraiya", state);
    }

    static void ShowToast(string title, string message)
    {
        void Show()
        {
            Form toast = new()
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                TopMost = true,
                BackColor = Color.FromArgb(32, 32, 32),
                Opacity = 0.95,
                Size = new Size(260, 96)
            };

            int radius = 14;
            toast.Paint += (_, args) =>
            {
                using Pen pen = new(Color.FromArgb(120, 255, 255, 255), 1);
                Rectangle bounds = new(Point.Empty, toast.Size - new Size(1, 1));
                using GraphicsPath path = CreateRoundedPath(bounds, radius);
                args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                args.Graphics.DrawPath(pen, path);
            };

            Label titleLabel = new()
            {
                Text = title,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(72, 10, 16, 0)
            };

            Label messageLabel = new()
            {
                Text = message,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10, FontStyle.Regular),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                Padding = new Padding(72, 0, 16, 14)
            };

            if (_appIcon != null)
            {
                PictureBox iconBox = new()
                {
                    Image = _appIcon.ToBitmap(),
                    SizeMode = PictureBoxSizeMode.StretchImage,
                    Size = new Size(48, 48),
                    Location = new Point(16, 24)
                };
                toast.Controls.Add(iconBox);
                iconBox.BringToFront();
            }

            toast.Controls.Add(messageLabel);
            toast.Controls.Add(titleLabel);

            using (GraphicsPath regionPath = CreateRoundedPath(new Rectangle(Point.Empty, toast.Size), radius))
            {
                toast.Region = new Region(regionPath);
            }

            Rectangle screen = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(Point.Empty);
            toast.Left = screen.Left + (screen.Width - toast.Width) / 2;
            toast.Top = screen.Bottom - toast.Height - 24;

            System.Windows.Forms.Timer timer = new() { Interval = 1000 };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                toast.Close();
            };

            toast.Shown += (_, __) => timer.Start();
            toast.FormClosed += (_, __) => timer.Dispose();

            toast.Show();
        }

        if (_hiddenForm != null && _hiddenForm.InvokeRequired)
        {
            _hiddenForm.BeginInvoke(new Action(Show));
        }
        else
        {
            Show();
        }
    }

    static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        Size size = new(diameter, diameter);
        Rectangle arc = new(bounds.Location, size);
        GraphicsPath path = new();

        // top left
        path.AddArc(arc, 180, 90);

        // top right
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);

        // bottom right
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);

        // bottom left
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();
        return path;
    }

    static void LoadAppIcon()
    {
        try
        {
            if (!File.Exists(_frogIconPath))
            {
                return;
            }

            DisposeAppIcon();
            _appIcon = new Icon(_frogIconPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Failed to load frog icon: {ex.Message}");
            _appIcon = null;
        }
    }

    static void DisposeAppIcon()
    {
        if (_appIcon == null) return;
        try
        {
            _appIcon.Dispose();
        }
        finally
        {
            _appIcon = null;
        }
    }

    static void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero) return;

        _keyboardProc = KeyboardHookCallback;
        using Process curProcess = Process.GetCurrentProcess();
        using ProcessModule curModule = curProcess.MainModule!;
        IntPtr moduleHandle = IntPtr.Zero;
        if (curModule.ModuleName != null)
        {
            moduleHandle = GetModuleHandle(curModule.ModuleName);
        }

        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, moduleHandle, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            Console.WriteLine("[!] Keyboard hook failed to install.");
        }
    }

    static void UninstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        _keyboardProc = null;
        _winPressed = _shiftPressed = _altPressed = false;
        _altComboInjected = false;
        _activeWinKeyCode = 0;
        _swallowedKeys.Clear();
    }

    static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _keyboardHook != IntPtr.Zero)
        {
            int message = wParam.ToInt32();
            bool keyDown = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
            bool keyUp = message == WM_KEYUP || message == WM_SYSKEYUP;

            KBDLLHOOKSTRUCT info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            Keys key = (Keys)info.vkCode;

            if (keyDown)
            {
                switch (key)
                {
                    case Keys.LWin:
                    case Keys.RWin:
                        _winPressed = true;
                        _activeWinKeyCode = info.vkCode;
                        break;
                    case Keys.LShiftKey:
                    case Keys.RShiftKey:
                        _shiftPressed = true;
                        break;
                    case Keys.LMenu:
                    case Keys.RMenu:
                    case Keys.Menu:
                        _altPressed = true;
                        _altComboInjected = false;
                        break;
                }

                if (_winPressed && _altPressed && key == Keys.J)
                {
                    ToggleActive();
                    _swallowedKeys.Add(info.vkCode);
                    return (IntPtr)1;
                }

                if (_altPressed && key == Keys.F11)
                {
                    ToggleFullscreenForActiveWindow();
                    _swallowedKeys.Add(info.vkCode);
                    return (IntPtr)1;
                }

                if (_isActive && _altPressed && key == Keys.Tab)
                {
                    bool backwards = _shiftPressed;
                    Direction direction = backwards ? Direction.Left : Direction.Right;
                    IntPtr monitor = GetMonitorUnderCursor();
                    HandleFocusCycle(direction, monitor);
                    EnsureAltComboFocus();
                    _swallowedKeys.Add(info.vkCode);
                    return (IntPtr)1;
                }

                if (_isActive && _winPressed && _shiftPressed && IsArrowKey(key))
                {
                    Direction direction = DirectionFromKey(key);
                    HandleReorder(direction);
                    _swallowedKeys.Add(info.vkCode);
                    return (IntPtr)1;
                }
            }

            if (keyUp)
            {
                switch (key)
                {
                    case Keys.LWin:
                    case Keys.RWin:
                        _winPressed = false;
                        _activeWinKeyCode = 0;
                        break;
                    case Keys.LShiftKey:
                    case Keys.RShiftKey:
                        _shiftPressed = false;
                        break;
                    case Keys.LMenu:
                    case Keys.RMenu:
                    case Keys.Menu:
                        _altPressed = false;
                        _altComboInjected = false;
                        break;
                }

                if (_swallowedKeys.Contains(info.vkCode))
                {
                    _swallowedKeys.Remove(info.vkCode);
                    return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    static void ToggleFullscreenForActiveWindow()
    {
        IntPtr hwnd = GetForegroundWindow();
        if ((hwnd == IntPtr.Zero || !IsWindowValidForToggleTarget(hwnd)) && _focusedWindow != IntPtr.Zero)
        {
            hwnd = _focusedWindow;
        }

        if (!IsWindowValidForToggleTarget(hwnd))
        {
            return;
        }

        if (_fullscreenToggles.TryGetValue(hwnd, out var saved))
        {
            RestoreWindowFromToggle(hwnd, saved);
            _fullscreenToggles.Remove(hwnd);

            if (_isActive)
            {
                ScanAllCandidates();
            }
            return;
        }

        if (!GetWindowRect(hwnd, out RECT currentRect))
        {
            return;
        }

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        MONITORINFO monitorInfo = new() { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var ordered = GetOrderedWindowsForMonitor(monitor)
            .Where(IsWindow)
            .Distinct()
            .ToList();
        if (!ordered.Contains(hwnd))
        {
            ordered.Add(hwnd);
        }

        var windowRects = new Dictionary<IntPtr, RECT>();
        foreach (var win in ordered)
        {
            if (IsWindow(win) && GetWindowRect(win, out RECT rect))
            {
                windowRects[win] = rect;
            }
        }

        if (!windowRects.ContainsKey(hwnd))
        {
            windowRects[hwnd] = currentRect;
        }

        _fullscreenToggles[hwnd] = new FullscreenToggleState(monitor, windowRects, ordered);

        RECT fullscreenRect = monitorInfo.rcMonitor;
        MoveWindowToRect(hwnd, fullscreenRect, "ALT+F11");
    }

    static void RestoreWindowFromToggle(IntPtr hwnd, FullscreenToggleState saved)
    {
        bool previousSuppress = _suppressMoveEvents;
        _suppressMoveEvents = true;
        try
        {
            foreach (var kv in saved.WindowRects)
            {
                IntPtr target = kv.Key;
                if (target == hwnd) continue;
                if (!IsWindow(target)) continue;
                MoveWindowToRect(target, kv.Value, "ALT+F11 peer restore");
            }

            if (saved.WindowRects.TryGetValue(hwnd, out var selfRect) && IsWindow(hwnd))
            {
                MoveWindowToRect(hwnd, selfRect, "ALT+F11 restore");
            }
        }
        finally
        {
            _suppressMoveEvents = previousSuppress;
        }

        if (saved.Monitor != IntPtr.Zero)
        {
            EnsureMonitorStructures(saved.Monitor);
            var filteredOrder = saved.OrderedHandles
                .Where(IsWindow)
                .Distinct()
                .ToList();
            if (filteredOrder.Count > 0)
            {
                _orderedPerMonitor[saved.Monitor] = filteredOrder;
            }
        }

        FocusWindowOnMonitor(hwnd);
    }

    static bool IsWindowValidForToggleTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (!IsWindow(hwnd)) return false;
        if (_hiddenForm != null && hwnd == _hiddenForm.Handle) return false;
        return true;
    }

    static void MoveWindowToRect(IntPtr hwnd, RECT rect, string context)
    {
        if (!IsWindow(hwnd)) return;

        if (GetWindowRect(hwnd, out RECT current) && AreRectsClose(current, rect))
        {
            return;
        }

        _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        _programmaticMoveWindows.Add(hwnd);
        bool ok = SetWindowPos(hwnd, IntPtr.Zero, rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top,
            SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        if (!ok)
        {
            _programmaticMoveWindows.Remove(hwnd);
        }
        Console.WriteLine($"    → {context}: {(ok ? "OK" : "FAIL")} hwnd=0x{hwnd.ToInt64():X}");
    }

    static AppConfiguration LoadConfiguration()
    {
        string configPath = LocateConfigurationPath();

        using FileStream stream = File.OpenRead(configPath);
        JsonSerializerOptions options = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        AppConfiguration? config = JsonSerializer.Deserialize<AppConfiguration>(stream, options);
        if (config == null)
        {
            throw new InvalidOperationException("Configuration file is empty or invalid JSON.");
        }

        config.Validate();
        return config;
    }

    static string LocateConfigurationPath()
    {
        List<string> searchedLocations = new();

        string? config = ProbeForConfiguration(AppContext.BaseDirectory, searchedLocations);
        if (config == null)
        {
            string currentDir = Directory.GetCurrentDirectory();
            if (!string.Equals(currentDir, AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                config = ProbeForConfiguration(currentDir, searchedLocations);
            }
        }

        if (config != null)
        {
            return config;
        }

        string searched = searchedLocations.Count == 0 ? "(none)" : string.Join(", ", searchedLocations);
        throw new InvalidOperationException($"Configuration file not found. Searched: {searched}");
    }

    static string? ProbeForConfiguration(string? startingDirectory, List<string> searched)
    {
        if (string.IsNullOrWhiteSpace(startingDirectory))
        {
            return null;
        }

        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(Path.GetFullPath(startingDirectory));
        }
        catch
        {
            return null;
        }

        const int maxLevels = 8;
        int traversed = 0;
        while (directory != null && traversed < maxLevels)
        {
            string candidate = Path.Combine(directory.FullName, "config.json");
            searched.Add(candidate);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
            traversed++;
        }

        return null;
    }

    static bool IsArrowKey(Keys key) => key is Keys.Left or Keys.Right or Keys.Up or Keys.Down;

    static Direction DirectionFromKey(Keys key) => key switch
    {
        Keys.Left => Direction.Left,
        Keys.Right => Direction.Right,
        Keys.Up => Direction.Up,
        Keys.Down => Direction.Down,
        _ => Direction.Left
    };

    static void EnsureAltComboFocus()
    {
        if (_altComboInjected) return;
        if (InjectCtrlTap())
        {
            _altComboInjected = true;
        }
    }

    static bool InjectCtrlTap()
    {
        INPUT[] inputs = new INPUT[2];

        inputs[0] = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = VK_CONTROL,
                    wScan = 0,
                    dwFlags = 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        inputs[1] = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = VK_CONTROL,
                    wScan = 0,
                    dwFlags = KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            int error = Marshal.GetLastWin32Error();
            Console.WriteLine($"[!] SendInput failed (err={error}).");
            return false;
        }

        return true;
    }

    static void HandleForegroundChanged(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            ClearFocusHighlight();
            return;
        }

        if (IsWindowValidForLayout(hwnd))
        {
            UpdateFocusHighlight(hwnd);
        }
        else
        {
            ClearFocusHighlight();
        }
    }

    static void HandleMoveSizeStart(IntPtr hwnd)
    {
        if (_programmaticMoveWindows.Remove(hwnd))
        {
            _draggingWindow = IntPtr.Zero;
            return;
        }
        if (!_isActive)
        {
            _draggingWindow = IntPtr.Zero;
            return;
        }
        if (_suppressMoveEvents || hwnd == IntPtr.Zero) return;
        if (!IsWindow(hwnd)) return;

        if (!IsWindowValidForLayout(hwnd))
        {
            _draggingWindow = IntPtr.Zero;
            return;
        }

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;

        EnsureMonitorStructures(monitor);

        var wins = GetOrderedWindowsForMonitor(monitor);
        if (wins.Count > 0)
        {
            int sourceIndex = wins.IndexOf(hwnd);
            if (sourceIndex != -1)
            {
                int logicalSlotCount = wins.Count switch
                {
                    <= 1 => 1,
                    2 => 2,
                    _ => 3
                };

                string label = GetSlotLabel(logicalSlotCount, sourceIndex);
                Console.WriteLine($"[drag] start slot={label}");
            }
        }

        _draggingWindow = hwnd;
    }

    static void HandleMoveSizeEnd(IntPtr hwnd)
    {
        if (!_isActive)
        {
            _draggingWindow = IntPtr.Zero;
            return;
        }

        if (!IsWindowValidForLayout(hwnd))
        {
            _draggingWindow = IntPtr.Zero;
            return;
        }
        // if (_programmaticMoveWindows.Remove(hwnd))
        // {
        //     _draggingWindow = IntPtr.Zero;
        //     return;
        // }
        // if (hwnd == IntPtr.Zero) return;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        // if (monitor == IntPtr.Zero) return;

        // EnsureMonitorStructures(monitor);

        // if (_draggingWindow != IntPtr.Zero && _draggingWindow != hwnd)
        // {
        //     _draggingWindow = IntPtr.Zero;
        //     return;
        // }

        // _draggingWindow = IntPtr.Zero;

        // if (_suppressMoveEvents) return;

        // ScanAllCandidates();

        // if (!_isActive) return;
        // if (!IsWindowValidForLayout(hwnd)) return;

        var workArea = EnsureMonitorWorkArea(monitor);
        if (workArea.right <= workArea.left || workArea.bottom <= workArea.top) return;

        var wins = GetOrderedWindowsForMonitor(monitor);
        if (wins.Count == 0)
        {
            return;
        }

        int sourceIndex = wins.IndexOf(hwnd);
        bool addedToList = false;
        if (sourceIndex == -1)
        {
            wins.Add(hwnd);
            sourceIndex = wins.Count - 1;
            addedToList = true;
        }

        int logicalSlotCount = wins.Count switch
        {
            <= 1 => 1,
            2 => 2,
            _ => 3
        };

        POINT cursor;
        if (!TryGetCursorPoint(out cursor))
        {
            if (!GetWindowRect(hwnd, out RECT windowRect)) return;
            cursor = new POINT
            {
                X = windowRect.left + ((windowRect.right - windowRect.left) / 2),
                Y = windowRect.top + ((windowRect.bottom - windowRect.top) / 2)
            };
        }

        int targetSlot = DetermineSlotFromPoint(workArea, cursor, logicalSlotCount);
        int targetIndex = GetTargetIndexForSlot(wins.Count, targetSlot);
        Console.WriteLine($"[drag] drop slot={GetSlotLabel(logicalSlotCount, targetSlot)}");
        if (targetIndex < 0 || targetIndex >= wins.Count)
        {
            return;
        }

        if (targetIndex == sourceIndex)
        {
            if (addedToList)
            {
                _orderedPerMonitor[monitor] = wins;
            }
            ApplyLayoutForMonitor(monitor);
            FocusWindowOnMonitor(hwnd);
            return;
        }

        (wins[sourceIndex], wins[targetIndex]) = (wins[targetIndex], wins[sourceIndex]);
        _orderedPerMonitor[monitor] = wins;
        ApplyLayoutForMonitor(monitor);
        FocusWindowOnMonitor(hwnd);
    }

    static void EnsureMonitorStructures(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return;

        if (!_allMonitors.Contains(monitor))
        {
            _allMonitors.Add(monitor);
        }

        if (!_candidatesPerMonitor.ContainsKey(monitor))
        {
            _candidatesPerMonitor[monitor] = new HashSet<IntPtr>();
        }

        if (!_orderedPerMonitor.ContainsKey(monitor))
        {
            _orderedPerMonitor[monitor] = new List<IntPtr>();
        }

        if (!_monitorWorkAreas.ContainsKey(monitor))
        {
            EnsureMonitorWorkArea(monitor);
        }

        _monitorWorkAreas.TryGetValue(monitor, out _);
    }

    static RECT EnsureMonitorWorkArea(IntPtr monitor)
    {
        if (!_monitorWorkAreas.TryGetValue(monitor, out var work))
        {
            MONITORINFO mi = new() { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref mi))
            {
                work = mi.rcWork;
                _monitorWorkAreas[monitor] = work;
            }
        }

        return _monitorWorkAreas.TryGetValue(monitor, out var stored) ? stored : default;
    }

    static int DetermineSlotFromPoint(RECT workArea, POINT point, int logicalSlotCount)
    {
        int left = Math.Min(workArea.left, workArea.right);
        int right = Math.Max(workArea.left, workArea.right);
        int top = Math.Min(workArea.top, workArea.bottom);
        int bottom = Math.Max(workArea.top, workArea.bottom);

        int x = Math.Clamp(point.X, left, right);
        int y = Math.Clamp(point.Y, top, bottom);

        int midX = left + ((right - left) / 2);
        if (x <= midX)
        {
            return 0;
        }

        if (logicalSlotCount <= 2)
        {
            return 1;
        }

        double topRatio = 0.58;
        int boundary = top + (int)((bottom - top) * topRatio);
        if (y <= boundary)
        {
            return 1;
        }

        return 2;
    }

    static List<IntPtr> GetOrderedWindowsForMonitor(IntPtr monitor)
    {
        EnsureMonitorStructures(monitor);

        if (!_orderedPerMonitor.TryGetValue(monitor, out var existingOrder))
        {
            existingOrder = new List<IntPtr>();
        }

        var sanitized = new List<IntPtr>();
        foreach (var window in existingOrder)
        {
            if (window == IntPtr.Zero) continue;
            if (!IsWindow(window)) continue;
            if (MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST) != monitor) continue;

            bool valid = IsWindowValidForLayout(window);
            if (!valid && !IsWindowFullscreenOnMonitor(window, monitor))
            {
                continue;
            }

            if (!sanitized.Contains(window))
            {
                sanitized.Add(window);
            }
        }

        if (_candidatesPerMonitor.TryGetValue(monitor, out var candidates))
        {
            foreach (var candidate in candidates.ToList())
            {
                if (!IsWindowValidForLayout(candidate)) continue;
                if (MonitorFromWindow(candidate, MONITOR_DEFAULTTONEAREST) != monitor) continue;
                if (!sanitized.Contains(candidate))
                {
                    sanitized.Add(candidate);
                }
            }
        }

        _orderedPerMonitor[monitor] = sanitized;
        return sanitized;
    }

    static bool TryGetCursorPoint(out POINT point)
    {
        if (GetPhysicalCursorPos(out point))
        {
            return true;
        }

        return GetCursorPos(out point);
    }

    static IntPtr GetMonitorUnderCursor()
    {
        if (TryGetCursorPoint(out POINT point))
        {
            var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                return monitor;
            }
        }

        return IntPtr.Zero;
    }

    static IntPtr GetRootWindowUnderCursor()
    {
        if (!TryGetCursorPoint(out POINT point))
        {
            return IntPtr.Zero;
        }

        IntPtr window = WindowFromPoint(point);
        if (window == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return GetAncestor(window, GA_ROOT);
    }

    static void MoveWindowToSlot(IntPtr monitor, IntPtr hwnd, int slotIndex)
    {
        if (!IsWindowValidForLayout(hwnd))
        {
            return;
        }

        if (!_candidatesPerMonitor.TryGetValue(monitor, out var candidates))
        {
            candidates = new HashSet<IntPtr>();
            _candidatesPerMonitor[monitor] = candidates;
        }

        candidates.Add(hwnd);

        var order = GetOrderedWindowsForMonitor(monitor);

        if (order.Count == 0)
        {
            order.Add(hwnd);
            _orderedPerMonitor[monitor] = order;
            ApplyLayoutForMonitor(monitor);
            UpdateFocusHighlight(hwnd);
            return;
        }

        int currentIndex = order.IndexOf(hwnd);
        int targetIndex = GetTargetIndexForSlot(order.Count, slotIndex);
        targetIndex = Math.Clamp(targetIndex, 0, Math.Max(order.Count - 1, 0));

        if (currentIndex == -1)
        {
            order.Insert(Math.Min(targetIndex, order.Count), hwnd);
        }
        else if (targetIndex != currentIndex)
        {
            (order[targetIndex], order[currentIndex]) = (order[currentIndex], order[targetIndex]);
        }

        _orderedPerMonitor[monitor] = order;
        ApplyLayoutForMonitor(monitor);
        UpdateFocusHighlight(hwnd);
    }

    static int GetTargetIndexForSlot(int count, int slotIndex)
    {
        if (count <= 0) return 0;

        return slotIndex switch
        {
            0 => 0,
            1 => count >= 2 ? 1 : count - 1,
            2 => count >= 3 ? 2 : count - 1,
            _ => Math.Min(count - 1, slotIndex)
        };
    }

    static bool IsFullscreenRect(RECT rect, RECT monitorRect)
    {
        const int tolerance = 8;
        bool closeLeft = Math.Abs(rect.left - monitorRect.left) <= tolerance;
        bool closeTop = Math.Abs(rect.top - monitorRect.top) <= tolerance;
        bool closeRight = Math.Abs(rect.right - monitorRect.right) <= tolerance;
        bool closeBottom = Math.Abs(rect.bottom - monitorRect.bottom) <= tolerance;
        return closeLeft && closeTop && closeRight && closeBottom;
    }

    static bool IsWindowFullscreenOnMonitor(IntPtr hwnd, IntPtr monitor)
    {
        if (hwnd == IntPtr.Zero || monitor == IntPtr.Zero) return false;
        if (!IsWindow(hwnd)) return false;
        if (!GetWindowRect(hwnd, out RECT rect)) return false;

        MONITORINFO mi = new() { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return false;

        return IsFullscreenRect(rect, mi.rcMonitor);
    }

    static bool MonitorHasFullscreenWindow(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return false;

        MONITORINFO mi = new() { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return false;
        var monitorRect = mi.rcMonitor;

        bool found = false;
        EnumWindows((h, _) =>
        {
            if (found) return false;
            if (!IsWindow(h) || !IsWindowVisible(h) || IsIconic(h)) return true;
            if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != monitor) return true;

            long style = GetWindowLongPtr(h, GWL_STYLE).ToInt64();
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            const long WS_EX_TOOLWINDOW = 0x00000080;
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
            if ((style & 0x00CF0000) == 0) return true;

            if (!GetWindowRect(h, out RECT rect)) return true;
            if (IsFullscreenRect(rect, monitorRect))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return found;
    }

    static void HandleReorder(Direction direction)
    {
        if (!_isActive) return;
        IntPtr activeWindow = GetForegroundWindow();
        if (activeWindow == IntPtr.Zero || !IsWindowValidForLayout(activeWindow))
        {
            activeWindow = _focusedWindow;
        }

        if (activeWindow == IntPtr.Zero) return;

        var monitor = MonitorFromWindow(activeWindow, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;

        var wins = GetOrderedWindowsForMonitor(monitor);
        if (wins.Count <= 1) return;

        int currentIndex = wins.IndexOf(activeWindow);
        if (currentIndex == -1)
        {
            FocusWindowOnMonitor(wins[0]);
            return;
        }

        GridSlot slot = GetSlotForIndex(currentIndex);

        int insertIndex = currentIndex;
        switch (direction)
        {
            case Direction.Left:
                if (slot == GridSlot.A) return;
                insertIndex = 0;
                break;
            case Direction.Right:
                if (slot == GridSlot.A && wins.Count > 1)
                {
                    insertIndex = 1;
                }
                else if (slot == GridSlot.B && wins.Count > 2)
                {
                    insertIndex = wins.Count;
                }
                else
                {
                    return;
                }
                break;
            case Direction.Up:
                if (slot == GridSlot.C && wins.Count > 1)
                {
                    insertIndex = 1;
                }
                else
                {
                    return;
                }
                break;
            case Direction.Down:
                if (slot == GridSlot.B && wins.Count > 2)
                {
                    insertIndex = wins.Count;
                }
                else if (slot == GridSlot.C && wins.Count > currentIndex + 1)
                {
                    insertIndex = currentIndex + 1;
                }
                else
                {
                    return;
                }
                break;
            default:
                return;
        }

        if (insertIndex == currentIndex) return;

        wins.RemoveAt(currentIndex);
        if (insertIndex > wins.Count) insertIndex = wins.Count;
        wins.Insert(insertIndex, activeWindow);

        _orderedPerMonitor[monitor] = wins;
        ApplyLayoutForMonitor(monitor);
        FocusWindowOnMonitor(activeWindow);
    }

    static void HandleFocusCycle(Direction direction, IntPtr monitorOverride = default)
    {
        IntPtr referenceWindow = GetForegroundWindow();
        if (referenceWindow == IntPtr.Zero || !IsWindowValidForLayout(referenceWindow))
        {
            referenceWindow = _focusedWindow;
        }

        IntPtr monitor = monitorOverride;
        if (monitor == IntPtr.Zero && referenceWindow != IntPtr.Zero)
        {
            monitor = MonitorFromWindow(referenceWindow, MONITOR_DEFAULTTONEAREST);
        }

        if (monitor == IntPtr.Zero)
        {
            monitor = GetMonitorUnderCursor();
        }

        if (monitor == IntPtr.Zero) return;

        var wins = GetOrderedWindowsForMonitor(monitor);
        if (wins.Count == 0) return;

        int currentIndex = 0;

        if (monitorOverride != IntPtr.Zero)
        {
            IntPtr cursorWindow = GetRootWindowUnderCursor();
            if (cursorWindow != IntPtr.Zero && MonitorFromWindow(cursorWindow, MONITOR_DEFAULTTONEAREST) == monitor)
            {
                int cursorIndex = wins.IndexOf(cursorWindow);
                if (cursorIndex != -1)
                {
                    currentIndex = cursorIndex;
                }
            }
        }

        if (currentIndex == 0 && referenceWindow != IntPtr.Zero && MonitorFromWindow(referenceWindow, MONITOR_DEFAULTTONEAREST) == monitor)
        {
            int refIndex = wins.IndexOf(referenceWindow);
            if (refIndex != -1)
            {
                currentIndex = refIndex;
            }
        }

        if (currentIndex == 0 && _focusedWindow != IntPtr.Zero && MonitorFromWindow(_focusedWindow, MONITOR_DEFAULTTONEAREST) == monitor)
        {
            int focusedIndex = wins.IndexOf(_focusedWindow);
            if (focusedIndex != -1)
            {
                currentIndex = focusedIndex;
            }
        }

        if (wins.Count == 1)
        {
            FocusWindowOnMonitor(wins[currentIndex]);
            return;
        }

        int targetIndex = currentIndex;
        if (direction is Direction.Left or Direction.Up)
        {
            targetIndex = (currentIndex - 1 + wins.Count) % wins.Count;
        }
        else if (direction is Direction.Right or Direction.Down)
        {
            targetIndex = (currentIndex + 1) % wins.Count;
        }

        if (targetIndex == currentIndex) return;

        IntPtr target = wins[targetIndex];
        if (!IsWindowValidForLayout(target)) return;

        FocusWindowOnMonitor(target);
    }

    static GridSlot GetSlotForIndex(int index) => index switch
    {
        0 => GridSlot.A,
        1 => GridSlot.B,
        _ => GridSlot.C
    };

    static void FocusWindowOnMonitor(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            EnsureMonitorStructures(monitor);
        }

        _ = ShowWindow(hwnd, SW_SHOWNORMAL);
        _ = SetForegroundWindow(hwnd);
        UpdateFocusHighlight(hwnd);
    }

    static void UpdateFocusHighlight(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return;

        if (_focusedWindow != IntPtr.Zero && _focusedWindow != hwnd && IsWindow(_focusedWindow))
        {
            ResetBorder(_focusedWindow);
        }

        _focusedWindow = hwnd;
        ApplyAccentBorder(_focusedWindow);
    }

    static void ClearFocusHighlight()
    {
        if (_focusedWindow != IntPtr.Zero && IsWindow(_focusedWindow))
        {
            ResetBorder(_focusedWindow);
        }
        _focusedWindow = IntPtr.Zero;
    }

    static void ApplyAccentBorder(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return;

        uint accent = GetFocusBorderArgb();
        int color = ArgbToBgr(accent);
        int width = 4;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref color, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_WIDTH, ref width, sizeof(int));
    }

    static void ResetBorder(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return;

        int defColor = DWMWA_COLOR_DEFAULT;
        int width = 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref defColor, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_WIDTH, ref width, sizeof(int));
    }

    static void ReapplyFocusHighlight()
    {
        if (_focusedWindow != IntPtr.Zero && IsWindow(_focusedWindow))
        {
            ApplyAccentBorder(_focusedWindow);
        }
    }
    static bool DiscoverAllMonitors()
    {
        _allMonitors.Clear();
        _candidatesPerMonitor.Clear();
        _monitorWorkAreas.Clear();
        _orderedPerMonitor.Clear();

        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT r, IntPtr data) =>
        {
            MONITORINFOEX miex = new();
            miex.cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>();
            if (GetMonitorInfo(h, ref miex))
            {
                _allMonitors.Add(h);
                _candidatesPerMonitor[h] = new HashSet<IntPtr>();
                _monitorWorkAreas[h] = miex.rcWork;
                _orderedPerMonitor[h] = new List<IntPtr>();
            }
            return true;
        }, IntPtr.Zero);

        return _allMonitors.Count > 0;
    }

    static void CleanupHooks()
    {
        if (_hookShowHide != IntPtr.Zero)
        {
            UnhookWinEvent(_hookShowHide);
            _hookShowHide = IntPtr.Zero;
        }
        if (_hookForeground != IntPtr.Zero)
        {
            UnhookWinEvent(_hookForeground);
            _hookForeground = IntPtr.Zero;
        }
        if (_hookMinMax != IntPtr.Zero)
        {
            UnhookWinEvent(_hookMinMax);
            _hookMinMax = IntPtr.Zero;
        }
        if (_hookMoveSize != IntPtr.Zero)
        {
            UnhookWinEvent(_hookMoveSize);
            _hookMoveSize = IntPtr.Zero;
        }

        UninstallKeyboardHook();
        ClearFocusHighlight();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        // Clean up tracking dictionaries
        _candidatesPerMonitor.Clear();
        _monitorWorkAreas.Clear();
        _orderedPerMonitor.Clear();
        _allMonitors.Clear();

        // Stop debounce timer
        _debounce?.Stop();
        _debounce?.Dispose();
    }

    // --- P/Invoke ---
    const uint EVENT_OBJECT_SHOW = 0x8002;
    const uint EVENT_OBJECT_HIDE = 0x8003;
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    const int OBJID_WINDOW = 0;

    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    const int SW_SHOWNOACTIVATE = 4;

    const uint MONITOR_DEFAULTTONEAREST = 2;
    const uint MONITORINFOF_PRIMARY = 0x00000001;

    const int GWL_STYLE = -16;
    const int GWL_EXSTYLE = -20;

    const int DWMWA_CLOAKED = 14;
    const int DWMWA_BORDER_COLOR = 34;
    const int DWMWA_BORDER_WIDTH = 35;
    const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);

    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOOWNERZORDER = 0x0200;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_FRAMECHANGED = 0x0020;

    const uint GA_ROOT = 2;

    const int SW_SHOWNORMAL = 1;

    const int WM_KEYDOWN = 0x0100;
    const int WM_KEYUP = 0x0101;
    const int WM_SYSKEYDOWN = 0x0104;
    const int WM_SYSKEYUP = 0x0105;

    const int WH_KEYBOARD_LL = 13;

    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const ushort VK_CONTROL = 0x11;

    static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] static extern bool GetPhysicalCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)] static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr hObject);

    // manja struktura
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetMonitorInfo")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    // veća struktura
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true, EntryPoint = "GetMonitorInfo")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
    [DllImport("dwmapi.dll")] static extern int DwmGetColorizationColor(out uint pcrColorization, out bool pfOpaqueBlend);

    [DllImport("user32.dll", SetLastError = true)] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags; // MONITORINFOF_PRIMARY
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }
}
