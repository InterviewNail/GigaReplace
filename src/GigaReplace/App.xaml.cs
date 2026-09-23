using System.Windows;
using System.Windows.Threading;
using GigaReplace.Services;
using GigaReplace.ViewModels;
using GigaReplace.Views;

namespace GigaReplace;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var window = new MainWindow { DataContext = new MainViewModel(new DialogService()) };
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ChoiceWindow.Show(
            MainWindow,
            "Something went wrong",
            "GigaReplace hit an unexpected error. Nothing is written without your confirmation, so your files are as they were unless a run was in progress.",
            ["OK"],
            [e.Exception.GetType().Name + ": " + e.Exception.Message],
            isError: true);
        e.Handled = true;
    }
}
