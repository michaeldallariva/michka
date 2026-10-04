using System.Windows;
using DochkaDock.Models;
using DochkaDock.Services;
using Microsoft.Win32;

namespace DochkaDock;

/// <summary>Create/edit dialog for a Drop Action — a user-defined
/// drag-and-drop command (Resize, Convert, Compress…). Same visual language
/// and Browse-button pattern as AppPickerWindow, but a single-purpose form
/// rather than a searchable grid.</summary>
public partial class DropActionEditorWindow : Window
{
    private readonly Action<string, string, string, string?> _onSave;

    public DropActionEditorWindow(DockItem? existing, Action<string, string, string, string?> onSave)
    {
        InitializeComponent();
        _onSave = onSave;

        if (existing is not null)
        {
            NameBox.Text = existing.DisplayName;
            CommandBox.Text = existing.Command ?? string.Empty;
            ArgumentsBox.Text = existing.ArgumentsTemplate ?? string.Empty;
            AcceptBox.Text = existing.AcceptExtensions ?? string.Empty;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationService.Instance["DropAction_BrowseDialogTitle"],
            Filter = LocalizationService.Instance["DropAction_BrowseDialogFilter"],
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
            CommandBox.Text = dialog.FileName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var command = CommandBox.Text.Trim();
        var arguments = ArgumentsBox.Text.Trim();
        var accept = AcceptBox.Text.Trim();

        if (name.Length == 0 || command.Length == 0)
        {
            MessageBox.Show(LocalizationService.Instance["DropAction_ValidationMessage"],
                LocalizationService.Instance["DropAction_Title"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _onSave(name, command, arguments, accept.Length == 0 ? null : accept);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
