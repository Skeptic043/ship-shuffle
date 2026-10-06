using System;
using System.Collections.Generic;
using System.Linq;

namespace ShipShuffle
{
    // Display aliases and ordering only. Neither discovery, size classification
    // nor placement reads this table. Lengths come from Sailwind/ship-sizes.txt.
    internal static class SettingsPresentation
    {
        private sealed class BoatDisplay
        {
            internal readonly string Label;
            internal readonly float Length;
            internal readonly int Region;
            internal BoatDisplay(string label, float length, int region = 4)
            { Label = label; Length = length; Region = region; }
        }

        private static readonly Dictionary<string, BoatDisplay> BoatNames = new Dictionary<string, BoatDisplay>(StringComparer.OrdinalIgnoreCase)
        {
            { "DNG Cutter", new BoatDisplay("Cutter", 7.9f) },
            { "GALLUS", new BoatDisplay("Gallus", 13.6f) },
            { "FFL Paraw", new BoatDisplay("Paraw", 14.4f) },
            { "happybayboat", new BoatDisplay("Happy Bay Boat", 18.6f) },
            { "Shroud Small", new BoatDisplay("Sh'ba", 19.2f) },
            { "CAELANOR", new BoatDisplay("Caelanor", 21.6f) },
            { "GLORIANA", new BoatDisplay("Gloriana", 35.5f) },
            { "Shroud Large", new BoatDisplay("Clipper", 39.5f) },
            { "CHRONIAN", new BoatDisplay("Aelasyl", 43.4f) },
            { "LEOPARD", new BoatDisplay("HMS Leopard", 53f) },
            { "dhow small", new BoatDisplay("Dhow", 13.2f, 0) },
            { "dhow medium", new BoatDisplay("Sanbuq", 24.1f, 0) },
            { "dhow large", new BoatDisplay("Baghlah", 27.8f, 0) },
            { "junk small singleroof", new BoatDisplay("Kakam", 12.5f, 1) },
            { "junk medium", new BoatDisplay("Junk", 24f, 1) },
            { "junk large", new BoatDisplay("Jong", 31.4f, 1) },
            { "medi small", new BoatDisplay("Cog", 14.6f, 2) },
            { "medi medium", new BoatDisplay("Brig", 29.7f, 2) }
        };

        internal static void Boat(SettingsChoice row, float measuredHullLength)
        {
            var clean = SettingsCatalog.RawLabel(row.Name);
            var known = BoatNames.TryGetValue(clean, out var display);
            row.Label = known ? display.Label : clean;
            // This companion is distinct from the sold DNG Cutter and Leopard.
            // Its exact-name label supplies no eligibility or length metadata.
            if (string.Equals(row.Name, KnownIdentities.LeopardCompanion.Name, StringComparison.Ordinal))
                row.Label = "Leopard's Cutter";
            row.SortLength = known ? display.Length : measuredHullLength;
            if (float.IsNaN(row.SortLength) || float.IsInfinity(row.SortLength) || row.SortLength <= 0f)
                row.SortLength = float.MaxValue;
            row.GroupOrder = row.Vanilla ? (known ? display.Region : 3) : 4;
            row.GroupLabel = BoatGroup(row.GroupOrder);
        }

        private static string BoatGroup(int group) =>
            group == 0 ? "Al'Ankh" : group == 1 ? "Emerald" : group == 2 ? "Aestrin" : group == 3 ? "Other boats" : "Mod boats";

        // Stock region membership follows New Beginnings SelectionCatalog's
        // KnownPorts at revision 1aba5a09148f9f4a44fe7b754848d93905b617a1.
        // The runtime catalogue decides Vanilla before this display mapping.
        internal static void Port(SettingsChoice row, int index)
        {
            if (row.Hidden) { row.GroupOrder = 5; row.GroupLabel = "Secret destinations"; }
            else if (!row.Vanilla) { row.GroupOrder = 6; row.GroupLabel = "Mod islands"; }
            else if (index >= 0 && index <= 5 || index == 31) { row.GroupOrder = 0; row.GroupLabel = "Al'Ankh"; }
            else if (index >= 9 && index <= 14 || index == 29 || index == 30) { row.GroupOrder = 1; row.GroupLabel = "Emerald"; }
            else if (index >= 15 && index <= 19 || index >= 26 && index <= 28) { row.GroupOrder = 2; row.GroupLabel = "Aestrin"; }
            else if (index >= 22 && index <= 25) { row.GroupOrder = 3; row.GroupLabel = "Fire Fish Lagoon"; }
            else { row.GroupOrder = 4; row.GroupLabel = "Small islands"; }
        }

        internal static IEnumerable<SettingsChoice> Order(IEnumerable<SettingsChoice> rows) => rows
            .OrderBy(row => row.GroupOrder)
            .ThenBy(row => row.Boat ? row.SortLength : 0f)
            .ThenBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Name, StringComparer.Ordinal);

        internal static string RowLabel(SettingsChoice row) => row.Boat
            ? row.Label + " (" + (row.Size.HasValue ? row.Size.Value.ToString() : "Mixed sizes") + ")"
            : row.Label;
    }
}
