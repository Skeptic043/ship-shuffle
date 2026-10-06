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

        internal static bool CanReadSpring => SpringField != null;

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
