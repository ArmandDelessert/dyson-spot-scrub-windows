using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DyssCockpit.Presentation.Services;
using DyssCockpit.Core;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.Presentation.ViewModels;

/// <summary>
/// The Journal tab: the curated log everything writes to through <see cref="RobotHub.AddLog"/>.
/// It also does the recording of every message on the robot's topics to a daily file (see
/// <see cref="MessageLog"/>), for finding what the tracker doesn't know yet, as long as the
/// "Enregistrer tous les messages" setting is on: the switch is on the settings page, where the
/// time the records are kept is too, and this follows it.
/// </summary>
public sealed partial class JournalViewModel : ObservableObject, IDisposable
{
    private readonly RobotHub _hub;
    private readonly AppSettings _settings;
    private readonly MessageLog _log;

    public JournalViewModel(RobotHub hub, AppSettings settings, MessageLog? log = null)
    {
        _hub = hub;
        _settings = settings;
        _log = log ?? new MessageLog(keepDays: settings.MessageRetentionDays);
        // The records too old are cleared at start-up by the application (see App), and here whenever the user changes how long they last.
        _log.KeepDays = _settings.MessageRetentionDays;
        _settings.PropertyChanged += OnSettingChanged;
        if (_settings.RecordMessages) RecordInfo = RecordingTo;
    }

    private string RecordingTo => T($"Enregistrement dans {_log.Directory}", $"Recording to {_log.Directory}");

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.MessageRetentionDays):
                _log.KeepDays = _settings.MessageRetentionDays;
                _log.Purge();
                break;
            case nameof(AppSettings.RecordMessages):
                if (!_settings.RecordMessages) _log.Close();
                RecordInfo = _settings.RecordMessages ? RecordingTo : "";
                _hub.AddLog(_settings.RecordMessages ? T("enregistrement des messages activé", "message recording on") : T("enregistrement des messages arrêté", "message recording off"));
                break;
        }
    }

    public ObservableCollection<string> Log => _hub.Log;

    /// <summary>Where the recording stands, for the line above the log: empty when it is off.</summary>
    [ObservableProperty] private string _recordInfo = "";

    /// <summary>Called on the MQTT thread for every message on the robot's topics.</summary>
    public void CaptureMessage(RobotMessage m)
    {
        if (!_settings.RecordMessages) return;
        try
        {
            _log.Write(m);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Disk full, folder locked: the record stops, the session goes on.
            _hub.Post(() =>
            {
                _hub.AddLog(T($"enregistrement des messages interrompu : {ex.Message}", $"message recording interrupted: {ex.Message}"));
                _settings.RecordMessages = false;
            });
            return;
        }
        var (count, file) = (_log.Count, Path.GetFileName(_log.CurrentFile));
        _hub.Post(() => RecordInfo = T($"{count} message(s) aujourd'hui → {file}", $"{count} message(s) today → {file}"));
    }

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingChanged;
        _log.Dispose();
    }
}
