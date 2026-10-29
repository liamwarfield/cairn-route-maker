using System;
using System.Collections.Generic;
using System.Linq;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;

namespace CairnRouteMaker.Editor;

/// <summary>
/// A route's Loadout page, reached from its Edit Route page: how Ava starts each time she's put on
/// the route's start (see <see cref="Loadout"/>). Its settings change with left and right, and
/// each change is saved straight away. Items are added from a page per kind of item.
/// </summary>
internal sealed partial class LevelSelect
{
    /// <summary>The item list switched off on the Loadout page, kept so switching it back on brings it back.</summary>
    private (Route Route, List<LoadoutItem> Items) _setAsideItems;

    private void ShowLoadout(Route route, string highlightItem = null)
    {
        var loadout = route.Loadout;
        if (!_refreshing)
        {
            // Items stepped down to none stay listed until the page is left, so they can be stepped back up.
            loadout.RemoveEmptyItems();
        }

        Begin("Loadout", $"Edit Route · {route.Name}", "How Ava starts each time she's put on this route's start.");
        Action page = () => ShowLoadout(route);
        Add(Header("Ava"));
        Add(Setting(route, page, "Health", $"{loadout.Health}%", loadout.StepHealth,
            note: loadout.EffectiveHealth < loadout.Health ? $"{loadout.EffectiveHealth}% in end game" : null,
            detail: "Her health when she starts, out of full."));
        Add(Setting(route, page, "Left hand", GripName(loadout.LeftHand), step => loadout.LeftHand = Loadout.StepGrip(loadout.LeftHand, step),
            detail: GripDetail));
        Add(Setting(route, page, "Right hand", GripName(loadout.RightHand), step => loadout.RightHand = Loadout.StepGrip(loadout.RightHand, step),
            detail: GripDetail));
        Add(Setting(route, page, "End game", OnOff(loadout.EndGame), _ => loadout.EndGame = !loadout.EndGame,
            detail: "As near Kami's summit: hunger, thirst and warmth capped at a third, health at half, and no running."));
        Add(Setting(route, page, "Survival needs", OnOff(loadout.SurvivalNeeds), _ => loadout.SurvivalNeeds = !loadout.SurvivalNeeds,
            detail: "Hunger, thirst and cold drain over time, as outside free climb. The bars start full either way."));

        Add(Header("Inventory"));
        Add(Setting(route, page, "Pitons", PitonCount(loadout.Pitons), loadout.StepPitons,
            note: loadout.Pitons == 0 ? "free solo" : null,
            detail: "How many pitons she has, up to a full harness. Unchanged leaves hers as they are."));
        if (loadout.Pitons != null)
        {
            Add(Setting(route, page, "Crystal pitons", $"{loadout.CrystalPitons}", loadout.StepCrystalPitons,
                detail: "How many of those are crystal pitons, which go into rock too hard for the others.",
                enabled: loadout.Pitons > 0));
        }

        Add(Setting(route, page, "Items", loadout.Items == null ? "unchanged" : "only these", _ => ToggleItems(route),
            detail: loadout.Items == null
                ? "Her food, water, chalk and other gear stay as they are."
                : "Her food, water, chalk and other gear are only these. Pitons, her flask, light stick and other things the game gives her itself are kept apart."));
        var highlight = -1;
        if (loadout.Items != null)
        {
            foreach (var item in loadout.Items)
            {
                if (item.Id == highlightItem)
                {
                    highlight = _entries.Count;
                }

                var listed = item;
                Add(Setting(route, page, AvaLoadout.ItemName(item.Id), item.Count == 0 ? "none" : $"×{item.Count}",
                    step => Loadout.StepCount(listed, step),
                    note: Notes(AvaLoadout.ItemNote(item.Id), item.Count == 0 ? "comes off the list" : null)));
            }

            if (loadout.Items.Count == 0)
            {
                Add(new Entry("Nothing: no food, water, chalk or other gear.", false, false, null));
            }

            Add(new Entry("Add an item", false, true, () => Push(page, () => ShowItemCategories(route))));
        }

        Add(Header("Default"));
        Add(new Entry("Reset the loadout", false, !loadout.IsDefault, () =>
            {
                route.Loadout = new Loadout();
                _setAsideItems = default;
                SaveLoadout(route);
                ShowLoadout(route);
            },
            detail: "Full health, taped fingers, no end game or survival needs, and her pitons and items unchanged."));
        End();
        if (highlight >= 0)
        {
            Select(highlight);
        }
    }

    private const string GripDetail = "Taped fingers grip best and save stamina, bare ones grip normally, and hurt ones tire her faster.";

    /// <summary>A setting that steps with left and right; each change is saved and the page redrawn in place.</summary>
    private Entry Setting(Route route, Action page, string label, string value, Action<int> step, string note = null, string detail = null,
        bool enabled = true) =>
        new(label, false, enabled, null, note: note, detail: detail, value: value, adjust: direction =>
        {
            step(direction);
            SaveLoadout(route);
            Refresh(page);
        });

    /// <summary>Switch between Ava keeping her own things and the route's list, keeping the list in case it's switched back.</summary>
    private void ToggleItems(Route route)
    {
        var loadout = route.Loadout;
        if (loadout.Items != null)
        {
            _setAsideItems = (route, loadout.Items);
            loadout.Items = null;
        }
        else
        {
            loadout.Items = _setAsideItems.Route == route ? _setAsideItems.Items : new List<LoadoutItem>();
            _setAsideItems = default;
        }
    }

    private void ShowItemCategories(Route route)
    {
        Begin("Add an Item", $"Loadout · {route.Name}", "Pick a kind of item.");
        var catalog = AvaLoadout.Catalog();
        if (catalog.Count == 0)
        {
            Add(new Entry("Load into the mountain to pick items.", false, false, null));
        }

        // The catalog is already in category order.
        foreach (var group in catalog.GroupBy(info => info.Category))
        {
            var category = group.Key;
            var count = group.Count();
            Add(new Entry(category, false, true, () => Push(() => ShowItemCategories(route), () => ShowItems(route, category)),
                note: count == 1 ? "1 item" : $"{count} items"));
        }

        End();
    }

    private void ShowItems(Route route, string category)
    {
        Begin(category, $"Add an Item · {route.Name}", "Choose one to add it to the loadout.");
        foreach (var info in AvaLoadout.Catalog())
        {
            if (info.Category != category)
            {
                continue;
            }

            var count = route.Loadout.Items?.Find(item => item.Id == info.Id)?.Count ?? 0;
            Add(new Entry(info.Name, false, true, () => AddItem(route, info),
                note: Notes(info.Note, count > 0 ? $"×{count} in the loadout" : null)));
        }

        End();
    }

    /// <summary>Add one of an item and go back to the Loadout page with it highlighted.</summary>
    private void AddItem(Route route, LoadoutItemInfo info)
    {
        route.Loadout.AddItem(info.Id);
        SaveLoadout(route);
        // Past this page and the categories, back to the Loadout page.
        _back.Pop();
        _back.Pop();
        ShowLoadout(route, info.Id);
    }

    /// <summary>Write a route whose loadout changed, and give the editor's copy the change too.</summary>
    private void SaveLoadout(Route route)
    {
        try
        {
            _storage.Save(route);
        }
        catch (Exception e)
        {
            _report?.Invoke($"Couldn't save the loadout: {e.Message}");
            return;
        }

        _editor?.OnRouteDetailsChanged(route);
    }

    /// <summary>"60% health · end game · 4 items" for the Edit Route page, or "default".</summary>
    private static string LoadoutSummary(Loadout loadout)
    {
        if (loadout.IsDefault)
        {
            return "default";
        }

        var parts = new List<string>();
        if (loadout.Health != Loadout.MaxHealth)
        {
            parts.Add($"{loadout.Health}% health");
        }

        if (loadout.LeftHand != Grip.Taped || loadout.RightHand != Grip.Taped)
        {
            parts.Add(loadout.LeftHand == loadout.RightHand ? $"{GripName(loadout.LeftHand).ToLowerInvariant()} fingers" : "mixed grip");
        }

        if (loadout.EndGame)
        {
            parts.Add("end game");
        }

        if (loadout.SurvivalNeeds)
        {
            parts.Add("survival needs");
        }

        if (loadout.Pitons is { } pitons)
        {
            parts.Add(pitons == 0 ? "free solo"
                : loadout.CrystalPitons > 0 ? $"{pitons} pitons ({loadout.CrystalPitons} crystal)"
                : pitons == 1 ? "1 piton" : $"{pitons} pitons");
        }

        if (loadout.Items != null)
        {
            var count = loadout.Items.Sum(item => item.Count);
            parts.Add(count switch
            {
                0 => "no items",
                1 => "1 item",
                _ => $"{count} items",
            });
        }

        return string.Join(" · ", parts);
    }

    private static string GripName(Grip grip) => grip switch
    {
        Grip.Bare => "Bare",
        Grip.Hurt => "Hurt",
        _ => "Taped",
    };

    private static string OnOff(bool on) => on ? "On" : "Off";

    private static string PitonCount(int? pitons) => pitons switch
    {
        null => "unchanged",
        0 => "none",
        _ => $"{pitons}",
    };
}
