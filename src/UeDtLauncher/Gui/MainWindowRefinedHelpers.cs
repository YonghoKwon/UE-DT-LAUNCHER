using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow
{
    private Control InfoTile(string title, string value, string caption)
    {
        var valueBlock = Txt(value, 17, true);
        valueBlock.MaxLines = 2;
        valueBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(valueBlock, value);
        return Card(new StackPanel
        {
            Spacing = 6,
            MinHeight = 92,
            Children =
            {
                Muted(title, 13),
                valueBlock,
                Muted(caption, 12)
            }
        }, 18);
    }

    private Control KeyValue(string key, string? value)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("130,*"),
            ColumnSpacing = 8
        };

        grid.Children.Add(Muted(key, 13));

        var valueBlock = Label(value ?? "-", 13, Fg());
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(valueBlock);

        return grid;
    }
}
