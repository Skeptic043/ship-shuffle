using System;
using System.Collections.Generic;
using System.Linq;

namespace ShipShuffle
{
    // Settings selectors only. Saved layout identities remain index + name.
    // A record is "name|<escaped exact name>": every exact-name instance
    // shares that choice, so allocated indices can change between launches.
    // Unrecognized records keep their text but select nothing.
    internal sealed class SelectionExclusions
    {
        internal sealed class Record
        {
            internal string Raw;
            internal string Name;
        }

        private readonly List<Record> records = new List<Record>();
        internal IEnumerable<Record> Records => records;
        internal bool Contains(string name) => records.Any(r => r.Name != null && string.Equals(r.Name, name, StringComparison.Ordinal));

        internal static SelectionExclusions Parse(string text, List<string> errors = null, string setting = "Exclusions")
        {
            var result = new SelectionExclusions();
            // Retain raw records, including malformed/absent entries. Only
            // delimiters are normalized on an explicit Apply, never on load.
            foreach (var raw in (text ?? "").Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var value = raw.Trim();
                var record = new Record { Raw = raw };
                if (value.StartsWith("name|", StringComparison.Ordinal))
                {
                    try
                    {
                        var encoded = value.Substring(5);
                        var decoded = Uri.UnescapeDataString(encoded);
                        // Reject malformed escapes and empty names without
                        // losing the user's record. Canonical escapes are strict.
                        if (!string.IsNullOrWhiteSpace(decoded) && string.Equals(Uri.EscapeDataString(decoded), encoded, StringComparison.OrdinalIgnoreCase))
                            record.Name = decoded;
                    }
                    catch (UriFormatException) { }
                }
                if (record.Name == null) errors?.Add(setting + ": unrecognized record retained but not applied: '" + value + "'.");
                result.records.Add(record);
            }
            return result;
        }

        internal void SetIncluded(string name, bool included)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (included) records.RemoveAll(r => string.Equals(r.Name, name, StringComparison.Ordinal));
            else if (!Contains(name)) records.Add(new Record { Name = name, Raw = "name|" + Uri.EscapeDataString(name) });
        }

        internal void Remove(Record record) => records.Remove(record);
        internal string Format() => string.Join(";", records.Select(r => r.Raw).ToArray());
    }

    internal sealed class SettingsChoice
    {
        internal string Name, Label;
        internal bool Boat, Vanilla, Hidden, MixedOrigin;
        internal BoatSize? Size;
        internal int Instances = 1;
        internal string GroupLabel;
        internal int GroupOrder;
        internal float SortLength;

        internal bool IsVisible(FleetSettings settings) => Boat ||
            (Hidden ? settings.IncludeHiddenPorts : Vanilla || settings.IncludeModPorts);

        internal string DisabledReason(FleetSettings settings)
        {
            if (MixedOrigin) return "Shared name across vanilla and mod objects";
            if (Boat)
            {
                if (Vanilla && !settings.IncludeVanillaBoats) return "Vanilla boats are off";
                if (!Vanilla && !settings.IncludeModBoats) return "Mod boats are off";
                if (!Size.HasValue) return Instances > 1 ? "Different hull sizes share this name" : "Size unavailable";
                if (!settings.SizeAllowed(Size.Value)) return Size + " boats are off";
            }
            else
            {
                if (Hidden) return settings.IncludeHiddenPorts ? null : "Secret destinations are off";
                if (!Vanilla && !settings.IncludeModPorts) return "Mod ports are off";
            }
            return null;
        }
    }

    // UI state is a draft. Refresh replaces only a ready catalogue and never
    // changes exclusion records. Global filters affect interaction, not choices.
    internal sealed class SelectionDraft
    {
        internal SelectionExclusions Selection { get; private set; }
        internal List<SettingsChoice> Choices { get; private set; } = new List<SettingsChoice>();
        internal bool HasCatalogue { get; private set; }
        private bool explicitRemoval;
        internal bool CanApply => HasCatalogue || explicitRemoval;
        internal string Original { get; private set; }
        internal SelectionDraft(string saved) { Original = saved ?? ""; Selection = SelectionExclusions.Parse(Original); }

        internal bool Refresh(IEnumerable<SettingsChoice> choices, bool ready)
        {
            if (!ready || choices == null) return false;
            var replacement = choices.ToList();
            if (replacement.Count == 0) return false;
            Choices = replacement;
            HasCatalogue = true;
            return true;
        }

        internal IEnumerable<SettingsChoice> Visible(string search, FleetSettings settings) => Choices.Where(c =>
            c.IsVisible(settings) && (string.IsNullOrEmpty(search) || c.Label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                c.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));

        internal IEnumerable<SelectionExclusions.Record> Unavailable(FleetSettings settings, bool boats) => Selection.Records.Where(r =>
            !Choices.Any(c => r.Name != null && string.Equals(c.Name, r.Name, StringComparison.Ordinal)) &&
            (boats || UnavailablePortVisible(r, settings)));

        private static bool UnavailablePortVisible(SelectionExclusions.Record record, FleetSettings settings)
        {
            // An unrecognized port record has no safe category or display name.
            // Keep it out of the editor until both optional groups are enabled.
            if (record.Name == null) return settings.IncludeHiddenPorts && settings.IncludeModPorts;
            if (HiddenRecord(record)) return settings.IncludeHiddenPorts;
            return settings.IncludeModPorts || KnownIdentities.VanillaPorts.Any(port =>
                string.Equals(port.Name, record.Name, StringComparison.Ordinal));
        }

        internal static bool HiddenRecord(SelectionExclusions.Record r) =>
            KnownIdentities.IsHiddenPortName(r.Name);

        internal void Set(SettingsChoice choice, bool included, FleetSettings settings)
        {
            if (!HasCatalogue || !choice.IsVisible(settings) || choice.DisabledReason(settings) != null) return;
            Selection.SetIncluded(choice.Name, included);
        }

        // Bulk actions affect visible, editable rows only. Unavailable entries
        // require explicit Remove and never change through a group toggle.
        internal void SetVisible(bool included, string search, FleetSettings settings)
        { foreach (var choice in Visible(search, settings)) Set(choice, included, settings); }
        internal void RemoveUnavailable(SelectionExclusions.Record record)
        {
            if (!Selection.Records.Contains(record)) return;
            Selection.Remove(record);
            explicitRemoval = true;
        }
        internal void Cancel() { Selection = SelectionExclusions.Parse(Original); explicitRemoval = false; }
        internal bool TryApply(string current, out string saved)
        {
            saved = current;
            if (!CanApply || !string.Equals(current ?? "", Original, StringComparison.Ordinal)) return false;
            saved = Selection.Format();
            Original = saved;
            return true;
        }
    }
}
