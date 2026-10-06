using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ShipShuffle
{
    // Gives a placed, moored sale boat's two planned lines some slack once, so
    // the game's physics can settle the hull, instead of the zero-slack lines
    // vanilla leaves after a kinematic-to-dynamic switch. At the first such
    // reset after placement each line still moored to its planned spring is
    // set to its fixed length (RopeSlack), then the component removes itself
    // and the lines are the game's. Added at placement only; never
    // serialized. The mechanics follow New Beginnings' StarterMooring
    // (adapted, not shared).
    internal sealed class MooringSlack : MonoBehaviour
    {
        private static readonly FieldInfo WasKinematicField = AccessTools.Field(typeof(PickupableBoatMooringRope), "wasKinematic");
        private static readonly FieldInfo MaxLengthField = AccessTools.Field(typeof(PickupableBoatMooringRope), "maxLength");
        private static readonly MethodInfo DistanceMethod = AccessTools.Method(typeof(PickupableBoatMooringRope), "GetCurrentDistanceSquared");

        private sealed class Line
        {
            internal PickupableBoatMooringRope Rope;
            internal SpringJoint Spring;
            internal readonly SlackLine State = new SlackLine();
        }

        private Line[] lines = new Line[0];
        private Rigidbody body;
        private PurchasableBoat boat;
        private string label;
        private float slack;
        private readonly List<string> applied = new List<string>();

        internal static bool Available => MooringRelease.CanReadSpring && WasKinematicField != null && MaxLengthField != null && DistanceMethod != null;

        // frontLine/backLine: the planned line lengths from the berth plan.
        internal static void Attach(BoatEntry entry, PickupableBoatMooringRope front, SpringJoint frontSpring, float frontLine,
            PickupableBoatMooringRope back, SpringJoint backSpring, float backLine)
        {
            Remove(entry.Boat.gameObject);
            if (!Available)
            {
                Plugin.Instance?.Warn("Mooring slack skipped for " + entry.Label + ": native rope fields are unavailable.");
                return;
            }
            var slack = entry.Boat.gameObject.AddComponent<MooringSlack>();
            slack.body = entry.Body;
            slack.boat = entry.Boat;
            slack.label = entry.Label;
            slack.slack = RopeSlack.For(entry.Size == BoatSize.Large);
            slack.lines = new[]
            {
                new Line { Rope = front, Spring = frontSpring },
                new Line { Rope = back, Spring = backSpring }
            };
            slack.lines[0].State.PlannedLine = frontLine;
            slack.lines[1].State.PlannedLine = backLine;
        }

        internal static void Remove(GameObject owner)
        {
            var slack = owner != null ? owner.GetComponent<MooringSlack>() : null;
            if (slack == null) return;
            slack.enabled = false;
            DestroyImmediate(slack);
        }

        private void OnEnable() => StartCoroutine(Loop());

        // Native Update performs the reset; act at the end of the same frame.
        private IEnumerator Loop()
        {
            var endOfFrame = new WaitForEndOfFrame();
            while (true)
            {
                yield return endOfFrame;
                bool keep;
                try
                {
                    keep = Tick();
                }
                catch (Exception exception)
                {
                    Plugin.Instance?.Warn("Mooring slack for " + label + " stopped: " + exception.Message);
                    keep = false;
                }
                if (!keep)
                {
                    Destroy(this);
                    yield break;
                }
            }
        }

        // False when this component has nothing left to do.
        private bool Tick()
        {
            // Enabled governs new games and loads only; placed lines keep their slack.
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.isActiveAndEnabled ||
                body == null || boat == null || boat.isPurchased()) return false;
            if (body.isKinematic) return true;
            var anyLeft = false;
            foreach (var line in lines)
            {
                if (line.State.Done) continue;
                var moored = line.Rope != null && line.Spring != null && line.Rope.GetBoatRigidbody() == body &&
                    ReferenceEquals(MooringRelease.GetSpring(line.Rope), line.Spring) && line.Spring.connectedBody == body;
                var nativeReset = moored && !(bool)WasKinematicField.GetValue(line.Rope);
                var distance = moored ? (float)DistanceMethod.Invoke(line.Rope, null) : float.NaN;
                var maxLength = (float)MaxLengthField.GetValue(null);
                var length = line.State.Step(true, GameState.playing, GameState.currentlyLoading, moored, nativeReset, distance, maxLength, slack);
                if (!line.State.Done)
                {
                    anyLeft = true;
                    continue;
                }
                var name = line.Rope != null ? line.Rope.name : "rope";
                if (float.IsNaN(length))
                {
                    plugin.DebugLog("Mooring slack: " + label + " " + name + (line.State.Skipped
                        ? " is no longer moored to its planned cleat; left to the game."
                        : " is at the rope limit (" + Mathf.Sqrt(distance).ToString("F2") + " m); left to the game."));
                    continue;
                }
                // Both native length representations must agree.
                line.Rope.currentRopeLengthSquared = length;
                line.Spring.maxDistance = Mathf.Sqrt(length);
                applied.Add(name + " " + Mathf.Sqrt(length).ToString("F2") + " m");
                plugin.DebugLog("Mooring slack: " + label + " " + name + " set to " + Mathf.Sqrt(length).ToString("F2") +
                    " m (planned line " + (float.IsNaN(line.State.PlannedLine) ? "unknown" : line.State.PlannedLine.ToString("F2") + " m") +
                    ", distance " + Mathf.Sqrt(distance).ToString("F2") + " m, + " + slack.ToString("F0") + " m).");
            }
            if (!anyLeft && applied.Count > 0)
                plugin.DebugLog("Mooring slack: " + label + " was given " + slack.ToString("F0") + " m of extra line once (" +
                    string.Join(", ", applied.ToArray()) + "); its lines are now the game's.");
            return anyLeft;
        }
    }
}
