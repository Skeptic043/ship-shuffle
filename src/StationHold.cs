using UnityEngine;

namespace ShipShuffle
{
    // A small free area lets the hull respond to waves. Outside it, a bounded
    // horizontal spring slows outward motion and brings the hull back.
    internal static class StationHold
    {
        internal const float FreeRadius = 0.25f;
        internal const float MaxAcceleration = 1f;
        private const float Stiffness = 1f;
        private const float Damping = 2f;

        internal static Vector3 Acceleration(Vector3 error, Vector3 velocity)
        {
            if (!HullFootprint.Finite(error) || !HullFootprint.Finite(velocity)) return Vector3.zero;
            error.y = 0f;
            velocity.y = 0f;
            var distance = error.magnitude;
            if (!DockGeometry.Finite(distance)) return Vector3.zero;
            if (distance <= FreeRadius) return Vector3.zero;
            var direction = error / distance;
            // Do not resist wave motion that is already returning the hull.
            var outwardSpeed = Mathf.Min(Vector3.Dot(velocity, direction), 0f);
            var acceleration = direction * ((distance - FreeRadius) * Stiffness - outwardSpeed * Damping);
            return Vector3.ClampMagnitude(acceleration, MaxAcceleration);
        }
    }
}
