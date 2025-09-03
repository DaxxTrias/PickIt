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
    private List<ItemFilter> _itemFilters;
    private bool _pluginBridgeModeOverride;
    private bool[,] InventorySlots => _inventorySlotsCache.Value;
    private readonly Stopwatch _sinceLastClick = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<string, Regex> _labelRegexCache = new();
    private uint? _lastAreaHash;
    private Element UIHoverWithFallback =>
        GameController?.IngameState?.UIHover is { Address: > 0 } s
            ? s
            : GameController?.IngameState?.UIHoverElement;
    private bool OkayToClick => _sinceLastClick.ElapsedMilliseconds > Settings.PauseBetweenClicks;

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
        if (path is { } p)
        {
            if (p.StartsWith("Metadata/MiscellaneousObjects/Portal", StringComparison.Ordinal)) return true;
            if (p.StartsWith("Metadata/MiscellaneousObjects/MultiplexPortal", StringComparison.Ordinal)) return true;
            if (p.StartsWith("Metadata/Effects/Microtransactions/Town_Portals/", StringComparison.Ordinal)) return true;
        }
        return entity.HasComponent<Portal>();
    }

    private static bool IsShrineEntity(Entity entity)
    {
        var path = entity?.Path;
        return path is { } p && p.Contains("Shrine", StringComparison.Ordinal);
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

        Settings.PickUpKey.OnValueChanged += () => Input.RegisterKey(Settings.PickUpKey.Value);
        Settings.ProfilerHotkey.OnValueChanged += () => Input.RegisterKey(Settings.ProfilerHotkey.Value);

        Input.RegisterKey(Settings.PickUpKey.Value);
        Input.RegisterKey(Settings.ProfilerHotkey.Value);
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


        if (Input.GetKeyState(Settings.PickUpKey.Value.Key) || _pluginBridgeModeOverride)
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

        // Apply a short cooldown to lazy looting when the area/scene changes
        var areaHash = GameController?.Area?.CurrentArea?.Hash;
        if (areaHash != null && areaHash != _lastAreaHash)
        {
            _lastAreaHash = areaHash;
            DisableLazyLootingTill = DateTime.Now.AddMilliseconds(5000); // assume players have left start area after 5s
            _sinceLastClick.Restart();
        }

        if (Settings.AutoClickHoveredLootInRange.Value && GetWorkMode() != WorkMode.Stop)
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
                        if (!IsItSafeToPickit())
                            return;

                        // Capacity check
                        var capacityOk = Settings.PickUpWhenInventoryIsFull || CanFitInventory(new ItemData(groundItem, GameController));

                        // Filter match
                        var doWePickThis = capacityOk && (Settings.PickUpEverything || (_itemFilters?.Any(filter =>
                            filter.Matches(new ItemData(groundItem, GameController))) ?? false));

                        var distance = groundItem?.ItemOnGround.DistancePlayer ?? float.MaxValue;
                        if (Input.GetKeyState(Settings.ProfilerHotkey.Value.Key))
                        {
                            DebugWindow.LogMsg($"HoverClick check: dist={distance:F1}, range={Settings.ItemPickitRange}, match={doWePickThis}, ok={OkayToClick}, capacity={capacityOk}");
                        }

                        // Ignore moving
                        if (Settings.IgnoreMoving && GameController.Player?.GetComponent<Actor>()?.isMoving == true &&
                            distance > Settings.ItemDistanceToIgnoreMoving.Value)
                        {
                            return;
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

        // New: hovered misc auto-click (doors/chests/portals/transitions/corpses) respecting misc toggles
        if (Settings.AutoClickHoveredMiscInRange.Value && Settings.MiscPickit && GetWorkMode() != WorkMode.Stop)
        {
            var hoverElement = UIHoverWithFallback;
            if (hoverElement != null && GameController?.IngameState?.IngameUi?.InventoryPanel is { IsVisible: false } &&
                !Input.IsKeyDown(Keys.LButton) && IsItSafeToPickit())
            {
                var hoveredAddr = hoverElement.Address;
                (LabelOnGround Label, float Dist, bool RequiresDelay)? candidate = null;

                void Consider(System.Collections.Generic.IEnumerable<LabelOnGround> labels, bool enabled, bool requiresDelay)
                {
                    if (!enabled || labels == null) return;
                    foreach (var l in labels)
                    {
                        if (l?.Label == null || l.ItemOnGround == null) continue;
                        if (l.Label.Address != hoveredAddr) continue;
                        var dist = l.ItemOnGround.DistancePlayer;
                        if (dist > Settings.MiscPickitRange) continue;
                        if (!IsLabelClickable(l.Label, null)) continue;
                        candidate = (l, dist, requiresDelay);
                        return;
                    }
                }

                Consider(_doorLabels?.Value, Settings.ClickDoors, false);
                Consider(_chestLabels?.Value, Settings.ClickChests, false);
                Consider(_corpseLabels?.Value, Settings.ClickCorpses, false);
                Consider(_portalLabels?.Value, Settings.ClickPortals, true);

                if (Settings.ClickTransitions && _transitionLabel?.Value is { } t && t.Label != null && t.ItemOnGround != null)
                {
                    var target = t.Label;
                    if (t.Label.Address == hoveredAddr && IsLabelClickable(target, null))
                    {
                        var dist = t.ItemOnGround.DistancePlayer;
                        if (dist <= Settings.MiscPickitRange)
                        {
                            candidate = (t, dist, true);
                        }
                    }
                }

                if (candidate is { } c)
                {
                    // Honor IgnoreMoving for hovered misc
                    if (Settings.IgnoreMoving && GameController.Player?.GetComponent<Actor>()?.isMoving == true &&
                        c.Dist > Settings.ItemDistanceToIgnoreMoving.Value)
                    {
                        // skip
                    }
                    else if (c.RequiresDelay)
                    {
                        if (_sinceLastClick.ElapsedMilliseconds >= Settings.MiscClickDelay)
                        {
                            _sinceLastClick.Restart();
                            Input.Click(MouseButtons.Left);
                        }
                    }
                    else if (OkayToClick)
                    {
                        _sinceLastClick.Restart();
                        Input.Click(MouseButtons.Left);
                    }
                }
            }
        }

        var inventories = GameController?.Game?.IngameState?.Data?.ServerData?.PlayerInventories;
        _inventoryItems = inventories != null && inventories.Count > 0 && inventories[0] != null
            ? inventories[0].Inventory
            : null;
        DrawIgnoredCellsSettings();
        if (Input.GetKeyState(Settings.LazyLootingPauseKey.Value.Key)) DisableLazyLootingTill = DateTime.Now.AddSeconds(2);
        
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
            if (Settings.MiscPickit && Settings.ClickDoors)
            {
                foreach (var door in _doorLabels.Value)
                {
                    Graphics.DrawFrame(door.Label.GetClientRect(), Color.Violet, 5);
                }
            }
            if (Settings.MiscPickit && Settings.ClickChests)
            {
                foreach (var chest in _chestLabels.Value)
                {
                    Graphics.DrawFrame(chest.Label.GetClientRect(), Color.Violet, 5);
                }
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
                                  && Vector3.Distance(playerPos, render.Pos) <= Settings.EnemyProximityRange);
            if (near != null)
            {
                try
                {
                    var render = near.GetComponent<Render>();
                    var dist = Vector3.Distance(playerPos, render.Pos);
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
        static bool IsFittingEntity(Entity entity)
        {
            return entity?.Path is { } path &&
                   (path.StartsWith("Metadata/Chests", StringComparison.Ordinal) ||
                   path.StartsWith("Metadata/Chests/", StringComparison.Ordinal) ||
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
        static bool IsFittingEntity(Entity entity)
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
        static bool IsFittingEntity(Entity entity)
        {
            if (entity?.Path is not { } path)
                return false;

            return path == "Metadata/Terrain/Leagues/Necropolis/Objects/NecropolisCorpseMarker"
                || path.Contains("Landmark_Chest", StringComparison.Ordinal)
                || path.Contains("Chest", StringComparison.Ordinal);
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
        static bool IsFittingEntity(Entity entity)
        {
            if (entity == null)
                return false;

            if (entity.Path is { } path)
            {
                if (path.StartsWith("Metadata/MiscellaneousObjects/Portal", StringComparison.Ordinal)) return true;
                if (path.StartsWith("Metadata/MiscellaneousObjects/MultiplexPortal", StringComparison.Ordinal)) return true;
                if (path.StartsWith("Metadata/Effects/Microtransactions/Town_Portals/", StringComparison.Ordinal)) return true;
            }

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
        // Disabled shrines for now
        return [];
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
                                          && Vector3.Distance(playerPos, render.Pos) < Settings.EnemyProximityRange))
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

    internal void LoadRuleFiles()
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

        // Also defer to hovered-misc when enabled and a misc label is hovered
        if (Settings.AutoClickHoveredMiscInRange.Value && Settings.MiscPickit)
        {
            var hoverElement = UIHoverWithFallback;
            if (hoverElement != null && GameController?.IngameState?.IngameUi?.InventoryPanel is { IsVisible: false })
            {
                var hoveredAddr = hoverElement.Address;
                bool IsHoveredMisc(LabelOnGround l) => l?.Label != null && l.Label.Address == hoveredAddr;
                if ((_doorLabels?.Value?.Any(IsHoveredMisc) ?? false) ||
                    (_chestLabels?.Value?.Any(IsHoveredMisc) ?? false) ||
                    (_corpseLabels?.Value?.Any(IsHoveredMisc) ?? false) ||
                    (_portalLabels?.Value?.Any(IsHoveredMisc) ?? false) ||
                    (_transitionLabel?.Value?.Label?.Address == hoveredAddr))
                {
                    return true;
                }
            }
        }


        // Hover/highlight fallbacks removed; rely solely on LabelOnGround.Entity data

        var pickUpThisItem = GetItemsToPickup(true).FirstOrDefault();
        var workMode = GetWorkMode();
        if (workMode == WorkMode.Manual || workMode == WorkMode.Lazy && (ShouldLazyLoot(pickUpThisItem) ||
            ShouldLazyLootMisc(_portalLabels.Value.FirstOrDefault()) ||
            /* shrines disabled */ false ||
            ShouldLazyLootMisc(_chestLabels.Value.FirstOrDefault())))
        {
            var inTownOrHideout = GameController.Area?.CurrentArea is { IsHideout: true } or { IsTown: true };

            // Merge misc candidates across types and choose the globally nearest
            var candidates = new List<(string Kind, Entity Entity, Element Target, float Distance, Action ForceUpdate, bool RequiresDelay)>();

            if (Settings.MiscPickit && !inTownOrHideout)
            {
                if (Settings.ClickCorpses)
                {
                    foreach (var c in _corpseLabels?.Value ?? Enumerable.Empty<LabelOnGround>())
                    {
                        if (c?.ItemOnGround == null) continue;
                        var dist = c.ItemOnGround.DistancePlayer;
                        if (dist <= Settings.MiscPickitRange && IsLabelClickable(c.Label, null))
                        {
                            var target = c.Label;
                            candidates.Add(("corpse", c.ItemOnGround, target, dist, _corpseLabels.ForceUpdate, false));
                        }
                    }
                }

                if (Settings.ClickDoors)
                {
                    foreach (var door in _doorLabels?.Value ?? Enumerable.Empty<LabelOnGround>())
                    {
                        if (door?.ItemOnGround == null) continue;
                        var dist = door.ItemOnGround.DistancePlayer;
                        if (dist <= Settings.MiscPickitRange && IsLabelClickable(door.Label, null))
                        {
                            candidates.Add(("door", door.ItemOnGround, door.Label, dist, _doorLabels.ForceUpdate, false));
                        }
                    }
                }

                if (Settings.ClickChests)
                {
                    foreach (var ch in _chestLabels?.Value ?? Enumerable.Empty<LabelOnGround>())
                    {
                        if (ch?.ItemOnGround == null) continue;
                        var dist = ch.ItemOnGround.DistancePlayer;
                        if (dist <= Settings.MiscPickitRange && IsLabelClickable(ch.Label, null))
                        {
                            var target = ch.Label;
                            candidates.Add(("chest", ch.ItemOnGround, target, dist, _chestLabels.ForceUpdate, false));
                        }
                    }
                }

                if (Settings.ClickPortals)
                {
                    foreach (var p in _portalLabels?.Value ?? Enumerable.Empty<LabelOnGround>())
                    {
                        if (p?.ItemOnGround == null) continue;
                        var dist = p.ItemOnGround.DistancePlayer;
                        if (dist <= Settings.MiscPickitRange && IsLabelClickable(p.Label, null))
                        {
                            candidates.Add(("portal", p.ItemOnGround, p.Label, dist, _portalLabels.ForceUpdate, true));
                        }
                    }
                }

                if (Settings.ClickTransitions)
                {
                    var t = _transitionLabel?.Value;
                    if (t != null)
                    {
                        var dist = t.ItemOnGround.DistancePlayer;
                        var target = t.Label;
                        if (IsLabelClickable(target, null))
                        {
                            candidates.Add(("transition", t.ItemOnGround, target, dist, _transitionLabel.ForceUpdate, true));
                        }
                    }
                }
            }

            if (candidates.Count > 0)
            {
                var nearest = candidates.OrderBy(c => c.Distance).First();
                if (pickUpThisItem == null || pickUpThisItem.Distance >= nearest.Distance)
                {
                    if (nearest.RequiresDelay && _sinceLastClick.ElapsedMilliseconds < Settings.MiscClickDelay)
                    {
                        return false;
                    }
                    await PickAsync(nearest.Entity, nearest.Target, null, nearest.ForceUpdate);
                    return true;
                }
            }

            if (pickUpThisItem == null)
            {
                return true;
            }

            pickUpThisItem.AttemptedPickups++;
            await PickAsync(pickUpThisItem.QueriedItem.Entity, pickUpThisItem.QueriedItem.Label, null, () => { });
        }

        return true;
    }

    private IEnumerable<PickItItemData> GetItemsToPickup(bool filterAttempts)
    {
        var labelsRaw = GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabelElement?.VisibleGroundItemLabels;

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

    private async SyncTask<bool> PickAsync(Entity item, Element label, RectangleF? customRect, Action onNonClickable)
    {
        _isCurrentlyPicking = true;
        try
        {
            var tryCount = 0;
            while (tryCount < 3)
            {
                if (label == null)
                {
                    onNonClickable();
                    return true;
                }

                if (Settings.IgnoreMoving && item != null && GameController.Player.GetComponent<Actor>().isMoving)
                {
                    if (item.DistancePlayer > Settings.ItemDistanceToIgnoreMoving.Value)
                    {
                        await TaskUtils.NextFrame();
                        continue;
                    }
                }

                var rect = customRect ?? label.GetClientRect();
                Vector2 position;
                if (rect.Width <= 1 || rect.Height <= 1)
                {
                    onNonClickable();
                    return true;
                }
                else
                {
                    var maxMarginX = (int)Math.Floor(rect.Width / 2f) - 1;
                    var maxMarginY = (int)Math.Floor(rect.Height / 2f) - 1;
                    var marginX = Math.Max(0, Math.Min(5, maxMarginX));
                    var marginY = Math.Max(0, Math.Min(3, maxMarginY));
                    if (marginX == 0 && marginY == 0)
                    {
                        position = rect.Center + GameController.Window.GetWindowRectangleTimeCache.TopLeft;
                    }
                    else
                    {
                        position = rect.ClickRandom(marginX, marginY) + GameController.Window.GetWindowRectangleTimeCache.TopLeft;
                    }
                }

                // Clamp the final screen-space click position to within the game window (with small padding)
                var screenWindowRect = GameController.Window.GetWindowRectangleTimeCache;
                var padded = screenWindowRect; padded.Inflate(-36, -36);
                var minX = padded.X + 1; var minY = padded.Y + 1;
                var maxX = padded.X + padded.Width - 1; var maxY = padded.Y + padded.Height - 1;
                var clampedX = Math.Max(minX, Math.Min(maxX, position.X));
                var clampedY = Math.Max(minY, Math.Min(maxY, position.Y));
                position = new Vector2(clampedX, clampedY);

                if (!IsTargeted(item, label))
                {
                    var acquired = await SetCursorPositionAsync(position, item, label);
                    if (!acquired)
                    {
                        tryCount++;
                        onNonClickable();
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
                        Input.Click(MouseButtons.Left);
                        _sinceLastClick.Restart();
                        tryCount++;
                    }
                    else
                    {
                    }
                }

                await TaskUtils.NextFrame();
            }

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

    private static async SyncTask<bool> SetCursorPositionAsync(Vector2 position, Entity item, Element label)
    {
        Input.SetCursorPos(position);
        return await TaskUtils.CheckEveryFrame(() => IsTargeted(item, label), new CancellationTokenSource(150).Token);
    }

    #endregion
}
