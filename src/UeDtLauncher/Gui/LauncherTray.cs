using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace UeDtLauncher.Gui;

internal sealed class LauncherTray : IDisposable
{
    private readonly Application _app;
    private readonly MainWindow _window;
    private readonly TrayIcon _icon;
    private readonly WindowIcon _normal = LoadIcon("normal");
    private readonly WindowIcon _update = LoadIcon("update");
    private readonly WindowIcon _disconnected = LoadIcon("disconnected");
    private readonly NativeMenu _menu = new();
    private readonly NativeMenuItem _check = new("업데이트 확인");
    private readonly List<NativeMenuItem> _rows = [];
    private readonly NativeMenuItemSeparator _separator = new();

    internal LauncherTray(Application app, MainWindow window, TrayIcon icon)
    {
        (_app, _window, _icon) = (app, window, icon);
        var open = new NativeMenuItem("열기");
        var exit = new NativeMenuItem("종료");
        open.Click += (_, _) => window.OpenFromTray();
        _check.Click += async (_, _) => await window.CheckTrayUpdatesAsync();
        exit.Click += async (_, _) => await window.ExitFromTrayAsync();
        _menu.Items.Add(open);
        _menu.Items.Add(_check);
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(exit);
        _menu.Opening += (_, _) => _check.IsEnabled = window.TrayCheckAvailable;
        _icon.Menu = _menu;
        _icon.Clicked += (_, _) => window.OpenFromTray();
        _window.TrayStatusChanged += Render;
        Render();
        TrayIcon.SetIcons(app, new TrayIcons { icon });
        _icon.IsVisible = true;
        window.EnableTray((config, platform, token) => CatalogSnapshotService.LoadAsync(config, platform, token, includeImages: false));
    }

    private void Render()
    {
        var status = _window.TrayStatus;
        var edition = LauncherBuildInfo.Edition == LauncherEdition.Developer ? "DT Launcher 개발자" : "DT Launcher";
        (_icon.Icon, _icon.ToolTipText) = status.ConnectionFailed ? (_disconnected, $"{edition} · 서비스 연결 안 됨")
            : status.HasUpdate ? (_update, $"{edition} · 업데이트 있음")
            : status.CheckFailed ? (_normal, $"{edition} · 업데이트 확인 필요") : (_normal, edition);
        _menu.Items.Remove(_separator);
        foreach (var row in _rows) _menu.Items.Remove(row);
        _rows.Clear();
        foreach (var project in status.Projects.Take(5))
            _rows.Add(new NativeMenuItem($"{project.Name}: {project.Status}") { IsEnabled = false });
        if (status.Projects.Count > 5)
            _rows.Add(new NativeMenuItem($"외 {status.Projects.Count - 5}개") { IsEnabled = false });
        if (_rows.Count == 0) return;
        _menu.Items.Insert(2, _separator);
        for (var index = 0; index < _rows.Count; index++)
            _menu.Items.Insert(3 + index, _rows[index]);
    }

    private static WindowIcon LoadIcon(string state)
    {
        var assembly = typeof(LauncherTray).Assembly.GetName().Name;
        using var stream = AssetLoader.Open(new Uri($"avares://{assembly}/Assets/Tray/{state}.ico"));
        return new WindowIcon(stream);
    }

    public void Dispose()
    {
        _window.TrayStatusChanged -= Render;
        // Avalonia removes and disposes the former icon when its collection is detached.
        TrayIcon.SetIcons(_app, null);
    }
}
