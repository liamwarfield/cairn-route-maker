using System;
using System.Collections.Generic;
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
    /// <summary>
    /// 2 added erase strokes, 3 the finish, 4 a route line, 5 several lines with colours, 6 a
    /// description, 7 rappel pieces of lines, 8 the loadout; older files read unchanged (a version
    /// 4 line becomes one green line, and files without a loadout get the default one).
    /// </summary>
    public const int FormatVersion = 8;

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
            Description = route.Description,
            ZoneKey = route.ZoneKey,
            GameVersion = route.GameVersion,
            Start = route.Start == null
                ? null
                : new StartDto { Position = ToArray(route.Start.Position), Yaw = route.Start.YawDegrees },
            Finish = route.Finish == null
                ? null
                : new FinishDto { Position = ToArray(route.Finish.Position), Radius = route.Finish.Radius },
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
            Lines = route.Lines.TrueForAll(line => line.Points.Count == 0)
                ? null
                : route.Lines
                    .Where(line => line.Points.Count > 0)
                    .Select(line => new LineDto { Grade = line.Grade, Points = ToDtos(line.Points) })
                    .ToArray(),
            Loadout = route.Loadout.IsDefault ? null : ToDto(route.Loadout),
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
            Description = dto.Description,
            ZoneKey = dto.ZoneKey ?? "",
            GameVersion = dto.GameVersion ?? "",
        };
        if (dto.Start != null)
        {
            route.Start = new StartPosition { Position = FromArray(dto.Start.Position), YawDegrees = dto.Start.Yaw };
        }

        if (dto.Finish != null)
        {
            route.Finish = new FinishZone { Position = FromArray(dto.Finish.Position), Radius = dto.Finish.Radius };
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

        foreach (var lineDto in dto.Lines ?? Array.Empty<LineDto>())
        {
            var line = new ClimbLine { Grade = lineDto.Grade };
            AddPoints(line, lineDto.Points);
            route.Lines.Add(line);
        }

        if (dto.Line is { Length: > 0 })
        {
            var line = new ClimbLine();
            AddPoints(line, dto.Line);
            route.Lines.Add(line);
        }

        if (dto.Loadout != null)
        {
            route.Loadout = FromDto(dto.Loadout);
        }

        return route;
    }

    private static LoadoutDto ToDto(Loadout loadout) => new()
    {
        Health = loadout.Health,
        LeftHand = loadout.LeftHand,
        RightHand = loadout.RightHand,
        EndGame = loadout.EndGame,
        SurvivalNeeds = loadout.SurvivalNeeds,
        Items = loadout.Items?
            .Where(item => item.Count > 0)
            .Select(item => new ItemDto { Id = item.Id, Count = item.Count })
            .ToArray(),
    };

    /// <summary>Missing fields keep their defaults, and out-of-range numbers are brought back into range.</summary>
    private static Loadout FromDto(LoadoutDto dto)
    {
        var loadout = new Loadout
        {
            LeftHand = dto.LeftHand ?? Grip.Taped,
            RightHand = dto.RightHand ?? Grip.Taped,
            EndGame = dto.EndGame ?? false,
            SurvivalNeeds = dto.SurvivalNeeds ?? false,
        };
        if (dto.Health is { } health)
        {
            var notch = (int)MathF.Round((float)(Math.Clamp(health, Loadout.MinHealth, Loadout.MaxHealth) - Loadout.MinHealth) / Loadout.HealthStep);
            loadout.Health = Loadout.MinHealth + notch * Loadout.HealthStep;
        }

        if (dto.Items != null)
        {
            loadout.Items = new List<LoadoutItem>();
            foreach (var item in dto.Items)
            {
                if (!string.IsNullOrEmpty(item.Id) && item.Count > 0)
                {
                    loadout.Items.Add(new LoadoutItem(item.Id, Math.Min(item.Count, LoadoutItem.MaxCount)));
                }
            }
        }

        return loadout;
    }

    private static LinePointDto[] ToDtos(List<LinePoint> points) =>
        points.Select(point => new LinePointDto { P = ToArray(point.Position), N = ToArray(point.Normal), Rappel = point.Rappel ? true : null })
            .ToArray();

    private static void AddPoints(ClimbLine line, LinePointDto[] points)
    {
        foreach (var pointDto in points ?? Array.Empty<LinePointDto>())
        {
            line.Points.Add(new LinePoint(FromArray(pointDto.P), FromArray(pointDto.N), pointDto.Rappel == true));
        }
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
        public string Description { get; set; }
        public string ZoneKey { get; set; }
        public string GameVersion { get; set; }
        public StartDto Start { get; set; }
        public FinishDto Finish { get; set; }
        public StrokeDto[] Strokes { get; set; }
        public LineDto[] Lines { get; set; }

        /// <summary>Version 4's single line; only read.</summary>
        public LinePointDto[] Line { get; set; }

        public LoadoutDto Loadout { get; set; }
    }

    private sealed class LoadoutDto
    {
        public int? Health { get; set; }
        public Grip? LeftHand { get; set; }
        public Grip? RightHand { get; set; }
        public bool? EndGame { get; set; }
        public bool? SurvivalNeeds { get; set; }

        /// <summary>Null: she keeps what she has. Empty: she carries nothing.</summary>
        public ItemDto[] Items { get; set; }
    }

    private sealed class ItemDto
    {
        public string Id { get; set; }
        public int Count { get; set; }
    }

    private sealed class LineDto
    {
        public LineGrade Grade { get; set; }
        public LinePointDto[] Points { get; set; }
    }

    private sealed class StartDto
    {
        public float[] Position { get; set; }
        public float Yaw { get; set; }
    }

    private sealed class FinishDto
    {
        public float[] Position { get; set; }
        public float Radius { get; set; }
    }

    private sealed class StrokeDto
    {
        public StrokeMode Mode { get; set; }
        public StampDto[] Stamps { get; set; }
    }

    private sealed class LinePointDto
    {
        public float[] P { get; set; }
        public float[] N { get; set; }

        /// <summary>True if the piece leading to this point is a rappel; left out otherwise.</summary>
        public bool? Rappel { get; set; }
    }

    private sealed class StampDto
    {
        public float[] P { get; set; }
        public float[] N { get; set; }
        public float R { get; set; }
    }
}
