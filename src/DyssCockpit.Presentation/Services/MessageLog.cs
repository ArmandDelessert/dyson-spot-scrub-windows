using System.Text;
using System.Text.Json;
using DyssCockpit.Core;

namespace DyssCockpit.Presentation.Services;

/// <summary>
/// Every message exchanged with the robot, one JSON object per line, in one file per day under
/// %LOCALAPPDATA%\DySS Cockpit\Messages — the same line shape as the CLI's "watch --log" and the captures
/// made so far. Files older than <see cref="KeepDays"/> days are deleted when a new day starts, so
/// leaving it on for good does not fill the disk. Written from the MQTT thread.
/// </summary>
public sealed class MessageLog(string? directory = null, int keepDays = MessageLog.DefaultKeepDays, TimeProvider? time = null) : IDisposable
{
    public const int DefaultKeepDays = 30;
    internal const string Prefix = "messages-";
    internal const string Extension = ".jsonl";

    private readonly object _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private StreamWriter? _writer;
    private DateOnly _day;
    private int _keepDays = keepDays;

    public string Directory { get; } = directory ?? AppFolders.Messages(SessionStore.Directory);

    /// <summary>How many days back the files are kept; zero or less keeps them all.</summary>
    public int KeepDays
    {
        get { lock (_gate) return _keepDays; }
        set { lock (_gate) _keepDays = value; }
    }
    public string? CurrentFile { get; private set; }
    /// <summary>Messages written to today's file since it was opened by this run.</summary>
    public int Count { get; private set; }

    /// <summary>Appends one message to the file of its day, opening or switching files as needed. IO errors are the caller's to handle.</summary>
    public void Write(RobotMessage m)
    {
        var line = JsonSerializer.Serialize(new
        {
            time = m.ReceivedUtc,
            topic = m.Topic,
            payload = (object?)m.Json ?? m.Payload,
        });
        var day = DateOnly.FromDateTime(m.ReceivedUtc.LocalDateTime);
        lock (_gate)
        {
            if (_writer is null || day != _day) Open(day);
            _writer!.WriteLine(line);
            Count++;
        }
    }

    private void Open(DateOnly day)
    {
        CloseWriter();
        System.IO.Directory.CreateDirectory(Directory);
        CurrentFile = Path.Combine(Directory, $"{Prefix}{day:yyyy-MM-dd}{Extension}");
        // Appended to, so a restart during the day keeps one file. No byte order mark: it breaks JSON parsers.
        _writer = new StreamWriter(CurrentFile, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        _day = day;
        Count = 0;
        DatedFiles.DeleteOlderThan(Directory, Prefix, Extension, day, _keepDays);
    }

    /// <summary>
    /// Deletes the files that are too old as of today. A day that starts does it by itself while
    /// messages are being recorded; this is for when they are not, at start-up and when the
    /// number of days is changed.
    /// </summary>
    public void Purge()
    {
        int days;
        lock (_gate) days = _keepDays;
        DatedFiles.DeleteOlderThan(Directory, Prefix, Extension, DateOnly.FromDateTime(_time.GetLocalNow().DateTime), days);
    }

    /// <summary>Closes today's file; the next message opens it again.</summary>
    public void Close()
    {
        lock (_gate) CloseWriter();
    }

    private void CloseWriter()
    {
        var w = _writer;
        _writer = null;
        // Closing flushes, which fails the same way the writes did when the disk is the problem.
        try { w?.Dispose(); }
        catch (IOException) { }
    }

    public void Dispose() => Close();
}
