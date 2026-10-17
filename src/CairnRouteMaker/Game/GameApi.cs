using System;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;
using MelonLoader;
using Unity.Cinemachine;
using UnityEngine;

namespace CairnRouteMaker.Game;

/// <summary>
/// The only place that touches Cairn's own types, so game updates break one file.
/// See docs/game-internals.md for where these names come from.
/// </summary>
internal static class GameApi
{
    // Layers 3 Ground, 6 ClimbingWall, 12 ClimbingHolds; used if Helpers isn't initialised yet.
    private const int FallbackSurfaceMask = (1 << 3) | (1 << 6) | (1 << 12);

    private static bool _savedDisableInputs;
    private static bool _inputBlocked;
    private static CinemachineBrain _disabledBrain;

    /// <summary>Rock, holds and ground: what the brush and start marker can land on.</summary>
    public static int SurfaceMask
    {
        get
        {
            try
            {
                var mask = Helpers.ClimbingWallAndHoldsAndGroundLayerMask;
                return mask != 0 ? mask : FallbackSurfaceMask;
            }
            catch (Exception)
            {
                return FallbackSurfaceMask;
            }
        }
    }

    /// <summary>The game's own debug-line shader, as a last resort for drawing the overlay.</summary>
    public static Shader LineMaterialShader()
    {
        try
        {
            var material = Helpers.GLLineMaterial;
            return material == null ? null : material.shader;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static PawnControllerSwitcher Switcher
    {
        get
        {
            var pawns = PawnManager.Instance;
            return pawns == null ? null : pawns.PawnControllerSwitcher;
        }
    }

    public static bool IsAvaReady => PawnManager.MCSpawned && Switcher != null;

    /// <summary>"World/Zone", or null outside the game world (menus, loading).</summary>
    public static string CurrentZoneKey
    {
        get
        {
            try
            {
                var streaming = StreamingManager.Instance;
                if (streaming == null || streaming.World == null || streaming.CurrentZone == null)
                {
                    return null;
                }

                return $"{streaming.World.WorldId}/{streaming.CurrentZone.name}";
            }
            catch (Exception)
            {
                // Singletons aren't up yet in menus and while loading.
                return null;
            }
        }
    }

    public static string GameVersion
    {
        get
        {
            try
            {
                return CairnVersion.ComputeFull();
            }
            catch (Exception)
            {
                return Application.version;
            }
        }
    }

    /// <summary>
    /// Stop the game reacting to keyboard, mouse and gamepad while the editor owns them.
    /// We still read devices directly through Keyboard.current / Mouse.current.
    /// Uses DisableInputs (what the game does while teleporting out of no-go areas), not
    /// SetIgnoreInputEvents: that one is for key rebinding and swallows device events entirely,
    /// so our own reads would go dead too.
    /// </summary>
    public static void BlockGameInput(bool block)
    {
        var input = InputManager.Instance;
        if (input == null || block == _inputBlocked)
        {
            return;
        }

        if (block)
        {
            _savedDisableInputs = input.disableInputs;
            input.DisableInputs();
            input.ShowAndFreeMouse();
        }
        else
        {
            input.EnableInputs();
            input.disableInputs = _savedDisableInputs;
            input.UpdateInputContext();
            input.HideAndLockMouse();
        }

        _inputBlocked = block;
    }

    /// <summary>
    /// Detach the main camera from Cinemachine so we can fly it ourselves. Re-enabling the brain
    /// hands control back and the gameplay camera blends back to Ava.
    /// </summary>
    public static Camera TakeCamera()
    {
        var camera = Camera.main;
        if (camera == null)
        {
            return null;
        }

        var brain = camera.GetComponent<CinemachineBrain>();
        if (brain != null && brain.enabled)
        {
            brain.enabled = false;
            _disabledBrain = brain;
        }

        return camera;
    }

    public static void ReleaseCamera()
    {
        if (_disabledBrain != null)
        {
            _disabledBrain.enabled = true;
        }

        _disabledBrain = null;
    }

    /// <summary>
    /// Put Ava on her feet at a position using the game's own teleport, which waits for the
    /// destination to stream in. Only done while she's walking: climbing keeps references to
    /// holds and ropes that a raw teleport would leave dangling.
    /// </summary>
    public static bool TryTeleportAva(Vector3 position, float yawDegrees, out string reason)
    {
        var switcher = Switcher;
        if (!IsAvaReady || switcher == null)
        {
            reason = "Ava isn't in the world yet.";
            return false;
        }

        if (switcher.CurrentMode != PawnControllerSwitcher.Mode.Walking)
        {
            reason = $"Ava must be on her feet to teleport (currently {switcher.CurrentMode}).";
            return false;
        }

        try
        {
            switcher.TransitionToMode(
                PawnControllerSwitcher.Mode.Walking,
                new Il2CppSystem.Nullable<Vector3>(position),
                new Il2CppSystem.Nullable<Quaternion>(Quaternion.Euler(0f, yawDegrees, 0f)),
                true,
                PawnControllerSwitcher.Context.None,
                true);
        }
        catch (Exception e)
        {
            MelonLogger.Error($"Teleport failed: {e}");
            reason = "The game refused the teleport (see log).";
            return false;
        }

        reason = null;
        return true;
    }
}
