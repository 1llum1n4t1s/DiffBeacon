using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace DiffBeacon.App;

public sealed class BeaconApplication : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light; // 本家WinMergeと同じ明るい配色を既定にする。暗い配色は「表示」メニューで切り替える。
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Program.Arguments, ImageApplicationOptionsStore.ForDesktop(), FolderApplicationOptionsStore.ForDesktop(),
                TableApplicationOptionsStore.ForDesktop());
        base.OnFrameworkInitializationCompleted();
    }
}
