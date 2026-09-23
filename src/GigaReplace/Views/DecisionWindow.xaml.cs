using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using GigaReplace.ViewModels;

namespace GigaReplace.Views;

public partial class DecisionWindow : Window
{
    private readonly DecisionViewModel _model;

    public DecisionWindow(DecisionViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        model.CloseRequested += (_, _) => DialogResult = true;
        model.PropertyChanged += OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DecisionViewModel.ShowCustom) && _model.ShowCustom)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                CustomBox.Focus();
                CustomBox.SelectAll();
            });
        }
    }
}
