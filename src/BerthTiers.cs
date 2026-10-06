using System;
using System.Collections.Generic;
using System.Linq;

namespace ShipShuffle
{
    // A vacated authored sale berth: the native pose of boat Owner and its two cleats.
    internal sealed class BerthSlot
    {
        internal string Owner;
        internal Vec2 Center;
        internal Vec2 Forward;
        internal float Length;
        // The owner's hull radius; 0 when unknown.
        internal float Radius;
        internal int FrontCleat;
        internal int BackCleat;
    }

    internal sealed class Pier
    {
        // Island cleat indexes in traversal order.
        internal int[] Order;
        internal Vec2[] Points;
        // +1/-1 water side relative to Perp of the traversal direction; 0 = both sides tried.
        internal int Side;
        internal string SideReason;
        internal float Length => Points.Length < 2 ? 0f : Vec2.Distance(Points[0], Points[Points.Length - 1]);
        // Cleats within 4 m of a pier cleat (paired bollards), skipped from the chain.
        internal int[] Cluster = new int[0];
        // Span along the pier covered by its cleats and their clustered neighbours.
        internal float Extent;
    }

    // Everything one port offers, in plain numbers. Cleat arrays are the whole
    // island (plus, for a marker-frame fallback, two virtual recovery points);
    // Free is per cleat (false when held by a staying boat's spring or claimed
    // by its authored berth), Claimed marks the authored claims.
    internal sealed class PortInput
    {
        internal Vec2[] Cleats = new Vec2[0];
        internal string[] Names;
        internal bool[] Free;
        internal string[] Unavailable;
        internal bool[] Claimed;
        internal int R1 = -1;
        internal int R2 = -1;
        // A port with no RecoveryPort (Onna): no recovery line, reserves or marker rule.
        internal bool NoRecovery;
        // Dormant authored pose: marker geometry without native recovery reserves.
        internal bool MarkerReference;
        internal bool NoDock;
        internal bool OffshoreOnly;
        // Only Oasis's authored sale-boat side uses its departing bow direction.
        // Its opposite recovery side retains the normal two-direction search.
        internal bool OasisNativeSaleHeading;
        internal StationPolicy StationPolicy;
        internal StationRegion? StationRegion;
        internal Vec2 BoatPos;
        internal Vec2 BoatForward;
        internal Vec2 AlternateOffset;
        internal Vec2 IslandCentre;
        internal bool HasIslandCentre;
        internal IList<Obstacle> Obstacles = new Obstacle[0];
        internal IList<BerthSlot> Slots = new BerthSlot[0];
        // Terrain height relative to sea level (see BerthInput.Ground); null without terrain.
        internal Func<Vec2, float?> Ground;

        internal string Name(int cleat) => Names != null && cleat >= 0 && cleat < Names.Length ? Names[cleat] : "cleat " + cleat;
    }

    // The reference a port's berths are measured from: the recovery dock line,
    // or for a port without a recovery berth, its longest pier.
    internal sealed class PortFrame
    {
        internal bool Ok;
        internal string Reason;
        internal bool NoRecovery;
        internal bool RadialSearch;
        internal bool OffshoreOnly;
        internal StationPolicy StationPolicy;
        internal StationRegion? StationRegion;
        internal DockChain Recovery;
        // Clearance input: obstacles, plus marker and reserves unless NoRecovery.
        internal BerthInput Base;
        internal List<Pier> Piers = new List<Pier>();
        internal float Standoff;
        internal Vec2 StationOrigin;
        internal Vec2 StationForward;
        internal Vec2[] StationNormals = new Vec2[0];
        internal float StationStart;
        internal Pier Reference;
        // The island's dock structure, regardless of claims: recovery chain,
        // every pier chain and every cleat as a point. Hulls keep their dock
        // clearance from all of it.
        internal List<Vec2[]> DockSegments = new List<Vec2[]>();
        // The same structure, split for ranking: chains (cleat indexes) and the
        // corner links joining chain ends.
        internal List<int[]> StructureChains = new List<int[]>();
        internal List<Vec2[]> CornerLinks = new List<Vec2[]>();
    }

    internal sealed class TierEvaluation
    {
        internal DockChain Recovery;
        internal PortFrame Frame;
        internal readonly List<Pier> Piers = new List<Pier>();
        internal readonly List<Candidate> Candidates = new List<Candidate>();
        internal Candidate Best;
        internal string Reason;
    }

    // Tiered berth search. T1 recovery dock line and T2 vacated authored
    // berths rank together; T3 other piers only when they give nothing; T4
    // a station-kept spot only when T1-T3 are empty.
    internal static class BerthTiers
    {
        internal const float TwinMinSeparation = 3f;
        internal const float TwinMaxSeparation = 20f;
        internal const float CentreSideCosine = 0.5f;
        internal const float SlotLengthAllowance = 1f;
        internal const float SlotDockTolerance = 0.25f;
        internal const float StationStep = 2f;
        internal const float StationRange = 40f;
        internal const float OffshoreRange = 60f;
        internal const int OffshoreBearings = 16;
        // Stored yaw is a float roundtrip through Unity's yaw conversion.
        internal const float OpenWaterHeadingToleranceDegrees = 1f;
        private static readonly float OpenWaterHeadingCos = (float)Math.Cos(OpenWaterHeadingToleranceDegrees * Math.PI / 180.0);
        internal const float StationShoreClearance = 6f;
        // Ports without a recovery berth, and the marker-frame fallback.
        internal const float NoRecoveryStandoff = 4.5f;
        internal const float MarkerHalfSpan = 8f;
        internal const float SyntheticRange = 25f;
        // A tier 3 pier's extent (cleats plus clustered neighbours) must reach 0.6 x the hull length.
        internal const float MinPierLengthFactor = 0.6f;
        // Ranking: open water around a berth counts up to 8 m, in 1 m buckets.
        internal const float SpaceCap = 8f;
        internal const float SpaceBucket = 1f;
        // Preferred mooring line length within a space bucket.
        internal const float ComfortableLine = 8f;
        // Cleats this close to a berth's own chain are part of that chain's edge.
        internal const float OwnEdgeRange = 4f;
        private static readonly float CosTwin = (float)Math.Cos(DockGeometry.MaxTurnDegrees * Math.PI / 180.0);

        // ------------------------------------------------------------ recovery reference

        // Vanilla Jungle, Swamp and Flower recovery berths reference Cave's
        // cleats. When a port's recovery cleats are missing or beyond
        // DockGeometry.MaxRecoveryCleatRange, use the two island cleats nearest
        // the marker (both within 25 m, 4-25 m apart, marker >= 1 m off their
        // line) as the recovery reference. r1 is the one nearer the bow.
        internal static bool TrySyntheticRecovery(IList<Vec2> cleats, int count, Vec2 boatPos, Vec2 boatForward,
            out int r1, out int r2, out string reason)
        {
            r1 = r2 = -1;
            reason = null;
            if (cleats == null || !boatPos.IsFinite) { reason = "no island cleats or marker"; return false; }
            var nearest = Enumerable.Range(0, Math.Min(count, cleats.Count)).Where(i => cleats[i].IsFinite)
                .OrderBy(i => Vec2.Distance(cleats[i], boatPos)).Take(2).ToArray();
            if (nearest.Length < 2) { reason = "fewer than two island cleats"; return false; }
            var a = cleats[nearest[0]];
            var b = cleats[nearest[1]];
            var farthest = Vec2.Distance(b, boatPos);
            if (!(farthest <= SyntheticRange))
            {
                reason = "the second nearest island cleat is " + DockGeometry.F(farthest) + " m from the marker (at most " +
                    DockGeometry.F(SyntheticRange) + " m)";
                return false;
            }
            var span = Vec2.Distance(a, b);
            if (!(span >= DockGeometry.MinStep && span <= DockGeometry.MaxStep))
            {
                reason = "the nearest island cleats are " + DockGeometry.F(span) + " m apart (4-25 m required)";
                return false;
            }
            var offLine = Math.Abs(Vec2.Cross((b - a) * (1f / span), boatPos - a));
            if (!(offLine >= DockGeometry.MinStandoff))
            {
                reason = "the marker is " + DockGeometry.F(offLine) + " m off the island cleats' line (at least 1 m required)";
                return false;
            }
            var ahead = boatForward.IsFinite && Vec2.Dot(b - a, boatForward) > 0f;
            r1 = ahead ? nearest[1] : nearest[0];
            r2 = ahead ? nearest[0] : nearest[1];
            return true;
        }

        // Vanilla RecoveryPort.GetBoatPos moves an obstructed boat 12 m to the
        // marker's left (goLeft) or right, away from the dock; so the dock lies
        // on +right when goLeft and on -right otherwise (New Beginnings'
        // BerthClearance.TryDirections). True for 25 of 28 dump ports with
        // valid cleats (not Cave, Mirage Mountain, Coffee): a fallback only.
        internal static Vec2 MarkerDockSide(Vec2 forward, bool goLeft)
        {
            var right = (forward * (1f / forward.Length)).RightOfForward;
            return goLeft ? right : -right;
        }

        // Two virtual recovery points: a 16 m line 4.5 m from the marker on its dock side.
        internal static void MarkerReference(Vec2 boatPos, Vec2 forward, bool goLeft, out Vec2 front, out Vec2 back)
        {
            var along = forward * (1f / forward.Length);
            var middle = boatPos + MarkerDockSide(forward, goLeft) * NoRecoveryStandoff;
            front = middle + along * MarkerHalfSpan;
            back = middle - along * MarkerHalfSpan;
        }

        // Whether a recovery pair puts the dock on the same side of the marker
        // as the marker frame does.
        internal static bool AgreesWithMarker(Vec2 r1, Vec2 r2, Vec2 boatPos, Vec2 forward, bool goLeft)
        {
            var axis = r2 - r1;
            var side = Vec2.Cross(axis, boatPos - r1) > 0f ? 1f : -1f;
            var dock = -(axis * (1f / axis.Length)).Perp * side;
            return Vec2.Dot(dock, MarkerDockSide(forward, goLeft)) > 0f;
        }

        // Another boat's planned berth at the same port (boats sharing a
        // shipyard are resolved in turn): its hull is an obstacle with the
        // normal gap rules, and its two cleats are taken, like a spring held
        // by a boat that stays (piers are still built through them).
        internal static void AddPlanned(PortInput port, string owner, Candidate planned)
        {
            if (port == null || planned == null || !planned.Accepted) return;
            var obstacles = new List<Obstacle>(port.Obstacles ?? new Obstacle[0])
            {
                new Obstacle { Name = "planned berth of " + owner, Root = planned.Root, Measurable = true, Hull = planned.Hull }
            };
            port.Obstacles = obstacles;
            if (planned.Tier == 4 || port.Free == null) return;
            foreach (var cleat in new[] { planned.FrontCleat, planned.BackCleat })
            {
                if (cleat < 0 || cleat >= port.Free.Length) continue;
                port.Free[cleat] = false;
                if (port.Unavailable != null && cleat < port.Unavailable.Length) port.Unavailable[cleat] = "planned berth of " + owner;
            }
        }

        internal static PortFrame Frame(PortInput port)
        {
            var frame = new PortFrame
            {
                NoRecovery = port.NoRecovery, RadialSearch = port.MarkerReference && port.NoDock,
                OffshoreOnly = port.OffshoreOnly, StationPolicy = port.StationPolicy, StationRegion = port.StationRegion
            };
            if (!port.NoRecovery || port.MarkerReference)
            {
                frame.Recovery = DockGeometry.BuildChain(port.Cleats, port.R1, port.R2, port.BoatPos);
                if (!frame.Recovery.Ok) { frame.Reason = frame.Recovery.Reason; return frame; }
                frame.Base = RecoveryInput(port, frame.Recovery);
                frame.Base.NoRecovery = port.NoRecovery;
                frame.Base.Ground = port.Ground;
                frame.Piers = BuildPiers(port, frame.Recovery);
                FillStructure(frame, port);
                frame.Base.OtherPiers = frame.DockSegments;
                frame.Standoff = frame.Recovery.Standoff;
                var axis = frame.Recovery.Points[frame.Recovery.R2] - frame.Recovery.Points[frame.Recovery.R1];
                frame.StationOrigin = port.BoatPos;
                frame.StationForward = port.BoatForward * (1f / port.BoatForward.Length);
                frame.StationNormals = new[] { frame.Recovery.Normal(axis * (1f / axis.Length)) };
                frame.StationStart = NoRecoveryStandoff;
                frame.Ok = true;
                return frame;
            }
            frame.Recovery = new DockChain { Ok = true, Order = new int[0], Points = new Vec2[0], R1 = -1, R2 = -1 };
            frame.Piers = BuildPiers(port, frame.Recovery);
            FillStructure(frame, port);
            frame.Reference = frame.Piers.OrderByDescending(pier => pier.Length).FirstOrDefault();
            if (frame.Reference == null || frame.Reference.Length < DockGeometry.MinStep)
            {
                frame.Reason = "no recovery berth and no pier to take a reference from";
                return frame;
            }
            var reference = frame.Reference;
            var direction = (reference.Points[reference.Points.Length - 1] - reference.Points[0]) * (1f / reference.Length);
            frame.Base = new BerthInput
            {
                BoatPos = (reference.Points[0] + reference.Points[reference.Points.Length - 1]) * 0.5f, BoatForward = direction,
                AlternateOffset = new Vec2(0f, 0f), Obstacles = port.Obstacles, NoRecovery = true, OtherPiers = frame.DockSegments,
                Ground = port.Ground
            };
            frame.Standoff = NoRecoveryStandoff;
            frame.StationOrigin = frame.Base.BoatPos;
            frame.StationForward = direction;
            frame.StationNormals = reference.Side != 0 ? new[] { direction.Perp * reference.Side } : new[] { direction.Perp, -direction.Perp };
            frame.StationStart = NoRecoveryStandoff;
            frame.Ok = true;
            return frame;
        }

        // ------------------------------------------------------------ tiers

        // Tiers in order: recovery dock line and vacated berths (1-2), other
        // piers (3), then a station-kept spot (4).
        internal static TierEvaluation Evaluate(PortInput port, HullSpec hull)
        {
            var frame = Frame(port);
            var result = new TierEvaluation { Frame = frame, Recovery = frame.Recovery };
            if (!frame.Ok)
            {
                result.Reason = frame.Reason;
                return result;
            }
            if (!ValidHull(hull, out var invalid))
            {
                result.Reason = invalid;
                return result;
            }
            result.Piers.AddRange(frame.Piers);
            if (!frame.NoRecovery && !port.OffshoreOnly && !port.StationRegion.HasValue)
            {
                var tier12 = DockGeometry.EvaluateAll(frame.Base, hull).Candidates;
                foreach (var slot in port.Slots) tier12.Add(EvaluateSlot(port, frame.Base, hull, slot));
                result.Candidates.AddRange(tier12);
                result.Best = Rank(frame, port, tier12, true);
                if (result.Best != null) return result;
            }
            if (!port.OffshoreOnly && !port.StationRegion.HasValue)
            {
                foreach (var pier in frame.Piers)
                    foreach (var side in pier.Side != 0 ? new[] { pier.Side } : new[] { 1, -1 })
                        result.Candidates.AddRange(EvaluatePier(port, frame, hull, pier, side).Candidates);
                result.Best = Rank(frame, port, result.Candidates.Where(item => item.Tier == 3), !frame.NoRecovery);
                if (result.Best != null) return result;
            }
            var station = FindStation(frame, hull, 0f);
            result.Candidates.Add(station);
            if (station.Accepted) result.Best = station;
            if (result.Best == null) result.Reason = "No tier offered a valid berth.";
            return result;
        }

        private static bool ValidHull(HullSpec hull, out string reason)
        {
            reason = hull == null || !DockGeometry.Finite(hull.Length) || !DockGeometry.Finite(hull.Radius) || !DockGeometry.Finite(hull.Extra) ||
                !DockGeometry.Finite(hull.CenterRight) || !DockGeometry.Finite(hull.CenterForward) || hull.Radius <= 0f ||
                hull.Length < 2f * hull.Radius || hull.Extra < 0f ? "Hull measurements are invalid." : null;
            return reason == null;
        }

        // Space first: open water around the berth (dock segments other than
        // its own, other hulls, reserves), capped at 8 m, in 1 m buckets. Then
        // farthest from recovery; a port without a recovery berth falls back
        // to pier extent and the shorter longest line.
        internal static Candidate Rank(PortFrame frame, PortInput port, IEnumerable<Candidate> candidates, bool byDistance) =>
            Ordered(frame, port, candidates, byDistance).FirstOrDefault();

        // Every accepted candidate, best first.
        internal static List<Candidate> Ordered(PortFrame frame, PortInput port, IEnumerable<Candidate> candidates, bool byDistance)
        {
            var accepted = candidates.Where(item => item.Accepted).ToList();
            foreach (var candidate in accepted) candidate.Space = Space(frame, port, candidate);
            // Within a bucket, berths whose lines are all 8 m or less come first;
            // otherwise the shorter longest line wins before distance.
            var ordered = accepted.OrderByDescending(item => Math.Floor(item.Space / SpaceBucket))
                .ThenBy(item => Longest(item) <= ComfortableLine ? 0f : Longest(item));
            return (byDistance ? ordered.ThenByDescending(item => item.Distance)
                : ordered.ThenByDescending(item => item.PierLength).ThenBy(Longest)).ToList();
        }

        private static float Longest(Candidate candidate) => Math.Max(candidate.FrontLine, candidate.BackLine);

        internal static float Space(PortFrame frame, PortInput port, Candidate candidate)
        {
            var space = SpaceCap;
            if (candidate.Blocker != null && DockGeometry.Finite(candidate.MinimumGap)) space = Math.Min(space, candidate.MinimumGap);
            int[] own = null;
            if (candidate.FrontCleat >= 0 && candidate.BackCleat >= 0)
                own = frame.StructureChains.FirstOrDefault(chain => chain.Contains(candidate.FrontCleat) && chain.Contains(candidate.BackCleat))
                    ?? frame.StructureChains.FirstOrDefault(chain => chain.Contains(candidate.FrontCleat) || chain.Contains(candidate.BackCleat));
            var ownPoints = own?.Select(index => port.Cleats[index]).ToArray();
            foreach (var chain in frame.StructureChains)
                if (chain != own) space = Math.Min(space, DockGeometry.ChainClearance(candidate.Hull, chain.Select(index => port.Cleats[index]).ToArray()));
            foreach (var link in frame.CornerLinks) space = Math.Min(space, DockGeometry.ChainClearance(candidate.Hull, link));
            for (var i = 0; i < port.Cleats.Length; ++i)
            {
                if (candidate.FrontCleat == i || candidate.BackCleat == i) continue;
                var point = port.Cleats[i];
                if (ownPoints != null && (ownPoints.Length == 1 ? Vec2.Distance(point, ownPoints[0])
                    : DockGeometry.ChainClearance(new Capsule2(point, point, 0f), ownPoints)) <= OwnEdgeRange) continue;
                space = Math.Min(space, DockGeometry.PointSegmentDistance(point, candidate.Hull.A, candidate.Hull.B) - candidate.Hull.Radius);
            }
            return Math.Max(space, float.MinValue);
        }

        private static void FillStructure(PortFrame frame, PortInput port)
        {
            frame.DockSegments = DockSegments(port, frame.Recovery, out var chains, out var links);
            frame.StructureChains = chains;
            frame.CornerLinks = links;
        }

        // Structural dock segments, built without the claims of staying boats.
        internal static List<Vec2[]> DockSegments(PortInput port, DockChain recovery, out List<int[]> chainsOut, out List<Vec2[]> linksOut)
        {
            var structure = new PortInput
            {
                Cleats = port.Cleats, Names = port.Names, R1 = port.R1, R2 = port.R2, NoRecovery = port.NoRecovery,
                MarkerReference = port.MarkerReference,
                IslandCentre = port.IslandCentre, HasIslandCentre = port.HasIslandCentre
            };
            var segments = new List<Vec2[]>();
            var chains = new List<int[]>();
            if (recovery != null && recovery.Points.Length >= 2)
            {
                segments.Add(recovery.Points);
                chains.Add(recovery.Order);
            }
            foreach (var pier in BuildPiers(structure, recovery ?? new DockChain { Order = new int[0], Points = new Vec2[0] }))
            {
                segments.Add(pier.Points);
                chains.Add(pier.Order);
            }
            // A chain stops where the quay turns a corner (Fort M(8) -> M(9)),
            // but the quay itself continues: join each chain end to its nearest
            // cleat outside that chain within one step.
            var links = new HashSet<long>();
            var cornerLinks = new List<Vec2[]>();
            foreach (var chain in chains)
                foreach (var end in new[] { chain[0], chain[chain.Length - 1] })
                {
                    var nearest = -1;
                    var best = DockGeometry.MaxStep;
                    for (var i = 0; i < port.Cleats.Length; ++i)
                    {
                        if (chain.Contains(i)) continue;
                        var distance = Vec2.Distance(port.Cleats[end], port.Cleats[i]);
                        if (distance > 0.01f && distance <= best) { best = distance; nearest = i; }
                    }
                    if (nearest < 0 || !links.Add(Math.Min(end, nearest) * 100000L + Math.Max(end, nearest))) continue;
                    segments.Add(new[] { port.Cleats[end], port.Cleats[nearest] });
                    cornerLinks.Add(new[] { port.Cleats[end], port.Cleats[nearest] });
                }
            segments.AddRange(port.Cleats.Select(point => new[] { point, point }));
            chainsOut = chains;
            linksOut = cornerLinks;
            return segments;
        }

        internal static BerthInput RecoveryInput(PortInput port, DockChain chain)
        {
            var count = chain.Order.Length;
            var input = new BerthInput
            {
                Chain = chain, Available = new bool[count], Unavailable = new string[count], Names = new string[count],
                BoatPos = port.BoatPos, BoatForward = port.BoatForward, AlternateOffset = port.AlternateOffset,
                Obstacles = port.Obstacles
            };
            for (var i = 0; i < count; ++i)
            {
                var cleat = chain.Order[i];
                input.Names[i] = port.Name(cleat);
                input.Unavailable[i] = i == chain.R1 || i == chain.R2 ? "recovery berth cleat" : Unavailable(port, cleat);
                input.Available[i] = input.Unavailable[i] == null;
            }
            return input;
        }

        private static string Unavailable(PortInput port, int cleat) =>
            cleat == port.R1 || cleat == port.R2 ? "recovery berth cleat"
            : port.Free == null || cleat < 0 || cleat >= port.Free.Length || port.Free[cleat] ? null
            : port.Unavailable != null && port.Unavailable[cleat] != null ? port.Unavailable[cleat] : "claimed";

        // ------------------------------------------------------------ tier 2

        // The incoming hull takes the owner's capsule centre and yaw, either
        // bow direction. Vanilla already parks the owner here, so recovery
        // gaps are waived for a hull at most 1 m longer than the owner's.
        internal static Candidate EvaluateSlot(PortInput port, BerthInput input, HullSpec hull, BerthSlot slot)
        {
            Candidate best = null;
            foreach (var sign in new[] { 1f, -1f })
            {
                var candidate = new Candidate
                {
                    Tier = 2, SlotOwner = slot.Owner, I = -1, J = -1
                };
                var reason = SlotProblem(port, slot);
                if (reason != null) return DockGeometry.Reject(candidate, reason);
                var forward = slot.Forward * (sign / slot.Forward.Length);
                var outwardFront = OasisSaleFront(port, slot);
                if (outwardFront >= 0)
                {
                    var outwardBack = outwardFront == slot.FrontCleat ? slot.BackCleat : slot.FrontCleat;
                    // On this side of the Oasis pier, the native Baghlah bow
                    // points from cleat 7 toward 5. Choosing solely the shorter
                    // lines reverses some incoming hulls into the dock. Evaluate
                    // that departing direction with all normal root, clearance
                    // and line checks rather than flipping an accepted pose.
                    if (Vec2.Dot(forward, port.Cleats[outwardFront] - port.Cleats[outwardBack]) <= 0f) continue;
                }
                var half = Math.Max(0f, hull.Length * 0.5f - hull.Radius);
                candidate.Forward = forward;
                candidate.Direction = forward;
                candidate.Hull = new Capsule2(slot.Center + forward * half, slot.Center - forward * half, hull.Radius);
                var front = Vec2.Dot(port.Cleats[slot.FrontCleat] - slot.Center, forward) >=
                    Vec2.Dot(port.Cleats[slot.BackCleat] - slot.Center, forward) ? slot.FrontCleat : slot.BackCleat;
                var back = front == slot.FrontCleat ? slot.BackCleat : slot.FrontCleat;
                candidate.FrontCleat = front;
                candidate.BackCleat = back;
                candidate.Span = Vec2.Distance(port.Cleats[front], port.Cleats[back]);
                var waive = hull.Length <= slot.Length + SlotLengthAllowance;
                var dock = float.PositiveInfinity;
                var ownerDock = float.PositiveInfinity;
                var ownerHalf = Math.Max(0f, slot.Length * 0.5f - slot.Radius);
                var owner = new Capsule2(slot.Center + forward * ownerHalf, slot.Center - forward * ownerHalf, Math.Max(slot.Radius, 0.01f));
                if (input.OtherPiers != null)
                    foreach (var segment in input.OtherPiers)
                    {
                        dock = Math.Min(dock, DockGeometry.ChainClearance(candidate.Hull, segment));
                        if (slot.Radius > 0f) ownerDock = Math.Min(ownerDock, DockGeometry.ChainClearance(owner, segment));
                    }
                candidate.DockClearance = dock;
                // Vanilla already parks the owner here; where it sits closer than
                // the margin, its own clearance (less 0.25 m) is the floor.
                var required = Math.Min(DockGeometry.DockClearance, ownerDock - SlotDockTolerance);
                if (!(dock >= required))
                    DockGeometry.Reject(candidate, "Hull is " + DockGeometry.F(dock) + " m from the island's docks; at least " +
                        DockGeometry.F(required) + " m is required.");
                else if (DockGeometry.CheckClearances(candidate, input, hull, waive))
                {
                    DockGeometry.SetRoot(candidate, input, hull);
                    var ground = DockGeometry.GroundProblem(candidate.Hull, input.Ground);
                    if (ground != null) DockGeometry.Reject(candidate, Rejection.ShallowGround, ground);
                    else if (DockGeometry.CheckLines(candidate, hull, port.Cleats[front], port.Cleats[back]))
                    {
                        candidate.Accepted = true;
                        candidate.Reason = "accepted" + (waive ? " (recovery gaps waived)" : "");
                    }
                }
                else
                {
                    // Away from the owner's cleats, along the berth normal.
                    var normal = forward.Perp;
                    if (Vec2.Dot(normal, slot.Center - (port.Cleats[front] + port.Cleats[back]) * 0.5f) < 0f) normal = -normal;
                    DockGeometry.TieOff(candidate, input, hull, normal, port.Cleats[front], port.Cleats[back], input.OtherPiers);
                }
                if (best == null || candidate.Accepted && (!best.Accepted ||
                    Math.Max(candidate.FrontLine, candidate.BackLine) < Math.Max(best.FrontLine, best.BackLine)))
                    best = candidate;
            }
            return best ?? DockGeometry.Reject(new Candidate { Tier = 2, SlotOwner = slot.Owner },
                "Vacated Oasis sale berth has no heading toward its departing cleat.");
        }

        private static int OasisSaleFront(PortInput port, BerthSlot slot)
        {
            if (!port.OasisNativeSaleHeading || port.Names == null) return -1;
            var front = port.Name(slot.FrontCleat);
            var back = port.Name(slot.BackCleat);
            const string outer = KnownIdentities.OasisSaleFrontCleat;
            const string inner = KnownIdentities.OasisSaleBackCleat;
            var outerIndex = front == outer && back == inner ? slot.FrontCleat
                : front == inner && back == outer ? slot.BackCleat : -1;
            if (outerIndex < 0) return -1;
            var innerIndex = outerIndex == slot.FrontCleat ? slot.BackCleat : slot.FrontCleat;
            var axis = port.Cleats[innerIndex] - port.Cleats[outerIndex];
            // This pair borders the native sale side. Do not apply its heading
            // across the quay on the recovery side, even for a reused pair.
            var berthSide = Vec2.Cross(axis, slot.Center - port.Cleats[outerIndex]);
            var recoverySide = Vec2.Cross(axis, port.BoatPos - port.Cleats[outerIndex]);
            return DockGeometry.Finite(berthSide) && DockGeometry.Finite(recoverySide) && berthSide * recoverySide < 0f
                ? outerIndex : -1;
        }

        private static string SlotProblem(PortInput port, BerthSlot slot)
        {
            if (slot == null || slot.FrontCleat < 0 || slot.BackCleat < 0 || slot.FrontCleat >= port.Cleats.Length ||
                slot.BackCleat >= port.Cleats.Length || slot.FrontCleat == slot.BackCleat)
                return "Vacated berth cleats are not island cleats of this port.";
            if (!slot.Center.IsFinite || !slot.Forward.IsFinite || slot.Forward.Length < 0.5f || !DockGeometry.Finite(slot.Length))
                return "Vacated berth pose is invalid.";
            if (slot.FrontCleat == port.R1 || slot.FrontCleat == port.R2 || slot.BackCleat == port.R1 || slot.BackCleat == port.R2)
                return "Vacated berth uses the recovery cleats.";
            var problem = Unavailable(port, slot.FrontCleat) ?? Unavailable(port, slot.BackCleat);
            return problem != null ? "Vacated berth cleat is unavailable: " + problem : null;
        }

        // ------------------------------------------------------------ tier 3

        // Chains from every island cleat off the recovery chain and not claimed
        // by a staying boat, seeded from the closest unused pair at least 4 m
        // apart, with the same step, turn and branch rules as the recovery
        // chain. Cleats within 4 m of a chain cleat (paired bollards) are
        // skipped rather than seeding or breaking another pier.
        internal static List<Pier> BuildPiers(PortInput port, DockChain recovery)
        {
            var used = new bool[port.Cleats.Length];
            foreach (var cleat in recovery.Order) used[cleat] = true;
            if (port.R1 >= 0 && port.R1 < used.Length) used[port.R1] = true;
            if (port.R2 >= 0 && port.R2 < used.Length) used[port.R2] = true;
            if (port.Claimed != null)
                for (var i = 0; i < used.Length && i < port.Claimed.Length; ++i) used[i] |= port.Claimed[i];
            // Cleats sitting on the recovery dock line (such as one between the
            // recovery pair) belong to it, not to another pier.
            if (recovery.Points.Length >= 2)
                for (var i = 0; i < used.Length; ++i)
                    if (!used[i] && DockGeometry.ChainClearance(new Capsule2(port.Cleats[i], port.Cleats[i], 0f), recovery.Points) <=
                        DockGeometry.InlineTolerance) used[i] = true;
            var piers = new List<Pier>();
            while (true)
            {
                int a = -1, b = -1;
                var nearest = float.PositiveInfinity;
                for (var i = 0; i < port.Cleats.Length; ++i)
                {
                    if (used[i]) continue;
                    for (var j = i + 1; j < port.Cleats.Length; ++j)
                    {
                        if (used[j]) continue;
                        var distance = Vec2.Distance(port.Cleats[i], port.Cleats[j]);
                        if (distance >= DockGeometry.MinStep && distance <= DockGeometry.MaxStep && distance < nearest)
                        {
                            nearest = distance; a = i; b = j;
                        }
                    }
                }
                if (a < 0) break;
                used[a] = used[b] = true;
                var axis = (port.Cleats[b] - port.Cleats[a]) * (1f / nearest);
                var before = DockGeometry.Extend(port.Cleats, used, port.Cleats[a], -axis);
                var after = DockGeometry.Extend(port.Cleats, used, port.Cleats[b], axis);
                var order = new List<int>();
                for (var i = before.Count - 1; i >= 0; --i) order.Add(before[i]);
                order.Add(a);
                order.Add(b);
                order.AddRange(after);
                var cluster = new List<int>();
                foreach (var member in order)
                    for (var i = 0; i < used.Length; ++i)
                        if (!used[i] && Vec2.Distance(port.Cleats[i], port.Cleats[member]) < DockGeometry.MinStep)
                        {
                            used[i] = true;
                            cluster.Add(i);
                        }
                // Extent along the pier: clustered cleats across a jetty's width
                // (Fire Fish Town E(17)-E(20)) add nothing; bollard pairs along
                // the edge (Onna) extend it.
                var pierAxis = port.Cleats[order[order.Count - 1]] - port.Cleats[order[0]];
                pierAxis = pierAxis * (1f / Math.Max(pierAxis.Length, 1e-3f));
                var along = order.Concat(cluster).Select(index => Vec2.Dot(port.Cleats[index] - port.Cleats[order[0]], pierAxis)).ToArray();
                var extent = along.Max() - along.Min();
                piers.Add(new Pier { Order = order.ToArray(), Points = order.Select(index => port.Cleats[index]).ToArray(), Cluster = cluster.ToArray(), Extent = extent });
            }
            var all = new List<Vec2[]> { recovery.Points };
            all.AddRange(piers.Select(pier => pier.Points));
            foreach (var pier in piers)
            {
                pier.Side = WaterSide(pier.Points, all.Where(points => points != pier.Points).ToList(),
                    port.HasIslandCentre ? (Vec2?)port.IslandCentre : null, out var reason);
                pier.SideReason = reason;
            }
            return piers;
        }

        // Water is away from a parallel twin pier (within 15 degrees, 3-20 m
        // apart, overlapping), else away from the island centre when the pier
        // faces it clearly (|cos| >= 0.5); otherwise both sides are tried.
        internal static int WaterSide(Vec2[] points, IList<Vec2[]> others, Vec2? islandCentre, out string reason)
        {
            reason = null;
            if (points == null || points.Length < 2) { reason = "single cleat"; return 0; }
            var first = points[0];
            var axis = points[points.Length - 1] - first;
            var length = axis.Length;
            if (!(length > 0.5f)) { reason = "degenerate pier"; return 0; }
            var direction = axis * (1f / length);
            var twinSides = new HashSet<int>();
            foreach (var other in others ?? new Vec2[0][])
            {
                if (other == null || other.Length < 2) continue;
                var otherAxis = other[other.Length - 1] - other[0];
                var otherLength = otherAxis.Length;
                if (!(otherLength > 0.5f)) continue;
                if (Math.Abs(Vec2.Dot(direction, otherAxis * (1f / otherLength))) < CosTwin - 1e-6f) continue;
                var centroid = Centroid(other);
                var separation = Vec2.Cross(direction, centroid - first);
                if (Math.Abs(separation) < TwinMinSeparation || Math.Abs(separation) > TwinMaxSeparation) continue;
                var a = Vec2.Dot(other[0] - first, direction);
                var b = Vec2.Dot(other[other.Length - 1] - first, direction);
                if (Math.Max(a, b) < 0f || Math.Min(a, b) > length) continue;
                twinSides.Add(separation > 0f ? 1 : -1);
            }
            if (twinSides.Count == 1)
            {
                reason = "away from a parallel pier";
                return -twinSides.First();
            }
            if (twinSides.Count == 2) { reason = "parallel piers on both sides"; return 0; }
            if (islandCentre.HasValue && islandCentre.Value.IsFinite)
            {
                var toCentre = islandCentre.Value - Centroid(points);
                var distance = toCentre.Length;
                if (distance > 0.5f)
                {
                    var facing = Vec2.Dot(direction.Perp, toCentre * (1f / distance));
                    if (Math.Abs(facing) >= CentreSideCosine)
                    {
                        reason = "away from the island centre";
                        return facing > 0f ? -1 : 1;
                    }
                }
                reason = "pier runs toward the island centre";
                return 0;
            }
            reason = "no island centre";
            return 0;
        }

        private static Vec2 Centroid(Vec2[] points)
        {
            var sum = new Vec2(0f, 0f);
            foreach (var point in points) sum = sum + point;
            return sum * (1f / points.Length);
        }

        internal static Evaluation EvaluatePier(PortInput port, PortFrame frame, HullSpec hull, Pier pier, int side)
        {
            if (pier.Extent < MinPierLengthFactor * hull.Length)
            {
                var short_ = new Evaluation { Reason = "Pier too small" };
                short_.Candidates.Add(DockGeometry.Reject(new Candidate
                {
                    Tier = 3, Estimated = true, Side = side, I = -1, J = -1, PierLength = pier.Extent,
                    FrontCleat = pier.Order[0], BackCleat = pier.Order[pier.Order.Length - 1]
                }, "Pier " + string.Join(">", pier.Order.Select(port.Name).ToArray()) + " spans " + DockGeometry.F(pier.Extent) +
                    " m with its clustered cleats; this hull needs " + DockGeometry.F(MinPierLengthFactor * hull.Length) + " m."));
                return short_;
            }
            var chain = new DockChain
            {
                Ok = true, Order = pier.Order, Points = pier.Points, R1 = -1, R2 = -1, Side = side, Standoff = frame.Standoff
            };
            var count = pier.Order.Length;
            var others = frame.DockSegments;
            var input = new BerthInput
            {
                Chain = chain, Available = new bool[count], Unavailable = new string[count], Names = new string[count],
                BoatPos = frame.Base.BoatPos, BoatForward = frame.Base.BoatForward, AlternateOffset = frame.Base.AlternateOffset,
                Obstacles = frame.Base.Obstacles, NoRecovery = frame.NoRecovery, OtherPiers = others,
                Ground = frame.Base.Ground
            };
            for (var i = 0; i < count; ++i)
            {
                input.Names[i] = port.Name(pier.Order[i]);
                input.Unavailable[i] = Unavailable(port, pier.Order[i]);
                input.Available[i] = input.Unavailable[i] == null;
            }
            var evaluation = DockGeometry.EvaluateAll(input, hull);
            foreach (var candidate in evaluation.Candidates)
            {
                candidate.Tier = 3;
                candidate.Estimated = true;
                candidate.Side = side;
                candidate.PierLength = pier.Extent;
            }
            return evaluation;
        }

        // ------------------------------------------------------------ tier 4

        // Parallel to the reference (recovery berth, or a no-recovery port's
        // longest pier), outward on its water side: the smallest outward offset
        // that clears the recovery reserves and marker (when present), staying
        // boats and 6 m from every island cleat.
        internal static Candidate FindStation(PortFrame frame, HullSpec hull, float minimumOutward)
        {
            Candidate last = null;
            foreach (var candidate in StationSpots(frame, hull, minimumOutward))
            {
                if (candidate.Accepted) return candidate;
                last = candidate;
            }
            if (last == null) last = new Candidate { Tier = 4, Estimated = true };
            if (frame.StationRegion.HasValue)
                return DockGeometry.Reject(last, "No compatible station-kept spot in the port's offshore gap (last: " + last.Reason + ")");
            if (frame.RadialSearch)
                return DockGeometry.Reject(last, "No station-kept spot within " + DockGeometry.F(OffshoreRange) +
                    " m of the authored marker (last: " + last.Reason + ")");
            return DockGeometry.Reject(last, "No station-kept spot within " + DockGeometry.F(StationRange) + " m outward of the " +
                (frame.NoRecovery ? "reference pier" : "recovery berth") + " (last: " + last.Reason + ")");
        }

        // Every station candidate in search order (accepted or not): 2 m steps
        // outward up to 40 m beyond the start, each normal, five shifts.
        internal static IEnumerable<Candidate> StationSpots(PortFrame frame, HullSpec hull, float minimumOutward)
        {
            if (frame.StationRegion.HasValue)
            {
                foreach (var candidate in RegionSpots(frame, hull)) yield return candidate;
                yield break;
            }
            if (frame.RadialSearch)
            {
                foreach (var candidate in OffshoreSpots(frame, hull, minimumOutward)) yield return candidate;
                yield break;
            }
            var segments = frame.DockSegments;
            var start = Math.Max(minimumOutward, StationStart(frame, hull));
            if (!DockGeometry.Finite(start) || start < 0f) yield break;
            var shifts = new[] { 0f, 0.5f, -0.5f, 1f, -1f };
            var steps = 0;
            for (var offset = start; steps <= StationRange / StationStep && offset <= start + StationRange + 1e-3f;
                offset += StationStep, ++steps)
                foreach (var normal in frame.StationNormals)
                    foreach (var shift in shifts)
                    {
                        var center = frame.StationOrigin + normal * offset + frame.StationForward * (shift * hull.Length);
                        // Keep shifts along the quay, but face out to water when
                        // the port requires an offshore berth.
                        var forward = frame.OffshoreOnly ? normal : frame.StationForward;
                        var policyProblem = StationPolicyProblem(frame, hull, center, false);
                        if (policyProblem != null) continue;
                        if (frame.StationPolicy.LeftHullWidths > 0f || frame.StationPolicy.BackHullLengths > 0f)
                        {
                            // Shift an already viable reference candidate. If
                            // the translated search started at the same minimum,
                            // its clearance search could shrink outward distance
                            // and cancel the requested lateral displacement.
                            if (!EvaluateStation(frame.Base, hull, center, forward, segments).Accepted) continue;
                            center += frame.StationForward.Perp * (frame.StationPolicy.LeftHullWidths * 2f * hull.Radius);
                            // Backward placement starts from the accepted left
                            // placement, retaining its reference search result.
                            if (frame.StationPolicy.BackHullLengths > 0f)
                            {
                                if (!EvaluateStation(frame.Base, hull, center, forward, segments).Accepted) continue;
                                center -= frame.StationForward * (frame.StationPolicy.BackHullLengths * hull.Length);
                            }
                            if (StationPolicyProblem(frame, hull, center) != null) continue;
                        }
                        var candidate = EvaluateStation(frame.Base, hull, center, forward, segments);
                        candidate.Outward = offset;
                        candidate.Shift = shift * hull.Length;
                        yield return candidate;
                    }
        }

        private static float StationStart(PortFrame frame, HullSpec hull) =>
            (frame.NoRecovery ? frame.StationStart : hull.Radius + DockGeometry.ReserveRadius + DockGeometry.BoatGap) +
            frame.StationPolicy.OutwardHullLengths * hull.Length;

        // Translate the departure reference to port/left relative to the
        // unchanged boat heading. Stored poses are checked against this origin,
        // never translated a second time. Geometry uses the actual shifted hull.
        private static Vec2 StationOrigin(PortFrame frame, HullSpec hull) =>
            frame.StationOrigin + frame.StationForward.Perp * (frame.StationPolicy.LeftHullWidths * 2f * hull.Radius) -
            frame.StationForward * (frame.StationPolicy.BackHullLengths * hull.Length);

        // Express outward distance in the live berth frame. The quay and
        // marker axes need not be exactly perpendicular.
        private static string StationPolicyProblem(PortFrame frame, HullSpec hull, Vec2 center, bool translated = true)
        {
            if (frame.StationRegion.HasValue)
            {
                var region = frame.StationRegion.Value;
                if (!ValidRegion(region)) return "Station search region is invalid.";
                var offset = center - frame.StationOrigin;
                var right = Vec2.Dot(offset, frame.StationForward.RightOfForward);
                var ahead = Vec2.Dot(offset, frame.StationForward);
                const float regionMargin = 0.05f;
                if (!center.IsFinite || right < region.MinimumRight - regionMargin || right > region.MaximumRight + regionMargin ||
                    ahead < region.MinimumForward - regionMargin || ahead > region.MaximumForward + regionMargin)
                    return "Station spot is outside the port's offshore gap.";
            }
            var policy = frame.StationPolicy;
            if (!DockGeometry.Finite(policy.OutwardHullLengths) || policy.OutwardHullLengths < 0f ||
                !DockGeometry.Finite(policy.LeftHullWidths) || policy.LeftHullWidths < 0f ||
                !DockGeometry.Finite(policy.BackHullLengths) || policy.BackHullLengths < 0f)
                return "Station departure policy is invalid.";
            var delta = center - (translated ? StationOrigin(frame, hull) : frame.StationOrigin);
            const float roundtripMargin = 0.05f;
            if (policy.ForwardOnly && Vec2.Dot(delta, frame.StationForward) < -roundtripMargin)
                return "Station spot is behind the port's departure reference.";
            if (policy.OutwardHullLengths > 0f)
            {
                var outward = frame.StationNormals.Any(normal =>
                {
                    var determinant = normal.X * frame.StationForward.Z - normal.Z * frame.StationForward.X;
                    if (Math.Abs(determinant) < 1e-4f) return false;
                    var distance = (delta.X * frame.StationForward.Z - delta.Z * frame.StationForward.X) / determinant;
                    return distance >= StationStart(frame, hull) - roundtripMargin;
                });
                if (!outward) return "Station spot leaves too little room outside the port's departure lane.";
            }
            return null;
        }

        private static bool ValidRegion(StationRegion region) =>
            DockGeometry.Finite(region.MinimumRight) && DockGeometry.Finite(region.MaximumRight) &&
            DockGeometry.Finite(region.MinimumForward) && DockGeometry.Finite(region.MaximumForward) &&
            DockGeometry.Finite(region.TargetRight) && DockGeometry.Finite(region.TargetForward) &&
            region.MinimumRight <= region.TargetRight && region.TargetRight <= region.MaximumRight &&
            region.MinimumForward <= region.TargetForward && region.TargetForward <= region.MaximumForward;

        private static IEnumerable<Candidate> RegionSpots(PortFrame frame, HullSpec hull)
        {
            var region = frame.StationRegion.Value;
            if (!ValidRegion(region)) yield break;
            var offsets = new List<Vec2>();
            for (var right = region.MinimumRight; right <= region.MaximumRight; right += StationStep)
                for (var forward = region.MinimumForward; forward <= region.MaximumForward; forward += StationStep)
                    offsets.Add(new Vec2(right, forward));
            foreach (var offset in offsets.OrderBy(point =>
                (point.X - region.TargetRight) * (point.X - region.TargetRight) +
                (point.Z - region.TargetForward) * (point.Z - region.TargetForward)))
            {
                var center = frame.StationOrigin + frame.StationForward.RightOfForward * offset.X + frame.StationForward * offset.Z;
                var problem = StationPolicyProblem(frame, hull, center);
                if (problem != null) continue;
                var candidate = EvaluateStation(frame.Base, hull, center, frame.StationForward, frame.DockSegments);
                candidate.Outward = offset.X;
                candidate.Shift = offset.Z;
                yield return candidate;
            }
        }

        // A dormant marker with no real dock may lie inland. Survey every side
        // of it, placing the hull tangentially along the shore. Every candidate
        // still passes the normal full-hull depth, structure and boat checks.
        private static IEnumerable<Candidate> OffshoreSpots(PortFrame frame, HullSpec hull, float minimumOutward)
        {
            var start = Math.Max(minimumOutward, NoRecoveryStandoff);
            for (var radius = start; radius <= OffshoreRange + 1e-3f; radius += StationStep)
                for (var bearing = 0; bearing < OffshoreBearings; ++bearing)
                {
                    var angle = 2.0 * Math.PI * bearing / OffshoreBearings;
                    var radial = frame.StationForward * (float)Math.Cos(angle) + frame.StationForward.Perp * (float)Math.Sin(angle);
                    var center = frame.StationOrigin + radial * radius;
                    var candidate = EvaluateStation(frame.Base, hull, center, radial.Perp, frame.DockSegments);
                    candidate.Outward = radius;
                    yield return candidate;
                }
        }

        internal static Candidate EvaluateStation(BerthInput input, HullSpec hull, Vec2 center, Vec2 forward, IList<Vec2[]> segments)
        {
            var candidate = new Candidate { Tier = 4, Estimated = true, I = -1, J = -1 };
            if (!center.IsFinite || !forward.IsFinite || forward.Length < 0.5f)
                return DockGeometry.Reject(candidate, "Station pose is invalid.");
            forward = forward * (1f / forward.Length);
            var half = Math.Max(0f, hull.Length * 0.5f - hull.Radius);
            candidate.Forward = forward;
            candidate.Direction = forward;
            candidate.Hull = new Capsule2(center + forward * half, center - forward * half, hull.Radius);
            var shore = float.PositiveInfinity;
            foreach (var segment in segments ?? new Vec2[0][])
                shore = Math.Min(shore, DockGeometry.ChainClearance(candidate.Hull, segment.Length == 1 ? new[] { segment[0], segment[0] } : segment));
            candidate.DockClearance = shore;
            if (!(shore >= StationShoreClearance))
                return DockGeometry.Reject(candidate, "Hull is " + DockGeometry.F(shore) + " m from island cleats; at least " +
                    DockGeometry.F(StationShoreClearance) + " m is required.");
            if (!DockGeometry.CheckClearances(candidate, input, hull, false)) return candidate;
            DockGeometry.SetRoot(candidate, input, hull);
            if (!candidate.Root.IsFinite) return DockGeometry.Reject(candidate, "Station pose produced a non-finite position.");
            var ground = DockGeometry.GroundProblem(candidate.Hull, input.Ground);
            if (ground != null) return DockGeometry.Reject(candidate, Rejection.ShallowGround, ground);
            candidate.Accepted = true;
            candidate.Reason = "accepted";
            return candidate;
        }

        // ------------------------------------------------------------ fixed (new-game validation)

        // Re-evaluates a planned moored berth by tier and cleat indexes.
        internal static Candidate EvaluateMoored(PortInput port, HullSpec hull, int tier, int frontCleat, int backCleat, int side)
        {
            if (port.OffshoreOnly || port.StationRegion.HasValue)
                return DockGeometry.Reject(new Candidate { Tier = tier, FrontCleat = frontCleat, BackCleat = backCleat },
                    "This port requires an offshore station-kept berth.");
            var frame = Frame(port);
            var fail = new Candidate { Tier = tier, FrontCleat = frontCleat, BackCleat = backCleat };
            if (!frame.Ok) return DockGeometry.Reject(fail, frame.Reason);
            if (!ValidHull(hull, out var invalid)) return DockGeometry.Reject(fail, invalid);
            if (tier == 1 || tier == 2)
            {
                if (frame.NoRecovery) return DockGeometry.Reject(fail, "Tier " + tier + " needs a recovery berth.");
                if (tier == 1)
                {
                    var a = Array.IndexOf(frame.Recovery.Order, frontCleat);
                    var b = Array.IndexOf(frame.Recovery.Order, backCleat);
                    if (a < 0 || b < 0 || a == b) return DockGeometry.Reject(fail, "Planned cleats are not both on the recovery dock line.");
                    return Matches(DockGeometry.EvaluatePair(frame.Base, hull, Math.Min(a, b), Math.Max(a, b)), frontCleat, backCleat);
                }
                var slot = port.Slots.FirstOrDefault(item => item.FrontCleat == frontCleat && item.BackCleat == backCleat ||
                    item.FrontCleat == backCleat && item.BackCleat == frontCleat);
                if (slot == null)
                    return DockGeometry.Reject(fail, "No moving boat owns the planned vacated berth.");
                return Matches(EvaluateSlot(port, frame.Base, hull, slot), frontCleat, backCleat);
            }
            if (tier == 3)
            {
                var pier = frame.Piers.FirstOrDefault(item => item.Order.Contains(frontCleat) && item.Order.Contains(backCleat));
                if (pier == null) return DockGeometry.Reject(fail, "Planned cleats are no longer on one pier.");
                var i = Array.IndexOf(pier.Order, frontCleat);
                var j = Array.IndexOf(pier.Order, backCleat);
                Candidate best = null;
                foreach (var trySide in side != 0 ? new[] { side } : pier.Side != 0 ? new[] { pier.Side } : new[] { 1, -1 })
                {
                    var evaluation = EvaluatePier(port, frame, hull, pier, trySide);
                    var candidate = evaluation.Candidates.First(item => item.I == Math.Min(i, j) && item.J == Math.Max(i, j));
                    if (best == null || candidate.Accepted && !best.Accepted) best = candidate;
                }
                return Matches(best, frontCleat, backCleat);
            }
            return DockGeometry.Reject(fail, "Unknown moored tier " + tier + ".");
        }

        private static Candidate Matches(Candidate candidate, int front, int back)
        {
            if (candidate.Accepted && !(candidate.FrontCleat == front && candidate.BackCleat == back ||
                candidate.FrontCleat == back && candidate.BackCleat == front))
                return DockGeometry.Reject(candidate, "Planned cleats resolved to a different pair.");
            return candidate;
        }

        // Re-evaluates a planned station-kept spot at an exact pose.
        internal static Candidate EvaluateStationAt(PortInput port, HullSpec hull, Vec2 center, Vec2 forward)
        {
            var frame = Frame(port);
            if (!frame.Ok) return DockGeometry.Reject(new Candidate { Tier = 4, Estimated = true }, frame.Reason);
            if (!ValidHull(hull, out var invalid)) return DockGeometry.Reject(new Candidate { Tier = 4, Estimated = true }, invalid);
            var policyProblem = StationPolicyProblem(frame, hull, center);
            if (policyProblem != null) return DockGeometry.Reject(new Candidate { Tier = 4, Estimated = true }, policyProblem);
            if (frame.StationRegion.HasValue && (!forward.IsFinite || forward.Length < 0.5f ||
                Vec2.Dot(frame.StationForward, forward * (1f / forward.Length)) < OpenWaterHeadingCos))
                return DockGeometry.Reject(new Candidate { Tier = 4, Estimated = true },
                    "Planned station heading does not follow the port's offshore gap.");
            if (frame.OffshoreOnly && (!forward.IsFinite || forward.Length < 0.5f ||
                !frame.StationNormals.Any(normal => Vec2.Dot(normal, forward * (1f / forward.Length)) >= OpenWaterHeadingCos)))
                return DockGeometry.Reject(new Candidate { Tier = 4, Estimated = true },
                    "Planned station heading does not face open water.");
            return EvaluateStation(frame.Base, hull, center, forward, frame.DockSegments);
        }

        // A saved placement put back on load at its exact stored root pose,
        // with no clearance, line-length, terrain or port-policy checks: it
        // passed them all when the layout was generated. Moored (tiers 1-3)
        // when both stored cleats are usable, with the ropes and line lengths
        // the relocator and MooringSlack use for that pose; otherwise held
        // (tier 4) at the same pose, with fallback saying why. A line beyond
        // what a native rope reaches (NativeRopeMaxLine) is not usable.
        internal static Candidate StoredBerth(HullSpec hull, Vec2 root, Vec2 forward, int tier, int side, Vec2? frontCleat, Vec2? backCleat,
            out string fallback)
        {
            fallback = null;
            if (forward.IsFinite && forward.Length > 0f) forward = forward * (1f / forward.Length);
            var center = CenterFromRoot(hull, root, forward);
            var half = Math.Max(0f, hull.Length * 0.5f - hull.Radius);
            var candidate = new Candidate
            {
                Tier = tier, Side = tier == 3 ? side : 0, Root = root, Forward = forward, Direction = forward, Center = center,
                Hull = new Capsule2(center + forward * half, center - forward * half, hull.Radius), I = -1, J = -1,
                Accepted = true, Reason = "stored pose"
            };
            if (tier != 4)
            {
                if (!frontCleat.HasValue || !backCleat.HasValue) fallback = "stored cleats are unusable";
                else if (!DockGeometry.AssignRopes(candidate, hull, frontCleat.Value, backCleat.Value))
                    fallback = "no distinct mooring ropes at opposite ends";
                else if (!(Math.Max(candidate.FrontLine, candidate.BackLine) <= DockGeometry.NativeRopeMaxLine))
                    fallback = "mooring lines " + DockGeometry.F(candidate.FrontLine) + " / " + DockGeometry.F(candidate.BackLine) +
                        " m; a native rope reaches " + DockGeometry.F(DockGeometry.NativeRopeMaxLine) + " m";
                else return candidate;
            }
            candidate.Tier = 4;
            candidate.Side = 0;
            candidate.Estimated = true;
            candidate.FrontRope = candidate.BackRope = -1;
            candidate.FrontLine = candidate.BackLine = float.NaN;
            return candidate;
        }

        // Capsule centre of a station spot from its root pose.
        internal static Vec2 CenterFromRoot(HullSpec hull, Vec2 root, Vec2 forward)
        {
            forward = forward * (1f / forward.Length);
            return root + forward.RightOfForward * hull.CenterRight + forward * hull.CenterForward;
        }
    }

    // Whether a boat may tie to a cleat. One decision serves the berth input
    // (BerthResolver.MakePortInput) and the relocator's preconditions, so a
    // berth the input offers is never refused at placement.
    internal enum CleatBlock { None, SpringHeld, Claimed }

    internal static class CleatAccess
    {
        // holder: the boat on the cleat's spring (null when free); a spring
        // held by the boat itself or by a boat that moves counts as free, as
        // every moving boat is released before any is placed. claimants: the
        // boats whose authored berth uses the cleat; the boat itself does not count.
        internal static CleatBlock Check<TBody, TClaim>(TBody holder, TBody boat, ICollection<TBody> moving,
            IEnumerable<TClaim> claimants, Func<TClaim, TBody> bodyOf, out TBody heldBy, out TClaim claimedBy)
            where TBody : class where TClaim : class
        {
            heldBy = null;
            claimedBy = null;
            if (holder != null && holder != boat && (moving == null || !moving.Contains(holder)))
            {
                heldBy = holder;
                return CleatBlock.SpringHeld;
            }
            foreach (var claimant in claimants ?? new TClaim[0])
            {
                if (claimant == null || bodyOf(claimant) == boat) continue;
                claimedBy = claimant;
                return CleatBlock.Claimed;
            }
            return CleatBlock.None;
        }
    }
}
