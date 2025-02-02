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
    public ToggleNode ShowInventoryView { get; set; } = new ToggleNode(true);
    public RangeNode<Vector2> InventoryPos { get; set; } = new RangeNode<Vector2>(new Vector2(0, 0), Vector2.Zero, new Vector2(4000, 4000));
    public HotkeyNode ProfilerHotkey { get; set; } = Keys.None;
    public HotkeyNode PickUpKey { get; set; } = Keys.F;
    public ToggleNode PickUpWhenInventoryIsFull { get; set; } = new ToggleNode(false);
    public ToggleNode PickUpEverything { get; set; } = new ToggleNode(false);
    [Menu("Item Pickit Range", "Range at which we will attempt to pickit")]
    public RangeNode<int> ItemPickitRange { get; set; } = new RangeNode<int>(600, 1, 1000);
    [Menu("Pause Between Clicks", "How many milliseconds to wait between clicks")]
    public RangeNode<int> PauseBetweenClicks { get; set; } = new RangeNode<int>(100, 0, 500);
    public ToggleNode IgnoreMoving { get; set; } = new ToggleNode(false);
    [ConditionalDisplay(nameof(IgnoreMoving), true)]
    public RangeNode<int> ItemDistanceToIgnoreMoving { get; set; } = new RangeNode<int>(20, 0, 1000);
    [Menu("Auto Click Hovered Loot In Range", "Auto pick up any hovered items that matches filters or pickup everything if the 'pickup everything' option is enabled")]
    public ToggleNode AutoClickHoveredLootInRange { get; set; } = new ToggleNode(false);
    public ToggleNode LazyLooting { get; set; } = new ToggleNode(false);
    [ConditionalDisplay(nameof(LazyLooting), true)]
    [Menu("No Lazy Looting While Enemy Close", "Will disable Lazy Looting while enemies close by")]
    public ToggleNode NoLazyLootingWhileEnemyClose { get; set; } = new ToggleNode(false);
    [ConditionalDisplay(nameof(LazyLooting), true)]
    public HotkeyNode LazyLootingPauseKey { get; set; } = new HotkeyNode(Keys.Space);
    [Menu("No Looting While Enemy Close", "Will disable pickit while enemies close by (this includes lazylooting as well as manual pickit)")]
    public ToggleNode NoLootingWhileEnemyClose { get; set; } = new ToggleNode(false);
    [Menu("Miscellaneous Pickit Options", "Pickit will click Doors, Chests, Corpses, Transitions, Portals")]
    public ToggleNode MiscPickit { get; set; } = new ToggleNode(true);
    [Menu("Misc Pickit Range", "Range at which we will pickit things that are not items (doors, chests, etc)")]
    [ConditionalDisplay(nameof(MiscPickit), true)]
    public RangeNode<int> MiscPickitRange { get; set; } = new RangeNode<int>(15, 0, 600);
    [ConditionalDisplay(nameof(MiscPickit), true)]
    [Menu("Click Chests", "Will click chests if enabled")]
    public ToggleNode ClickChests { get; set; } = new ToggleNode(true);
    [ConditionalDisplay(nameof(MiscPickit), true)]
    [Menu("Click Doors", "Will click doors if enabled")]
    public ToggleNode ClickDoors { get; set; } = new ToggleNode(true);
    [Menu("Click Transitions", "Will click area/zone transitions if enabled")]
    [ConditionalDisplay(nameof(MiscPickit), true)]
    public ToggleNode ClickTransitions { get; set; } = new ToggleNode(false);
    [ConditionalDisplay(nameof(MiscPickit), true)]
    [Menu("Click Corpses", "Will click corpses if enabled")]
    public ToggleNode ClickCorpses { get; set; } = new ToggleNode(true);
    [ConditionalDisplay(nameof(MiscPickit), true)]
    [Menu("Click Portals", "Will click portals if enabled")]
    public ToggleNode ClickPortals { get; set; } = new ToggleNode(false);
    [Menu("Misc Click Delay", "How many milliseconds should pickit wait between clicks for a misc object (portal, doors, etc)")]
    public RangeNode<int> MiscClickDelay { get; set; } = new RangeNode<int>(15000, 100, 100000);

    [JsonIgnore]
    public TextNode FilterTest { get; set; } = new TextNode();

    [JsonIgnore]
    public ButtonNode ReloadFilters { get; set; } = new ButtonNode();

    [Menu("Use a Custom \"\\config\\custom_folder\" folder ")]
    public TextNode CustomConfigDir { get; set; } = new TextNode();

    public List<PickitRule> PickitRules = new List<PickitRule>();

    [JsonIgnore]
    public FilterNode Filters { get; } = new FilterNode();

    [Menu(null, "For debugging. Highlights items if they match an existing filter")]
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
            var customConfigFileDirectory = !string.IsNullOrEmpty(pickit.Settings.CustomConfigDir)
                ? Path.Combine(Path.GetDirectoryName(pickit.ConfigDirectory), pickit.Settings.CustomConfigDir)
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

        pickit.Settings.PickitRules = tempNpcInvRules;
    }
}

public record PickitRule(string Name, string Location, bool Enabled)
{
    public bool Enabled = Enabled;
}