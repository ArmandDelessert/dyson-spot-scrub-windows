using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dyss.Presentation.Services;
using Dyss.Core;

namespace Dyss.Presentation.ViewModels;

/// <summary>A country a MyDyson account can belong to: its ISO code, named in the user's language.</summary>
public sealed record CountryOption(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A language of a country, as the account's culture ("fr-CH"): named in itself, "français (Suisse)".</summary>
public sealed record CultureOption(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Two-step login: request the one-time code, then verify it with the password. The account's
/// country and language cannot be read from Dyson before logging in — every call needs them — so
/// they start from Windows' regional settings and can be picked from lists.
/// </summary>
public sealed partial class LoginViewModel : ObservableObject, IDisposable
{
    public LoginViewModel() : this(RegionInfo.CurrentRegion.TwoLetterISORegionName, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
    {
    }

    /// <param name="region">The machine's country, chosen first when Dyson might have accounts there.</param>
    /// <param name="language">The machine's language, chosen first among the country's.</param>
    internal LoginViewModel(string region, string language)
    {
        _language = language;
        _selectedCountry = Countries.FirstOrDefault(c => c.Code == region) ?? Countries.FirstOrDefault(c => c.Code == "CH") ?? Countries[0];
        ListCultures(_selectedCountry);
    }

    private readonly string _language;

    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _otpCode = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditAccount))] private bool _codeSent;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _message = "";
    /// <summary>Whether <see cref="Message"/> says something went wrong, or only what happens next.</summary>
    [ObservableProperty] private bool _messageIsError;

    /// <summary>The account's e-mail, country and language are fixed once a code has been asked for; <see cref="RestartCommand"/> frees them.</summary>
    public bool CanEditAccount => !CodeSent;

    /// <summary>Every country, by name in the user's language.</summary>
    public IReadOnlyList<CountryOption> Countries { get; } = AllCountries.Value;
    [ObservableProperty] private CountryOption _selectedCountry;
    /// <summary>The languages of <see cref="SelectedCountry"/>, the machine's first.</summary>
    public ObservableCollection<CultureOption> Cultures { get; } = [];
    [ObservableProperty] private CultureOption? _selectedCulture;

    partial void OnSelectedCountryChanged(CountryOption value) => ListCultures(value);

    private void ListCultures(CountryOption country)
    {
        Cultures.Clear();
        foreach (var c in CulturesByCountry.Value.GetValueOrDefault(country.Code, [])
                     .OrderBy(c => !c.Code.StartsWith(_language + "-", StringComparison.OrdinalIgnoreCase))
                     .ThenBy(c => c.Name, StringComparer.CurrentCulture))
            Cultures.Add(c);
        SelectedCulture = Cultures.FirstOrDefault();
    }

    private static readonly Lazy<CultureInfo[]> SpecificCultures = new(() =>
        [.. CultureInfo.GetCultures(CultureTypes.SpecificCultures).Where(c => RegionOf(c) is not null)]);

    private static RegionInfo? RegionOf(CultureInfo culture)
    {
        try
        {
            var region = new RegionInfo(culture.Name);
            // Groupings such as "001" (the world) or "419" (Latin America) are no account's country.
            return region.TwoLetterISORegionName is [var a, var b] && char.IsLetter(a) && char.IsLetter(b) ? region : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static readonly Lazy<IReadOnlyList<CountryOption>> AllCountries = new(() =>
        [.. SpecificCultures.Value.Select(RegionOf).OfType<RegionInfo>()
            .DistinctBy(r => r.TwoLetterISORegionName)
            .Select(r => new CountryOption(r.TwoLetterISORegionName, NameOf(r)))
            .OrderBy(c => c.Name, StringComparer.CurrentCulture)]);

    /// <summary>
    /// A country's name in Windows' display language. .NET names a region in the language of the
    /// culture it came from — Switzerland is "Schweiz" from de-CH — so the name is asked of Windows,
    /// with the English one if it has none.
    /// </summary>
    private static string NameOf(RegionInfo region)
    {
        try
        {
            var buffer = new char[128];
            var length = GetGeoInfoEx(region.TwoLetterISORegionName, GeoFriendlyName, buffer, buffer.Length);
            if (length > 1) return new string(buffer, 0, length - 1);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not Windows, or too old a one: the English name will do.
        }
        return region.EnglishName;
    }

    private const int GeoFriendlyName = 8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetGeoInfoEx(string location, int geoType, char[] geoData, int geoDataCount);

    private static readonly Lazy<Dictionary<string, List<CultureOption>>> CulturesByCountry = new(() =>
        SpecificCultures.Value
            .GroupBy(c => RegionOf(c)!.TwoLetterISORegionName)
            .ToDictionary(g => g.Key, g => g.Select(c => new CultureOption(c.Name, Capitalised(c.NativeName))).ToList()));

    private static string Capitalised(string name) =>
        name.Length == 0 ? name : char.ToUpper(name[0], CultureInfo.CurrentCulture) + name[1..];

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

    private void Say(string message, bool isError)
    {
        Message = message;
        MessageIsError = isError;
    }

    [RelayCommand]
    private async Task SendCodeAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || SelectedCulture is null)
        {
            Say("Indiquez l'e-mail du compte, son pays et sa langue.", isError: true);
            return;
        }
        Busy = true;
        Say("", isError: false);
        try
        {
            _api?.Dispose();
            _api = new DysonCloudClient(SelectedCountry.Code, SelectedCulture.Code);
            await _api.ProvisionAsync();
            var status = await _api.GetUserStatusAsync(Email.Trim());
            if (!string.Equals(status.AccountStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                Say($"Aucun compte MyDyson actif avec cette adresse pour ce pays ({SelectedCountry.Name}). Vérifiez l'adresse et le pays choisi lors de la création du compte.", isError: true);
                return;
            }
            _challengeId = (await _api.BeginLoginAsync(Email.Trim())).ChallengeId;
            CodeSent = true;
            Say("Un code à usage unique vous a été envoyé par e-mail. Saisissez-le avec votre mot de passe.", isError: false);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("connexion", ex);
            Say(Describe(ex, verifying: false), isError: true);
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
            Say("Saisissez le mot de passe et le code reçu.", isError: true);
            return;
        }
        Busy = true;
        Say("", isError: false);
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
            ErrorLog.Write("connexion", ex);
            Say(Describe(ex, verifying: true), isError: true);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Back to the first step, to change the address or the country, or to get a new code once the last one has expired.</summary>
    [RelayCommand]
    private void Restart()
    {
        CodeSent = false;
        OtpCode = "";
        _challengeId = null;
        Say("", isError: false);
    }

    /// <summary>
    /// What a failure means for the user, rather than the HTTP status behind it, which goes to the
    /// error log. Dyson answers a wrong password or code, or an expired code, with a plain 400.
    /// </summary>
    internal static string Describe(Exception ex, bool verifying) => ex switch
    {
        HttpRequestException or TaskCanceledException =>
            "Impossible de joindre le cloud Dyson. Vérifiez la connexion à Internet, puis réessayez.",
        DysonApiException { StatusCode: HttpStatusCode.TooManyRequests } =>
            "Trop de tentatives : patientez quelques minutes avant de réessayer.",
        DysonApiException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } when verifying =>
            "Mot de passe ou code incorrect, ou code expiré. Vérifiez-les, ou recommencez pour recevoir un nouveau code.",
        DysonApiException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.NotFound } =>
            "Le cloud Dyson a refusé la demande. Vérifiez l'adresse e-mail, ainsi que le pays et la langue du compte.",
        DysonApiException { StatusCode: { } status } =>
            $"Le cloud Dyson a répondu par une erreur (HTTP {(int)status}). Réessayez dans un moment.",
        _ => $"La connexion a échoué : {ex.Message}",
    };
}
