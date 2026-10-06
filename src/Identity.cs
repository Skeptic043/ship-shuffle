using System;
using System.Globalization;

namespace ShipShuffle
{
    internal enum BoatSize { Small = 1, Medium = 2, Large = 3 }

    // Exact runtime identity: scene/port index plus complete object name.
    internal struct Identity : IEquatable<Identity>
    {
        internal readonly int Index;
        internal readonly string Name;

        internal Identity(int index, string name) { Index = index; Name = name ?? ""; }

        public bool Equals(Identity other) => Index == other.Index && string.Equals(Name, other.Name, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is Identity other && Equals(other);
        public override int GetHashCode() => Index * 397 ^ StringComparer.Ordinal.GetHashCode(Name ?? "");
        public override string ToString() => Index.ToString(CultureInfo.InvariantCulture) + "|" + Name;
    }

    // Additional room for held boats at ports with a constrained departure.
    // Zero values preserve the usual berth search and native moored tiers.
    internal struct StationPolicy
    {
        internal float OutwardHullLengths;
        internal float LeftHullWidths;
        internal float BackHullLengths;
        internal bool ForwardOnly;
    }

    // Bounded station search in the live recovery marker's right/forward frame.
    internal struct StationRegion
    {
        internal float MinimumRight, MaximumRight, MinimumForward, MaximumForward;
        internal float TargetRight, TargetForward;
    }
}
