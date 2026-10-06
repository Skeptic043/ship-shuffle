using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ShipShuffle
{
    // New-game generation and load re-application of a whole fleet.
    internal static class FleetGenerator
    {
        private sealed class Pending
        {
            internal FleetBoat Boat;
            internal BoatEntry Entry;
            internal int Port;
            internal int Tier;
            internal string FrontId;
            internal string BackId;
            internal int Side;
            internal Vector3 LocalPosition;
            internal float LocalYaw;
            internal FleetMove Move;
            internal BerthPlan Plan;
            // Load: the port's dock site (or its island alone) and the stored
            // cleats' keys, which still count for the cascade when the boat is
            // held instead of moored.
            internal DockSite Site;
            internal string[] CleatKeys = new string[0];
            // Set when no berth was left for it at a shared port; it stays home.
            internal string PreDrop;

            internal void Take(BerthPlan plan)
            {
                Tier = plan.Tier;
                FrontId = plan.FrontId;
                BackId = plan.BackId;
                Side = plan.Candidate.Side;
                LocalPosition = plan.LocalPosition;
                LocalYaw = plan.LocalYaw;
            }
        }

        internal static AssignmentDocument GenerateNewGame(ShuffleConfig config)
        {
            var plugin = Plugin.Instance;
            var errors = new List<string>();
            var settings = config.ReadFleetSettings(errors);
            var world = FleetWorld.Survey();
            foreach (var error in errors) plugin.Warn("Config: " + error);
            var seed = NewGameSeed(config.Seed.Value, new System.Random());
            plugin.DebugLog("New game: " + world.Boats.Count + " unpurchased sale boats, " + world.Ports.Count + " ports catalogued.");
            foreach (var rejection in world.KnownBoats.Rejections) plugin.DebugLog("Boat catalog: " + rejection);
            foreach (var rejection in world.KnownPorts.Rejections) plugin.DebugLog("Port catalog: " + rejection);

            FleetPlanner.Classify(world.Boats, world.Ports, settings);
            foreach (var home in OwnedLargeHomes(world))
                if (world.Ports[home].SharedMix) ++world.Ports[home].FixedLarge;
            if (plugin.DebugEnabled)
            {
                foreach (var boat in world.Boats)
                    plugin.DebugLog("  Boat " + boat.Label + " (" + (boat.Size?.ToString() ?? "size unknown") + ", home " +
                        (world.PortKey(boat.Home) ?? "none") + "): " + (boat.Eligible ? "eligible" : "stays, " + boat.Reason));
                for (var p = 0; p < world.Ports.Count; ++p)
                    plugin.DebugLog("  Port " + world.Ports[p].Label + ": " + world.Ports[p].Reason);
                if (settings.MultipleBoatsAtShipyards)
                    plugin.DebugLog("New game: MultipleBoatsAtShipyards is on; capacity " + string.Join(", ", world.Ports.Where(port => port.Capacity > 1)
                        .Select(port => port.Label + " " + port.Capacity).DefaultIfEmpty("none found").ToArray()) +
                        "; at most one Large per shared port; every other port 1.");
            }

            var eligible = world.Boats.Where(item => item.Eligible).Select(item => ((BoatEntry)item.Tag).Body);
            var occupancy = Occupancy.Build(eligible);
            var edges = new List<IList<int>>();
            var slots = new Dictionary<long, Candidate>();
            for (var b = 0; b < world.Boats.Count; ++b)
            {
                var boat = world.Boats[b];
                var list = new List<int>();
                edges.Add(list);
                if (!boat.Eligible) continue;
                var tiers = new int[5];
                for (var p = 0; p < world.Ports.Count; ++p)
                {
                    if (!FleetPlanner.CanReceive(boat, p, world.Ports[p], settings, out _, true)) continue;
                    try
                    {
                        var best = BerthResolver.Evaluate(world.Sites[p], (BoatEntry)boat.Tag, occupancy, world.SaleBoats, settings).Best;
                        if (best == null) continue;
                        list.Add(p);
                        ++tiers[best.Tier];
                        slots[(long)b << 32 | (uint)p] = best;
                    }
                    catch (Exception exception)
                    {
                        plugin.Warn("New game: evaluating " + boat.Label + " at " + world.Ports[p].Label + " failed: " + exception.Message);
                    }
                }
                if (plugin.DebugEnabled)
                    plugin.DebugLog("  " + boat.Label + ": " + list.Count + " compatible port(s) (T1/T2 " + (tiers[1] + tiers[2]) +
                        ", T3 " + tiers[3] + ", T4 " + tiers[4] + ")" +
                        (FleetPlanner.HasNativeSlot(boat, world.Ports) ? " plus its native berth" : "") +
                        (list.Count > 0 ? " [" + string.Join(", ", list.Select(p => world.Ports[p].Id.Index + ":T" +
                            slots[(long)b << 32 | (uint)p].Tier).ToArray()) + "]" : "") + ".");
            }

            var random = new System.Random(seed);
            var match = FleetPlanner.Match(world.Boats, world.Ports, edges, random);
            if (plugin.DebugEnabled)
            {
                plugin.DebugLog("New game: checked admission attempt order: " +
                    string.Join(", ", match.Order.Select(b => world.Boats[b].Label).ToArray()) + ".");
                plugin.DebugLog("New game: checked places by size: Large " + match.PrimaryCounts[2] +
                    ", Medium " + match.PrimaryCounts[1] + ", Small " + match.PrimaryCounts[0] + ".");
                plugin.DebugLog("New game: checked ports covered " + match.CoveredPorts + "/" +
                    world.Ports.Count(port => port.Receiving) + "; shared-port primary mix: " +
                    string.Join(", ", world.Ports.Select((port, p) => new { port, p }).Where(item => item.port.SharedMix)
                        .Select(item => item.port.Label + " " + match.PrimaryPortCounts[item.p] + " boat(s), " +
                            match.PrimaryLargeCounts[item.p] + " Large").ToArray()) + ".");
                plugin.DebugLog("New game: matching settled after " + match.Rounds + " round(s)" +
                    (match.ConservativeRepair ? "; conservative resident-capacity repair was needed" : "") + ".");
            }
            // The overflow draw consumes the same random sequence as the checked
            // draw, so it must run whether or not it is logged.
            if (settings.RandomizeOverflowBoats)
            {
                var overflow = FleetPlanner.MatchOverflow(world.Boats, world.Ports, edges, match, settings, random);
                plugin.DebugLog("New game: RandomizeOverflowBoats is on; " + overflow +
                    " remaining boat(s) assigned to unchecked destinations after the checked draw.");
            }
            var pending = new List<Pending>();
            var placementOrder = FleetPlanner.RandomizedSizeOrder(world.Boats, random)
                .Where(b => match.Assigned[b] >= 0 && !match.IsNative(world.Boats, b)).ToList();
            if (plugin.DebugEnabled)
                plugin.DebugLog("New game: placement order (Large, Medium, Small): " +
                    string.Join(", ", placementOrder.Select(b => world.Boats[b].Label).ToArray()) + ".");
            foreach (var b in placementOrder)
            {
                var p = match.Assigned[b];
                if (p < 0 || match.IsNative(world.Boats, b)) continue;
                var entry = (BoatEntry)world.Boats[b].Tag;
                var item = new Pending { Boat = world.Boats[b], Entry = entry, Port = p };
                try
                {
                    item.Take(BerthResolver.MakePlan(world.Sites[p], entry, slots[(long)b << 32 | (uint)p]));
                }
                catch (Exception exception)
                {
                    item.PreDrop = "placement plan failed: " + exception.Message;
                }
                pending.Add(item);
            }
            ResolveShared(world, settings, pending);
            ValidateAndApply(world, settings, pending, "New game");
            ArmSaleRig(world);

            var document = new AssignmentDocument { Seed = seed };
            for (var b = 0; b < world.Boats.Count; ++b)
            {
                var boat = world.Boats[b];
                if (!boat.Eligible) continue;
                var moved = pending.FirstOrDefault(item => item.Boat == boat);
                if (moved != null && moved.Plan != null)
                {
                    document.Records.Add(ToRecord(moved));
                    if (!plugin.DebugEnabled) continue;
                    var c = moved.Plan.Candidate;
                    plugin.DebugLog("  Assignment: " + boat.Label + " -> " + moved.Plan.Site.Port.Label + ", " + moved.Plan.Describe() +
                        ", " + DockGeometry.F(c.Distance) + " m from recovery, nearest gap " +
                        (c.Blocker != null ? DockGeometry.F(c.MinimumGap) + " m (" + c.Blocker + ")" : "none in range") +
                        (moved.Plan.Held ? "" : ", lines " + DockGeometry.F(c.FrontLine) + " / " + DockGeometry.F(c.BackLine) + " m") + ".");
                }
                else if (plugin.DebugEnabled)
                {
                    var why = match.IsNative(world.Boats, b) && match.PrimaryAssigned[b] ? "drawn to its native berth"
                        : moved == null ? "no compatible free port"
                        : moved.Move.Reason ?? "not placed";
                    plugin.DebugLog("  Assignment: " + boat.Label + " stays home (" + why + ").");
                }
            }
            plugin.Report("New game: seed " + seed + (config.Seed.Value > 0 ? " (from config)" : " (random)") + "; " +
                document.Records.Count + " boat(s) moved, " + (world.Boats.Count - document.Records.Count) + " stayed.");
            ReportPortCounts(world, pending, "New game");
            return document;
        }

        // A positive configured seed repeats a layout; 0 or less picks a new one.
        internal static int NewGameSeed(int configured, System.Random random) =>
            configured > 0 ? configured : random.Next(1, int.MaxValue);

        private static void ArmSaleRig(FleetWorld world)
        {
            // Sale equipment does not depend on relocation choices or success.
            // Arm validates the exact Leopard and waits for natural activation.
            foreach (var entry in world.KnownBoats.Boats.Where(item => !item.Purchased))
                LeopardSaleRig.Arm(entry);
        }

        private static IEnumerable<int> OwnedLargeHomes(FleetWorld world)
        {
            foreach (var entry in world.KnownBoats.Boats.Where(item => item.Purchased && item.Size == BoatSize.Large))
            {
                // Owned boats may have left their authored moorings. Count only
                // a nearby actual position, rather than their old berth label.
                var home = world.HomeOf(null, entry.Saveable.transform.position);
                if (home >= 0) yield return home;
            }
        }

        // Boats sharing a port (shipyards with MultipleBoatsAtShipyards) are
        // resolved in turn against the boats that really move: a native boat
        // that stays is an obstacle, and each later boat treats the earlier
        // planned hulls and cleats as taken, with the normal gap rules. A boat
        // left without a berth there stays at home (and the cascade follows).
        private static void ResolveShared(FleetWorld world, FleetSettings settings, List<Pending> pending)
        {
            var shared = pending.Where(item => world.Ports[item.Port].Capacity > 1).Select(item => item.Port).Distinct().ToList();
            if (shared.Count == 0) return;
            var plugin = Plugin.Instance;
            var occupancy = Occupancy.Build(pending.Select(item => item.Entry.Body));
            foreach (var p in shared)
            {
                var planned = new List<PlannedBerth>();
                foreach (var item in pending.Where(entry => entry.Port == p))
                {
                    Candidate best = null;
                    try
                    {
                        best = BerthResolver.Evaluate(world.Sites[p], item.Entry, occupancy, world.SaleBoats, settings, planned).Best;
                    }
                    catch (Exception exception)
                    {
                        plugin.Warn("New game: evaluating " + item.Boat.Label + " at shared port " + world.Ports[p].Label + " failed: " + exception.Message);
                    }
                    if (best == null)
                    {
                        item.PreDrop = "no berth left at " + world.Ports[p].Label + " beside " + planned.Count +
                            " boat(s) moving in before it and the boats that stay";
                        continue;
                    }
                    BerthPlan plan;
                    try
                    {
                        plan = BerthResolver.MakePlan(world.Sites[p], item.Entry, best);
                    }
                    catch (Exception exception)
                    {
                        item.PreDrop = "placement plan failed: " + exception.Message;
                        continue;
                    }
                    item.Take(plan);
                    planned.Add(new PlannedBerth { Boat = item.Entry, Candidate = best });
                    plugin.DebugLog("New game: shared port " + world.Ports[p].Label + " (capacity " + world.Ports[p].Capacity + "), boat " +
                        planned.Count + " moving in: " + item.Boat.Label + " -> " + plan.Describe() + ".");
                }
            }
        }

        // Sale boats per port after a fleet is applied: boats that stay plus
        // boats moved in, against each port's capacity (new games; a restored
        // layout has no place limit).
        private static void ReportPortCounts(FleetWorld world, List<Pending> pending, string context, bool restoring = false)
        {
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.DebugEnabled) return;
            var parts = new List<string>();
            for (var p = 0; p < world.Ports.Count; ++p)
            {
                var arrived = pending.Count(item => item.Port == p && item.Plan != null);
                var staying = world.Boats.Count(boat => boat.Home == p && !pending.Any(item => item.Boat == boat && item.Plan != null));
                if (arrived + staying == 0 && (restoring || world.Ports[p].Capacity <= 1)) continue;
                parts.Add(world.Ports[p].Label + " " + (arrived + staying) + (restoring ? "" : "/" + world.Ports[p].Capacity) +
                    (arrived > 0 && staying > 0 ? " (" + staying + " stayed, " + arrived + " moved in)" : ""));
            }
            plugin.DebugLog(context + ": sale boats per port (" + (restoring ? "boats" : "boats/capacity") + "): " + string.Join(", ", parts.ToArray()) + ".");
        }

        internal static void ApplyStored(AssignmentDocument document)
        {
            var plugin = Plugin.Instance;
            var world = FleetWorld.Survey();
            if (plugin.DebugEnabled)
            {
                foreach (var entry in world.KnownBoats.Boats)
                    plugin.DebugLog("Load catalogue: " + entry.Label + ", size " + entry.Size + ", purchased " + entry.Purchased + ".");
                foreach (var rejection in world.KnownBoats.Rejections)
                    plugin.DebugLog("Load catalogue rejection: " + rejection);
            }
            var pending = new List<Pending>();
            // A saved placement is put back exactly; only what makes it
            // impossible skips it.
            foreach (var record in document.Records)
            {
                var id = new Identity(record.BoatIndex, record.BoatName);
                var boat = world.FindBoat(id);
                if (boat == null)
                {
                    // An owned boat holds no port place; its hull and cleats
                    // still block placement through the occupancy.
                    var owned = world.KnownBoats.Boats.FirstOrDefault(item => item.Identity.Equals(id) && item.Purchased);
                    if (owned != null) plugin.DebugLog("Load: " + owned.Label + " is owned now; its stored sale berth no longer applies.");
                    else plugin.Warn("Load: stored boat " + id + " is not catalogued; skipped.");
                    continue;
                }
                var port = world.FindPort(new Identity(record.PortIndex, record.PortName));
                var site = port < 0 ? null : world.Sites[port] ?? BerthResolver.IslandSite(world.Ports[port].Tag as PortEntry);
                if (site == null)
                {
                    plugin.Warn("Load: " + boat.Label + " stays at its native berth: stored port " + record.PortIndex + "|" + record.PortName +
                        (port < 0 ? " is not catalogued." : " has no island."));
                    continue;
                }
                pending.Add(new Pending
                {
                    Boat = boat, Entry = (BoatEntry)boat.Tag, Port = port, Site = site, Tier = record.Kind == RecordKind.Held ? 4 : record.Tier,
                    FrontId = record.FrontCleat, BackId = record.BackCleat, Side = record.Side,
                    LocalPosition = new Vector3(record.LocalX, 0f, record.LocalZ), LocalYaw = record.YawDegrees
                });
            }
            var placed = ValidateAndApply(world, null, pending, "Load", true);
            ArmSaleRig(world);
            if (plugin.DebugEnabled)
            {
                foreach (var item in pending.Where(item => item.Plan != null))
                    plugin.DebugLog("Load: moved " + item.Entry.Label + " to " + item.Plan.Site.Port.Label + ", " + item.Plan.Describe() + ".");
                plugin.DebugLog("Load: Scrambled Seas installed: " + plugin.ScrambledSeasInstalled + ".");
            }
            plugin.Report("Load: applied " + placed + " of " + document.Records.Count + " stored assignment(s) (seed " + document.Seed + ").");
            ReportPortCounts(world, pending, "Load", true);
        }

        // The placement as it was made: tier, cleats and the root pose in the
        // island's frame, which load puts back exactly.
        // The stored pose is the root projected to the island origin's height,
        // in the island's frame (x, z); load rebuilds it at island-local y 0
        // (BerthResolver.StationPose), so store and restore use the same
        // plane and the spawn height never skews x/z.
        private static AssignmentRecord ToRecord(Pending item)
        {
            var plan = item.Plan;
            var port = plan.Site.Port;
            var island = plan.Site.Island;
            var local = island.InverseTransformPoint(new Vector3(plan.Position.x, island.position.y, plan.Position.z));
            var record = new AssignmentRecord
            {
                BoatIndex = item.Boat.Id.Index, BoatName = item.Boat.Id.Name, PortIndex = port.Index, PortName = port.IdentityName,
                Tier = plan.Tier, LocalX = local.x, LocalZ = local.z, YawDegrees = plan.LocalYaw
            };
            if (plan.Held) record.Kind = RecordKind.Held;
            else
            {
                record.FrontCleat = plan.FrontId;
                record.BackCleat = plan.BackId;
                record.Side = plan.Tier == 3 ? plan.Candidate.Side : 0;
            }
            return record;
        }

        // A saved placement at its exact stored pose. Moored on its stored
        // cleats when both resolve to a spring no staying boat holds;
        // otherwise held (station-kept) at the same pose. No clearance or
        // geometry checks: the layout passed them when it was generated.
        private static bool TryStoredPlan(Pending item, Occupancy occupancy, out BerthPlan plan, out string reason)
        {
            plan = null;
            reason = null;
            var site = item.Site;
            var entry = item.Entry;
            BerthResolver.StationPose(site, item.LocalPosition, item.LocalYaw, out var position, out var rotation);
            var root = HullFootprint.Flat(position);
            var forward = HullFootprint.Flat(rotation * Vector3.forward);
            GPButtonDockMooring front = null, back = null;
            Vec2? frontAt = null, backAt = null;
            string unusable = null;
            if (item.Tier != 4)
            {
                var frontProblem = BerthResolver.StoredCleat(site, item.FrontId, occupancy, entry.Body, out front, out var frontPosition);
                var backProblem = BerthResolver.StoredCleat(site, item.BackId, occupancy, entry.Body, out back, out var backPosition);
                item.CleatKeys = new[] { front, back }.Where(cleat => cleat != null).Select(cleat => FleetWorld.CleatKey(cleat.transform)).ToArray();
                unusable = frontProblem ?? backProblem;
                if (unusable == null)
                {
                    frontAt = frontPosition;
                    backAt = backPosition;
                }
            }
            var candidate = BerthTiers.StoredBerth(entry.Spec, root, forward, item.Tier, item.Side, frontAt, backAt, out var fallback);
            var plugin = Plugin.Instance;
            if (candidate.Tier == 4 && item.Tier != 4)
                plugin?.DebugLog("Load: " + entry.Label + " is held at its stored pose instead of moored (" + (unusable ?? fallback) + ").");
            if (plugin != null && plugin.DebugEnabled)
            {
                var overlaps = BerthResolver.Overlaps(occupancy, entry, candidate);
                if (overlaps != null) plugin.DebugLog("Load: " + entry.Label + " is placed at its stored pose overlapping " + overlaps + ".");
            }
            plan = BerthResolver.MakeStoredPlan(site, entry, candidate, front, back, item.FrontId, item.BackId, item.LocalPosition, item.LocalYaw);
            return true;
        }

        // Re-resolves one fresh planned berth under the current occupancy.
        internal static bool TryResolve(DockSite site, BoatEntry entry, Occupancy occupancy, IEnumerable<BoatEntry> saleBoats,
            int tier, string frontId, string backId, int side, Vector3 localPosition, float localYaw,
            IEnumerable<PlannedBerth> planned, out BerthPlan plan, out string reason)
        {
            plan = null;
            var input = BerthResolver.MakePortInput(site, entry, occupancy, saleBoats, planned);
            Candidate candidate;
            if (tier == 4)
            {
                BerthResolver.StationPose(site, localPosition, localYaw, out var position, out var rotation);
                var forward = HullFootprint.Flat(rotation * Vector3.forward);
                var center = BerthTiers.CenterFromRoot(entry.Spec, HullFootprint.Flat(position), forward);
                candidate = BerthTiers.EvaluateStationAt(input, entry.Spec, center, forward);
            }
            else
            {
                var front = site.IndexOf(frontId);
                var back = site.IndexOf(backId);
                candidate = front < 0 || back < 0
                    ? DockGeometry.Reject(new Candidate { Tier = tier }, "planned cleats are not island cleats of " + site.Port.Label)
                    : BerthTiers.EvaluateMoored(input, entry.Spec, tier, front, back, side);
            }
            if (Plugin.Instance != null && Plugin.Instance.DebugEnabled)
                Plugin.Instance.DebugLog("  " + entry.Label + " at " + site.Port.Label + ": " + BerthResolver.Describe(input, candidate));
            if (!candidate.Accepted)
            {
                reason = candidate.Reason;
                return false;
            }
            try
            {
                plan = BerthResolver.MakePlan(site, entry, candidate);
            }
            catch (Exception exception)
            {
                reason = "placement plan failed: " + exception.Message;
                return false;
            }
            if (tier == 4)
            {
                // Keep the planned X/Z and heading. Height comes from this boat's
                // current native buoyancy model, just as for fresh placement.
                plan.LocalYaw = localYaw;
                BerthResolver.StationPose(site, localPosition, localYaw, out var position, out var rotation);
                plan.Position = new Vector3(position.x, plan.Position.y, position.z);
                plan.LocalPosition = site.Island.InverseTransformPoint(plan.Position);
                plan.Rotation = rotation;
            }
            else if (plan.FrontId != frontId)
                Plugin.Instance?.DebugLog("Front/back cleats for " + entry.Label + " follow the live recovery heading.");
            reason = null;
            return true;
        }

        // Re-resolve every planned berth with only the planned boats treated as
        // moving (at a shared port, each boat also clears the berths planned
        // before it there), cascade stay-home and capacity conflicts, and
        // repeat until stable; then move the fleet. Returns the number of
        // boats placed. restoring (load): a saved placement is put back at its
        // stored pose (TryStoredPlan) instead of re-resolved, and the cascade
        // applies no place or Large limit: only a berth that a boat which
        // stayed still holds (its claimed cleats) or a duplicate cleat drops it.
        private static int ValidateAndApply(FleetWorld world, FleetSettings settings, List<Pending> pending,
            string context, bool restoring = false)
        {
            var plugin = Plugin.Instance;
            if (!restoring && settings == null) throw new ArgumentNullException(nameof(settings));
            Diagnostics.MooringCensus(world.SaleBoats, context + " before validation");
            foreach (var item in pending)
            {
                item.Move = new FleetMove
                {
                    Boat = item.Boat.Label, Home = world.PortKey(item.Boat.Home), Target = world.PortKey(item.Port),
                    Claims = item.Boat.Claims, Size = item.Boat.Size
                };
                if (item.PreDrop == null) continue;
                item.Move.Dropped = true;
                item.Move.Reason = item.PreDrop;
                plugin.Warn(context + ": " + item.Boat.Label + " stays at its native berth (" + item.PreDrop + ").");
            }
            var moves = pending.Select(item => item.Move).ToList();
            Func<string, int> capacity = key => int.MaxValue;
            if (!restoring)
            {
                var capacities = FleetPlanner.PlacementCapacities(world.Boats, world.Ports, settings, pending.Select(item => item.Boat));
                foreach (var port in world.Ports) port.Capacity = capacities[port.Id.ToString()];
                capacity = key => key != null && capacities.TryGetValue(key, out var places) ? places : 1;
            }
            // New game: unpurchased boats without a move hold places and the
            // shared-shipyard Large ledger applies (dropped moves are counted by
            // the cascade itself). Load has no place or Large limit.
            var homes = new List<string>();
            List<string> largeHomes = null;
            Func<string, int> largeCapacity = null;
            if (!restoring)
            {
                homes = world.Boats.Where(boat => boat.Home >= 0 && !pending.Any(item => item.Boat == boat))
                    .Select(boat => world.PortKey(boat.Home)).ToList();
                largeHomes = world.Boats.Where(boat => boat.Size == BoatSize.Large &&
                    boat.Home >= 0 && !pending.Any(item => item.Boat == boat)).Select(boat => world.PortKey(boat.Home))
                    .Concat(OwnedLargeHomes(world).Select(world.PortKey)).ToList();
                var shared = new HashSet<string>(world.Ports.Where(port => port.SharedMix).Select(port => port.Id.ToString()), StringComparer.Ordinal);
                largeCapacity = key => shared.Contains(key) ? 1 : int.MaxValue;
            }
            Occupancy occupancy;
            HashSet<string> claims;
            while (true)
            {
                var active = pending.Where(item => !item.Move.Dropped).ToList();
                occupancy = Occupancy.Build(active.Select(item => item.Entry.Body));
                var planned = new Dictionary<int, List<PlannedBerth>>();
                var failed = false;
                foreach (var item in active)
                {
                    BerthPlan plan = null;
                    string reason;
                    if (!planned.TryGetValue(item.Port, out var before)) planned[item.Port] = before = new List<PlannedBerth>();
                    try
                    {
                        if (restoring ? TryStoredPlan(item, occupancy, out plan, out reason)
                            : TryResolve(world.Sites[item.Port], item.Entry, occupancy, world.SaleBoats, item.Tier, item.FrontId,
                                item.BackId, item.Side, item.LocalPosition, item.LocalYaw, before, out plan, out reason))
                            BoatRelocator.TryCheckPreconditions(plan, occupancy, out reason);
                    }
                    catch (Exception exception)
                    {
                        reason = exception.Message;
                    }
                    if (reason == null)
                    {
                        item.Move.Tag = plan;
                        item.Move.TargetCleats = restoring ? item.CleatKeys : plan.Held ? new string[0]
                            : new[] { FleetWorld.CleatKey(plan.Front.transform), FleetWorld.CleatKey(plan.Back.transform) };
                        before.Add(new PlannedBerth { Boat = item.Entry, Candidate = plan.Candidate });
                        continue;
                    }
                    item.Move.Dropped = true;
                    item.Move.Reason = "validation failed: " + reason;
                    plugin.Warn(context + ": " + item.Boat.Label + " stays at its native berth (" + item.Move.Reason + ").");
                    failed = true;
                }
                var staying = world.Boats.Where(boat => !pending.Any(item => item.Boat == boat && !item.Move.Dropped)).ToList();
                claims = new HashSet<string>(staying.SelectMany(boat => boat.Claims), StringComparer.Ordinal);
                var dropped = FleetPlanner.Cascade(moves, homes, claims, capacity, largeHomes, largeCapacity);
                foreach (var move in dropped)
                    plugin.Warn(context + ": " + move.Boat + " stays at its native berth (" + move.Reason + ").");
                if (!failed && dropped.Count == 0) break;
            }
            if (moves.All(move => move.Dropped))
            {
                Diagnostics.MooringCensus(world.SaleBoats, context + " after validation (no placements)");
                return 0;
            }
            var placed = BoatRelocator.ApplyFleet(moves, homes, claims, occupancy, context, capacity, largeHomes, largeCapacity);
            Diagnostics.MooringCensus(world.SaleBoats, context + " after placement");
            foreach (var item in pending)
            {
                item.Plan = !item.Move.Dropped && placed.Contains(item.Move.Tag as BerthPlan) ? (BerthPlan)item.Move.Tag : null;
            }
            return placed.Count;
        }
    }
}
