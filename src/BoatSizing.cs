using System;

namespace ShipShuffle
{
    // Coarse filter policy, not a berth-fit test. The planner still uses the
    // complete measured hull, rope anchors and terrain for each destination.
    internal static class BoatSizing
    {
        // World metres. Small covers the retained Dhow/Cog/Kakam/Gallus
        // fixtures (up to 12 x 4.28 m), Medium the Sanbuq/Brig/Junk
        // fixtures (up to 25.86 x 6.68 m). Both dimensions must fit.
        internal static BoatSize FromHull(float length, float radius)
        {
            var diameter = 2f * radius;
            if (float.IsNaN(length) || float.IsInfinity(length) || length <= 0f ||
                float.IsNaN(diameter) || float.IsInfinity(diameter) || diameter <= 0f)
                throw new InvalidOperationException("Hull size must be finite and positive.");
            if (length <= 13f && diameter <= 4.4f) return BoatSize.Small;
            if (length <= 26f && diameter <= 6.8f) return BoatSize.Medium;
            return BoatSize.Large;
        }
    }
}
