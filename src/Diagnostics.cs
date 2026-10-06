using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ShipShuffle
{
    // Read-only runtime dump for in-game verification. Nothing here mutates
    // boats, cleats, springs or saved data.
    internal static class Diagnostics
    {
        internal static void Dump()
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return;
            var boats = BoatCatalog.Discover();
            var ports = PortCatalog.Discover();
            plugin.Report("=== Ship Shuffle dump: " + ports.Ports.Count + " ports, " + boats.Boats.Count + " boats; modData key present: " +
                (GameState.modData != null && GameState.modData.ContainsKey(AssignmentStore.Key)) + " ===");
            foreach (var rejection in ports.Rejections) plugin.Report("Port rejected: " + rejection);
            var claims = BerthResolver.AuthoredClaims(null);
            foreach (var port in ports.Ports)
            {
                try
                {
                    DumpPort(plugin, port, claims);
                }
                catch (Exception exception)
                {
                    plugin.Report("Port " + port.Label + ": dump failed: " + exception.Message);
                }
            }
            foreach (var rejection in boats.Rejections) plugin.Report("Boat rejected: " + rejection);
            foreach (var boat in boats.Boats)
            {
                try
                {
                    DumpBoat(plugin, boat, ports);
                }
                catch (Exception exception)
                {
                    plugin.Report("Boat " + boat.Label + ": dump failed: " + exception.Message);
                }
            }
            try
            {
                DumpHidden(plugin, boats);
                DumpStored(plugin);
            }
            catch (Exception exception)
            {
                plugin.Report("Hidden boats or stored assignments: dump failed: " + exception.Message);
            }
            plugin.Report("=== End of Ship Shuffle dump ===");
            try { NotificationUi.instance?.ShowNotification("Ship Shuffle:\ndiagnostics written to the BepInEx log"); }
            catch (Exception exception) { plugin.Warn("Dump notification could not be shown: " + exception.Message); }
        }

        private static void DumpPort(Plugin plugin, PortEntry port, Dictionary<Transform, string> claims)
        {
            if (!BerthResolver.TryBuildSite(port, out var site, out var reason))
            {
                plugin.Report("Port " + port.Label + " on " + port.Island.name + (port.NoRecovery ? " (no recovery berth)" :
                    ": boatPos " + port.Recovery.boatPos.position.ToString("F2")) + ", rejected: " + reason);
                return;
            }
            if (site.NoRecovery && !site.MarkerReference)
            {
                plugin.Report("Port " + port.Label + " on " + port.Island.name + ": no recovery berth; reserves not applied; " +
                    site.IslandCleatCount + " island cleats, reference pier midpoint " + site.BoatPos + ".");
                return;
            }
            var marker = site.Marker;
            plugin.Report("Port " + port.Label + " on " + port.Island.name + ": boatPos " + marker.position.ToString("F2") +
                (site.MarkerReference ? " (dormant authored pose; no native recovery berth or reserves)" : "") +
                ", forward " + site.BoatForward + ", goLeft " + site.MarkerGoLeft + ", standoff " +
                DockGeometry.F(site.Chain.Standoff) + " m, water side " + site.Chain.Side + ", chain " + site.Cleats.Length +
                " of " + site.IslandCleatCount + " island cleats:");
            for (var i = 0; i < site.Cleats.Length; ++i)
            {
                var cleat = site.Cleats[i];
                if (cleat == null)
                {
                    plugin.Report("  " + i + " " + site.CleatIds[i] + ": virtual recovery reference from the marker frame");
                    continue;
                }
                var tag = i == site.Chain.R1 ? " [R1]" : i == site.Chain.R2 ? " [R2]" : "";
                claims.TryGetValue(cleat.transform, out var owner);
                plugin.Report("  " + i + tag + " " + site.CleatIds[i] + ": " +
                    DockGeometry.F(Vec2.Distance(site.Chain.Points[i], site.BoatPos)) + " m from boatPos, spring -> " +
                    BoatRelocator.BodyName(cleat.spring.connectedBody) + ", authored claim " + (owner ?? "none") +
                    ", active " + cleat.gameObject.activeInHierarchy);
            }
        }

        private static void DumpBoat(Plugin plugin, BoatEntry boat, PortCatalog ports)
        {
            var position = boat.Saveable.transform.position;
            var flat = HullFootprint.Flat(position);
            var nearest = ports.Ports.Where(port => !port.NoRecovery)
                .OrderBy(port => Vec2.Distance(HullFootprint.Flat(port.Recovery.boatPos.position), flat)).FirstOrDefault();
            plugin.Report("Boat " + boat.Label + ": size " + (boat.Size?.ToString() ?? "unknown") + ", origin " + boat.Origin +
                ", purchased " + boat.Purchased + ", position " + position.ToString("F2") + ", yaw " +
                boat.Saveable.transform.eulerAngles.y.ToString("F1") + ", kinematic " + boat.Body.isKinematic +
                (boat.Boat.GetComponent<StationKeeper>() != null ? ", station-kept" : "") +
                ", nearest port " + (nearest == null ? "none" : nearest.Label + " at " +
                DockGeometry.F(Vec2.Distance(HullFootprint.Flat(nearest.Recovery.boatPos.position), flat)) + " m") +
                ", lines " + DescribeRopes(boat));
        }

        // One snapshot on each side of a fleet transaction, using its existing
        // catalogue so diagnostics cannot change discovery or placement.
        internal static void MooringCensus(IEnumerable<BoatEntry> boats, string context)
        {
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.DebugEnabled) return;
            foreach (var boat in boats)
            {
                try
                {
                    if (boat.Purchased) continue;
                    plugin.DebugLog(context + ": moorings of " + boat.Label + ", kinematic " + boat.Body.isKinematic +
                        ", authored front " + DescribeCleat(boat.Ropes.mooringFront) + ", back " +
                        DescribeCleat(boat.Ropes.mooringBack) + "; " + DescribeRopes(boat));
                }
                catch (Exception exception)
                {
                    plugin.DebugLog(context + ": mooring census unavailable: " + exception.Message);
                }
            }
        }

        private static string DescribeRopes(BoatEntry boat) => string.Join(", ", boat.Ropes.ropes.Select(rope =>
        {
            if (rope == null) return "missing rope";
            var spring = MooringRelease.GetSpring(rope);
            return rope.name + " moored " + rope.IsMoored() + ", rope body " + BoatRelocator.BodyName(rope.GetBoatRigidbody()) +
                ", spring " + ObjectName(spring) + " -> " + BoatRelocator.BodyName(spring != null ? spring.connectedBody : null);
        }).ToArray());

        private static string DescribeCleat(Transform cleat)
        {
            var spring = cleat != null ? cleat.GetComponent<GPButtonDockMooring>()?.spring : null;
            return ObjectName(cleat) + " -> " + BoatRelocator.BodyName(spring != null ? spring.connectedBody : null);
        }

        private static string ObjectName(UnityEngine.Object value) =>
            value != null ? value.name + "#" + value.GetInstanceID() : "none";

        // Vanilla BoatHorizonPerformanceSwitcher deactivates boat roots beyond
        // fullySunkDistance during play, so the catalogue above cannot see them.
        private static void DumpHidden(Plugin plugin, BoatCatalog boats)
        {
            var switched = new HashSet<BoatHorizon>();
            foreach (var switcher in Resources.FindObjectsOfTypeAll<BoatHorizonPerformanceSwitcher>())
                if (switcher != null && switcher.gameObject.scene.IsValid() && switcher.gameObject.scene.isLoaded &&
                    switcher.horizons != null)
                    foreach (var horizon in switcher.horizons) if (horizon != null) switched.Add(horizon);
            var count = 0;
            foreach (var boat in Resources.FindObjectsOfTypeAll<PurchasableBoat>())
            {
                if (boat == null || !boat.gameObject.scene.IsValid() || !boat.gameObject.scene.isLoaded ||
                    boat.gameObject.activeInHierarchy || !BoatCatalog.IsBoatLike(boat.GetComponent<SaveableObject>())) continue;
                var horizon = boat.GetComponentInChildren<BoatHorizon>(true);
                if (horizon == null) continue;
                var saveable = boat.GetComponent<SaveableObject>();
                ++count;
                plugin.Report("Inactive boat " + boat.gameObject.name + " (" + saveable.sceneIndex + "): " +
                    (switched.Contains(horizon) ? "hidden-by-distance" : "inactive, not distance-managed") +
                    ", purchased " + boat.isPurchased() + ", position " + boat.transform.position.ToString("F2") +
                    ", distance to player " + horizon.distanceToPlayer.ToString("F0") + " m.");
            }
            plugin.Report("Inactive boats with a BoatHorizon: " + count + ".");
        }

        private static void DumpStored(Plugin plugin)
        {
            if (GameState.modData == null || !GameState.modData.TryGetValue(AssignmentStore.Key, out var text)) return;
            var errors = new List<string>();
            var document = AssignmentStore.Parse(text, errors);
            if (document == null) { plugin.Report("Stored assignments unreadable: " + string.Join("; ", errors.ToArray())); return; }
            plugin.Report("Stored assignments (seed " + document.Seed + "): " + document.Records.Count + ".");
            foreach (var record in document.Records) plugin.Report("  " + record.Describe());
        }
    }
}
