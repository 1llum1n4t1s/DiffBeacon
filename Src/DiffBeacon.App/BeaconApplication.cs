using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace DiffBeacon.App;

public sealed class BeaconApplication : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Program.Arguments, ImageApplicationOptionsStore.ForDesktop(), FolderApplicationOptionsStore.ForDesktop());
        base.OnFrameworkInitializationCompleted();
    }
}
