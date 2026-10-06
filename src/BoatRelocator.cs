using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ShipShuffle
{
    internal sealed class ProbeVelocityGuard
    {
        private readonly Component probes;
        private readonly FieldInfo field;
        private readonly bool previous;
        private bool restored;

        internal static bool CanGuard(SaveableObject boat)
        {
            var component = boat != null ? boat.GetComponent("BoatProbes") : null;
            var velocityField = component != null ? AccessTools.Field(component.GetType(), "dontUpdateVelocity") : null;
            return velocityField != null && velocityField.FieldType == typeof(bool);
        }

        // Native BoatProbes filters water-relative drag velocity while this
        // flag is set. Preserve its existing value during the move.
        internal ProbeVelocityGuard(SaveableObject boat)
        {
            probes = boat.GetComponent("BoatProbes");
            field = probes != null ? AccessTools.Field(probes.GetType(), "dontUpdateVelocity") : null;
            if (field == null || field.FieldType != typeof(bool))
                throw new InvalidOperationException("Boat has no compatible BoatProbes velocity guard.");
            previous = (bool)field.GetValue(probes);
            field.SetValue(probes, true);
        }

        internal void Restore()
        {
            if (restored) return;
            try
            {
                if (probes != null) field.SetValue(probes, previous);
                restored = true;
            }
            catch (Exception exception)
            {
                Plugin.Instance?.Error("Could not restore a boat's velocity guard.", exception);
            }
        }
    }


    // Moves a whole fleet. Boats trade places in chains and cycles, so every
    // moving boat is released from its native berth before any is placed.
    // Each boat keeps its own snapshot (following New Beginnings'
    // FixedStart.ApplyRelocated); a rollback mirrors placement for every
    // affected boat at once (FleetRollback). A boat that stays home then
    // drops every move that depended on its port or cleats.
    internal static class BoatRelocator
    {
        private sealed class RopePosition
        {
            public PickupableBoatMooringRope Rope;
            public Transform Parent;
            public Vector3 Position;
            public Quaternion Rotation;
            public GPButtonDockMooring Mooring;
            public bool RestoreBlocked;
        }

        private sealed class Entry
        {
            public FleetMove Move;
            public BerthPlan Plan;
            public Vector3 OldPosition;
            public Quaternion OldRotation;
            public Vector3 OldBodyPosition;
            public Quaternion OldBodyRotation;
            public Vector3 OldVelocity;
            public Vector3 OldAngularVelocity;
            public Anchor Anchor;
            public Rigidbody AnchorBody;
            public Vector3 OldAnchorPosition;
            public float OldAnchorLength;
            public RopePosition[] Ropes;
            public ProbeVelocityGuard Guard;
            public bool Released;
            public bool AnchorReset;
            public bool Moved;
            public bool Placed;
            // Back at its native berth with every original line re-moored.
            public bool Restored;
        }

        private static readonly List<ProbeVelocityGuard> ActiveGuards = new List<ProbeVelocityGuard>();

        // moves: FleetMove.Tag is the BerthPlan. stayingHomes: the home of each
        // staying boat without a move (see FleetPlanner.Cascade); capacity:
        // places per port key (null: 1). Optional Large ledgers constrain fresh
        // shared-port arrivals after any failed departure. Returns the plans that were placed
        // and kept; every other boat is back at its native berth.
        internal static List<BerthPlan> ApplyFleet(IList<FleetMove> moves, ICollection<string> stayingHomes,
            ICollection<string> stayingClaims, Occupancy occupancy, string context, Func<string, int> capacity = null,
            ICollection<string> stayingLargeHomes = null, Func<string, int> largeCapacity = null)
        {
            var entries = new List<Entry>();
            foreach (var move in moves)
            {
                if (move.Dropped) continue;
                var plan = move.Tag as BerthPlan;
                if (!TryCheckPreconditions(plan, occupancy, out var reason) || !TryCapture(plan, out var entry, out reason))
                {
                    Drop(move, context, "precondition failed: " + reason);
                    continue;
                }
                entry.Move = move;
                entries.Add(entry);
            }
            Settle(moves, entries, stayingHomes, stayingClaims, context, capacity, stayingLargeHomes, largeCapacity);

            foreach (var entry in entries)
            {
                if (entry.Move.Dropped) continue;
                try
                {
                    entry.Guard = new ProbeVelocityGuard(entry.Plan.Boat.Saveable);
                    entry.Released = true;
                    StationKeeper.Remove(entry.Plan.Boat.Boat.gameObject);
                    MooringRelease.ReleaseAll(entry.Plan.Boat.Ropes.ropes, entry.Plan.Boat.Body);
                    entry.AnchorReset = true;
                    entry.Plan.Boat.Ropes.GetAnchorController().ResetAnchor();
                }
                catch (Exception exception)
                {
                    Drop(entry.Move, context, "release failed: " + exception.Message);
                    Plugin.Instance?.Error(context + ": releasing " + entry.Plan.Boat.Label + " failed.", exception);
                    Settle(moves, entries, stayingHomes, stayingClaims, context, capacity, stayingLargeHomes, largeCapacity);
                }
            }

            foreach (var entry in entries)
            {
                if (entry.Move.Dropped) continue;
                try
                {
                    Place(entry, context);
                }
                catch (Exception exception)
                {
                    Drop(entry.Move, context, "placement failed: " + exception.Message);
                    Plugin.Instance?.Error(context + ": placing " + entry.Plan.Boat.Label + " failed.", exception);
                    Settle(moves, entries, stayingHomes, stayingClaims, context, capacity, stayingLargeHomes, largeCapacity);
                }
            }

            var placed = new List<BerthPlan>();
            foreach (var entry in entries)
            {
                if (entry.Move.Dropped || !entry.Placed) continue;
                try
                {
                    Plugin.Instance.Run(ReleaseGuard(entry.Guard, entry.Plan, context));
                }
                catch (Exception exception)
                {
                    entry.Guard?.Restore();
                    Plugin.Instance?.Warn(context + ": velocity guard of " + entry.Plan.Boat.Label +
                        " was restored early: " + exception.Message);
                }
                placed.Add(entry.Plan);
            }
            return placed;
        }

        private static void Drop(FleetMove move, string context, string reason)
        {
            move.Dropped = true;
            move.Reason = reason;
            Plugin.Instance?.Warn(context + ": " + move.Boat + " stays at its native berth (" + reason + ").");
        }

        // Cascade the stay-home consequences, then return every dropped boat
        // that was already touched to its native berth, all together: release
        // them all, restore every pose, then re-moor the original lines, so a
        // cleat one of them still held (a swap, or a rope a failed placement
        // already tied) is free again when its owner returns.
        private static void Settle(IList<FleetMove> moves, List<Entry> entries, ICollection<string> stayingHomes,
            ICollection<string> stayingClaims, string context, Func<string, int> capacity,
            ICollection<string> stayingLargeHomes, Func<string, int> largeCapacity)
        {
            foreach (var move in FleetPlanner.Cascade(moves, stayingHomes, stayingClaims, capacity, stayingLargeHomes, largeCapacity))
                Plugin.Instance?.Warn(context + ": " + move.Boat + " stays at its native berth (" + move.Reason + ").");
            var affected = entries.Where(item => item.Move.Dropped && item.Released && !item.Restored).ToList();
            if (affected.Count == 0) return;
            try
            {
                var restored = FleetRollback.Run(affected, entry => Unmoor(entry, context), RestorePose, entry => Remoor(entry, context),
                    (entry, exception) => Plugin.Instance?.Error(context + ": " + entry.Plan.Boat.Label + " could not be fully restored.", exception));
                foreach (var entry in restored)
                {
                    entry.Restored = true;
                    Plugin.Instance?.DebugLog(context + ": " + entry.Plan.Boat.Label + " returned to its previous berth.");
                }
            }
            finally
            {
                // Keep the probe from interpreting the rollback as a voyage.
                foreach (var entry in affected) entry.Guard?.Restore();
            }
        }

        private static void Place(Entry entry, string context)
        {
            var plan = entry.Plan;
            var boat = plan.Boat;
            var transform = boat.Saveable.transform;
            var body = boat.Body;
            entry.Moved = true;
            transform.SetPositionAndRotation(plan.Position, plan.Rotation);
            body.position = plan.Position;
            body.rotation = plan.Rotation;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            if (entry.AnchorBody != null)
            {
                entry.AnchorBody.position = entry.OldAnchorPosition + (plan.Position - entry.OldPosition);
                entry.AnchorBody.velocity = Vector3.zero;
                entry.AnchorBody.angularVelocity = Vector3.zero;
            }
            if (!HullFootprint.Finite(transform.position) || !HullFootprint.Finite(body.position) ||
                entry.AnchorBody != null && !HullFootprint.Finite(entry.AnchorBody.position))
                throw new InvalidOperationException("Boat or anchor gained a non-finite position during placement.");
            Physics.SyncTransforms();
            if (plan.Held)
            {
                MooringSlack.Remove(boat.Boat.gameObject);
                StationKeeper.Attach(boat, plan.Site.Island, plan.LocalPosition, plan.LocalYaw);
                Plugin.Instance?.DebugLog(context + ": moving " + boat.Label + " from " + entry.OldPosition.ToString("F2") +
                    " to station-kept spot " + plan.Position.ToString("F2") + " yaw " + plan.Rotation.eulerAngles.y.ToString("F1") + ".");
                entry.Placed = true;
                return;
            }

            // The evaluation measured these exact ropes' line lengths.
            var frontRope = RopeAt(boat, plan.Candidate.FrontRope);
            var backRope = RopeAt(boat, plan.Candidate.BackRope);
            if (frontRope == backRope) throw new InvalidOperationException("Front and back mooring ropes are the same rope.");
            Plugin.Instance?.DebugLog(context + ": moving " + boat.Label + " from " + entry.OldPosition.ToString("F2") +
                " to " + plan.Position.ToString("F2") + " yaw " + plan.Rotation.eulerAngles.y.ToString("F1") + "; tying " +
                frontRope.name + " to " + plan.FrontId + " (" + DockGeometry.F(plan.Candidate.FrontLine) + " m) and " +
                backRope.name + " to " + plan.BackId + " (" + DockGeometry.F(plan.Candidate.BackLine) + " m).");
            MoorAt(frontRope, plan.Front, body);
            MoorAt(backRope, plan.Back, body);
            // Moored boats are only placed; the game's physics then holds them
            // on lines with some slack (no keeper).
            MooringSlack.Attach(boat, frontRope, plan.Front.spring, plan.Candidate.FrontLine, backRope, plan.Back.spring, plan.Candidate.BackLine);
            entry.Placed = true;
        }

        // Rollback, first step: off every line, with no hold or slack left.
        private static void Unmoor(Entry entry, string context)
        {
            var boat = entry.Plan.Boat;
            StationKeeper.Remove(boat.Boat.gameObject);
            MooringSlack.Remove(boat.Boat.gameObject);
            foreach (var rope in entry.Ropes)
            {
                try
                {
                    if (!MooringRelease.TryRelease(rope.Rope, boat.Body, out var reason))
                    {
                        rope.RestoreBlocked = true;
                        Plugin.Instance?.Warn(context + ": a rope of " + boat.Label + " was left untouched: " + reason + ".");
                    }
                }
                catch (Exception exception)
                {
                    rope.RestoreBlocked = true;
                    Plugin.Instance?.Warn(context + ": a rope of " + boat.Label + " could not be released: " + exception.Message);
                }
            }
        }

        // Rollback, second step: the captured pose and anchor.
        private static void RestorePose(Entry entry)
        {
            var boat = entry.Plan.Boat;
            if (entry.Moved)
            {
                boat.Saveable.transform.SetPositionAndRotation(entry.OldPosition, entry.OldRotation);
                boat.Body.position = entry.OldBodyPosition;
                boat.Body.rotation = entry.OldBodyRotation;
                boat.Body.velocity = entry.OldVelocity;
                boat.Body.angularVelocity = entry.OldAngularVelocity;
                if (entry.AnchorBody != null) entry.AnchorBody.position = entry.OldAnchorPosition;
            }
            if (entry.AnchorReset && entry.Anchor != null) entry.Anchor.OnLoad(false, entry.OldAnchorLength);
            if (entry.Moved) Physics.SyncTransforms();
        }

        // Rollback, last step: the original lines. False when one could not
        // return (logged as an error); the boat then counts as not restored.
        private static bool Remoor(Entry entry, string context)
        {
            var boat = entry.Plan.Boat;
            var complete = true;
            foreach (var rope in entry.Ropes)
            {
                if (rope.RestoreBlocked)
                {
                    complete = false;
                    continue;
                }
                try
                {
                    if (rope.Mooring == null)
                    {
                        rope.Rope.transform.SetParent(rope.Parent, true);
                        rope.Rope.transform.SetPositionAndRotation(rope.Position, rope.Rotation);
                    }
                    else if (rope.Mooring.spring != null && rope.Mooring.spring.connectedBody == null)
                        rope.Rope.MoorTo(rope.Mooring);
                    else
                    {
                        complete = false;
                        Plugin.Instance?.Error(context + ": rope " + rope.Rope.name + " of " + boat.Label + " could not return to " +
                            rope.Mooring.name + " because its spring is held by " +
                            BodyName(rope.Mooring.spring != null ? rope.Mooring.spring.connectedBody : null) + "; left at rest.", null);
                    }
                }
                catch (Exception exception)
                {
                    complete = false;
                    Plugin.Instance?.Error(context + ": rope " + rope.Rope.name + " of " + boat.Label + " could not be restored.", exception);
                }
            }
            return complete;
        }

        private static bool TryCapture(BerthPlan plan, out Entry entry, out string reason)
        {
            entry = null;
            reason = null;
            var boat = plan.Boat;
            var transform = boat.Saveable.transform;
            var anchor = boat.Ropes.anchor;
            var anchorBody = anchor != null ? anchor.GetComponent<Rigidbody>() : null;
            try
            {
                entry = new Entry
                {
                    Plan = plan,
                    OldPosition = transform.position, OldRotation = transform.rotation,
                    OldBodyPosition = boat.Body.position, OldBodyRotation = boat.Body.rotation,
                    OldVelocity = boat.Body.velocity, OldAngularVelocity = boat.Body.angularVelocity,
                    Anchor = anchor, AnchorBody = anchorBody,
                    OldAnchorPosition = anchorBody != null ? anchorBody.position : Vector3.zero,
                    OldAnchorLength = anchor != null ? anchor.GetRopeLength() : 0f,
                    Ropes = boat.Ropes.ropes.Select(rope => new RopePosition
                    {
                        Rope = rope, Parent = rope.transform.parent,
                        Position = rope.transform.position, Rotation = rope.transform.rotation,
                        Mooring = GetOriginalMooring(rope)
                    }).ToArray()
                };
            }
            catch (Exception exception)
            {
                reason = "state could not be recorded: " + exception.Message;
                return false;
            }
            if (!HullFootprint.Finite(entry.OldPosition) || !HullFootprint.Finite(entry.OldRotation) ||
                anchorBody != null && !HullFootprint.Finite(entry.OldAnchorPosition) || !DockGeometry.Finite(entry.OldAnchorLength))
            {
                reason = "the boat or its anchor has a non-finite starting state";
                return false;
            }
            return true;
        }

        internal static bool TryCheckPreconditions(BerthPlan plan, Occupancy occupancy, out string reason)
        {
            reason = null;
            var boat = plan?.Boat;
            if (boat == null || boat.Boat == null || boat.Saveable == null || boat.Body == null || boat.Ropes == null ||
                !boat.Saveable.gameObject.scene.isLoaded || plan.Candidate == null ||
                !plan.Held && (plan.Front == null || plan.Back == null || plan.Front == plan.Back))
                reason = "boat or target cleats are no longer available";
            else if (boat.Purchased)
                reason = "boat is purchased; Ship Shuffle never moves owned boats";
            else if (boat.Ropes.anchor != null && boat.Ropes.anchor.IsSet())
                reason = "boat is anchored; a failed move could not restore that state";
            else if (boat.Ropes.GetAnchorController() == null || boat.Ropes.ropes == null || boat.Ropes.ropes.Length < 2 ||
                boat.Ropes.ropes.Any(rope => rope == null) || !ProbeVelocityGuard.CanGuard(boat.Saveable))
                reason = "boat lacks its anchor controller, mooring ropes or velocity guard";
            else if (!MooringRelease.CanRelease(boat.Ropes.ropes, boat.Body, out var releaseProblem))
                reason = releaseProblem;
            else if (plan.Held)
            {
                if (!HullFootprint.Finite(plan.Position) || !HullFootprint.Finite(plan.Rotation) || plan.Site?.Port?.Island == null)
                    reason = "station-kept target is invalid";
            }
            else if (plan.Front.spring == null || plan.Back.spring == null ||
                plan.Front.GetComponent<SpringJoint>() != plan.Front.spring || plan.Back.GetComponent<SpringJoint>() != plan.Back.spring)
                reason = "a target cleat has no initialized spring";
            else if (!HullFootprint.Finite(plan.Position) || !HullFootprint.Finite(plan.Rotation))
                reason = "target placement is non-finite";
            else if (!plan.Restored)
            {
                // The same cleat decision as the berth input. A restored plan
                // chose its cleats from their physical state when it was made.
                var problem = occupancy.CleatProblem(plan.Front, boat.Body, out _);
                var cleat = plan.FrontId;
                if (problem == null)
                {
                    problem = occupancy.CleatProblem(plan.Back, boat.Body, out _);
                    cleat = plan.BackId;
                }
                if (problem != null) reason = "target cleat " + cleat + " is unavailable: " + problem;
            }
            return reason == null;
        }

        private static IEnumerator ReleaseGuard(ProbeVelocityGuard guard, BerthPlan plan, string context)
        {
            ActiveGuards.Add(guard);
            try
            {
                yield return new WaitForFixedUpdate();
                yield return new WaitForFixedUpdate();
                yield return new WaitForEndOfFrame();
            }
            finally
            {
                guard.Restore();
                ActiveGuards.Remove(guard);
            }
            try
            {
                var boat = plan.Boat;
                if (boat.Body == null) yield break;
                if (plan.Held)
                {
                    Plugin.Instance?.DebugLog(context + ": " + boat.Label + " settled at station-kept spot " +
                        boat.Saveable.transform.position.ToString("F2") + "; kinematic " + boat.Body.isKinematic + ".");
                    yield break;
                }
                if (plan.Front == null || plan.Back == null) yield break;
                Plugin.Instance?.DebugLog(context + ": " + boat.Label + " settled at " +
                    boat.Saveable.transform.position.ToString("F2") + " yaw " +
                    boat.Saveable.transform.eulerAngles.y.ToString("F1") + "; front spring " + plan.FrontId + " -> " +
                    BodyName(plan.Front.spring.connectedBody) + ", back spring " + plan.BackId + " -> " +
                    BodyName(plan.Back.spring.connectedBody) + "; kinematic " + boat.Body.isKinematic + ".");
                if (plan.Front.spring.connectedBody != boat.Body || plan.Back.spring.connectedBody != boat.Body)
                    Plugin.Instance?.Warn(context + ": a dock spring of " + boat.Label + " disconnected after placement.");
            }
            catch (Exception exception)
            {
                Plugin.Instance?.Warn(context + ": final placement check failed: " + exception.Message);
            }
        }

        internal static void RestoreAllGuards()
        {
            foreach (var guard in ActiveGuards.ToArray()) guard.Restore();
            ActiveGuards.Clear();
        }

        private static PickupableBoatMooringRope RopeAt(BoatEntry boat, int index) =>
            index >= 0 && index < boat.Ropes.ropes.Length && boat.Ropes.ropes[index] != null
                ? boat.Ropes.ropes[index]
                : throw new InvalidOperationException("Planned mooring rope " + index + " is unavailable.");

        internal static string BodyName(Rigidbody body) => body != null ? body.name : "none";

        internal static GPButtonDockMooring GetMooring(PickupableBoatMooringRope rope)
        {
            var spring = MooringRelease.GetSpring(rope);
            var mooring = spring != null ? spring.GetComponent<GPButtonDockMooring>() : null;
            return mooring != null && mooring.spring == spring ? mooring : null;
        }

        private static GPButtonDockMooring GetOriginalMooring(PickupableBoatMooringRope rope)
        {
            if (!rope.IsMoored()) return null;
            var mooring = GetMooring(rope);
            if (mooring == null)
                throw new InvalidOperationException("An existing mooring rope has no recoverable dock spring.");
            return mooring;
        }

        private static void MoorAt(PickupableBoatMooringRope rope, GPButtonDockMooring dock, Rigidbody body)
        {
            if (dock.spring.connectedBody != null)
                throw new InvalidOperationException("Dock spring " + dock.name + " is held by " + dock.spring.connectedBody.name + ".");
            if (rope == null || rope.IsMoored())
                throw new InvalidOperationException("Chosen mooring rope is unavailable.");
            rope.MoorTo(dock);
            if (!rope.IsMoored() || dock.spring.connectedBody != body)
                throw new InvalidOperationException("Mooring at " + dock.name + " did not connect to the boat.");
        }
    }
}
