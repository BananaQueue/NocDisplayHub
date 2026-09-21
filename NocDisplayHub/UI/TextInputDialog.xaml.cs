using System.Windows;
using System.Windows.Input;

namespace NocDisplayHub.UI;

/// <summary>Minimal modal prompt for a single line of text — used for naming a profile when WPF has no built-in input box.</summary>
public partial class TextInputDialog : Window
{
    public string InputText => InputTextBox.Text.Trim();

    public TextInputDialog(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputTextBox.Text = initialValue;
        InputTextBox.Focus();
        InputTextBox.SelectAll();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DialogResult = true;
        }
    }
}
