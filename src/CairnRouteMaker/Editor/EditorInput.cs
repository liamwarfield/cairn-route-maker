using CairnRouteMaker.Controls;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnRouteMaker.Editor;

/// <summary>
/// Reads keyboard/mouse and gamepad (the game's own input is blocked while the editor is open) and
/// turns them into editor actions, so every action works from either device. Buttons come from
/// <see cref="ModControls"/>, which the game's remap screen can rebind; sticks, mouse movement, the
/// scroll wheel, L3+R3 and menu navigation are read from the devices directly.
///
/// Default gamepad layout:
///   left stick move, right stick look, LB/RB down/up, hold LT fast,
///   RT paint, X cycle brush, D-pad up/down brush size, Y set start, A set finish,
///   View undo, D-pad right save, Menu play-test, D-pad left level select, B close.
///   With the line tool (the fourth brush): RT adds or drags points, D-pad down deletes the
///   selected point and holding it clears the line.
///   In freecam: A moves Ava to the marker; editing controls do nothing.
///   R3 on its own switches between editing and freecam.
///   L3+R3 opens and closes the editor from anywhere (handled by the mod, not here).
/// With a gamepad the brush aims at the centre of the screen.
/// </summary>
internal sealed class EditorInput
{
    private const float StickDeadzone = 0.15f;
    /// <summary>Shift / LT: 72 m/s.</summary>
    private const float FastMultiplier = 12f;

    /// <summary>Alt: 24 m/s, a third of fast.</summary>
    private const float AltMultiplier = 4f;
    private const float StickLookSpeed = 140f;
    private const float MouseLookSensitivity = 0.12f;

    /// <summary>Holding the gamepad's delete-point button this long with the line tool clears the line.</summary>
    private const float ClearHoldSeconds = 1f;

    // The gamepad's freecam button (R3) switches modes on release, so pressing it as half of
    // L3+R3 doesn't also switch.
    private bool _toggleDown;
    private bool _toggleCombo;
    private float _clearHeld;
    private bool _clearFired;
    private float _newLineHeld = -1f;

    /// <summary>True once the gamepad was the last device touched; switches the brush to screen-centre aiming.</summary>
    public bool UsingGamepad { get; private set; }

    /// <summary>Set by the editor while the line tool is in use: the D-pad edits points instead of sizing the brush.</summary>
    public bool LineTool { get; set; }

    // Continuous controls, read each frame.
    public Vector3 Move { get; private set; }
    public float MoveSpeedScale { get; private set; } = 1f;
    public Vector2 LookDegrees { get; private set; }
    public bool Looking { get; private set; }
    public bool PaintHeld { get; private set; }
    public bool PaintPressed { get; private set; }
    public bool PaintReleased { get; private set; }

    /// <summary>Brush size factor for this frame (1 = unchanged).</summary>
    public float BrushScale { get; private set; } = 1f;
    public Vector2 PointerPosition { get; private set; }

    // One-shot actions.
    public bool CycleBrush { get; private set; }
    public int? SelectBrush { get; private set; }
    public bool SetStart { get; private set; }
    public bool SetFinish { get; private set; }
    public bool Undo { get; private set; }
    public bool Save { get; private set; }
    public bool PlayTest { get; private set; }
    public bool Close { get; private set; }
    public bool LevelSelect { get; private set; }
    public bool Teleport { get; private set; }
    public bool ToggleFreecam { get; private set; }
    public bool DeletePoint { get; private set; }
    public bool ClearLine { get; private set; }
    public bool ToggleLine { get; private set; }
    public bool CycleLineColor { get; private set; }
    public bool NewLine { get; private set; }

    // Menu navigation (level select).
    public int MenuMove { get; private set; }
    public bool MenuConfirm { get; private set; }
    public bool MenuBack { get; private set; }
    public bool MenuClick { get; private set; }

    /// <summary>L3+R3 on a gamepad: the controller's way to open or close the editor.</summary>
    public static bool GamepadToggleEditor()
    {
        var pad = Gamepad.current;
        return pad != null
            && ((pad.leftStickButton.wasPressedThisFrame && pad.rightStickButton.isPressed)
                || (pad.rightStickButton.wasPressedThisFrame && pad.leftStickButton.isPressed));
    }

    /// <summary>Forget held buttons from before the editor opened.</summary>
    public void Reset()
    {
        _toggleDown = false;
        _toggleCombo = false;
        _clearHeld = 0f;
        _clearFired = false;
        _newLineHeld = -1f;
    }

    public void Read(float dt)
    {
        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        var pad = Gamepad.current;

        var move = Vector3.zero;
        var look = Vector2.zero;
        var speed = 1f;
        var brushScale = 1f;
        var paintHeld = false;
        var paintPressed = false;
        var paintReleased = false;
        var looking = false;

        CycleBrush = false;
        SelectBrush = null;
        SetStart = SetFinish = Undo = Save = PlayTest = Close = LevelSelect = Teleport = ToggleFreecam = false;
        DeletePoint = ClearLine = ToggleLine = CycleLineColor = NewLine = false;
        MenuMove = 0;
        MenuConfirm = MenuBack = MenuClick = false;

        if (keyboard != null && mouse != null)
        {
            var ctrl = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
            if (keyboard.anyKey.wasPressedThisFrame || mouse.delta.ReadValue().sqrMagnitude > 4f
                || mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame)
            {
                UsingGamepad = false;
            }

            // Held Ctrl is for Ctrl+S / Ctrl+Z, so it mustn't move the camera too.
            if (!ctrl)
            {
                if (ModControls.MoveForward.KeyHeld) move.z += 1f;
                if (ModControls.MoveBack.KeyHeld) move.z -= 1f;
                if (ModControls.MoveRight.KeyHeld) move.x += 1f;
                if (ModControls.MoveLeft.KeyHeld) move.x -= 1f;
                if (ModControls.MoveUp.KeyHeld) move.y += 1f;
                if (ModControls.MoveDown.KeyHeld) move.y -= 1f;
            }

            if (ModControls.Fast.KeyHeld) speed *= FastMultiplier;
            if (ModControls.Medium.KeyHeld) speed *= AltMultiplier;

            if (ModControls.Look.KeyHeld)
            {
                looking = true;
                look += mouse.delta.ReadValue() * MouseLookSensitivity;
            }

            paintHeld |= ModControls.Paint.KeyHeld && !looking;
            paintPressed |= ModControls.Paint.KeyPressed && !looking;
            paintReleased |= ModControls.Paint.KeyReleased;
            Teleport |= ModControls.Teleport.KeyPressed && !looking;

            var scroll = mouse.scroll.ReadValue().y;
            if (scroll != 0f) brushScale *= scroll > 0f ? 1.1f : 1f / 1.1f;
            if (ModControls.BrushBigger.KeyPressed) brushScale *= 1.25f;
            if (ModControls.BrushSmaller.KeyPressed) brushScale /= 1.25f;

            CycleBrush |= ModControls.CycleBrush.KeyPressed;
            if (ModControls.AllowBrush.KeyPressed) SelectBrush = 0;
            if (ModControls.DenyBrush.KeyPressed) SelectBrush = 1;
            if (ModControls.EraseBrush.KeyPressed) SelectBrush = 2;
            if (ModControls.LineTool.KeyPressed) SelectBrush = 3;
            SetStart |= ModControls.SetStart.KeyPressed;
            SetFinish |= ModControls.SetFinish.KeyPressed;
            Undo |= ctrl && ModControls.Undo.KeyPressed;
            Save |= ctrl && ModControls.Save.KeyPressed;
            PlayTest |= ModControls.PlayTest.KeyPressed;
            // The keyboard's open-editor and freecam keys are handled by the mod itself, so they work with the editor closed too.
            Close |= ModControls.Exit.KeyPressed;
            LevelSelect |= ModControls.LevelSelect.KeyPressed;
            DeletePoint |= !ctrl && ModControls.DeletePoint.KeyPressed;
            ClearLine |= ctrl && ModControls.DeletePoint.KeyPressed;
            ToggleLine |= ModControls.ShowLine.KeyPressed;
            CycleLineColor |= !ctrl && ModControls.LineColor.KeyPressed;
            NewLine |= !ctrl && ModControls.NewLine.KeyPressed;

            if (keyboard.upArrowKey.wasPressedThisFrame || ModControls.MoveForward.KeyPressed) MenuMove -= 1;
            if (keyboard.downArrowKey.wasPressedThisFrame || ModControls.MoveBack.KeyPressed) MenuMove += 1;
            MenuConfirm |= keyboard.enterKey.wasPressedThisFrame || keyboard.spaceKey.wasPressedThisFrame;
            MenuBack |= ModControls.Exit.KeyPressed || ModControls.LevelSelect.KeyPressed;
            MenuClick |= mouse.leftButton.wasPressedThisFrame;
            if (scroll != 0f) MenuMove += scroll > 0f ? -1 : 1;
            PointerPosition = mouse.position.ReadValue();
        }

        if (pad != null)
        {
            var leftStick = Deadzone(pad.leftStick.ReadValue());
            var rightStick = Deadzone(pad.rightStick.ReadValue());
            if (leftStick != Vector2.zero || rightStick != Vector2.zero || AnyButton(pad))
            {
                UsingGamepad = true;
            }

            move += new Vector3(leftStick.x, 0f, leftStick.y);
            if (ModControls.MoveUp.PadHeld) move.y += 1f;
            if (ModControls.MoveDown.PadHeld) move.y -= 1f;
            if (ModControls.Fast.PadHeld) speed *= FastMultiplier;
            look += rightStick * (StickLookSpeed * dt);

            paintHeld |= ModControls.Paint.PadHeld;
            paintPressed |= ModControls.Paint.PadPressed;
            paintReleased |= ModControls.Paint.PadReleased;

            if (LineTool)
            {
                ReadLinePad(dt);
            }
            else
            {
                // Held on a gamepad, the brush keeps growing or shrinking.
                if (ModControls.BrushBigger.PadHeld) brushScale *= Mathf.Pow(2f, dt);
                if (ModControls.BrushSmaller.PadHeld) brushScale /= Mathf.Pow(2f, dt);
            }

            CycleBrush |= ModControls.CycleBrush.PadPressed;
            SetStart |= ModControls.SetStart.PadPressed;
            SetFinish |= ModControls.SetFinish.PadPressed;
            Teleport |= ModControls.Teleport.PadPressed;
            Undo |= ModControls.Undo.PadPressed;
            Save |= ModControls.Save.PadPressed;
            PlayTest |= ModControls.PlayTest.PadPressed;
            Close |= ModControls.Exit.PadPressed;
            LevelSelect |= ModControls.LevelSelect.PadPressed;

            if (ModControls.ToggleFreecam.PadPressed)
            {
                _toggleDown = true;
                _toggleCombo = false;
            }

            if (_toggleDown && pad.leftStickButton.isPressed && pad.rightStickButton.isPressed)
            {
                _toggleCombo = true;
            }

            if (ModControls.ToggleFreecam.PadReleased)
            {
                ToggleFreecam |= _toggleDown && !_toggleCombo;
                _toggleDown = false;
            }

            if (pad.dpad.up.wasPressedThisFrame || pad.leftStick.up.wasPressedThisFrame) MenuMove -= 1;
            if (pad.dpad.down.wasPressedThisFrame || pad.leftStick.down.wasPressedThisFrame) MenuMove += 1;
            MenuConfirm |= pad.buttonSouth.wasPressedThisFrame;
            MenuBack |= ModControls.Exit.PadPressed || ModControls.LevelSelect.PadPressed;
        }

        Move = move;
        MoveSpeedScale = speed;
        LookDegrees = look;
        Looking = looking;
        PaintHeld = paintHeld;
        PaintPressed = paintPressed;
        PaintReleased = paintReleased;
        BrushScale = brushScale;
    }

    /// <summary>The delete-point button deletes a point; held on, it clears the whole line once.</summary>
    private void ReadLinePad(float dt)
    {
        ReadLineColorPad(dt);
        DeletePoint |= ModControls.DeletePoint.PadPressed;
        if (!ModControls.DeletePoint.PadHeld)
        {
            _clearHeld = 0f;
            _clearFired = false;
            return;
        }

        _clearHeld += dt;
        if (_clearHeld >= ClearHoldSeconds && !_clearFired)
        {
            ClearLine = true;
            _clearFired = true;
        }
    }

    /// <summary>Tapped, the colour button changes the line's colour on release; held for a second, it starts a new line instead.</summary>
    private void ReadLineColorPad(float dt)
    {
        if (ModControls.LineColor.PadPressed)
        {
            _newLineHeld = 0f;
        }

        if (_newLineHeld < 0f)
        {
            return;
        }

        if (ModControls.LineColor.PadHeld)
        {
            _newLineHeld += dt;
            if (_newLineHeld >= ClearHoldSeconds)
            {
                NewLine = true;
                _newLineHeld = -1f;
            }
        }
        else
        {
            CycleLineColor = true;
            _newLineHeld = -1f;
        }
    }

    private static Vector2 Deadzone(Vector2 stick) => stick.magnitude < StickDeadzone ? Vector2.zero : stick;

    private static bool AnyButton(Gamepad pad) =>
        pad.buttonSouth.isPressed || pad.buttonEast.isPressed || pad.buttonWest.isPressed || pad.buttonNorth.isPressed
        || pad.leftShoulder.isPressed || pad.rightShoulder.isPressed || pad.leftTrigger.isPressed || pad.rightTrigger.isPressed
        || pad.dpad.up.isPressed || pad.dpad.down.isPressed || pad.dpad.left.isPressed || pad.dpad.right.isPressed
        || pad.startButton.isPressed || pad.selectButton.isPressed
        || pad.leftStickButton.isPressed || pad.rightStickButton.isPressed;
}
