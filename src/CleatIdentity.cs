using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ShipShuffle
{
    // Persistent cleat identity: the name path from the island to the cleat,
    // each element suffixed with its 1-based ordinal among same-named siblings
    // (for example "dock_mooring M#2"). Several Sailwind docks reuse names.
    internal static class CleatIdentity
    {
        // Off-island recovery cleats get these placeholders; they are never
        // tied to, never stored and never resolved from a record.
        internal const string RecoveryFront = "@recovery-front";
        internal const string RecoveryBack = "@recovery-back";
        internal const string MarkerFront = "@marker-front";
        internal const string MarkerBack = "@marker-back";
        internal static bool IsPlaceholder(string identity) => identity != null && identity.StartsWith("@", StringComparison.Ordinal);

        internal static string Build(Transform island, Transform cleat)
        {
            if (island == null || cleat == null) return null;
            var parts = new List<string>();
            for (var current = cleat; current != island; current = current.parent)
            {
                if (current == null) return null;
                parts.Add(Escape(current.name) + "#" + Ordinal(current).ToString(CultureInfo.InvariantCulture));
            }
            if (parts.Count == 0) return null;
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        internal static bool TryResolve(Transform island, string identity, out Transform cleat, out string reason)
        {
            cleat = null;
            reason = null;
            if (island == null || string.IsNullOrEmpty(identity))
            {
                reason = "Cleat identity or island is missing.";
                return false;
            }
            if (IsPlaceholder(identity))
            {
                reason = "Cleat identity '" + identity + "' is an off-island recovery cleat placeholder and cannot be stored.";
                return false;
            }
            var current = island;
            foreach (var part in identity.Split('/'))
            {
                var separator = part.LastIndexOf('#');
                if (separator <= 0 || !int.TryParse(part.Substring(separator + 1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var ordinal) || ordinal < 1)
                {
                    reason = "Cleat identity element '" + part + "' is malformed.";
                    return false;
                }
                var name = Unescape(part.Substring(0, separator));
                Transform match = null;
                var seen = 0;
                for (var i = 0; i < current.childCount; ++i)
                {
                    var child = current.GetChild(i);
                    if (!string.Equals(child.name, name, StringComparison.Ordinal)) continue;
                    if (++seen == ordinal) match = child;
                }
                if (match == null)
                {
                    reason = "Cleat identity element '" + part + "' matched " + seen + " sibling(s) under " + current.name + ".";
                    return false;
                }
                current = match;
            }
            cleat = current;
            return true;
        }

        private static int Ordinal(Transform transform)
        {
            var parent = transform.parent;
            if (parent == null) return 1;
            var ordinal = 0;
            for (var i = 0; i < parent.childCount; ++i)
            {
                var sibling = parent.GetChild(i);
                if (string.Equals(sibling.name, transform.name, StringComparison.Ordinal)) ++ordinal;
                if (sibling == transform) return ordinal;
            }
            return ordinal;
        }

        private static string Escape(string name) =>
            (name ?? "").Replace("%", "%25").Replace("/", "%2F").Replace("#", "%23");

        // Single-pass decoding; Escape turned every literal '%' into "%25".
        private static string Unescape(string name) => Uri.UnescapeDataString(name);
    }
}
