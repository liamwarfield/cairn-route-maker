using System.Collections.Generic;
using System.Numerics;

namespace CairnRouteMaker.Routes;

public enum StrokeMode
{
    Allow,
    Deny,
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

/// <summary>
/// A climbing route: an ordered list of allow/deny strokes plus where Ava starts.
/// Anything not covered by a stroke is off-route, and later strokes override earlier ones,
/// so painting an area, denying holds inside it, and gym-style hold setting are all the same data.
/// </summary>
public sealed class Route
{
    public string Name { get; set; } = "Untitled route";

    /// <summary>Which world/zone the coordinates belong to, e.g. "Kami/Zone_Foo".</summary>
    public string ZoneKey { get; set; } = "";

    public string GameVersion { get; set; } = "";
    public StartPosition Start { get; set; }
    public List<Stroke> Strokes { get; } = new();

    public bool IsEmpty => Start == null && Strokes.Count == 0;

    /// <summary>
    /// Whether a point counts as on-route: the last stroke containing it decides.
    /// Not used for scoring yet; it is here so the rule lives next to the data it interprets.
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
