using System.Windows;
using Dyss.Presentation.Services;
using Dyss.Presentation.ViewModels;

namespace Dyss.App.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "A Window's lifetime is its own Closed event, which is where the view model is disposed.")]
public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm = new();

    public RobotContext? Result => _vm.Result;

    public LoginWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Closed += (_, _) => _vm.Dispose();
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        await _vm.VerifyAsync(Password.Password);
        if (_vm.Result is not null)
        {
            DialogResult = true;
            Close();
        }
    }
}
