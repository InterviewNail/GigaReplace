using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GigaReplace.ViewModels;

namespace GigaReplace.Views;

public partial class ReviewView : UserControl
{
    public ReviewView() => InitializeComponent();

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only rows open the dialog, not headers or scrollbars.
        for (var element = e.OriginalSource as System.Windows.DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is DataGridRow row && DataContext is ReviewViewModel model)
            {
                model.DecideInDialogCommand.Execute(row.Item);
                return;
            }
        }
    }
}
