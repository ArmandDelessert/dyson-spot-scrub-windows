using System.Windows;

namespace Dyss.App.Views;

/// <summary>A one-line text prompt, WPF having no equivalent of InputBox.</summary>
public partial class TextPromptWindow : Window
{
    private TextPromptWindow(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        Input.Text = initial;
        // An empty name is not something the robot would take, and an accidental Enter on a blank
        // field should not look like a working action.
        Input.TextChanged += (_, _) => OkButton.IsEnabled = !string.IsNullOrWhiteSpace(Input.Text);
        OkButton.IsEnabled = !string.IsNullOrWhiteSpace(initial);
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    /// <summary>Returns the text, or null when the user cancelled.</summary>
    public static string? Ask(Window owner, string title, string prompt, string initial)
    {
        var w = new TextPromptWindow(title, prompt, initial) { Owner = owner };
        return w.ShowDialog() == true ? w.Input.Text : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Input.Text)) { Input.Focus(); return; }
        DialogResult = true;
    }
}
