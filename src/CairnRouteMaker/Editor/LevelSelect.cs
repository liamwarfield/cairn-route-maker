using System;
using System.Collections.Generic;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using Il2Cpp;

namespace CairnRouteMaker.Editor;

/// <summary>
/// A list of places to jump to for testing: saved routes (travel there and stand on the start),
/// zones of the current world, and the other worlds. Built fresh each time it opens.
/// </summary>
internal sealed class LevelSelect
{
    public sealed class Entry
    {
        public Entry(string label, bool isHeader, bool enabled, Action choose)
        {
            Label = label;
            IsHeader = isHeader;
            Enabled = enabled;
            Choose = choose;
        }

        public string Label { get; }
        public bool IsHeader { get; }
        public bool Enabled { get; }
        public Action Choose { get; }
    }

    private readonly List<Entry> _entries = new();

    public bool IsOpen { get; private set; }
    public IReadOnlyList<Entry> Entries => _entries;
    public int Selected { get; private set; }

    /// <summary>Set by the HUD when an entry is clicked with the mouse; handled on the next update.</summary>
    public int? ClickedIndex { get; set; }

    public void Open(RouteStorage storage, Traveler traveler, Action<string> report)
    {
        _entries.Clear();

        _entries.Add(new Entry("Saved routes", true, false, null));
        foreach (var route in storage.LoadAll())
        {
            var parts = route.ZoneKey.Split('/', 2);
            if (parts.Length != 2 || !Enum.TryParse<StreamingTweakables.World>(parts[0], out var world))
            {
                continue;
            }

            var start = route.Start;
            var label = $"{route.ZoneKey}{(start == null ? "  (no start set)" : "")}";
            _entries.Add(new Entry(label, false, true, () => traveler.Start(
                world,
                parts[1],
                start == null ? null : RouteOverlay.ToUnity(start.Position),
                start?.YawDegrees ?? 0f,
                report)));
        }

        if (GameApi.CurrentWorld is { } currentWorld)
        {
            _entries.Add(new Entry($"Zones in {currentWorld}", true, false, null));
            foreach (var zone in GameApi.ZonesInCurrentWorld())
            {
                var note = zone.IsCurrent ? "  (you are here)" : zone.IsDiscovered ? "" : "  (not discovered yet)";
                var name = zone.Name;
                _entries.Add(new Entry(name + note, false, !zone.IsCurrent, () => traveler.Start(currentWorld, name, null, 0f, report)));
            }
        }

        _entries.Add(new Entry("Worlds", true, false, null));
        foreach (var world in GameApi.Worlds())
        {
            // Locked worlds are allowed on purpose: this is a mapping tool, not a playthrough.
            var note = world.IsCurrent ? "  (you are here)" : world.IsUnlocked ? "" : "  (not unlocked in this save)";
            var id = world.Id;
            _entries.Add(new Entry(id + note, false, !world.IsCurrent, () => traveler.Start(id, null, null, 0f, report)));
        }

        Selected = -1;
        ClickedIndex = null;
        Move(1);
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    /// <summary>Move the highlight to the next choosable entry in a direction, wrapping around.</summary>
    public void Move(int direction)
    {
        if (direction == 0 || _entries.Count == 0)
        {
            return;
        }

        var step = Math.Sign(direction);
        var index = Selected;
        for (var tries = 0; tries < _entries.Count; tries++)
        {
            index = (index + step + _entries.Count) % _entries.Count;
            if (_entries[index].Enabled)
            {
                Selected = index;
                return;
            }
        }
    }

    public void Select(int index)
    {
        if (index >= 0 && index < _entries.Count && _entries[index].Enabled)
        {
            Selected = index;
        }
    }

    /// <summary>Close the menu and start the highlighted trip.</summary>
    public void Confirm()
    {
        if (Selected < 0 || Selected >= _entries.Count || !_entries[Selected].Enabled)
        {
            return;
        }

        var choice = _entries[Selected];
        Close();
        choice.Choose();
    }
}
