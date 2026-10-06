using System;
using System.Collections.Generic;
using System.Globalization;

namespace ShipShuffle
{
    // Plain-number world X/Z geometry. This file has no Unity dependency so the
    // offline harness can compile it unchanged; the plugin converts at the edge.
    // Height is ignored on purpose: IslandHorizon lowers distant islands and
    // their cleats, while recovery markers and boats stay at sea level.
    internal struct Vec2
    {
        internal readonly float X;
        internal readonly float Z;

        internal Vec2(float x, float z) { X = x; Z = z; }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Z + b.Z);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Z - b.Z);
        public static Vec2 operator -(Vec2 a) => new Vec2(-a.X, -a.Z);
        public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Z * s);

        internal float Length => (float)Math.Sqrt((double)X * X + (double)Z * Z);
        internal bool IsFinite => DockGeometry.Finite(X) && DockGeometry.Finite(Z);
        // Dot(Perp(d), v) == Cross(d, v).
        internal Vec2 Perp => new Vec2(-Z, X);
        // Unity is left-handed with Y up: a yaw-only forward (x, z) has right (z, -x).
        internal Vec2 RightOfForward => new Vec2(Z, -X);

        internal static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Z * b.Z;
        internal static float Cross(Vec2 a, Vec2 b) => a.X * b.Z - a.Z * b.X;
        internal static float Distance(Vec2 a, Vec2 b) => (a - b).Length;

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:F2}, {1:F2})", X, Z);
    }

    internal struct Capsule2
    {
        internal Vec2 A;
        internal Vec2 B;
        internal float Radius;

        internal Capsule2(Vec2 a, Vec2 b, float radius) { A = a; B = b; Radius = radius; }
        internal Vec2 Center => (A + B) * 0.5f;
        internal bool IsValid => A.IsFinite && B.IsFinite && DockGeometry.Finite(Radius) && Radius > 0f;
    }

    // Hull measurements in world metres. CenterRight/CenterForward are the
    // root capsule's scaled local centre in the boat's yaw frame.
    internal sealed class HullSpec
    {
        internal float Length;
        internal float Radius;
        internal float Extra;
        internal float CenterRight;
        internal float CenterForward;
        // Mooring rope anchors at their resting positions, in world metres in
        // the boat's yaw frame (X = right, Z = forward).
        internal Vec2[] Ropes;
    }

    internal sealed class Obstacle
    {
        internal string Name;
        internal Vec2 Root;
        internal bool Measurable;
        internal Capsule2 Hull;
    }

    internal sealed class DockChain
    {
        internal bool Ok;
        internal string Reason;
        // Indices into the input cleat list, in traversal order R1 -> R2.
        internal int[] Order = new int[0];
        internal Vec2[] Points = new Vec2[0];
        internal int R1;
        internal int R2;
        // +1 or -1: the water side relative to Perp of the traversal direction.
        internal int Side;
        internal float Standoff;

        internal Vec2 Normal(Vec2 direction) => direction.Perp * Side;
    }

    internal sealed class BerthInput
    {
        internal DockChain Chain;
        internal bool[] Available;
        internal string[] Unavailable;
        internal string[] Names;
        internal Vec2 BoatPos;
        internal Vec2 BoatForward;
        internal Vec2 AlternateOffset;
        internal IList<Obstacle> Obstacles = new Obstacle[0];
        // Other piers on the island; the hull must clear them as it clears its own.
        internal IList<Vec2[]> OtherPiers;
        // A port without a recovery berth: no marker rule and no reserves.
        internal bool NoRecovery;
        // Terrain height at a world X/Z relative to sea level, or null where no
        // terrain covers the point; null delegate when the island has no terrain.
        internal Func<Vec2, float?> Ground;

        internal string Name(int chainPosition) =>
            Names != null && chainPosition >= 0 && chainPosition < Names.Length && Names[chainPosition] != null
                ? Names[chainPosition] : "chain[" + chainPosition + "]";
    }

    // Why a candidate was rejected, where a decision depends on it (tying
    // off the dock); Candidate.Reason holds the text for the log.
    internal enum Rejection
    {
        None,
        Other,
        // Too close to the recovery marker.
        RecoveryMarker,
        // Below the 2 m gap to another hull or a recovery reserve.
        HullGap,
        // Terrain under the hull within one draft of sea level.
        ShallowGround
    }

    internal sealed class Candidate
    {
        internal int I;
        internal int J;
        internal float Span = float.NaN;
        internal Vec2 Direction;
        internal Vec2 Normal;
        internal Vec2 Forward;
        // True when the bow points from chain[I] towards chain[J].
        internal bool ForwardAlongChain;
        internal Vec2 Center;
        internal Vec2 Root;
        internal Capsule2 Hull;
        internal float Push;
        internal float DockClearance = float.NaN;
        internal float RecoveryClearance = float.NaN;
        internal float MinimumGap = float.PositiveInfinity;
        internal string Blocker;
        internal float Distance = float.NaN;
        internal int FrontRope = -1;
        internal int BackRope = -1;
        internal float FrontLine = float.NaN;
        internal float BackLine = float.NaN;
        internal bool Accepted;
        internal string Reason;
        internal Rejection Rejection;
        // 1 recovery dock line, 2 vacated authored berth, 3 other pier, 4 station-kept spot.
        internal int Tier = 1;
        // Tiers 3 and 4 are estimated: dock and shore colliders are unknown until the island loads.
        internal bool Estimated;
        // Water side used for a pier pair (+1/-1), 0 when not applicable.
        internal int Side;
        // Island cleat indexes of the tied cleats (-1 for a station-kept spot).
        internal int FrontCleat = -1;
        internal int BackCleat = -1;
        // Tier 2: owner of the vacated berth. Tier 4: outward offset and along-berth shift.
        internal string SlotOwner;
        internal float Outward;
        internal float Shift;
        // Pushed this far beyond the normal standoff to clear the recovery berth
        // or another hull, still tied to the same cleats (New Beginnings' Crab Beach approach).
        internal float TiedOff;
        // Tier 3: extent of the pier it ties to (cleats and their clustered neighbours).
        internal float PierLength;
        // Open water around the berth: min(clearance to every dock segment
        // except its own, gap to other hulls and reserves), capped at 8 m.
        internal float Space = float.NaN;

        internal int FrontPosition => ForwardAlongChain ? J : I;
        internal int BackPosition => ForwardAlongChain ? I : J;
    }

    internal sealed class Evaluation
    {
        internal readonly List<Candidate> Candidates = new List<Candidate>();
        internal string Reason;
    }

    internal static class DockGeometry
    {
        internal const float MinStep = 4f;
        internal const float MaxStep = 25f;
        internal const float MaxTurnDegrees = 15f;
        internal const float MinStandoff = 1f;
        // Every level24 recovery pair lies within 17.4 m of its marker. A pair
        // far away is a mis-referenced cleat (Jungle, Swamp and Flower point at
        // Cave's cleats in the research dump), never a usable dock line.
        internal const float MaxRecoveryCleatRange = 40f;
        // Line limits now bound the span; this only rejects degenerate pairs.
        internal const float MinSpan = 4f;
        internal const float InlineTolerance = 1.5f;
        internal const float DockClearance = 0.5f;
        internal const float PushStep = 0.5f;
        internal const float MaxPush = 4f;
        internal const float BoatGap = 2f;
        internal const float ReserveRadius = 4f;
        internal const float ReserveLength = 32f;
        internal const float ReserveSideOffset = 12f;
        internal const float NativeSphereClearance = 6f;
        internal const float ObstacleRange = 150f;
        internal const float LargeExtra = 1f;

        internal const float BranchDegrees = 45f;
        // Horizontal rope-anchor-to-cleat distance. Short lines are taut and
        // snatch the hull against the dock; long ones cross the harbour.
        internal const float MinMooringLine = 2f;
        internal const float MaxMooringLine = 12f;
        // The longest line a native mooring rope reaches: sqrt of
        // PickupableBoatMooringRope.maxLength (a squared length, 900).
        internal const float NativeRopeMaxLine = 30f;
        // Tied off the dock: up to 10 m further out, on lines up to 18 m.
        internal const float TieOffStep = 1f;
        internal const float MaxTieOff = 10f;
        internal const float TiedOffMaxLine = 18f;
        // Terrain under the hull must stay this far below sea level (world Y 0
        // once the island's horizon drop is undone): max(1.5 m, 0.8 x radius),
        // sampled along the capsule axis and +/- one radius at most 3 m apart.
        internal const float MinDraft = 1.5f;
        internal const float DraftFactor = 0.8f;
        internal const float GroundSpacing = 3f;

        private static readonly float CosMaxTurn = (float)Math.Cos(MaxTurnDegrees * Math.PI / 180.0);
        private static readonly float CosBranch = (float)Math.Cos(BranchDegrees * Math.PI / 180.0);

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static string F(float value) => value.ToString("F2", CultureInfo.InvariantCulture);

        // Chains outward from both recovery cleats. Each next cleat is the
        // nearest unused one 4-25 m away within 15 degrees of the previous
        // segment, so a gently curved dock is followed but a corner is not.
        internal static DockChain BuildChain(IList<Vec2> cleats, int r1, int r2, Vec2 boatPos)
        {
            var chain = new DockChain();
            if (cleats == null || r1 < 0 || r2 < 0 || r1 >= cleats.Count || r2 >= cleats.Count || r1 == r2)
                return Fail(chain, "Recovery cleats are missing or identical.");
            for (var i = 0; i < cleats.Count; ++i)
                if (!cleats[i].IsFinite)
                    return Fail(chain, "Cleat " + i + " has a non-finite position.");
            if (!boatPos.IsFinite) return Fail(chain, "Recovery boat position is non-finite.");
            var frontRange = Vec2.Distance(cleats[r1], boatPos);
            var backRange = Vec2.Distance(cleats[r2], boatPos);
            if (!(frontRange <= MaxRecoveryCleatRange) || !(backRange <= MaxRecoveryCleatRange))
                return Fail(chain, "Recovery cleats are " + F(frontRange) + " / " + F(backRange) + " m from the recovery marker; at most " +
                    F(MaxRecoveryCleatRange) + " m is accepted.");
            var axis = cleats[r2] - cleats[r1];
            var axisLength = axis.Length;
            if (!Finite(axisLength) || axisLength < 1f)
                return Fail(chain, "Recovery cleats are less than 1 m apart.");

            var used = new bool[cleats.Count];
            used[r1] = used[r2] = true;
            var before = Extend(cleats, used, cleats[r1], axis * (-1f / axisLength));
            var after = Extend(cleats, used, cleats[r2], axis * (1f / axisLength));
            var order = new List<int>();
            for (var i = before.Count - 1; i >= 0; --i) order.Add(before[i]);
            chain.R1 = order.Count;
            order.Add(r1);
            chain.R2 = order.Count;
            order.Add(r2);
            order.AddRange(after);
            chain.Order = order.ToArray();
            chain.Points = new Vec2[order.Count];
            for (var i = 0; i < order.Count; ++i) chain.Points[i] = cleats[order[i]];

            var perpendicular = Vec2.Cross(axis, boatPos - cleats[r1]) / axisLength;
            if (!Finite(perpendicular) || Math.Abs(perpendicular) < MinStandoff)
                return Fail(chain, "Recovery boat position is " + F(Math.Abs(perpendicular)) +
                    " m from the recovery dock line; at least " + F(MinStandoff) + " m is required to know the water side.");
            chain.Side = perpendicular > 0f ? 1 : -1;
            chain.Standoff = Math.Abs(perpendicular);
            chain.Ok = true;
            return chain;
        }

        internal static List<int> Extend(IList<Vec2> cleats, bool[] used, Vec2 start, Vec2 direction)
        {
            var result = new List<int>();
            var current = start;
            var heading = direction;
            while (result.Count < cleats.Count)
            {
                var best = -1;
                var bestDistance = float.PositiveInfinity;
                var nearestAhead = float.PositiveInfinity;
                for (var i = 0; i < cleats.Count; ++i)
                {
                    if (used[i]) continue;
                    var step = cleats[i] - current;
                    var distance = step.Length;
                    if (!(distance >= MinStep && distance <= MaxStep)) continue;
                    var cosine = Vec2.Dot(step, heading) / distance;
                    if (cosine >= CosBranch && distance < nearestAhead) nearestAhead = distance;
                    if (cosine < CosMaxTurn - 1e-6f) continue;
                    if (distance < bestDistance) { best = i; bestDistance = distance; }
                }
                // A nearer cleat ahead that turns more sharply marks a corner
                // or side pier; stop rather than jump past it.
                if (best < 0 || nearestAhead < bestDistance) break;
                used[best] = true;
                result.Add(best);
                heading = (cleats[best] - current) * (1f / bestDistance);
                current = cleats[best];
            }
            return result;
        }

        private static DockChain Fail(DockChain chain, string reason)
        {
            chain.Ok = false;
            chain.Reason = reason;
            return chain;
        }

        internal static bool ValidateInput(BerthInput input, HullSpec hull, out string reason)
        {
            reason = null;
            if (input == null || input.Chain == null || !input.Chain.Ok)
                reason = "The dock chain is unavailable.";
            else if (input.Available == null || input.Available.Length != input.Chain.Points.Length)
                reason = "Cleat availability does not match the dock chain.";
            else if (!input.BoatPos.IsFinite || !input.BoatForward.IsFinite || input.BoatForward.Length < 0.5f ||
                !input.AlternateOffset.IsFinite)
                reason = "Recovery marker geometry is invalid.";
            else if (hull == null || !Finite(hull.Length) || !Finite(hull.Radius) || !Finite(hull.Extra) ||
                !Finite(hull.CenterRight) || !Finite(hull.CenterForward) || hull.Radius <= 0f ||
                hull.Length < 2f * hull.Radius || hull.Extra < 0f)
                reason = "Hull measurements are invalid.";
            return reason == null;
        }

        // Every pair in chain order, including rejected candidates.
        // BerthTiers chooses among the accepted candidates.
        internal static Evaluation EvaluateAll(BerthInput input, HullSpec hull)
        {
            var result = new Evaluation();
            if (!ValidateInput(input, hull, out var reason))
            {
                result.Reason = reason;
                return result;
            }
            var count = input.Chain.Points.Length;
            for (var i = 0; i < count; ++i)
                for (var j = i + 1; j < count; ++j)
                {
                    var candidate = EvaluatePair(input, hull, i, j);
                    result.Candidates.Add(candidate);
                }
            if (!result.Candidates.Exists(candidate => candidate.Accepted))
                result.Reason = "No cleat pair on the dock chain passed placement and clearance checks.";
            return result;
        }

        internal static Candidate EvaluatePair(BerthInput input, HullSpec hull, int i, int j)
        {
            var candidate = new Candidate { I = i, J = j };
            if (!ValidateInput(input, hull, out var invalid)) return Reject(candidate, invalid);
            var chain = input.Chain;
            var points = chain.Points;
            if (i < 0 || j >= points.Length || i >= j) return Reject(candidate, "Invalid cleat pair.");
            if (!input.Available[i])
                return Reject(candidate, input.Name(i) + " is unavailable: " + UnavailableReason(input, i));
            if (!input.Available[j])
                return Reject(candidate, input.Name(j) + " is unavailable: " + UnavailableReason(input, j));

            var first = points[i];
            var second = points[j];
            var span = Vec2.Distance(first, second);
            candidate.Span = span;
            if (!Finite(span) || span < MinSpan)
                return Reject(candidate, "Span " + F(span) + " m is below " + F(MinSpan) + " m.");
            for (var k = i + 1; k < j; ++k)
            {
                var offset = PointSegmentDistance(points[k], first, second);
                if (!(offset <= InlineTolerance))
                    return Reject(candidate, input.Name(k) + " is " + F(offset) + " m off the pair line.");
            }

            var direction = (second - first) * (1f / span);
            var normal = chain.Normal(direction);
            var boatForward = input.BoatForward * (1f / input.BoatForward.Length);
            candidate.ForwardAlongChain = Vec2.Dot(boatForward, direction) >= 0f;
            var forward = candidate.ForwardAlongChain ? direction : -direction;
            candidate.Direction = direction;
            candidate.Normal = normal;
            candidate.Forward = forward;
            if (chain.Order != null && chain.Order.Length == points.Length)
            {
                candidate.FrontCleat = chain.Order[candidate.FrontPosition];
                candidate.BackCleat = chain.Order[candidate.BackPosition];
            }
            var midpoint = (first + second) * 0.5f;
            var half = Math.Max(0f, hull.Length * 0.5f - hull.Radius);

            var push = 0f;
            while (true)
            {
                candidate.Push = push;
                candidate.Center = midpoint + normal * (chain.Standoff + hull.Extra + push);
                candidate.Hull = new Capsule2(candidate.Center + forward * half,
                    candidate.Center - forward * half, hull.Radius);
                candidate.DockClearance = ChainClearance(candidate.Hull, points);
                if (input.OtherPiers != null)
                    foreach (var pier in input.OtherPiers)
                        candidate.DockClearance = Math.Min(candidate.DockClearance, ChainClearance(candidate.Hull, pier));
                if (!Finite(candidate.DockClearance))
                    return Reject(candidate, "Dock clearance is not finite.");
                if (candidate.DockClearance >= DockClearance) break;
                push += PushStep;
                if (push > MaxPush + 1e-4f)
                    return Reject(candidate, "Hull stays " + F(candidate.DockClearance) +
                        " m from the dock line after a " + F(MaxPush) + " m outward push.");
            }

            if (!CheckClearances(candidate, input, hull, false))
                return TieOff(candidate, input, hull, normal, points[candidate.FrontPosition], points[candidate.BackPosition],
                    DockSegments(points, input.OtherPiers));
            SetRoot(candidate, input, hull);
            if (!candidate.Root.IsFinite || !Finite(candidate.Distance))
                return Reject(candidate, "Placement produced a non-finite position.");
            var ground = GroundProblem(candidate.Hull, input.Ground);
            if (ground != null)
            {
                // Shallow water at the dock: tie off further out on longer lines
                // rather than giving up the berth.
                Reject(candidate, Rejection.ShallowGround, ground);
                return TieOff(candidate, input, hull, normal, points[candidate.FrontPosition], points[candidate.BackPosition],
                    DockSegments(points, input.OtherPiers));
            }
            if (!CheckLines(candidate, hull, points[candidate.FrontPosition], points[candidate.BackPosition])) return candidate;
            candidate.Accepted = true;
            candidate.Reason = "accepted";
            return candidate;
        }

        internal static List<Vec2[]> DockSegments(Vec2[] own, IList<Vec2[]> others)
        {
            var result = new List<Vec2[]>();
            if (own != null && own.Length > 0) result.Add(own);
            if (others != null) result.AddRange(others);
            return result;
        }

        // The recovery marker, a recovery reserve, another hull or shallow ground
        // block a berth that a few metres more water would clear.
        internal static bool TieOffEligible(Rejection rejection) =>
            (rejection == Rejection.RecoveryMarker || rejection == Rejection.HullGap || rejection == Rejection.ShallowGround);

        // Retry a rejected pair further out along its normal, 1 m at a time up
        // to 10 m, tied to the same cleats on lines up to 18 m.
        internal static Candidate TieOff(Candidate candidate, BerthInput input, HullSpec hull, Vec2 normal, Vec2 frontCleat,
            Vec2 backCleat, IList<Vec2[]> dockSegments)
        {
            if (!TieOffEligible(candidate.Rejection)) return candidate;
            var first = candidate.Reason;
            var cause = candidate.Rejection;
            string terrain = null;
            var baseCenter = candidate.Hull.Center;
            var half = Math.Max(0f, hull.Length * 0.5f - hull.Radius);
            for (var extra = TieOffStep; extra <= MaxTieOff + 1e-4f; extra += TieOffStep)
            {
                var center = baseCenter + normal * extra;
                candidate.Hull = new Capsule2(center + candidate.Forward * half, center - candidate.Forward * half, hull.Radius);
                candidate.MinimumGap = float.PositiveInfinity;
                candidate.Blocker = null;
                candidate.Reason = null;
                candidate.Rejection = Rejection.None;
                var dock = float.PositiveInfinity;
                if (dockSegments != null)
                    foreach (var segment in dockSegments) dock = Math.Min(dock, ChainClearance(candidate.Hull, segment));
                candidate.DockClearance = dock;
                if (!(dock >= DockClearance)) return Reject(candidate, cause, first + " Tied off the dock: hull meets the dock at +" + F(extra) + " m.");
                if (!CheckClearances(candidate, input, hull, false))
                {
                    if (TieOffEligible(candidate.Rejection)) continue;
                    return candidate;
                }
                SetRoot(candidate, input, hull);
                if (!candidate.Root.IsFinite) return Reject(candidate, "Placement produced a non-finite position.");
                // Shallow ground here may still give way to deeper water further out.
                terrain = GroundProblem(candidate.Hull, input.Ground);
                if (terrain != null) continue;
                if (!CheckLines(candidate, hull, frontCleat, backCleat, TiedOffMaxLine)) return candidate;
                candidate.TiedOff = extra;
                candidate.Accepted = true;
                candidate.Reason = "accepted, tied off the dock (+" + F(extra) + " m)";
                return candidate;
            }
            return Reject(candidate, cause, first + " Tied off the dock: still blocked at +" + F(MaxTieOff) + " m" +
                (terrain != null ? " (" + terrain + ")" : "") + ".");
        }

        // Recovery marker, both recovery reserves (unless waived) and every
        // staying boat within range. candidate.Hull must already be set.
        internal static bool CheckClearances(Candidate candidate, BerthInput input, HullSpec hull, bool waiveRecovery)
        {
            var boatForward = input.BoatForward * (1f / input.BoatForward.Length);
            candidate.RecoveryClearance = PointSegmentDistance(input.BoatPos, candidate.Hull.A, candidate.Hull.B) - hull.Radius;
            if (!waiveRecovery && !input.NoRecovery)
            {
                if (!(candidate.RecoveryClearance >= NativeSphereClearance))
                {
                    Reject(candidate, Rejection.RecoveryMarker, "Hull is " + F(candidate.RecoveryClearance) +
                        " m from the recovery marker; at least " + F(NativeSphereClearance) + " m is required.");
                    return false;
                }
                var reserveForward = boatForward * (ReserveLength * 0.5f - ReserveRadius);
                Gap(candidate, new Capsule2(input.BoatPos + reserveForward, input.BoatPos - reserveForward, ReserveRadius),
                    "recovery berth reserve");
                var alternate = input.BoatPos + input.AlternateOffset;
                Gap(candidate, new Capsule2(alternate + reserveForward, alternate - reserveForward, ReserveRadius),
                    "obstructed recovery berth reserve");
            }
            if (input.Obstacles != null)
                foreach (var obstacle in input.Obstacles)
                {
                    if (obstacle == null) continue;
                    var range = Vec2.Distance(obstacle.Root, candidate.Hull.Center);
                    if (Finite(range) && range > ObstacleRange) continue;
                    if (!obstacle.Measurable || !obstacle.Hull.IsValid)
                    {
                        Reject(candidate, "Nearby boat " + obstacle.Name + " has no single measurable root hull.");
                        return false;
                    }
                    Gap(candidate, obstacle.Hull, obstacle.Name);
                }
            if (candidate.Blocker == null) return true;
            if (!Finite(candidate.MinimumGap))
            {
                Reject(candidate, "Hull clearance could not be measured.");
                return false;
            }
            if (candidate.MinimumGap < BoatGap)
            {
                Reject(candidate, Rejection.HullGap, "Hull gap " + F(candidate.MinimumGap) + " m to " + candidate.Blocker +
                    " is below " + F(BoatGap) + " m.");
                return false;
            }
            return true;
        }

        internal static void SetRoot(Candidate candidate, BerthInput input, HullSpec hull)
        {
            var center = candidate.Hull.Center;
            candidate.Center = center;
            candidate.Root = center - (candidate.Forward.RightOfForward * hull.CenterRight + candidate.Forward * hull.CenterForward);
            candidate.Distance = Vec2.Distance(center, input.BoatPos);
        }

        // Predict the two lines the relocator will actually tie.
        internal static bool CheckLines(Candidate candidate, HullSpec hull, Vec2 frontCleat, Vec2 backCleat, float maxLine = MaxMooringLine)
        {
            if (!AssignRopes(candidate, hull, frontCleat, backCleat))
            {
                Reject(candidate, "The boat has no distinct mooring ropes at opposite ends.");
                return false;
            }
            var shortest = Math.Min(candidate.FrontLine, candidate.BackLine);
            var longest = Math.Max(candidate.FrontLine, candidate.BackLine);
            if (!Finite(shortest) || !Finite(longest))
            {
                Reject(candidate, "Mooring line lengths are not finite.");
                return false;
            }
            if (longest > maxLine)
            {
                Reject(candidate, "Mooring lines " + F(candidate.FrontLine) + " / " + F(candidate.BackLine) +
                    " m; at most " + F(maxLine) + " m is allowed.");
                return false;
            }
            if (shortest < MinMooringLine)
            {
                Reject(candidate, "Mooring lines " + F(candidate.FrontLine) + " / " + F(candidate.BackLine) +
                    " m; at least " + F(MinMooringLine) + " m is required.");
                return false;
            }
            return true;
        }

        // The two ropes the relocator ties for the candidate's root pose and
        // these cleats, with their line lengths. No length limits.
        internal static bool AssignRopes(Candidate candidate, HullSpec hull, Vec2 frontCleat, Vec2 backCleat)
        {
            var frontLocal = ToLocal(frontCleat, candidate.Root, candidate.Forward);
            var backLocal = ToLocal(backCleat, candidate.Root, candidate.Forward);
            if (!ChooseRopes(hull.Ropes, frontLocal, backLocal, out candidate.FrontRope, out candidate.BackRope, out _)) return false;
            candidate.FrontLine = Vec2.Distance(hull.Ropes[candidate.FrontRope], frontLocal);
            candidate.BackLine = Vec2.Distance(hull.Ropes[candidate.BackRope], backLocal);
            return true;
        }

        internal static float Draft(float radius) => Math.Max(MinDraft, DraftFactor * radius);

        // Points under a hull footprint: the capsule axis at most 3 m apart,
        // each also offset +/- one radius sideways, plus both cap tips.
        internal static List<Vec2> HullSamples(Capsule2 hull)
        {
            var samples = new List<Vec2>();
            var axis = hull.B - hull.A;
            var length = axis.Length;
            var along = length > 1e-3f ? axis * (1f / length) : new Vec2(0f, 1f);
            var side = along.Perp * hull.Radius;
            var steps = Math.Max(1, (int)Math.Ceiling(length / GroundSpacing));
            for (var k = 0; k <= steps; ++k)
            {
                var point = hull.A + axis * ((float)k / steps);
                samples.Add(point);
                samples.Add(point + side);
                samples.Add(point - side);
            }
            samples.Add(hull.A - along * hull.Radius);
            samples.Add(hull.B + along * hull.Radius);
            return samples;
        }

        // Null when the ground is deep enough everywhere under the hull (or
        // no terrain is known); otherwise the rejection reason.
        internal static string GroundProblem(Capsule2 hull, Func<Vec2, float?> ground)
        {
            if (ground == null) return null;
            var draft = Draft(hull.Radius);
            var highest = float.NegativeInfinity;
            var at = default(Vec2);
            foreach (var point in HullSamples(hull))
            {
                var height = ground(point);
                if (height.HasValue && Finite(height.Value) && height.Value > highest)
                {
                    highest = height.Value;
                    at = point;
                }
            }
            return highest > -draft
                ? "Terrain under the hull reaches " + F(highest) + " m against sea level at " + at + "; the hull needs " + F(draft) +
                    " m of water."
                : null;
        }

        internal static Vec2 ToLocal(Vec2 world, Vec2 root, Vec2 forward) =>
            new Vec2(Vec2.Dot(world - root, forward.RightOfForward), Vec2.Dot(world - root, forward));

        // New Beginnings' rope choice in the boat's yaw frame. Native
        // MoorClosestRope treats each cleat independently, so on a long hull
        // both nearest ropes can belong to one end. Pick one rope from each
        // longitudinal end, preferring a same-side pair facing the dock.
        internal static bool ChooseRopes(Vec2[] ropes, Vec2 frontCleat, Vec2 backCleat,
            out int front, out int back, out int priority)
        {
            front = back = -1;
            priority = int.MaxValue;
            if (ropes == null || ropes.Length < 2 || !frontCleat.IsFinite || !backCleat.IsFinite) return false;
            foreach (var rope in ropes) if (!rope.IsFinite) return false;
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
            foreach (var rope in ropes)
            {
                minX = Math.Min(minX, rope.X); maxX = Math.Max(maxX, rope.X);
                minZ = Math.Min(minZ, rope.Z); maxZ = Math.Max(maxZ, rope.Z);
            }
            var useX = maxX - minX > maxZ - minZ;
            Func<Vec2, float> along = p => useX ? p.X : p.Z;
            Func<Vec2, float> across = p => useX ? p.Z : p.X;
            var minimum = useX ? minX : minZ;
            var maximum = useX ? maxX : maxZ;
            var lateralCenter = useX ? (minZ + maxZ) * 0.5f : (minX + maxX) * 0.5f;
            var sideTolerance = Math.Max(0.1f, ((useX ? maxZ - minZ : maxX - minX)) * 0.1f);
            var dockSide = across((frontCleat + backCleat) * 0.5f) - lateralCenter;
            var endTolerance = Math.Max(0.15f, (maximum - minimum) * 0.12f);
            var bestDistance = float.PositiveInfinity;
            for (var low = 0; low < ropes.Length; ++low)
            {
                if (along(ropes[low]) > minimum + endTolerance) continue;
                for (var high = 0; high < ropes.Length; ++high)
                {
                    if (high == low || along(ropes[high]) < maximum - endTolerance) continue;
                    var lowSide = across(ropes[low]) - lateralCenter;
                    var highSide = across(ropes[high]) - lateralCenter;
                    var crossesHull = lowSide > sideTolerance && highSide < -sideTolerance ||
                        lowSide < -sideTolerance && highSide > sideTolerance;
                    var facesDock = Math.Abs(dockSide) > sideTolerance &&
                        (lowSide + highSide) * dockSide > sideTolerance * sideTolerance;
                    var rank = crossesHull ? 2 : facesDock ? 0 : 1;
                    var lowFront = SquaredDistance(ropes[low], frontCleat) + SquaredDistance(ropes[high], backCleat);
                    if (rank < priority || rank == priority && lowFront < bestDistance)
                    {
                        priority = rank; bestDistance = lowFront; front = low; back = high;
                    }
                    var highFront = SquaredDistance(ropes[high], frontCleat) + SquaredDistance(ropes[low], backCleat);
                    if (rank < priority || rank == priority && highFront < bestDistance)
                    {
                        priority = rank; bestDistance = highFront; front = high; back = low;
                    }
                }
            }
            return front >= 0 && back >= 0 && front != back;
        }

        private static float SquaredDistance(Vec2 a, Vec2 b)
        {
            var d = a - b;
            return d.X * d.X + d.Z * d.Z;
        }

        private static string UnavailableReason(BerthInput input, int position) =>
            input.Unavailable != null && position < input.Unavailable.Length && input.Unavailable[position] != null
                ? input.Unavailable[position] : "claimed";

        private static void Gap(Candidate candidate, Capsule2 other, string name)
        {
            var gap = CapsuleGap(candidate.Hull, other);
            if (float.IsNaN(gap)) gap = float.NegativeInfinity;
            if (gap < candidate.MinimumGap || candidate.Blocker == null && gap == candidate.MinimumGap)
            {
                candidate.MinimumGap = gap;
                candidate.Blocker = name;
            }
        }

        internal static Candidate Reject(Candidate candidate, string reason) => Reject(candidate, Rejection.Other, reason);

        internal static Candidate Reject(Candidate candidate, Rejection rejection, string reason)
        {
            candidate.Accepted = false;
            candidate.Reason = reason;
            candidate.Rejection = rejection;
            return candidate;
        }

        internal static float ChainClearance(Capsule2 hull, Vec2[] points)
        {
            var clearance = float.PositiveInfinity;
            for (var k = 1; k < points.Length; ++k)
                clearance = Math.Min(clearance, SegmentDistance(hull.A, hull.B, points[k - 1], points[k]) - hull.Radius);
            return clearance;
        }

        internal static float CapsuleGap(Capsule2 a, Capsule2 b) =>
            SegmentDistance(a.A, a.B, b.A, b.B) - a.Radius - b.Radius;

        internal static float PointSegmentDistance(Vec2 point, Vec2 a, Vec2 b) => SegmentDistance(a, b, point, point);

        // Closest distance between segments PQ and RS, computed in double.
        internal static float SegmentDistance(Vec2 p, Vec2 q, Vec2 r, Vec2 s)
        {
            double d1x = (double)q.X - p.X, d1z = (double)q.Z - p.Z;
            double d2x = (double)s.X - r.X, d2z = (double)s.Z - r.Z;
            double vx = (double)p.X - r.X, vz = (double)p.Z - r.Z;
            double a = d1x * d1x + d1z * d1z, e = d2x * d2x + d2z * d2z, f = d2x * vx + d2z * vz;
            double first, second;
            if (a <= 1e-10 && e <= 1e-10) return (float)Math.Sqrt(vx * vx + vz * vz);
            if (a <= 1e-10) { first = 0; second = Clamp(f / e); }
            else
            {
                var c = d1x * vx + d1z * vz;
                if (e <= 1e-10) { second = 0; first = Clamp(-c / a); }
                else
                {
                    var b = d1x * d2x + d1z * d2z;
                    var denominator = a * e - b * b;
                    first = denominator > 1e-10 ? Clamp((b * f - c * e) / denominator) : 0;
                    second = (b * first + f) / e;
                    if (second < 0) { second = 0; first = Clamp(-c / a); }
                    else if (second > 1) { second = 1; first = Clamp((b - c) / a); }
                }
            }
            var dx = vx + d1x * first - d2x * second;
            var dz = vz + d1z * first - d2z * second;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        private static double Clamp(double value) => Math.Max(0, Math.Min(1, value));
    }

    // Mooring-line slack for placed sale boats (pure rules; MooringSlack
    // applies them). Vanilla PickupableBoatMooringRope.Update sets a moored
    // rope's length to the current distance whenever the hull turns from
    // kinematic to dynamic, so placed lines have no slack. At the first such
    // reset after placement each planned rope is set once to the longer of
    // its planned line and its current distance, + 3 m (4 m for Large
    // hulls), capped at sqrt(maxLength + 25) (vanilla ChangeRopeLength's
    // release limit; maxLength is a squared length, 900). Later resets are
    // vanilla's, as for any sale boat.
    internal static class RopeSlack
    {
        internal const float Normal = 3f;
        internal const float Large = 4f;
        internal const float ReleaseAllowanceSquared = 25f;

        internal static float For(bool large) => large ? Large : Normal;

        // Squared rope length to set, or NaN when the input is unusable or the
        // line is already at or beyond the cap. Never shorter than the distance.
        internal static float LengthSquared(float distanceSquared, float maxLengthSquared, float slack)
        {
            if (!DockGeometry.Finite(distanceSquared) || distanceSquared < 0f || !DockGeometry.Finite(maxLengthSquared) ||
                maxLengthSquared <= 0f || !DockGeometry.Finite(slack) || slack < 0f) return float.NaN;
            var cap = maxLengthSquared + ReleaseAllowanceSquared;
            if (distanceSquared >= cap) return float.NaN;
            var length = Math.Min(Math.Sqrt(distanceSquared) + slack, Math.Sqrt(cap));
            return (float)(length * length);
        }

        // The line's squared length: the longer of the planned line (metres,
        // from the berth plan; horizontal, so a high cleat makes the real rope
        // longer) and the current distance, + slack. NaN when nothing usable
        // can be set.
        internal static float FixedLengthSquared(float plannedLine, float distanceSquared, float maxLengthSquared, float slack)
        {
            var planned = DockGeometry.Finite(plannedLine) && plannedLine > 0f ? plannedLine * plannedLine : float.NaN;
            var basis = float.IsNaN(planned) ? distanceSquared
                : DockGeometry.Finite(distanceSquared) && distanceSquared > planned ? distanceSquared : planned;
            return LengthSquared(basis, maxLengthSquared, slack);
        }
    }

    // One planned rope, set once. It waits until the hull is dynamic while
    // playing and not loading and the rope's own Update has handled the
    // kinematic-to-dynamic edge; then it yields its length (nothing when the
    // rope is no longer moored to its planned spring) and is done.
    internal sealed class SlackLine
    {
        // Planned line length from the berth plan (metres); NaN when unknown.
        internal float PlannedLine = float.NaN;
        internal bool Done;
        // Done without a length: no longer moored to its planned spring.
        internal bool Skipped;

        // nativeReset: the rope's wasKinematic flag is clear. Returns the
        // squared length to set, or NaN.
        internal float Step(bool dynamic, bool playing, bool loading, bool stillMoored, bool nativeReset,
            float distanceSquared, float maxLengthSquared, float slack)
        {
            if (Done || !dynamic || !playing || loading) return float.NaN;
            if (!stillMoored)
            {
                Done = Skipped = true;
                return float.NaN;
            }
            if (!nativeReset) return float.NaN;
            Done = true;
            return RopeSlack.FixedLengthSquared(PlannedLine, distanceSquared, maxLengthSquared, slack);
        }
    }
}
