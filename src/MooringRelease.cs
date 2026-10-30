using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ShipShuffle
{
    // Native Unmoor clears the spring without checking which hull owns it.
    // A stale rope reference must never disconnect another boat.
    internal static class MooringRelease
    {
        private static readonly FieldInfo SpringField =
            AccessTools.Field(typeof(PickupableBoatMooringRope), "mooredToSpring");

        // Native Awake records where a rope rests twice: initialPos in its
        // initialParent's frame (Update measures line length from it) and
        // springAnchor in the hull's frame (the spring pulls there). Both are
        // the same point on a vanilla hull.
        private static readonly FieldInfo InitialPosField = AccessTools.Field(typeof(PickupableBoatMooringRope), "initialPos");
        private static readonly FieldInfo InitialParentField = AccessTools.Field(typeof(PickupableBoatMooringRope), "initialParent");
        private static readonly FieldInfo SpringAnchorField = AccessTools.Field(typeof(PickupableBoatMooringRope), "springAnchor");
        internal const float RopeAnchorTolerance = 1f;

        internal static bool CanReadSpring => SpringField != null;

        // Why a moored rope's native line length would not describe its own
        // spring, or null (also when the native fields cannot be read).
        internal static string GeometryProblem(PickupableBoatMooringRope rope, Rigidbody body)
        {
            if (rope == null || body == null || InitialPosField == null || InitialParentField == null || SpringAnchorField == null)
                return null;
            if (!(InitialParentField.GetValue(rope) is Transform parent) || parent == null) return "rope " + rope.name + " has no rest parent";
            var measured = parent.TransformPoint((Vector3)InitialPosField.GetValue(rope));
            var anchored = body.transform.TransformPoint((Vector3)SpringAnchorField.GetValue(rope));
            var gap = Vector3.Distance(measured, anchored);
            return gap <= RopeAnchorTolerance ? null
                : "rope " + rope.name + " measures its length from a point " + gap.ToString("F1") + " m from where it is tied to the hull";
        }

        internal static SpringJoint GetSpring(PickupableBoatMooringRope rope) =>
            rope != null ? SpringField?.GetValue(rope) as SpringJoint : null;

        internal static bool CanRelease(IEnumerable<PickupableBoatMooringRope> ropes, Rigidbody body, out string reason)
        {
            foreach (var rope in ropes)
            {
                reason = Problem(rope, body);
                if (reason != null) return false;
            }
            reason = null;
            return true;
        }

        internal static void ReleaseAll(IEnumerable<PickupableBoatMooringRope> ropes, Rigidbody body)
        {
            if (!CanRelease(ropes, body, out var reason)) throw new InvalidOperationException(reason);
            foreach (var rope in ropes)
                if (!TryRelease(rope, body, out reason)) throw new InvalidOperationException(reason);
        }

        internal static bool TryRelease(PickupableBoatMooringRope rope, Rigidbody body, out string reason)
        {
            reason = Problem(rope, body);
            if (reason != null) return false;
            rope.Unmoor();
            rope.ResetRopePos();
            return true;
        }

        private static string Problem(PickupableBoatMooringRope rope, Rigidbody body)
        {
            if (rope == null || body == null || rope.GetBoatRigidbody() != body)
                return "a mooring rope does not belong to this boat";
            if (!rope.IsMoored()) return null;
            var spring = GetSpring(rope);
            if (spring == null) return "a moored rope has no readable dock spring";
            return spring.connectedBody != null && spring.connectedBody != body
                ? "rope " + rope.name + " references a dock spring held by " + spring.connectedBody.name
                : null;
        }
    }
}
