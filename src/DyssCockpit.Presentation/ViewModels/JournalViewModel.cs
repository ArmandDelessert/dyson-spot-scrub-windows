using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DyssCockpit.Presentation.Services;
using DyssCockpit.Core;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.Presentation.ViewModels;

/// <summary>
/// The Journal tab: the curated log everything writes to through <see cref="RobotHub.AddLog"/>,
/// and, when ticked, the record of every message on the robot's topics in a daily file (see
/// <see cref="MessageLog"/>), for finding what the tracker doesn't know yet. The tick is kept
/// from one run to the next.
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
    }

    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppSettings.MessageRetentionDays)) return;
        _log.KeepDays = _settings.MessageRetentionDays;
        _log.Purge();
    }

    public ObservableCollection<string> Log => _hub.Log;

    [ObservableProperty] private string _recordInfo = "";

    /// <summary>Whether every message is written to the daily file.</summary>
    public bool RecordMessages
    {
        get => _settings.RecordMessages;
        set
        {
            if (_settings.RecordMessages == value) return;
            _settings.RecordMessages = value;
            if (!value) _log.Close();
            OnPropertyChanged();
            RecordInfo = value ? T($"Enregistrement dans {_log.Directory}", $"Recording to {_log.Directory}") : "";
            _hub.AddLog(value ? T("enregistrement des messages activé", "message recording on") : T("enregistrement des messages arrêté", "message recording off"));
        }
    }

    public string RecordFolder => _log.Directory;

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
                RecordMessages = false;
            });
            return;
        }
        var (count, file) = (_log.Count, Path.GetFileName(_log.CurrentFile));
        _hub.Post(() => RecordInfo = T($"{count} message(s) aujourd'hui → {file}", $"{count} message(s) today → {file}"));
    }

    [RelayCommand]
    private void OpenRecordFolder()
    {
        try
        {
            Directory.CreateDirectory(_log.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_log.Directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _hub.AddLog(T($"dossier des messages : {ex.Message}", $"message folder: {ex.Message}"));
        }
    }

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingChanged;
        _log.Dispose();
    }
}
