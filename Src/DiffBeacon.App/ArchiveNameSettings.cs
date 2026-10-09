using Avalonia.Controls;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal static class ArchiveNameSettings
{
    internal static int[]? Capture(IReadOnlyList<int> choices) => choices.All(value => value == 28591) ? null : choices.ToArray();
    internal static IEnumerable<int> Choices(int[]? choices, int count) => choices ?? Enumerable.Repeat(28591, count);
    internal static ComboBox Picker(int selected = 28591) => new()
    {
        ItemsSource = ManagedArchiveReadOptions.SupportedGZipNameCodePages,
        SelectedItem = selected, Width = 130, Margin = new Avalonia.Thickness(4)
    };
    internal static int Selected(ComboBox picker) => picker.SelectedItem is int value ? value : 28591;
}
