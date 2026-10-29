using System;
using System.Collections.Generic;

namespace CairnRouteMaker.Routes;

/// <summary>
/// How a hand's fingers start, named for the grip gauge on the game's taping screen. Taped fingers
/// give the best grip, bare ones a normal grip, and hurt ones make her tire faster.
/// </summary>
public enum Grip
{
    Taped,
    Bare,
    Hurt,
}

/// <summary>An item Ava carries on a route: the game's item id (e.g. "ITEM_ENERGY_BAR") and how many.</summary>
public sealed class LoadoutItem
{
    /// <summary>The most of one item a route can hand out.</summary>
    public const int MaxCount = 20;

    public LoadoutItem(string id, int count)
    {
        Id = id;
        Count = count;
    }

    public string Id { get; }

    /// <summary>Whole items, or single pitons and puffs of chalk. Zero means it's about to be taken off the list.</summary>
    public int Count { get; set; }
}

/// <summary>
/// The state Ava is put in each time she's taken to the route's start: her health, her grip, the
/// end-game mode from near Kami's summit (hunger, thirst and warmth capped at a third), whether
/// hunger, thirst and cold drain at all (free climb normally has them off), and optionally exactly
/// what she carries. The defaults are what every route had before loadouts: Ava at full health
/// with taped fingers, no end game, no drain, and her inventory left as it is.
/// </summary>
public sealed class Loadout
{
    public const int MinHealth = 5;
    public const int MaxHealth = 100;
    public const int HealthStep = 5;

    /// <summary>Health as a percentage of Ava's full health. End game caps it at half.</summary>
    public int Health { get; set; } = MaxHealth;

    public Grip LeftHand { get; set; } = Grip.Taped;
    public Grip RightHand { get; set; } = Grip.Taped;

    /// <summary>Near Kami's summit: hunger, thirst and warmth capped at a third, health at half, and no running.</summary>
    public bool EndGame { get; set; }

    /// <summary>Hunger, thirst and cold drain over time, as outside free climb.</summary>
    public bool SurvivalNeeds { get; set; }

    /// <summary>Exactly what Ava carries, or null to leave her inventory as it is.</summary>
    public List<LoadoutItem> Items { get; set; }

    public bool IsDefault => Health == MaxHealth && LeftHand == Grip.Taped && RightHand == Grip.Taped
        && !EndGame && !SurvivalNeeds && Items == null;

    /// <summary>Step the health by <paramref name="steps"/> notches, wrapping past either end.</summary>
    public void StepHealth(int steps)
    {
        var notches = (MaxHealth - MinHealth) / HealthStep + 1;
        var notch = (Health - MinHealth) / HealthStep;
        notch = ((notch + steps) % notches + notches) % notches;
        Health = MinHealth + notch * HealthStep;
    }

    /// <summary>The next or previous grip, wrapping around.</summary>
    public static Grip StepGrip(Grip grip, int steps)
    {
        var count = Enum.GetValues<Grip>().Length;
        return (Grip)((((int)grip + steps) % count + count) % count);
    }

    /// <summary>Add one of an item, or one more if it's already listed. Starts an item list if there isn't one.</summary>
    public void AddItem(string id)
    {
        Items ??= new List<LoadoutItem>();
        var existing = Items.Find(item => item.Id == id);
        if (existing == null)
        {
            Items.Add(new LoadoutItem(id, 1));
        }
        else
        {
            existing.Count = Math.Min(existing.Count + 1, LoadoutItem.MaxCount);
        }
    }

    /// <summary>Change an item's count, wrapping from the most to none and back.</summary>
    public static void StepCount(LoadoutItem item, int steps)
    {
        const int values = LoadoutItem.MaxCount + 1;
        item.Count = ((item.Count + steps) % values + values) % values;
    }

    /// <summary>Drop items whose count went to zero.</summary>
    public void RemoveEmptyItems() => Items?.RemoveAll(item => item.Count <= 0);

    /// <summary>The health that can actually be had: end game caps it at half.</summary>
    public int EffectiveHealth => EndGame ? Math.Min(Health, MaxHealth / 2) : Health;
}
