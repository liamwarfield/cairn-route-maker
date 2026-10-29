using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnRouteMaker.Controls;

/// <summary>Which heading a control is listed under in Settings > Controls > Remap controls.</summary>
internal enum ControlSection
{
    Freecam,
    RouteEditor,
}

/// <summary>
/// The editor modes a control does something in. The remap screen flags two controls as clashing
/// when they share a key and a mode. Controls with no modes are never flagged.
/// </summary>
[Flags]
internal enum ControlModes
{
    None = 0,
    Freecam = 1,
    RouteEditor = 2,
    Both = Freecam | RouteEditor,
}

/// <summary>
/// One rebindable command. It has a keyboard/mouse action and a gamepad action, each with a single
/// binding, so the game's remap screen can list them separately (it keeps one list per device).
/// </summary>
internal sealed class Control
{
    public Control(string name, ControlSection section, ControlModes modes, string label, string keyboard, string gamepad, string gamepadLabel = null)
    {
        Name = name;
        Section = section;
        Modes = modes;
        Label = label;
        GamepadLabel = gamepadLabel ?? label;
        DefaultKeyboard = keyboard;
        DefaultGamepad = gamepad;
    }

    public string Name { get; }
    public ControlSection Section { get; }
    public ControlModes Modes { get; }
    public string Label { get; }
    public string GamepadLabel { get; }
    public string DefaultKeyboard { get; }
    public string DefaultGamepad { get; }

    /// <summary>Null until <see cref="ModControls.EnsureCreated"/>, and for controls with no keyboard default.</summary>
    public InputAction Keyboard { get; internal set; }

    /// <summary>Null until <see cref="ModControls.EnsureCreated"/>, and for controls with no gamepad default.</summary>
    public InputAction Gamepad { get; internal set; }

    public bool KeyHeld => Keyboard != null && Keyboard.IsPressed();
    public bool KeyPressed => Keyboard != null && Keyboard.WasPressedThisFrame();
    public bool KeyReleased => Keyboard != null && Keyboard.WasReleasedThisFrame();
    public bool PadHeld => Gamepad != null && Gamepad.IsPressed();
    public bool PadPressed => Gamepad != null && Gamepad.WasPressedThisFrame();
    public bool PadReleased => Gamepad != null && Gamepad.WasReleasedThisFrame();

    /// <summary>The current keyboard/mouse binding as the game would print it, e.g. "W" or "RMB".</summary>
    public string KeyName => ModControls.DisplayName(Keyboard);

    /// <summary>The current gamepad binding as the game would print it.</summary>
    public string PadName => ModControls.DisplayName(Gamepad);
}

/// <summary>
/// The mod's controls, as Input System actions so the game's remap screen can rebind them (see
/// Game/ControlsMenuPatches). The actions live in their own asset, not the game's, so the game's
/// input contexts never switch them off. They stay enabled and are only read when needed.
/// Rebinds are saved to their own file, since the game saves only its own actions.
///
/// Gamepad sticks, mouse movement, the scroll wheel, L3+R3 and menu navigation aren't listed:
/// the remap screen only binds buttons, and the game doesn't list its own sticks either.
/// </summary>
internal static class ModControls
{
    private const string KeyboardMap = "Keyboard";
    private const string GamepadMap = "Gamepad";

    // Freecam: flying the camera, which the route editor also uses, plus the commands for both modes.
    public static readonly Control MoveForward = new("MoveForward", ControlSection.Freecam, ControlModes.Both, "Move forward", "<Keyboard>/w", null);
    public static readonly Control MoveBack = new("MoveBack", ControlSection.Freecam, ControlModes.Both, "Move back", "<Keyboard>/s", null);
    public static readonly Control MoveLeft = new("MoveLeft", ControlSection.Freecam, ControlModes.Both, "Move left", "<Keyboard>/a", null);
    public static readonly Control MoveRight = new("MoveRight", ControlSection.Freecam, ControlModes.Both, "Move right", "<Keyboard>/d", null);
    public static readonly Control MoveDown = new("MoveDown", ControlSection.Freecam, ControlModes.Both, "Move down", "<Keyboard>/q", "<Gamepad>/leftShoulder");
    public static readonly Control MoveUp = new("MoveUp", ControlSection.Freecam, ControlModes.Both, "Move up", "<Keyboard>/e", "<Gamepad>/rightShoulder");
    public static readonly Control Fast = new("Fast", ControlSection.Freecam, ControlModes.Both, "Move fast (hold)", "<Keyboard>/leftShift", "<Gamepad>/leftTrigger");
    public static readonly Control Medium = new("Medium", ControlSection.Freecam, ControlModes.Both, "Move at medium speed (hold)", "<Keyboard>/leftAlt", null);
    public static readonly Control Look = new("Look", ControlSection.Freecam, ControlModes.Both, "Look around (hold)", "<Mouse>/rightButton", null);
    public static readonly Control Teleport = new("Teleport", ControlSection.Freecam, ControlModes.Freecam, "Move Ava to the marker", "<Mouse>/leftButton", "<Gamepad>/buttonSouth");
    public static readonly Control PlayTest = new("PlayTest", ControlSection.Freecam, ControlModes.Both, "Play-test route", "<Keyboard>/p", "<Gamepad>/start");
    public static readonly Control LevelSelect = new("LevelSelect", ControlSection.Freecam, ControlModes.Both, "Level select", "<Keyboard>/l", "<Gamepad>/dpad/left");
    public static readonly Control Exit = new("Exit", ControlSection.Freecam, ControlModes.Both, "Close freecam / route editor", "<Keyboard>/escape", "<Gamepad>/buttonEast");

    // The keyboard key works anywhere in free climb; the gamepad button only with the editor open.
    public static readonly Control ToggleFreecam = new("ToggleFreecam", ControlSection.Freecam, ControlModes.Both, "Freecam / route editor", "<Keyboard>/f8", "<Gamepad>/rightStickPress", "Switch freecam / route editor");

    // Route editor: painting and the route's markers. Keyboard-only entries work anywhere in free climb.
    public static readonly Control OpenEditor = new("OpenEditor", ControlSection.RouteEditor, ControlModes.Both, "Open / close route editor", "<Keyboard>/f6", null);
    public static readonly Control GoToStart = new("GoToStart", ControlSection.RouteEditor, ControlModes.Both, "Teleport to route start", "<Keyboard>/f7", null);
    public static readonly Control ShowRoute = new("ShowRoute", ControlSection.RouteEditor, ControlModes.Both, "Show / hide route", "<Keyboard>/f9", null);
    public static readonly Control Paint = new("Paint", ControlSection.RouteEditor, ControlModes.RouteEditor, "Paint", "<Mouse>/leftButton", "<Gamepad>/rightTrigger");
    public static readonly Control CycleBrush = new("CycleBrush", ControlSection.RouteEditor, ControlModes.RouteEditor, "Cycle allow / deny / erase / line", "<Keyboard>/tab", "<Gamepad>/buttonWest");
    public static readonly Control AllowBrush = new("AllowBrush", ControlSection.RouteEditor, ControlModes.RouteEditor, "Allow brush", "<Keyboard>/1", null);
    public static readonly Control DenyBrush = new("DenyBrush", ControlSection.RouteEditor, ControlModes.RouteEditor, "Deny brush", "<Keyboard>/2", null);
    public static readonly Control EraseBrush = new("EraseBrush", ControlSection.RouteEditor, ControlModes.RouteEditor, "Erase brush", "<Keyboard>/3", null);
    public static readonly Control LineTool = new("LineTool", ControlSection.RouteEditor, ControlModes.RouteEditor, "Line tool", "<Keyboard>/4", null);
    public static readonly Control BrushBigger = new("BrushBigger", ControlSection.RouteEditor, ControlModes.RouteEditor, "Bigger brush", "<Keyboard>/rightBracket", "<Gamepad>/dpad/up");
    public static readonly Control BrushSmaller = new("BrushSmaller", ControlSection.RouteEditor, ControlModes.RouteEditor, "Smaller brush", "<Keyboard>/leftBracket", "<Gamepad>/dpad/down");
    public static readonly Control SetStart = new("SetStart", ControlSection.RouteEditor, ControlModes.RouteEditor, "Set start", "<Keyboard>/g", "<Gamepad>/buttonNorth");
    public static readonly Control SetFinish = new("SetFinish", ControlSection.RouteEditor, ControlModes.RouteEditor, "Set finish", "<Keyboard>/f", "<Gamepad>/buttonSouth");
    public static readonly Control ShowLine = new("ShowLine", ControlSection.RouteEditor, ControlModes.Both, "Show / hide route line", "<Keyboard>/h", null);

    // Only read while the line tool is in use, when the D-pad doesn't size the brush, so it shares
    // D-pad down with Smaller brush on purpose; left out of the clash checks for that reason.
    // With Ctrl on the keyboard, or held for a second on a gamepad, it clears the whole line.
    public static readonly Control DeletePoint = new("DeletePoint", ControlSection.RouteEditor, ControlModes.None,
        "Delete line point (with Ctrl: delete line)", "<Keyboard>/backspace", "<Gamepad>/dpad/down", "Delete line point (hold: delete line)");

    // Shares D-pad up with Bigger brush the same way. Tapped on a gamepad it changes the colour, held it starts a new line.
    public static readonly Control LineColor = new("LineColor", ControlSection.RouteEditor, ControlModes.None,
        "Next line colour", "<Keyboard>/c", "<Gamepad>/dpad/up", "Next line colour (hold: new line)");
    public static readonly Control NewLine = new("NewLine", ControlSection.RouteEditor, ControlModes.RouteEditor, "New route line", "<Keyboard>/n", null);

    // L3 is also half of L3+R3, so on a gamepad it only counts when let go without R3 pressed.
    public static readonly Control Rappel = new("Rappel", ControlSection.RouteEditor, ControlModes.RouteEditor,
        "Rappel / climb (dotted line piece)", "<Keyboard>/r", "<Gamepad>/leftStickPress");

    // Ctrl+key on the keyboard. The remap screen only rebinds the key, so these are left out of its
    // clash checks: S would always clash with Move back.
    public static readonly Control Undo = new("Undo", ControlSection.RouteEditor, ControlModes.None, "Undo (with Ctrl)", "<Keyboard>/z", "<Gamepad>/select", "Undo");
    public static readonly Control Save = new("Save", ControlSection.RouteEditor, ControlModes.None, "Save (with Ctrl)", "<Keyboard>/s", "<Gamepad>/dpad/right", "Save");

    // Only read in the level select and free-climb menus, where nothing else uses E or Y, so it's
    // left out of the clash checks: E is also Move up and Y also Set start.
    public static readonly Control EditRoute = new("EditRoute", ControlSection.RouteEditor, ControlModes.None,
        "Edit route (in route menus)", "<Keyboard>/e", "<Gamepad>/buttonNorth");

    /// <summary>Every control, in the order the remap screen lists them.</summary>
    public static readonly IReadOnlyList<Control> All = new[]
    {
        MoveForward, MoveBack, MoveLeft, MoveRight, MoveDown, MoveUp, Fast, Medium, Look,
        Teleport, PlayTest, LevelSelect, Exit, ToggleFreecam,
        OpenEditor, GoToStart, ShowRoute, Paint, CycleBrush, AllowBrush, DenyBrush, EraseBrush,
        LineTool, BrushBigger, BrushSmaller, SetStart, SetFinish, ShowLine, DeletePoint, LineColor, NewLine, Rappel, Undo, Save,
        EditRoute,
    };

    private static string _savePath;
    private static InputActionAsset _asset;
    private static bool _failed;

    /// <summary>Goes up whenever a binding changes, so text built from bindings can be cached.</summary>
    public static int Version { get; private set; }

    public static bool IsCreated => _asset != null;

    public static void Initialize(string savePath) => _savePath = savePath;

    /// <summary>
    /// Build the actions and load saved rebinds. Called lazily, since the Input System isn't up when
    /// the mod is first loaded.
    /// </summary>
    public static void EnsureCreated()
    {
        if (_asset != null || _failed)
        {
            return;
        }

        try
        {
            _asset = InputActionAsset.FromJson(BuildAssetJson());
            _asset.name = "CairnRouteMaker.Controls";
            // Created at runtime, so nothing in a scene holds on to it: keep scene changes from unloading it.
            _asset.hideFlags = HideFlags.DontUnloadUnusedAsset;
            var keyboard = _asset.FindActionMap(KeyboardMap, true);
            var gamepad = _asset.FindActionMap(GamepadMap, true);
            foreach (var control in All)
            {
                control.Keyboard = control.DefaultKeyboard == null ? null : keyboard.FindAction(control.Name, true);
                control.Gamepad = control.DefaultGamepad == null ? null : gamepad.FindAction(control.Name, true);
            }

            LoadRebinds();
            keyboard.Enable();
            gamepad.Enable();
            Version++;
        }
        catch (Exception e)
        {
            // It would fail the same way every frame, so give up: the mod's keys just won't respond.
            _failed = true;
            _asset = null;
            MelonLogger.Error($"Couldn't set up the mod's controls: {e}");
        }
    }

    /// <summary>A binding was changed or reset on the remap screen: save it.</summary>
    public static void OnBindingsChanged()
    {
        Version++;
        SaveRebinds();
    }

    public static string DisplayName(InputAction action)
    {
        if (action == null)
        {
            return "-";
        }

        var name = InputActionRebindingExtensions.GetBindingDisplayString(action, 0, default(InputBinding.DisplayStringOptions));
        return string.IsNullOrEmpty(name) ? "-" : name;
    }

    private static IEnumerable<(string Key, InputAction Action)> Actions()
    {
        foreach (var control in All)
        {
            if (control.Keyboard != null)
            {
                yield return ($"{KeyboardMap}/{control.Name}", control.Keyboard);
            }

            if (control.Gamepad != null)
            {
                yield return ($"{GamepadMap}/{control.Name}", control.Gamepad);
            }
        }
    }

    private static void LoadRebinds()
    {
        if (_savePath == null || !File.Exists(_savePath))
        {
            return;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_savePath));
            foreach (var (key, action) in Actions())
            {
                if (saved != null && saved.TryGetValue(key, out var overrides) && !string.IsNullOrEmpty(overrides))
                {
                    InputActionRebindingExtensions.LoadBindingOverridesFromJson(action, overrides, true);
                }
            }

            MelonLogger.Msg($"Loaded control rebinds from {_savePath}");
        }
        catch (Exception e)
        {
            MelonLogger.Error($"Couldn't load control rebinds from {_savePath}: {e.Message}");
        }
    }

    private static void SaveRebinds()
    {
        if (_savePath == null || _asset == null)
        {
            return;
        }

        try
        {
            // One entry per action, in the Input System's own override format, holding only rebinds.
            var saved = new SortedDictionary<string, string>();
            foreach (var (key, action) in Actions())
            {
                var overrides = InputActionRebindingExtensions.SaveBindingOverridesAsJson(action);
                if (!string.IsNullOrEmpty(overrides) && overrides.Contains("\"path\""))
                {
                    saved[key] = overrides;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_savePath)!);
            File.WriteAllText(_savePath, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            MelonLogger.Error($"Couldn't save control rebinds to {_savePath}: {e.Message}");
        }
    }

    /// <summary>
    /// The asset in the Input System's JSON format. Ids are derived from names so they stay the
    /// same between runs: saved rebinds find their binding by id.
    /// </summary>
    private static string BuildAssetJson()
    {
        object Map(string map, Func<Control, string> path)
        {
            var controls = All.Where(c => path(c) != null).ToList();
            return new
            {
                name = map,
                id = StableId(map),
                actions = controls.Select(c => new
                {
                    name = c.Name,
                    type = "Button",
                    id = StableId($"{map}/{c.Name}"),
                    expectedControlType = "Button",
                    processors = "",
                    interactions = "",
                    initialStateCheck = false,
                }),
                bindings = controls.Select(c => new
                {
                    name = "",
                    id = StableId($"{map}/{c.Name}/0"),
                    path = path(c),
                    interactions = "",
                    processors = "",
                    groups = "",
                    action = c.Name,
                    isComposite = false,
                    isPartOfComposite = false,
                }),
            };
        }

        return JsonSerializer.Serialize(new
        {
            name = "CairnRouteMaker",
            maps = new[] { Map(KeyboardMap, c => c.DefaultKeyboard), Map(GamepadMap, c => c.DefaultGamepad) },
            controlSchemes = Array.Empty<object>(),
        });
    }

    private static string StableId(string name) =>
        new Guid(MD5.HashData(Encoding.UTF8.GetBytes("CairnRouteMaker/" + name))).ToString();

    /// <summary>The id of a control's only binding, which the remap screen uses to find it.</summary>
    public static string BindingId(Control control, bool gamepad) =>
        StableId($"{(gamepad ? GamepadMap : KeyboardMap)}/{control.Name}/0");
}
