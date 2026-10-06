using System;
using System.Collections.Generic;
using System.Linq;

namespace ShipShuffle
{
    internal sealed class FleetSettings
    {
        internal bool IncludeVanillaBoats = true;
        internal bool IncludeModBoats = true;
        internal bool AllowSmall = true;
        internal bool AllowMedium = true;
        internal bool AllowLarge = true;
        internal bool IncludeHiddenPorts;
        // Ports added by other mods (Bottleneck Is.) receive boats only when enabled.
        internal bool IncludeModPorts;
        // Gold Rock City, Dragon Cliffs and Fort Aestrin get 3 shuffle places.
        internal bool MultipleBoatsAtShipyards;
        internal bool RandomizeOverflowBoats = true;
        internal SelectionExclusions ExcludedBoats = new SelectionExclusions();
        internal SelectionExclusions ExcludedPorts = new SelectionExclusions();
        // The fixed tables, copied so a caller may vary them.
        internal HashSet<Identity> LargeBoatExcludedPorts = new HashSet<Identity>(KnownIdentities.LargeBoatExcludedPorts);
        internal HashSet<Tuple<Identity, Identity>> BoatPortExclusions = new HashSet<Tuple<Identity, Identity>>(KnownIdentities.BoatPortExclusions);

        internal bool SizeAllowed(BoatSize size) =>
            size == BoatSize.Small ? AllowSmall : size == BoatSize.Medium ? AllowMedium : AllowLarge;

        // New-game shuffle places. Stationary residents add places only at
        // shipyards. Restoring a saved layout applies no place limit.
        internal int CapacityOf(Identity port) =>
            MultipleBoatsAtShipyards && KnownIdentities.Shipyards.Contains(port) ? KnownIdentities.ShipyardCapacity : 1;

        internal int CapacityWithResidents(Identity port, int residents) =>
            CapacityOf(port) + (KnownIdentities.Shipyards.Contains(port) ? Math.Max(0, residents) : 0);
    }

    // One unpurchased sale boat. Purchased boats never enter the planner.
    internal sealed class FleetBoat
    {
        internal Identity Id;
        internal string Label;
        internal BoatSize? Size;
        internal bool Vanilla;
        // False for boats the catalogue could not validate; they never move.
        internal bool Catalogued = true;
        internal string CatalogReason;
        internal int Home = -1;
        internal string[] Claims = new string[0];
        internal bool Eligible;
        internal string Reason;
        internal object Tag;
    }

    internal sealed class FleetPort
    {
        internal Identity Id;
        internal string Label;
        internal bool HasSite;
        internal string SiteReason;
        internal bool Vanilla = true;
        internal bool Blocked;
        // Shuffle allowance plus reserved resident places at shipyards.
        internal int Capacity = 1;
        // Ineligible native boats; each always keeps one place here.
        internal int Fixed;
        // Only the active exact shipyard allowance has a participating Large cap.
        internal bool SharedMix;
        internal int FixedLarge;
        internal bool Receiving;
        // Unchecked destinations admitted only for the second draw.
        internal bool OverflowReceiving;
        internal string Reason;
        internal object Tag;
    }

    internal sealed class MatchResult
    {
        // Port per boat (-1 = unmatched). Native when it equals the boat's home.
        internal int[] Assigned;
        internal int Rounds;
        internal int[] Order = new int[0];
        // Captured before terminal native fallback or the frozen overflow draw.
        internal bool[] PrimaryAssigned = new bool[0];
        internal int[] PrimaryCounts = new int[3]; // Small, Medium, Large
        internal bool ConservativeRepair;
        internal int[] PrimaryPortCounts = new int[0];
        internal int[] PrimaryLargeCounts = new int[0];
        internal int CoveredPorts;

        internal bool IsNative(IList<FleetBoat> boats, int boat) => Assigned[boat] >= 0 && Assigned[boat] == boats[boat].Home;
    }

    // A planned move between ports, keyed by stable strings so the cascade
    // works the same for live objects and offline fixtures.
    internal sealed class FleetMove
    {
        internal string Boat;
        internal BoatSize? Size;
        internal string Home;
        internal string Target;
        internal string[] TargetCleats = new string[0];
        internal string[] Claims = new string[0];
        internal bool Dropped;
        internal string Reason;
        internal object Tag;
    }

    // Unity-free eligibility, randomized matching and the stay-home cascade.
    internal static class FleetPlanner
    {
        internal static void Classify(IList<FleetBoat> boats, IList<FleetPort> ports, FleetSettings settings)
        {
            foreach (var boat in boats)
            {
                boat.Eligible = false;
                if (!boat.Catalogued) boat.Reason = "not movable: " + (boat.CatalogReason ?? "failed catalogue checks");
                else if (!boat.Size.HasValue) boat.Reason = "unknown size class";
                else if (boat.Vanilla && !settings.IncludeVanillaBoats) boat.Reason = "vanilla boats are disabled";
                else if (!boat.Vanilla && !settings.IncludeModBoats) boat.Reason = "mod boats are disabled";
                else if (!settings.SizeAllowed(boat.Size.Value)) boat.Reason = boat.Size.Value + " boats are disabled";
                else if (settings.ExcludedBoats.Contains(boat.Id.Name))
                    boat.Reason = "listed in ExcludedBoats";
                else { boat.Eligible = true; boat.Reason = "eligible"; }
            }
            for (var p = 0; p < ports.Count; ++p)
            {
                var port = ports[p];
                // Ineligible residents keep their hulls and cleats. At the
                // three shipyards they also keep separate resident places.
                var blockers = boats.Where(boat => !boat.Eligible && boat.Home == p).ToList();
                var blocker = blockers.FirstOrDefault();
                port.Fixed = blockers.Count;
                port.FixedLarge = blockers.Count(boat => boat.Size == BoatSize.Large);
                port.SharedMix = settings.MultipleBoatsAtShipyards && KnownIdentities.Shipyards.Contains(port.Id);
                port.Capacity = settings.CapacityWithResidents(port.Id, port.Fixed);
                port.Blocked = blocker != null && port.Fixed >= port.Capacity;
                port.Receiving = false;
                port.OverflowReceiving = false;
                if (port.Blocked) port.Reason = "blocked: keeps its native boat " + blocker.Label + " (" + blocker.Reason + ")";
                else if (!port.HasSite) port.Reason = "no usable dock line: " + port.SiteReason;
                else if (KnownIdentities.IsHiddenPort(port.Id) && !settings.IncludeHiddenPorts) port.Reason = "secret destinations are disabled";
                else if (!port.Vanilla && !settings.IncludeModPorts) port.Reason = "mod-added port (IncludeModPorts is off)";
                else if (settings.ExcludedPorts.Contains(port.Id.Name))
                {
                    port.OverflowReceiving = settings.RandomizeOverflowBoats;
                    port.Reason = port.OverflowReceiving ? "unchecked, available to remaining boats" : "listed in ExcludedPorts";
                }
                else
                {
                    port.Receiving = true;
                    port.Reason = port.Capacity > 1
                        ? "receiving, up to " + port.Capacity + " sale boats" + (port.Fixed > 0 ? " (" + port.Fixed + " kept by native boats that cannot move)" : "")
                        : "receiving";
                }
            }
        }

        // New-game quota, frozen before validation or placement can drop moves.
        // Eligible natives that stay still use a shuffle place, as in
        // matching; ineligible residents keep separate places at shipyards.
        // Purchased boats never hold a place; their hulls and cleats still
        // block placement through the occupancy. No later failure grows quota.
        internal static Dictionary<string, int> PlacementCapacities(IList<FleetBoat> boats, IList<FleetPort> ports,
            FleetSettings settings, IEnumerable<FleetBoat> planned)
        {
            var moving = new HashSet<FleetBoat>(planned);
            var residents = boats.Where(boat => boat.Home >= 0 && boat.Home < ports.Count && !moving.Contains(boat) && !boat.Eligible)
                .Select(boat => ports[boat.Home].Id.ToString())
                .GroupBy(home => home, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            return ports.ToDictionary(port => port.Id.ToString(), port => settings.CapacityWithResidents(port.Id,
                residents.TryGetValue(port.Id.ToString(), out var count) ? count : 0), StringComparer.Ordinal);
        }

        // Whether boat may be moved to port (its own home is never a move).
        internal static bool CanReceive(FleetBoat boat, int portIndex, FleetPort port, FleetSettings settings, out string reason,
            bool includeOverflow = false)
        {
            reason = null;
            if (!boat.Eligible) reason = "boat is not eligible";
            else if (boat.Home == portIndex) reason = "home port";
            else if (!port.Receiving && !(includeOverflow && settings.RandomizeOverflowBoats && port.OverflowReceiving)) reason = port.Reason;
            else if (boat.Size == BoatSize.Large && settings.LargeBoatExcludedPorts.Contains(port.Id))
                reason = "LargeBoatExcludedPorts";
            else if (settings.BoatPortExclusions.Contains(Tuple.Create(boat.Id, port.Id))) reason = "BoatPortExclusions";
            return reason == null;
        }

        internal static bool HasNativeSlot(FleetBoat boat, IList<FleetPort> ports) =>
            boat.Eligible && boat.Home >= 0 && boat.Home < ports.Count && !ports[boat.Home].Blocked;

        // Fair admission among active size pools. Compatible destinations and
        // peers are shuffled once, independently of native fallback repair.
        // Every receiving native edge is one ordinary draw choice. Actual
        // unmatched residents reserve their homes; departed owners release them.
        // A repeated reservation state uses conservative closure to terminate
        // safely rather than oscillating. This is not uniform global sampling.
        internal static MatchResult Match(IList<FleetBoat> boats, IList<FleetPort> ports, IList<IList<int>> edges, Random random,
            bool drawNative = true)
        {
            var m = new MatchState
            {
                Boats = boats, Ports = ports, Places = new Places(boats, ports),
                Result = new MatchResult { Assigned = Enumerable.Repeat(-1, boats.Count).ToArray() }
            };
            m.Adjacency = BuildAdjacency(m, edges, random, drawNative);
            m.Pools = ShuffledPools(boats, random);
            m.Categories = Enumerable.Range(0, 3).ToList();
            Shuffle(m.Categories, random);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                ++m.Result.Rounds;
                DrawPrimary(m);
                if (ReserveUnmatchedHomes(m, seen)) continue;
                RetryReservedOwners(m);
                BalanceExchange(m);
                SpreadExtraPlaces(m);
                RecordPrimaryResult(m);
                AddNativeFallbacks(m);
                return m.Result;
            }
        }

        // Working state of one Match call. Counts and Order belong to the
        // current round's draw.
        private sealed class MatchState
        {
            internal IList<FleetBoat> Boats;
            internal IList<FleetPort> Ports;
            internal Places Places;
            internal MatchResult Result;
            internal List<int>[] Adjacency;
            internal List<int>[] Pools;   // eligible boats per size (Small, Medium, Large), shuffled
            internal List<int> Categories; // shuffled size order
            internal int[] Counts;        // matched boats per size
            internal List<int> Order;     // admission attempts, in order

            // A fresh visited set for one augmenting-path search.
            internal bool[] Unvisited() => new bool[2 * Ports.Count];
        }

        // Each eligible boat's destinations: valid, receiving, unblocked ports
        // with a place, plus its native berth when drawNative. Shuffled once,
        // then stably ordered by the soft port size preference.
        private static List<int>[] BuildAdjacency(MatchState m, IList<IList<int>> edges, Random random, bool drawNative)
        {
            var boats = m.Boats;
            var ports = m.Ports;
            var adjacency = new List<int>[boats.Count];
            for (var b = 0; b < boats.Count; ++b)
            {
                adjacency[b] = new List<int>();
                if (!boats[b].Eligible) continue;
                if (edges != null && b < edges.Count && edges[b] != null)
                    foreach (var p in edges[b])
                        if (p >= 0 && p < ports.Count && p != boats[b].Home && m.Places.TotalLimit(p) > 0 && ports[p].Receiving &&
                            !ports[p].Blocked && !adjacency[b].Contains(p)) adjacency[b].Add(p);
                if (drawNative && HasNativeSlot(boats[b], ports) && ports[boats[b].Home].Receiving)
                    adjacency[b].Add(boats[b].Home);
                Shuffle(adjacency[b], random);
                // Existing soft port preference retains every compatible edge.
                adjacency[b] = adjacency[b].OrderBy(p =>
                {
                    var preferred = KnownIdentities.PreferredMaximumSize(ports[p].Id);
                    return !preferred.HasValue ? 0 : boats[b].Size <= preferred.Value ? -1 : 1;
                }).ToList();
            }
            return adjacency;
        }

        // Eligible boats per size class, each pool shuffled (Small, Medium, Large).
        private static List<int>[] ShuffledPools(IList<FleetBoat> boats, Random random)
        {
            var pools = Enumerable.Range(0, 3).Select(size => Enumerable.Range(0, boats.Count)
                .Where(b => boats[b].Eligible && (int)boats[b].Size.Value - 1 == size).ToList()).ToArray();
            foreach (var pool in pools) Shuffle(pool, random);
            return pools;
        }

        // One round's draw under the current home reservations. Admission
        // alternates over the size categories, always serving the category
        // with the fewest matches (ties by the shuffled category order).
        // Spread one participating place per selected destination first.
        // Fixed residents have separate places and do not consume this
        // first pass. Then retry every unmatched peer with extras open.
        private static void DrawPrimary(MatchState m)
        {
            var state = m.Places;
            state.Clear();
            m.Counts = new int[3];
            m.Order = new List<int>();
            for (var pass = 0; pass < 2; ++pass)
            {
                state.Spread = pass == 0;
                var candidates = m.Pools.Select(pool => pool.Where(b => !state.Contains(b)).ToList()).ToArray();
                var next = new int[3];
                while (m.Categories.Any(size => next[size] < candidates[size].Count))
                {
                    var active = m.Categories.Where(size => next[size] < candidates[size].Count).ToList();
                    var least = active.Min(size => m.Counts[size]);
                    var size = active.First(group => m.Counts[group] == least);
                    var boat = candidates[size][next[size]++];
                    m.Order.Add(boat);
                    if (state.Augment(boat, m.Adjacency, m.Unvisited())) ++m.Counts[size];
                }
            }
            m.Result.Order = m.Order.ToArray();
            RebuildAssigned(m);
        }

        // Reserves the home places of eligible boats left unmatched. Returns
        // true when the reservations changed and the draw must run again. A
        // repeated reservation state (or too many rounds) switches to
        // conservative closure, which only ever adds reservations.
        private static bool ReserveUnmatchedHomes(MatchState m, HashSet<string> seen)
        {
            var state = m.Places;
            var result = m.Result;
            var required = new int[m.Ports.Count];
            var requiredLarge = new int[m.Ports.Count];
            CountUnmatchedHomes(m, required, requiredLarge);
            if (!result.ConservativeRepair && (!seen.Add(string.Join(",", state.Kept) + ";" + string.Join(",", state.KeptLarge)) ||
                result.Rounds > Math.Max(8, 4 * m.Boats.Count + 2 * m.Ports.Count))) result.ConservativeRepair = true;
            var changed = false;
            for (var p = 0; p < m.Ports.Count; ++p)
            {
                var keep = result.ConservativeRepair ? Math.Max(state.Kept[p], required[p]) : required[p];
                var keepLarge = result.ConservativeRepair ? Math.Max(state.KeptLarge[p], requiredLarge[p]) : requiredLarge[p];
                if (keep != state.Kept[p] || keepLarge != state.KeptLarge[p]) changed = true;
                state.Kept[p] = keep;
                state.KeptLarge[p] = keepLarge;
            }
            if (changed) return true;
            // Keep this safe assignment fixed while reclaiming only actual
            // fallback reservations. This phase never restarts the draw,
            // so conservative cycle closure cannot leave stale owners pinned.
            Array.Copy(required, state.Kept, state.Kept.Length);
            Array.Copy(requiredLarge, state.KeptLarge, state.KeptLarge.Length);
            return false;
        }

        // A still-unmatched owner can release its own reservation
        // atomically with a successful primary draw. Keeping that place
        // closed during its retry would anchor it unnecessarily at home.
        // Categories are retried fewest matches first, until nothing moves.
        private static void RetryReservedOwners(MatchState m)
        {
            var boats = m.Boats;
            var state = m.Places;
            var result = m.Result;
            var retry = m.Categories.Select(size => m.Pools[size].Where(b => result.Assigned[b] < 0).ToList()).ToArray();
            var progress = true;
            while (progress)
            {
                progress = false;
                var waiting = m.Categories.OrderBy(size => m.Counts[size]).ToList();
                foreach (var size in waiting)
                    foreach (var boat in retry[m.Categories.IndexOf(size)])
                    {
                        if (result.Assigned[boat] >= 0) continue;
                        var home = boats[boat].Home;
                        if (home < 0 || home >= m.Ports.Count || state.Kept[home] == 0) continue;
                        --state.Kept[home];
                        if (boats[boat].Size == BoatSize.Large) --state.KeptLarge[home];
                        if (!state.Augment(boat, m.Adjacency, m.Unvisited()))
                        {
                            ++state.Kept[home];
                            if (boats[boat].Size == BoatSize.Large) ++state.KeptLarge[home];
                            continue;
                        }
                        ++m.Counts[size];
                        m.Order.Add(boat);
                        progress = true;
                        CopyOccupants(m);
                    }
            }
            result.Order = m.Order.ToArray();
        }

        // A failed early peer need not make its whole category
        // infeasible. Exchange a surplus category's selected boat
        // for an unmatched peer when this preserves coverage and
        // strictly improves the balance. Unsafe home fallbacks veto
        // the exchange without changing the existing assignment.
        private static void BalanceExchange(MatchState m)
        {
            var boats = m.Boats;
            var ports = m.Ports;
            var state = m.Places;
            var result = m.Result;
            var counts = m.Counts;
            var improved = true;
            while (improved)
            {
                improved = false;
                foreach (var size in m.Categories.OrderBy(group => counts[group]))
                    foreach (var boat in m.Pools[size])
                    {
                        if (result.Assigned[boat] >= 0) continue;
                        var before = result.Assigned.ToArray();
                        var keptBefore = state.Kept.ToArray();
                        var largeBefore = state.KeptLarge.ToArray();
                        var home = boats[boat].Home;
                        if (home >= 0 && home < ports.Count && state.Kept[home] > 0)
                        {
                            --state.Kept[home];
                            if (boats[boat].Size == BoatSize.Large) --state.KeptLarge[home];
                        }
                        var success = state.Augment(boat, m.Adjacency, m.Unvisited(),
                            occupant => counts[(int)boats[occupant].Size.Value - 1] > counts[size] + 1,
                            occupant => m.Pools[(int)boats[occupant].Size.Value - 1].Where(peer => before[peer] < 0));
                        RebuildAssigned(m);
                        var safe = success && Enumerable.Range(0, ports.Count).All(p =>
                        {
                            var arrivals = boats.Where((item, b) => result.Assigned[b] == p && item.Home != p).Count();
                            return (arrivals == 0 || Staying(boats, ports, result.Assigned, p) + arrivals <= ports[p].Capacity) &&
                                MixSafe(boats, ports, result.Assigned, p);
                        });
                        if (!safe)
                        {
                            result.Assigned = before;
                            Array.Copy(keptBefore, state.Kept, state.Kept.Length);
                            Array.Copy(largeBefore, state.KeptLarge, state.KeptLarge.Length);
                            state.Clear();
                            for (var b = 0; b < boats.Count; ++b) if (before[b] >= 0) state.Add(b, before[b]);
                            continue;
                        }
                        for (var group = 0; group < m.Pools.Length; ++group)
                            counts[group] = m.Pools[group].Count(peer => result.Assigned[peer] >= 0);
                        CountUnmatchedHomes(m, state.Kept, state.KeptLarge);
                        m.Order.Add(boat);
                        improved = true;
                    }
            }
            result.Order = m.Order.ToArray();
        }

        // Retain cardinality and category counts while moving a peer
        // from a shared extra place into a compatible empty destination.
        private static void SpreadExtraPlaces(MatchState m)
        {
            var boats = m.Boats;
            var ports = m.Ports;
            var state = m.Places;
            var assigned = m.Result.Assigned;
            var spreadAgain = true;
            while (spreadAgain)
            {
                spreadAgain = false;
                foreach (var size in m.Categories)
                    foreach (var boat in m.Pools[size])
                    {
                        var source = assigned[boat];
                        if (source < 0 || state.Occupants[source].Count <= 1) continue;
                        foreach (var target in m.Adjacency[boat])
                        {
                            if (state.Occupants[target].Count != 0) continue;
                            assigned[boat] = target;
                            var arrivals = boats.Where((item, b) => assigned[b] == target && item.Home != target).Count();
                            if (!MixSafe(boats, ports, assigned, target) ||
                                arrivals > 0 && Staying(boats, ports, assigned, target) + arrivals > ports[target].Capacity)
                            { assigned[boat] = source; continue; }
                            state.Move(boat, source, target);
                            spreadAgain = true;
                            break;
                        }
                    }
            }
        }

        // The primary draw's statistics, captured before native fallbacks.
        private static void RecordPrimaryResult(MatchState m)
        {
            var result = m.Result;
            result.PrimaryPortCounts = m.Places.Occupants.Select(list => list.Count).ToArray();
            result.PrimaryLargeCounts = m.Places.Occupants.Select(list => list.Count(b => m.Boats[b].Size == BoatSize.Large)).ToArray();
            result.CoveredPorts = result.PrimaryPortCounts.Count(count => count > 0);
            result.PrimaryAssigned = result.Assigned.Select(p => p >= 0).ToArray();
            result.PrimaryCounts = m.Counts;
        }

        // Unmatched boats with a native slot stay at home while places remain.
        // Fallbacks cannot enter augmentation, displace a checked draw,
        // or consume a category's primary share.
        private static void AddNativeFallbacks(MatchState m)
        {
            var boats = m.Boats;
            var result = m.Result;
            var occupied = m.Places.Occupants.Select(list => list.Count).ToArray();
            for (var b = 0; b < boats.Count; ++b)
            {
                var home = boats[b].Home;
                if (result.Assigned[b] >= 0 || !HasNativeSlot(boats[b], m.Ports) || occupied[home] >= m.Places.TotalLimit(home)) continue;
                result.Assigned[b] = home;
                ++occupied[home];
            }
        }

        // Assigned from the current occupants; every other boat is unmatched.
        private static void RebuildAssigned(MatchState m)
        {
            for (var b = 0; b < m.Boats.Count; ++b) m.Result.Assigned[b] = -1;
            CopyOccupants(m);
        }

        // Writes each occupant's port into Assigned, leaving other entries.
        private static void CopyOccupants(MatchState m)
        {
            for (var p = 0; p < m.Ports.Count; ++p)
                foreach (var occupant in m.Places.Occupants[p]) m.Result.Assigned[occupant] = p;
        }

        // Clears homes/largeHomes, then counts each eligible unmatched boat
        // at its home (and Large boats again in largeHomes).
        private static void CountUnmatchedHomes(MatchState m, int[] homes, int[] largeHomes)
        {
            Array.Clear(homes, 0, homes.Length);
            Array.Clear(largeHomes, 0, largeHomes.Length);
            for (var b = 0; b < m.Boats.Count; ++b)
            {
                var boat = m.Boats[b];
                if (boat.Eligible && m.Result.Assigned[b] < 0 && boat.Home >= 0 && boat.Home < m.Ports.Count)
                {
                    ++homes[boat.Home];
                    if (boat.Size == BoatSize.Large) ++largeHomes[boat.Home];
                }
            }
        }

        // Sale boats that stay at port p after a matching: its ineligible
        // natives plus its eligible natives that were not moved elsewhere.
        internal static int Staying(IList<FleetBoat> boats, IList<FleetPort> ports, IList<int> assigned, int p) =>
            ports[p].Fixed + boats.Where((boat, b) => boat.Eligible && boat.Home == p && (assigned[b] < 0 || assigned[b] == p)).Count();

        // Fresh-generation guard only. Existing native over-limit remains
        // untouched when no additional Large arrives. Resident bonuses never
        // increase this allowance. Callers count all known stationary Large.
        internal static bool SharedLargeArrivalsAllowed(FleetPort port, int stayingLarge, int arrivingLarge) =>
            !port.SharedMix || arrivingLarge == 0 || stayingLarge + arrivingLarge <= 1;

        private static bool MixSafe(IList<FleetBoat> boats, IList<FleetPort> ports, IList<int> assigned, int p)
        {
            var arriving = boats.Where((boat, b) => boat.Size == BoatSize.Large && assigned[b] == p && boat.Home != p).Count();
            var staying = ports[p].FixedLarge + boats.Where((boat, b) => boat.Eligible && boat.Size == BoatSize.Large &&
                boat.Home == p && (assigned[b] < 0 || assigned[b] == p)).Count();
            return SharedLargeArrivalsAllowed(ports[p], staying, arriving);
        }

        // The primary draw is complete and immutable. Only boats outside its
        // checked destinations participate, and every primary occupant reserves
        // its place. The ordinary native repair also runs in this smaller draw.
        internal static int MatchOverflow(IList<FleetBoat> boats, IList<FleetPort> ports, IList<IList<int>> edges,
            MatchResult primary, FleetSettings settings, Random random)
        {
            if (!settings.RandomizeOverflowBoats) return 0;
            var remaining = Enumerable.Range(0, boats.Count).Where(b => boats[b].Eligible &&
                (primary.Assigned[b] < 0 || primary.Assigned[b] == boats[b].Home) &&
                !(primary.Assigned[b] >= 0 && ports[primary.Assigned[b]].Receiving)).ToList();
            if (remaining.Count == 0) return 0;
            var participating = new HashSet<int>(remaining);
            var reserved = new int[ports.Count];
            var reservedLarge = new int[ports.Count];
            for (var b = 0; b < boats.Count; ++b)
            {
                if (!boats[b].Eligible || participating.Contains(b)) continue;
                var p = primary.Assigned[b] >= 0 ? primary.Assigned[b] : boats[b].Home;
                if (p >= 0 && p < ports.Count)
                {
                    ++reserved[p];
                    if (boats[b].Size == BoatSize.Large) ++reservedLarge[p];
                }
            }
            var secondaryPorts = ports.Select((port, p) => new FleetPort
            {
                Id = port.Id, Label = port.Label, Capacity = port.Capacity,
                Fixed = port.Fixed + reserved[p],
                SharedMix = port.SharedMix, FixedLarge = port.FixedLarge + reservedLarge[p],
                Blocked = port.Blocked || port.Fixed + reserved[p] >= port.Capacity,
                Receiving = port.OverflowReceiving, Reason = port.Reason
            }).ToList();
            var secondaryBoats = remaining.Select(b => boats[b]).ToList();
            // Filter again through policy, even when a caller provides a graph.
            var secondaryEdges = remaining.Select(b => (IList<int>)(edges != null && b < edges.Count && edges[b] != null
                ? edges[b].Where(p => p >= 0 && p < ports.Count && ports[p].OverflowReceiving &&
                    CanReceive(boats[b], p, ports[p], settings, out _, true)).ToList() : new List<int>())).ToList();
            // Overflow prefers a real move. Native homes are terminal fallback,
            // rather than another random edge when a compatible free port exists.
            var secondary = Match(secondaryBoats, secondaryPorts, secondaryEdges, random, false);
            var moved = 0;
            for (var i = 0; i < remaining.Count; ++i)
            {
                var b = remaining[i];
                primary.Assigned[b] = secondary.Assigned[i];
                if (secondary.Assigned[i] >= 0 && secondary.Assigned[i] != boats[b].Home) ++moved;
            }
            return moved;
        }

        // Place bookkeeping for the matching.
        private sealed class Places
        {
            private readonly IList<FleetBoat> boats;
            private readonly IList<FleetPort> ports;
            internal readonly List<int>[] Occupants;
            private readonly int[] foreign;
            private readonly int[] large;
            internal bool Spread;
            // Reservations for actual unmatched native residents.
            internal readonly int[] Kept;
            internal readonly int[] KeptLarge;

            internal Places(IList<FleetBoat> boats, IList<FleetPort> ports)
            {
                this.boats = boats;
                this.ports = ports;
                Occupants = Enumerable.Range(0, ports.Count).Select(p => new List<int>()).ToArray();
                foreign = new int[ports.Count];
                large = new int[ports.Count];
                Kept = new int[ports.Count];
                KeptLarge = new int[ports.Count];
            }

            internal int TotalLimit(int p) => Spread ? Math.Min(1, ports[p].Capacity - ports[p].Fixed) : ports[p].Capacity - ports[p].Fixed;
            internal bool Contains(int boat) => Occupants.Any(list => list.Contains(boat));
            internal void Move(int boat, int source, int target) { Remove(boat, source); Add(boat, target); }
            internal int ForeignLimit(int p) => ports[p].Capacity - ports[p].Fixed - Kept[p];

            internal void Clear()
            {
                foreach (var list in Occupants) list.Clear();
                Array.Clear(foreign, 0, foreign.Length);
                Array.Clear(large, 0, large.Length);
            }

            private bool Foreign(int boat, int port) => boats[boat].Home != port;

            // Kuhn augmenting path. A full port re-routes one of its boats;
            // when the arriving boat needs a place reserved for boats from
            // elsewhere, only such a boat is re-routed.
            internal bool Augment(int boat, List<int>[] adjacency, bool[] visited,
                Func<int, bool> canDrop = null,
                Func<int, IEnumerable<int>> alternatives = null, HashSet<int> path = null)
            {
                if (path == null) path = new HashSet<int>();
                if (!path.Add(boat)) return false;
                try
                {
                    foreach (var port in adjacency[boat])
                    {
                        var visit = port + (ports[port].SharedMix && boats[boat].Size == BoatSize.Large ? ports.Count : 0);
                        if (visited[visit]) continue;
                        visited[visit] = true;
                        var isForeign = Foreign(boat, port);
                        var foreignFull = isForeign && foreign[port] >= ForeignLimit(port);
                        var mixFull = ports[port].SharedMix && boats[boat].Size == BoatSize.Large &&
                            (large[port] > 0 || isForeign && ports[port].FixedLarge + KeptLarge[port] > 0);
                        // Native-only overload is existing authored state. Once
                        // anybody arrives, all primary and fallback residents fit.
                        var occupiedLimit = !isForeign && foreign[port] == 0 ? TotalLimit(port) : Math.Min(TotalLimit(port), ForeignLimit(port));
                        if (Occupants[port].Count < occupiedLimit && !foreignFull && !mixFull)
                        {
                            Add(boat, port);
                            return true;
                        }
                        foreach (var occupant in Occupants[port].ToArray())
                        {
                            if (foreignFull && !Foreign(occupant, port)) continue;
                            if (mixFull && (ports[port].FixedLarge + KeptLarge[port] > 0 && isForeign || boats[occupant].Size != BoatSize.Large)) continue;
                            if (!Augment(occupant, adjacency, visited, canDrop, alternatives, path))
                            {
                                // Replace a blocked selected peer with an unmatched
                                // peer of the same category along the alternating
                                // path. Category count stays unchanged at this step.
                                var replaced = alternatives != null && alternatives(occupant).Any(peer =>
                                    Augment(peer, adjacency, visited, canDrop, alternatives, path));
                                if (!replaced)
                                {
                                    if (canDrop == null || !canDrop(occupant)) continue;
                                }
                            }
                            Remove(occupant, port);
                            Add(boat, port);
                            return true;
                        }
                    }
                    return false;
                }
                finally { path?.Remove(boat); }
            }

            internal void Add(int boat, int port)
            {
                Occupants[port].Add(boat);
                if (Foreign(boat, port)) ++foreign[port];
                if (boats[boat].Size == BoatSize.Large) ++large[port];
            }

            private void Remove(int boat, int port)
            {
                Occupants[port].Remove(boat);
                if (Foreign(boat, port)) --foreign[port];
                if (boats[boat].Size == BoatSize.Large) --large[port];
            }
        }

        internal static void Shuffle<T>(IList<T> list, Random random)
        {
            for (var i = list.Count - 1; i > 0; --i)
            {
                var j = random.Next(i + 1);
                var swap = list[i];
                list[i] = list[j];
                list[j] = swap;
            }
        }

        // Fresh physical placement order: randomized within each category,
        // with larger hulls processed first. The catalogue never breaks ties.
        internal static List<int> RandomizedSizeOrder(IList<FleetBoat> boats, Random random)
        {
            var order = Enumerable.Range(0, boats.Count).Where(b => boats[b].Eligible).ToList();
            Shuffle(order, random);
            return order.OrderByDescending(b => boats[b].Size).ToList();
        }

        // A boat that does not move stays at its home port with its authored
        // claims and uses one of that port's places. Drop every move into a
        // port without a free place or onto such a claim, and every later
        // duplicate cleat, until nothing changes. stayingHomes lists the home
        // of every staying boat that has no move here (one entry per boat;
        // dropped moves count their own boat). capacity gives each target's
        // places (null: 1 everywhere). Returns the moves dropped by this call
        // (in order).
        internal static List<FleetMove> Cascade(IList<FleetMove> moves, IEnumerable<string> stayingHomes,
            IEnumerable<string> stayingClaims, Func<string, int> capacity = null,
            IEnumerable<string> stayingLargeHomes = null, Func<string, int> largeCapacity = null)
        {
            var largeHomes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var home in stayingLargeHomes ?? new string[0]) Count(largeHomes, home);
            var homes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var home in stayingHomes ?? new string[0]) Count(homes, home);
            var claims = new HashSet<string>(stayingClaims ?? new string[0], StringComparer.Ordinal);
            var dropped = new List<FleetMove>();
            foreach (var move in moves.Where(item => item.Dropped)) Stay(move, homes, claims, largeHomes);
            var changed = true;
            while (changed)
            {
                changed = false;
                var targets = new Dictionary<string, int>(StringComparer.Ordinal);
                var largeTargets = new Dictionary<string, int>(StringComparer.Ordinal);
                var cleats = new HashSet<string>(StringComparer.Ordinal);
                foreach (var move in moves)
                {
                    if (move.Dropped) continue;
                    string reason = null;
                    var places = move.Target == null ? 1 : Math.Max(1, capacity?.Invoke(move.Target) ?? 1);
                    var staying = move.Target != null && homes.TryGetValue(move.Target, out var count) ? count : 0;
                    var earlier = move.Target != null && targets.TryGetValue(move.Target, out var taken) ? taken : 0;
                    var largeStaying = move.Target != null && largeHomes.TryGetValue(move.Target, out var largeCount) ? largeCount : 0;
                    var largeEarlier = move.Target != null && largeTargets.TryGetValue(move.Target, out var largeTaken) ? largeTaken : 0;
                    if (move.Target == null) reason = "no target port";
                    else if (staying >= places)
                        reason = places == 1 ? "target port " + move.Target + " keeps its native boat"
                            : "target port " + move.Target + " is full with " + staying + " boat(s) that stay (capacity " + places + ")";
                    else if (move.TargetCleats.FirstOrDefault(claims.Contains) is string claimed)
                        reason = "target cleat " + claimed + " is still claimed by a boat that stays";
                    else if (staying + earlier >= places)
                        reason = places == 1 ? "another move already targets port " + move.Target
                            : "port " + move.Target + " is full (" + staying + " staying, " + earlier + " moving in, capacity " + places + ")";
                    else if (largeCapacity != null && move.Size == BoatSize.Large && move.Home != move.Target &&
                        largeStaying + largeEarlier >= Math.Max(0, largeCapacity(move.Target)))
                        reason = "port " + move.Target + " keeps its Large allowance (" + largeStaying + " staying, " + largeEarlier + " moving in)";
                    else if (move.TargetCleats.FirstOrDefault(cleat => !cleats.Add(cleat)) is string shared)
                        reason = "another move already targets cleat " + shared;
                    if (reason == null)
                    {
                        targets[move.Target] = earlier + 1;
                        if (move.Size == BoatSize.Large) largeTargets[move.Target] = largeEarlier + 1;
                        continue;
                    }
                    move.Dropped = true;
                    move.Reason = reason;
                    dropped.Add(move);
                    Stay(move, homes, claims, largeHomes);
                    changed = true;
                    break;
                }
            }
            return dropped;
        }

        private static void Count(Dictionary<string, int> homes, string home)
        {
            if (home != null) homes[home] = homes.TryGetValue(home, out var count) ? count + 1 : 1;
        }

        private static void Stay(FleetMove move, Dictionary<string, int> homes, HashSet<string> claims,
            Dictionary<string, int> largeHomes)
        {
            Count(homes, move.Home);
            if (move.Size == BoatSize.Large) Count(largeHomes, move.Home);
            foreach (var claim in move.Claims) claims.Add(claim);
        }
    }

    // Rollback mirrors placement: boats trade cleats, so a boat going home
    // may find its old cleat held by another boat going home (or by a rope a
    // failed placement already tied). Release every affected boat first,
    // then restore every pose, then re-moor the original lines. A boat whose
    // step throws drops out of the later steps; failed reports it.
    // Returns the boats fully restored (every original line re-moored).
    internal static class FleetRollback
    {
        internal static List<T> Run<T>(IList<T> boats, Action<T> release, Action<T> restorePose, Func<T, bool> remoor,
            Action<T, Exception> failed)
        {
            var live = Step(boats, release, failed);
            live = Step(live, restorePose, failed);
            var restored = new List<T>();
            foreach (var boat in live)
            {
                try
                {
                    if (remoor(boat)) restored.Add(boat);
                }
                catch (Exception exception)
                {
                    failed(boat, exception);
                }
            }
            return restored;
        }

        private static List<T> Step<T>(IEnumerable<T> boats, Action<T> action, Action<T, Exception> failed)
        {
            var result = new List<T>();
            foreach (var boat in boats)
            {
                try
                {
                    action(boat);
                    result.Add(boat);
                }
                catch (Exception exception)
                {
                    failed(boat, exception);
                }
            }
            return result;
        }
    }
}
