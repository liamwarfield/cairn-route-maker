using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CairnRouteMaker.Routes;

/// <summary>
/// JSON format for sharing routes. Vectors are stored as [x, y, z] arrays so the file stays
/// readable and independent of Unity's types.
/// </summary>
public static class RouteFile
{
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(Route route)
    {
        var dto = new RouteDto
        {
            FormatVersion = FormatVersion,
            Name = route.Name,
            ZoneKey = route.ZoneKey,
            GameVersion = route.GameVersion,
            Start = route.Start == null
                ? null
                : new StartDto { Position = ToArray(route.Start.Position), Yaw = route.Start.YawDegrees },
            Strokes = route.Strokes
                .Select(stroke => new StrokeDto
                {
                    Mode = stroke.Mode,
                    Stamps = stroke.Stamps
                        .Select(stamp => new StampDto
                        {
                            P = ToArray(stamp.Position),
                            N = ToArray(stamp.Normal),
                            R = stamp.Radius,
                        })
                        .ToArray(),
                })
                .ToArray(),
        };
        return JsonSerializer.Serialize(dto, Options);
    }

    public static Route Deserialize(string json)
    {
        var dto = JsonSerializer.Deserialize<RouteDto>(json, Options)
            ?? throw new InvalidDataException("Route file is empty.");
        if (dto.FormatVersion > FormatVersion)
        {
            throw new InvalidDataException(
                $"Route file format {dto.FormatVersion} is newer than this mod supports ({FormatVersion}).");
        }

        var route = new Route
        {
            Name = dto.Name ?? "Untitled route",
            ZoneKey = dto.ZoneKey ?? "",
            GameVersion = dto.GameVersion ?? "",
        };
        if (dto.Start != null)
        {
            route.Start = new StartPosition { Position = FromArray(dto.Start.Position), YawDegrees = dto.Start.Yaw };
        }

        foreach (var strokeDto in dto.Strokes ?? Array.Empty<StrokeDto>())
        {
            var stroke = new Stroke(strokeDto.Mode);
            foreach (var stampDto in strokeDto.Stamps ?? Array.Empty<StampDto>())
            {
                stroke.Stamps.Add(new Stamp(FromArray(stampDto.P), FromArray(stampDto.N), stampDto.R));
            }

            route.Strokes.Add(stroke);
        }

        return route;
    }

    private static float[] ToArray(Vector3 v) => new[] { v.X, v.Y, v.Z };

    private static Vector3 FromArray(float[] a)
    {
        if (a == null || a.Length != 3)
        {
            throw new InvalidDataException("Expected a vector of three numbers.");
        }

        return new Vector3(a[0], a[1], a[2]);
    }

    private sealed class RouteDto
    {
        public int FormatVersion { get; set; }
        public string Name { get; set; }
        public string ZoneKey { get; set; }
        public string GameVersion { get; set; }
        public StartDto Start { get; set; }
        public StrokeDto[] Strokes { get; set; }
    }

    private sealed class StartDto
    {
        public float[] Position { get; set; }
        public float Yaw { get; set; }
    }

    private sealed class StrokeDto
    {
        public StrokeMode Mode { get; set; }
        public StampDto[] Stamps { get; set; }
    }

    private sealed class StampDto
    {
        public float[] P { get; set; }
        public float[] N { get; set; }
        public float R { get; set; }
    }
}
