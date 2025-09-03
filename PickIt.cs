using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared;
using ExileCore2.Shared.Cache;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Helpers;
using ImGuiNET;
using ItemFilterLibrary;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore2.PoEMemory;
using RectangleF = ExileCore2.Shared.RectangleF;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using System.Collections.Concurrent;

namespace PickIt;

public partial class PickIt : BaseSettingsPlugin<PickItSettings>
{
    private readonly CachedValue<List<LabelOnGround>> _chestLabels;
    private readonly CachedValue<List<LabelOnGround>> _doorLabels;
    private readonly CachedValue<List<LabelOnGround>> _portalLabels;
    private readonly CachedValue<LabelOnGround> _transitionLabel;
    private readonly CachedValue<List<LabelOnGround>> _corpseLabels;
    private readonly CachedValue<List<LabelOnGround>> _shrineLabels;
    private readonly CachedValue<bool[,]> _inventorySlotsCache;
    private ServerInventory _inventoryItems;
    private SyncTask<bool> _pickUpTask;
    private bool _isCurrentlyPicking;
    private long _lastClick;
    private List<ItemFilter> _itemFilters;
    private bool _pluginBridgeModeOverride;
    private bool[,] InventorySlots => _inventorySlotsCache.Value;
    private readonly Stopwatch _sinceLastClick = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<string, Regex> _labelRegexCache = new();
    private Element UIHoverWithFallback =>
        GameController?.IngameState?.UIHover is { Address: > 0 } s
            ? s
            : GameController?.IngameState?.UIHoverElement;
    private bool OkayToClick => _sinceLastClick.ElapsedMilliseconds > Settings.PauseBetweenClicks;

    // Debug helpers gated by profiler hotkey
    private bool DebugOn = false;
    private void Debug(string message)
    {
        if (DebugOn)
            DebugWindow.LogMsg($"[PickItDbg] {message}");
    }

    // DebugScanMiscEnvironment removed
    private void DebugScanMiscEnvironment() { }

    private static bool IsDoorEntity(Entity entity)
    {
        var path = entity?.Path;
        return path is { } p && (p.Contains("DoorRandom", StringComparison.Ordinal) || p.Contains("Door", StringComparison.Ordinal) || p.Contains("Endgame/TowerCompletion", StringComparison.Ordinal) || p.Contains("WaterLevelLever", StringComparison.Ordinal));
    }

    private static bool IsChestEntity(Entity entity)
    {
        var path = entity?.Path;
        return path is { } p && (p.StartsWith("Metadata/Chests", StringComparison.Ordinal) || p.Contains("CampsiteChest", StringComparison.Ordinal));
    }

    private static bool IsPortalEntity(Entity entity)
    {
        if (entity == null) return false;
        var path = entity.Path;
        if (path is { } p && p.StartsWith("Metadata/MiscellaneousObjects/Portal", StringComparison.Ordinal)) return true;
        return entity.HasComponent<Portal>();
    }

    private static bool IsShrineEntity(Entity entity)
    {
        var path = entity?.Path;
        return path is { } p && p.Contains("Shrine", StringComparison.Ordinal);
    }

    private bool TryGetEntityScreenCenter(Entity entity, out Vector2 screen)
    {
        screen = default;
        try
        {
            if (entity == null) return false;
            var render = entity.GetComponent<Render>();
            var world = render?.Pos ?? entity.Pos;
            var camera = GameController?.IngameState?.Camera;
            if (camera == null) return false;
            var v = camera.WorldToScreen(world);
            screen = v;
            return true;
        }
        catch
        {
            return false;
        }
    }


    public PickIt()
    {
        _inventorySlotsCache = new FrameCache<bool[,]>(() => GetContainer2DArray(_inventoryItems));
        _chestLabels = new TimeCache<List<LabelOnGround>>(UpdateChestList, 200);
        _doorLabels = new TimeCache<List<LabelOnGround>>(UpdateDoorList, 200);
        _corpseLabels = new TimeCache<List<LabelOnGround>>(UpdateCorpseList, 200);
        _portalLabels = new TimeCache<List<LabelOnGround>>(UpdatePortalList, 200);
        _transitionLabel = new TimeCache<LabelOnGround>(() => GetLabel(@"Metadata/MiscellaneousObjects/AreaTransition_Animate"), 200);
        _shrineLabels = new TimeCache<List<LabelOnGround>>(UpdateShrineList, 200);
    }

    public override bool Initialise()
    {
        #region Register keys

        Settings.PickUpKey.OnValueChanged += () => Input.RegisterKey(Settings.PickUpKey);
        Settings.ProfilerHotkey.OnValueChanged += () => Input.RegisterKey(Settings.ProfilerHotkey);

        Input.RegisterKey(Settings.PickUpKey);
        Input.RegisterKey(Settings.ProfilerHotkey);
        Input.RegisterKey(Keys.Escape);

        #endregion

        Settings.ReloadFilters.OnPressed = LoadRuleFiles;
        LoadRuleFiles();
        GameController.PluginBridge.SaveMethod("PickIt.ListItems", () => GetItemsToPickup(false).Select(x => x.QueriedItem).ToList());
        GameController.PluginBridge.SaveMethod("PickIt.IsActive", () => _pickUpTask?.GetAwaiter().IsCompleted == false && _isCurrentlyPicking);
        GameController.PluginBridge.SaveMethod("PickIt.SetWorkMode", (bool running) => { _pluginBridgeModeOverride = running; });
        return true;
    }

    private enum WorkMode
    {
        Stop,
        Lazy,
        Manual
    }

    private WorkMode GetWorkMode()
    {
        if (!GameController.Window.IsForeground() ||
            !Settings.Enable ||
            Input.GetKeyState(Keys.Escape))
        {
            _pluginBridgeModeOverride = false;
            return WorkMode.Stop;
        }


        if (Input.GetKeyState(Settings.PickUpKey.Value) || _pluginBridgeModeOverride)
        {
            return WorkMode.Manual;
        }

        if (CanLazyLoot())
        {
            return WorkMode.Lazy;
        }

        return WorkMode.Stop;
    }

    private DateTime DisableLazyLootingTill { get; set; }

    public override void Tick()
    {
        var playerInvCount = GameController?.Game?.IngameState?.Data?.ServerData?.PlayerInventories?.Count;
        if (playerInvCount is null or 0)
            return;

        if (Settings.AutoClickHoveredLootInRange.Value)
        {
            var hoverElement = UIHoverWithFallback;
            var hoverItemIcon = hoverElement?.AsObject<HoverItemIcon>();
            if (hoverItemIcon != null && GameController?.IngameState?.IngameUi?.InventoryPanel is { IsVisible: false } &&
                !Input.IsKeyDown(Keys.LButton))
            {
                if (hoverItemIcon.Item != null && OkayToClick)
                {
                    var groundItem =
                        GameController?.IngameState?.IngameUi?.ItemsOnGroundLabels?
                            .FirstOrDefault(e => e.Label.Address == hoverItemIcon.Address);
                    if (groundItem != null)
                    {
                        var doWePickThis = Settings.PickUpEverything || (_itemFilters?.Any(filter =>
                            filter.Matches(new ItemData(groundItem, GameController))) ?? false);
                        var distance = groundItem?.ItemOnGround.DistancePlayer ?? float.MaxValue;
                        if (Input.GetKeyState(Settings.ProfilerHotkey.Value))
                        {
                            DebugWindow.LogMsg($"HoverClick check: dist={distance:F1}, range={Settings.ItemPickitRange}, match={doWePickThis}, ok={OkayToClick}");
                        }
                        if (doWePickThis && distance <= Settings.ItemPickitRange)
                        {
                            _sinceLastClick.Restart();
                            Input.Click(MouseButtons.Left);
                        }
                    }
                }
            }
        }

        var inventories = GameController?.Game?.IngameState?.Data?.ServerData?.PlayerInventories;
        _inventoryItems = inventories != null && inventories.Count > 0 && inventories[0] != null
            ? inventories[0].Inventory
            : null;
        DrawIgnoredCellsSettings();
        if (Input.GetKeyState(Settings.LazyLootingPauseKey)) DisableLazyLootingTill = DateTime.Now.AddSeconds(2);
        
        return;
    }

    public override void Render()
    {
        if (Settings.DebugHighlight)
        {
            foreach (var item in GetItemsToPickup(false))
            {
                Graphics.DrawFrame(item.QueriedItem.ClientRect, Color.Violet, 5);
            }
            foreach (var door in _doorLabels.Value)
            {
                Graphics.DrawFrame(door.Label.GetClientRect(), Color.Violet, 5);
            }
            foreach (var chest in _chestLabels.Value)
            {
                Graphics.DrawFrame(chest.Label.GetClientRect(), Color.Violet, 5);
            }
        }


        if (GetWorkMode() != WorkMode.Stop)
        {
            TaskUtils.RunOrRestart(ref _pickUpTask, RunPickerIterationAsync);
        }
        else
        {
            _pickUpTask = null;
        }

        if (_pickUpTask?.GetAwaiter().IsCompleted != false)
        {
            _isCurrentlyPicking = false;
        }

    }

    //TODO: Make function pretty
    private void DrawIgnoredCellsSettings()
    {
        if (!Settings.ShowInventoryView.Value)
            return;

        var opened = true;

        const ImGuiWindowFlags nonMoveableFlag = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground |
                                                 ImGuiWindowFlags.NoTitleBar |
                                                 ImGuiWindowFlags.NoInputs |
                                                 ImGuiWindowFlags.NoFocusOnAppearing;

        ImGui.SetNextWindowPos(Settings.InventoryPos.Value);
        if (ImGui.Begin($"{Name}##InventoryCellMap", ref opened,nonMoveableFlag))
        {
            var slots = InventorySlots;
            if (slots == null || slots.GetLength(0) < 5 || slots.GetLength(1) < 12)
            {
                ImGui.End();
                return;
            }
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0,0));

            var numb = 0;
            for (var i = 0; i < 5; i++)
            for (var j = 0; j < 12; j++)
            {
                var toggled = Convert.ToBoolean(slots[i, j]);
                if (ImGui.Checkbox($"##{numb}IgnoredCells", ref toggled)) slots[i, j] = toggled;

                if (j != 11) ImGui.SameLine();

                numb += 1;
            }

            ImGui.PopStyleVar(2);

            ImGui.End();
        }
    }

    private bool IsItSafeToPickit()
    {
        if (!Settings.NoLootingWhileEnemyClose)
            return true;

        var wrapper = GameController?.EntityListWrapper;
        var player = GameController?.Player;
        if (wrapper?.ValidEntitiesByType == null || player == null)
            return true;

        try
        {
            var monsters = wrapper.ValidEntitiesByType[EntityType.Monster];
            var playerPos = player.Pos;
            var near = monsters.FirstOrDefault(x => x?.GetComponent<Monster>() != null && x.IsValid && x.IsHostile && x.IsAlive
                                  && !x.IsHidden && x.Path != null && !x.Path.Contains("ElementalSummoned")
                                  && x.GetComponent<Render>() is { } render
                                  && Vector3.Distance(playerPos, render.Pos) <= Settings.ItemPickitRange);
            if (near != null)
            {
                try
                {
                    var render = near.GetComponent<Render>();
                    var dist = Vector3.Distance(playerPos, render.Pos);
                    Debug($"IsItSafeToPickit=false: hostile near, path={near.Path}, dist={dist:F1}, range={Settings.ItemPickitRange}");
                }
                catch
                {
                }
                return false;
            }
        }
        catch (Exception)
        {
            // Swallow and consider it safe if any transient null/state issue occurs
        }
        return true;
    }

    private bool DoWePickThis(PickItItemData item)
    {
        if (!IsItSafeToPickit())
            return false;
        else
            return Settings.PickUpEverything || (_itemFilters?.Any(filter => filter.Matches(item)) ?? false);
    }

    private List<LabelOnGround> UpdateChestList()
    {
        bool IsFittingEntity(Entity entity)
        {
            return entity?.Path is { } path &&
                   (path.StartsWith("Metadata/Chests", StringComparison.Ordinal) ||
                   path.Contains("CampsiteChest", StringComparison.Ordinal)) &&
                   entity.HasComponent<Chest>();
        }

        if (!IsItSafeToPickit())
            return [];

        var ui = GameController?.Game?.IngameState?.IngameUi;
        var visible = ui?.ItemsOnGroundLabelsVisible;
        if (visible != null)
        {
            return visible
                .Where(x => x.Address != 0 && x.IsVisible && IsFittingEntity(x.ItemOnGround))
                .OrderBy(x => x.ItemOnGround.DistancePlayer)
                .ToList() ?? [];
        }

        return ui?.ItemsOnGroundLabels
            .Where(x => x.Address != 0 && IsFittingEntity(x.ItemOnGround))
            .OrderBy(x => x.ItemOnGround.DistancePlayer)
            .ToList() ?? [];
    }

    private List<LabelOnGround> UpdateDoorList()
    {
        bool IsFittingEntity(Entity entity)
        {
            return entity?.Path is { } path && (
                    path.Contains("DoorRandom", StringComparison.Ordinal) ||
                    path.Contains("Door", StringComparison.Ordinal) ||
                    path.Contains("Endgame/TowerCompletion", StringComparison.Ordinal) ||
                    path.Contains("WaterLevelLever", StringComparison.Ordinal));
        }

        if (!IsItSafeToPickit())
            return [];

        var ui = GameController?.Game?.IngameState?.IngameUi;
        var visible = ui?.ItemsOnGroundLabelsVisible;
        if (visible != null)
        {
            return visible
                .Where(x => x.Address != 0 && x.IsVisible && IsFittingEntity(x.ItemOnGround))
                .OrderBy(x => x.ItemOnGround.DistancePlayer)
                .ToList() ?? [];
        }

        return ui?.ItemsOnGroundLabels
            .Where(x => x.Address != 0 && IsFittingEntity(x.ItemOnGround))
            .OrderBy(x => x.ItemOnGround.DistancePlayer)
            .ToList() ?? [];
    }

    private List<LabelOnGround> UpdateCorpseList()
    {
        bool IsFittingEntity(Entity entity)
        {
            return entity?.Path is "Metadata/Terrain/Leagues/Necropolis/Objects/NecropolisCorpseMarker";
        }

        if (!IsItSafeToPickit())
            return [];

        var ui = GameController?.Game?.IngameState?.IngameUi;
        var visible = ui?.ItemsOnGroundLabelsVisible;
        if (visible != null)
        {
            return visible
                .Where(x => x.Address != 0 && x.IsVisible && IsFittingEntity(x.ItemOnGround))
                .OrderBy(x => x.ItemOnGround.DistancePlayer)
                .ToList() ?? [];
        }

        return ui?.ItemsOnGroundLabels
            .Where(x => x.Address != 0 && IsFittingEntity(x.ItemOnGround))
            .OrderBy(x => x.ItemOnGround.DistancePlayer)
            .ToList() ?? [];
    }

    private List<LabelOnGround> UpdatePortalList()
    {
        bool IsFittingEntity(Entity entity)
        {
            if (entity == null)
                return false;

            if (entity.Path is { } path && path.StartsWith("Metadata/MiscellaneousObjects/Portal", StringComparison.Ordinal))
                return true;

            return entity.HasComponent<Portal>();
        }

        if (!IsItSafeToPickit())
            return [];

        var ui = GameController?.Game?.IngameState?.IngameUi;
        var visible = ui?.ItemsOnGroundLabelsVisible;
        if (visible != null)
        {
            return visible
                .Where(x => x.Address != 0 && x.IsVisible && IsFittingEntity(x.ItemOnGround))
                .OrderBy(x => x.ItemOnGround.DistancePlayer)
                .ToList() ?? [];
        }

        return ui?.ItemsOnGroundLabels
            .Where(x => x.Address != 0 && IsFittingEntity(x.ItemOnGround))
            .OrderBy(x => x.ItemOnGround.DistancePlayer)
            .ToList() ?? [];
    }

    private List<LabelOnGround> UpdateShrineList()
    {
        bool IsFittingEntity(Entity entity)
        {
            return entity?.Path is { } path && path.Contains("Shrine", StringComparison.Ordinal);
        }

        if (!IsItSafeToPickit())
            return [];

        var ui = GameController?.Game?.IngameState?.IngameUi;
        var visible = ui?.ItemsOnGroundLabelsVisible;
        if (visible != null)
        {
            return visible
                .Where(x => x.Address != 0 && x.IsVisible && IsFittingEntity(x.ItemOnGround))
                .OrderBy(x => x.ItemOnGround.DistancePlayer)
                .ToList() ?? [];
        }

        return ui?.ItemsOnGroundLabels
            .Where(x => x.Address != 0 && IsFittingEntity(x.ItemOnGround))
            .OrderBy(x => x.ItemOnGround.DistancePlayer)
            .ToList() ?? [];
    }

    private bool CanLazyLoot()
    {
        if (!Settings.LazyLooting) return false;
        if (DisableLazyLootingTill > DateTime.Now) return false;
        try
        {
            if (Settings.NoLazyLootingWhileEnemyClose)
            {
                var wrapper = GameController?.EntityListWrapper;
                var player = GameController?.Player;
                if (wrapper?.ValidEntitiesByType != null && player != null)
                {
                    var playerPos = player.Pos;
                    var monsters = wrapper.ValidEntitiesByType[EntityType.Monster];
                    if (monsters.Any(x => x?.GetComponent<Monster>() != null && x.IsValid && x.IsHostile && x.IsAlive
                                          && !x.IsHidden && x.Path != null && !x.Path.Contains("ElementalSummoned")
                                          && x.GetComponent<Render>() is { } render
                                          && Vector3.Distance(playerPos, render.Pos) < Settings.ItemPickitRange))
                        return false;
                }
            }
        }
        catch (NullReferenceException)
        {
        }

        return true;
    }

    private bool ShouldLazyLoot(PickItItemData item)
    {
        if (!Settings.LazyLooting)
            return false;

        if (Settings.LazyLooting && Settings.MiscPickit && Settings.ClickDoors && _doorLabels != null)
        {
            var doorLabel = _doorLabels?.Value?.FirstOrDefault(x =>
                x.ItemOnGround.DistancePlayer <= Settings.MiscPickitRange &&
                IsLabelClickable(x.Label, null));
            if (doorLabel != null)
            {
                return true;
            }
        }

        if (item == null)
            return false;

        var itemPos = item.QueriedItem.Entity.Pos;
        var playerPos = GameController.Player.Pos;
        return Math.Abs(itemPos.Z - playerPos.Z) <= 50 &&
               itemPos.Xy().DistanceSquared(playerPos.Xy()) <= 275 * 275;
    }

    private bool ShouldLazyLootMisc(LabelOnGround label)
    {
        if (!Settings.LazyLooting && !Settings.MiscPickit)
            return false;

        if (label?.ItemOnGround == null)
            return false;

        var itemPos = label.ItemOnGround.Pos;
        var playerPos = GameController.Player.Pos;
        return Math.Abs(itemPos.Z - playerPos.Z) <= 50 &&
               itemPos.Xy().DistanceSquared(playerPos.Xy()) <= 275 * 275;
    }

    private bool IsLabelClickable(Element element, RectangleF? customRect)
    {
        if (element is not { IsValid: true, IsVisible: true, IndexInParent: not null })
        {
            return false;
        }

        var rect = customRect ?? element.GetClientRect();
        var center = rect.Center;

        var gameWindowRect = GameController.Window.GetWindowRectangleTimeCache with { Location = Vector2.Zero };
        gameWindowRect.Inflate(-36, -36);
        var containsCenter = gameWindowRect.Contains(center.X, center.Y);

        if (!containsCenter)
        {
            // Consider partially visible labels clickable if their rect intersects the game window rect.
            var rectRight = rect.X + rect.Width;
            var rectBottom = rect.Y + rect.Height;
            var winRight = gameWindowRect.X + gameWindowRect.Width;
            var winBottom = gameWindowRect.Y + gameWindowRect.Height;
            var intersects = rect.X < winRight && rectRight > gameWindowRect.X && rect.Y < winBottom && rectBottom > gameWindowRect.Y;
            if (DebugOn)
            {
                Debug($"IsLabelClickable={intersects}: center={center}, rect=({rect.X:F0},{rect.Y:F0},{rect.Width:F0},{rect.Height:F0}), gameRect=({gameWindowRect.X:F0},{gameWindowRect.Y:F0},{gameWindowRect.Width:F0},{gameWindowRect.Height:F0}), containsCenter={containsCenter}");
            }
            return intersects;
        }

        return true;
    }

    private LabelOnGround GetLabel(string id)
    {
        var labels = GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels;
        if (labels == null)
        {
            return null;
        }

        var regex = _labelRegexCache.GetOrAdd(id, key => new Regex(key, RegexOptions.Compiled));
        var labelQuery =
            from labelOnGround in labels
            where labelOnGround?.Label is { IsValid: true, Address: > 0, IsVisible: true }
            let itemOnGround = labelOnGround.ItemOnGround
            where itemOnGround?.Metadata is { } metadata && regex.IsMatch(metadata)
            let dist = GameController?.Player?.GridPos.DistanceSquared(itemOnGround.GridPos)
            orderby dist
            select labelOnGround;

        return labelQuery.FirstOrDefault();
    }


    #region (Re)Loading Rules

    private void LoadRuleFiles()
    {
        var pickitConfigFileDirectory = ConfigDirectory;
        var existingRules = Settings.PickitRules ?? new List<PickitRule>();

        if (!string.IsNullOrEmpty(Settings.CustomConfigDir))
        {
            var customConfigFileDirectory = Path.Combine(Path.GetDirectoryName(ConfigDirectory), Settings.CustomConfigDir);

            if (Directory.Exists(customConfigFileDirectory))
            {
                pickitConfigFileDirectory = customConfigFileDirectory;
            }
            else
            {
                DebugWindow.LogError("[Pickit] custom config folder does not exist.", 15);
            }
        }

        try
        {
            var newRules = new DirectoryInfo(pickitConfigFileDirectory).GetFiles("*.ifl")
                .Select(x => new PickitRule(x.Name, Path.GetRelativePath(pickitConfigFileDirectory, x.FullName), false))
                .ExceptBy(existingRules.Select(x => x.Location), x => x.Location)
                .ToList();
            foreach (var groundRule in existingRules)
            {
                var fullPath = Path.Combine(pickitConfigFileDirectory, groundRule.Location);
                if (File.Exists(fullPath))
                {
                    newRules.Add(groundRule);
                }
                else
                {
                    LogError($"File '{groundRule.Name}' not found.");
                }
            }

            _itemFilters = newRules
                .Where(rule => rule.Enabled)
                .Select(rule => ItemFilter.LoadFromPath(Path.Combine(pickitConfigFileDirectory, rule.Location)))
                .ToList();

            Settings.PickitRules = newRules;
        }
        catch (Exception ex)
        {
            LogError($"[Pickit] Error loading filters: {ex}.", 15);
        }
    }


    private async SyncTask<bool> RunPickerIterationAsync()
    {
        if (!GameController.Window.IsForeground()) return true;

        // If auto-click-on-hover is enabled and we currently have a hovered item icon,
        // let the lightweight tick-path handle the click to avoid fighting over the cursor.
        if (Settings.AutoClickHoveredLootInRange.Value)
        {
            var hoverElement = UIHoverWithFallback;
            var hoverItemIcon = hoverElement?.AsObject<HoverItemIcon>();
            if (hoverItemIcon != null && GameController?.IngameState?.IngameUi?.InventoryPanel is { IsVisible: false })
                return true;
        }



        // Hover/highlight fallbacks removed; rely solely on LabelOnGround.Entity data

        var pickUpThisItem = GetItemsToPickup(true).FirstOrDefault();
        var workMode = GetWorkMode();
        Debug($"RunPickerIteration: mode={workMode}, item={(pickUpThisItem?.BaseName ?? "<none>")}, dist={(pickUpThisItem?.Distance is {} d ? d.ToString("F1") : "-")}");
        if (workMode == WorkMode.Manual || workMode == WorkMode.Lazy && (ShouldLazyLoot(pickUpThisItem) ||
            ShouldLazyLootMisc(_portalLabels.Value.FirstOrDefault()) ||
            ShouldLazyLootMisc(_transitionLabel.Value) ||
            ShouldLazyLootMisc(_shrineLabels.Value.FirstOrDefault()) ||
            ShouldLazyLootMisc(_chestLabels.Value.FirstOrDefault())))
        {
            if (Settings.ClickCorpses && Settings.MiscPickit)
            {
                if (GameController.Area?.CurrentArea is { IsHideout: true } or { IsTown: true })
                {
                    Debug("Skip corpses: in town/hideout");
                    return false;
                }

                var corpseLabel = _corpseLabels?.Value.FirstOrDefault(x =>
                    x.ItemOnGround.DistancePlayer <= Settings.MiscPickitRange &&
                    IsLabelClickable(x.Label, null));

                Debug(corpseLabel != null
                    ? $"Corpse candidate: meta={corpseLabel.ItemOnGround.Metadata}, dist={corpseLabel.ItemOnGround.DistancePlayer:F1}"
                    : "No corpse candidate");

                if (corpseLabel != null)
                {
                    await PickAsync(corpseLabel.ItemOnGround, corpseLabel.Label, null, _corpseLabels.ForceUpdate, requireTargeting: false, bypassClickGate: true, yieldAfterMove: true);
                    return true;
                }
            }

            if (Settings.ClickDoors && Settings.MiscPickit)
            {
                if (GameController.Area?.CurrentArea is { IsHideout: true } or { IsTown: true })
                {
                    Debug("Skip doors: in town/hideout");
                    return false;
                }

                var doorLabel = _doorLabels?.Value.FirstOrDefault(x =>
                    x.ItemOnGround.DistancePlayer <= Settings.MiscPickitRange);

                if (DebugOn)
                {
                    var target = doorLabel?.Label;
                    var clickable = target != null && IsLabelClickable(target, null);
                    Debug(doorLabel != null
                        ? $"Door candidate: meta={doorLabel.ItemOnGround.Metadata}, dist={doorLabel.ItemOnGround.DistancePlayer:F1}, clickable={clickable}"
                        : "No door candidate");
                }

                if (doorLabel != null && (pickUpThisItem == null || pickUpThisItem.Distance >= doorLabel.ItemOnGround.DistancePlayer))
                {
                    var doorTarget = doorLabel.Label;
                    Debug($"Picking door: meta={doorLabel.ItemOnGround.Metadata}");
                    await PickAsync(doorLabel.ItemOnGround, doorTarget, null, _doorLabels.ForceUpdate, requireTargeting: false, bypassClickGate: true, yieldAfterMove: true);
                    return true;
                }
            }

            if (Settings.ClickChests && Settings.MiscPickit)
            {
                if (GameController.Area?.CurrentArea is { IsHideout: true } or { IsTown: true })
                {
                    Debug("Skip chests: in town/hideout");
                    return false;
                }

                var chestLabel = _chestLabels?.Value.FirstOrDefault(x =>
                    x.ItemOnGround.DistancePlayer <= Settings.MiscPickitRange);

                if (DebugOn)
                {
                    var target = chestLabel?.Label;
                    var clickable = target != null && IsLabelClickable(target, null);
                    Debug(chestLabel != null
                        ? $"Chest candidate: meta={chestLabel.ItemOnGround.Metadata}, dist={chestLabel.ItemOnGround.DistancePlayer:F1}, clickable={clickable}"
                        : "No chest candidate");
                }

                if (chestLabel != null && (pickUpThisItem == null || pickUpThisItem.Distance >= chestLabel.ItemOnGround.DistancePlayer))
                {
                    var chestTarget = chestLabel.Label;
                    Debug($"Picking chest: meta={chestLabel.ItemOnGround.Metadata}");
                    await PickAsync(chestLabel.ItemOnGround, chestTarget, null, _chestLabels.ForceUpdate, requireTargeting: false, bypassClickGate: true, yieldAfterMove: true);
                    return true;
                }
            }

            if (Settings.ClickPortals && Settings.MiscPickit)
            {
                if (GameController.Area?.CurrentArea is { IsHideout: true } or { IsTown: true })
                {
                    Debug("Skip portals: in town/hideout");
                    return false;
                }

                var portalLabel = _portalLabels?.Value.FirstOrDefault(x =>
                    x.ItemOnGround.DistancePlayer <= Settings.MiscPickitRange &&
                    IsLabelClickable(x.Label, null));

                Debug(portalLabel != null
                    ? $"Portal candidate: meta={portalLabel.ItemOnGround.Metadata}, dist={portalLabel.ItemOnGround.DistancePlayer:F1}"
                    : "No portal candidate");

                if (portalLabel != null && (pickUpThisItem == null || pickUpThisItem.Distance >= portalLabel.ItemOnGround.DistancePlayer))
                {
                    if (_sinceLastClick.ElapsedMilliseconds < Settings.MiscClickDelay)
                    {
                        Debug($"Portal click gated: sinceLast={_sinceLastClick.ElapsedMilliseconds} < delay={Settings.MiscClickDelay}");
                        return false;
                    }
                    Debug("Picking portal");
                    await PickAsync(portalLabel.ItemOnGround, portalLabel.Label, null, _portalLabels.ForceUpdate, requireTargeting: false, bypassClickGate: true, yieldAfterMove: true);
                    return true;
                }
            }

            if (Settings.ClickTransitions && Settings.MiscPickit)
            {
                if (GameController.Area?.CurrentArea is { IsHideout: true } or { IsTown: true })
                {
                    Debug("Skip transitions: in town/hideout");
                    return false;
                }

                var transitionLabel = _transitionLabel?.Value;

                if (DebugOn && transitionLabel != null)
                {
                    var clickable = IsLabelClickable(transitionLabel.Label, null);
                    Debug($"Transition candidate: meta={transitionLabel.ItemOnGround.Metadata}, dist={transitionLabel.ItemOnGround.DistancePlayer:F1}, clickable={clickable}");
                }

                if (transitionLabel != null && (pickUpThisItem == null || pickUpThisItem.Distance >= transitionLabel.ItemOnGround.DistancePlayer))
                {
                    if (_sinceLastClick.ElapsedMilliseconds < Settings.MiscClickDelay)
                    {
                        Debug($"Transition click gated: sinceLast={_sinceLastClick.ElapsedMilliseconds} < delay={Settings.MiscClickDelay}");
                        return false;
                    }
                    Debug("Picking transition");
                    await PickAsync(transitionLabel.ItemOnGround, transitionLabel.Label, null, _transitionLabel.ForceUpdate, requireTargeting: false, bypassClickGate: true, yieldAfterMove: true);
                    return true;
                }
            }

            if (pickUpThisItem == null)
            {
                Debug("No item to pick");
                return true;
            }

            pickUpThisItem.AttemptedPickups++;
            Debug($"Picking item: name={pickUpThisItem.BaseName}, dist={pickUpThisItem.Distance:F1}");
            await PickAsync(pickUpThisItem.QueriedItem.Entity, pickUpThisItem.QueriedItem.Label, null, () => { }, requireTargeting: true, bypassClickGate: false, yieldAfterMove: false);
        }

        return true;
    }

    private IEnumerable<PickItItemData> GetItemsToPickup(bool filterAttempts)
    {
        var labelsRaw = GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabelElement?.VisibleGroundItemLabels;
        if (DebugOn && labelsRaw != null)
        {
            var arr = labelsRaw.ToList();
            var total = arr.Count;
            var inRangeCount = arr.Count(x => x.Entity?.DistancePlayer is { } d && d < Settings.ItemPickitRange);
            var clickableCount = arr.Count(x => x.Entity?.Path != null && IsLabelClickable(x.Label, null));
            var afterPipe = arr
                .Where(x => x.Entity?.DistancePlayer is { } distance && distance < Settings.ItemPickitRange)
                .Where(x => x.Entity?.Path != null && IsLabelClickable(x.Label, null))
                .Select(x => new PickItItemData(x, GameController))
                .Where(x => x.Entity != null && (!filterAttempts || x.AttemptedPickups == 0) && DoWePickThis(x)
                            && (Settings.PickUpWhenInventoryIsFull || CanFitInventory(x)))
                .Count();
            Debug($"GetItemsToPickup counts: total={total}, inRange={inRangeCount}, clickable={clickableCount}, afterFilters={afterPipe}");
        }

        var labels = labelsRaw?
            .Where(x=> x.Entity?.DistancePlayer is {} distance && distance < Settings.ItemPickitRange)
            .OrderBy(x => x.Entity?.DistancePlayer ?? int.MaxValue);

        return labels?
            .Where(x => x.Entity?.Path != null && IsLabelClickable(x.Label, null))
            .Select(x => new PickItItemData(x, GameController))
            .Where(x => x.Entity != null
                        && (!filterAttempts || x.AttemptedPickups == 0)
                        && DoWePickThis(x)
                        && (Settings.PickUpWhenInventoryIsFull || CanFitInventory(x))) ?? [];
    }

    private async SyncTask<bool> PickAsync(Entity item, Element label, RectangleF? customRect, Action onNonClickable, bool requireTargeting = true, bool bypassClickGate = false, bool yieldAfterMove = false)
    {
        _isCurrentlyPicking = true;
        try
        {
            var tryCount = 0;
            Debug($"PickAsync start: target={(item?.Metadata ?? item?.Path ?? "<null>")}, labelAddr={(label?.Address ?? 0)}, dist={(item?.DistancePlayer.ToString("F1") ?? "-")}");
            while (tryCount < 3)
            {
                if (label == null)
                {
                    Debug("PickAsync: label is null, invoking onNonClickable");
                    onNonClickable();
                    return true;
                }

                if (Settings.IgnoreMoving && item != null && GameController.Player.GetComponent<Actor>().isMoving)
                {
                    if (item.DistancePlayer > Settings.ItemDistanceToIgnoreMoving.Value)
                    {
                        Debug($"PickAsync: ignoring due to moving, dist={item.DistancePlayer:F1} > {Settings.ItemDistanceToIgnoreMoving.Value}");
                        await TaskUtils.NextFrame();
                        continue;
                    }
                }

                var rect = customRect ?? label.GetClientRect();
                Debug($"PickAsync: labelRect=({rect.X:F0},{rect.Y:F0},{rect.Width:F0},{rect.Height:F0}), text='{label.Text}'");
                Vector2 position;
                var window = GameController.Window.GetWindowRectangleTimeCache;
                bool rectInScreen = rect.X >= window.X && rect.Y >= window.Y && rect.X + rect.Width <= window.X + window.Width && rect.Y + rect.Height <= window.Y + window.Height;
                Debug($"PickAsync: rectInScreen={rectInScreen}, window=({window.X:F0},{window.Y:F0},{window.Width:F0},{window.Height:F0})");
                Vector2 OffsetIfNeeded(Vector2 p) => rectInScreen ? p : p + window.TopLeft;

                bool isDoor = IsDoorEntity(item);
                bool isChest = IsChestEntity(item);
                bool isPortal = IsPortalEntity(item);
                bool isTransition = item?.Path?.Contains("AreaTransition", StringComparison.Ordinal) == true;
                bool isMisc = isDoor || isChest || isPortal || isTransition;
                bool usedEntityCenter = false;
                if (isMisc && TryGetEntityScreenCenter(item, out var entityScreen))
                {
                    // For chests, bias slightly downward from entity center to avoid clicking above the box
                    if (isChest)
                    {
                        entityScreen = new Vector2(entityScreen.X, entityScreen.Y + 12);
                    }
                    position = entityScreen;
                    usedEntityCenter = true;
                    Debug($"PickAsync: using entity center at <{position.X:F0},{position.Y:F0}>");
                }
                else
                {
                    if (rect.Width <= 1 || rect.Height <= 1)
                    {
                        position = OffsetIfNeeded(rect.Center);
                    }
                    else
                    {
                        var maxMarginX = (int)Math.Floor(rect.Width / 2f) - 1;
                        var maxMarginY = (int)Math.Floor(rect.Height / 2f) - 1;
                        var marginX = Math.Max(0, Math.Min(5, maxMarginX));
                        var marginY = Math.Max(0, Math.Min(3, maxMarginY));
                        if (marginX == 0 && marginY == 0)
                        {
                            position = OffsetIfNeeded(rect.Center);
                        }
                        else
                        {
                            position = OffsetIfNeeded(rect.ClickRandom(marginX, marginY));
                            if (isMisc && !usedEntityCenter)
                            {
                                var bottomY = rect.Y + Math.Min(rect.Height - 2, rect.Height * 0.8f);
                                position = OffsetIfNeeded(new Vector2(rect.Center.X, bottomY));
                            }
                        }
                    }
                }

                // Clamp the final screen-space click position to within the game window (with small padding)
                var screenWindowRect = GameController.Window.GetWindowRectangleTimeCache;
                var padded = screenWindowRect; padded.Inflate(-36, -36);
                var minX = padded.X + 1; var minY = padded.Y + 1;
                var maxX = padded.X + padded.Width - 1; var maxY = padded.Y + padded.Height - 1;
                var clampedX = Math.Max(minX, Math.Min(maxX, position.X));
                var clampedY = Math.Max(minY, Math.Min(maxY, position.Y));
                if (DebugOn && (Math.Abs(clampedX - position.X) > float.Epsilon || Math.Abs(clampedY - position.Y) > float.Epsilon))
                {
                    Debug($"PickAsync: clamped click from {position} to <{clampedX}, {clampedY}> within window=({padded.X:F0},{padded.Y:F0},{padded.Width:F0},{padded.Height:F0})");
                }
                position = new Vector2(clampedX, clampedY);
                Debug($"PickAsync: finalClickPos=<{position.X:F0},{position.Y:F0}>");

                if (!IsTargeted(item, label))
                {
                    Debug($"PickAsync: not targeted, moving cursor to {position}");
                    var targeted = await SetCursorPositionAsync(position, item, label, waitForTargeting: requireTargeting);
                    if (!requireTargeting || targeted)
                    {
                        Debug($"PickAsync: post-move targeted={targeted}, requireTargeting={requireTargeting}, bypassClickGate={bypassClickGate}, okToClick={OkayToClick}");
                        if (yieldAfterMove)
                        {
                            await TaskUtils.NextFrame();
                            if (isChest)
                            {
                                Debug("PickAsync: chest extra settle frame before click");
                                await TaskUtils.NextFrame();
                            }
                        }
                        if (bypassClickGate || OkayToClick)
                        {
                            Debug("PickAsync: clicking after retarget");
                            Input.Click(MouseButtons.Left);
                            _sinceLastClick.Restart();
                            tryCount++;
                            if (bypassClickGate)
                            {
                                await TaskUtils.NextFrame();
                                var targetedNow = IsTargeted(item, label) || (label?.HasShinyHighlight == true);
                                Debug($"PickAsync: post-click targeted={targetedNow}");
                                if (targetedNow && isChest)
                                {
                                    // Strongboxes often need a confirm click even when targeted
                                    Debug("PickAsync: chest confirm second click");
                                    Input.Click(MouseButtons.Left);
                                    _sinceLastClick.Restart();
                                    tryCount++;
                                }
                                else if (!targetedNow)
                                {
                                    // Fallback: if we used entity center (common for doors/chests), try label bottom-center
                                    if (isMisc && usedEntityCenter)
                                    {
                                        var fallback = rect.Width > 1 && rect.Height > 1
                                            ? new Vector2(rect.Center.X, rect.Y + Math.Min(rect.Height - 2, rect.Height * 0.8f))
                                            : rect.Center;
                                        var fallbackPos = OffsetIfNeeded(fallback);
                                        // Clamp fallback
                                        var screenWindowRect2 = GameController.Window.GetWindowRectangleTimeCache;
                                        var padded2 = screenWindowRect2; padded2.Inflate(-36, -36);
                                        var fx = Math.Max(padded2.X + 1, Math.Min(padded2.X + padded2.Width - 1, fallbackPos.X));
                                        var fy = Math.Max(padded2.Y + 1, Math.Min(padded2.Y + padded2.Height - 1, fallbackPos.Y));
                                        fallbackPos = new Vector2(fx, fy);
                                        Debug($"PickAsync: post-click fallback move to <{fallbackPos.X:F0},{fallbackPos.Y:F0}>");
                                        Input.SetCursorPos(fallbackPos);
                                        await TaskUtils.NextFrame();
                                    }

                                    Debug("PickAsync: retrying click on single-press path");
                                    Input.Click(MouseButtons.Left);
                                    _sinceLastClick.Restart();
                                    tryCount++;
                                }
                            }
                        }
                        else
                        {
                            Debug("PickAsync: skipped click after retarget (gate)");
                        }
                    }
                    else
                    {
                        Debug("PickAsync: post-move still not targeted and targeting required");
                    }
                }
                else
                {
                    if (!IsTargeted(item, label))
                    {
                        await TaskUtils.NextFrame();
                        continue;
                    }

                    if (OkayToClick)
                    {
                        Debug("PickAsync: clicking target");
                        Input.Click(MouseButtons.Left);
                        _sinceLastClick.Restart();
                        tryCount++;
                    }
                    else
                    {
                        Debug($"PickAsync: click gated by OkayToClick, sinceLast={_sinceLastClick.ElapsedMilliseconds}ms");
                    }
                }

                await TaskUtils.NextFrame();
            }

            Debug($"PickAsync: end after {tryCount} attempts");
            return true;
        }
        finally
        {
            _isCurrentlyPicking = false;
        }
    }

    private static bool IsTargeted(Entity item, Element label)
    {
        if (item == null) return false;
        if (item.GetComponent<Targetable>()?.isTargeted is { } isTargeted)
        {
            return isTargeted;
        }

        return label is { HasShinyHighlight: true };
    }

    private async SyncTask<bool> SetCursorPositionAsync(Vector2 position, Entity item, Element label, bool waitForTargeting = true)
    {
        Debug($"SetCursorPos: {position}");
        Input.SetCursorPos(position);
        if (!waitForTargeting)
        {
            Debug("SetCursorPos: skipping wait for targeting");
            return false;
        }
        // Give the UI a very short time slice to update hover/targeting
        await TaskUtils.NextFrame();
        var targeted = await TaskUtils.CheckEveryFrame(() => IsTargeted(item, label), new CancellationTokenSource(150).Token);
        Debug($"SetCursorPos: targeted={targeted}, labelHasShiny={label?.HasShinyHighlight}");
        return targeted;
    }

    #endregion
}
