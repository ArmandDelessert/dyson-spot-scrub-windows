using System.Windows;

namespace MyDyson.App.Views;

/// <summary>A one-line text prompt, WPF having no equivalent of InputBox.</summary>
public partial class TextPromptWindow : Window
{
    private TextPromptWindow(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        Input.Text = initial;
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    /// <summary>Returns the text, or null when the user cancelled.</summary>
    public static string? Ask(Window owner, string title, string prompt, string initial)
    {
        var w = new TextPromptWindow(title, prompt, initial) { Owner = owner };
        return w.ShowDialog() == true ? w.Input.Text : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
