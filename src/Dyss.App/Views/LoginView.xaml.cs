using Dyss.Presentation.Services;
using Dyss.Presentation.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dyss.App.Views;

/// <summary>Logging in to MyDyson: the code sent by e-mail, then the password and that code.</summary>
public sealed partial class LoginView : UserControl
{
    private readonly TaskCompletionSource<RobotContext> _result = new();

    /// <param name="loggers">The application's loggers, handed to the login and the account it opens.</param>
    public LoginView(ILoggerFactory loggers)
    {
        // Before the bindings that read it are set up.
        ViewModel = new LoginViewModel(loggers);
        InitializeComponent();
        // Whatever happens to the view, an abandoned login gives back its REST client.
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public LoginViewModel ViewModel { get; }

    /// <summary>The account once logged in. Never completes if the user closes the window instead.</summary>
    public Task<RobotContext> Result => _result.Task;

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        // The password comes from the box and is never kept in the view model.
        await ViewModel.VerifyAsync(Password.Password);
        if (ViewModel.Result is { } ctx) _result.TrySetResult(ctx);
    }
}
