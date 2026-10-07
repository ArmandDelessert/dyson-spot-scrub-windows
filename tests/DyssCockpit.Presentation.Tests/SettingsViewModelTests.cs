using DyssCockpit.Core;
using DyssCockpit.Presentation.Services;
using DyssCockpit.Presentation.ViewModels;

namespace DyssCockpit.Presentation.Tests;

/// <summary>The application's settings page (not the robot's): what it shows, what it writes, what it asks of the shell.</summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dyss-settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private sealed class FakeShell : IAppShell
    {
        public bool StartsWithWindows { get; set; }
        public List<(string Title, string Body)> Notifications { get; } = [];
        public string? RestartFailure { get; set; }
        public int Restarts { get; private set; }
        public int LogsFolderOpened { get; private set; }

        public void ShowNotification(string title, string body) => Notifications.Add((title, body));

        public string? Restart()
        {
            Restarts++;
            return RestartFailure;
        }

        public void OpenLogsFolder() => LogsFolderOpened++;
    }

    private (SettingsViewModel Vm, AppSettings Settings, FakeShell Shell, FakeDialogs Dialogs) New(Action<AppSettings>? prepare = null)
    {
        var settings = AppSettings.Load(_path);
        prepare?.Invoke(settings);
        var shell = new FakeShell();
        var dialogs = new FakeDialogs();
        return (new SettingsViewModel(settings, shell, dialogs, running: AppLanguage.French), settings, shell, dialogs);
    }

    [Fact]
    public void ThePageStartsFromTheDefaultSettings()
    {
        var (vm, _, _, _) = New();

        Assert.True(vm.NotifyCleanFinished);
        Assert.True(vm.NotifyUnreachable);
        Assert.True(vm.CloseToTray);
        Assert.False(vm.StartWithWindows);
        Assert.Equal(0, vm.LanguageIndex);
        Assert.True(vm.ShowFurniture);
        Assert.True(vm.ShowTravelPath);
        Assert.False(vm.ShowCleanedArea);
        Assert.True(vm.ShowExportButton);
        Assert.False(vm.SmoothRobotMotion);
    }

    [Fact]
    public void WhatTheUserChoosesIsWrittenAtOnceAndReadBackNextTime()
    {
        var (vm, _, _, _) = New();

        vm.NotifyUnreachable = false;
        vm.CloseToTray = false;
        vm.ShowCleanedArea = true;
        vm.ShowFurniture = false;
        vm.SmoothRobotMotion = true;

        var next = AppSettings.Load(_path);
        Assert.False(next.NotifyUnreachable);
        Assert.False(next.CloseToTray);
        Assert.True(next.ShowCleanedArea);
        Assert.False(next.ShowFurniture);
        Assert.True(next.SmoothRobotMotion);
        Assert.True(next.NotifyCleanFinished);   // untouched
    }

    [Fact]
    public void ThePageReflectsTheSettingsItIsGiven()
    {
        var (vm, _, _, _) = New(s => { s.Language = "en"; s.NotifyCleanFinished = false; s.ShowTravelPath = false; });

        Assert.Equal(2, vm.LanguageIndex);
        Assert.False(vm.NotifyCleanFinished);
        Assert.False(vm.ShowTravelPath);
        Assert.True(vm.IsRestartRequired);   // English was chosen, French is running
    }

    [Fact]
    public void ALanguageChosenEarlierAndNotYetAppliedStillAsksForTheRestart()
    {
        var (french, _, _, _) = New(s => s.Language = "fr");
        Assert.False(french.IsRestartRequired);

        var (english, _, _, _) = New(s => s.Language = "en");
        Assert.True(english.IsRestartRequired);
    }

    [Fact]
    public void ChangingAValueTellsThePageOnce()
    {
        var (vm, _, _, _) = New();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ShowExportButton = false;
        vm.ShowExportButton = false;   // the same value again

        Assert.Equal([nameof(SettingsViewModel.ShowExportButton)], raised);
    }

    [Fact]
    public void AnotherLanguageThanTheRunningOneAsksForARestartAndGoingBackCancelsIt()
    {
        // The tests run in French (see FrenchByDefault), whatever the machine's language.
        var (vm, settings, _, _) = New();

        vm.LanguageIndex = 2;
        Assert.Equal("en", settings.Language);
        Assert.True(vm.IsRestartRequired);

        vm.LanguageIndex = 1;
        Assert.Equal("fr", settings.Language);
        Assert.False(vm.IsRestartRequired);


        // Windows' own language is whatever the machine has, so for automatic only what is stored is checked.
        vm.LanguageIndex = 0;
        Assert.Equal("auto", settings.Language);
    }

    [Fact]
    public async Task TheRestartButtonRestartsAndOnlyAnswersWhenWindowsCouldNot()
    {
        var (vm, _, shell, dialogs) = New();

        await vm.RestartCommand.ExecuteAsync(null);
        Assert.Equal(1, shell.Restarts);
        Assert.Empty(dialogs.Alerts);

        shell.RestartFailure = "Hung";
        await vm.RestartCommand.ExecuteAsync(null);
        Assert.Equal(2, shell.Restarts);
        Assert.Contains("Hung", Assert.Single(dialogs.Alerts).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StartingWithWindowsGoesThroughTheShellAndAnAlreadySetValueIsNotWrittenAgain()
    {
        var shell = new FakeShell { StartsWithWindows = true };
        var vm = new SettingsViewModel(AppSettings.Load(_path), shell, new FakeDialogs(), AppLanguage.French);
        Assert.True(vm.StartWithWindows);   // what the registry says, not what was stored

        vm.StartWithWindows = false;

        Assert.False(shell.StartsWithWindows);
    }

    [Fact]
    public void WhenWindowsRefusesToStartWithWindowsTheSwitchGoesBackAndTheUserIsTold()
    {
        var dialogs = new FakeDialogs();
        var vm = new SettingsViewModel(AppSettings.Load(_path), new RefusingShell(), dialogs, AppLanguage.French);

        vm.StartWithWindows = true;

        Assert.False(vm.StartWithWindows);
        Assert.Contains("Access denied", Assert.Single(dialogs.Alerts).Message, StringComparison.Ordinal);
    }

    /// <summary>A registry that will not take the entry.</summary>
    private sealed class RefusingShell : IAppShell
    {
        public bool StartsWithWindows
        {
            get => false;
            set => throw new UnauthorizedAccessException("Access denied");
        }

        public void ShowNotification(string title, string body) { }
        public string? Restart() => null;
        public void OpenLogsFolder() { }
    }

    [Fact]
    public void TheTestNotificationGoesThroughTheShell()
    {
        var (vm, _, shell, _) = New();

        vm.SendTestNotificationCommand.Execute(null);

        var (title, body) = Assert.Single(shell.Notifications);
        Assert.Equal("Notification de test", title);
        Assert.Contains("DySS Cockpit", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLogsFolderIsOpenedByTheShell()
    {
        var (vm, _, shell, _) = New();

        vm.OpenLogsFolderCommand.Execute(null);

        Assert.Equal(1, shell.LogsFolderOpened);
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+0123abcd", "1.2.3")]
    [InlineData("1.0.0-beta.1+0123abcd", "1.0.0-beta.1")]
    [InlineData("0.0.0-dev+fa71de3", "0.0.0-dev")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void TheVersionIsWhatTheBuildCarriesWithoutTheCommit(string? informational, string expected) =>
        Assert.Equal(expected, SettingsViewModel.VersionOf(informational));

    [Fact]
    public void TheAboutLineNamesTheApplicationAndItsVersion()
    {
        var (vm, _, _, _) = New();

        Assert.StartsWith("DySS Cockpit ", vm.VersionText, StringComparison.Ordinal);
        Assert.DoesNotContain('+', vm.VersionText);
    }
}
