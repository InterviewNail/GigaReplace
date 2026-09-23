using System.IO;
using System.Windows;
using GigaReplace.ViewModels;
using GigaReplace.Views;
using Microsoft.Win32;

namespace GigaReplace.Services;

public interface IDialogService
{
    string? PickFolder(string title, string? initial);

    string? PickFile(string title, string filter, string? initial);

    string? PickSaveFile(string title, string filter, string defaultName, string? initialDirectory);

    void Info(string title, string message, IEnumerable<string>? details = null);

    void Error(string title, string message, IEnumerable<string>? details = null);

    /// <summary>Returns the index of the chosen button, or -1 if the dialog was closed.</summary>
    int Choose(string title, string message, IReadOnlyList<string> buttons, IEnumerable<string>? details = null);

    DecisionOutcome ShowDecision(DecisionViewModel model);
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;

    public string? PickFolder(string title, string? initial)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public string? PickFile(string title, string filter, string? initial)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        var folder = ExistingFolderOf(initial);
        if (folder is not null)
        {
            dialog.InitialDirectory = folder;
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string defaultName, string? initialDirectory)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultName, OverwritePrompt = true };
        var folder = ExistingFolderOf(initialDirectory);
        if (folder is not null)
        {
            dialog.InitialDirectory = folder;
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public void Info(string title, string message, IEnumerable<string>? details = null) =>
        Choose(title, message, ["OK"], details);

    public void Error(string title, string message, IEnumerable<string>? details = null) =>
        ChoiceWindow.Show(Owner, title, message, ["OK"], details, isError: true);

    public int Choose(string title, string message, IReadOnlyList<string> buttons, IEnumerable<string>? details = null) =>
        ChoiceWindow.Show(Owner, title, message, buttons, details, isError: false);

    public DecisionOutcome ShowDecision(DecisionViewModel model)
    {
        var window = new DecisionWindow(model) { Owner = Owner };
        window.ShowDialog();
        return model.Outcome ?? new DecisionOutcome(DecisionAction.Stop, null, false);
    }

    private static string? ExistingFolderOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (Directory.Exists(path))
        {
            return path;
        }

        var parent = Path.GetDirectoryName(path);
        return parent is not null && Directory.Exists(parent) ? parent : null;
    }
}
