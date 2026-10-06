using System.Globalization;
using System.Text;
using System.Text.Json;
using DyssCockpit.Core;

namespace DyssCockpit.Presentation.Services;

/// <summary>
/// Every message exchanged with the robot, one JSON object per line, in one file per day under
/// %APPDATA%\DySS Cockpit\messages — the same line shape as the CLI's "watch --log" and the captures
/// made so far. Files older than <see cref="KeepDays"/> days are deleted when a new day starts, so
/// leaving it on for good does not fill the disk. Written from the MQTT thread.
/// </summary>
public sealed class MessageLog(string? directory = null) : IDisposable
{
    public const int KeepDays = 30;
    private const string Prefix = "messages-";

    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public string Directory { get; } = directory ?? Path.Combine(SessionStore.Directory, "messages");
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
        CurrentFile = Path.Combine(Directory, $"{Prefix}{day:yyyy-MM-dd}.jsonl");
        // Appended to, so a restart during the day keeps one file. No byte order mark: it breaks JSON parsers.
        _writer = new StreamWriter(CurrentFile, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        _day = day;
        Count = 0;
        DeleteOldFiles(day);
    }

    private void DeleteOldFiles(DateOnly today)
    {
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, $"{Prefix}*.jsonl"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)[Prefix.Length..];
            if (DateOnly.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                && d < today.AddDays(-KeepDays))
            {
                try { File.Delete(file); }
                catch (IOException) { }   // open elsewhere: next time
            }
        }
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
