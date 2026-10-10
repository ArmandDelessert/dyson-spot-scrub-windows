using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DyssCockpit.Core;

namespace DyssCockpit.Presentation.Services;

/// <summary>
/// The cleans this computer has seen, kept on disk: the Dyson cloud only holds the last few (seven
/// on the account this was written against, going back three weeks), so the history would otherwise
/// shrink as new cleans push the old ones out. One file a clean,
/// <c>Cleans\clean-2026-10-10-&lt;cleanId&gt;.json.gz</c>, named after the day the clean began so that
/// <see cref="DatedFiles"/> can age them like the logs. Inside, compressed (a trail is hundreds of
/// kilobytes of numbers), two lines of JSON: the clean's line of the list, then its detail — what
/// the history tab needs to show it again without the cloud. Both are the application's own records,
/// whose unknown fields are kept, so what the cloud sent more than this version knows is not lost.
/// Everything is best effort: a file that cannot be read or written is skipped, never an error.
/// </summary>
public sealed class CleanArchive(string folder, TimeProvider? time = null)
{
    internal const string Prefix = "clean-";
    internal const string Extension = ".json.gz";

    private static readonly JsonSerializerOptions Format = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Folder { get; } = folder;

    /// <summary>The day a clean began, local time, which names its file; today when the cloud gave no start.</summary>
    private DateOnly DayOf(CleanSummary summary) =>
        DateOnly.FromDateTime((summary.Start?.ToLocalTime() ?? _time.GetLocalNow()).DateTime);

    private string? FileOf(string cleanId)
    {
        try
        {
            return Directory.Exists(Folder)
                ? Directory.EnumerateFiles(Folder, $"{Prefix}*-{cleanId}{Extension}").FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public bool Has(string cleanId) => FileOf(cleanId) is not null;

    /// <summary>Keeps a clean, unless it is already kept. False when it could not be written.</summary>
    public bool Save(CleanSummary summary, CleanDetail detail)
    {
        if (Has(summary.CleanId)) return true;
        try
        {
            Directory.CreateDirectory(Folder);
            using var packed = new MemoryStream();
            using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
            {
                writer.Write(JsonSerializer.Serialize(summary, Format));
                writer.Write('\n');
                writer.Write(JsonSerializer.Serialize(detail, Format));
                writer.Write('\n');
            }
            AtomicFile.WriteAllBytes(Path.Combine(Folder, $"{Prefix}{DayOf(summary):yyyy-MM-dd}-{summary.CleanId}{Extension}"), packed.ToArray());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>The detail of a kept clean; null when it is not kept or its file is unreadable.</summary>
    public CleanDetail? LoadDetail(string cleanId)
    {
        if (FileOf(cleanId) is not { } file) return null;
        try
        {
            using var reader = Open(file);
            _ = reader.ReadLine();
            return reader.ReadLine() is { } line ? JsonSerializer.Deserialize<CleanDetail>(line, Format) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return null; }
    }

    /// <summary>The list line of every kept clean, newest first. Only the first line of each file is read.</summary>
    public IReadOnlyList<CleanSummary> Summaries()
    {
        if (!Directory.Exists(Folder)) return [];
        var found = new List<CleanSummary>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(Folder, $"{Prefix}*{Extension}"))
            {
                try
                {
                    using var reader = Open(file);
                    if (reader.ReadLine() is { } line && JsonSerializer.Deserialize<CleanSummary>(line, Format) is { } summary)
                        found.Add(summary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return [.. found.OrderByDescending(s => s.StartTime ?? 0)];
    }

    /// <summary>How many files <see cref="Purge"/> would delete with that many days.</summary>
    public int CountOlderThan(int days) =>
        DatedFiles.CountOlderThan(Folder, Prefix, Extension, DateOnly.FromDateTime(_time.GetLocalNow().DateTime), days);

    /// <summary>Deletes the cleans that began more than <paramref name="days"/> days ago; zero or less keeps them all.</summary>
    public void Purge(int days) =>
        DatedFiles.DeleteOlderThan(Folder, Prefix, Extension, DateOnly.FromDateTime(_time.GetLocalNow().DateTime), days);

    private static StreamReader Open(string file) =>
        new(new GZipStream(File.OpenRead(file), CompressionMode.Decompress), Encoding.UTF8);
}
