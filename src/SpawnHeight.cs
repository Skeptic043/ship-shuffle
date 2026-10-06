using System;
using HarmonyLib;
using UnityEngine;

namespace ShipShuffle
{
    // A one-time, flat-water start pose from the boat's initialized native
    // buoyancy model. Waves and subsequent vertical motion remain native.
    internal static class SpawnHeight
    {
        internal static float For(BoatEntry boat)
        {
            try
            {
                var probes = boat.Saveable.GetComponent("BoatProbes");
                if (probes == null || Read(probes, "_rb") as Rigidbody != boat.Body)
                    throw new InvalidOperationException("native buoyancy has not initialized");
                var gravity = Physics.gravity;
                if (!boat.Body.useGravity || !HullFootprint.Finite(gravity) || gravity.y >= 0f ||
                    Math.Abs(gravity.x) > 1e-5f || Math.Abs(gravity.z) > 1e-5f)
                    throw new InvalidOperationException("native vertical gravity is unavailable");
                var points = Read(probes, "_forcePoints") as Array;
                var queries = Read(probes, "_queryPoints") as Vector3[];
                if (points == null || queries == null || queries.Length != points.Length + 1)
                    throw new InvalidOperationException("native buoyancy queries have not initialized");
                var scale = boat.Saveable.transform.lossyScale.y;
                var com = (Vector3)Read(probes, "_centerOfMass");
                if (!DockGeometry.Finite(scale) || scale <= 0f || !HullFootprint.Finite(com))
                    throw new InvalidOperationException("native buoyancy transform is invalid");
                var offsets = new float[points.Length];
                var weights = new float[points.Length];
                for (var i = 0; i < points.Length; ++i)
                {
                    var point = points.GetValue(i);
                    // Start already applies customScale to the force points.
                    var offset = (Vector3)Read(point, "_offsetPosition");
                    if (!HullFootprint.Finite(offset))
                        throw new InvalidOperationException("native buoyancy point is non-finite");
                    offsets[i] = (offset.y + com.y) * scale;
                    weights[i] = (float)Read(point, "_weight");
                }
                var oceanType = probes.GetType().Assembly.GetType("Crest.OceanRenderer");
                var ocean = oceanType != null ? AccessTools.Property(oceanType, "Instance")?.GetValue(null, null) : null;
                var sea = ocean != null ? AccessTools.Property(oceanType, "SeaLevel")?.GetValue(ocean, null) : null;
                if (!(sea is float seaLevel)) throw new InvalidOperationException("native sea level is unavailable");
                if (!TrySolve(offsets, weights, boat.Body.mass, (float)Read(probes, "_forceMultiplier"),
                    (float)Read(probes, "_totalWeight"), seaLevel, out var height))
                    throw new InvalidOperationException("native buoyancy weights, mass or forces are invalid");
                return height;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("Cannot determine spawn height for " + boat.Label + ": " + exception.Message, exception);
            }
        }

        private static object Read(object owner, string field) => owner != null
            ? AccessTools.Field(owner.GetType(), field)?.GetValue(owner)
                ?? throw new InvalidOperationException("native buoyancy field " + field + " is unavailable")
            : throw new InvalidOperationException("native buoyancy point is unavailable");

        // Native lift divided by gravity is 1000 * multiplier / totalWeight
        // times weighted immersion. A monotone bounded solve handles mixed
        // point heights, including points that begin above the water.
        internal static bool TrySolve(float[] offsets, float[] weights, float mass, float multiplier,
            float totalWeight, float seaLevel, out float height)
        {
            height = 0f;
            if (offsets == null || weights == null || offsets.Length == 0 || offsets.Length != weights.Length ||
                !DockGeometry.Finite(mass) || mass <= 0f || !DockGeometry.Finite(multiplier) || multiplier <= 0f ||
                !DockGeometry.Finite(totalWeight) || totalWeight <= 0f || !DockGeometry.Finite(seaLevel)) return false;
            double sum = 0, lowOffset = double.PositiveInfinity, highOffset = double.NegativeInfinity;
            for (var i = 0; i < offsets.Length; ++i)
            {
                if (!DockGeometry.Finite(offsets[i]) || !DockGeometry.Finite(weights[i]) || weights[i] < 0f) return false;
                sum += weights[i];
                lowOffset = Math.Min(lowOffset, offsets[i]);
                highOffset = Math.Max(highOffset, offsets[i]);
            }
            if (sum <= 0 || Math.Abs(sum - totalWeight) > Math.Max(1e-4, sum * 1e-4)) return false;
            var depth = (double)mass * totalWeight / (1000.0 * multiplier * sum);
            var low = seaLevel - highOffset - depth;
            var high = seaLevel - lowOffset;
            for (var step = 0; step < 48; ++step)
            {
                var middle = (low + high) * 0.5;
                double submerged = 0;
                for (var i = 0; i < offsets.Length; ++i)
                    submerged += weights[i] * Math.Max(0, seaLevel - middle - offsets[i]);
                if (1000.0 * multiplier * submerged / totalWeight > mass) low = middle;
                else high = middle;
            }
            height = (float)((low + high) * 0.5);
            return DockGeometry.Finite(height);
        }
    }
}
