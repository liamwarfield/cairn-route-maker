using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CairnRouteMaker.Routes;

/// <summary>
/// The player's fastest on-route time for each route, by route id, in one file of their own. They
/// aren't kept in the route files, so sharing a route doesn't share its times. The file is read the
/// first time it's needed and written whenever a time changes.
/// </summary>
public sealed class PersonalBests
{
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private Dictionary<string, BestDto> _bests;

    public PersonalBests(string path) => _path = path;

    /// <summary>The best time in seconds, or null if the route hasn't been finished on-route yet.</summary>
    public double? Get(string routeId) =>
        routeId != null && Bests.TryGetValue(routeId, out var best) ? best.Seconds : null;

    /// <summary>Keep a finished time if it beats the route's best so far, or there isn't one.</summary>
    /// <returns>True if it's the new best.</returns>
    public bool Record(string routeId, double seconds, DateTime when)
    {
        if (routeId == null || Get(routeId) is { } best && best <= seconds)
        {
            return false;
        }

        Bests[routeId] = new BestDto { Seconds = seconds, Set = when };
        Save();
        return true;
    }

    /// <summary>Forget a route's best time.</summary>
    /// <returns>True if it had one.</returns>
    public bool Clear(string routeId)
    {
        if (routeId == null || !Bests.Remove(routeId))
        {
            return false;
        }

        Save();
        return true;
    }

    private Dictionary<string, BestDto> Bests => _bests ??= Load();

    private Dictionary<string, BestDto> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var dto = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(_path), Options);
                if (dto?.Routes != null)
                {
                    return new Dictionary<string, BestDto>(dto.Routes);
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            // Unreadable times shouldn't stop routes being timed.
        }

        return new Dictionary<string, BestDto>();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path)) ?? ".");
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new FileDto { FormatVersion = FormatVersion, Routes = _bests }, Options));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed class FileDto
    {
        public int FormatVersion { get; set; }
        public Dictionary<string, BestDto> Routes { get; set; }
    }

    private sealed class BestDto
    {
        public double Seconds { get; set; }

        /// <summary>When the time was set, local time.</summary>
        public DateTime Set { get; set; }
    }
}
