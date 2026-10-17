using System;
using System.Collections.Generic;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using Il2Cpp;

namespace CairnRouteMaker.Editor;

/// <summary>
/// The editor's menu. The first page jumps around for testing: saved routes (travel there and
/// stand on the start), zones of the current world, and the other worlds. From it, "Manage saved
/// routes" lists every route with travel and delete actions. Pages are rebuilt each time they show.
/// </summary>
internal sealed class LevelSelect
{
    public sealed class Entry
    {
        public Entry(string label, bool isHeader, bool enabled, Action choose, bool leavesEditor = false)
        {
            Label = label;
            IsHeader = isHeader;
            Enabled = enabled;
            Choose = choose;
            LeavesEditor = leavesEditor;
        }

        public string Label { get; }
        public bool IsHeader { get; }
        public bool Enabled { get; }
        public Action Choose { get; }

        /// <summary>Travel entries: the editor closes (handing back input and camera) before they run.</summary>
        public bool LeavesEditor { get; }
    }

    private readonly List<Entry> _entries = new();
    private readonly Stack<Action> _back = new();
    private RouteStorage _storage;
    private Traveler _traveler;
    private Action<string> _report;
    private Action<string> _onDeleted;

    public bool IsOpen { get; private set; }
    public string Title { get; private set; }
    public IReadOnlyList<Entry> Entries => _entries;
    public int Selected { get; private set; }

    /// <summary>Set by the HUD when an entry is clicked with the mouse; handled on the next update.</summary>
    public int? ClickedIndex { get; set; }

    /// <summary>The highlighted entry, if it can be chosen.</summary>
    public Entry Current => Selected >= 0 && Selected < _entries.Count && _entries[Selected].Enabled ? _entries[Selected] : null;

    /// <param name="onDeleted">Called with the zone key after a route file is deleted.</param>
    public void Open(RouteStorage storage, Traveler traveler, Action<string> report, Action<string> onDeleted)
    {
        _storage = storage;
        _traveler = traveler;
        _report = report;
        _onDeleted = onDeleted;
        _back.Clear();
        ShowLevels();
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    /// <summary>Back out one page; from the first page, close the menu.</summary>
    public void Back()
    {
        if (_back.Count == 0)
        {
            Close();
            return;
        }

        _back.Pop()();
    }

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

    private void ShowLevels()
    {
        Begin("LEVEL SELECT");
        Add(new Entry("Manage saved routes...", false, true, () => Push(ShowLevels, ShowRoutes)));

        Add(new Entry("Saved routes", true, false, null));
        foreach (var route in _storage.LoadAll())
        {
            var label = $"{route.ZoneKey}{(route.Start == null ? "  (no start set)" : "")}";
            Add(new Entry(label, false, TryParseZone(route.ZoneKey, out _, out _), () => TravelTo(route), leavesEditor: true));
        }

        if (GameApi.CurrentWorld is { } currentWorld)
        {
            Add(new Entry($"Zones in {currentWorld}", true, false, null));
            foreach (var zone in GameApi.ZonesInCurrentWorld())
            {
                var note = zone.IsCurrent ? "  (you are here)" : zone.IsDiscovered ? "" : "  (not discovered yet)";
                var name = zone.Name;
                Add(new Entry(name + note, false, !zone.IsCurrent, () => _traveler.Start(currentWorld, name, null, 0f, _report), leavesEditor: true));
            }
        }

        Add(new Entry("Worlds", true, false, null));
        foreach (var world in GameApi.Worlds())
        {
            // Locked worlds are allowed on purpose: this is a mapping tool, not a playthrough.
            var note = world.IsCurrent ? "  (you are here)" : world.IsUnlocked ? "" : "  (not unlocked in this save)";
            var id = world.Id;
            Add(new Entry(id + note, false, !world.IsCurrent, () => _traveler.Start(id, null, null, 0f, _report), leavesEditor: true));
        }

        End();
    }

    private void ShowRoutes()
    {
        Begin("SAVED ROUTES");
        var routes = _storage.LoadAll();
        if (routes.Count == 0)
        {
            Add(new Entry("No saved routes yet.", false, false, null));
        }

        foreach (var route in routes)
        {
            var strokes = route.Strokes.Count;
            var label = $"{route.ZoneKey}   {strokes} stroke{(strokes == 1 ? "" : "s")}, {(route.Start == null ? "no start" : "start set")}";
            Add(new Entry(label, false, true, () => Push(ShowRoutes, () => ShowRouteActions(route))));
        }

        End();
    }

    private void ShowRouteActions(Route route)
    {
        Begin(route.ZoneKey);
        var canTravel = TryParseZone(route.ZoneKey, out _, out _);
        Add(new Entry(route.Start == null ? "Travel to this zone" : "Travel to the start", false, canTravel, () => TravelTo(route), leavesEditor: true));
        Add(new Entry("Delete this route...", false, true, () => Push(() => ShowRouteActions(route), () => ShowConfirmDelete(route))));
        End();
    }

    private void ShowConfirmDelete(Route route)
    {
        Begin($"Delete the route for {route.ZoneKey}?");
        Add(new Entry("It's moved to UserData/CairnRouteMaker/Backups, so it can be restored.", true, false, null));
        Add(new Entry("No, keep it", false, true, Back));
        Add(new Entry("Yes, delete it", false, true, () =>
        {
            try
            {
                _storage.Delete(route.ZoneKey, DateTime.Now);
                _onDeleted?.Invoke(route.ZoneKey);
                _report?.Invoke($"Deleted the route for {route.ZoneKey} (backed up).");
            }
            catch (Exception e)
            {
                _report?.Invoke($"Couldn't delete the route: {e.Message}");
            }

            // Skip the now-stale actions page and go back to the (shorter) list.
            _back.Pop();
            _back.Pop();
            ShowRoutes();
        }));
        End();
    }

    private void TravelTo(Route route)
    {
        if (!TryParseZone(route.ZoneKey, out var world, out var zone))
        {
            return;
        }

        var start = route.Start;
        _traveler.Start(world, zone, start == null ? null : RouteOverlay.ToUnity(start.Position), start?.YawDegrees ?? 0f, _report);
    }

    private static bool TryParseZone(string zoneKey, out StreamingTweakables.World world, out string zone)
    {
        var parts = zoneKey.Split('/', 2);
        zone = parts.Length == 2 ? parts[1] : null;
        world = default;
        return parts.Length == 2 && Enum.TryParse(parts[0], out world);
    }

    /// <summary>Go to a page, remembering how to come back to the current one.</summary>
    private void Push(Action current, Action next)
    {
        _back.Push(current);
        next();
    }

    private void Begin(string title)
    {
        Title = title;
        _entries.Clear();
        ClickedIndex = null;
    }

    private void Add(Entry entry) => _entries.Add(entry);

    private void End()
    {
        Selected = -1;
        Move(1);
    }
}
