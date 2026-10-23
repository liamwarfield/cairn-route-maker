using System;
using System.Collections.Generic;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using Il2Cpp;
using UnityEngine;

namespace CairnRouteMaker.Editor;

/// <summary>
/// The mod's menu. In game, the first page jumps around for testing: saved routes (travel there and
/// stand on the start), zones of the current mountain, and the other mountains. From it, "Manage saved
/// routes" lists every route with travel and delete actions. On the title screen it's the free-climb
/// launcher instead: pick a saved route, or a mountain, zone and spawn point, to start a game there.
/// Both end with the settings: the paint colours (see <see cref="PaintColors"/>).
/// Pages are rebuilt each time they show.
/// </summary>
internal sealed class LevelSelect
{
    public sealed class Entry
    {
        public Entry(string label, bool isHeader, bool enabled, Action choose, bool leavesEditor = false, string note = null,
            IReadOnlyList<Color32> swatches = null)
        {
            Label = label;
            IsHeader = isHeader;
            Enabled = enabled;
            Choose = choose;
            LeavesEditor = leavesEditor;
            Note = note;
            Swatches = swatches;
        }

        public string Label { get; }

        /// <summary>Shown smaller and dimmer after the label, e.g. "you are here".</summary>
        public string Note { get; }

        /// <summary>Colour squares shown between the label and the note, or null.</summary>
        public IReadOnlyList<Color32> Swatches { get; }
        public bool IsHeader { get; }
        public bool Enabled { get; }
        public Action Choose { get; }

        /// <summary>Travel entries: the editor closes (handing back input and camera) before they run.</summary>
        public bool LeavesEditor { get; }
    }

    /// <summary>Where to start a free-climb game; with a route, Ava is then taken to its start.</summary>
    public sealed class LaunchTarget
    {
        public LaunchTarget(StreamingTweakables.World world, string zone, string spawner, Route route)
        {
            World = world;
            Zone = zone;
            Spawner = spawner;
            Route = route;
        }

        public StreamingTweakables.World World { get; }
        public string Zone { get; }
        public string Spawner { get; }
        public Route Route { get; }
    }

    private readonly List<Entry> _entries = new();
    private readonly Stack<Action> _back = new();
    private RouteStorage _storage;
    private Traveler _traveler;
    private Action<string> _report;
    private Action<string> _onDeleted;
    private Action<LaunchTarget> _launch;

    public bool IsOpen { get; private set; }
    public string Title { get; private set; }

    /// <summary>A small line above the title: where this page was reached from.</summary>
    public string Breadcrumb { get; private set; }

    /// <summary>A line under the title, or null.</summary>
    public string Description { get; private set; }

    /// <summary>Goes up each time a page is shown, so the view knows to rebuild.</summary>
    public int Page { get; private set; }
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

    /// <summary>Open as the free-climb launcher (title screen): choosing a place calls <paramref name="launch"/>.</summary>
    public void OpenLauncher(RouteStorage storage, Action<LaunchTarget> launch)
    {
        _storage = storage;
        _launch = launch;
        _back.Clear();
        ShowLaunch();
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
        var here = GameApi.CurrentZoneKey;
        Begin("Level Select", "Route Maker", here == null ? null : $"You're in {ZoneNames.PrettyKey(here)}, {ZoneNames.World(here)}");
        Add(new Entry("Manage saved routes", false, true, () => Push(ShowLevels, ShowRoutes)));

        Add(new Entry("Saved routes", true, false, null));
        foreach (var route in _storage.LoadAll())
        {
            Add(new Entry(ZoneNames.PrettyKey(route.ZoneKey), false, TryParseZone(route.ZoneKey, out _, out _), () => TravelTo(route), leavesEditor: true,
                note: route.Start == null ? "no start set" : null));
        }

        if (GameApi.CurrentWorld is { } currentWorld)
        {
            Add(new Entry($"Zones in {currentWorld}", true, false, null));
            foreach (var zone in GameApi.ZonesInCurrentWorld())
            {
                var note = zone.IsCurrent ? "you are here" : null;
                var name = zone.Name;
                Add(new Entry(ZoneNames.Pretty(name), false, !zone.IsCurrent, () => _traveler.Start(currentWorld, name, null, 0f, _report), leavesEditor: true, note: note));
            }
        }

        Add(new Entry("Mountains", true, false, null));
        foreach (var world in GameApi.Worlds())
        {
            // Locked worlds are allowed on purpose: this is a mapping tool, not a playthrough.
            var note = world.IsCurrent ? "you are here" : null;
            var id = world.Id;
            Add(new Entry(id.ToString(), false, !world.IsCurrent, () => _traveler.Start(id, null, null, 0f, _report), leavesEditor: true, note: note));
        }

        AddSettings(ShowLevels);
        End();
    }

    private void ShowLaunch()
    {
        Begin("Free Climb", null, "Climb a saved route, or start anywhere on a mountain.");
        Add(new Entry("Saved routes", true, false, null));
        var anyRoutes = false;
        foreach (var route in _storage.LoadAll())
        {
            anyRoutes = true;
            var spawner = TryParseZone(route.ZoneKey, out var world, out var zone) ? FirstSpawner(world, zone) : null;
            var note = spawner == null ? "can't start a game here" : route.Start == null ? "no start set" : null;
            Add(new Entry(ZoneNames.PrettyKey(route.ZoneKey), false, spawner != null, () => _launch(new LaunchTarget(world, zone, spawner, route)), leavesEditor: true, note: note));
        }

        if (!anyRoutes)
        {
            Add(new Entry("No saved routes yet.", false, false, null));
        }

        Add(new Entry("Mountains", true, false, null));
        foreach (var world in GameApi.Worlds())
        {
            // Locked worlds are allowed on purpose, as in the in-game level select.
            var id = world.Id;
            Add(new Entry(id.ToString(), false, GameApi.LaunchableZones(id).Count > 0, () => Push(ShowLaunch, () => ShowLaunchZones(id))));
        }

        AddSettings(ShowLaunch);
        End();
    }

    private void ShowLaunchZones(StreamingTweakables.World world)
    {
        Begin(world.ToString(), "Free Climb", "Choose a zone to start in.");
        foreach (var zone in GameApi.LaunchableZones(world))
        {
            var name = zone.Name;
            var hasRoute = _storage.Exists($"{world}/{name}");
            if (zone.Spawners.Count == 1)
            {
                var spawner = zone.Spawners[0];
                Add(new Entry(ZoneNames.Pretty(name), false, true, () => _launch(new LaunchTarget(world, name, spawner, null)), leavesEditor: true,
                    note: hasRoute ? "saved route" : null));
            }
            else
            {
                var starts = $"{zone.Spawners.Count} starts";
                Add(new Entry(ZoneNames.Pretty(name), false, true,
                    () => Push(() => ShowLaunchZones(world), () => ShowLaunchSpawners(world, name, zone.Spawners)),
                    note: hasRoute ? $"saved route · {starts}" : starts));
            }
        }

        End();
    }

    private void ShowLaunchSpawners(StreamingTweakables.World world, string zone, IReadOnlyList<string> spawners)
    {
        Begin(ZoneNames.Pretty(zone), $"Free Climb · {world}", "Choose where to start.");
        foreach (var spawner in spawners)
        {
            Add(new Entry(spawner, false, true, () => _launch(new LaunchTarget(world, zone, spawner, null)), leavesEditor: true));
        }

        End();
    }

    private static string FirstSpawner(StreamingTweakables.World world, string zone)
    {
        foreach (var candidate in GameApi.LaunchableZones(world))
        {
            if (candidate.Name == zone)
            {
                return candidate.Spawners[0];
            }
        }

        return null;
    }

    private void ShowRoutes()
    {
        Begin("Saved Routes", "Level Select");
        var routes = _storage.LoadAll();
        if (routes.Count == 0)
        {
            Add(new Entry("No saved routes yet.", false, false, null));
        }

        foreach (var route in routes)
        {
            Add(new Entry(ZoneNames.PrettyKey(route.ZoneKey), false, true, () => Push(ShowRoutes, () => ShowRouteActions(route)), note: Summary(route)));
        }

        End();
    }

    private void ShowRouteActions(Route route)
    {
        Begin(ZoneNames.PrettyKey(route.ZoneKey), $"Saved Routes · {ZoneNames.World(route.ZoneKey)}", Summary(route));
        var canTravel = TryParseZone(route.ZoneKey, out _, out _);
        Add(new Entry(route.Start == null ? "Travel to this zone" : "Travel to the start", false, canTravel, () => TravelTo(route), leavesEditor: true));
        Add(new Entry("Delete this route", false, true, () => Push(() => ShowRouteActions(route), () => ShowConfirmDelete(route))));
        End();
    }

    private void ShowConfirmDelete(Route route)
    {
        Begin("Delete this route?", ZoneNames.PrettyKey(route.ZoneKey), "It's moved to UserData/CairnRouteMaker/Backups, so it can be restored.");
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

    /// <summary>The settings section that ends the first page; <paramref name="page"/> is that page, to come back to.</summary>
    private void AddSettings(Action page)
    {
        Add(new Entry("Settings", true, false, null));
        Add(new Entry("Paint colours", false, true, () => Push(page, ShowPaintColors),
            swatches: new[] { PaintColors.Allow, PaintColors.Deny }));
    }

    private void ShowPaintColors()
    {
        Begin("Paint Colours", "Settings", "Pick from the lists, or set any #RRGGBB in UserData/MelonPreferences.cfg.");
        foreach (var mode in new[] { StrokeMode.Allow, StrokeMode.Deny })
        {
            var color = PaintColors.Of(mode);
            Add(new Entry(mode == StrokeMode.Allow ? "Allow paint" : "Deny paint", false, true, () => Push(ShowPaintColors, () => ShowPaintChoices(mode)),
                note: PaintColors.NameOf(color), swatches: new[] { color }));
        }

        Add(new Entry("Presets", true, false, null));
        foreach (var preset in PaintColors.Presets)
        {
            Add(new Entry(preset.Name, false, true, () =>
            {
                // Redraw the swatches above, staying on this entry.
                var index = Selected;
                PaintColors.SetBoth(preset.Allow, preset.Deny);
                ShowPaintColors();
                Select(index);
            }, note: preset.Note, swatches: new[] { preset.Allow, preset.Deny }));
        }

        End();
    }

    private void ShowPaintChoices(StrokeMode mode)
    {
        var allow = mode == StrokeMode.Allow;
        Begin(allow ? "Allow Paint" : "Deny Paint", "Settings · Paint Colours", allow ? "Paint for holds Ava may use." : "Paint for holds that are off-route.");
        var current = PaintColors.Of(mode);
        var other = PaintColors.Of(allow ? StrokeMode.Deny : StrokeMode.Allow);
        var currentIndex = -1;
        foreach (var choice in PaintColors.Palette)
        {
            var color = choice.Color;
            string note = null;
            if (PaintColors.Same(color, current))
            {
                note = "current";
                currentIndex = _entries.Count;
            }
            else if (PaintColors.Same(color, other))
            {
                note = allow ? "swaps with deny paint" : "swaps with allow paint";
            }

            Add(new Entry(choice.Name, false, true, () =>
            {
                PaintColors.Set(mode, color);
                Back();
            }, note: note, swatches: new[] { color }));
        }

        End();
        if (currentIndex >= 0)
        {
            Select(currentIndex);
        }
    }

    private static string Summary(Route route)
    {
        var strokes = route.Strokes.Count;
        return $"{strokes} stroke{(strokes == 1 ? "" : "s")} · {(route.Start == null ? "no start" : "start set")}"
            + $" · {(route.Finish == null ? "no finish" : "finish set")}";
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

    private void Begin(string title, string breadcrumb = null, string description = null)
    {
        Title = title;
        Breadcrumb = breadcrumb;
        Description = description;
        Page++;
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
