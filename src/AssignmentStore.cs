using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ShipShuffle
{
    // Moored: tied to two island cleats (tiers 1-3). Held: station-kept off
    // the recovery berth (tier 4).
    internal enum RecordKind { Moored, Held }

    internal sealed class AssignmentRecord
    {
        internal RecordKind Kind = RecordKind.Moored;
        internal int BoatIndex;
        internal string BoatName;
        internal int PortIndex;
        internal string PortName;
        // Moored: tier 1-3 and the two cleat identities. Side is the pier water
        // side for tier 3 (+1/-1) and 0 for tiers 1-2.
        internal int Tier = 1;
        internal string FrontCleat;
        internal string BackCleat;
        internal int Side;
        // Both kinds: the placed root's island-local position and yaw. Load
        // puts the boat back at exactly this pose.
        internal float LocalX;
        internal float LocalZ;
        internal float YawDegrees;

        internal string Describe() =>
            "boat " + BoatIndex + "|" + BoatName + " -> port " + PortIndex + "|" + PortName + (Kind == RecordKind.Moored
                ? " tier " + Tier + " (front " + FrontCleat + ", back " + BackCleat + (Side != 0 ? ", side " + Side : "") + ") at"
                : " station-kept at") + " island-local (" + LocalX.ToString("F2", CultureInfo.InvariantCulture) + ", " +
                LocalZ.ToString("F2", CultureInfo.InvariantCulture) + ") yaw " + YawDegrees.ToString("F1", CultureInfo.InvariantCulture);
    }

    internal sealed class AssignmentDocument
    {
        internal int Seed;
        internal readonly List<AssignmentRecord> Records = new List<AssignmentRecord>();
    }

    // Plain-text record stored in GameState.modData. No Unity dependency so the
    // offline harness can round-trip it. Every field is URL-escaped; positions
    // are island-local so floating-origin shifts and Scrambled Seas cannot
    // invalidate them. A header line, then one line per record:
    //   1|seed|<int>
    //   moored|boatIndex|boatName|portIndex|portName|tier|frontCleat|backCleat|side|x|z|yaw
    //   held|boatIndex|boatName|portIndex|portName|x|z|yaw
    internal static class AssignmentStore
    {
        internal const string Key = "ShipShuffle";
        private const string Version = "1";
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        internal static string Format(AssignmentDocument document)
        {
            var builder = new StringBuilder();
            builder.Append(Join(Version, "seed", document.Seed.ToString(Invariant)));
            foreach (var record in document.Records)
            {
                builder.Append('\n');
                if (record.Kind == RecordKind.Held)
                {
                    builder.Append(Join("held", record.BoatIndex.ToString(Invariant), record.BoatName,
                        record.PortIndex.ToString(Invariant), record.PortName, R(record.LocalX), R(record.LocalZ), R(record.YawDegrees)));
                    continue;
                }
                builder.Append(Join("moored", record.BoatIndex.ToString(Invariant), record.BoatName,
                    record.PortIndex.ToString(Invariant), record.PortName, record.Tier.ToString(Invariant),
                    record.FrontCleat, record.BackCleat, (record.Tier == 3 ? record.Side : 0).ToString(Invariant),
                    R(record.LocalX), R(record.LocalZ), R(record.YawDegrees)));
            }
            return builder.ToString();
        }

        // Returns null when the header is not the current format. Malformed or
        // ambiguous records are reported and dropped whole; the rest remain usable.
        internal static AssignmentDocument Parse(string text, List<string> errors)
        {
            if (string.IsNullOrEmpty(text))
            {
                errors.Add("Stored assignment text is empty.");
                return null;
            }
            var lines = text.Split(new[] { '\n' }, StringSplitOptions.None);
            var header = Split(lines[0].TrimEnd('\r'));
            if (header == null || header.Length != 3 || header[0] != Version || header[1] != "seed" ||
                !int.TryParse(header[2], NumberStyles.Integer, Invariant, out var seed))
            {
                errors.Add("Stored assignment header is not '" + Version + "|seed|<int>'.");
                return null;
            }
            var document = new AssignmentDocument { Seed = seed };
            var boats = new Dictionary<int, int>();
            for (var lineIndex = 1; lineIndex < lines.Length; ++lineIndex)
            {
                var line = lines[lineIndex].TrimEnd('\r');
                if (line.Length == 0) continue;
                var record = ParseRecord(Split(line));
                if (record == null)
                {
                    errors.Add("Stored assignment line " + (lineIndex + 1) + " is malformed and was ignored.");
                    continue;
                }
                boats[record.BoatIndex] = boats.TryGetValue(record.BoatIndex, out var seen) ? seen + 1 : 1;
                document.Records.Add(record);
            }
            foreach (var pair in boats)
                if (pair.Value > 1)
                {
                    errors.Add("Boat " + pair.Key + " has " + pair.Value + " stored assignments; all were ignored.");
                    document.Records.RemoveAll(record => record.BoatIndex == pair.Key);
                }
            return document;
        }

        private static AssignmentRecord ParseRecord(string[] fields)
        {
            if (fields == null || fields.Length < 5 ||
                !int.TryParse(fields[1], NumberStyles.Integer, Invariant, out var boatIndex) ||
                !int.TryParse(fields[3], NumberStyles.Integer, Invariant, out var portIndex) ||
                boatIndex < 0 || portIndex < 0 || string.IsNullOrEmpty(fields[2]) || string.IsNullOrEmpty(fields[4])) return null;
            var record = new AssignmentRecord { BoatIndex = boatIndex, BoatName = fields[2], PortIndex = portIndex, PortName = fields[4] };
            switch (fields[0])
            {
                case "moored":
                    // Side is +1/-1 for tier 3 and 0 otherwise.
                    if (fields.Length != 12 ||
                        !int.TryParse(fields[5], NumberStyles.Integer, Invariant, out var tier) || tier < 1 || tier > 3 ||
                        !int.TryParse(fields[8], NumberStyles.Integer, Invariant, out record.Side) ||
                        (tier == 3 ? record.Side != 1 && record.Side != -1 : record.Side != 0) ||
                        !TryFloat(fields[9], out record.LocalX) || !TryFloat(fields[10], out record.LocalZ) ||
                        !TryFloat(fields[11], out record.YawDegrees)) return null;
                    record.Tier = tier;
                    record.FrontCleat = fields[6];
                    record.BackCleat = fields[7];
                    return ValidCleats(record) ? record : null;
                case "held":
                    if (fields.Length != 8 || !TryFloat(fields[5], out record.LocalX) ||
                        !TryFloat(fields[6], out record.LocalZ) || !TryFloat(fields[7], out record.YawDegrees)) return null;
                    record.Kind = RecordKind.Held;
                    record.Tier = 4;
                    return record;
                default:
                    return null;
            }
        }

        // "@..." marks an off-island recovery cleat, which is never a berth.
        private static bool ValidCleats(AssignmentRecord record) =>
            !string.IsNullOrEmpty(record.FrontCleat) && !string.IsNullOrEmpty(record.BackCleat) && record.FrontCleat != record.BackCleat &&
            !record.FrontCleat.StartsWith("@", StringComparison.Ordinal) && !record.BackCleat.StartsWith("@", StringComparison.Ordinal);

        private static string R(float value) => value.ToString("R", Invariant);

        private static bool TryFloat(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, Invariant, out value) && !float.IsNaN(value) && !float.IsInfinity(value);

        private static string Join(params string[] fields)
        {
            var escaped = new string[fields.Length];
            for (var i = 0; i < fields.Length; ++i) escaped[i] = Uri.EscapeDataString(fields[i] ?? "");
            return string.Join("|", escaped);
        }

        private static string[] Split(string line)
        {
            try
            {
                var fields = line.Split('|');
                for (var i = 0; i < fields.Length; ++i) fields[i] = Uri.UnescapeDataString(fields[i]);
                return fields;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
