using System;
using System.Collections.Generic;
using System.Linq;
using CairnRouteMaker.Routes;
using Il2Cpp;
using Il2CppTGBTools.Localization;
using Il2CppTheGameBakers.Cairn;
using MelonLoader;

namespace CairnRouteMaker.Game;

/// <summary>One item a route can hand out, for the loadout page's picker.</summary>
internal sealed class LoadoutItemInfo
{
    public LoadoutItemInfo(string id, string name, string category, string note)
    {
        Id = id;
        Name = name;
        Category = category;
        Note = note;
    }

    /// <summary>The game's id, e.g. "ITEM_ENERGY_BAR".</summary>
    public string Id { get; }

    /// <summary>The name in the game's language.</summary>
    public string Name { get; }
    public string Category { get; }

    /// <summary>A word on how it's handed out, e.g. "full of water"; or null.</summary>
    public string Note { get; }
}

/// <summary>
/// Puts Ava in a route's <see cref="Loadout"/>: her survival stats, health, fingers, the end-game
/// mode, whether hunger, thirst and cold drain, and what she carries. See "Route loadouts" in
/// docs/game-internals.md for where the calls come from.
/// </summary>
internal static class AvaLoadout
{
    /// <summary>A finger counts as taped above the game's tape threshold (0.7), so this is just under it.</summary>
    private const float BareFingerHealth = 0.69f;

    /// <summary>Under the game's hurt threshold (0.25), but above 0, which would break the finger.</summary>
    private const float HurtFingerHealth = 0.1f;

    private const int FingersPerHand = 5;

    private static readonly Loadout Default = new();

    private static readonly SurvivalStat[] Needs = { SurvivalStat.Hunger, SurvivalStat.Thirst, SurvivalStat.Cold };

    /// <summary>Containers whose name says what they hold; any other liquid container comes full of water.</summary>
    private static readonly Dictionary<string, string> CanContents = new()
    {
        ["ITEM_CAN_WATER"] = "ITEM_WATER",
        ["ITEM_CAN_MILK"] = "ITEM_MILK",
        ["ITEM_CAN_ALPINE_COLA"] = "ITEM_ALPINE_COLA",
        ["ITEM_CAN_VIGORADE"] = "ITEM_VIGORADE",
        ["ITEM_CAN_RASPBERRY_JUICE"] = "ITEM_RASPBERRIES_JUICE",
        ["ITEM_CAN_MOONSHINE"] = "ITEM_ALC_MOONSHINE",
    };

    /// <summary>Items a route can't hand out: story pieces, test items, things the game manages itself, and pitons (a setting of their own).</summary>
    private static readonly HashSet<string> Hidden = new()
    {
        Loadout.PitonId, Loadout.CrystalPitonId, "ITEM_PUFF_UPGRADE", "ITEM_PUFF_STORAGE", "ITEM_CLIMBOT", "ITEM_SELF_DRINK_FROM_SOURCE", "ITEM_INTERACTION_TEST",
        "ITEM_CAN_TEST", "ITEM_AA_DEBUG_ALL", "ITEM_AA_DEBUG_ALL_COOKED", "ITEM_MAGIC_FROG_LICK", "ITEM_MAGIC_FROG_SECOND_LICK",
    };

    private static List<LoadoutItemInfo> _catalog;

    /// <summary>
    /// Put Ava in a loadout (null for the default: full health, taped fingers, no end game, no
    /// drain, inventory as it is). Survival bars are filled to whatever cap applies, critical
    /// timers stopped and each limb's climbing stamina reset, so every go starts the same.
    /// </summary>
    public static void Apply(Loadout loadout)
    {
        loadout ??= Default;
        if (!InFreeClimb)
        {
            return;
        }

        try
        {
            SetSurvivalNeeds(loadout.SurvivalNeeds);
            var data = SurvivalManager.Instance;
            if (data != null)
            {
                var before = DescribeStats(data);
                SetEndGame(data, loadout.EndGame);
                FillNeeds(data);
                // As restoring Ava always did; then health is set exactly.
                data.RefillStat(SurvivalStat.Exhaustion);
                SetHealth(data, loadout.Health);
                SetGrip(data, loadout.LeftHand, loadout.RightHand);
                MelonLogger.Msg($"Set Ava up for the route: {before} -> {DescribeStats(data)}.");
            }

            ResetStamina();
            if (loadout.Items != null || loadout.Pitons != null)
            {
                SetInventory(loadout);
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"Couldn't set Ava up for the route: {e}");
        }
    }

    /// <summary>
    /// The route on show changed: switch off end game and survival needs if the new one (or none)
    /// doesn't use them, so they don't linger past the route that asked for them. Turning them on
    /// waits until Ava is put on the new route's start.
    /// </summary>
    public static void LeaveModes(Loadout next)
    {
        next ??= Default;
        if (!InFreeClimb)
        {
            return;
        }

        try
        {
            if (!next.SurvivalNeeds)
            {
                SetSurvivalNeeds(false);
            }

            var data = SurvivalManager.Instance;
            if (!next.EndGame && data != null && InEndGame)
            {
                SetEndGame(data, false);
                FillNeeds(data);
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"Couldn't switch off the last route's end game or survival needs: {e.Message}");
        }
    }

    /// <summary>
    /// Only a free-climb game with Ava in it is ever changed: the route on show is also cleared when
    /// the player leaves for a story game, whose own end game mustn't be touched.
    /// </summary>
    private static bool InFreeClimb => GameApi.IsFreeClimbGame && GameApi.IsAvaReady;

    /// <summary>
    /// The game's items a route can hand out, sorted by category then name. Empty until Ava has
    /// been in a game; kept from then on, so the title screen's menu has it after the first one.
    /// </summary>
    public static IReadOnlyList<LoadoutItemInfo> Catalog()
    {
        if (_catalog != null)
        {
            return _catalog;
        }

        var result = new List<LoadoutItemInfo>();
        // Not from the title screen: asking for the inventory manager before a game might make one.
        if (!GameApi.IsAvaReady)
        {
            return result;
        }

        var manager = InventoryManager.Instance;
        var items = manager == null || manager.library == null ? null : manager.library.items;
        if (items == null)
        {
            return result;
        }

        foreach (var item in items)
        {
            try
            {
                if (item != null && Offered(item, out var id))
                {
                    result.Add(new LoadoutItemInfo(id, NameOf(item, id), CategoryName(item.debugCategory), NoteFor(item, id)));
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"Couldn't list an item for loadouts: {e.Message}");
            }
        }

        result.Sort((a, b) => a.Category == b.Category
            ? string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase)
            : CategoryOrder(a.Category).CompareTo(CategoryOrder(b.Category)));
        _catalog = result;
        MelonLogger.Msg($"Loadout items: {result.Count} of the game's {items.Length} items can be handed out.");
        return result;
    }

    /// <summary>An item's name in the game's language, or one made from its id if the game hasn't listed it.</summary>
    public static string ItemName(string id) =>
        Catalog().FirstOrDefault(info => info.Id == id)?.Name ?? PrettyId(id);

    /// <summary>An item's note from <see cref="Catalog"/>, e.g. how a meal was cooked; or null.</summary>
    public static string ItemNote(string id) => Catalog().FirstOrDefault(info => info.Id == id)?.Note;

    private static bool Offered(InventoryItem item, out string id)
    {
        id = ((InventoryItemStringIdEnum)item.id.value).ToString();
        if (Hidden.Contains(id) || id.Contains("TEST") || id.Contains("HALLU") || item.isPlayerStorySignificant || item.IsLiquid())
        {
            return false;
        }

        var stored = item.storedIn;
        if ((stored & (StorageType.Readables | StorageType.Charms | StorageType.Infinite | StorageType.Robot)) != 0)
        {
            return false;
        }

        if (item.debugCategory is ItemDebugCategory.MONEY or ItemDebugCategory.TRASH)
        {
            return false;
        }

        return item.Type is InventoryItemType.Consumable or InventoryItemType.Container or InventoryItemType.ClimbingGear;
    }

    private static string NameOf(InventoryItem item, string id)
    {
        try
        {
            var localization = LocalizationManager.Instance;
            var name = localization == null ? null : localization.Get(item.locKey);
            if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith("ITEM_", StringComparison.Ordinal))
            {
                return name.Trim();
            }
        }
        catch (Exception)
        {
            // Fall back to the id below.
        }

        return PrettyId(id);
    }

    /// <summary>"Energy bar" for "ITEM_ENERGY_BAR".</summary>
    private static string PrettyId(string id)
    {
        var words = (id.StartsWith("ITEM_", StringComparison.Ordinal) ? id[5..] : id).Replace('_', ' ').ToLowerInvariant();
        return words.Length == 0 ? id : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>
    /// How a meal or drink was made ("creamy", "herbal", ...), which sets it apart from others of the
    /// same name with other effects; or for a liquid container, that it comes full of water.
    /// </summary>
    private static string NoteFor(InventoryItem item, string id)
    {
        var consumable = item.TryCast<ConsumableItem>();
        if (consumable != null)
        {
            return consumable.modifier switch
            {
                ConsumableModifier.Creamy => "creamy",
                ConsumableModifier.ExtraSweet => "extra sweet",
                ConsumableModifier.Intense => "intense",
                ConsumableModifier.Herbal => "herbal",
                ConsumableModifier.Mixed => "mixed",
                ConsumableModifier.Flambeed => "flambéed",
                ConsumableModifier.Rare => "rare",
                _ => null,
            };
        }

        var container = item.TryCast<ContainerItem>();
        if (container == null || container.containerType != ContainerType.Liquid)
        {
            return null;
        }

        return CanContents.TryGetValue(id, out var content) && content != "ITEM_WATER" ? null : "full of water";
    }

    private static readonly ItemDebugCategory[] CategoryOrderList =
    {
        ItemDebugCategory.CLIMBINGGEAR, ItemDebugCategory.CLIMBINGTAPE, ItemDebugCategory.MEDECINE,
        ItemDebugCategory.DRINKBOTTLE, ItemDebugCategory.DRINKCAN, ItemDebugCategory.WATERSOURCE, ItemDebugCategory.DRINKS,
        ItemDebugCategory.DRINKPOWDER, ItemDebugCategory.ALCOHOLDRINKS, ItemDebugCategory.INDUSTRIALFOOD, ItemDebugCategory.JUNKFOOD,
        ItemDebugCategory.COOKEDMEAL, ItemDebugCategory.FRUITS, ItemDebugCategory.PLANTS, ItemDebugCategory.EGGSANDFISH,
        ItemDebugCategory.OTHER, ItemDebugCategory.NONE,
    };

    private static string CategoryName(ItemDebugCategory category) => category switch
    {
        ItemDebugCategory.CLIMBINGGEAR => "Climbing gear",
        ItemDebugCategory.CLIMBINGTAPE => "Climbing tape",
        ItemDebugCategory.MEDECINE => "Medicine",
        ItemDebugCategory.DRINKBOTTLE => "Bottles and flasks",
        ItemDebugCategory.DRINKCAN => "Cans",
        ItemDebugCategory.WATERSOURCE => "Water",
        ItemDebugCategory.DRINKS => "Drinks",
        ItemDebugCategory.DRINKPOWDER => "Drink powders",
        ItemDebugCategory.ALCOHOLDRINKS => "Alcohol",
        ItemDebugCategory.INDUSTRIALFOOD => "Packaged food",
        ItemDebugCategory.JUNKFOOD => "Snacks",
        ItemDebugCategory.COOKEDMEAL => "Cooked meals",
        ItemDebugCategory.FRUITS => "Fruit",
        ItemDebugCategory.PLANTS => "Plants",
        ItemDebugCategory.EGGSANDFISH => "Eggs and fish",
        _ => "Other",
    };

    private static int CategoryOrder(string name)
    {
        var index = Array.FindIndex(CategoryOrderList, category => CategoryName(category) == name);
        return index < 0 ? CategoryOrderList.Length : index;
    }

    /// <summary>
    /// Free climb starts with hunger, thirst and cold switched off (NoPhysiologicalNeeds). The game
    /// reads the running setup's rules live, so flipping the flag starts or stops the drain.
    /// </summary>
    private static void SetSurvivalNeeds(bool on)
    {
        var scenes = CairnSceneManager.Instance;
        var setup = scenes.GameSetup;
        var wasOn = (setup.constraints & GamemodeConstraints.NoPhysiologicalNeeds) == 0;
        if (wasOn == on)
        {
            return;
        }

        setup.constraints = on
            ? setup.constraints & ~GamemodeConstraints.NoPhysiologicalNeeds
            : setup.constraints | GamemodeConstraints.NoPhysiologicalNeeds;
        // GameSetup is a struct: this is a copy, so it has to be stored back.
        scenes.GameSetup = setup;
        MelonLogger.Msg($"Survival needs {(on ? "on: hunger, thirst and cold drain" : "off")} ({GameApi.DescribeGameSetup()}).");
    }

    /// <summary>
    /// The game's end game, without its 10-second animation (which pauses survival and game time
    /// while it plays): set the flag, show the HUD's caps fully drawn, and let the stats settle
    /// under the caps in <see cref="FillNeeds"/> and <see cref="SetHealth"/>.
    /// </summary>
    private static void SetEndGame(SurvivalManager data, bool on)
    {
        var story = StoryEventManager.Instance;
        if (story == null || story.InEndGame == on)
        {
            return;
        }

        if (data.IsAnimatingEndGame)
        {
            MelonLogger.Warning("The game's own end-game animation is playing; the loadout's end game may not take.");
        }

        // Only sets the flag: the animation is SurvivalManager's, started by the story's event.
        story.SetIsInEndGame(on);
        data.EndGameAnimationAmount = on ? 1f : 0f;
        MelonLogger.Msg(story.InEndGame == on
            ? $"End game {(on ? "on" : "off")}."
            : $"Tried to switch end game {(on ? "on" : "off")}, but it's still {(story.InEndGame ? "on" : "off")}.");
    }

    /// <summary>The story's end-game flag (or summit end), which caps Ava's stats.</summary>
    private static bool InEndGame
    {
        get
        {
            var story = StoryEventManager.Instance;
            return story != null && story.InEndGame;
        }
    }

    /// <summary>
    /// Hunger, thirst and cold to full, or to the end game's cap. RefillStat ignores the cap, so
    /// ModifyStat by nothing then clamps them down to it.
    /// </summary>
    private static void FillNeeds(SurvivalManager data)
    {
        var state = data.survivalState;
        foreach (var stat in Needs)
        {
            data.RefillStat(stat);
            if (InEndGame)
            {
                data.ModifyStat(stat, 0f, false);
            }

            state?.EndCriticalStatTimer(stat);
        }

        if (state != null)
        {
            state.EndCriticalStatTimer(SurvivalStat.Hp);
            state.EndCriticalStatTimer(SurvivalStat.Exhaustion);
        }
    }

    /// <summary>Health as a percentage of Ava's full health, no higher than the current cap (half in end game).</summary>
    private static void SetHealth(SurvivalManager data, int percent)
    {
        var hp = Math.Min(data.AbsoluteMaxHp * percent / 100f, data.CurrentMaxHp);
        // At 1 HP or less the next knock kills her, and 0 kills her outright.
        data.SetCurrentHp(Math.Max(hp, 2f));
    }

    /// <summary>
    /// Each hand's fingers to one health: taped is full health (anything above 0.7 shows tape),
    /// bare just under the tape, hurt under the game's hurt threshold. Fingers 0-4 are the left hand.
    /// </summary>
    private static void SetGrip(SurvivalManager data, Grip left, Grip right)
    {
        var fingers = data.fingersState == null ? null : data.fingersState.fingerHP01;
        for (var i = 0; fingers != null && i < fingers.Length; i++)
        {
            // Ignore the cooldown after a broken finger, and set the health exactly.
            data.SetFingerHealth01(i, FingerHealth(i < FingersPerHand ? left : right), true, false);
        }
    }

    private static float FingerHealth(Grip grip) => grip switch
    {
        Grip.Bare => BareFingerHealth,
        Grip.Hurt => HurtFingerHealth,
        _ => 1f,
    };

    private static void ResetStamina()
    {
        var climbing = GameApi.Switcher == null ? null : GameApi.Switcher.climbingPawnController;
        if (climbing == null)
        {
            return;
        }

        foreach (var limb in climbing.limbs)
        {
            limb?.ResetStamina();
        }
    }

    /// <summary>
    /// Empty what the loadout sets from everything Ava carries (bag, chalk bag, harness and the
    /// Climbot's storage): her pitons if it sets them, everything else if it sets items. Then give
    /// her the route's pitons and items. Custom items (the light stick, the barometer and other
    /// things the game hands out itself) stay: the game can't take them away. The flask, charms,
    /// readables and the infinite store aren't in these inventories, so they aren't touched.
    /// </summary>
    private static void SetInventory(Loadout loadout)
    {
        var manager = InventoryManager.Instance;
        if (manager == null)
        {
            MelonLogger.Warning("No inventory to fill: the inventory manager isn't there.");
            return;
        }

        bool Replaced(InventoryItem item) => IsPiton(item) ? loadout.Pitons != null : loadout.Items != null;
        var removed = 0;
        removed += Empty(manager.BagInventoryData, Replaced);
        removed += Empty(manager.PuffInventoryData, Replaced);
        removed += Empty(manager.PitonInventoryData, Replaced);
        removed += Empty(manager.ClimbotInventoryData, Replaced);

        var given = new List<string>();
        var gifts = new List<LoadoutItem>();
        if (loadout.Pitons is { } pitons)
        {
            gifts.Add(new LoadoutItem(Loadout.PitonId, pitons - loadout.CrystalPitons));
            gifts.Add(new LoadoutItem(Loadout.CrystalPitonId, loadout.CrystalPitons));
        }

        gifts.AddRange(loadout.Items ?? new List<LoadoutItem>());
        foreach (var item in gifts)
        {
            if (item.Count > 0 && Give(manager, item) is { } result)
            {
                given.Add(result);
            }
        }

        MelonLogger.Msg($"Inventory: took {removed} stack{(removed == 1 ? "" : "s")} away, gave {(given.Count == 0 ? "nothing" : string.Join(", ", given))}.");
    }

    private static bool IsPiton(InventoryItem item) =>
        ((InventoryItemStringIdEnum)item.id.value).ToString() is Loadout.PitonId or Loadout.CrystalPitonId;

    /// <summary>
    /// Take the items <paramref name="replaced"/> picks out of an inventory, without leftovers,
    /// except custom ones. Returns how many stacks went.
    /// </summary>
    private static int Empty(InventoryDataBase inventory, Func<InventoryItem, bool> replaced)
    {
        if (inventory == null)
        {
            return 0;
        }

        // Snapshot first: removing changes the indices.
        var stacks = new List<InventoryItemData>();
        var bag = inventory.TryCast<BagInventoryData>();
        for (var i = 0; i < inventory.Count; i++)
        {
            var data = bag != null ? bag.GetBagItemData(i)?.ItemData : inventory[i]?.TryCast<InventoryItemData>();
            if (data != null)
            {
                stacks.Add(data);
            }
        }

        var removed = 0;
        foreach (var data in stacks)
        {
            var item = data.InventoryItem;
            if (item == null || item.Type == InventoryItemType.Custom || !replaced(item))
            {
                continue;
            }

            // The inventory's own Remove, not the item's: that one makes leftovers (an empty
            // wrapper, an empty bottle) and throws for custom items.
            inventory.Remove(item, data);
            removed++;
        }

        return removed;
    }

    /// <summary>Add one of the route's items. Returns what was given, for the log, or null if nothing was.</summary>
    private static string Give(InventoryManager manager, LoadoutItem entry)
    {
        if (!Enum.TryParse<InventoryItemStringIdEnum>(entry.Id, true, out var parsed))
        {
            MelonLogger.Warning($"Unknown item {entry.Id}: this version of the game doesn't have it.");
            return null;
        }

        var id = new InventoryItemStringId((int)parsed);
        var item = manager.GetItem(id);
        if (item == null)
        {
            MelonLogger.Warning($"The game has no item {entry.Id}.");
            return null;
        }

        // The request the game's own "give item" debug command makes, without its toast. Pitons
        // and chalk count single units, as the quick wheel shows them (pitons at full health; one
        // whole chalk is three puffs). Everything else counts whole items, liquid containers full,
        // which is what PutItemsWhole does. Only the overload that returns the response is safe:
        // the interop passes the out overloads an 8-byte slot for the whole response struct, and
        // the game crashes writing it.
        var request = new InventoryStorageRequest(id, entry.Count, ContentFor(item, entry.Id), 0UL, LootTarget.Aava,
            BagSearchMode.BottomUp, false, false, default(InventoryStorageRequest.Metadata));
        if (item.Type != InventoryItemType.ClimbingGear && (item.storedIn & StorageType.Puff) == 0)
        {
            request = request.WholeItemRequestToPartsRequest();
        }

        var missing = MissingParts(manager.PutItemParts(request));
        return missing > 0 ? $"{entry.Id} x{entry.Count} (didn't all fit)" : $"{entry.Id} x{entry.Count}";
    }

    /// <summary>What a liquid container comes filled with: its drink for a can, else water. Nothing for other items.</summary>
    private static InventoryItemStringId ContentFor(InventoryItem item, string id)
    {
        var container = item.TryCast<ContainerItem>();
        if (container == null || container.containerType != ContainerType.Liquid)
        {
            return default;
        }

        var content = CanContents.TryGetValue(id, out var drink) ? drink : "ITEM_WATER";
        return Enum.TryParse<InventoryItemStringIdEnum>(content, out var parsed) ? new InventoryItemStringId((int)parsed) : default;
    }

    private static int MissingParts(InventoryStorageRequestResponse response)
    {
        try
        {
            return response == null ? 0 : response.MissingParts;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string DescribeStats(SurvivalManager data)
    {
        var parts = new List<string>();
        foreach (var stat in Needs)
        {
            parts.Add($"{stat} {data.GetStat(stat):0.#}");
        }

        parts.Add($"HP {data.CurrentHp:0.#}/{data.CurrentMaxHp:0.#}");
        if (InEndGame)
        {
            parts.Add("end game");
        }

        if (data.fingersState != null)
        {
            parts.Add($"fingers L {data.GetAverageFingersHealth01PerHand(false):0.##} R {data.GetAverageFingersHealth01PerHand(true):0.##}");
        }

        return string.Join(", ", parts);
    }
}
