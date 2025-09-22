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
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Timers;
using System.Windows.Forms;
using Microsoft.Win32;

class Program
{
    // --- Config ---
    const int DebounceMs = 120; // delay before recompute after an event

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

    static readonly System.Timers.Timer _debounce = new(DebounceMs) { AutoReset = false, Enabled = false };
    static Form? _hiddenForm;
    static IntPtr _keyboardHook = IntPtr.Zero;
    static LowLevelKeyboardProc? _keyboardProc;
    static IntPtr _hookMoveSize = IntPtr.Zero;
    static bool _winPressed;
    static bool _shiftPressed;
    static bool _altPressed;
    static bool _suppressMoveEvents;
    static readonly HashSet<int> _swallowedKeys = new();
    static readonly HashSet<IntPtr> _programmaticMoveWindows = new();
    static IntPtr _draggingWindow = IntPtr.Zero;

    enum GridSlot { A, B, C }
    enum Direction { Left, Right, Up, Down }

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
        Console.WriteLine("Jiraiya - All monitors spiral grid\n");

        if (!DiscoverAllMonitors())
        {
            Console.WriteLine("[!] Nisu pronađeni monitori. Zatvaram.");
            return;
        }

        Console.WriteLine($"[i] Radim na {_allMonitors.Count} monitor(a)");
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
            Console.WriteLine("[!] Bar jedan hook nije postavljen (IntPtr.Zero). Provjeri potpis i UAC.");
        }

        Console.WriteLine("[i] Hooks aktivni. Pritisni Ctrl+C za izlaz.");

        // Setup console cancel handler for clean shutdown
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n[i] Shutting down...");
            CleanupHooks();
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
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        
        // Create invisible form to handle message loop
        _hiddenForm = new Form()
        {
            WindowState = FormWindowState.Minimized,
            ShowInTaskbar = false,
            Visible = false
        };
        
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
            Console.WriteLine($"[∴] Monitor layout: {count} prozor(a)");
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

    static bool IsWindowValidForLayout(IntPtr h)
    {
        if (!IsWindow(h) || !IsWindowVisible(h) || IsIconic(h)) return false;

        long style = GetWindowLongPtr(h, GWL_STYLE).ToInt64();
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        const long WS_OVERLAPPEDWINDOW = 0x00CF0000;
        const long WS_EX_TOOLWINDOW = 0x00000080;

        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
        if ((style & WS_OVERLAPPEDWINDOW) == 0) return false;

        // exclude cloaked (UWP)
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;

        if (GetWindowRect(h, out RECT windowRect))
        {
            var monitor = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                MONITORINFO mi = new() { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref mi))
                {
                    if (IsFullscreenRect(windowRect, mi.rcMonitor)) return false;
                }
            }
        }

        return true;
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
            Console.WriteLine("[i] Jiraiya nastavlja (Win+Alt+J)");
            ScanAllCandidates();
            ApplyAllLayouts();
            ReapplyFocusHighlight();
        }
        else
        {
            Console.WriteLine("[i] Jiraiya pauziran (Win+Alt+J)");
            ClearFocusHighlight();
        }
    }

    static void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Console.WriteLine("[i] Promjena monitora detektirana – osvježavam layout");
        DiscoverAllMonitors();
        ScanAllCandidates();
        ApplyAllLayouts();
    }

    static uint GetFocusBorderArgb()
    {
        Color accent = Color.FromArgb(unchecked((int)_accentArgb));
        Color softened = MixColors(accent, Color.White, 0.28);
        if (accent.R > accent.B + 40)
        {
            // bias warm accents slightly toward a calmer blue glow
            Color coolRef = Color.FromArgb(unchecked((int)0xFF2D7CFF));
            softened = MixColors(softened, coolRef, 0.30);
        }
        Color enriched = MixColors(softened, accent, 0.18);
        uint argb = 0xFF000000;
        argb |= (uint)(enriched.R << 16);
        argb |= (uint)(enriched.G << 8);
        argb |= enriched.B;
        return argb;
    }

    static int ArgbToBgr(uint argb)
    {
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte b = (byte)(argb & 0xFF);
        return b | (g << 8) | (r << 16);
    }

    static Color MixColors(Color baseColor, Color blend, double amount)
    {
        amount = Math.Clamp(amount, 0d, 1d);
        int r = (int)(baseColor.R * (1 - amount) + blend.R * amount);
        int g = (int)(baseColor.G * (1 - amount) + blend.G * amount);
        int b = (int)(baseColor.B * (1 - amount) + blend.B * amount);
        return Color.FromArgb(255, Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
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
            Console.WriteLine("[!] Keyboard hook nije postavljen.");
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
                        break;
                    case Keys.LShiftKey:
                    case Keys.RShiftKey:
                        _shiftPressed = true;
                        break;
                    case Keys.LMenu:
                    case Keys.RMenu:
                    case Keys.Menu:
                        _altPressed = true;
                        break;
                }

                if (_winPressed && _altPressed && key == Keys.J)
                {
                    ToggleActive();
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
                        break;
                    case Keys.LShiftKey:
                    case Keys.RShiftKey:
                        _shiftPressed = false;
                        break;
                    case Keys.LMenu:
                    case Keys.RMenu:
                    case Keys.Menu:
                        _altPressed = false;
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

    static bool IsArrowKey(Keys key) => key is Keys.Left or Keys.Right or Keys.Up or Keys.Down;

    static Direction DirectionFromKey(Keys key) => key switch
    {
        Keys.Left => Direction.Left,
        Keys.Right => Direction.Right,
        Keys.Up => Direction.Up,
        Keys.Down => Direction.Down,
        _ => Direction.Left
    };

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
        if (_suppressMoveEvents || hwnd == IntPtr.Zero) return;
        if (!IsWindow(hwnd)) return;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;

        EnsureMonitorStructures(monitor);
        _draggingWindow = hwnd;
    }

    static void HandleMoveSizeEnd(IntPtr hwnd)
    {
        if (_programmaticMoveWindows.Remove(hwnd))
        {
            _draggingWindow = IntPtr.Zero;
            return;
        }
        if (hwnd == IntPtr.Zero) return;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;

        EnsureMonitorStructures(monitor);

        if (_draggingWindow != IntPtr.Zero && _draggingWindow != hwnd)
        {
            _draggingWindow = IntPtr.Zero;
            return;
        }

        _draggingWindow = IntPtr.Zero;

        if (_suppressMoveEvents) return;

        ScanAllCandidates();

        if (!_isActive) return;
        if (!IsWindowValidForLayout(hwnd)) return;
        if (!GetWindowRect(hwnd, out RECT windowRect)) return;

        var workArea = EnsureMonitorWorkArea(monitor);
        if (workArea.right <= workArea.left || workArea.bottom <= workArea.top) return;

        int candidateCount = _candidatesPerMonitor.TryGetValue(monitor, out var candidateSet) ? candidateSet.Count : 0;
        int logicalSlotCount = candidateCount switch
        {
            <= 1 => 1,
            2 => 2,
            _ => 3
        };

        POINT cursor;
        if (!GetCursorPos(out cursor))
        {
            cursor = new POINT
            {
                X = windowRect.left + ((windowRect.right - windowRect.left) / 2),
                Y = windowRect.top + ((windowRect.bottom - windowRect.top) / 2)
            };
        }

        int targetSlot = DetermineSlotFromPoint(workArea, cursor, logicalSlotCount);
        MoveWindowToSlot(monitor, hwnd, targetSlot);
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

    static void MoveWindowToSlot(IntPtr monitor, IntPtr hwnd, int slotIndex)
    {
        EnsureMonitorStructures(monitor);

        if (!_orderedPerMonitor.TryGetValue(monitor, out var existingOrder))
        {
            existingOrder = new List<IntPtr>();
        }

        if (!_candidatesPerMonitor.TryGetValue(monitor, out var candidates))
        {
            candidates = new HashSet<IntPtr>();
            _candidatesPerMonitor[monitor] = candidates;
        }

        candidates.Add(hwnd);

        // work on a copy to avoid mutating while iterating
        var order = existingOrder.Where(IsWindowValidForLayout)
                                 .Where(h => MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) == monitor)
                                 .Distinct()
                                 .ToList();

        foreach (var candidate in candidates.ToList())
        {
            if (!IsWindowValidForLayout(candidate)) continue;
            if (MonitorFromWindow(candidate, MONITOR_DEFAULTTONEAREST) != monitor) continue;
            if (!order.Contains(candidate))
            {
                order.Add(candidate);
            }
        }

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

        if (!order.Contains(hwnd))
        {
            order.Add(hwnd);
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
        if (activeWindow == IntPtr.Zero)
        {
            activeWindow = _focusedWindow;
        }

        if (activeWindow == IntPtr.Zero) return;

        var monitor = MonitorFromWindow(activeWindow, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;

        EnsureMonitorStructures(monitor);

        if (!_orderedPerMonitor.TryGetValue(monitor, out var wins) || wins.Count <= 1) return;

        int currentIndex = wins.IndexOf(activeWindow);
        if (currentIndex == -1) return;

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
        int width = 6;
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

    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOOWNERZORDER = 0x0200;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_FRAMECHANGED = 0x0020;

    const int SW_SHOWNORMAL = 1;

    const int WM_KEYDOWN = 0x0100;
    const int WM_KEYUP = 0x0101;
    const int WM_SYSKEYDOWN = 0x0104;
    const int WM_SYSKEYUP = 0x0105;

    const int WH_KEYBOARD_LL = 13;

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] static extern IntPtr GetModuleHandle(string lpModuleName);

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
    struct POINT
    {
        public int X;
        public int Y;
    }
}
