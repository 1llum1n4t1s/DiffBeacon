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
        Styles.Add(AvaloniaEditTheme());
    }

    /// <summary>
    /// 差分タブの本文エディター（AvaloniaEdit）の見た目。AvaloniaEdit のテーマはコンパイル済みXAMLで、読込みは型名で探す。
    /// その型が発行時の削減で消えないよう、DiffBeacon.App.csproj で AvaloniaEdit を TrimmerRootAssembly に指定している。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "AvaloniaEdit は TrimmerRootAssembly で丸ごと保持しているため、コンパイル済みテーマの読込みに必要な型は削除されない。")]
    private static Avalonia.Styling.IStyle AvaloniaEditTheme()
        => new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DiffBeacon/"))
        { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") };

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Program.Arguments, ImageApplicationOptionsStore.ForDesktop(), FolderApplicationOptionsStore.ForDesktop(),
                TableApplicationOptionsStore.ForDesktop(), FolderColumnOptionsStore.ForDesktop());
        base.OnFrameworkInitializationCompleted();
    }
}
