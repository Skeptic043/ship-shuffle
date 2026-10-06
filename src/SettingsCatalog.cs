using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ShipShuffle
{
    // Captured only on editor open/Refresh. These rows retain plain values,
    // never scene objects. A failed capture does not replace the last snapshot.
    internal sealed class SettingsCatalog
    {
        internal readonly List<SettingsChoice> Boats = new List<SettingsChoice>();
        internal readonly List<SettingsChoice> Ports = new List<SettingsChoice>();
        internal readonly List<UnavailableChoice> UnavailableBoats = new List<UnavailableChoice>();

        internal sealed class UnavailableChoice
        {
            internal string Label;
            internal string Reason;
        }

        internal static bool TryCapture(out SettingsCatalog snapshot, out string notice)
        {
            snapshot = null;
            notice = "Waiting for the boat and port catalogues. Finish loading, then Refresh. Existing choices are kept.";
            if (GameState.currentlyLoading) return false;
            try
            {
                var boats = BoatCatalog.Discover(includeInactive: true);
                var ports = PortCatalog.Discover();
                if (boats.Boats.Count == 0 || ports.Ports.Count == 0) return false;
                var result = new SettingsCatalog();
                result.Boats.AddRange(Group(boats.Boats.Select(b =>
                {
                    var row = new SettingsChoice { Name = b.IdentityName, Boat = true,
                        Vanilla = b.Vanilla, Size = b.Size };
                    SettingsPresentation.Boat(row, b.Spec.Length);
                    return row;
                })));
                result.Ports.AddRange(Group(ports.Ports.Select(p =>
                {
                    var row = new SettingsChoice
                    {
                        Name = p.IdentityName, Label = p.DisplayName,
                        Vanilla = KnownIdentities.IsVanillaPort(p.Identity),
                        Hidden = KnownIdentities.IsHiddenPort(p.Identity)
                    };
                    SettingsPresentation.Port(row, p.Index);
                    return row;
                })));
                foreach (var unusable in boats.Unusable.Where(boat => !boat.IntentionalExclusion))
                {
                    var row = new SettingsChoice { Name = unusable.Boat.gameObject.name, Boat = true };
                    SettingsPresentation.Boat(row, 0f);
                    result.UnavailableBoats.Add(new UnavailableChoice { Label = row.Label, Reason = unusable.Reason });
                }
                snapshot = result;
                notice = "";
                return true;
            }
            catch (Exception exception)
            {
                notice = "Discovery is not ready. Existing choices are kept.";
                Plugin.Instance?.DebugLog("Settings catalogue refresh failed: " + exception.Message);
                return false;
            }
        }

        internal static IEnumerable<SettingsChoice> Group(IEnumerable<SettingsChoice> choices)
        {
            var rows = choices.GroupBy(c => c.Name, StringComparer.Ordinal).Select(group =>
            {
                var first = group.First();
                var members = group.ToArray();
                return new SettingsChoice
                {
                    Name = first.Name, Label = first.Label, Boat = first.Boat,
                    Vanilla = members.All(m => m.Vanilla), Hidden = members.Any(m => m.Hidden),
                    Size = members.All(m => m.Size == first.Size) ? first.Size : null,
                    Instances = members.Length, GroupLabel = first.GroupLabel, GroupOrder = first.GroupOrder,
                    SortLength = members.Max(m => m.SortLength),
                    MixedOrigin = members.Any(m => m.Vanilla != first.Vanilla)
                };
            }).ToList();
            foreach (var duplicates in rows.GroupBy(c => c.Label, StringComparer.Ordinal).Where(g => g.Count() > 1))
                foreach (var row in duplicates) row.Label += " [" + row.Name + "]";
            return SettingsPresentation.Order(rows);
        }

        internal static string RawLabel(string name)
        {
            var label = name ?? "";
            if (label.StartsWith("BOAT ", StringComparison.Ordinal)) label = label.Substring(5);
            if (label.EndsWith("(Clone)", StringComparison.Ordinal)) label = label.Substring(0, label.Length - 7).TrimEnd();
            label = Regex.Replace(label, @"\s*\(\d+\)$", "").Trim();
            return string.IsNullOrEmpty(label) ? name : label;
        }
    }
}
