using System.Windows;
using System.Windows.Controls;
using GigaReplace.ViewModels;

namespace GigaReplace.Views;

public partial class ResultsView : UserControl
{
    public ResultsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ResultsViewModel old)
        {
            old.LogAppended -= OnLogAppended;
            old.LogCleared -= OnLogCleared;
        }

        if (e.NewValue is ResultsViewModel model)
        {
            model.LogAppended += OnLogAppended;
            model.LogCleared += OnLogCleared;
        }
    }

    private void OnLogAppended(object? sender, string text)
    {
        LogBox.AppendText(text);
        LogBox.ScrollToEnd();
    }

    private void OnLogCleared(object? sender, EventArgs e) => LogBox.Clear();
}
