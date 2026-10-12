using System.Windows;
using System.Windows.Input;
using NocDisplayHub.Core.Config;

namespace NocDisplayHub.UI;

/// <summary>Lists a profile's saved-over versions, newest first, and lets the operator pick one to restore.</summary>
public partial class RestoreBackupDialog : Window
{
    public ProfileBackup? Selected => (BackupList.SelectedItem as BackupItem)?.Backup;

    public RestoreBackupDialog(string profileName, IReadOnlyList<ProfileBackup> backups)
    {
        InitializeComponent();
        PromptText.Text = $"Pick a previous version of \"{profileName}\". The current version is kept too, so this can be undone.";
        BackupList.ItemsSource = backups.Select(b => new BackupItem(b)).ToList();
        BackupList.SelectedIndex = backups.Count > 0 ? 0 : -1;
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not null) DialogResult = true;
    }

    private void BackupList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not null) DialogResult = true;
    }

    private sealed record BackupItem(ProfileBackup Backup)
    {
        public override string ToString() =>
            $"{Backup.SavedOverAt:ddd MMM d, h:mm:ss tt}   —   {Backup.BindingCount} cell(s) assigned";
    }
}
