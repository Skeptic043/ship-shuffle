using System;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShipShuffle
{
    // The additive island scene is the only source of detailed dock/scenery
    // colliders. Planning remains pure while it is absent; finalization feeds
    // this query back into the same candidate resolver once the scene is live.
    internal static class LiveScenery
    {
        internal static bool Ready(DockSite site)
        {
            if (site?.Port?.Island == null) return false;
            var island = site.Port.Island;
            // The detail scene loads well before the horizon system has raised
            // it to its final playable height. Validate only once native
            // IslandHorizon says the island is fully emerged. An island
            // without a detail scene (index 0 or below) has nothing to wait for.
            if (island.currentDistance > island.fullyEmergedDistance) return false;
            if (island.islandIndex <= 0) return true;
            var scene = SceneManager.GetSceneByBuildIndex(island.islandIndex);
            return scene.IsValid() && scene.isLoaded;
        }

        // BerthInput.SolidProblem for candidates of a freshly built site.
        internal static string Problem(DockSite site, BoatEntry boat, Candidate candidate)
        {
            if (candidate == null || !candidate.Root.IsFinite || !candidate.Forward.IsFinite) return "candidate hull is unavailable";
            var forward = new Vector3(candidate.Forward.X, 0f, candidate.Forward.Z).normalized;
            return Problem(site, boat, new Vector3(candidate.Root.X, SpawnHeight.For(boat), candidate.Root.Z),
                Quaternion.LookRotation(forward, Vector3.up));
        }

        internal static string Problem(DockSite site, BoatEntry boat, Vector3 root, Quaternion rotation)
        {
            if (!Ready(site)) return "destination scenery is not loaded";
            if (boat?.Hull == null || !HullFootprint.Finite(root)) return "candidate hull is unavailable";
            // Without a detail scene there are no additive colliders to test.
            if (site.Port.Island.islandIndex <= 0) return null;
            try
            {
                Capsule(boat, root, rotation, out var a, out var b, out var radius);
                var sceneIndex = site.Port.Island.islandIndex;
                var hits = Physics.OverlapCapsule(a, b, radius + DockGeometry.DockClearance, ~0, QueryTriggerInteraction.Ignore)
                    .Where(collider => collider != null && collider.enabled && collider.gameObject.activeInHierarchy &&
                        collider.gameObject.scene.buildIndex == sceneIndex && collider.attachedRigidbody != boat.Body &&
                        !collider.transform.IsChildOf(boat.Saveable.transform) &&
                        !Physics.GetIgnoreLayerCollision(boat.Hull.gameObject.layer, collider.gameObject.layer) &&
                        !Physics.GetIgnoreCollision(boat.Hull, collider))
                    .Select(collider => collider.name).Distinct().OrderBy(name => name).ToArray();
                return hits.Length == 0 ? null : string.Join(", ", hits);
            }
            catch (Exception exception)
            {
                return "live hull query failed: " + exception.Message;
            }
        }

        internal static void Capsule(BoatEntry boat, Vector3 root, Quaternion rotation,
            out Vector3 a, out Vector3 b, out float radius)
        {
            var hull = boat.Hull;
            var scale = boat.Saveable.transform.lossyScale;
            var axis = hull.direction == 0 ? Vector3.right : hull.direction == 1 ? Vector3.up : Vector3.forward;
            var axialScale = Mathf.Abs(scale[hull.direction]);
            var radialScale = Mathf.Max(Mathf.Abs(scale[(hull.direction + 1) % 3]), Mathf.Abs(scale[(hull.direction + 2) % 3]));
            radius = hull.radius * radialScale;
            var halfSegment = Mathf.Max(0f, hull.height * axialScale * 0.5f - radius);
            var center = root + rotation * Vector3.Scale(hull.center, scale);
            var offset = rotation * axis * halfSegment;
            a = center + offset;
            b = center - offset;
            if (!HullFootprint.Finite(a) || !HullFootprint.Finite(b) || !DockGeometry.Finite(radius) || radius <= 0f)
                throw new InvalidOperationException("root capsule produced invalid live geometry");
        }
    }
}
