using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyDyson.Core;

namespace MyDyson.App.ViewModels;

/// <summary>
/// The Journal tab: the curated log everything writes to through <see cref="RobotHub.AddLog"/>,
/// and the raw capture of every message on the robot's topics, for finding what the tracker
/// doesn't know yet.
/// </summary>
public sealed partial class JournalViewModel(RobotHub hub) : ObservableObject, IDisposable
{
    public ObservableCollection<string> Log => hub.Log;

    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private string _captureButtonLabel = "Capturer tous les messages…";
    [ObservableProperty] private string _captureInfo = "";
    private StreamWriter? _captureWriter;
    private readonly object _captureLock = new();
    private string? _captureFileName;
    private int _captureCount;

    /// <summary>
    /// Writes every message the robot exchanges, one JSON object per line, in the same shape the
    /// CLI's "watch --log" produces. The Journal tab only ever shows a curated subset (recognised
    /// events and our own command results); this is the way to see everything, including message
    /// types nothing in this app understands yet. Called on the MQTT thread.
    /// </summary>
    public void CaptureMessage(RobotMessage m)
    {
        StreamWriter? w;
        lock (_captureLock) { w = _captureWriter; }
        if (w is null) return;

        var line = JsonSerializer.Serialize(new
        {
            time = m.ReceivedUtc,
            topic = m.Topic,
            payload = (object?)m.Json ?? m.Payload,
        });
        lock (_captureLock) { _captureWriter?.WriteLine(line); }
        _captureCount++;
        hub.Post(() => CaptureInfo = $"{_captureCount} message(s) → {_captureFileName}");
    }

    [RelayCommand]
    private void ToggleCapture()
    {
        if (IsCapturing) { StopCapture(); return; }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON Lines (*.jsonl)|*.jsonl",
            FileName = $"capture-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.jsonl",
        };
        if (dlg.ShowDialog() != true) return;

        lock (_captureLock)
        {
            // No byte order mark: a BOM at the head of a JSON Lines file breaks standard JSON parsers.
            _captureWriter = new StreamWriter(dlg.FileName, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            _captureCount = 0;
        }
        _captureFileName = Path.GetFileName(dlg.FileName);
        IsCapturing = true;
        CaptureButtonLabel = "Arrêter la capture";
        CaptureInfo = $"0 message(s) → {_captureFileName}";
        hub.AddLog($"capture démarrée : {dlg.FileName}");
    }

    private void StopCapture()
    {
        StreamWriter? w;
        lock (_captureLock) { w = _captureWriter; _captureWriter = null; }
        w?.Dispose();
        IsCapturing = false;
        CaptureButtonLabel = "Capturer tous les messages…";
        CaptureInfo = _captureCount > 0 ? $"Dernière capture : {_captureCount} message(s) dans {_captureFileName}" : "";
        hub.AddLog($"capture arrêtée, {_captureCount} message(s) enregistré(s)");
    }

    /// <summary>Closes the capture file if one is open; safe to call more than once.</summary>
    public void Dispose()
    {
        if (IsCapturing) StopCapture();
    }
}
