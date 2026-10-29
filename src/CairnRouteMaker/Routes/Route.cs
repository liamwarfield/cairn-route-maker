using System.Collections.Generic;
using System.Numerics;

namespace CairnRouteMaker.Routes;

public enum StrokeMode
{
    Allow,
    Deny,

    /// <summary>Clears earlier paint back to unpainted (which counts as off-route).</summary>
    Erase,
}

/// <summary>One brush dab: a sphere of influence centred on the rock surface.</summary>
public readonly record struct Stamp(Vector3 Position, Vector3 Normal, float Radius);

/// <summary>Everything painted between pressing and releasing the brush.</summary>
public sealed class Stroke
{
    public StrokeMode Mode { get; }
    public List<Stamp> Stamps { get; } = new();

    public Stroke(StrokeMode mode) => Mode = mode;
}

public sealed class StartPosition
{
    public Vector3 Position { get; set; }

    /// <summary>Facing, in degrees clockwise from world +Z.</summary>
    public float YawDegrees { get; set; }
}

/// <summary>The top of a route: the climb is complete once Ava is inside this sphere.</summary>
public sealed class FinishZone
{
    public Vector3 Position { get; set; }
    public float Radius { get; set; }

    /// <summary>
    /// Whether any part of the segment from Ava's root up through her body touches the sphere, so
    /// the check doesn't depend on exactly where the game puts her root bone.
    /// </summary>
    public bool Contains(Vector3 bottom, Vector3 top)
    {
        var segment = top - bottom;
        var lengthSquared = segment.LengthSquared();
        var t = lengthSquared > 0f ? System.Math.Clamp(Vector3.Dot(Position - bottom, segment) / lengthSquared, 0f, 1f) : 0f;
        return Vector3.DistanceSquared(bottom + segment * t, Position) <= Radius * Radius;
    }
}

/// <summary>
/// A control point of a route line: a spot on the rock and the rock's outward normal there.
/// <paramref name="Rappel"/> marks the piece of line from the point before to this one as a rappel,
/// which is drawn dotted; the first point's is unused.
/// </summary>
public readonly record struct LinePoint(Vector3 Position, Vector3 Normal, bool Rappel = false);

/// <summary>A route line's colour: the difficulty scale of the game's map, easiest first.</summary>
public enum LineGrade
{
    Yellow,
    Green,
    Blue,
    Red,
    Black,
    Purple,
    White,
}

/// <summary>
/// One route line: control points bottom to top, with a smooth curve drawn through them (see
/// <see cref="RouteCurve"/>), solid where it's climbed and dotted where it's rappelled. It's only a
/// guide and doesn't affect what counts as on-route.
/// </summary>
public sealed class ClimbLine
{
    public LineGrade Grade { get; set; } = LineGrade.Green;
    public List<LinePoint> Points { get; } = new();

    public ClimbLine Clone()
    {
        var copy = new ClimbLine { Grade = Grade };
        copy.Points.AddRange(Points);
        return copy;
    }
}

/// <summary>
/// A climbing route: an ordered list of allow/deny strokes plus where Ava starts and finishes.
/// Anything not covered by a stroke is off-route, and later strokes override earlier ones,
/// so painting an area, denying holds inside it, and gym-style hold setting are all the same data.
/// </summary>
public sealed class Route
{
    /// <summary>
    /// The file it's saved in, without ".json"; set by <see cref="RouteStorage"/>, and null until
    /// the route is first saved. It isn't written into the file.
    /// </summary>
    public string Id { get; set; }

    public string Name { get; set; } = "Untitled route";

    /// <summary>A line or two about the route, or null.</summary>
    public string Description { get; set; }

    /// <summary>Which world/zone the coordinates belong to, e.g. "Kami/Zone_Foo".</summary>
    public string ZoneKey { get; set; } = "";

    public string GameVersion { get; set; } = "";
    public StartPosition Start { get; set; }
    public FinishZone Finish { get; set; }
    public List<Stroke> Strokes { get; } = new();

    /// <summary>The route's lines, e.g. the main line and its variants, each in its own colour.</summary>
    public List<ClimbLine> Lines { get; } = new();

    public bool IsEmpty => Start == null && Finish == null && Strokes.Count == 0 && !Lines.Exists(line => line.Points.Count > 0);

    /// <summary>
    /// Whether a point counts as on-route: the last stroke containing it decides, and only an
    /// allow stroke puts it on-route (deny marks it off-route, erase clears it back to unpainted).
    /// </summary>
    public bool IsAllowed(Vector3 point)
    {
        for (var s = Strokes.Count - 1; s >= 0; s--)
        {
            foreach (var stamp in Strokes[s].Stamps)
            {
                if (Vector3.DistanceSquared(stamp.Position, point) <= stamp.Radius * stamp.Radius)
                {
                    return Strokes[s].Mode == StrokeMode.Allow;
                }
            }
        }

        return false;
    }
}
