using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using System.Numerics;

namespace PickIt;

public class PickItSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(false);
    public ToggleNode ShowInventoryView { get; set; } = new(true);
    public RangeNode<Vector2> InventoryPos { get; set; } = new(new Vector2(0, 0), Vector2.Zero, new Vector2(4000, 4000));
    public HotkeyNode ProfilerHotkey { get; set; } = Keys.None;
    public HotkeyNode PickUpKey { get; set; } = Keys.F;
    public ToggleNode PickUpWhenInventoryIsFull { get; set; } = new(false);
    public ToggleNode PickUpEverything { get; set; } = new(false);
    public RangeNode<int> ItemPickupRange { get; set; } = new(600, 1, 1000);
    public RangeNode<int> MonsterCheckRange { get; set; } = new(1000, 1, 2500);

    [Menu(null, "In milliseconds")]
    public RangeNode<int> PauseBetweenClicks { get; set; } = new(100, 0, 500);

    public ToggleNode IgnoreMoving { get; set; } = new(false);

    [ConditionalDisplay(nameof(IgnoreMoving), true)]
    public RangeNode<int> ItemDistanceToIgnoreMoving { get; set; } = new(20, 0, 1000);

    [Menu(null, "Auto pick up any hovered items that match configured filters")]
    public ToggleNode AutoClickHoveredLootInRange { get; set; } = new(false);

    public ToggleNode SmoothCursorMovement { get; set; } = new(true);
    public ToggleNode UseInputLock { get; set; } = new(true);

    public ToggleNode LazyLooting { get; set; } = new(false);

    [ConditionalDisplay(nameof(LazyLooting), true)]
    public ToggleNode NoLazyLootingWhileEnemyClose { get; set; } = new(true);

    [ConditionalDisplay(nameof(LazyLooting), true)]
    public HotkeyNode LazyLootingPauseKey { get; set; } = new(Keys.None);

    [Menu(null, "Includes lazy looting as well as manual activation")]
    public ToggleNode NoLootingWhileEnemyClose { get; set; } = new(false);

    public MiscClickableOptions MiscOptions { get; set; } = new();

    [JsonIgnore]
    public TextNode FilterTest { get; set; } = new();

    [Menu("Use a Custom \"\\config\\custom_folder\" folder ")]
    public TextNode CustomConfigDir { get; set; } = new();

    public List<PickitRule> PickitRules = [];

    [Menu(null, "For debugging. Highlights items if they match an existing filter")]
    [JsonIgnore]
    public ToggleNode DebugHighlight { get; set; } = new(false);
    
    [JsonIgnore]
    public FilterNode Filters { get; } = new();
}

[Submenu(CollapsedByDefault = true)]
public class MiscClickableOptions
{
    public RangeNode<int> MiscPickitRange { get; set; } = new(15, 0, 600);
    public ToggleNode ClickChests { get; set; } = new(true);
    public ToggleNode ClickDoors { get; set; } = new(true);
    public ToggleNode ClickZoneTransitions { get; set; } = new(false);
}

[Submenu(RenderMethod = nameof(Render))]
public class FilterNode
{
    public void Render()
    {
        RulesDisplay.DrawSettings();
    }
}

public record PickitRule(string Name, string Location, bool Enabled)
{
    public bool Enabled = Enabled;
}