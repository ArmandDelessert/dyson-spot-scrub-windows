using System.Net;
using DyssCockpit.Core;
using DyssCockpit.Presentation.ViewModels;

namespace DyssCockpit.Presentation.Tests;

public class LoginViewModelTests
{
    [Fact]
    public void TheMachinesCountryAndLanguageComeFirst()
    {
        using var vm = new LoginViewModel("CH", "de");

        Assert.Equal("CH", vm.SelectedCountry.Code);
        Assert.Equal("de-CH", vm.SelectedCulture!.Code);
        Assert.Contains(vm.Cultures, c => c.Code == "fr-CH");
    }

    [Fact]
    public void AnotherCountryListsItsOwnLanguages()
    {
        using var vm = new LoginViewModel("CH", "fr");

        vm.SelectedCountry = vm.Countries.Single(c => c.Code == "FR");

        Assert.Equal("fr-FR", vm.SelectedCulture!.Code);
        Assert.All(vm.Cultures, c => Assert.EndsWith("-FR", c.Code, StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownRegionFallsBackOnSwitzerland()
    {
        using var vm = new LoginViewModel("001", "fr");

        Assert.Equal("CH", vm.SelectedCountry.Code);
        Assert.DoesNotContain(vm.Countries, c => c.Code.Any(char.IsDigit));
    }

    [Fact]
    public void FailuresAreToldInPlainWords()
    {
        var refused = new DysonApiException("POST /v3/userregistration/email/verify failed: HTTP 400 Bad Request", HttpStatusCode.BadRequest);

        Assert.StartsWith("Mot de passe ou code incorrect", LoginViewModel.Describe(refused, verifying: true), StringComparison.Ordinal);
        Assert.StartsWith("Le cloud Dyson a refusé", LoginViewModel.Describe(refused, verifying: false), StringComparison.Ordinal);
        Assert.StartsWith("Impossible de joindre", LoginViewModel.Describe(new HttpRequestException("DNS"), verifying: false), StringComparison.Ordinal);
        Assert.Contains("HTTP 503", LoginViewModel.Describe(new DysonApiException("x", HttpStatusCode.ServiceUnavailable), verifying: true), StringComparison.Ordinal);
    }

    [Fact]
    public void RestartingFreesTheAccountFields()
    {
        using var vm = new LoginViewModel("CH", "fr") { CodeSent = true, OtpCode = "123456" };
        Assert.False(vm.CanEditAccount);

        vm.RestartCommand.Execute(null);

        Assert.True(vm.CanEditAccount);
        Assert.Equal("", vm.OtpCode);
    }

    // ---- The two steps, against canned answers --------------------------------------------------

    private const string Active = """{"accountStatus":"ACTIVE","authenticationMethod":"EMAIL_PWD_2FA"}""";

    /// <summary>A login whose cloud answers from <paramref name="handler"/> and whose session goes to <paramref name="saved"/>, never to the user's file.</summary>
    private static LoginViewModel Login(out TestHub.RouteHandler handler, List<StoredSession> saved, string status = Active)
    {
        var routes = handler = new TestHub.RouteHandler(
            ("provisioningservice", "\"5.10.38\""),
            ("email/userstatus", status),
            ("email/auth", """{"challengeId":"challenge-1"}"""),
            ("email/verify", """{"account":"acc","token":"tok","tokenType":"Bearer"}"""));
        return new LoginViewModel("CH", "fr")
        {
            NewApi = (country, culture) => new DysonCloudClient(country, culture, http: new HttpClient(routes)),
            SaveSession = saved.Add,
            Email = " me@example.org ",
        };
    }

    [Fact]
    public async Task AnActiveAccountIsSentACodeAndItsFieldsAreFixed()
    {
        using var vm = Login(out var handler, []);

        await vm.SendCodeCommand.ExecuteAsync(null);

        Assert.Equal(["/v1/provisioningservice/application/Android/version", "/v3/userregistration/email/userstatus", "/v3/userregistration/email/auth"], handler.Requested);
        Assert.All(handler.Bodies, b => Assert.Equal("""{"email":"me@example.org"}""", b.Body));
        Assert.True(vm.CodeSent);
        Assert.False(vm.CanEditAccount);
        Assert.StartsWith("Un code à usage unique", vm.Message, StringComparison.Ordinal);
        Assert.False(vm.MessageIsError);
        Assert.False(vm.Busy);
    }

    [Fact]
    public async Task AnAddressWithoutAnActiveAccountIsToldSoAndNoCodeIsAskedFor()
    {
        using var vm = Login(out var handler, [], status: """{"accountStatus":"UNREGISTERED"}""");

        await vm.SendCodeCommand.ExecuteAsync(null);

        Assert.False(vm.CodeSent);
        Assert.True(vm.MessageIsError);
        Assert.StartsWith("Aucun compte MyDyson actif", vm.Message, StringComparison.Ordinal);
        Assert.Contains(vm.SelectedCountry.Name, vm.Message, StringComparison.Ordinal);   // the country is the usual mistake
        Assert.DoesNotContain(handler.Requested, p => p.EndsWith("/email/auth", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAnAddressNothingIsAsked()
    {
        using var vm = Login(out var handler, []);
        vm.Email = "  ";

        await vm.SendCodeCommand.ExecuteAsync(null);

        Assert.Empty(handler.Requested);
        Assert.True(vm.MessageIsError);
    }

    [Fact]
    public async Task ARefusalOfTheAddressIsToldInPlainWords()
    {
        using var vm = Login(out var handler, []);
        handler.Refusals["email/userstatus"] = HttpStatusCode.BadRequest;

        await vm.SendCodeCommand.ExecuteAsync(null);

        Assert.StartsWith("Le cloud Dyson a refusé", vm.Message, StringComparison.Ordinal);
        Assert.False(vm.CodeSent);
        Assert.False(vm.Busy);
    }

    [Fact]
    public async Task TheRightCodeOpensTheAccountAndKeepsItsSession()
    {
        var saved = new List<StoredSession>();
        using var vm = Login(out var handler, saved);
        await vm.SendCodeCommand.ExecuteAsync(null);
        vm.OtpCode = " 123456 ";

        await vm.VerifyAsync("pw");

        Assert.Equal(("/v3/userregistration/email/verify", """{"email":"me@example.org","password":"pw","challengeId":"challenge-1","otpCode":"123456"}"""), handler.Bodies[^1]);
        var session = Assert.Single(saved);
        Assert.Equal(("me@example.org", "CH", "fr-CH", "acc", "tok"), (session.Email, session.Country, session.Culture, session.Account, session.Token));
        Assert.NotNull(vm.Result);
        Assert.Equal("", vm.Message);
    }

    [Fact]
    public async Task AWrongCodeIsSaidAndNothingIsKept()
    {
        var saved = new List<StoredSession>();
        using var vm = Login(out var handler, saved);
        await vm.SendCodeCommand.ExecuteAsync(null);
        handler.Refusals["email/verify"] = HttpStatusCode.BadRequest;
        vm.OtpCode = "000000";

        await vm.VerifyAsync("pw");

        Assert.StartsWith("Mot de passe ou code incorrect", vm.Message, StringComparison.Ordinal);
        Assert.True(vm.MessageIsError);
        Assert.Empty(saved);
        Assert.Null(vm.Result);
        Assert.True(vm.CodeSent);   // the same code can be typed again, until it expires
    }

    [Fact]
    public async Task VerifyingNeedsThePasswordAndTheCode()
    {
        using var vm = Login(out var handler, []);
        await vm.SendCodeCommand.ExecuteAsync(null);
        var asked = handler.Requested.Count;
        vm.OtpCode = "123456";

        await vm.VerifyAsync("");

        Assert.StartsWith("Saisissez le mot de passe", vm.Message, StringComparison.Ordinal);
        Assert.Equal(asked, handler.Requested.Count);
    }
}
