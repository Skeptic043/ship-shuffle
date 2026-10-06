using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ShipShuffle
{
    // Converts root hull capsules into world X/Z footprints (adapted from New
    // Beginnings' BerthClearance) and gathers the other boats as obstacles.
    internal static class HullFootprint
    {
        internal static Vec2 Flat(Vector3 value) => new Vec2(value.x, value.z);

        internal static HullSpec MakeSpec(BoatEntry boat)
        {
            var hull = boat.Hull;
            var scale = boat.Body.transform.lossyScale;
            if (hull == null || hull.direction != 2 || !Finite(scale) || !Finite(hull.center) ||
                !DockGeometry.Finite(hull.radius) || !DockGeometry.Finite(hull.height) || hull.radius <= 0f || hull.height <= 0f ||
                scale.x == 0f || scale.y == 0f || scale.z == 0f)
                throw new InvalidOperationException("Boat " + boat.Label + " has invalid root hull geometry.");
            var radius = hull.radius * Math.Max(Math.Abs(scale.x), Math.Abs(scale.y));
            var length = Math.Max(hull.height * Math.Abs(scale.z), 2f * radius);
            if (!DockGeometry.Finite(radius) || !DockGeometry.Finite(length) || radius <= 0f || length <= 0f ||
                !DockGeometry.Finite(hull.center.x * scale.x) || !DockGeometry.Finite(hull.center.z * scale.z))
                throw new InvalidOperationException("Boat " + boat.Label + " overflowed its root hull geometry.");
            return new HullSpec
            {
                Length = length,
                Radius = radius,
                CenterRight = hull.center.x * scale.x,
                CenterForward = hull.center.z * scale.z,
                Ropes = RopeAnchors(boat)
            };
        }

        private static readonly FieldInfo RopeParentField = AccessTools.Field(typeof(PickupableBoatMooringRope), "initialParent");
        private static readonly FieldInfo RopePositionField = AccessTools.Field(typeof(PickupableBoatMooringRope), "initialPos");

        // Resting rope positions, even while a rope is currently tied to a
        // cleat. Same order as BoatMooringRopes.ropes.
        internal static Vec2[] RopeAnchors(BoatEntry boat)
        {
            if (RopeParentField == null || RopePositionField == null)
                throw new InvalidOperationException("Mooring rope rest-position fields were not found in this game build.");
            var root = boat.Saveable.transform;
            var scale = root.lossyScale;
            var result = new Vec2[boat.Ropes.ropes.Length];
            for (var i = 0; i < result.Length; ++i)
            {
                var rope = boat.Ropes.ropes[i];
                var parent = rope != null ? RopeParentField.GetValue(rope) as Transform : null;
                if (parent == null || !(RopePositionField.GetValue(rope) is Vector3 rest))
                    throw new InvalidOperationException("Mooring rope " + i + " on " + boat.Label + " has no rest position.");
                var local = Vector3.Scale(root.InverseTransformPoint(parent.TransformPoint(rest)), scale);
                if (!Finite(local)) throw new InvalidOperationException("Mooring rope " + i + " on " + boat.Label + " is non-finite.");
                result[i] = new Vec2(local.x, local.z);
            }
            return result;
        }

        // A hidden boat can have its collider disabled; its authored capsule
        // still occupies the berth once the island and physics become live.
        internal static CapsuleCollider[] RootCapsules(Rigidbody body) =>
            body.GetComponents<CapsuleCollider>().Where(hull => hull != null && !hull.isTrigger &&
                (hull.attachedRigidbody == body || !hull.enabled)).ToArray();

        // Every active loaded boat with mooring ropes, at its current pose.
        internal static List<KeyValuePair<Rigidbody, Obstacle>> AllBoats()
        {
            var result = new List<KeyValuePair<Rigidbody, Obstacle>>();
            foreach (var ropes in Resources.FindObjectsOfTypeAll<BoatMooringRopes>())
            {
                if (ropes == null || !ropes.gameObject.scene.IsValid() || !ropes.gameObject.scene.isLoaded ||
                    !ropes.gameObject.activeInHierarchy) continue;
                var body = ropes.GetComponent<Rigidbody>();
                if (body == null || result.Exists(pair => pair.Key == body)) continue;
                var obstacle = new Obstacle { Name = body.name, Root = Flat(body.transform.position) };
                try
                {
                    var hulls = RootCapsules(body);
                    if (hulls.Length == 1)
                    {
                        obstacle.Hull = MakeFootprint(body.transform.position, body.transform.rotation,
                            body.transform.lossyScale, hulls[0].center, hulls[0].radius, hulls[0].height, hulls[0].direction);
                        obstacle.Measurable = obstacle.Hull.IsValid;
                    }
                }
                catch (InvalidOperationException)
                {
                    obstacle.Measurable = false;
                }
                result.Add(new KeyValuePair<Rigidbody, Obstacle>(body, obstacle));
            }
            return result;
        }

        // World metres: the native recovery marker itself has scale 0.5.
        internal static Capsule2 MakeFootprint(Vector3 position, Quaternion rotation, Vector3 scale,
            Vector3 center, float radius, float height, int direction)
        {
            if (!Finite(position) || !Finite(scale) || !Finite(center) || !Finite(rotation) ||
                !DockGeometry.Finite(radius) || !DockGeometry.Finite(height) || radius <= 0f || height <= 0f ||
                direction < 0 || direction > 2 || scale.x == 0f || scale.y == 0f || scale.z == 0f)
                throw new InvalidOperationException("Hull capsule has invalid geometry.");
            var axis = direction == 0 ? Vector3.right : direction == 1 ? Vector3.up : Vector3.forward;
            var axialScale = Math.Abs(scale[direction]);
            var radialScale = Math.Max(Math.Abs(scale[(direction + 1) % 3]), Math.Abs(scale[(direction + 2) % 3]));
            var worldRadius = radius * radialScale;
            var halfSegment = Math.Max(0f, height * axialScale * 0.5f - worldRadius);
            var worldCenter = position + rotation * Vector3.Scale(center, scale);
            var offset = rotation * axis * halfSegment;
            var a = worldCenter + offset;
            var b = worldCenter - offset;
            if (!Finite(a) || !Finite(b) || !DockGeometry.Finite(worldRadius) || worldRadius <= 0f)
                throw new InvalidOperationException("Hull capsule overflowed its geometry.");
            return new Capsule2(Flat(a), Flat(b), worldRadius);
        }

        // Vector forms of DockGeometry.Finite.
        internal static bool Finite(Vector3 value) =>
            DockGeometry.Finite(value.x) && DockGeometry.Finite(value.y) && DockGeometry.Finite(value.z);
        internal static bool Finite(Quaternion value) =>
            DockGeometry.Finite(value.x) && DockGeometry.Finite(value.y) && DockGeometry.Finite(value.z) && DockGeometry.Finite(value.w);
    }
}
