using CairnRouteMaker.Game;
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

    private float _nextTry;

    public NVector3 Point { get; private set; }

    /// <summary>Outward from the rock: straight up until it's found.</summary>
    public NVector3 Normal { get; private set; } = NVector3.UnitY;

    /// <summary>Whether it's a wall Ava would start on climbing.</summary>
    public bool Climbing { get; private set; }

    public bool Found { get; private set; }

    public void Reset(NVector3 point)
    {
        Point = point;
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
        if (!GameApi.TryFindRock(RouteOverlay.ToUnity(Point), out var normal, out var climbing))
        {
            return false;
        }

        Normal = NVector3.Normalize(RouteOverlay.ToNumerics(normal));
        Climbing = climbing;
        Found = true;
        return true;
    }
}
