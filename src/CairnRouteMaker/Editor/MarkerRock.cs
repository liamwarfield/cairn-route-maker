using CairnRouteMaker.Game;
using MelonLoader;
using NVector3 = System.Numerics.Vector3;

namespace CairnRouteMaker.Editor;

/// <summary>
/// The rock under a start or finish marker, which decides which way its pad faces. Routes only store
/// the point, and its zone may not be loaded yet when a route is shown, so until the rock turns up
/// it's assumed to be flat ground and looked for again every few seconds.
/// </summary>
internal sealed class MarkerRock
{
    private const float RetrySeconds = 2f;

    private readonly string _name;
    private float _nextTry;
    private float _radius;
    private NVector3? _viewedAlong;

    public MarkerRock(string name) => _name = name;

    public NVector3 Point { get; private set; }

    /// <summary>
    /// Outward from the rock as a whole across the marker (see GameApi.AverageRockNormal), rather
    /// than from the one facet at its point: straight up until the rock is found.
    /// </summary>
    public NVector3 Normal { get; private set; } = NVector3.UnitY;

    /// <summary>Whether it's a wall Ava would start on climbing.</summary>
    public bool Climbing { get; private set; }

    public bool Found { get; private set; }

    /// <summary>
    /// Look for the rock at <paramref name="point"/>, judging its facing across
    /// <paramref name="radius"/>. <paramref name="viewedAlong"/> is the way the camera looked when
    /// the point was picked on a wall, if known: the rock is judged as seen from there.
    /// </summary>
    public void Reset(NVector3 point, float radius, NVector3? viewedAlong = null)
    {
        Point = point;
        _radius = radius;
        _viewedAlong = viewedAlong;
        Normal = NVector3.UnitY;
        Climbing = false;
        Found = false;
        _nextTry = float.NegativeInfinity;
    }

    /// <summary>Look for the rock if it's due; true if it was found just now.</summary>
    public bool Update(float time)
    {
        if (Found || time < _nextTry)
        {
            return false;
        }

        _nextTry = time + RetrySeconds;
        var point = RouteOverlay.ToUnity(Point);
        if (!GameApi.TryFindRock(point, out var facet, out var climbing))
        {
            return false;
        }

        // Twice: the second look is straight at the face the first one found.
        var axis = climbing && _viewedAlong is { } view ? -RouteOverlay.ToUnity(view) : facet;
        axis = GameApi.AverageRockNormal(point, axis, _radius);
        axis = GameApi.AverageRockNormal(point, axis, _radius);
        Normal = NVector3.Normalize(RouteOverlay.ToNumerics(axis));
        Climbing = climbing;
        Found = true;
        MelonLogger.Msg($"{_name} marker on {(climbing ? "a wall" : "the ground")}: rock faces {Normal:0.00} (the facet at the point, {RouteOverlay.ToNumerics(facet):0.00}).");
        return true;
    }
}
