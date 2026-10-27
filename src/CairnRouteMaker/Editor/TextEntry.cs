using System;
using System.Collections.Generic;
using System.Text;
using CairnRouteMaker.Controls;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnRouteMaker.Editor;

internal enum TextEntryResult
{
    Editing,
    Done,
    Cancelled,
}

/// <summary>
/// A line of text being typed in the level select, e.g. a route's new name. A physical keyboard
/// types into it (through the Input System's text events, so the keyboard layout and Steam's own
/// on-screen keyboard both work); with a gamepad, <see cref="LevelSelectView"/> shows an on-screen
/// keyboard of <see cref="Layout"/> that the D-pad moves around.
///
/// The starting text begins selected, as when renaming a file, so typing replaces it.
///
/// Keyboard: type, Backspace/Delete (with Ctrl: a word), arrows/Home/End move the caret,
/// Enter done, Escape cancel.
/// Gamepad: D-pad/left stick choose a key, A press it, X backspace, Y space, LT shift,
/// LB/RB move the caret, Menu done, the Exit button (B) cancel.
/// </summary>
internal sealed class TextEntry
{
    private const float RepeatDelay = 0.4f;
    private const float RepeatInterval = 0.06f;

    public enum KeyKind
    {
        Char,
        Shift,
        Space,
        Backspace,
        Done,
    }

    /// <summary>A key of the on-screen keyboard; it covers <see cref="Span"/> columns from <see cref="Column"/>.</summary>
    public sealed class Key
    {
        public Key(KeyKind kind, char character, int column, int span = 1)
        {
            Kind = kind;
            Character = character;
            Column = column;
            Span = span;
        }

        public KeyKind Kind { get; }
        public char Character { get; }
        public int Column { get; }
        public int Span { get; }

        public string Label(bool shift) => Kind switch
        {
            KeyKind.Char => (shift ? char.ToUpperInvariant(Character) : Character).ToString(),
            KeyKind.Shift => "Shift",
            KeyKind.Space => "Space",
            KeyKind.Backspace => "Delete",
            _ => "Done",
        };
    }

    public const int Columns = 10;

    /// <summary>The on-screen keyboard, row by row: lower case, with Shift for capitals.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<Key>> Layout = new[]
    {
        CharRow("1234567890"),
        CharRow("qwertyuiop"),
        CharRow("asdfghjkl'"),
        CharRow("zxcvbnm,.-"),
        CharRow("!?&#+/():_"),
        new[]
        {
            new Key(KeyKind.Shift, '\0', 0, 2),
            new Key(KeyKind.Space, ' ', 2, 4),
            new Key(KeyKind.Backspace, '\0', 6, 2),
            new Key(KeyKind.Done, '\0', 8, 2),
        },
    };

    // Characters typed on a physical keyboard since the last update. There's one subscription,
    // kept for the life of the game; it only fills this buffer.
    private static readonly StringBuilder Typed = new();
    private static Il2CppSystem.Action<char> _onTextInput;
    private static IntPtr _listeningTo;
    private static bool _listenFailed;

    private readonly StringBuilder _text;
    private readonly int _openedFrame;
    private readonly Repeater _backspace = new();
    private readonly Repeater _delete = new();
    private readonly Repeater _left = new();
    private readonly Repeater _right = new();
    private readonly Repeater _padLeft = new();
    private readonly Repeater _padRight = new();
    private readonly Repeater _padUp = new();
    private readonly Repeater _padDown = new();
    private readonly Repeater _padBackspace = new();
    private readonly Repeater _caretLeft = new();
    private readonly Repeater _caretRight = new();

    public TextEntry(string text, int maxLength)
    {
        text ??= "";
        _text = new StringBuilder(text.Length > maxLength ? text[..maxLength] : text);
        MaxLength = maxLength;
        Caret = _text.Length;
        AllSelected = _text.Length > 0;
        Row = 1;
        _openedFrame = Time.frameCount;
        LastChange = Time.unscaledTime;
        Listen();
        Typed.Clear();
    }

    public string Text => _text.ToString();
    public int MaxLength { get; }
    public int Caret { get; private set; }

    /// <summary>The whole text is selected: typing replaces it, Backspace clears it.</summary>
    public bool AllSelected { get; private set; }

    /// <summary>On-screen Shift: the next letter is a capital.</summary>
    public bool Shift { get; private set; }

    /// <summary>The on-screen key that's highlighted.</summary>
    public int Row { get; private set; }
    public int Column { get; private set; }
    public Key Highlighted => KeyAt(Row, Column);

    /// <summary>When the text or caret last changed, so the caret can stay solid while typing.</summary>
    public float LastChange { get; private set; }

    /// <summary>Shown under the text, e.g. why it can't be used; cleared by the next edit.</summary>
    public string Error { get; set; }

    public TextEntryResult Update()
    {
        Listen();
        // The press that opened it mustn't also count.
        if (Time.frameCount <= _openedFrame)
        {
            Typed.Clear();
            return TextEntryResult.Editing;
        }

        var now = Time.unscaledTime;
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            foreach (var c in Typed.ToString())
            {
                if (!char.IsControl(c))
                {
                    Insert(c);
                }
            }

            Typed.Clear();
            var ctrl = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) return TextEntryResult.Done;
            if (keyboard.escapeKey.wasPressedThisFrame) return TextEntryResult.Cancelled;
            if (_backspace.Fire(keyboard.backspaceKey.isPressed, now)) DeleteBack(ctrl);
            if (_delete.Fire(keyboard.deleteKey.isPressed, now)) DeleteForward(ctrl);
            if (_left.Fire(keyboard.leftArrowKey.isPressed, now)) MoveCaret(-1, ctrl);
            if (_right.Fire(keyboard.rightArrowKey.isPressed, now)) MoveCaret(1, ctrl);
            if (keyboard.homeKey.wasPressedThisFrame) SetCaret(0);
            if (keyboard.endKey.wasPressedThisFrame) SetCaret(_text.Length);
        }

        var pad = Gamepad.current;
        if (pad != null)
        {
            if (_padLeft.Fire(pad.dpad.left.isPressed || pad.leftStick.left.isPressed, now)) MoveKey(0, -1);
            if (_padRight.Fire(pad.dpad.right.isPressed || pad.leftStick.right.isPressed, now)) MoveKey(0, 1);
            if (_padUp.Fire(pad.dpad.up.isPressed || pad.leftStick.up.isPressed, now)) MoveKey(-1, 0);
            if (_padDown.Fire(pad.dpad.down.isPressed || pad.leftStick.down.isPressed, now)) MoveKey(1, 0);
            if (_caretLeft.Fire(pad.leftShoulder.isPressed, now)) MoveCaret(-1, false);
            if (_caretRight.Fire(pad.rightShoulder.isPressed, now)) MoveCaret(1, false);
            if (_padBackspace.Fire(pad.buttonWest.isPressed, now)) DeleteBack(false);
            if (pad.buttonNorth.wasPressedThisFrame) Insert(' ');
            if (pad.leftTrigger.wasPressedThisFrame) Shift = !Shift;
            if (pad.startButton.wasPressedThisFrame) return TextEntryResult.Done;
            if (ModControls.Exit.PadPressed) return TextEntryResult.Cancelled;
            if (pad.buttonSouth.wasPressedThisFrame && Press(Highlighted)) return TextEntryResult.Done;
        }

        return TextEntryResult.Editing;
    }

    /// <summary>Press an on-screen key; true for Done.</summary>
    private bool Press(Key key)
    {
        switch (key.Kind)
        {
            case KeyKind.Char:
                Insert(Shift ? char.ToUpperInvariant(key.Character) : key.Character);
                Shift = false;
                return false;
            case KeyKind.Shift:
                Shift = !Shift;
                return false;
            case KeyKind.Space:
                Insert(' ');
                return false;
            case KeyKind.Backspace:
                DeleteBack(false);
                return false;
            default:
                return true;
        }
    }

    private void Insert(char c)
    {
        if (AllSelected)
        {
            _text.Clear();
            Caret = 0;
            AllSelected = false;
        }

        if (_text.Length >= MaxLength)
        {
            return;
        }

        _text.Insert(Caret, c);
        Caret++;
        Changed();
    }

    private void DeleteBack(bool word)
    {
        if (ClearSelection())
        {
            return;
        }

        var from = word ? WordStart(Caret) : Math.Max(0, Caret - 1);
        _text.Remove(from, Caret - from);
        Caret = from;
        Changed();
    }

    private void DeleteForward(bool word)
    {
        if (ClearSelection())
        {
            return;
        }

        var to = word ? WordEnd(Caret) : Math.Min(_text.Length, Caret + 1);
        _text.Remove(Caret, to - Caret);
        Changed();
    }

    /// <summary>With everything selected, deleting clears it all.</summary>
    private bool ClearSelection()
    {
        if (!AllSelected)
        {
            return false;
        }

        _text.Clear();
        Caret = 0;
        AllSelected = false;
        Changed();
        return true;
    }

    private void MoveCaret(int direction, bool word)
    {
        if (AllSelected)
        {
            // Like a text box: the caret lands at that end of the selection.
            AllSelected = false;
            SetCaret(direction < 0 ? 0 : _text.Length);
            return;
        }

        SetCaret(!word ? Caret + direction : direction < 0 ? WordStart(Caret) : WordEnd(Caret));
    }

    private void SetCaret(int caret)
    {
        AllSelected = false;
        Caret = Math.Clamp(caret, 0, _text.Length);
        Changed();
    }

    private int WordStart(int from)
    {
        var i = from;
        while (i > 0 && _text[i - 1] == ' ') i--;
        while (i > 0 && _text[i - 1] != ' ') i--;
        return i;
    }

    private int WordEnd(int from)
    {
        var i = from;
        while (i < _text.Length && _text[i] == ' ') i++;
        while (i < _text.Length && _text[i] != ' ') i++;
        return i;
    }

    private void Changed()
    {
        LastChange = Time.unscaledTime;
        Error = null;
    }

    /// <summary>Move the highlight; rows wrap around, and between rows it keeps to the same column.</summary>
    private void MoveKey(int rows, int columns)
    {
        if (rows != 0)
        {
            Row = (Row + rows + Layout.Count) % Layout.Count;
            return;
        }

        var row = Layout[Row];
        var index = IndexOf(row, Column);
        var next = row[(index + columns + row.Count) % row.Count];
        Column = next.Column;
    }

    private static Key KeyAt(int row, int column) => Layout[row][IndexOf(Layout[row], column)];

    private static int IndexOf(IReadOnlyList<Key> row, int column)
    {
        for (var i = 0; i < row.Count; i++)
        {
            if (column < row[i].Column + row[i].Span)
            {
                return i;
            }
        }

        return row.Count - 1;
    }

    private static Key[] CharRow(string characters)
    {
        var keys = new Key[characters.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = new Key(KeyKind.Char, characters[i], i);
        }

        return keys;
    }

    /// <summary>Subscribe to the current keyboard's text events, again if the keyboard changed.</summary>
    private static void Listen()
    {
        var keyboard = Keyboard.current;
        if (_listenFailed || keyboard == null || keyboard.Pointer == _listeningTo)
        {
            return;
        }

        try
        {
            // Capped: nothing empties it while no text is being typed.
            _onTextInput ??= new Action<char>(c =>
            {
                if (Typed.Length < 256)
                {
                    Typed.Append(c);
                }
            });
            keyboard.add_onTextInput(_onTextInput);
            _listeningTo = keyboard.Pointer;
        }
        catch (Exception e)
        {
            _listenFailed = true;
            MelonLogger.Error($"Couldn't read typed text from the keyboard: {e}");
        }
    }

    /// <summary>A held key or button: fires when pressed, then repeatedly after a moment.</summary>
    private sealed class Repeater
    {
        private float _next = -1f;

        public bool Fire(bool down, float now)
        {
            if (!down)
            {
                _next = -1f;
                return false;
            }

            if (_next < 0f)
            {
                _next = now + RepeatDelay;
                return true;
            }

            if (now < _next)
            {
                return false;
            }

            _next = now + RepeatInterval;
            return true;
        }
    }
}
