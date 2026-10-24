using System;
using System.Collections.Generic;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using Il2Cpp;

namespace CairnRouteMaker.Editor;

/// <summary>
/// The mod's menu. Routes are listed under the zone they're in, and going somewhere also chooses
/// which route to show there, if any. In game, the first page has the routes of Ava's zone (switch
/// to one, start a new one, or show none), then the zones of the current mountain and the other
/// mountains (each opening a page of its zones). A zone with routes opens a page of them; one
/// without is travelled to straight away.
/// "Manage saved routes" lists every route with travel and delete actions. On the title screen it's
/// the free-climb launcher instead: a mountain, a zone, then one of its routes or a spawn point to
/// start from without one. Pages are rebuilt each time they show.
/// </summary>
internal sealed class LevelSelect
{
    public sealed class Entry
    {
        public Entry(string label, bool isHeader, bool enabled, Action choose, bool leavesEditor = false, string note = null)
        {
            Label = label;
            IsHeader = isHeader;
            Enabled = enabled;
            Choose = choose;
            LeavesEditor = leavesEditor;
            Note = note;
        }

        public string Label { get; }

        /// <summary>Shown smaller and dimmer after the label, e.g. "you are here".</summary>
        public string Note { get; }
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
    private RouteEditor _editor;
    private Traveler _traveler;
    private Action<string> _report;
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

    /// <summary>Open in game, over the route editor; choosing a route changes the one it shows.</summary>
    public void Open(RouteEditor editor, RouteStorage storage, Traveler traveler, Action<string> report)
    {
        _editor = editor;
        _storage = storage;
        _traveler = traveler;
        _report = report;
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
        var routes = _storage.LoadAll();
        Begin("Level Select", "Route Maker", Where(here, _editor.Route));
        Add(new Entry("Manage saved routes", false, true, () => Push(ShowLevels, ShowRoutes)));

        if (here != null)
        {
            Add(Header($"Routes in {ZoneNames.PrettyKey(here)}"));
            foreach (var route in routes)
            {
                if (route.ZoneKey == here)
                {
                    Add(RouteEntry(route));
                }
            }

            Add(new Entry("New route", false, true, () =>
            {
                Close();
                _editor.EditNewRoute();
            }));
            Add(new Entry("No route", false, _editor.Route != null, () =>
            {
                _editor.ShowRoute(null);
                _report?.Invoke("No route showing.");
            }, leavesEditor: true));
        }

        if (GameApi.CurrentWorld is { } currentWorld)
        {
            Add(Header($"Zones in {Mountain(currentWorld)}"));
            AddZones(currentWorld, routes, ShowLevels);
        }

        Add(Header("Mountains"));
        foreach (var world in GameApi.Worlds())
        {
            // Locked worlds are allowed on purpose: this is a mapping tool, not a playthrough.
            var id = world.Id;
            var count = routes.FindAll(route => ZoneNames.World(route.ZoneKey) == id.ToString()).Count;
            Add(new Entry(Mountain(id), false, !world.IsCurrent, () => Push(ShowLevels, () => ShowMountain(id)),
                note: world.IsCurrent ? "you are here" : RouteCount(count)));
        }

        End();
    }

    /// <summary>In game, another mountain's zones.</summary>
    private void ShowMountain(StreamingTweakables.World world)
    {
        Begin(Mountain(world), "Level Select", "Choose a zone to go to.");
        AddZones(world, _storage.LoadAll(), () => ShowMountain(world));
        End();
    }

    /// <summary>
    /// A mountain's zones. One with routes opens a page of them; one without is travelled to with
    /// no route. <paramref name="page"/> shows the page these are on, for coming back to it.
    /// </summary>
    private void AddZones(StreamingTweakables.World world, List<Route> routes, Action page)
    {
        var zones = GameApi.ZonesIn(world);
        if (zones.Count == 0)
        {
            Add(new Entry("The game hasn't listed its zones.", false, false, null));
        }

        foreach (var zone in zones)
        {
            var name = zone.Name;
            var count = CountIn(routes, $"{world}/{name}");
            if (zone.IsCurrent)
            {
                Add(new Entry(ZoneNames.Pretty(name), false, false, null, note: "you are here"));
            }
            else if (count > 0)
            {
                Add(new Entry(ZoneNames.Pretty(name), false, true, () => Push(page, () => ShowZoneRoutes(world, name)), note: RouteCount(count)));
            }
            else
            {
                Add(new Entry(ZoneNames.Pretty(name), false, true, () => GoWithoutRoute(world, name), leavesEditor: true));
            }
        }
    }

    /// <summary>In game, a zone that has routes.</summary>
    private void ShowZoneRoutes(StreamingTweakables.World world, string zone)
    {
        Begin(ZoneNames.Pretty(zone), $"Level Select · {Mountain(world)}", "Climb one of its routes, or go there without one.");
        Add(Header("Routes"));
        AddRoutes(_storage.LoadAll(), $"{world}/{zone}", RouteEntry);
        Add(Header("Without a route"));
        Add(new Entry($"Go to {ZoneNames.Pretty(zone)}", false, true, () => GoWithoutRoute(world, zone), leavesEditor: true));
        End();
    }

    /// <summary>A route to switch to: choosing it shows it and takes Ava to its start.</summary>
    private Entry RouteEntry(Route route) =>
        new(route.Name, false, TryParseZone(route.ZoneKey, out _, out _), () => Climb(route), leavesEditor: true,
            note: Notes(_editor.IsShowing(route) ? "showing" : null, route.Start == null ? "no start set" : null));

    private void ShowLaunch()
    {
        Begin("Free Climb", null, "Choose a mountain, then a zone and a route.");
        var routes = _storage.LoadAll();
        Add(Header("Mountains"));
        foreach (var world in GameApi.Worlds())
        {
            // Locked worlds are allowed on purpose, as in the in-game level select.
            var id = world.Id;
            var zones = GameApi.LaunchableZones(id);
            var count = 0;
            foreach (var zone in zones)
            {
                count += CountIn(routes, $"{id}/{zone.Name}");
            }

            Add(new Entry(Mountain(id), false, zones.Count > 0, () => Push(ShowLaunch, () => ShowLaunchZones(id)), note: RouteCount(count)));
        }

        End();
    }

    private void ShowLaunchZones(StreamingTweakables.World world)
    {
        Begin(Mountain(world), "Free Climb", "Choose a zone to start in.");
        var routes = _storage.LoadAll();
        foreach (var zone in GameApi.LaunchableZones(world))
        {
            var name = zone.Name;
            var count = CountIn(routes, $"{world}/{name}");
            var note = Notes(RouteCount(count), zone.Spawners.Count > 1 ? $"{zone.Spawners.Count} starts" : null);
            if (count > 0)
            {
                Add(new Entry(ZoneNames.Pretty(name), false, true, () => Push(() => ShowLaunchZones(world), () => ShowLaunchZone(world, zone)), note: note));
            }
            else if (zone.Spawners.Count > 1)
            {
                Add(new Entry(ZoneNames.Pretty(name), false, true, () => Push(() => ShowLaunchZones(world), () => ShowLaunchSpawners(world, name, zone.Spawners)), note: note));
            }
            else
            {
                var spawner = zone.Spawners[0];
                Add(new Entry(ZoneNames.Pretty(name), false, true, () => _launch(new LaunchTarget(world, name, spawner, null)), leavesEditor: true, note: note));
            }
        }

        End();
    }

    /// <summary>A zone with routes: climb one (from the zone's first spawn point), or start without one.</summary>
    private void ShowLaunchZone(StreamingTweakables.World world, GameApi.LaunchZone zone)
    {
        var name = zone.Name;
        var first = zone.Spawners[0];
        Begin(ZoneNames.Pretty(name), $"Free Climb · {Mountain(world)}", "Climb one of its routes, or start without one.");
        Add(Header("Routes"));
        AddRoutes(_storage.LoadAll(), $"{world}/{name}", route =>
            new Entry(route.Name, false, true, () => _launch(new LaunchTarget(world, name, first, route)), leavesEditor: true,
                note: route.Start == null ? "no start set" : null));

        Add(Header("Without a route"));
        if (zone.Spawners.Count == 1)
        {
            Add(new Entry("Just climb", false, true, () => _launch(new LaunchTarget(world, name, first, null)), leavesEditor: true));
        }
        else
        {
            foreach (var spawner in zone.Spawners)
            {
                Add(new Entry(spawner, false, true, () => _launch(new LaunchTarget(world, name, spawner, null)), leavesEditor: true));
            }
        }

        End();
    }

    private void ShowLaunchSpawners(StreamingTweakables.World world, string zone, IReadOnlyList<string> spawners)
    {
        Begin(ZoneNames.Pretty(zone), $"Free Climb · {Mountain(world)}", "Choose where to start.");
        foreach (var spawner in spawners)
        {
            Add(new Entry(spawner, false, true, () => _launch(new LaunchTarget(world, zone, spawner, null)), leavesEditor: true));
        }

        End();
    }

    private void ShowRoutes()
    {
        Begin("Saved Routes", "Level Select");
        var routes = _storage.LoadAll();
        if (routes.Count == 0)
        {
            Add(new Entry("No saved routes yet.", false, false, null));
        }

        string zone = null;
        foreach (var route in routes)
        {
            if (route.ZoneKey != zone)
            {
                zone = route.ZoneKey;
                Add(Header(Place(zone)));
            }

            Add(new Entry(route.Name, false, true, () => Push(ShowRoutes, () => ShowRouteActions(route)),
                note: Notes(_editor.IsShowing(route) ? "showing" : null, Summary(route))));
        }

        End();
    }

    private void ShowRouteActions(Route route)
    {
        Begin(route.Name, $"Saved Routes · {Place(route.ZoneKey)}", Summary(route));
        var canTravel = TryParseZone(route.ZoneKey, out _, out _);
        var label = route.Start != null ? "Travel to the start"
            : route.ZoneKey == GameApi.CurrentZoneKey ? "Show this route"
            : "Travel to this zone";
        Add(new Entry(label, false, canTravel, () => Climb(route), leavesEditor: true));
        Add(new Entry("Delete this route", false, true, () => Push(() => ShowRouteActions(route), () => ShowConfirmDelete(route))));
        End();
    }

    private void ShowConfirmDelete(Route route)
    {
        Begin("Delete this route?", $"{route.Name} · {ZoneNames.PrettyKey(route.ZoneKey)}", "It's moved to UserData/CairnRouteMaker/Backups, so it can be restored.");
        Add(new Entry("No, keep it", false, true, Back));
        Add(new Entry("Yes, delete it", false, true, () =>
        {
            try
            {
                _storage.Delete(route, DateTime.Now);
                _editor.OnRouteDeleted(route);
                _report?.Invoke($"Deleted {route.Name} from {ZoneNames.PrettyKey(route.ZoneKey)} (backed up).");
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

    /// <summary>Add an entry for each saved route of a zone, or a line saying there are none.</summary>
    private void AddRoutes(List<Route> routes, string zoneKey, Func<Route, Entry> entry)
    {
        var any = false;
        foreach (var route in routes)
        {
            if (route.ZoneKey == zoneKey)
            {
                any = true;
                Add(entry(route));
            }
        }

        if (!any)
        {
            Add(new Entry("No saved routes yet.", false, false, null));
        }
    }

    /// <summary>"You're in Well, Kami · showing Route 2".</summary>
    private static string Where(string here, Route shown)
    {
        if (here == null)
        {
            return null;
        }

        var route = shown == null ? "no route showing"
            : shown.ZoneKey == here ? $"showing {shown.Name}"
            : $"showing {shown.Name} from {ZoneNames.PrettyKey(shown.ZoneKey)}";
        return $"You're in {Place(here)} · {route}";
    }

    /// <summary>"Well, Kami" for "Kami/09_Well".</summary>
    private static string Place(string zoneKey) =>
        ZoneNames.World(zoneKey) is { } world ? $"{ZoneNames.PrettyKey(zoneKey)}, {ZoneNames.PrettyWorld(world)}" : ZoneNames.PrettyKey(zoneKey);

    private static string Mountain(StreamingTweakables.World world) => ZoneNames.PrettyWorld(world.ToString());

    private static string Summary(Route route)
    {
        var strokes = route.Strokes.Count;
        return $"{strokes} stroke{(strokes == 1 ? "" : "s")} · {(route.Start == null ? "no start" : "start set")}"
            + $" · {(route.Finish == null ? "no finish" : "finish set")}";
    }

    private static int CountIn(List<Route> routes, string zoneKey) => routes.FindAll(route => route.ZoneKey == zoneKey).Count;

    private static string RouteCount(int count) => count switch
    {
        0 => null,
        1 => "1 route",
        _ => $"{count} routes",
    };

    /// <summary>The notes that apply, joined with dots, or null if none do.</summary>
    private static string Notes(params string[] notes)
    {
        var joined = string.Join(" · ", Array.FindAll(notes, note => note != null));
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>Show a route and take Ava to its start, or to its zone if it has no start.</summary>
    private void Climb(Route route)
    {
        _editor.ShowRoute(route);
        // The editor keeps its own copy if this route was already showing.
        route = _editor.Route;
        if (route.Start == null && route.ZoneKey == GameApi.CurrentZoneKey)
        {
            _report?.Invoke($"Showing {route.Name}. It has no start yet.");
            return;
        }

        TravelTo(route);
    }

    private void GoWithoutRoute(StreamingTweakables.World world, string zone)
    {
        _editor.ShowRoute(null);
        _traveler.Start(world, zone, null, 0f, _report);
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

    private static Entry Header(string label) => new(label, true, false, null);

    private void End()
    {
        Selected = -1;
        Move(1);
    }
}
