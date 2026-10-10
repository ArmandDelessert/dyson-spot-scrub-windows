using DyssCockpit.Core;
using DyssCockpit.Presentation.Services;
using DyssCockpit.Presentation.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace DyssCockpit.Presentation.Tests;

/// <summary>The application's settings page (not the robot's): what it shows, what it writes, what it asks of the shell.</summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dyss-settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        if (Directory.Exists(_dataFolder)) Directory.Delete(_dataFolder, recursive: true);
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

        public int MessagesFolderOpened { get; private set; }

        public void OpenMessagesFolder() => MessagesFolderOpened++;

        public int CleansFolderOpened { get; private set; }

        public void OpenCleansFolder() => CleansFolderOpened++;
    }

    /// <summary>The data folder the page counts old files in: this test's own, never the user's.</summary>
    private readonly string _dataFolder = Directory.CreateTempSubdirectory("dyss-data-").FullName;

    private (SettingsViewModel Vm, AppSettings Settings, FakeShell Shell, FakeDialogs Dialogs) New(Action<AppSettings>? prepare = null, TimeProvider? time = null)
    {
        var settings = AppSettings.Load(_path);
        prepare?.Invoke(settings);
        var shell = new FakeShell();
        var dialogs = new FakeDialogs();
        return (new SettingsViewModel(settings, shell, dialogs, running: AppLanguage.French, dataFolder: _dataFolder, time: time), settings, shell, dialogs);
    }

    [Fact]
    public void ThePageStartsFromTheDefaultSettings()
    {
        var (vm, _, _, _) = New();

        Assert.Equal(1, vm.TaskNotificationsIndex);   // the end of a clean only
        Assert.True(vm.NotifyUnreachable);
        Assert.True(vm.NotifyRobotFault);
        Assert.True(vm.CloseToTray);
        Assert.False(vm.StartWithWindows);
        Assert.Equal(0, vm.LanguageIndex);
        Assert.True(vm.ShowFurniture);
        Assert.True(vm.ShowTravelPath);
        Assert.True(vm.ShowCleanedArea);
        Assert.False(vm.ShowExportButton);
        Assert.False(vm.SmoothRobotMotion);
        Assert.False(vm.RecordMessages);
        Assert.False(vm.ArchiveCleans);
        Assert.Equal(3, vm.CleanArchiveRetentionIndex);   // a year
        Assert.Equal(2, vm.LogRetentionIndex);       // 7 days
        Assert.Equal(4, vm.MessageRetentionIndex);   // 30 days
    }

    [Fact]
    public void TheRetentionIsChosenFromTheListAndWrittenAsADayCount()
    {
        var (vm, settings, _, _) = New();

        vm.LogRetentionIndex = 0;
        vm.MessageRetentionIndex = 6;   // the last: for ever

        Assert.Equal(1, settings.LogRetentionDays);
        Assert.Equal(0, settings.MessageRetentionDays);
        var next = AppSettings.Load(_path);
        Assert.Equal(1, next.LogRetentionDays);
        Assert.Equal(0, next.MessageRetentionDays);

        vm.LogRetentionIndex = -1;   // nothing selected, as a list does while it refreshes
        vm.LogRetentionIndex = 7;
        Assert.Equal(1, settings.LogRetentionDays);
    }

    private static string Touch(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    /// <summary>A clock at noon on 9 October 2026, UTC.</summary>
    private static FakeTimeProvider Clock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        return clock;
    }

    [Fact]
    public void ShorteningTheRetentionAsksFirstWhenFilesWouldGoAndSaysHowMany()
    {
        var (vm, settings, _, dialogs) = New(time: Clock());
        var logs = AppFolders.Logs(_dataFolder);
        Touch(logs, "dyss-cockpit-2026-10-08.log");   // yesterday
        Touch(logs, "dyss-cockpit-2026-10-04.log");   // 5 days
        Touch(logs, "dyss-cockpit-2026-10-01.log");   // 8 days: already past the 7 of today
        var asked = new List<(string Title, string Message)>();
        dialogs.Confirm = (title, message) => { asked.Add((title, message)); return true; };

        vm.LogRetentionIndex = 0;   // 1 day: the 5 and 8 days old files would go

        var (title, message) = Assert.Single(asked);
        Assert.Equal("Journaux de l'application", title);
        Assert.Contains("2 fichier(s) de journal", message, StringComparison.Ordinal);
        Assert.Contains("1 jour(s)", message, StringComparison.Ordinal);
        Assert.Equal(1, settings.LogRetentionDays);
    }

    [Fact]
    public void RefusingPutsTheListAndTheSettingBackAsTheyWere()
    {
        var (vm, settings, _, dialogs) = New(time: Clock());
        Touch(AppFolders.Messages(_dataFolder), "messages-2026-08-01.jsonl");
        dialogs.Confirm = (_, _) => false;

        vm.MessageRetentionIndex = 1;   // 3 days

        Assert.Equal(30, settings.MessageRetentionDays);
        Assert.Equal(4, vm.MessageRetentionIndex);
        Assert.Equal(30, AppSettings.Load(_path).MessageRetentionDays);
    }

    [Fact]
    public void NothingIsAskedWhenNothingWouldBeDeleted()
    {
        var (vm, settings, _, dialogs) = New(time: Clock());
        Touch(AppFolders.Logs(_dataFolder), "dyss-cockpit-2026-10-09.log");
        var asked = 0;
        dialogs.Confirm = (_, _) => { asked++; return false; };   // would refuse, if asked

        vm.LogRetentionIndex = 0;   // 1 day: today's file stays
        vm.MessageRetentionIndex = 6;   // for ever
        vm.LogRetentionIndex = 3;       // 14 days, longer than before

        Assert.Equal(0, asked);
        Assert.Equal(14, settings.LogRetentionDays);
        Assert.Equal(0, settings.MessageRetentionDays);
    }

    [Fact]
    public void RecordingTheMessagesIsASwitchOfThePageAndFollowsAChangeMadeElsewhere()
    {
        var (vm, settings, _, _) = New();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RecordMessages = true;
        Assert.True(settings.RecordMessages);
        Assert.True(AppSettings.Load(_path).RecordMessages);

        settings.RecordMessages = false;   // a disk that filled up stops the recording
        Assert.False(vm.RecordMessages);
        Assert.Contains(nameof(SettingsViewModel.RecordMessages), raised);
    }

    [Fact]
    public void KeepingTheCleansIsASwitchAndItsRetentionAListThatAsksBeforeDeleting()
    {
        var (vm, settings, shell, dialogs) = New(time: Clock());
        vm.ArchiveCleans = true;
        Assert.True(AppSettings.Load(_path).ArchiveCleans);

        vm.CleanArchiveRetentionIndex = 5;   // for ever
        Assert.Equal(0, settings.CleanArchiveRetentionDays);

        Touch(AppFolders.Cleans(_dataFolder), "clean-2026-05-01-abc.json.gz");   // 5 months old
        Touch(AppFolders.Cleans(_dataFolder), "clean-2026-10-01-def.json.gz");
        var asked = new List<string>();
        dialogs.Confirm = (_, message) => { asked.Add(message); return false; };
        vm.CleanArchiveRetentionIndex = 1;   // 3 months: the first would go

        Assert.Single(asked);
        Assert.Contains("1 fichier(s)", asked[0], StringComparison.Ordinal);
        Assert.Equal(0, settings.CleanArchiveRetentionDays);   // refused: as it was
        Assert.Equal(5, vm.CleanArchiveRetentionIndex);

        dialogs.Confirm = (_, _) => true;
        vm.CleanArchiveRetentionIndex = 1;
        Assert.Equal(90, settings.CleanArchiveRetentionDays);

        vm.OpenCleansFolderCommand.Execute(null);
        Assert.Equal(1, shell.CleansFolderOpened);
    }

    [Fact]
    public void TheMessagesFolderIsOpenedByTheShell()
    {
        var (vm, _, shell, _) = New();

        vm.OpenMessagesFolderCommand.Execute(null);

        Assert.Equal(1, shell.MessagesFolderOpened);
        Assert.Equal(0, shell.LogsFolderOpened);
    }

    [Fact]
    public void ARetentionAlreadyStoredIsShownAtItsPlaceInTheList()
    {
        var (vm, _, _, _) = New(s => { s.LogRetentionDays = 90; s.MessageRetentionDays = 0; });

        Assert.Equal(5, vm.LogRetentionIndex);
        Assert.Equal(6, vm.MessageRetentionIndex);
    }

    [Fact]
    public void WhatTheUserChoosesIsWrittenAtOnceAndReadBackNextTime()
    {
        var (vm, _, _, _) = New();

        vm.NotifyUnreachable = false;
        vm.NotifyRobotFault = false;
        vm.CloseToTray = false;
        vm.ShowCleanedArea = true;
        vm.ShowFurniture = false;
        vm.SmoothRobotMotion = true;

        var next = AppSettings.Load(_path);
        Assert.False(next.NotifyUnreachable);
        Assert.False(next.NotifyRobotFault);
        Assert.False(next.CloseToTray);
        Assert.True(next.ShowCleanedArea);
        Assert.False(next.ShowFurniture);
        Assert.True(next.SmoothRobotMotion);
        Assert.Equal(TaskNotificationMode.EndOnly, next.TaskNotifications);   // untouched
    }

    [Fact]
    public void TheNotificationsOfACleanAreChosenFromTheListAndRememberedByName()
    {
        var (vm, settings, _, _) = New();

        vm.TaskNotificationsIndex = 2;
        Assert.Equal(TaskNotificationMode.StartAndEnd, settings.TaskNotifications);
        Assert.Contains("\"StartAndEnd\"", File.ReadAllText(_path), StringComparison.Ordinal);
        Assert.Equal(TaskNotificationMode.StartAndEnd, AppSettings.Load(_path).TaskNotifications);

        vm.TaskNotificationsIndex = 0;
        Assert.Equal(TaskNotificationMode.None, AppSettings.Load(_path).TaskNotifications);

        vm.TaskNotificationsIndex = -1;   // nothing selected, as a list does while it refreshes
        Assert.Equal(TaskNotificationMode.None, settings.TaskNotifications);
    }

    [Fact]
    public void AnUnknownModeInTheFileFallsBackToTheEndOfAClean()
    {
        File.WriteAllText(_path, """{"TaskNotifications":"Always"}""");

        Assert.Equal(TaskNotificationMode.EndOnly, AppSettings.Load(_path).TaskNotifications);
    }

    [Fact]
    public void ThePageReflectsTheSettingsItIsGiven()
    {
        var (vm, _, _, _) = New(s => { s.Language = "en-US"; s.TaskNotifications = TaskNotificationMode.StartAndEnd; s.ShowTravelPath = false; });

        Assert.Equal(2, vm.LanguageIndex);
        Assert.Equal(2, vm.TaskNotificationsIndex);
        Assert.False(vm.ShowTravelPath);
        Assert.True(vm.IsRestartRequired);   // English was chosen, French is running
    }

    [Fact]
    public void ALanguageChosenEarlierAndNotYetAppliedStillAsksForTheRestart()
    {
        var (french, _, _, _) = New(s => s.Language = "fr-FR");
        Assert.False(french.IsRestartRequired);

        var (english, _, _, _) = New(s => s.Language = "en-US");
        Assert.True(english.IsRestartRequired);
    }

    [Fact]
    public void ChangingAValueTellsThePageOnce()
    {
        var (vm, _, _, _) = New();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ShowExportButton = true;
        vm.ShowExportButton = true;   // the same value again

        Assert.Equal([nameof(SettingsViewModel.ShowExportButton)], raised);
    }

    [Fact]
    public void AnotherLanguageThanTheRunningOneAsksForARestartAndGoingBackCancelsIt()
    {
        // The tests run in French (see FrenchByDefault), whatever the machine's language.
        var (vm, settings, _, _) = New();

        vm.LanguageIndex = 2;
        Assert.Equal("en-US", settings.Language);
        Assert.True(vm.IsRestartRequired);

        vm.LanguageIndex = 1;
        Assert.Equal("fr-FR", settings.Language);
        Assert.False(vm.IsRestartRequired);


        // Windows' own language is whatever the machine has, so for automatic only what is stored is checked.
        vm.LanguageIndex = 0;
        Assert.Equal("", settings.Language);
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
        public void OpenMessagesFolder() { }
        public void OpenCleansFolder() { }
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

    [Theory]
    [InlineData("0.0.0-dev+ada1ea8dc42b3bfa43d6e11107dbccf81928f8bf", "ada1ea8")]
    [InlineData("1.2.3+0123abc", "0123abc")]
    [InlineData("1.2.3+abc", "abc")]
    [InlineData("1.2.3", null)]
    [InlineData("1.2.3+", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheCommitIsTheFirstSevenCharactersAfterThePlus(string? informational, string? expected) =>
        Assert.Equal(expected, SettingsViewModel.CommitOf(informational));

    [Theory]
    [InlineData("0.0.0-dev+ada1ea8dc42b3bfa43d6e11107dbccf81928f8bf", "DySS Cockpit 0.0.0-dev (ada1ea8)")]
    [InlineData("0.1.0-beta.1+0123abcdef", "DySS Cockpit 0.1.0-beta.1 (0123abc)")]
    [InlineData("1.0.0", "DySS Cockpit 1.0.0")]
    [InlineData(null, "DySS Cockpit ?")]
    public void TheVersionLineGivesTheVersionThenItsCommitWhenThereIsOne(string? informational, string expected) =>
        Assert.Equal(expected, SettingsViewModel.VersionTextOf(informational));

    [Fact]
    public void TheAboutLineNamesTheApplicationAndItsVersion()
    {
        var (vm, _, _, _) = New();

        Assert.StartsWith("DySS Cockpit ", vm.VersionText, StringComparison.Ordinal);
        Assert.DoesNotContain('+', vm.VersionText);
    }

    [Fact]
    public void TheLogoutButtonRunsTheDashboardsOwnCommandAndTheAccountIsNamedAboveIt()
    {
        var logouts = 0;
        var vm = new SettingsViewModel(AppSettings.Load(_path), new FakeShell(), new FakeDialogs(), AppLanguage.French,
            logout: new CommunityToolkit.Mvvm.Input.RelayCommand(() => logouts++), accountEmail: "someone@example.invalid");

        vm.LogoutCommand.Execute(null);

        Assert.Equal(1, logouts);
        Assert.Equal("Connecté avec someone@example.invalid", vm.AccountText);
    }

    [Fact]
    public void WithoutAnAccountTheButtonDoesNothingAndNothingIsSaid()
    {
        var (vm, _, _, _) = New();

        vm.LogoutCommand.Execute(null);

        Assert.Equal("", vm.AccountText);
    }
}
