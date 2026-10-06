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
}
