using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace UeDtLauncher.Gui;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var startup = LauncherStartupOptions.Discover(desktop.Args ?? Array.Empty<string>());
            desktop.MainWindow = new MainWindow(startup);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
