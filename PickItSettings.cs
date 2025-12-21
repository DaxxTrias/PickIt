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
    public ToggleNode Enable { get; set; } = new ToggleNode(false);
    [Menu("Show Inventory Overlay", "Display a 5x12 inventory grid overlay to toggle ignored cells.")]
    public ToggleNode ShowInventoryView { get; set; } = new ToggleNode(true);
    [Menu("Inventory Overlay Position", "Screen position for the inventory overlay (drag to move).")]
    public RangeNode<Vector2> InventoryPos { get; set; } = new RangeNode<Vector2>(new Vector2(0, 0), Vector2.Zero, new Vector2(4000, 4000));
    [Menu("Profiler/Debug Hotkey", "Hold to print diagnostic info (e.g., hover checks) to the log.")]
    public HotkeyNodeV2 ProfilerHotkey { get; set; } = Keys.None;
    [Menu("Manual Pick Hotkey", "Hold to force manual pickup mode while pressed.")]
    public HotkeyNodeV2 PickUpKey { get; set; } = Keys.F;
    [Menu("Pick Up When Inventory Is Full", "Attempt picking even if no inventory space is available.")]
    public ToggleNode PickUpWhenInventoryIsFull { get; set; } = new ToggleNode(false);
    [Menu("Pick Up Everything", "Ignore filters and pick up all items within range.")]
    public ToggleNode PickUpEverything { get; set; } = new ToggleNode(false);
    [Menu("No Looting Near Enemies", "Pause all looting (manual, hover, lazy) while enemies are within the proximity range.")]
    public ToggleNode NoLootingWhileEnemyClose { get; set; } = new ToggleNode(false);
    [ConditionalDisplay(nameof(NoLootingWhileEnemyClose), true)]
    [Menu("Enemy Proximity Range", "Radius (units) used for enemy proximity checks to pause looting. Also applies to lazy looting when its enemy toggle is enabled.")]
    public RangeNode<int> EnemyProximityRange { get; set; } = new RangeNode<int>(600, 1, 1000);
    [Menu("Item Pickit Range", "Maximum distance (units) to consider items for pickup.")]
    public RangeNode<int> ItemPickitRange { get; set; } = new RangeNode<int>(600, 1, 1000);
    [Menu("Pause Between Clicks", "Delay in milliseconds between clicks for non-misc actions.")]
    public RangeNode<int> PauseBetweenClicks { get; set; } = new RangeNode<int>(100, 0, 500);
    [Menu("Ignore While Moving", "When on, avoid clicking while the player is moving beyond the set distance.")]
    public ToggleNode IgnoreMoving { get; set; } = new ToggleNode(false);
    [ConditionalDisplay(nameof(IgnoreMoving), true)]
    [Menu("Ignore-Moving Distance", "Minimum distance (units) before clicks occur while moving.")]
    public RangeNode<int> ItemDistanceToIgnoreMoving { get; set; } = new RangeNode<int>(20, 0, 1000);
    [Menu("Auto-Click Hovered Loot", "Auto-click hovered items that match filters or when 'Pick Up Everything' is on.")]
    public ToggleNode AutoClickHoveredLootInRange { get; set; } = new ToggleNode(false);
    [Menu("Auto-Click Hovered Misc", "Auto-click hovered doors, chests, transitions, portals, and corpses when enabled.")]
    public ToggleNode AutoClickHoveredMiscInRange { get; set; } = new ToggleNode(false);
    [Menu("Lazy Looting", "Automatically pick nearby targets when it is safe.")]
    public ToggleNode LazyLooting { get; set; } = new ToggleNode(false);
    [ConditionalDisplay(nameof(LazyLooting), true)]
    [Menu("No Lazy Looting Near Enemies", "Pause lazy looting while enemies are within the proximity range.")]
    public ToggleNode NoLazyLootingWhileEnemyClose { get; set; } = new ToggleNode(false);
    [ConditionalDisplay(nameof(LazyLooting), true)]
    [Menu("Lazy Looting Pause Hotkey", "Hold to temporarily pause lazy looting.")]
    public HotkeyNodeV2 LazyLootingPauseKey { get; set; } = new HotkeyNodeV2(Keys.Space);
    
    [Menu("Miscellaneous Pickit Options", "Enable clicking of doors, chests, corpses, transitions, and portals.")]
    public ToggleNode MiscPickit { get; set; } = new ToggleNode(true);
    [Menu("Misc Pickit Range", "Maximum distance (units) for misc interactions (doors, chests, etc.).")]
    [ConditionalDisplay(nameof(MiscPickit), true)]
    public RangeNode<int> MiscPickitRange { get; set; } = new RangeNode<int>(25, 0, 600);
    [Menu("Misc Click Delay", "Delay in milliseconds between clicks for misc actions that require pacing (e.g., portals).")]
    public RangeNode<int> MiscClickDelay { get; set; } = new RangeNode<int>(10000, 100, 100000);
    [ConditionalDisplay(nameof(MiscPickit), true)]
    [Menu("Click Chests", "Click chests when in range.")]
    public ToggleNode ClickChests { get; set; } = new ToggleNode(true);
    [ConditionalDisplay(nameof(MiscPickit), true)]
    [Menu("Click Doors", "Click doors and levers when in range.")]
    public ToggleNode ClickDoors { get; set; } = new ToggleNode(true);
    [Menu("Click Transitions", "Click area/zone transitions when in range.")]
    [ConditionalDisplay(nameof(MiscPickit), true)]
    public ToggleNode ClickTransitions { get; set; } = new ToggleNode(false);
    [ConditionalDisplay(nameof(MiscPickit), true)]
    [Menu("Click Corpses", "Click interactable corpses when in range (league-specific mechanics).")]
    public ToggleNode ClickCorpses { get; set; } = new ToggleNode(true);
    [ConditionalDisplay(nameof(MiscPickit), true)]
    [Menu("Click Portals", "Click portals when in range. Uses separate misc click delay pacing.")]
    public ToggleNode ClickPortals { get; set; } = new ToggleNode(false);
    [ConditionalDisplay(nameof(MiscPickit), true)]
    [Menu("Click Shrines", "Click shrines when in range.")]
    public ToggleNode ClickShrines { get; set; } = new ToggleNode(true);

    [JsonIgnore]
    public TextNode FilterTest { get; set; } = new TextNode();

    [JsonIgnore]
    public ButtonNode ReloadFilters { get; set; } = new ButtonNode();

    [Menu("Custom Config Folder", "Optional subfolder under 'config' to load .ifl rule files from.")]
    public TextNode CustomConfigDir { get; set; } = new TextNode();

    public List<PickitRule> PickitRules = new List<PickitRule>();

    [JsonIgnore]
    public FilterNode Filters { get; } = new FilterNode();

    [Menu("Debug Highlight", "For debugging. Highlights items/misc when they match active settings/filters.")]
    [JsonIgnore]
    public ToggleNode DebugHighlight { get; set; } = new ToggleNode(false);
}

[Submenu(RenderMethod = nameof(Render))]
public class FilterNode
{
    public void Render(PickIt pickit)
    {
        if (ImGui.Button("Open filter Folder"))
        {
            var configDir = pickit.ConfigDirectory;
            var parentDir = Path.GetDirectoryName(pickit.ConfigDirectory) ?? pickit.ConfigDirectory;
            var customConfigFileDirectory = !string.IsNullOrEmpty(pickit.Settings.CustomConfigDir)
                ? Path.Combine(parentDir, pickit.Settings.CustomConfigDir)
                : null;

            var directoryToOpen = Directory.Exists(customConfigFileDirectory)
                ? customConfigFileDirectory
                : configDir;

            Process.Start("explorer.exe", directoryToOpen);
        }

        ImGui.Separator();
        ImGui.BulletText("Select Rules To Load");
        ImGui.BulletText("Ordering rule sets so general items will match first rather than last will improve performance");

        var tempNpcInvRules = new List<PickitRule>(pickit.Settings.PickitRules); // Create a copy

        for (int i = 0; i < tempNpcInvRules.Count; i++)
        {
            ImGui.PushID(i);
            if (ImGui.ArrowButton("##upButton", ImGuiDir.Up) && i > 0)
                (tempNpcInvRules[i - 1], tempNpcInvRules[i]) = (tempNpcInvRules[i], tempNpcInvRules[i - 1]);

            ImGui.SameLine();
            ImGui.Text(" ");
            ImGui.SameLine();

            if (ImGui.ArrowButton("##downButton", ImGuiDir.Down) && i < tempNpcInvRules.Count - 1)
                (tempNpcInvRules[i + 1], tempNpcInvRules[i]) = (tempNpcInvRules[i], tempNpcInvRules[i + 1]);

            ImGui.SameLine();
            ImGui.Text(" - ");
            ImGui.SameLine();

            ImGui.Checkbox($"{tempNpcInvRules[i].Name}###enabled", ref tempNpcInvRules[i].Enabled);
            ImGui.PopID();
        }

        // Detect changes (order or Enabled flags) and auto-reload filters
        bool changed = false;
        if (tempNpcInvRules.Count != pickit.Settings.PickitRules.Count)
        {
            changed = true;
        }
        else
        {
            for (int i = 0; i < tempNpcInvRules.Count; i++)
            {
                var a = tempNpcInvRules[i];
                var b = pickit.Settings.PickitRules[i];
                if (!string.Equals(a.Name, b.Name) || !string.Equals(a.Location, b.Location) || a.Enabled != b.Enabled)
                {
                    changed = true;
                    break;
                }
            }
        }

        if (changed)
        {
            pickit.Settings.PickitRules = tempNpcInvRules;
            pickit.LoadRuleFiles();
        }
        else
        {
            pickit.Settings.PickitRules = tempNpcInvRules;
        }
    }
}

public record PickitRule(string Name, string Location, bool Enabled)
{
    public bool Enabled = Enabled;
}