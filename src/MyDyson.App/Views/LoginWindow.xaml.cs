using System.Windows;
using MyDyson.App.Services;
using MyDyson.App.ViewModels;

namespace MyDyson.App.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm = new();

    public RobotContext? Result => _vm.Result;

    public LoginWindow()
    {
        InitializeComponent();
        DataContext = _vm;
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
