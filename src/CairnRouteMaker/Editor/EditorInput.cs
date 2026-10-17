using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnRouteMaker.Editor;

/// <summary>
/// Reads keyboard/mouse and gamepad directly (the game's own input is blocked while the editor
/// is open) and turns them into editor actions, so every action works from either device.
///
/// Gamepad layout:
///   left stick move, right stick look, LB/RB down/up, hold LT fast,
///   RT paint, X cycle brush, D-pad up/down brush size, Y set start,
///   View undo, D-pad right save, Menu play-test, D-pad left level select, B close.
///   L3+R3 opens and closes the editor from anywhere (handled by the mod, not here).
/// With a gamepad the brush aims at the centre of the screen.
/// </summary>
internal sealed class EditorInput
{
    private const float StickDeadzone = 0.15f;
    private const float FastMultiplier = 12f;
    private const float StickLookSpeed = 140f;
    private const float MouseLookSensitivity = 0.12f;

    /// <summary>True once the gamepad was the last device touched; switches the brush to screen-centre aiming.</summary>
    public bool UsingGamepad { get; private set; }

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
    public bool Undo { get; private set; }
    public bool Save { get; private set; }
    public bool PlayTest { get; private set; }
    public bool Close { get; private set; }
    public bool LevelSelect { get; private set; }

    // Menu navigation (level select).
    public int MenuMove { get; private set; }
    public bool MenuConfirm { get; private set; }
    public bool MenuBack { get; private set; }

    /// <summary>L3+R3 on a gamepad: the controller's way to open or close the editor.</summary>
    public static bool GamepadToggleEditor()
    {
        var pad = Gamepad.current;
        return pad != null
            && ((pad.leftStickButton.wasPressedThisFrame && pad.rightStickButton.isPressed)
                || (pad.rightStickButton.wasPressedThisFrame && pad.leftStickButton.isPressed));
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
        SetStart = Undo = Save = PlayTest = Close = LevelSelect = false;
        MenuMove = 0;
        MenuConfirm = MenuBack = false;

        if (keyboard != null && mouse != null)
        {
            var ctrl = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
            if (keyboard.anyKey.wasPressedThisFrame || mouse.delta.ReadValue().sqrMagnitude > 4f
                || mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame)
            {
                UsingGamepad = false;
            }

            if (!ctrl)
            {
                if (keyboard.wKey.isPressed) move.z += 1f;
                if (keyboard.sKey.isPressed) move.z -= 1f;
                if (keyboard.dKey.isPressed) move.x += 1f;
                if (keyboard.aKey.isPressed) move.x -= 1f;
                if (keyboard.eKey.isPressed) move.y += 1f;
                if (keyboard.qKey.isPressed) move.y -= 1f;
            }

            if (keyboard.leftShiftKey.isPressed) speed *= FastMultiplier;
            if (keyboard.leftAltKey.isPressed) speed *= 0.25f;

            if (mouse.rightButton.isPressed)
            {
                looking = true;
                look += mouse.delta.ReadValue() * MouseLookSensitivity;
            }

            paintHeld |= mouse.leftButton.isPressed && !looking;
            paintPressed |= mouse.leftButton.wasPressedThisFrame && !looking;
            paintReleased |= mouse.leftButton.wasReleasedThisFrame;

            var scroll = mouse.scroll.ReadValue().y;
            if (scroll != 0f) brushScale *= scroll > 0f ? 1.1f : 1f / 1.1f;
            if (keyboard.rightBracketKey.wasPressedThisFrame) brushScale *= 1.25f;
            if (keyboard.leftBracketKey.wasPressedThisFrame) brushScale /= 1.25f;

            CycleBrush |= keyboard.tabKey.wasPressedThisFrame;
            if (keyboard.digit1Key.wasPressedThisFrame) SelectBrush = 0;
            if (keyboard.digit2Key.wasPressedThisFrame) SelectBrush = 1;
            if (keyboard.digit3Key.wasPressedThisFrame) SelectBrush = 2;
            SetStart |= keyboard.gKey.wasPressedThisFrame;
            Undo |= ctrl && keyboard.zKey.wasPressedThisFrame;
            Save |= ctrl && keyboard.sKey.wasPressedThisFrame;
            PlayTest |= keyboard.pKey.wasPressedThisFrame;
            // F6 and F8 are handled by the mod itself, so they work with the editor closed too.
            Close |= keyboard.escapeKey.wasPressedThisFrame;
            LevelSelect |= keyboard.lKey.wasPressedThisFrame;

            if (keyboard.upArrowKey.wasPressedThisFrame || keyboard.wKey.wasPressedThisFrame) MenuMove -= 1;
            if (keyboard.downArrowKey.wasPressedThisFrame || keyboard.sKey.wasPressedThisFrame) MenuMove += 1;
            MenuConfirm |= keyboard.enterKey.wasPressedThisFrame || keyboard.spaceKey.wasPressedThisFrame;
            MenuBack |= keyboard.escapeKey.wasPressedThisFrame || keyboard.lKey.wasPressedThisFrame;
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
            if (pad.rightShoulder.isPressed) move.y += 1f;
            if (pad.leftShoulder.isPressed) move.y -= 1f;
            if (pad.leftTrigger.ReadValue() > 0.5f) speed *= FastMultiplier;
            look += rightStick * (StickLookSpeed * dt);

            var trigger = pad.rightTrigger;
            paintHeld |= trigger.isPressed;
            paintPressed |= trigger.wasPressedThisFrame;
            paintReleased |= trigger.wasReleasedThisFrame;

            if (pad.dpad.up.isPressed) brushScale *= Mathf.Pow(2f, dt);
            if (pad.dpad.down.isPressed) brushScale /= Mathf.Pow(2f, dt);

            CycleBrush |= pad.buttonWest.wasPressedThisFrame;
            SetStart |= pad.buttonNorth.wasPressedThisFrame;
            Undo |= pad.selectButton.wasPressedThisFrame;
            Save |= pad.dpad.right.wasPressedThisFrame;
            PlayTest |= pad.startButton.wasPressedThisFrame;
            Close |= pad.buttonEast.wasPressedThisFrame;
            LevelSelect |= pad.dpad.left.wasPressedThisFrame;

            if (pad.dpad.up.wasPressedThisFrame || pad.leftStick.up.wasPressedThisFrame) MenuMove -= 1;
            if (pad.dpad.down.wasPressedThisFrame || pad.leftStick.down.wasPressedThisFrame) MenuMove += 1;
            MenuConfirm |= pad.buttonSouth.wasPressedThisFrame;
            MenuBack |= pad.buttonEast.wasPressedThisFrame || pad.dpad.left.wasPressedThisFrame;
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

    private static Vector2 Deadzone(Vector2 stick) => stick.magnitude < StickDeadzone ? Vector2.zero : stick;

    private static bool AnyButton(Gamepad pad) =>
        pad.buttonSouth.isPressed || pad.buttonEast.isPressed || pad.buttonWest.isPressed || pad.buttonNorth.isPressed
        || pad.leftShoulder.isPressed || pad.rightShoulder.isPressed || pad.leftTrigger.isPressed || pad.rightTrigger.isPressed
        || pad.dpad.up.isPressed || pad.dpad.down.isPressed || pad.dpad.left.isPressed || pad.dpad.right.isPressed
        || pad.startButton.isPressed || pad.selectButton.isPressed;
}
