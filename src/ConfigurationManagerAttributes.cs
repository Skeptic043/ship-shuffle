using System;
using BepInEx.Configuration;

namespace ShipShuffle
{
    // Configuration Manager discovers these optional fields on config tags by
    // name. No ConfigurationManager reference or plugin dependency is needed.
    internal sealed class ConfigurationManagerAttributes
    {
        public Action<ConfigEntryBase> CustomDrawer;
        public bool? Browsable;
        public bool? HideDefaultButton;
        public bool? HideSettingName;
        public string DispName;
        public string Category;
        public int? Order;
    }
}
