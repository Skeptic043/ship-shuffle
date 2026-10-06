using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace ShipShuffle
{
    internal sealed class DockSite
    {
        internal PortEntry Port;
        // Every initialized island cleat; identities match by index.
        internal GPButtonDockMooring[] IslandCleats;
        internal string[] IslandIds;
        internal int R1;
        internal int R2;
        // Recovery chain order, R1 -> R2 traversal.
        internal GPButtonDockMooring[] Cleats;
        internal string[] CleatIds;
        internal DockChain Chain;
        internal Vec2 BoatPos;
        internal Vec2 BoatForward;
        internal Vec2 AlternateOffset;
        internal int IslandCleatCount => IslandCleats.Length;
        // Native recovery availability is independent of an authored pose reference.
        internal bool NoRecovery;
        internal bool MarkerReference;
        internal bool MarkerGoLeft;
        // Marker-frame fallback: virtual recovery points in the marker's frame
        // (IslandCleats holds null at those indexes).
        internal Vector3?[] VirtualLocal;
        internal Transform Marker;
        // Every Terrain under the island (active or not); empty when it has none.
        internal Terrain[] Terrains = new Terrain[0];

        internal Vec2 Position(int index) => IslandCleats[index] != null
            ? HullFootprint.Flat(IslandCleats[index].transform.position)
            : HullFootprint.Flat(Marker.TransformPoint(VirtualLocal[index].Value));

        internal int IndexOf(string identity) => Array.IndexOf(IslandIds, identity);
        internal int IndexOf(Transform cleat) => Array.FindIndex(IslandCleats, item => item != null && item.transform == cleat);
        internal Transform Island => Port.Island.transform;
    }

    internal sealed class BerthPlan
    {
        internal DockSite Site;
        internal BoatEntry Boat;
        internal Candidate Candidate;
        internal int Tier => Candidate.Tier;
        // Tier 4: held at a station-kept spot.
        internal bool Held => Candidate.Tier == 4;
        // Moored tiers 1-3 only.
        internal GPButtonDockMooring Front;
        internal GPButtonDockMooring Back;
        internal string FrontId;
        internal string BackId;
        // A saved placement put back on load: its cleats were chosen from
        // their physical state when the plan was made (authored claims do not
        // count).
        internal bool Restored;
        internal Vector3 Position;
        internal Quaternion Rotation;
        // The root pose in the island's frame (stored in the save).
        internal Vector3 LocalPosition;
        internal float LocalYaw;

        internal string Describe() => "tier " + Tier + (Held ? " station-kept spot " + DockGeometry.F(Candidate.Outward) +
            " m outward" : " " + FrontId + " / " + BackId + (Candidate.Side != 0 ? " side " + Candidate.Side : "") +
            (Candidate.SlotOwner != null ? " (vacated berth of " + Candidate.SlotOwner + ")" : "") +
            (Candidate.TiedOff > 0f ? ", tied off the dock (+" + DockGeometry.F(Candidate.TiedOff) + " m)" : "")) +
            (Candidate.Estimated ? ", estimated" : "");
    }

    // A berth already given to another boat at the same port.
    internal sealed class PlannedBerth
    {
        internal BoatEntry Boat;
        internal Candidate Candidate;
    }

    // Unity side of the tiered berth search: builds the plain-number port
    // input from live objects; every placement decision is made by
    // DockGeometry and BerthTiers.
    internal static class BerthResolver
    {
        private static readonly FieldInfo IslandHeightField = AccessTools.Field(typeof(IslandHorizon), "initialHeight");

        internal static bool TryBuildSite(PortEntry port, out DockSite site, out string reason)
        {
            if (!TryBuildDocks(port, out site, out reason)) return false;
            site.Terrains = port.Island.GetComponentsInChildren<Terrain>(true).Where(item => item != null && item.terrainData != null).ToArray();
            if (site.Terrains.Length == 0)
                Plugin.Instance?.DebugLog(port.Label + ": no terrain to check; berths here are not vetted against the sea floor.");
            else
                Plugin.Instance?.DebugLog(port.Label + ": " + site.Terrains.Length + " terrain(s) vet berths against the sea floor.");
            return true;
        }

        private static bool TryBuildDocks(PortEntry port, out DockSite site, out string reason)
        {
            site = null;
            reason = null;
            var island = port.Island;
            var recovery = port.PlacementReference ?? port.Recovery;
            if (island == null)
            {
                reason = "Port island is missing.";
                return false;
            }
            var cleats = island.GetComponentsInChildren<GPButtonDockMooring>(true)
                .Where(item => item != null && item.spring != null && item.GetComponent<SpringJoint>() == item.spring &&
                    PortCatalog.FindIsland(item.transform) == island)
                .Distinct().ToArray();
            var ids = cleats.Select(item => CleatIdentity.Build(island.transform, item.transform)).ToList();
            if (ids.Any(item => item == null) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            {
                reason = "Dock cleat identities on " + island.name + " are missing or not unique.";
                return false;
            }
            if (recovery == null) return TryBuildNoRecoverySite(port, cleats, ids.ToArray(), out site, out reason);
            if (recovery.parentPort != port.Port || recovery.boatPos == null ||
                !port.NoRecovery && (recovery.mooringFront == null || recovery.mooringBack == null))
            {
                reason = "Recovery berth is incomplete.";
                return false;
            }
            var all = cleats.ToList();
            Vector3?[] virtualLocal = null;
            // Recovery cleats may live outside the island (recovery-prefab cleats
            // reparented with the RecoveryPort). Accept any initialized, loaded
            // cleat; it is only ever the recovery pair, under a placeholder name.
            var r1 = -1;
            var r2 = -1;
            if (port.NoRecovery)
            {
                if (!Synthesize(port, all, ids, "(dormant authored pose; no native recovery berth)",
                    out r1, out r2, out virtualLocal, out reason)) return false;
            }
            else
            {
                r1 = RecoveryIndex(recovery.mooringFront, all, ids, CleatIdentity.RecoveryFront, out reason);
                r2 = r1 < 0 ? -1 : RecoveryIndex(recovery.mooringBack, all, ids, CleatIdentity.RecoveryBack, out reason);
            }
            if (!port.NoRecovery && (r1 < 0 || r2 < 0 || r1 == r2))
            {
                var unresolved = "Recovery cleats " + recovery.mooringFront.name + " / " + recovery.mooringBack.name + " of " + island.name +
                    " are unusable: " + (reason ?? "both are the same cleat");
                ids = ids.Take(cleats.Length).ToList();
                all = cleats.ToList();
                if (!Synthesize(port, all, ids, "(unresolved: " + (reason ?? "same cleat") + ")", out r1, out r2, out virtualLocal, out reason))
                {
                    reason = unresolved + "; " + reason;
                    return false;
                }
            }
            else if (!port.NoRecovery)
            {
                var markerFlat = HullFootprint.Flat(recovery.boatPos.position);
                var vanillaRange = Math.Max(Vec2.Distance(HullFootprint.Flat(all[r1].transform.position), markerFlat),
                    Vec2.Distance(HullFootprint.Flat(all[r2].transform.position), markerFlat));
                if (!(vanillaRange <= DockGeometry.MaxRecoveryCleatRange))
                {
                    ids = ids.Take(cleats.Length).ToList();
                    all = cleats.ToList();
                    if (!Synthesize(port, all, ids, "(" + DockGeometry.F(vanillaRange) + " m)", out r1, out r2, out virtualLocal, out reason))
                        return false;
                }
            }
            cleats = all.ToArray();
            var marker = recovery.boatPos;
            var forward = HullFootprint.Flat(marker.forward);
            var side = HullFootprint.Flat(marker.TransformDirection(recovery.goLeft ? Vector3.left : Vector3.right));
            if (!HullFootprint.Finite(marker.position) || !forward.IsFinite || forward.Length < 0.5f ||
                !side.IsFinite || side.Length < 0.5f)
            {
                reason = "Recovery marker orientation is not usable in the horizontal plane.";
                return false;
            }
            var boatPos = HullFootprint.Flat(marker.position);
            var built = new DockSite
            {
                Port = port, IslandCleats = cleats, IslandIds = ids.ToArray(), R1 = r1, R2 = r2, Marker = marker,
                VirtualLocal = virtualLocal ?? new Vector3?[cleats.Length],
                NoRecovery = port.NoRecovery, MarkerReference = port.NoRecovery, MarkerGoLeft = recovery.goLeft,
                BoatPos = boatPos, BoatForward = forward * (1f / forward.Length),
                AlternateOffset = side * (DockGeometry.ReserveSideOffset / side.Length)
            };
            // Cleat positions include any virtual points, so the chain is
            // built from the site itself.
            var chain = DockGeometry.BuildChain(Enumerable.Range(0, cleats.Length).Select(built.Position).ToArray(), r1, r2, boatPos);
            if (!chain.Ok)
            {
                reason = chain.Reason;
                return false;
            }
            built.Chain = chain;
            built.Cleats = chain.Order.Select(index => cleats[index]).ToArray();
            built.CleatIds = chain.Order.Select(index => ids[index]).ToArray();
            site = built;
            return true;
        }

        // Vanilla Jungle, Swamp and Flower recovery berths reference Cave's
        // cleats (verified in level24). Fall back to the island's own pair, and
        // when there is none, to two virtual points on the marker's dock side.
        private static bool Synthesize(PortEntry port, List<GPButtonDockMooring> cleats, List<string> ids, string vanilla,
            out int r1, out int r2, out Vector3?[] virtualLocal, out string reason)
        {
            virtualLocal = null;
            var reference = port.PlacementReference ?? port.Recovery;
            var marker = reference.boatPos;
            var boatPos = HullFootprint.Flat(marker.position);
            var forward = HullFootprint.Flat(marker.forward);
            var points = cleats.Select(item => HullFootprint.Flat(item.transform.position)).ToArray();
            if (BerthTiers.TrySyntheticRecovery(points, points.Length, boatPos, forward, out r1, out r2, out var pairReason))
            {
                Plugin.Instance?.DebugLog(port.Label + ": vanilla recovery cleats point elsewhere " + vanilla + "; using island cleats " +
                    cleats[r1].name + "/" + cleats[r2].name + " as the recovery reference.");
                if (!BerthTiers.AgreesWithMarker(points[r1], points[r2], boatPos, forward, reference.goLeft))
                    Plugin.Instance?.DebugLog(port.Label + ": the island pair puts the dock on the opposite side from the recovery marker frame " +
                        "(goLeft " + reference.goLeft + "); keeping the island pair.");
                reason = null;
                return true;
            }
            if (!(forward.Length >= 0.5f) || !boatPos.IsFinite)
            {
                reason = "vanilla recovery cleats point elsewhere " + vanilla + ", no island pair (" + pairReason + ") and no usable marker";
                return false;
            }
            BerthTiers.MarkerReference(boatPos, forward, reference.goLeft, out var front, out var back);
            var y = marker.position.y;
            virtualLocal = new Vector3?[cleats.Count + 2];
            virtualLocal[cleats.Count] = marker.InverseTransformPoint(new Vector3(front.X, y, front.Z));
            virtualLocal[cleats.Count + 1] = marker.InverseTransformPoint(new Vector3(back.X, y, back.Z));
            r1 = cleats.Count;
            r2 = cleats.Count + 1;
            cleats.Add(null);
            cleats.Add(null);
            ids.Add(CleatIdentity.MarkerFront);
            ids.Add(CleatIdentity.MarkerBack);
            Plugin.Instance?.DebugLog(port.Label + ": vanilla recovery cleats point elsewhere " + vanilla + " and no island pair fits (" +
                pairReason + "); using the authored marker frame (dock on its " + (reference.goLeft ? "right" : "left") + ") as the placement reference.");
            reason = null;
            return true;
        }

        // Onna has no RecoveryPort: no recovery line, reserves or marker rule.
        private static bool TryBuildNoRecoverySite(PortEntry port, GPButtonDockMooring[] cleats, string[] ids, out DockSite site, out string reason)
        {
            site = new DockSite
            {
                Port = port, IslandCleats = cleats, IslandIds = ids, R1 = -1, R2 = -1, NoRecovery = true,
                Cleats = new GPButtonDockMooring[0], CleatIds = new string[0], VirtualLocal = new Vector3?[cleats.Length]
            };
            var centre = HullFootprint.Flat(port.Island.GetPosition());
            var probe = new PortInput
            {
                Cleats = cleats.Select(item => HullFootprint.Flat(item.transform.position)).ToArray(), Names = ids, NoRecovery = true,
                IslandCentre = centre, HasIslandCentre = centre.IsFinite
            };
            var frame = BerthTiers.Frame(probe);
            if (!frame.Ok)
            {
                site = null;
                reason = frame.Reason;
                return false;
            }
            site.BoatPos = frame.Base.BoatPos;
            site.BoatForward = frame.Base.BoatForward;
            reason = null;
            Plugin.Instance?.DebugLog(port.Label + ": no recovery berth; reserves not applied (reference pier " +
                string.Join(">", frame.Reference.Order.Select(index => cleats[index].name).ToArray()) + ").");
            return true;
        }

        private static int RecoveryIndex(Transform mooring, List<GPButtonDockMooring> cleats, List<string> ids, string placeholder,
            out string reason)
        {
            reason = null;
            var cleat = mooring != null ? mooring.GetComponent<GPButtonDockMooring>() : null;
            var index = cleat != null ? cleats.IndexOf(cleat) : -1;
            if (index >= 0) return index;
            if (cleat == null || cleat.spring == null || cleat.GetComponent<SpringJoint>() != cleat.spring ||
                !cleat.gameObject.scene.IsValid() || !cleat.gameObject.scene.isLoaded)
            {
                reason = (mooring != null ? mooring.name : "missing cleat") + " is not an initialized dock cleat in the loaded scene";
                return -1;
            }
            cleats.Add(cleat);
            ids.Add(placeholder);
            return cleats.Count - 1;
        }

        // Authored claims of every active boat except one (diagnostics).
        internal static Dictionary<Transform, string> AuthoredClaims(BoatMooringRopes except)
        {
            var claims = new Dictionary<Transform, string>();
            foreach (var ropes in Resources.FindObjectsOfTypeAll<BoatMooringRopes>())
            {
                if (ropes == null || ropes == except || !ropes.gameObject.scene.IsValid() ||
                    !ropes.gameObject.scene.isLoaded || !ropes.gameObject.activeInHierarchy) continue;
                foreach (var mooring in new[] { ropes.mooringFront, ropes.mooringBack })
                    if (mooring != null && !claims.ContainsKey(mooring)) claims.Add(mooring, ropes.gameObject.name);
            }
            return claims;
        }

        // Plain-number view of one port for one boat. saleBoats supplies the
        // vacated authored berths (tier 2): boats that move, or the boat itself.
        // planned: berths already given to other boats at this port (shared
        // shipyards); their hulls and cleats are taken.
        internal static PortInput MakePortInput(DockSite site, BoatEntry boat, Occupancy occupancy, IEnumerable<BoatEntry> saleBoats,
            IEnumerable<PlannedBerth> planned = null)
        {
            var count = site.IslandCleats.Length;
            var input = new PortInput
            {
                Cleats = Enumerable.Range(0, count).Select(site.Position).ToArray(),
                Names = site.IslandIds, Free = new bool[count], Unavailable = new string[count], Claimed = new bool[count],
                R1 = site.R1, R2 = site.R2, NoRecovery = site.NoRecovery, MarkerReference = site.MarkerReference,
                NoDock = site.IslandCleats.All(item => item == null),
                OffshoreOnly = KnownIdentities.OffshoreOnly(site.Port.Identity),
                OasisNativeSaleHeading = KnownIdentities.NativeSaleHeading(site.Port.Identity),
                StationPolicy = KnownIdentities.StationPolicyFor(site.Port.Identity),
                StationRegion = KnownIdentities.StationRegionFor(site.Port.Identity),
                BoatPos = site.BoatPos, BoatForward = site.BoatForward,
                AlternateOffset = site.AlternateOffset, Obstacles = occupancy.ObstaclesFor(boat.Body)
            };
            if (site.Marker != null)
            {
                // Live marker pose: the recovery marker moves with the shifting world.
                input.BoatPos = HullFootprint.Flat(site.Marker.position);
                var forward = HullFootprint.Flat(site.Marker.forward);
                input.BoatForward = forward * (1f / forward.Length);
                var side = HullFootprint.Flat(site.Marker.TransformDirection(site.MarkerGoLeft ? Vector3.left : Vector3.right));
                input.AlternateOffset = side * (DockGeometry.ReserveSideOffset / side.Length);
            }
            var centre = HullFootprint.Flat(site.Port.Island.GetPosition());
            input.IslandCentre = centre;
            input.HasIslandCentre = centre.IsFinite;
            for (var i = 0; i < count; ++i)
            {
                var cleat = site.IslandCleats[i];
                if (cleat == null && site.VirtualLocal[i].HasValue) input.Unavailable[i] = "virtual recovery reference";
                else input.Unavailable[i] = occupancy.CleatProblem(cleat, boat.Body, out input.Claimed[i]);
                input.Free[i] = input.Unavailable[i] == null;
            }
            var slots = new List<BerthSlot>();
            foreach (var other in saleBoats ?? new BoatEntry[0])
            {
                if (other == null || other.Purchased || other.Ropes == null || other.Body == null ||
                    other.Body != boat.Body && !occupancy.Moving.Contains(other.Body)) continue;
                var front = site.IndexOf(other.Ropes.mooringFront);
                var back = site.IndexOf(other.Ropes.mooringBack);
                if (front < 0 || back < 0 || front == back) continue;
                try
                {
                    var hull = HullFootprint.MakeFootprint(other.Body.transform.position, other.Body.transform.rotation,
                        other.Body.transform.lossyScale, other.Hull.center, other.Hull.radius, other.Hull.height, other.Hull.direction);
                    slots.Add(new BerthSlot
                    {
                        Owner = other.Label, Center = hull.Center, Forward = HullFootprint.Flat(other.Body.transform.forward),
                        Length = Vec2.Distance(hull.A, hull.B) + 2f * hull.Radius, Radius = hull.Radius, FrontCleat = front, BackCleat = back
                    });
                }
                catch (InvalidOperationException) { }
            }
            input.Slots = slots;
            input.Ground = MakeGround(site);
            foreach (var other in planned ?? new PlannedBerth[0])
                if (other != null && other.Boat != boat) BerthTiers.AddPlanned(input, other.Boat.Label, other.Candidate);
            return input;
        }

        private sealed class TerrainPatch
        {
            internal TerrainData Data;
            internal float X;
            internal float Z;
            internal float SizeX;
            internal float SizeZ;
            // Terrain origin height with the island's horizon drop undone.
            internal float Y;
        }

        // Highest terrain height relative to sea level at a world X/Z over every
        // Terrain of the island. Terrains ignore rotation and scale; heights are
        // TerrainData.GetInterpolatedHeight at normalized coordinates plus the
        // terrain origin, less IslandHorizon's drop (NativeBerth.TryHorizonOffset).
        // Sea level is world Y 0: the Crest ocean root sits at Y 0 and
        // FloatingOriginManager shifts only X and Z.
        internal static Func<Vec2, float?> MakeGround(DockSite site)
        {
            if (site.Terrains == null || site.Terrains.Length == 0 || !TryHorizonOffset(site.Port.Island, out var drop)) return null;
            var patches = new List<TerrainPatch>();
            foreach (var terrain in site.Terrains)
            {
                var data = terrain != null ? terrain.terrainData : null;
                if (data == null) continue;
                var size = data.size;
                var origin = terrain.transform.position;
                if (!(size.x > 0f) || !(size.z > 0f) || !HullFootprint.Finite(origin)) continue;
                patches.Add(new TerrainPatch { Data = data, X = origin.x, Z = origin.z, SizeX = size.x, SizeZ = size.z, Y = origin.y - drop.y });
            }
            if (patches.Count == 0) return null;
            return point =>
            {
                float? highest = null;
                foreach (var patch in patches)
                {
                    var u = (point.X - patch.X) / patch.SizeX;
                    var v = (point.Z - patch.Z) / patch.SizeZ;
                    if (!(u >= 0f && u <= 1f && v >= 0f && v <= 1f)) continue;
                    var height = patch.Y + patch.Data.GetInterpolatedHeight(u, v);
                    if (!highest.HasValue || height > highest.Value) highest = height;
                }
                return highest;
            };
        }

        internal static TierEvaluation Evaluate(DockSite site, BoatEntry boat, Occupancy occupancy, IEnumerable<BoatEntry> saleBoats,
            FleetSettings settings, IEnumerable<PlannedBerth> planned = null)
        {
            var input = MakePortInput(site, boat, occupancy, saleBoats, planned);
            var evaluation = BerthTiers.Evaluate(input, boat.Spec);
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.DebugEnabled) return evaluation;
            // One line per boat/port pair; the most common rejection explains a miss.
            var common = evaluation.Candidates.Where(item => !item.Accepted && item.Reason != null)
                .GroupBy(item => Regex.Replace(item.Reason, @"[-\d.]+", "#")).OrderByDescending(group => group.Count())
                .FirstOrDefault();
            plugin.DebugLog("  " + boat.Label + " at " + site.Port.Label + ": " +
                evaluation.Candidates.Count(item => item.Accepted) + " of " + evaluation.Candidates.Count + " candidates valid" +
                (evaluation.Best != null ? "; best " + Describe(input, evaluation.Best)
                    : common != null ? "; most common rejection (" + common.Count() + "x): " + common.First().Reason
                    : evaluation.Reason != null ? "; " + evaluation.Reason : ""));
            return evaluation;
        }

        internal static BerthPlan MakePlan(DockSite site, BoatEntry boat, Candidate candidate)
        {
            var forward = new Vector3(candidate.Forward.X, 0f, candidate.Forward.Z);
            var plan = new BerthPlan
            {
                Site = site, Boat = boat, Candidate = candidate,
                Position = new Vector3(candidate.Root.X, SpawnHeight.For(boat), candidate.Root.Z),
                Rotation = Quaternion.LookRotation(forward, Vector3.up)
            };
            if (candidate.Tier != 4)
            {
                if (candidate.FrontCleat == site.R1 || candidate.FrontCleat == site.R2 || candidate.BackCleat == site.R1 ||
                    candidate.BackCleat == site.R2 || CleatIdentity.IsPlaceholder(site.IslandIds[candidate.FrontCleat]) ||
                    CleatIdentity.IsPlaceholder(site.IslandIds[candidate.BackCleat]))
                    throw new InvalidOperationException("A berth may never use the recovery cleats.");
                plan.Front = site.IslandCleats[candidate.FrontCleat];
                plan.Back = site.IslandCleats[candidate.BackCleat];
                plan.FrontId = site.IslandIds[candidate.FrontCleat];
                plan.BackId = site.IslandIds[candidate.BackCleat];
            }
            plan.LocalPosition = site.Island.InverseTransformPoint(plan.Position);
            plan.LocalYaw = Mathf.DeltaAngle(site.Island.eulerAngles.y, plan.Rotation.eulerAngles.y);
            return plan;
        }

        // ------------------------------------------------------------ restore

        // The port's island alone, for restoring a saved placement where the
        // dock site could not be built; null when the island is gone.
        internal static DockSite IslandSite(PortEntry port) =>
            port?.Island == null ? null : new DockSite
            {
                Port = port, IslandCleats = new GPButtonDockMooring[0], IslandIds = new string[0], R1 = -1, R2 = -1,
                Cleats = new GPButtonDockMooring[0], CleatIds = new string[0], VirtualLocal = new Vector3?[0]
            };

        // A saved placement's cleat on load: found by its identity on the
        // island, with an initialized spring that no staying boat holds (a
        // moving boat's spring is released before placement). Returns why the
        // boat cannot tie there, or null; cleat is set whenever it resolved.
        internal static string StoredCleat(DockSite site, string identity, Occupancy occupancy, Rigidbody boat,
            out GPButtonDockMooring cleat, out Vec2 position)
        {
            cleat = null;
            position = default;
            if (!CleatIdentity.TryResolve(site.Island, identity, out var transform, out var reason))
                return "stored cleat " + identity + " did not resolve: " + reason;
            cleat = transform.GetComponent<GPButtonDockMooring>();
            if (cleat == null || cleat.spring == null || cleat.GetComponent<SpringJoint>() != cleat.spring)
                return "stored cleat " + identity + " has no initialized spring";
            position = HullFootprint.Flat(cleat.transform.position);
            var holder = cleat.spring.connectedBody;
            return holder != null && holder != boat && !occupancy.Moving.Contains(holder)
                ? "stored cleat " + identity + " is held by " + holder.name : null;
        }

        // The plan for a saved placement at its exact stored pose (island-local
        // root position and yaw); front/back are null for a held spot.
        internal static BerthPlan MakeStoredPlan(DockSite site, BoatEntry boat, Candidate candidate, GPButtonDockMooring front,
            GPButtonDockMooring back, string frontId, string backId, Vector3 localPosition, float localYaw)
        {
            StationPose(site, localPosition, localYaw, out var position, out var rotation);
            var plan = new BerthPlan
            {
                Site = site, Boat = boat, Candidate = candidate, Restored = true,
                Position = new Vector3(position.x, SpawnHeight.For(boat), position.z), Rotation = rotation, LocalYaw = localYaw
            };
            if (candidate.Tier != 4)
            {
                plan.Front = front;
                plan.Back = back;
                plan.FrontId = frontId;
                plan.BackId = backId;
            }
            plan.LocalPosition = site.Island.InverseTransformPoint(plan.Position);
            return plan;
        }

        // Debug note: staying boats whose hulls a restored placement overlaps.
        internal static string Overlaps(Occupancy occupancy, BoatEntry boat, Candidate candidate)
        {
            var names = occupancy.ObstaclesFor(boat.Body).Where(obstacle => obstacle != null && obstacle.Measurable && obstacle.Hull.IsValid &&
                DockGeometry.CapsuleGap(candidate.Hull, obstacle.Hull) < 0f).Select(obstacle => obstacle.Name).ToArray();
            return names.Length == 0 ? null : string.Join(", ", names);
        }

        // World root pose of a station spot stored in the island's frame.
        internal static void StationPose(DockSite site, Vector3 localPosition, float localYaw, out Vector3 position, out Quaternion rotation)
        {
            position = site.Island.TransformPoint(localPosition);
            rotation = Quaternion.Euler(0f, site.Island.eulerAngles.y + localYaw, 0f);
        }

        // IslandHorizon lowers distant islands by changing local Y; this is the
        // world-space drop to apply to sea-level probes (New Beginnings' NativeBerth).
        internal static bool TryHorizonOffset(IslandHorizon island, out Vector3 offset)
        {
            offset = Vector3.zero;
            if (island == null || IslandHeightField == null || !(IslandHeightField.GetValue(island) is float initialHeight) ||
                !DockGeometry.Finite(initialHeight)) return false;
            var localDelta = new Vector3(0f, island.transform.localPosition.y - initialHeight, 0f);
            offset = island.transform.parent != null ? island.transform.parent.TransformVector(localDelta) : localDelta;
            return HullFootprint.Finite(offset);
        }

        internal static string Describe(PortInput port, Candidate candidate)
        {
            var text = "T" + candidate.Tier + (candidate.Estimated ? " (estimated)" : "") + " ";
            if (candidate.Tier == 4)
                text += "station spot " + DockGeometry.F(candidate.Outward) + " m out, shift " + DockGeometry.F(candidate.Shift) + " m";
            else
                text += (candidate.FrontCleat >= 0 ? port.Name(candidate.FrontCleat) : "?") + " / " +
                    (candidate.BackCleat >= 0 ? port.Name(candidate.BackCleat) : "?") +
                    (candidate.Side != 0 ? " side " + candidate.Side : "") +
                    (candidate.SlotOwner != null ? " vacated by " + candidate.SlotOwner : "") +
                    ": span " + DockGeometry.F(candidate.Span) + " m";
            if (!float.IsNaN(candidate.DockClearance)) text += ", dock clearance " + DockGeometry.F(candidate.DockClearance) + " m";
            if (candidate.Blocker != null) text += ", nearest gap " + DockGeometry.F(candidate.MinimumGap) + " m (" + candidate.Blocker + ")";
            if (candidate.TiedOff > 0f) text += ", tied off the dock (+" + DockGeometry.F(candidate.TiedOff) + " m)";
            if (candidate.Accepted)
                text += (candidate.Tier != 4 ? ", lines " + DockGeometry.F(candidate.FrontLine) + " / " + DockGeometry.F(candidate.BackLine) + " m" : "") +
                    ", distance from recovery " + DockGeometry.F(candidate.Distance) + " m, root " + candidate.Root;
            return text + " -> " + (candidate.Accepted ? "ACCEPT" : "reject: " + candidate.Reason);
        }
    }
}
