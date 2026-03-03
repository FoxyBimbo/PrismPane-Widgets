using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using EchoUI.Models;
using EchoUI.Services;
using EchoUI.Views;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using Screen = System.Windows.Forms.Screen;

namespace EchoUI;

public partial class MainWindow : Window
{
    private const bool EnableStateDiagnostics = true;
    private readonly AppSettings _settings;
    private readonly ExtensionManager _extManager;
    private readonly ScriptEngine _scriptEngine;
    private readonly ObservableCollection<AppNotification> _notifications = [];
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _settingsSaveTimer;
    private bool _widgetsRestored;
    private bool _isRestoringWidgetsState;

    /// <summary>All open widget windows keyed by their unique instance ID.</summary>
    private readonly Dictionary<string, Window> _widgets = [];
    private bool _isShuttingDown;

    public static WidgetDockManager DockManager { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _extManager = new ExtensionManager(_settings);
        _scriptEngine = new ScriptEngine();

        var api = new TaskbarScriptApi(AddNotification, (_, _) => { });
        _scriptEngine.ExposeApi("echo", api);

        _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            if (!_isShuttingDown)
                _settings.Save();
        };

        // ── System tray icon ────────────────────────────────
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = new Icon(Application.GetResourceStream(new Uri("pack://application:,,,/AppIcon.ico"))!.Stream),
            Text = "EchoUI",
            Visible = true
        };

        RefreshTrayMenu();
        _trayIcon.DoubleClick += (_, _) => OpenSettings();

        Application.Current.Dispatcher.ShutdownStarted += (_, _) =>
        {
            if (_isShuttingDown)
                return;

            _isShuttingDown = true;
            PersistOpenWidgets();
            _settings.Save();
        };

        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        SourceInitialized += (_, _) => EnsureWidgetsRestored();
        Dispatcher.BeginInvoke(EnsureWidgetsRestored, DispatcherPriority.Loaded);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isShuttingDown)
            return;

        _isShuttingDown = true;
        PersistOpenWidgets();
        _settings.Save();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureWidgetsRestored();
    }

    private void EnsureWidgetsRestored()
    {
        if (_widgetsRestored)
            return;

        _widgetsRestored = true;
        _isRestoringWidgetsState = true;
        RestoreOpenWidgets();
        _isRestoringWidgetsState = false;
        Hide();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        CloseAllWidgets();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        DockManager.RestoreAll();
    }

    // ── Widget management ───────────────────────────────────

    private static string NormalizeWidgetKind(string kind) =>
        kind == "DesktopFolder" ? "Folder" : kind;

    private void SpawnWidget(string kind)
    {
        var normalizedKind = NormalizeWidgetKind(kind);

        if (normalizedKind == "TitleBar" && TryActivateExistingTitleBar())
            return;

        var (id, ws) = _settings.CreateWidgetInstance(normalizedKind);
        if (normalizedKind == "TitleBar")
            EnsureTitleBarDefaults(ws);

        ws.IsOpen = true;
        OpenWidget(id, ws);
        _settings.Save();
    }

    private void RestoreOpenWidgets()
    {
        foreach (var (id, ws) in _settings.Widgets.Where(pair => pair.Value.IsOpen).ToList())
            OpenWidget(id, ws);
    }

    private void OpenWidget(string id, WidgetSettings ws)
    {
        var normalizedKind = NormalizeWidgetKind(ws.Kind);

        if (normalizedKind == "TitleBar")
        {
            if (TryActivateExistingTitleBar())
            {
                _settings.RemoveWidgetInstance(id);
                _settings.Save();
                return;
            }

            EnsureTitleBarDefaults(ws);
        }

        Window? widget = normalizedKind switch
        {
            "ShortcutPanel" => new ShortcutPanelWidget(id, ws, _settings),
            "Folder" => new DesktopFolderWidget(id, ws, _settings),
            "TitleBar" => new TitleBarWidget(id, ws, _settings),
            "Clock" => new ClockWidget(id, ws, _settings),
            "CpuMonitor" => new CpuMonitorWidget(id, ws, _settings),
            "RamMonitor" => new RamMonitorWidget(id, ws, _settings),
            "Weather" => new WeatherWidget(id, ws, _settings),
            _ => null
        };

        if (widget is null)
            return;

        RegisterWidget(id, widget);
        ApplyWidgetPlacement(id, ws, widget);
        widget.Show();

        if (normalizedKind == "Folder")
        {
            foreach (var ext in _extManager.GetWidgets())
            {
                var code = _extManager.ReadScript(ext);
                _scriptEngine.Run(code, ext.ScriptType);
            }
        }
    }

    private void UpdateWidgetPlacement(string id, Window win)
    {
        var ws = _settings.GetWidgetSettings(id);
        ws.IsOpen = true;

        var edge = DockManager.GetEdge(id);
        ws.DockEdge = edge;

        var width = win.Width;
        var height = win.Height;
        if (win is DesktopFolderWidget folderWidget)
        {
            height = folderWidget.GetPersistedHeight();
            var activeFolder = folderWidget.ActiveFolderPath;
            if (!string.IsNullOrWhiteSpace(activeFolder))
            {
                ws.ActiveFolder = activeFolder;
                ws.Custom["DefaultFolder"] = activeFolder;
                ws.Custom.Remove("ActiveFolder");
            }
            if (folderWidget.IsMinimizeStateInitialized)
                ws.IsMinimized = folderWidget.IsWidgetMinimized;
            ws.Custom.Remove("IsMinimized");

            if (EnableStateDiagnostics)
                Debug.WriteLine($"[MainWindow:{id}] UpdateWidgetPlacement ActiveFolder='{ws.ActiveFolder}', IsMinimized={ws.IsMinimized}, WidgetMin={folderWidget.IsWidgetMinimized}, Initialized={folderWidget.IsMinimizeStateInitialized}");
        }

        ws.Width = width;
        ws.Height = height;
        ws.Left = win.Left;
        ws.Top = win.Top;

        if (edge != DockEdge.None)
        {
            var thickness = edge is DockEdge.Left or DockEdge.Right
                ? (win.ActualWidth > 0 ? win.ActualWidth : width)
                : (win.ActualHeight > 0 ? win.ActualHeight : height);
            ws.DockThickness = thickness;
        }
        else
        {
            ws.DockThickness = null;
        }

        ScheduleSettingsSave();
    }

    private void ScheduleSettingsSave()
    {
        if (_isShuttingDown || _isRestoringWidgetsState)
            return;

        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void RegisterWidget(string id, Window widget)
    {
        _widgets[id] = widget;
        widget.Closed += OnWidgetClosed;
        widget.LocationChanged += (_, _) => UpdateWidgetPlacement(id, widget);
        widget.SizeChanged += (_, _) => UpdateWidgetPlacement(id, widget);
        DockManager.TrackFloatingWidget(widget);
    }

    private static void EnsureTitleBarDefaults(WidgetSettings ws)
    {
        var height = ws.Height is > 0 ? ws.Height.Value : 36;
        ws.Height = height;
        ws.DockEdge = DockEdge.Top;
        ws.DockThickness = height;
        ws.Top = 0;
    }

    private bool TryActivateExistingTitleBar()
    {
        var existing = _widgets.Values.OfType<TitleBarWidget>().FirstOrDefault();
        if (existing is null)
            return false;

        if (existing.WindowState == WindowState.Minimized)
            existing.WindowState = WindowState.Normal;

        existing.Activate();
        return true;
    }

    private void ApplyWidgetPlacement(string id, WidgetSettings ws, Window widget)
    {
        if (ws.Width is > 0)
            widget.Width = ws.Width.Value;
        if (ws.Height is > 0)
            widget.Height = ws.Height.Value;

        if (ws.Left.HasValue && ws.Top.HasValue)
        {
            widget.WindowStartupLocation = WindowStartupLocation.Manual;
            widget.Left = ws.Left.Value;
            widget.Top = ws.Top.Value;
            EnsureWidgetOnScreen(widget);
        }

        widget.SourceInitialized += (_, _) =>
        {
            var edge = ws.DockEdge;
            var thickness = ws.DockThickness ?? (ws.DockEdge is DockEdge.Left or DockEdge.Right
                ? widget.Width
                : widget.Height);

            if (widget is TitleBarWidget)
            {
                edge = DockEdge.Top;
                thickness = widget.Height > 0 ? widget.Height : (ws.Height ?? 36);
                ws.DockEdge = edge;
                ws.DockThickness = thickness;
            }

            if (edge == DockEdge.None)
                return;

            switch (widget)
            {
                case DesktopFolderWidget folderWidget:
                    folderWidget.RestoreDock(edge, thickness);
                    break;
                case ShortcutPanelWidget shortcutWidget:
                    shortcutWidget.RestoreDock(edge, thickness);
                    break;
                default:
                    DockManager.Dock(id, widget, edge, thickness);
                    break;
            }
        };
    }

    private static void EnsureWidgetOnScreen(Window widget)
    {
        if (double.IsNaN(widget.Width) || double.IsNaN(widget.Height))
            return;

        var rect = new Rectangle(
            (int)Math.Round(widget.Left),
            (int)Math.Round(widget.Top),
            (int)Math.Round(widget.Width),
            (int)Math.Round(widget.Height));

        bool intersects = Screen.AllScreens
            .Any(screen => rect.IntersectsWith(screen.WorkingArea));

        if (!intersects)
        {
            var screen = Screen.PrimaryScreen!.WorkingArea;
            widget.Left = screen.Left + (screen.Width - widget.Width) / 2;
            widget.Top = screen.Top + (screen.Height - widget.Height) / 2;
        }
    }

    private void OnWidgetClosed(object? sender, EventArgs e)
    {
        if (sender is not Window win) return;

        string? id = sender switch
        {
            DesktopFolderWidget dfw => dfw.WidgetId,
            ShortcutPanelWidget spw => spw.WidgetId,
            TitleBarWidget tbw => tbw.WidgetId,
            ClockWidget cw => cw.WidgetId,
            CpuMonitorWidget cmw => cmw.WidgetId,
            RamMonitorWidget rmw => rmw.WidgetId,
            WeatherWidget ww => ww.WidgetId,
            _ => null
        };
        if (id is null) return;

        var isAppClosing = _isShuttingDown || Application.Current.Dispatcher.HasShutdownStarted;

        if (win is DesktopFolderWidget folderWidget)
            folderWidget.PersistState();

        if (isAppClosing)
        {
            UpdateWidgetPlacement(id, win);
            _settings.GetWidgetSettings(id).IsOpen = false;
        }
        else
        {
            _settings.RemoveWidgetInstance(id);
        }

        DockManager.Undock(id);
        DockManager.UntrackFloatingWidget(win);
        win.Closed -= OnWidgetClosed;
        _widgets.Remove(id);

        _settings.Save();

        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
    }

    private void PersistOpenWidgets()
    {
        foreach (var (id, win) in _widgets)
        {
            if (win is DesktopFolderWidget folderWidget)
            {
                folderWidget.PersistState();
                var ws = _settings.GetWidgetSettings(id);
                ws.IsMinimized = folderWidget.IsWidgetMinimized;
            }
            UpdateWidgetPlacement(id, win);
            _settings.GetWidgetSettings(id).IsOpen = true;
        }
    }

    private void CloseAllWidgets()
    {
        _isShuttingDown = true;
        PersistOpenWidgets();
        foreach (var (id, win) in _widgets.ToList())
        {
            DockManager.Undock(id);
            DockManager.UntrackFloatingWidget(win);
            win.Closed -= OnWidgetClosed;
            win.Close();
        }
        _widgets.Clear();
        _settings.Save();

        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
    }

    private void AddNotification(string title, string message)
    {
        Dispatcher.Invoke(() =>
        {
            _notifications.Insert(0, new AppNotification
            {
                Title = title,
                Message = message,
                Timestamp = DateTime.Now
            });
        });
    }

    // ── Settings ────────────────────────────────────────────
    private void OpenSettings()
    {
        var win = new SettingsWindow(_settings, _extManager, spawnWidget: SpawnWidget, settingsApplied: RefreshTrayMenu);
        if (win.ShowDialog() == true)
        {
            ApplyWidgetSettings();
        }
    }

    public void OpenSettingsFromWidget()
    {
        OpenSettings();
    }

    private void RefreshTrayMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();

        var builtinWidgets = new HashSet<string>(["Folder", "ShortcutPanel", "TitleBar", "Clock", "CpuMonitor", "RamMonitor", "Weather"], StringComparer.OrdinalIgnoreCase);
        foreach (var kind in builtinWidgets)
            menu.Items.Add($"New {kind} Widget", null, (_, _) => SpawnWidget(kind));

        var extensionWidgets = _extManager.GetWidgets()
            .Where(w => !builtinWidgets.Contains(w.Name))
            .ToList();

        if (builtinWidgets.Count > 0 && extensionWidgets.Count > 0)
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        foreach (var widget in extensionWidgets)
            menu.Items.Add($"New {widget.Name} Widget", null, (_, _) => SpawnWidget(widget.Name));

        if (menu.Items.Count > 0)
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _trayIcon.ContextMenuStrip = menu;
    }

    private void ApplyWidgetSettings()
    {
        foreach (var (id, win) in _widgets)
        {
            var ws = _settings.GetWidgetSettings(id);
            win.Topmost = ws.Topmost;
            win.Opacity = ws.Opacity;
            ThemeHelper.ApplyToElement(win, ws.CustomColors);
        }
    }

    // ── Exit ────────────────────────────────────────────────
    private void ExitApp()
    {
        if (!_isShuttingDown)
            CloseAllWidgets();
        DockManager.RestoreAll();
        Application.Current.Shutdown();
    }
}