using System;
using System.Collections.Generic;
using CairnRouteMaker.Routes;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace CairnRouteMaker.Editor;

/// <summary>
/// Where a route starts: a glowing ring painted on the rock with an arrow at its front, pointing the
/// way Ava will face (or up the wall, for a start she makes climbing), under a soft column of light
/// about her height.
/// </summary>
internal sealed class StartMarker
{
    private static readonly NVector3 Color = new(0.2f, 0.9f, 0.4f);

    /// <summary>The ring and arrow themselves, paler than their halo so they read as lines.</summary>
    private static readonly NVector3 LineColor = NVector3.Lerp(Color, NVector3.One, 0.45f);

    private const int PadResolution = 512;
    private const float PadSize = 2.3f;
    private const float PadDepth = 1f;

    /// <summary>
    /// How much of the ground facing the other way still gets the ring (see MarkerPad.Place): the
    /// paint's own setting, which keeps it off the sides of stones and grass.
    /// </summary>
    private const float PadMinFacing = 0.5f;

    /// <summary>Wall rock is rougher and seen from the side, so the ring reaches further onto it.</summary>
    private const float WallPadDepth = 1.6f;
    private const float WallPadMinFacing = 0.25f;
    private const float RingRadius = 0.55f;
    private const float LineHalfWidth = 0.03f;
    private const float HaloWidth = 0.1f;
    private const float HaloAlpha = 0.35f;

    /// <summary>The faint fill inside the ring: this at the middle, rising to <see cref="FillEdgeAlpha"/> at the ring.</summary>
    private const float FillAlpha = 0.06f;
    private const float FillEdgeAlpha = 0.18f;

    /// <summary>
    /// The arrow, a chevron pointing forward (+y) in front of the ring, where Ava doesn't stand on it.
    /// It's bolder than the ring and kept clear of it, so the two don't run together in grass.
    /// </summary>
    private static readonly (float X, float Y)[] Chevron = { (-0.25f, 0.75f), (0f, 0.97f), (0.25f, 0.75f) };
    private const float ArrowHalfWidth = 0.045f;

    private const float ColumnHeight = 2.4f;
    private const float ColumnHalfWidth = 0.45f;
    private const float ColumnTint = 0.1f;
    private const float ColumnGlow = 0.4f;

    /// <summary>On a wall the column stands where Ava will (GameApi's WallStandOff), reaching down to her feet.</summary>
    private const float WallStandOff = 0.3f;
    private const float WallColumnDrop = 1f;

    /// <summary>The column fades out as the camera comes this close (horizontally), so it doesn't fill the view.</summary>
    private const float NearFadeStart = 1f;
    private const float NearFadeEnd = 3f;

    private readonly MarkerPad _pad;
    private readonly GlowMesh _column;
    private readonly MarkerRock _rock = new("Start");
    private StartPosition _start;
    private bool _padDrawn;

    /// <summary>The column's foot, relative to the start point.</summary>
    private NVector3 _foot;

    public StartMarker(Transform parent, DecalPaint paint)
    {
        _pad = new MarkerPad(paint, parent, "RouteStart pad", PadResolution);
        _column = new GlowMesh(parent, "RouteStart");
        var triangles = new List<int>();
        GlowMesh.AddBeam(triangles, 0);
        _column.SetTriangles(GlowMesh.BeamVertices, triangles);
        _column.Visible = false;
    }

    public void Set(StartPosition start)
    {
        _start = start;
        _column.Visible = start != null;
        _pad.Visible = start != null;
        if (start == null)
        {
            return;
        }

        if (!_padDrawn)
        {
            _pad.Draw(PadSize, PadTexel);
            _padDrawn = true;
        }

        // The start's facing is the camera's from when it was set, which on a wall looked at the rock.
        _rock.Reset(start.Position, RingRadius, Facing(start));
        _rock.Update(Time.unscaledTime);
        Layout();
    }

    /// <summary>Lay the column out for the camera and breathe; call every frame, after the camera has moved.</summary>
    public void Update(Camera camera, float time)
    {
        if (_start == null || camera == null || !_column.Shown)
        {
            return;
        }

        if (_rock.Update(time))
        {
            Layout();
        }

        var eye = RouteOverlay.ToNumerics(camera.transform.position) - _start.Position;
        var toAxis = eye - _foot;
        toAxis.Y = 0f;
        var strength = GlowMesh.Breath(time, 0.25f) * GlowMesh.Smoothstep(NearFadeStart, NearFadeEnd, toAxis.Length());
        _column.BeginFrame();
        _column.SetBeam(0, _foot, ColumnHeight, ColumnHalfWidth, eye, Color, ColumnTint * strength, ColumnGlow * strength);
        _column.EndFrame();
        _pad.SetBrightness(GlowMesh.Breath(time, 0.3f));
    }

    /// <summary>Draw the painted ring over the route paint; see MarkerPad.DrawOnTop.</summary>
    public void DrawOnTop() => _pad.DrawOnTop();

    /// <summary>Put the pad on the rock and the column over it, now the rock's slope is known (or assumed).</summary>
    private void Layout()
    {
        var facing = Facing(_start);
        var normal = _rock.Normal;

        // On a wall the arrow points up it; under a roof there's no up along the rock, so it shows her facing.
        var forward = _rock.Climbing && MathF.Abs(normal.Y) < 0.9f ? NVector3.UnitY : facing;
        _pad.Place(_start.Position, normal, forward, _rock.Climbing ? WallPadDepth : PadDepth, _rock.Climbing ? WallPadMinFacing : PadMinFacing);

        _foot = _rock.Climbing ? normal * WallStandOff - NVector3.UnitY * WallColumnDrop : NVector3.Zero;
        _column.Position = RouteOverlay.ToUnity(_start.Position);
    }

    private static NVector3 Facing(StartPosition start)
    {
        var yaw = start.YawDegrees * MathF.PI / 180f;
        return new NVector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
    }

    private static NVector4 PadTexel(float x, float y)
    {
        var r = MathF.Sqrt(x * x + y * y);
        var ring = MathF.Abs(r - RingRadius) - LineHalfWidth;
        var arrow = MathF.Min(
            MarkerPad.SegmentDistance(x, y, Chevron[0], Chevron[1]),
            MarkerPad.SegmentDistance(x, y, Chevron[1], Chevron[2])) - ArrowHalfWidth;
        var edge = MathF.Min(ring, arrow);

        var line = MarkerPad.Coverage(edge, PadSize / PadResolution);
        var halo = HaloAlpha * (1f - GlowMesh.Smoothstep(0f, HaloWidth, edge));
        var inside = r / RingRadius;
        var fill = inside < 1f ? FillAlpha + (FillEdgeAlpha - FillAlpha) * inside * inside * inside * inside : 0f;
        return MarkerPad.Over(LineColor, line, Color, MathF.Max(halo, fill));
    }
}
