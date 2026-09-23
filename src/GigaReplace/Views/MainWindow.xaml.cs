using System.ComponentModel;
using System.Windows;
using GigaReplace.ViewModels;

namespace GigaReplace.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel model)
        {
            if (model.IsBusy && ChoiceWindow.Show(this, "Still working", "A scan or run is still in progress. Close anyway?", ["Keep working", "Close"], null, false) != 1)
            {
                e.Cancel = true;
                return;
            }

            model.OnClosing();
        }

        base.OnClosing(e);
    }
}
