using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace UeDtLauncher.Gui;

public sealed partial class App : Application
{
    internal LauncherGuiInstance? GuiInstance { get; set; }
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var startup = LauncherStartupOptions.Discover(desktop.Args ?? Array.Empty<string>());
            var window = new MainWindow(startup);
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            if (GuiInstance is { } instance)
                instance.SetActivationHandler(token => Dispatcher.UIThread.InvokeAsync(
                    window.OpenFromTray, DispatcherPriority.Normal, token).GetTask());

            var icon = new TrayIcon { IsVisible = false };
            if (icon.NativeMenuExporter is not null)
            {
                var tray = new LauncherTray(this, window, icon);
                window.Closed += (_, _) => tray.Dispose();
            }
            else icon.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
