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
using System.Windows.Forms;
using ExileCore2.PoEMemory;
using RectangleF = ExileCore2.Shared.RectangleF;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace PickIt;

public partial class PickIt : BaseSettingsPlugin<PickItSettings>
{
    private readonly CachedValue<List<LabelOnGround>> _chestLabels;
    private readonly CachedValue<List<LabelOnGround>> _doorLabels;
    private readonly CachedValue<LabelOnGround> _transitionLabel;
    private readonly CachedValue<bool[,]> _inventorySlotsCache;
    private ServerInventory _inventoryItems;
    private SyncTask<bool> _pickUpTask;
    private bool _isCurrentlyPicking;
    public List<ItemFilter> ItemFilters;
    private bool _pluginBridgeModeOverride;
    private DateTime _disableLazyLootingTill;
    private CancellationTokenSource _cts;
    public static PickIt Main;
    private bool[,] InventorySlots => _inventorySlotsCache.Value;
    private readonly Stopwatch _sinceLastClick = Stopwatch.StartNew();
    private Element UIHoverWithFallback => GameController.IngameState.UIHover switch { null or { Address: 0 } => GameController.IngameState.UIHoverElement, var s => s };
    private bool OkayToClick => _sinceLastClick.ElapsedMilliseconds > Settings.PauseBetweenClicks;

    public PickIt()
    {
        _inventorySlotsCache = new FrameCache<bool[,]>(() => GetContainer2DArray(_inventoryItems));
        _chestLabels = new TimeCache<List<LabelOnGround>>(UpdateChestList, 200);
        _doorLabels = new TimeCache<List<LabelOnGround>>(UpdateDoorList, 200);
        _transitionLabel = new TimeCache<LabelOnGround>(() => GetLabel(@"Metadata/MiscellaneousObjects/AreaTransition_Animate"), 200);
    }

    public override bool Initialise()
    {
        Main = this;

        #region Register keys

        Settings.PickUpKey.OnValueChanged += () => Input.RegisterKey(Settings.PickUpKey);
        Settings.ProfilerHotkey.OnValueChanged += () => Input.RegisterKey(Settings.ProfilerHotkey);

        Input.RegisterKey(Settings.PickUpKey);
        Input.RegisterKey(Settings.ProfilerHotkey);
        Input.RegisterKey(Keys.Escape);

        #endregion

        RulesDisplay.LoadAndApplyRules();
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
            Input.GetKeyState(Keys.Escape) ||
            !IsSafeToRun())
        {
            _pluginBridgeModeOverride = false;
            return WorkMode.Stop;
        }

        if (Input.GetKeyState(Settings.ProfilerHotkey.Value))
        {
            var sw = Stopwatch.StartNew();
            var looseVar2 = GetItemsToPickup(false).FirstOrDefault();
            sw.Stop();
            LogMessage($"GetItemsToPickup Elapsed Time: {sw.ElapsedTicks} Item: {looseVar2?.BaseName} Distance: {looseVar2?.Distance}");
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

    public override void Tick()
    {
        var playerInvCount = GameController?.Game?.IngameState?.Data?.ServerData?.PlayerInventories?.Count;
        if (playerInvCount is null or 0)
            return;

        if (Settings.AutoClickHoveredLootInRange.Value)
        {
            var hoverItemIcon = UIHoverWithFallback.AsObject<HoverItemIcon>();
            if (hoverItemIcon != null && !GameController.IngameState.IngameUi.InventoryPanel.IsVisible &&
                !Input.IsKeyDown(Keys.LButton))
            {
                if (hoverItemIcon.Item != null && OkayToClick)
                {
                    var groundItem = GameController.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels
                        .FirstOrDefault(e => e.Label.Address == hoverItemIcon.Address);
                    if (groundItem != null)
                    {
                        var doWePickThis = DoWePickThis(new PickItItemData(groundItem, GameController));
                        if (doWePickThis && groundItem.Entity.DistancePlayer < 20f)
                        {
                            _sinceLastClick.Restart();
                            Input.Click(MouseButtons.Left);
                        }
                    }
                }
            }
        }

        _inventoryItems = GameController.Game.IngameState.Data.ServerData.PlayerInventories[0].Inventory;
        if (Input.GetKeyState(Settings.LazyLootingPauseKey)) _disableLazyLootingTill = DateTime.Now.AddSeconds(2);
    }

    public override void Render()
    {
        DrawIgnoredCellsSettings();
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

        var workMode = GetWorkMode();
        if (workMode == WorkMode.Stop)
        {
            _cts?.Cancel();
            _cts = null;
        }

        TaskUtils.RunOrRestart(ref _pickUpTask, () =>
        {
            if (workMode != WorkMode.Stop)
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                return RunPickerIterationAsync(_cts.Token);
            }
            else
            {
                _cts?.Cancel();
                _cts = null;
                return null;
            }
        });

        if (_pickUpTask?.GetAwaiter().IsCompleted != false)
        {
            _isCurrentlyPicking = false;
        }

        if (Settings.FilterTest.Value is { Length: > 0 } &&
            GameController.IngameState.UIHover is { Address: not 0 } h &&
            h.Entity.IsValid)
        {
            var f = ItemFilter.LoadFromString(Settings.FilterTest);
            var matched = f.Matches(new ItemData(h.Entity, GameController));
            DebugWindow.LogMsg($"Debug item match: {matched}");
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
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0,0));

            var numb = 0;
            for (var i = 0; i < 5; i++)
            for (var j = 0; j < 12; j++)
            {
                var toggled = Convert.ToBoolean(InventorySlots[i, j]);
                if (ImGui.Checkbox($"##{numb}IgnoredCells", ref toggled)) InventorySlots[i, j] = toggled;

                if (j != 11) ImGui.SameLine();

                numb += 1;
            }

            ImGui.PopStyleVar(2);

            ImGui.End();
        }
    }

    private bool IsSafeToRun()
    {
        return !Settings.NoLootingWhileEnemyClose || !AnyNearbyMonsters();
    }

    private bool AnyNearbyMonsters()
    {
        return GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster]
            .Any(x => x?.GetComponent<Monster>() != null && x.IsValid && x.IsHostile && x.IsAlive && !x.IsHidden &&
                      Vector3.Distance(GameController.Player.Pos, x.GetComponent<Render>().Pos) < Settings.MonsterCheckRange);
    }

    private bool DoWePickThis(PickItItemData item)
    {
        return Settings.PickUpEverything || (ItemFilters?.Any(filter => filter.Matches(item)) ?? false);
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

        if (GameController.EntityListWrapper.OnlyValidEntities.Any(IsFittingEntity))
        {
            return GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabelsVisible
                .Where(x => x.Address != 0 &&
                            x.IsVisible &&
                            IsFittingEntity(x.ItemOnGround))
                .OrderBy(x => x.ItemOnGround.DistancePlayer)
                .ToList() ?? [];
        }

        return [];
    }

    private List<LabelOnGround> UpdateDoorList()
    {
        bool IsFittingEntity(Entity entity)
        {
            return entity?.Path is { } path && (
                    path.Contains("DoorRandom", StringComparison.Ordinal) ||
                    path.Contains("Door", StringComparison.Ordinal) ||
                    path.Contains("TowerCompletion", StringComparison.Ordinal));
        }

        if (GameController.EntityListWrapper.OnlyValidEntities.Any(IsFittingEntity))
        {
            return GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabelsVisible
                .Where(x => x.Address != 0 &&
                            x.IsVisible &&
                            IsFittingEntity(x.ItemOnGround))
                .OrderBy(x => x.ItemOnGround.DistancePlayer)
                .ToList() ?? [];
        }

        return [];
    }

    private bool CanLazyLoot()
    {
        if (!Settings.LazyLooting) return false;
        if (_disableLazyLootingTill > DateTime.Now) return false;
        if (GameController.Area.CurrentArea.IsHideout || GameController.Area.CurrentArea.IsTown) return false;
        try
        {
            return !Settings.NoLazyLootingWhileEnemyClose || !AnyNearbyMonsters();
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

        if (item == null)
            return false;

        var itemPos = item.QueriedItem.Entity.Pos;
        return IsCloseEnoughForLazyLoot(itemPos);
    }

    private bool IsCloseEnoughForLazyLoot(Vector3 itemPos)
    {
        var playerPos = GameController.Player.Pos;
        return Math.Abs(itemPos.Z - playerPos.Z) <= 50 &&
               itemPos.Xy().Distance(playerPos.Xy()) <= 275;
    }

    private bool ShouldLazyLoot(LabelOnGround label)
    {
        if (!Settings.LazyLooting)
            return false;

        if (label == null)
            return false;

        var itemPos = label.ItemOnGround.Pos;
        return IsCloseEnoughForLazyLoot(itemPos);
    }

    private bool IsLabelClickable(Element element, RectangleF? customRect)
    {
        if (element is not { IsValid: true, IsVisible: true, IndexInParent: not null })
        {
            return false;
        }

        var center = (customRect ?? element.GetClientRect()).Center;

        var gameWindowRect = GameController.Window.GetWindowRectangleTimeCache with { Location = Vector2.Zero };
        gameWindowRect.Inflate(-36, -36);
        return gameWindowRect.Contains(center.X, center.Y);
    }

    private LabelOnGround GetLabel(string id)
    {
        var labels = GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels;
        if (labels == null)
        {
            return null;
        }

        var regex = new Regex(id);
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

    private async SyncTask<bool> RunPickerIterationAsync(CancellationToken cancellationToken)
    {
        if (!GameController.Window.IsForeground()) return true;
        var workMode = GetWorkMode();
        if (workMode != WorkMode.Lazy) LogMessage("RunPickerIterationAsync");

        var pickUpThisItem = GetItemsToPickup(true).FirstOrDefault();
        if (workMode == WorkMode.Manual || workMode == WorkMode.Lazy && 
            (ShouldLazyLoot(pickUpThisItem) ||
            ShouldLazyLoot(_transitionLabel.Value) ||
            _chestLabels.Value.Any(ShouldLazyLoot)))
        {
            if (Settings.MiscOptions.ClickDoors)
            {
                var doorLabel = _doorLabels?.Value.FirstOrDefault(x =>
                    x.ItemOnGround.DistancePlayer <= Settings.MiscOptions.MiscPickitRange &&
                    IsLabelClickable(x.Label, null));

                if (doorLabel != null && (pickUpThisItem == null || pickUpThisItem.Distance >= doorLabel.ItemOnGround.DistancePlayer))
                {
                    await PickAsync(doorLabel.ItemOnGround, doorLabel.Label, null, _doorLabels.ForceUpdate, cancellationToken);
                    return true;
                }
            }

            if (Settings.MiscOptions.ClickChests)
            {
                var chestLabel = _chestLabels?.Value.FirstOrDefault(x =>
                    x.ItemOnGround.DistancePlayer <= Settings.MiscOptions.MiscPickitRange &&
                    IsLabelClickable(x.Label, null));

                if (chestLabel != null && (pickUpThisItem == null || pickUpThisItem.Distance >= chestLabel.ItemOnGround.DistancePlayer))
                {
                    await PickAsync(chestLabel.ItemOnGround, chestLabel.Label, null, _chestLabels.ForceUpdate, cancellationToken);
                    return true;
                }
            }

            if (Settings.MiscOptions.ClickZoneTransitions)
            {
                var transitionLabel = _transitionLabel?.Value;

                if (transitionLabel != null && pickUpThisItem == null)
                {
                    await PickAsync(transitionLabel.ItemOnGround, transitionLabel.Label, null, _transitionLabel.ForceUpdate, cancellationToken);
                    return true;
                }
            }

            if (pickUpThisItem == null)
            {
                return true;
            }

            pickUpThisItem.AttemptedPickups++;
            await PickAsync(pickUpThisItem.QueriedItem.Entity, pickUpThisItem.QueriedItem.Label, pickUpThisItem.QueriedItem.ClientRect, () => { }, cancellationToken);
        }

        return true;
    }

    private IEnumerable<PickItItemData> GetItemsToPickup(bool filterAttempts)
    {
        var labels = GameController.Game.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels?
            .Where(x=> x.Entity?.DistancePlayer is {} distance && distance < Settings.ItemPickupRange)
            .OrderBy(x => x.Entity?.DistancePlayer ?? int.MaxValue);

        return labels?
            .Where(x => x.Entity?.Path != null && IsLabelClickable(x.Label, x.ClientRect))
            .Select(x => new PickItItemData(x, GameController))
            .Where(x => x.Entity != null
                        && (!filterAttempts || x.AttemptedPickups == 0)
                        && DoWePickThis(x)
                        && (Settings.PickUpWhenInventoryIsFull || CanFitInventory(x))) ?? [];
    }

    private async SyncTask<bool> PickAsync(Entity item, Element label, RectangleF? customRect, Action onNonClickable, CancellationToken cancellationToken)
    {
        _isCurrentlyPicking = true;
        try
        {
            var tryCount = 0;
            using var x = Settings.UseInputLock ? Input.InputManager.BlockUserMouseInput() : null;
            while (tryCount < 3 && !cancellationToken.IsCancellationRequested)
            {
                if (!IsLabelClickable(label, customRect))
                {
                    onNonClickable();
                    return true;
                }

                if (Settings.IgnoreMoving && GameController.Player.GetComponent<Actor>().isMoving)
                {
                    if (item.DistancePlayer > Settings.ItemDistanceToIgnoreMoving.Value)
                    {
                        await TaskUtils.NextFrame();
                        continue;
                    }
                }

                var position = label.GetClientRect().ClickRandom(5, 3) + GameController.Window.GetWindowRectangleTimeCache.TopLeft;
                if (OkayToClick)
                {
                    if (!IsTargeted(item, label))
                    {
                        await SetCursorPositionAsync(position, item, label);
                    }
                    else
                    {
                        if (!IsTargeted(item, label))
                        {
                            await TaskUtils.NextFrame();
                            continue;
                        }

                        LogMessage("Click!");
                        Input.Click(MouseButtons.Left);
                        _sinceLastClick.Restart();
                        tryCount++;
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return true;
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

    private async SyncTask<bool> SetCursorPositionAsync(Vector2 position, Entity item, Element label)
    {
        var currentPos = Input.ForceMousePosition;
        DebugWindow.LogMsg($"Set cursor pos: {currentPos} -> {position} {item} {label}");
        if (Settings.SmoothCursorMovement)
        {
            var steps = Math.Clamp((int)(position - currentPos).Length() / 10, 10, 50);
            await Input.InputManager.MoveMouseSyncTask(new MouseMoveStroke(Enumerable.Range(0, steps)
                .Select(x => new MouseMoveStrokePoint(Vector2.Lerp(currentPos, position, x / (float)steps), TimeSpan.FromMilliseconds(1))).ToList()));
        }
        else
        {
            Input.SetCursorPos(position);
        }
        return await TaskUtils.CheckEveryFrame(() => IsTargeted(item, label), new CancellationTokenSource(60).Token);
    }
}
