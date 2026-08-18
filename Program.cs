using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WinExplorerForceTabs;

internal static class Program
{
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_CLOSE = 0xF060;
    private const int CmdNewTab = 0xA21B; // Ctrl+T
    private const int CmdCloseTab = 0xA021; // Ctrl+W, same command used by ExplorerTabUtility

    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_LAYERED = 0x00080000L;
    private const uint LWA_ALPHA = 0x00000002;
    private const int ExplorerUiReadyTimeoutMs = 1200;
    private const int ExplorerUiPollMs = 50;
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "win-explorer-force-tabs.log");

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "WinExplorerForceTabs.Singleton", out var firstInstance);
        if (!firstInstance)
            return;

        InitializeLog();
        Log($"START pid={Environment.ProcessId} version=1.0.0 log={LogPath}");
        var watcher = new ExplorerWatcher();
        watcher.Run();
    }

    private static void InitializeLog()
    {
        try
        {
            File.WriteAllText(
                LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} LOG_START{Environment.NewLine}");
        }
        catch { }
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private sealed class ExplorerWatcher
    {
        private readonly ConcurrentDictionary<nint, DateTime> _knownTopWindows = new();
        private readonly ConcurrentDictionary<nint, nint> _hiddenWindows = new();
        private readonly ManualResetEventSlim _hookReady = new(false);
        private WinEventDelegate? _showHookCallback;
        private nint _showHookHandle;
        private bool _busy;

        public void Run()
        {
            SnapshotExistingWindows();
            StartShowHook();

            while (true)
            {
                try
                {
                    if (!_busy)
                        CheckForNewExplorerWindow();
                }
                catch
                {
                    // Explorer is restarted or COM is temporarily unavailable: retry next cycle.
                }

                Thread.Sleep(250);
            }
        }

        private void SnapshotExistingWindows()
        {
            foreach (var w in EnumerateExplorerTabs())
                _knownTopWindows.TryAdd(w.TopLevelHandle, DateTime.UtcNow);
        }

        private void StartShowHook()
        {
            _showHookCallback = OnWindowShown;

            var hookThread = new Thread(() =>
            {
                _showHookHandle = SetWinEventHook(
                    EVENT_OBJECT_SHOW,
                    EVENT_OBJECT_SHOW,
                    0,
                    _showHookCallback,
                    0,
                    0,
                    WINEVENT_OUTOFCONTEXT);

                Log($"HOOK handle=0x{_showHookHandle:X}");
                _hookReady.Set();

                if (_showHookHandle == 0)
                    return;

                try
                {
                    while (GetMessage(out var msg, 0, 0, 0) > 0)
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }
                }
                finally
                {
                    UnhookWinEvent(_showHookHandle);
                }
            })
            {
                IsBackground = true,
                Name = "WinExplorerForceTabs.WinEventHook"
            };

            hookThread.SetApartmentState(ApartmentState.STA);
            hookThread.Start();
            _hookReady.Wait(1500);
        }

        private void OnWindowShown(
            nint hWinEventHook,
            uint eventType,
            nint hWnd,
            int idObject,
            int idChild,
            uint idEventThread,
            uint eventTime)
        {
            if (hWnd == 0 || idObject != OBJID_WINDOW || idChild != 0)
                return;

            if (!HasClass(hWnd, "CabinetWClass"))
                return;

            // CabinetWClass is shared by File Explorer and other Shell windows.
            // Qualify positively: a real Windows 11 File Explorer window must expose
            // its tab strip through UI Automation. Retry because the HWND can appear
            // before the XAML/UIA tree is ready (notably after Win+E).
            if (!HasExplorerTabStrip(hWnd, ExplorerUiReadyTimeoutMs, diagnostic: true))
            {
                Log($"HOOK_IGNORE_NO_TABSTRIP sourceTop=0x{hWnd:X}");
                return;
            }

            // Do not touch windows already known at startup or windows merely being restored.
            if (_knownTopWindows.ContainsKey(hWnd))
                return;

            // Hide only if another Explorer top-level window already exists.
            // The very first Explorer window must remain visible.
            var hasTarget = _knownTopWindows.Keys.Any(other => other != hWnd && IsWindow(other));
            if (!hasTarget)
                return;

            HideSourceWindow(hWnd);
        }

        private void HideSourceWindow(nint hWnd)
        {
            if (_hiddenWindows.ContainsKey(hWnd) || !IsWindow(hWnd))
                return;

            try
            {
                var originalExStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
                if (!_hiddenWindows.TryAdd(hWnd, originalExStyle))
                    return;

                var exStyle = (long)originalExStyle;
                if ((exStyle & WS_EX_LAYERED) == 0)
                    SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_LAYERED));

                if (!SetLayeredWindowAttributes(hWnd, 0, 0, LWA_ALPHA))
                {
                    // If transparency fails, restore the style and leave the window visible.
                    SetWindowLongPtr(hWnd, GWL_EXSTYLE, originalExStyle);
                    _hiddenWindows.TryRemove(hWnd, out _);
                    Log($"HIDE_FAIL sourceTop=0x{hWnd:X}");
                    return;
                }

                Log($"HIDE sourceTop=0x{hWnd:X}");
            }
            catch (Exception ex)
            {
                _hiddenWindows.TryRemove(hWnd, out _);
                Log($"HIDE_ERROR sourceTop=0x{hWnd:X} {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void RestoreHiddenWindow(nint hWnd)
        {
            if (!_hiddenWindows.TryRemove(hWnd, out var originalExStyle))
                return;

            if (!IsWindow(hWnd))
                return;

            try
            {
                SetLayeredWindowAttributes(hWnd, 0, 255, LWA_ALPHA);
                SetWindowLongPtr(hWnd, GWL_EXSTYLE, originalExStyle);
                Log($"RESTORE sourceTop=0x{hWnd:X}");
            }
            catch (Exception ex)
            {
                Log($"RESTORE_ERROR sourceTop=0x{hWnd:X} {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void CheckForNewExplorerWindow()
        {
            var tabs = EnumerateExplorerTabs();
            var topWindows = tabs.Select(t => t.TopLevelHandle).Where(h => h != 0).Distinct().ToArray();

            foreach (var hWnd in topWindows)
            {
                if (_knownTopWindows.ContainsKey(hWnd))
                    continue;

                _knownTopWindows[hWnd] = DateTime.UtcNow;

                var tabsInNewWindow = tabs.Where(t => t.TopLevelHandle == hWnd).ToArray();
                if (tabsInNewWindow.Length != 1)
                {
                    RestoreHiddenWindow(hWnd);
                    continue;
                }

                var target = ChooseTargetWindow(topWindows, hWnd, tabs);
                if (target == 0)
                {
                    RestoreHiddenWindow(hWnd);
                    continue; // First Explorer window: keep it.
                }

                // The EVENT_OBJECT_SHOW hook can fire before Explorer exposes its COM/UIA state.
                // At this point EnumerateExplorerTabs() has positively qualified this HWND
                // as a tab-capable File Explorer window, so it is safe to hide it.
                HideSourceWindow(hWnd);

                Log($"NEW sourceTop=0x{hWnd:X} sourceTab=0x{tabsInNewWindow[0].TabHandle:X} targetTop=0x{target:X} location={tabsInNewWindow[0].Location}");
                MoveWindowIntoTab(tabsInNewWindow[0], target);
                break;
            }

            // Forget windows that no longer exist.
            var alive = topWindows.ToHashSet();
            foreach (var old in _knownTopWindows.Keys.Where(h => !alive.Contains(h)).ToArray())
                _knownTopWindows.TryRemove(old, out _);
        }

        private static nint ChooseTargetWindow(nint[] allWindows, nint source, List<ExplorerTab> tabs)
        {
            return allWindows
                .Where(h => h != source && IsWindow(h))
                .OrderByDescending(h => tabs.Count(t => t.TopLevelHandle == h))
                .FirstOrDefault();
        }

        private void MoveWindowIntoTab(ExplorerTab source, nint targetTopWindow)
        {
            if (string.IsNullOrWhiteSpace(source.Location))
            {
                RestoreHiddenWindow(source.TopLevelHandle);
                return;
            }

            _busy = true;
            try
            {
                var before = EnumerateExplorerTabs()
                    .Where(t => t.TopLevelHandle == targetTopWindow)
                    .Select(t => t.TabHandle)
                    .ToHashSet();

                var activeTab = FindWindowEx(targetTopWindow, 0, "ShellTabWindowClass", null);
                if (activeTab == 0)
                    return;

                // Ask Explorer itself to create a tab.
                PostMessage(activeTab, WM_COMMAND, CmdNewTab, 0);

                ExplorerTab? newTab = null;
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 2500)
                {
                    Thread.Sleep(50);
                    newTab = EnumerateExplorerTabs().FirstOrDefault(t =>
                        t.TopLevelHandle == targetTopWindow &&
                        t.TabHandle != 0 &&
                        !before.Contains(t.TabHandle));

                    if (newTab is not null)
                        break;
                }

                if (newTab is null)
                    return;

                try
                {
                    // Shell virtual folders returned by Explorer can look like
                    // ::{GUID}. Navigate2 expects shell:::{GUID}.
                    var destination = NormalizeLocation(source.Location);
                    Log($"NAVIGATE source={source.Location} destination={destination}");
                    newTab.ComWindow.Navigate2(destination);
                    SetForegroundWindow(targetTopWindow);
                    ShowWindow(targetTopWindow, 9); // SW_RESTORE

                    // Give the target tab time to accept navigation before closing the source.
                    var navWait = Stopwatch.StartNew();
                    while (navWait.ElapsedMilliseconds < 1500)
                    {
                        Thread.Sleep(50);
                        var targetLocation = GetLocation(newTab.ComWindow);
                        if (!string.IsNullOrWhiteSpace(targetLocation) &&
                            string.Equals(targetLocation.TrimEnd('\\'), source.Location.TrimEnd('\\'),
                                StringComparison.OrdinalIgnoreCase))
                            break;
                    }

                    // Close the SOURCE through Explorer's own Ctrl+W command first.
                    // ExplorerTabUtility uses this internal command to close a tab.
                    Log($"CLOSE sourceTop=0x{source.TopLevelHandle:X} sourceTab=0x{source.TabHandle:X}");
                    SendMessage(source.TabHandle, WM_COMMAND, CmdCloseTab, 1);

                    var closeWait = Stopwatch.StartNew();
                    while (closeWait.ElapsedMilliseconds < 800 && IsWindow(source.TopLevelHandle))
                        Thread.Sleep(50);

                    // COM fallback.
                    if (IsWindow(source.TopLevelHandle))
                    {
                        try { source.ComWindow.Quit(); } catch { }
                        Thread.Sleep(200);
                    }

                    // Last-resort top-level close methods.
                    if (IsWindow(source.TopLevelHandle))
                    {
                        SendMessage(source.TopLevelHandle, WM_SYSCOMMAND, SC_CLOSE, 0);
                        Thread.Sleep(200);
                    }
                    if (IsWindow(source.TopLevelHandle))
                        SendMessage(source.TopLevelHandle, WM_CLOSE, 0, 0);

                    Thread.Sleep(200);
                    var sourceAlive = IsWindow(source.TopLevelHandle);
                    Log($"CLOSE_RESULT sourceAlive={sourceAlive}");

                    if (sourceAlive)
                        RestoreHiddenWindow(source.TopLevelHandle);
                    else
                        _hiddenWindows.TryRemove(source.TopLevelHandle, out _);
                }
                catch (Exception ex)
                {
                    // Leave the original window intact if navigation fails.
                    Log($"ERROR MoveWindowIntoTab {ex.GetType().Name}: {ex.Message}");
                    RestoreHiddenWindow(source.TopLevelHandle);
                }
            }
            finally
            {
                // Covers early returns (no active tab, tab creation timeout, etc.).
                if (IsWindow(source.TopLevelHandle) && _hiddenWindows.ContainsKey(source.TopLevelHandle))
                    RestoreHiddenWindow(source.TopLevelHandle);

                _busy = false;
            }
        }

        private static List<ExplorerTab> EnumerateExplorerTabs()
        {
            var result = new List<ExplorerTab>();
            object? shell = null;
            object? windows = null;

            // One UIA qualification per top-level HWND for this enumeration pass.
            // Shell.Application.Windows() can contain several COM entries (tabs) for one HWND.
            var explorerUiCache = new Dictionary<nint, bool>();

            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is null)
                    return result;

                shell = Activator.CreateInstance(shellType);
                if (shell is null)
                    return result;

                dynamic dShell = shell;
                windows = dShell.Windows();
                dynamic dWindows = windows;

                int count = dWindows.Count;
                for (var i = 0; i < count; i++)
                {
                    object? item = null;
                    try
                    {
                        item = dWindows.Item(i);
                        if (item is null)
                            continue;

                        dynamic window = item;
                        string fullName = Convert.ToString(window.FullName) ?? string.Empty;
                        if (!fullName.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            Marshal.FinalReleaseComObject(item);
                            continue;
                        }

                        var tabHandle = GetTabHandle(item);
                        if (tabHandle == 0)
                        {
                            Marshal.FinalReleaseComObject(item);
                            continue;
                        }

                        var top = GetAncestor(tabHandle, 2); // GA_ROOT
                        if (top == 0 || !HasClass(top, "CabinetWClass"))
                        {
                            Marshal.FinalReleaseComObject(item);
                            continue;
                        }

                        // Do not infer File Explorer from explorer.exe/CabinetWClass alone:
                        // Control Panel and other Shell namespace windows can share both.
                        // A window is eligible only if it exposes the actual Windows 11
                        // File Explorer tab strip through UI Automation. This intentionally
                        // accepts virtual File Explorer locations such as Home and This PC.
                        if (!explorerUiCache.TryGetValue(top, out var isTabbedExplorer))
                        {
                            isTabbedExplorer = HasExplorerTabStrip(top);
                            explorerUiCache[top] = isTabbedExplorer;
                        }

                        if (!isTabbedExplorer)
                        {
                            Marshal.FinalReleaseComObject(item);
                            continue;
                        }

                        var location = GetLocation(window);
                        if (string.IsNullOrWhiteSpace(location))
                        {
                            Marshal.FinalReleaseComObject(item);
                            continue;
                        }

                        result.Add(new ExplorerTab(item, tabHandle, top, location));
                        item = null; // ownership transferred to ExplorerTab
                    }
                    catch
                    {
                        if (item is not null && Marshal.IsComObject(item))
                            Marshal.FinalReleaseComObject(item);
                    }
                }
            }
            catch
            {
                // Explorer may be restarting.
            }
            finally
            {
                if (windows is not null && Marshal.IsComObject(windows))
                    Marshal.FinalReleaseComObject(windows);
                if (shell is not null && Marshal.IsComObject(shell))
                    Marshal.FinalReleaseComObject(shell);
            }

            return result;
        }


        private static bool HasExplorerTabStrip(
            nint topLevelHandle,
            int waitMilliseconds = 0,
            bool diagnostic = false)
        {
            if (topLevelHandle == 0 || !IsWindow(topLevelHandle))
            {
                if (diagnostic)
                    Log($"QUALIFY_FAIL sourceTop=0x{topLevelHandle:X} reason=invalid-window");
                return false;
            }

            if (!HasClass(topLevelHandle, "CabinetWClass"))
            {
                if (diagnostic)
                    Log($"QUALIFY_FAIL sourceTop=0x{topLevelHandle:X} reason=class-not-CabinetWClass");
                return false;
            }

            if (diagnostic)
                Log($"QUALIFY_BEGIN sourceTop=0x{topLevelHandle:X} waitMs={waitMilliseconds}");

            var sw = Stopwatch.StartNew();
            var attempt = 0;
            var sawNativeShellTab = false;
            var sawUiaRoot = false;
            string lastError = "none";

            do
            {
                attempt++;
                nint shellTabHwnd = 0;
                var uiaMatched = false;

                try
                {
                    // ShellTabWindowClass is a cheap prefilter only. It is not sufficient
                    // by itself to qualify a real File Explorer window.
                    shellTabHwnd = FindWindowEx(topLevelHandle, 0, "ShellTabWindowClass", null);
                    if (shellTabHwnd != 0)
                    {
                        sawNativeShellTab = true;

                        var root = AutomationElement.FromHandle(topLevelHandle);
                        if (root is not null)
                        {
                            sawUiaRoot = true;
                            uiaMatched = ContainsTopExplorerTabControl(root);
                        }
                    }

                    if (diagnostic)
                    {
                        Log(
                            $"QUALIFY_ATTEMPT sourceTop=0x{topLevelHandle:X} " +
                            $"attempt={attempt} elapsedMs={sw.ElapsedMilliseconds} " +
                            $"shellTabHwnd=0x{shellTabHwnd:X} uiaTabMatch={uiaMatched}");
                    }

                    if (uiaMatched)
                    {
                        if (diagnostic)
                        {
                            Log(
                                $"QUALIFY_OK sourceTop=0x{topLevelHandle:X} " +
                                $"attempt={attempt} elapsedMs={sw.ElapsedMilliseconds} " +
                                $"shellTabHwnd=0x{shellTabHwnd:X}");
                        }
                        return true;
                    }
                }
                catch (ElementNotAvailableException ex)
                {
                    lastError = $"{ex.GetType().Name}: {ex.Message}";
                }
                catch (InvalidOperationException ex)
                {
                    lastError = $"{ex.GetType().Name}: {ex.Message}";
                }
                catch (COMException ex)
                {
                    lastError = $"{ex.GetType().Name}: {ex.Message}";
                }
                catch (Exception ex)
                {
                    lastError = $"{ex.GetType().Name}: {ex.Message}";
                }

                if (sw.ElapsedMilliseconds >= waitMilliseconds)
                    break;

                Thread.Sleep(ExplorerUiPollMs);
            }
            while (IsWindow(topLevelHandle));

            if (diagnostic)
            {
                Log(
                    $"QUALIFY_FAIL sourceTop=0x{topLevelHandle:X} " +
                    $"attempts={attempt} elapsedMs={sw.ElapsedMilliseconds} " +
                    $"sawNativeShellTab={sawNativeShellTab} sawUiaRoot={sawUiaRoot} " +
                    $"lastError={SanitizeLog(lastError)}");

                DumpTopUiAutomationTree(topLevelHandle);
            }

            return false;
        }

        private static void DumpTopUiAutomationTree(nint topLevelHandle)
        {
            const int maxDepth = 8;
            const int maxElements = 160;

            try
            {
                var root = AutomationElement.FromHandle(topLevelHandle);
                if (root is null)
                {
                    Log($"UIA_DUMP_FAIL sourceTop=0x{topLevelHandle:X} reason=no-root");
                    return;
                }

                Log($"UIA_DUMP_BEGIN sourceTop=0x{topLevelHandle:X}");

                var walker = TreeWalker.ControlViewWalker;
                var queue = new Queue<(AutomationElement Element, int Depth)>();
                queue.Enqueue((root, 0));
                var visited = 0;

                while (queue.Count > 0 && visited < maxElements)
                {
                    var (element, depth) = queue.Dequeue();
                    visited++;

                    try
                    {
                        var current = element.Current;
                        var controlType = current.ControlType?.ProgrammaticName ?? "?";
                        var name = SanitizeLog(current.Name);
                        var automationId = SanitizeLog(current.AutomationId);
                        var className = SanitizeLog(current.ClassName);
                        var rect = current.BoundingRectangle;

                        Log(
                            $"UIA depth={depth} type={controlType} " +
                            $"name=\"{name}\" automationId=\"{automationId}\" " +
                            $"class=\"{className}\" " +
                            $"rect={rect.Left:0},{rect.Top:0},{rect.Width:0},{rect.Height:0}");

                        if (depth >= maxDepth)
                            continue;

                        var child = walker.GetFirstChild(element);
                        while (child is not null)
                        {
                            queue.Enqueue((child, depth + 1));
                            child = walker.GetNextSibling(child);
                        }
                    }
                    catch (ElementNotAvailableException)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                Log($"UIA_DUMP_END sourceTop=0x{topLevelHandle:X} elements={visited}");
            }
            catch (Exception ex)
            {
                Log(
                    $"UIA_DUMP_FAIL sourceTop=0x{topLevelHandle:X} " +
                    $"error={ex.GetType().Name}: {SanitizeLog(ex.Message)}");
            }
        }

        private static string SanitizeLog(string? value)
        {
            return (value ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("\"", "'");
        }

        private static bool ContainsTopExplorerTabControl(AutomationElement root)
        {
            System.Windows.Rect rootRect;
            try
            {
                rootRect = root.Current.BoundingRectangle;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }

            if (rootRect.IsEmpty || rootRect.Height <= 0)
                return false;

            // File Explorer's tab strip belongs to the window chrome. Restrict candidates
            // to the upper part of the window so a Tab control inside Shell content does
            // not accidentally qualify a non-Explorer Shell window.
            var maxTabTop = rootRect.Top + Math.Min(220.0, rootRect.Height * 0.30);

            var walker = TreeWalker.ControlViewWalker;
            var queue = new Queue<(AutomationElement Element, int Depth)>();
            queue.Enqueue((root, 0));

            // Keep the UIA walk bounded. The Explorer chrome is near the root and should
            // be found long before these limits; bounding also avoids walking huge views.
            const int maxDepth = 12;
            const int maxElements = 500;
            var visited = 0;

            while (queue.Count > 0 && visited < maxElements)
            {
                var (element, depth) = queue.Dequeue();
                visited++;

                try
                {
                    if (depth > 0 && element.Current.ControlType == ControlType.Tab)
                    {
                        var current = element.Current;
                        var rect = current.BoundingRectangle;

                        // Windows 11 File Explorer exposes the real tab strip as:
                        //   Tab (AutomationId="TabView", class=Microsoft.UI.Xaml.Controls.TabView)
                        //     -> List (AutomationId="TabListView")
                        //        -> TabItem ...
                        //     -> Button (AutomationId="AddButton")
                        // The TabItem is therefore NOT a direct child of TabView.
                        // Qualify by the Explorer tab-view signature and a descendant TabItem,
                        // without relying on localized names such as "Add new tab".
                        if (!rect.IsEmpty && rect.Top <= maxTabTop &&
                            string.Equals(current.AutomationId, "TabView", StringComparison.Ordinal) &&
                            string.Equals(current.ClassName, "Microsoft.UI.Xaml.Controls.TabView", StringComparison.Ordinal) &&
                            HasExplorerTabViewChildren(element))
                            return true;
                    }

                    if (depth >= maxDepth)
                        continue;

                    var child = walker.GetFirstChild(element);
                    while (child is not null)
                    {
                        queue.Enqueue((child, depth + 1));
                        child = walker.GetNextSibling(child);
                    }
                }
                catch (ElementNotAvailableException)
                {
                    // UI tree changed during traversal; the next poll will retry.
                }
                catch (InvalidOperationException)
                {
                    // Provider temporarily unavailable for this branch.
                }
            }

            return false;
        }

        private static bool HasExplorerTabViewChildren(AutomationElement tabControl)
        {
            try
            {
                var tabList = tabControl.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "TabListView"));

                if (tabList is null || tabList.Current.ControlType != ControlType.List)
                    return false;

                var tabItem = tabList.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

                if (tabItem is null)
                    return false;

                // AddButton is a second positive signature of File Explorer's tab strip.
                // Its AutomationId is language-independent; the visible Name is ignored.
                var addButton = tabControl.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "AddButton"));

                return addButton is not null && addButton.Current.ControlType == ControlType.Button;
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (COMException)
            {
            }

            return false;
        }

        private static string NormalizeLocation(string location)
        {
            location = Environment.ExpandEnvironmentVariables(location ?? string.Empty).Trim();

            // Same normalization used by ExplorerTabUtility for virtual Shell locations.
            if (location.StartsWith("::", StringComparison.Ordinal))
                location = $"shell:{location}";
            else if (location.StartsWith("{", StringComparison.Ordinal))
                location = $"shell:::{location}";

            return location.Trim(' ', '/', '\\', '\n', '\'', '"').Replace('/', '\\');
        }

        private static string GetLocation(dynamic window)
        {
            try
            {
                string path = Convert.ToString(window.Document.Folder.Self.Path) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(path))
                    return path;
            }
            catch { }

            try
            {
                string url = Convert.ToString(window.LocationURL) ?? string.Empty;
                if (url.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return uri.LocalPath;
                return url;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static nint GetTabHandle(object comWindow)
        {
            try
            {
                if (comWindow is not IServiceProvider sp)
                    return 0;

                var guid = typeof(IShellBrowser).GUID;
                var hr = sp.QueryService(ref guid, ref guid, out var browser);
                if (hr != 0 || browser is null)
                    return 0;

                try
                {
                    browser.GetWindow(out var hWnd);
                    return hWnd;
                }
                finally
                {
                    Marshal.ReleaseComObject(browser);
                }
            }
            catch
            {
                return 0;
            }
        }

        private static bool HasClass(nint hWnd, string className)
        {
            var buffer = new char[256];
            var n = GetClassName(hWnd, buffer, buffer.Length);
            return n > 0 && new string(buffer, 0, n).Equals(className, StringComparison.Ordinal);
        }
    }

    private sealed class ExplorerTab : IDisposable
    {
        public dynamic ComWindow { get; }
        public nint TabHandle { get; }
        public nint TopLevelHandle { get; }
        public string Location { get; }

        public ExplorerTab(object comWindow, nint tabHandle, nint topLevelHandle, string location)
        {
            ComWindow = comWindow;
            TabHandle = tabHandle;
            TopLevelHandle = topLevelHandle;
            Location = location;
        }

        public void Dispose()
        {
            try
            {
                object o = ComWindow;
                if (Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
            }
            catch { }
        }
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellBrowser? ppvObject);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig]
        int GetWindow(out nint handle);
    }

    private delegate void WinEventDelegate(
        nint hWinEventHook,
        uint eventType,
        nint hWnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint HWnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Pt;
        public uint LPrivate;
    }

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern nint FindWindowEx(nint hwndParent, nint hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);
}
