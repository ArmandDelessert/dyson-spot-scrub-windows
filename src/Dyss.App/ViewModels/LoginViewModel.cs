using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dyss.App.Services;
using Dyss.Core;

namespace Dyss.App.ViewModels;

/// <summary>Two-step login: request the one-time code, then verify it with the password.</summary>
public sealed partial class LoginViewModel : ObservableObject, IDisposable
{
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _country = "CH";
    [ObservableProperty] private string _culture = "fr-CH";
    [ObservableProperty] private string _otpCode = "";
    [ObservableProperty] private bool _codeSent;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _message = "";

    private DysonCloudClient? _api;
    private string? _challengeId;

    /// <summary>Set by the window on success; the window then closes.</summary>
    public RobotContext? Result { get; private set; }

    /// <summary>On success the REST client is handed over to <see cref="Result"/>, which then owns it; only an abandoned login has one left to dispose.</summary>
    public void Dispose()
    {
        if (Result is null) _api?.Dispose();
        _api = null;
    }

    [RelayCommand]
    private async Task SendCodeAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || Country.Length != 2)
        {
            Message = "Indiquez l'e-mail du compte et un code pays à deux lettres.";
            return;
        }
        Busy = true;
        Message = "";
        try
        {
            _api?.Dispose();
            _api = new DysonCloudClient(Country.ToUpperInvariant(), Culture);
            await _api.ProvisionAsync();
            var status = await _api.GetUserStatusAsync(Email.Trim());
            if (!string.Equals(status.AccountStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                Message = $"Compte {status.AccountStatus ?? "inconnu"} pour ce pays.";
                return;
            }
            _challengeId = (await _api.BeginLoginAsync(Email.Trim())).ChallengeId;
            CodeSent = true;
            Message = "Un code à usage unique vous a été envoyé par e-mail.";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>The password comes from the PasswordBox and is never kept in the view model.</summary>
    public async Task VerifyAsync(string password)
    {
        if (_api is null || _challengeId is null) return;
        if (string.IsNullOrWhiteSpace(OtpCode) || string.IsNullOrEmpty(password))
        {
            Message = "Saisissez le mot de passe et le code reçu.";
            return;
        }
        Busy = true;
        Message = "";
        try
        {
            var login = await _api.CompleteLoginAsync(Email.Trim(), password, _challengeId, OtpCode.Trim());
            var stored = new StoredSession(Email.Trim(), _api.Country, _api.Culture, login.Account, login.Token, DateTimeOffset.UtcNow);
            SessionStore.Save(stored);
            Result = RobotContext.FromLogin(_api, stored);
            _api = null; // ownership moved to the context
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}
