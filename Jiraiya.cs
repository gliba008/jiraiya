// .NET 6/7/8 Console – Jiraiya for Windows 11 (all monitors)
// Listens for window show/hide/move/minimize/maximize events and tiles candidates on all monitors
// Spiral grid layout: 1 win: 100%:100%, 2 wins: 50%:100%, 3 wins: 50%:100%,50%:50%,50%:50%, 4 wins: 50%:100%,50%:50%,25%:50%,25%:50%
// Each monitor is managed independently. Logs actions to console.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Timers;
using System.Threading;
using System.Windows.Forms;

class Program
{
    // --- Config ---
    const int DebounceMs = 120; // delay before recompute after an event

    // WinEvent hooks
    static IntPtr _hookShowHide = IntPtr.Zero;
    static IntPtr _hookForeground = IntPtr.Zero;
    static IntPtr _hookLocation = IntPtr.Zero;
    static IntPtr _hookMinMax = IntPtr.Zero;

    // Per-monitor tracking
    static readonly Dictionary<IntPtr, HashSet<IntPtr>> _candidatesPerMonitor = new();
    static readonly Dictionary<IntPtr, RECT> _monitorWorkAreas = new();
    static readonly List<IntPtr> _allMonitors = new();
    
    // Window ordering tracking for stable layout
    static readonly Dictionary<IntPtr, long> _windowOrderTracker = new();
    static long _orderCounter = 0;

    static readonly System.Timers.Timer _debounce = new(DebounceMs) { AutoReset = false, Enabled = false };
    static Form? _hiddenForm;

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

        _debounce.Elapsed += (_, __) => ApplyAllLayouts();

        // set hooks with proper flags for better performance
        const uint hookFlags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        _hookShowHide = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE, IntPtr.Zero, _cb, 0, 0, hookFlags);
        _hookForeground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _cb, 0, 0, hookFlags);
        _hookLocation = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _cb, 0, 0, hookFlags);
        _hookMinMax = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _cb, 0, 0, hookFlags);

        Console.WriteLine($"[i] Hooks: show/hide=0x{_hookShowHide.ToInt64():X}, fg=0x{_hookForeground.ToInt64():X}, loc=0x{_hookLocation.ToInt64():X}, minmax=0x{_hookMinMax.ToInt64():X}");
        if (_hookShowHide == IntPtr.Zero || _hookForeground == IntPtr.Zero || _hookLocation == IntPtr.Zero || _hookMinMax == IntPtr.Zero)
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
            _hiddenForm.BeginInvoke(new Action(() => ProcessWinEvent(eventType, hwnd)));
        }
        else
        {
            ProcessWinEvent(eventType, hwnd);
        }
    }

    static void ProcessWinEvent(uint eventType, IntPtr hwnd)
    {
        Console.WriteLine($"CALLBACK event=0x{eventType:X} hwnd=0x{hwnd.ToInt64():X}");

        switch (eventType)
        {
            case EVENT_OBJECT_SHOW:
            case EVENT_SYSTEM_FOREGROUND:
            case EVENT_OBJECT_LOCATIONCHANGE:
            case EVENT_SYSTEM_MINIMIZEEND: // window restored from minimized
            case EVENT_OBJECT_HIDE:
            case EVENT_SYSTEM_MINIMIZESTART: // window minimized
                ScanAllCandidates();
                Debounce();
                break;
        }
    }

    static void Debounce()
    {
        try { _debounce.Stop(); _debounce.Start(); } catch { }
    }

    static void ScanAllCandidates()
    {
        // Enumerate in reverse Z-order (bottom to top) to match taskbar order
        var allWindows = new List<IntPtr>();
        _ = EnumWindows((IntPtr h, IntPtr l) => { allWindows.Add(h); return true; }, IntPtr.Zero);

        // Process in reverse order (oldest/bottom windows first)
        allWindows.Reverse();

        // Iterira po monitorima
        foreach (var h in allWindows)
        {
            // Provjeri je li window dobar za layouta
            bool shouldProcessWindow = IsWindowValidForLayout(h);
            if (shouldProcessWindow)
            {
                // Gets the monitor
                var monitor = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);

                // Handles the monitor in candidates field
                if (_candidatesPerMonitor.ContainsKey(monitor) == false)
                {
                    _candidatesPerMonitor.Add(monitor, new());
                }

                // Handles the candidate in the monitor
                var monitorCandidates = _candidatesPerMonitor[monitor];
                if (monitorCandidates.Contains(h) == false)
                {
                    monitorCandidates.Add(h);
                }

                // Assign order for initial windows
                // Ne znam šta je ovo
                if (!_windowOrderTracker.ContainsKey(h))
                {
                    _windowOrderTracker[h] = ++_orderCounter;
                }
            }
        }

        // Cleans closed windows
        foreach (var key in _candidatesPerMonitor.Keys)
        {
            var candidates = _candidatesPerMonitor[key];
            foreach (var win in candidates)
            {
                if (IsWindow(win) == false)
                {
                    candidates.Remove(win);
                }
            }
        }
    }

    static void ApplyAllLayouts()
    {
        foreach (var monitor in _allMonitors)
        {
            ApplyLayoutForMonitor(monitor);
        }
    }

    static void ApplyLayoutForMonitor(IntPtr monitor)
    {
        try
        {
            // Re-evaluate work area for this monitor
            MONITORINFO mi = new() { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref mi))
            {
                _monitorWorkAreas[monitor] = mi.rcWork;
            }

            var workArea = _monitorWorkAreas[monitor];
            var candidates = _candidatesPerMonitor[monitor];

            // var wins = candidates.Where(IsWindowValidForLayout)
            //                      .Where(h => MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) == monitor)
            //                      .Distinct()
            //                      .ToList();
            var wins = candidates.ToList();

            int count = wins.Count;
            Console.WriteLine($"[∴] Monitor layout: {count} prozor(a)");
            if (count == 0) return;

            // Apply spiral grid layout
            var rects = CalculateSpiralGrid(workArea, count);
            for (int i = 0; i < count; i++)
            {
                SetToRect(wins[i], rects[i], $"spiral {i + 1}/{count}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[!] Layout error: " + ex.Message);
        }
    }

    static void UpdateCandidateForAllMonitors(IntPtr hwnd)
    {
        ScanAllCandidates();
    }

    static void RemoveCandidateFromAllMonitors(IntPtr hwnd)
    {
        foreach (var candidates in _candidatesPerMonitor.Values)
        {
            _ = candidates.Remove(hwnd);
        }
        // Keep order tracking for stability - don't remove unless window is destroyed
        // This way if window comes back, it keeps its original position
    }

    static long GetWindowCreationOrder(IntPtr hwnd)
    {
        return _windowOrderTracker.TryGetValue(hwnd, out long order) ? order : long.MaxValue;
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

        return true;
    }

    // Spiral Grid Layout - 1 win: 100%:100%, 2 wins: 50%:100%, 3 wins: 50%:100%,50%:50%,50%:50%, 4 wins: 50%:100%,50%:50%,25%:50%,25%:50%
    static List<RECT> CalculateSpiralGrid(RECT workArea, int count)
    {
        var rects = new List<RECT>(count);
        if (count == 0) return rects;

        int w = workArea.right - workArea.left;
        int h = workArea.bottom - workArea.top;

        if (count == 1)
        {
            // 1 prozor: 100%:100%
            rects.Add(new RECT { left = workArea.left, top = workArea.top, right = workArea.right, bottom = workArea.bottom });
        }
        else if (count == 2)
        {
            // 2 prozora: 50%:100% svaki
            int halfW = w / 2;
            rects.Add(new RECT { left = workArea.left, top = workArea.top, right = workArea.left + halfW, bottom = workArea.bottom });
            rects.Add(new RECT { left = workArea.left + halfW, top = workArea.top, right = workArea.right, bottom = workArea.bottom });
        }
        else if (count == 3)
        {
            // 3 prozora: 50%:100%, 50%:50%, 50%:50%
            int halfW = w / 2;
            int halfH = h / 2;
            rects.Add(new RECT { left = workArea.left, top = workArea.top, right = workArea.left + halfW, bottom = workArea.bottom });
            rects.Add(new RECT { left = workArea.left + halfW, top = workArea.top, right = workArea.right, bottom = workArea.top + halfH });
            rects.Add(new RECT { left = workArea.left + halfW, top = workArea.top + halfH, right = workArea.right, bottom = workArea.bottom });
        }
        else
        {
            // 4 prozora: 50%:100%, 50%:50%, 25%:50%, 25%:50%
            int halfW = w / 2;
            int quarterW = w / 4;
            int halfH = h / 2;
            rects.Add(new RECT { left = workArea.left, top = workArea.top, right = workArea.left + halfW, bottom = workArea.bottom });
            rects.Add(new RECT { left = workArea.left + halfW, top = workArea.top, right = workArea.right, bottom = workArea.top + halfH });
            rects.Add(new RECT { left = workArea.left + halfW, top = workArea.top + halfH, right = workArea.left + halfW + quarterW, bottom = workArea.bottom });
            rects.Add(new RECT { left = workArea.left + halfW + quarterW, top = workArea.top + halfH, right = workArea.right, bottom = workArea.bottom });
        }

        return rects;
    }

    static List<RECT> CalculateExtendedSpiral(RECT workArea, int count)
    {
        var rects = new List<RECT>(count);
        int w = workArea.right - workArea.left;
        int h = workArea.bottom - workArea.top;

        // Start with basic 4-window layout, then subdivide further
        int cols = (int)Math.Ceiling(Math.Sqrt(count));
        int rows = (int)Math.Ceiling((double)count / cols);
        
        int cellW = w / cols;
        int cellH = h / rows;

        for (int i = 0; i < count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            
            int left = workArea.left + col * cellW;
            int top = workArea.top + row * cellH;
            int right = (col == cols - 1) ? workArea.right : left + cellW;
            int bottom = (row == rows - 1) ? workArea.bottom : top + cellH;

            rects.Add(new RECT { left = left, top = top, right = right, bottom = bottom });
        }

        return rects;
    }

    static void SetToRect(IntPtr hwnd, RECT r, string label)
    {
        if (!IsWindow(hwnd)) return;

        _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        bool ok = SetWindowPos(hwnd, IntPtr.Zero, r.left, r.top, r.right - r.left, r.bottom - r.top,
            SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        Console.WriteLine($"    → {label}: {(ok ? "OK" : "FAIL")} hwnd=0x{hwnd.ToInt64():X}");
    }

    static bool DiscoverAllMonitors()
    {
        _allMonitors.Clear();
        _candidatesPerMonitor.Clear();
        _monitorWorkAreas.Clear();

        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT r, IntPtr data) =>
        {
            MONITORINFOEX miex = new();
            miex.cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>();
            if (GetMonitorInfo(h, ref miex))
            {
                _allMonitors.Add(h);
                _candidatesPerMonitor[h] = new HashSet<IntPtr>();
                _monitorWorkAreas[h] = miex.rcWork;
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
        if (_hookLocation != IntPtr.Zero)
        {
            UnhookWinEvent(_hookLocation);
            _hookLocation = IntPtr.Zero;
        }
        if (_hookMinMax != IntPtr.Zero)
        {
            UnhookWinEvent(_hookMinMax);
            _hookMinMax = IntPtr.Zero;
        }
        
        // Clean up tracking dictionaries
        _windowOrderTracker.Clear();
        _candidatesPerMonitor.Clear();
        _monitorWorkAreas.Clear();
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

    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOOWNERZORDER = 0x0200;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    // manja struktura
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetMonitorInfo")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    // veća struktura
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true, EntryPoint = "GetMonitorInfo")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

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
}
