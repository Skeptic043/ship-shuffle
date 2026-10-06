using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ShipShuffle
{
    // Which boats still occupy the world while a fleet is planned. Boats in
    // Moving either leave their berth or are the only boat at their port, so
    // they are neither obstacles nor claimants and their connected springs
    // count as free. Every other boat with mooring ropes stays exactly where
    // it is, including purchased, hidden and unclassified boats. At a shared
    // shipyard the other boats moving in are added per port as planned
    // berths (BerthResolver.MakePortInput), not here.
    internal sealed class Occupancy
    {
        internal readonly HashSet<Rigidbody> Moving;
        private readonly List<KeyValuePair<Rigidbody, Obstacle>> obstacles = new List<KeyValuePair<Rigidbody, Obstacle>>();
        // Authored cleats of boats that stay, with their owners.
        internal readonly Dictionary<Transform, List<BoatMooringRopes>> Claims = new Dictionary<Transform, List<BoatMooringRopes>>();

        private Occupancy(IEnumerable<Rigidbody> moving)
        {
            Moving = new HashSet<Rigidbody>(moving);
        }

        internal static Occupancy Build(IEnumerable<Rigidbody> moving)
        {
            var result = new Occupancy(moving);
            foreach (var pair in HullFootprint.AllBoats())
                if (!result.Moving.Contains(pair.Key)) result.obstacles.Add(pair);
            foreach (var ropes in Resources.FindObjectsOfTypeAll<BoatMooringRopes>())
            {
                if (ropes == null || !ropes.gameObject.scene.IsValid() || !ropes.gameObject.scene.isLoaded ||
                    !ropes.gameObject.activeInHierarchy) continue;
                var body = ropes.GetComponent<Rigidbody>();
                if (body != null && result.Moving.Contains(body)) continue;
                foreach (var mooring in new[] { ropes.mooringFront, ropes.mooringBack })
                {
                    if (mooring == null) continue;
                    if (!result.Claims.TryGetValue(mooring, out var owners)) result.Claims.Add(mooring, owners = new List<BoatMooringRopes>());
                    if (!owners.Contains(ropes)) owners.Add(ropes);
                }
            }
            return result;
        }

        internal List<Obstacle> ObstaclesFor(Rigidbody boat)
        {
            var result = new List<Obstacle>();
            foreach (var pair in obstacles) if (pair.Key != boat) result.Add(pair.Value);
            return result;
        }

        // Why boat may not tie to cleat, or null (CleatAccess.Check). claimed:
        // an authored claim blocks it.
        internal string CleatProblem(GPButtonDockMooring cleat, Rigidbody boat, out bool claimed)
        {
            claimed = false;
            if (cleat == null || cleat.spring == null) return "cleat or spring is gone";
            var connected = cleat.spring.connectedBody;
            Claims.TryGetValue(cleat.transform, out var owners);
            var block = CleatAccess.Check(connected != null ? connected : null, boat, Moving,
                owners?.Where(ropes => ropes != null), ropes => ropes.GetComponent<Rigidbody>(),
                out var heldBy, out var claimedBy);
            claimed = block == CleatBlock.Claimed;
            return block == CleatBlock.SpringHeld ? "spring connected to " + heldBy.name
                : claimed ? "authored berth of " + claimedBy.gameObject.name
                : null;
        }
    }
}
