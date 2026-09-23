using System.Windows;
using System.Windows.Controls;

namespace GigaReplace.Views;

/// <summary>A message with a few choices. The first button is the default; "Cancel"/"OK" closes on Escape.</summary>
public partial class ChoiceWindow : Window
{
    private int _choice = -1;

    private ChoiceWindow() => InitializeComponent();

    public static int Show(Window? owner, string title, string message, IReadOnlyList<string> buttons, IEnumerable<string>? details, bool isError)
    {
        var window = new ChoiceWindow { Title = title };
        if (owner is { IsLoaded: true })
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.TitleText.Text = title;
        window.MessageText.Text = message;
        if (isError)
        {
            window.Glyph.Text = "";
            window.Glyph.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        }

        var lines = details?.ToList() ?? [];
        if (lines.Count > 0)
        {
            window.DetailsList.ItemsSource = lines;
            window.DetailsScroll.Visibility = Visibility.Visible;
        }

        for (int i = 0; i < buttons.Count; i++)
        {
            int index = i;
            var button = new Button
            {
                Content = buttons[i],
                MinWidth = 96,
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = i == 0,
                IsCancel = buttons.Count == 1 || buttons[i] is "Cancel" or "Close",
            };
            if (i == 0 && buttons.Count > 1)
            {
                button.SetResourceReference(StyleProperty, "AccentButtonStyle");
            }

            button.Click += (_, _) =>
            {
                window._choice = index;
                window.DialogResult = true;
            };
            window.Buttons.Children.Add(button);
        }

        window.ShowDialog();
        return window._choice;
    }
}
