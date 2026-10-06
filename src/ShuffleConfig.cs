using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace ShipShuffle
{
    // Player-facing settings only. Port tuning (large-boat and per-boat port
    // exclusions, size classes, estimated berths and station-kept spots) and
    // the diagnostics shortcut are fixed in code.
    internal sealed class ShuffleConfig
    {
        // Fixed Ctrl+F9; active only while DebugLogging is on.
        internal static readonly KeyboardShortcut DumpShortcut = new KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl);

        internal readonly ConfigEntry<bool> Enabled;
        internal readonly ConfigEntry<int> Seed;
        internal readonly ConfigEntry<bool> IncludeVanillaBoats;
        internal readonly ConfigEntry<bool> IncludeModBoats;
        internal readonly ConfigEntry<bool> AllowSmall;
        internal readonly ConfigEntry<bool> AllowMedium;
        internal readonly ConfigEntry<bool> AllowLarge;
        internal readonly ConfigEntry<string> ExcludedBoats;
        internal readonly ConfigEntry<bool> IncludeHiddenPorts;
        internal readonly ConfigEntry<bool> IncludeModPorts;
        internal readonly ConfigEntry<string> ExcludedPorts;
        internal readonly ConfigEntry<bool> MultipleBoatsAtShipyards;
        internal readonly ConfigEntry<bool> RandomizeOverflowBoats;
        internal readonly ConfigEntry<bool> DebugLogging;

        internal ShuffleConfig(ConfigFile config)
        {
            var menu = new SettingsMenu(this);
            Enabled = config.Bind("General", "Enabled", true,
                Basic("Shuffle unpurchased sale boats between ports on new games, and re-apply the stored layout when loading saves made with Ship Shuffle.", "Enabled", 100));
            Seed = config.Bind("General", "Seed", 0,
                Basic("Random seed for new games. 0 or less picks a new random seed each time. The seed used is written to the log.", "Seed", 20));
            IncludeVanillaBoats = config.Bind("Boats", "IncludeVanillaBoats", true, Basic("Shuffle vanilla boats.", "Include vanilla boats", 90));
            IncludeModBoats = config.Bind("Boats", "IncludeModBoats", true,
                Basic("Shuffle detected mod boats.", "Include mod boats", 80));
            AllowSmall = config.Bind("Boats", "AllowSmall", true, Basic("Shuffle small boats.", "Small boats", 70));
            AllowMedium = config.Bind("Boats", "AllowMedium", true, Basic("Shuffle medium boats.", "Medium boats", 60));
            AllowLarge = config.Bind("Boats", "AllowLarge", true, Basic("Shuffle large boats.", "Large boats", 50));
            ExcludedBoats = config.Bind("Boats", "ExcludedBoats", "",
                new ConfigDescription("Choose boats and destinations for future new games.", null,
                    new ConfigurationManagerAttributes { CustomDrawer = menu.Draw, Category = "Options", DispName = "Advanced", Order = 0,
                        HideSettingName = true, HideDefaultButton = true }));
            IncludeHiddenPorts = config.Bind("Ports", "IncludeHiddenPorts", false,
                Hidden("Allow secret destinations through Advanced. Boats still need a compatible, clear placement."));
            IncludeModPorts = config.Bind("Ports", "IncludeModPorts", false,
                Hidden("Allow boats to be moved to ports added by other mods."));
            ExcludedPorts = config.Bind("Ports", "ExcludedPorts", "",
                Hidden("Destination choices managed through Advanced."));
            MultipleBoatsAtShipyards = config.Bind("Ports", "MultipleBoatsAtShipyards", false,
                Basic("Allow up to 3 shuffle places at Gold Rock City, Dragon Cliffs and Fort Aestrin, with at most one Large boat and the other places for Medium or Small boats.", "Multiple boats at shipyard ports", 30));
            RandomizeOverflowBoats = config.Bind("Ports", "RandomizeOverflowBoats", true,
                Basic("Fill checked destinations first, then shuffle leftover boats to compatible unchecked destinations.", "Randomize remaining boats", 25));
            DebugLogging = config.Bind("Diagnostics", "DebugLogging", false,
                Hidden("Write detailed berth and mooring decisions to the BepInEx log. While on, Ctrl+F9 writes a report of ports, docks and boats."));
        }

        private static ConfigDescription Basic(string description, string name, int order) =>
            new ConfigDescription(description, null, new ConfigurationManagerAttributes { Category = "Options", DispName = name, Order = order });
        private static ConfigDescription Hidden(string description) =>
            new ConfigDescription(description, null, new ConfigurationManagerAttributes { Browsable = false });

        // Player settings; the fixed port tables come from KnownIdentities.
        internal FleetSettings ReadFleetSettings(List<string> errors) => new FleetSettings
        {
            IncludeVanillaBoats = IncludeVanillaBoats.Value, IncludeModBoats = IncludeModBoats.Value,
            AllowSmall = AllowSmall.Value, AllowMedium = AllowMedium.Value, AllowLarge = AllowLarge.Value,
            IncludeHiddenPorts = IncludeHiddenPorts.Value, IncludeModPorts = IncludeModPorts.Value,
            ExcludedBoats = SelectionExclusions.Parse(ExcludedBoats.Value, errors, "ExcludedBoats"),
            ExcludedPorts = SelectionExclusions.Parse(ExcludedPorts.Value, errors, "ExcludedPorts"),
            MultipleBoatsAtShipyards = MultipleBoatsAtShipyards.Value, RandomizeOverflowBoats = RandomizeOverflowBoats.Value
        };
    }
}
