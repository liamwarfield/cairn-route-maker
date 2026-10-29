using System;
using System.Collections.Generic;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using Il2Cpp;
using UnityEngine;

namespace CairnRouteMaker.Editor;

/// <summary>
/// The mod's menu. Routes are listed under the zone they're in, and going somewhere also chooses
/// which route to show there, if any. In game, the first page has the routes of Ava's zone (switch
/// to one, start a new one, or show none), then the zones of the current mountain and the other
/// mountains (each opening a page of its zones). A zone with routes opens a page of them; one
/// without is travelled to straight away.
/// "Manage saved routes" lists every route with travel and delete actions. On the title screen it's
/// the free-climb launcher instead: a mountain, a zone, then one of its routes or a spawn point to
/// start from without one. The route settings (see <see cref="PaintColors"/>) have pages of their own,
/// opened from the Routes tab of the game's Settings (see <see cref="PaintColorsMenu"/>).
/// Wherever a route is listed, the Edit route button (Y / E by default) opens its Edit Route page,
/// and its personal best time is noted beside it once it has one. Pages are rebuilt each time they show.
/// </summary>
internal sealed class LevelSelect
{
    public sealed class Entry
    {
        public Entry(string label, bool isHeader, bool enabled, Action choose, bool leavesEditor = false, string note = null,
            IReadOnlyList<Color32> swatches = null, Action edit = null, string detail = null)
        {
            Label = label;
            IsHeader = isHeader;
            Enabled = enabled;
            Choose = choose;
            LeavesEditor = leavesEditor;
            Note = note;
            Swatches = swatches;
            Edit = edit;
            Detail = detail;
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

        /// <summary>What the Edit route button does on this entry, or null: route entries open the route's Edit Route page.</summary>
        public Action Edit { get; }

        /// <summary>Shown in place of the page's description while this entry is highlighted, e.g. a route's description; or null.</summary>
        public string Detail { get; }
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

    /// <summary>Route names are cut off in the menus well before this, but it keeps them to one line.</summary>
    private const int MaxNameLength = 40;
    /// <summary>One line as typed; the text box shrinks its font to fit. It fits in two lines under the title.</summary>
    private const int MaxDescriptionLength = 100;

    private readonly List<Entry> _entries = new();
    private readonly Stack<Action> _back = new();
    private Action<string> _textDone;
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

    /// <summary>The page's text box, e.g. a route's new name, or null. While there is one, it takes all the menu's input.</summary>
    public TextEntry TextEntry { get; private set; }

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
    public void OpenLauncher(RouteStorage storage, Action<LaunchTarget> launch, Action<string> report)
    {
        _storage = storage;
        _launch = launch;
        _report = report;
        _back.Clear();
        ShowLaunch();
        IsOpen = true;
    }

    /// <summary>Open on the Routes page; backing out of it closes the menu.</summary>
    public void OpenPaintColors()
    {
        _back.Clear();
        ShowPaintColors();
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        TextEntry = null;
    }

    /// <summary>The Edit route button: edit the highlighted route, if it is one.</summary>
    public void Edit() => Current?.Edit?.Invoke();

    /// <summary>
    /// Type into the page's text box, if it has one. Returns true if it did, and then the menu's
    /// own input should be skipped this frame.
    /// </summary>
    public bool UpdateTextEntry()
    {
        if (TextEntry == null)
        {
            return false;
        }

        switch (TextEntry.Update())
        {
            case TextEntryResult.Done:
                _textDone(TextEntry.Text);
                break;
            case TextEntryResult.Cancelled:
                Back();
                break;
        }

        return true;
    }

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
                    Add(RouteEntry(route, ShowLevels));
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
        AddRoutes(_storage.LoadAll(), $"{world}/{zone}", route => RouteEntry(route, () => ShowZoneRoutes(world, zone)));
        Add(Header("Without a route"));
        Add(new Entry($"Go to {ZoneNames.Pretty(zone)}", false, true, () => GoWithoutRoute(world, zone), leavesEditor: true));
        End();
    }

    /// <summary>A route to switch to: choosing it shows it and takes Ava to its start. <paramref name="page"/> is the page it's on.</summary>
    private Entry RouteEntry(Route route, Action page) =>
        new(route.Name, false, TryParseZone(route.ZoneKey, out _, out _), () => Climb(route), leavesEditor: true,
            note: Notes(_editor.IsShowing(route) ? "climbing" : null, Best(route), route.Start == null ? "no start set" : null),
            edit: EditAction(route, page), detail: route.Description);

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
                note: Notes(Best(route), route.Start == null ? "no start set" : null),
                edit: EditAction(route, () => ShowLaunchZone(world, zone)), detail: route.Description));

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
                note: Notes(_editor.IsShowing(route) ? "climbing" : null, Summary(route)), edit: EditAction(route, ShowRoutes),
                detail: route.Description));
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
                _editor?.OnRouteDeleted(route);
                _report?.Invoke($"Deleted {route.Name} from {ZoneNames.PrettyKey(route.ZoneKey)} (backed up).");
            }
            catch (Exception e)
            {
                _report?.Invoke($"Couldn't delete the route: {e.Message}");
            }

            // Skip the now-stale page of the route and go back to the (shorter) list it was opened from.
            _back.Pop();
            _back.Pop()();
        }));
        End();
    }

    /// <summary>The Edit route button on a route's entry: open its Edit Route page, coming back to <paramref name="page"/>.</summary>
    private Action EditAction(Route route, Action page) => () => Push(page, () => ShowEditRoute(route));

    private void ShowEditRoute(Route route)
    {
        Begin(route.Name, $"Edit Route · {Place(route.ZoneKey)}", Summary(route));
        Action page = () => ShowEditRoute(route);
        Add(new Entry("Rename", false, true, () => Push(page, () => ShowTextField(route, "Rename Route", "Type a new name for this route.",
            route.Name, MaxNameLength, name => Rename(route, name)))));
        Add(new Entry("Description", false, true, () => Push(page, () => ShowTextField(route, "Description", "A line or two about the route. Leave it empty for none.",
            route.Description, MaxDescriptionLength, text => SetDescription(route, text))),
            note: route.Description == null ? "none" : null, detail: route.Description));
        Add(new Entry("Duplicate", false, true, () => Duplicate(route)));
        if (_storage.Bests.Get(route.Id) is { } best)
        {
            Add(new Entry("Reset personal best", false, true, () => Push(page, () => ShowConfirmResetBest(route, best)),
                note: Attempt.FormatTime(best)));
        }

        Add(new Entry("Delete", false, true, () => Push(page, () => ShowConfirmDelete(route))));
        End();
    }

    private void ShowConfirmResetBest(Route route, double best)
    {
        Begin("Reset personal best?", $"{route.Name} · {ZoneNames.PrettyKey(route.ZoneKey)}",
            $"Your best time of {Attempt.FormatTime(best)} is forgotten. The next finish that stays on route sets a new one.");
        Add(new Entry("No, keep it", false, true, Back));
        Add(new Entry("Yes, reset it", false, true, () =>
        {
            try
            {
                _storage.Bests.Clear(route.Id);
                _report?.Invoke($"Reset the personal best on {route.Name}.");
            }
            catch (Exception e)
            {
                _report?.Invoke($"Couldn't reset the personal best: {e.Message}");
            }

            Back();
        }));
        End();
    }

    /// <summary>A page for typing one of a route's details. <paramref name="done"/> gets the text, and goes back or sets an error.</summary>
    private void ShowTextField(Route route, string title, string description, string text, int maxLength, Action<string> done)
    {
        Begin(title, $"Edit Route · {route.Name}", description);
        End();
        TextEntry = new TextEntry(text, maxLength);
        _textDone = done;
    }

    /// <summary>Save a route under a new name and go back to its Edit Route page, or say why the name can't be used.</summary>
    private void Rename(Route route, string name)
    {
        name = name.Trim();
        if (name == route.Name)
        {
            Back();
            return;
        }

        if (name.Length == 0)
        {
            TextEntry.Error = "A route needs a name.";
            return;
        }

        if (_storage.LoadAll().Exists(other => other.ZoneKey == route.ZoneKey && other.Id != route.Id
                && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            TextEntry.Error = $"Another route in {ZoneNames.PrettyKey(route.ZoneKey)} is called that.";
            return;
        }

        var old = route.Name;
        route.Name = name;
        if (!TrySave(route))
        {
            route.Name = old;
            return;
        }

        _report?.Invoke($"Renamed {old} to {name}.");
        Back();
    }

    /// <summary>Save a route's description (empty for none) and go back to its Edit Route page.</summary>
    private void SetDescription(Route route, string text)
    {
        var value = text.Trim();
        value = value.Length == 0 ? null : value;
        var old = route.Description;
        if (value != old)
        {
            route.Description = value;
            if (!TrySave(route))
            {
                route.Description = old;
                return;
            }
        }

        Back();
    }

    /// <summary>Write a route changed on a text page, and give the editor's copy the change too; if it fails, say why on the page.</summary>
    private bool TrySave(Route route)
    {
        try
        {
            _storage.Save(route);
        }
        catch (Exception e)
        {
            TextEntry.Error = $"Couldn't save: {e.Message}";
            return false;
        }

        _editor?.OnRouteDetailsChanged(route);
        return true;
    }

    /// <summary>Save a copy of a route and show the copy's Edit Route page in place of the original's.</summary>
    private void Duplicate(Route route)
    {
        // The editor's copy may have changes its file doesn't have yet.
        var source = _editor != null && _editor.IsShowing(route) ? _editor.Route : route;
        Route copy;
        try
        {
            copy = _storage.Duplicate(source);
        }
        catch (Exception e)
        {
            _report?.Invoke($"Couldn't copy the route: {e.Message}");
            return;
        }

        _report?.Invoke($"Saved a copy: {copy.Name}.");
        // Backing out then goes to the list, which has the copy in it too.
        ShowEditRoute(copy);
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

    /// <summary>"You're in Well, Kami · climbing Route 2".</summary>
    private static string Where(string here, Route shown)
    {
        if (here == null)
        {
            return null;
        }

        var route = shown == null ? "no route"
            : shown.ZoneKey == here ? $"climbing {shown.Name}"
            : $"climbing {shown.Name} from {ZoneNames.PrettyKey(shown.ZoneKey)}";
        return $"You're in {Place(here)} · {route}";
    }

    /// <summary>"Well, Kami" for "Kami/09_Well".</summary>
    private static string Place(string zoneKey) =>
        ZoneNames.World(zoneKey) is { } world ? $"{ZoneNames.PrettyKey(zoneKey)}, {ZoneNames.PrettyWorld(world)}" : ZoneNames.PrettyKey(zoneKey);

    private static string Mountain(StreamingTweakables.World world) => ZoneNames.PrettyWorld(world.ToString());

    private void ShowPaintColors()
    {
        Begin("Routes", "Settings", "Pick from the lists, or set any #RRGGBB in UserData/MelonPreferences.cfg.");
        foreach (var mode in new[] { StrokeMode.Allow, StrokeMode.Deny })
        {
            var color = PaintColors.Of(mode);
            Add(new Entry(mode == StrokeMode.Allow ? "Allow paint" : "Deny paint", false, true, () => Push(ShowPaintColors, () => ShowPaintChoices(mode)),
                note: PaintColors.NameOf(color), swatches: new[] { color }));
        }

        Add(new Entry("Style", false, true, () => ChangePaint(() => PaintColors.SetStyle(PaintColors.Style switch
            {
                PaintStyle.FillAndGlow => PaintStyle.GlowOnly,
                PaintStyle.GlowOnly => PaintStyle.FillOnly,
                _ => PaintStyle.FillAndGlow,
            })), note: PaintColors.NameOf(PaintColors.Style)));
        Add(new Entry("Glow pulse", false, true, () => ChangePaint(() => PaintColors.SetGlowPulse(!PaintColors.GlowPulse)),
            note: PaintColors.GlowPulse ? "on" : "off"));
        Add(new Entry("Show route lines", false, true, () => ChangePaint(() => PaintColors.SetShowLine(!PaintColors.ShowLine)),
            note: PaintColors.ShowLine ? "on" : "off"));
        Add(new Entry("Paint draw distance", false, true, () => ChangePaint(() => PaintColors.SetPaintDistanceLevel(
                (PaintColors.LevelOf(PaintColors.PaintDistance) + 1) % PaintColors.PaintDistanceLevels.Length)),
            note: PaintColors.DescribePaintDistance(PaintColors.PaintDistance)));

        Add(new Entry("Presets", true, false, null));
        foreach (var preset in PaintColors.Presets)
        {
            Add(new Entry(preset.Name, false, true, () => ChangePaint(() => PaintColors.ApplyPreset(preset)),
                note: preset.Note, swatches: new[] { preset.Allow, preset.Deny }));
        }

        End();
    }

    /// <summary>Make a change from the Routes page, then show it again on the same entry.</summary>
    private void ChangePaint(Action change)
    {
        var index = Selected;
        change();
        ShowPaintColors();
        Select(index);
    }

    private void ShowPaintChoices(StrokeMode mode)
    {
        var allow = mode == StrokeMode.Allow;
        Begin(allow ? "Allow Paint" : "Deny Paint", "Settings · Routes", allow ? "Paint for holds Ava may use." : "Paint for holds that are off-route.");
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

    private string Summary(Route route)
    {
        var strokes = route.Strokes.Count;
        return (Best(route) is { } best ? $"{best} · " : "")
            + $"{strokes} stroke{(strokes == 1 ? "" : "s")} · {(route.Start == null ? "no start" : "start set")}"
            + $" · {(route.Finish == null ? "no finish" : "finish set")}"
            + LineSummary(route);
    }

    /// <summary>" · blue line", " · 3 lines", or nothing.</summary>
    private static string LineSummary(Route route)
    {
        var lines = route.Lines.FindAll(line => line.Points.Count >= 2);
        return lines.Count switch
        {
            0 => "",
            1 => $" · {LineColors.NameOf(lines[0].Grade).ToLowerInvariant()} line",
            _ => $" · {lines.Count} lines",
        };
    }

    /// <summary>"PB 1:23.45", or null if the route hasn't been finished on-route yet.</summary>
    private string Best(Route route) => _storage.Bests.Get(route.Id) is { } best ? $"PB {Attempt.FormatTime(best)}" : null;

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
        TextEntry = null;
    }

    private void Add(Entry entry) => _entries.Add(entry);

    private static Entry Header(string label) => new(label, true, false, null);

    private void End()
    {
        Selected = -1;
        Move(1);
    }
}
