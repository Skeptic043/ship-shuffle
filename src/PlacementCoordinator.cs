using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ShipShuffle
{
    // Vanilla's sale boats start beside their cleats, so they are already in
    // place when BoatHorizon first lets them go live. Ship Shuffle places its
    // boats before the destination's additive scenery exists, so this keeps
    // an assigned, unpurchased boat frozen until that scenery is live, then
    // once puts it back on its berth (another one at the same port if the
    // loaded scenery blocks it), ties it again and hands it to the game.
    internal sealed class PlacementCoordinator : MonoBehaviour
    {
        private static readonly List<PlacementCoordinator> Active = new List<PlacementCoordinator>();
        private static readonly HashSet<Rigidbody> Frozen = new HashSet<Rigidbody>();
        // Every boat placed this session, finalized or not: none is at its
        // authored berth any more.
        private static readonly HashSet<Rigidbody> Relocated = new HashSet<Rigidbody>();

        private BerthPlan plan;
        private Rigidbody body;
        private int sceneryReadyFrame = -1;

        internal static bool Holds(Rigidbody body) => body != null && Frozen.Contains(body);

        internal static PlacementCoordinator Attach(BerthPlan plan)
        {
            var owner = plan.Boat.Boat.gameObject;
            var coordinator = owner.GetComponent<PlacementCoordinator>();
            if (coordinator == null) coordinator = owner.AddComponent<PlacementCoordinator>();
            coordinator.plan = plan;
            coordinator.body = plan.Boat.Body;
            coordinator.sceneryReadyFrame = -1;
            coordinator.enabled = true;
            Frozen.Add(coordinator.body);
            Relocated.Add(coordinator.body);
            coordinator.body.isKinematic = true;
            if (!Active.Contains(coordinator)) Active.Add(coordinator);
            return coordinator;
        }

        // BoatHorizon decides the frozen state again on its next update.
        internal static void Release(GameObject owner)
        {
            var coordinator = owner != null ? owner.GetComponent<PlacementCoordinator>() : null;
            if (coordinator == null) return;
            coordinator.Unregister();
            DestroyImmediate(coordinator);
        }

        internal static void ClearAll()
        {
            foreach (var coordinator in Active.ToArray())
                if (coordinator != null) Release(coordinator.gameObject);
            Active.Clear();
            Frozen.Clear();
            Relocated.Clear();
        }

        private void OnDestroy() => Unregister();

        private void Unregister()
        {
            if (body != null) Frozen.Remove(body);
            Active.Remove(this);
        }

        private void Update()
        {
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.isActiveAndEnabled || plan?.Boat?.Boat == null || plan.Boat.Purchased)
            {
                Release(gameObject);
                return;
            }
            // A world shift translates the island before IslandSceneryScene
            // moves its additive roots after it; check only between shifts.
            if (!GameState.playing || GameState.currentlyLoading || GameState.waitingForShift || !LiveScenery.Ready(plan.Site))
            {
                sceneryReadyFrame = -1;
                return;
            }
            // IslandSceneryScene aligns the additive roots in its first Update.
            if (sceneryReadyFrame < 0)
            {
                sceneryReadyFrame = Time.frameCount;
                return;
            }
            if (Time.frameCount <= sceneryReadyFrame) return;
            try
            {
                FinalizeBerth();
            }
            catch (Exception exception)
            {
                plugin.Error("Placement finalization failed for " + plan.Boat.Label + "; it is handed to the game as it stands.", exception);
            }
            finally
            {
                plan.Finalized = true;
                Release(gameObject);
            }
        }

        private void FinalizeBerth()
        {
            var old = plan;
            // The plan's world pose predates any world shift; its island pose does not.
            var final = BerthResolver.Refreshed(old);
            var sceneryProblem = LiveScenery.Problem(final.Site, final.Boat, final.Position, final.Rotation);
            if (sceneryProblem != null)
            {
                var replacement = Replacement(old);
                if (replacement == null)
                    Plugin.Instance?.Warn("Placement finalization: no clear berth at " + old.Site.Port.Label + " for " + old.Boat.Label +
                        "; it stays at its stored spot (" + sceneryProblem + ").");
                else
                {
                    Plugin.Instance?.DebugLog("Placement finalization: " + old.Boat.Label + " replaced blocked " + old.Describe() +
                        " with " + replacement.Describe() + " at the same port (" + sceneryProblem + ").");
                    final = replacement;
                }
            }
            BoatRelocator.FinalizePlacement(old, final, "Placement finalization");
            if (!SameAssignment(old, final)) FleetGenerator.UpdateStoredAssignment(final);
        }

        // Every other shuffled boat already sits on its own berth, so it is an
        // obstacle and holds its springs; only its authored claim is void.
        private static BerthPlan Replacement(BerthPlan old)
        {
            if (!BerthResolver.TryBuildSite(old.Site.Port, out var site, out var reason))
            {
                Plugin.Instance?.Warn("Placement finalization: " + old.Site.Port.Label + " could not be rebuilt (" + reason + ").");
                return null;
            }
            var occupancy = Occupancy.Build(new[] { old.Boat.Body }, Relocated.Where(item => item != null).ToArray());
            var saleBoats = BoatCatalog.Discover().Boats.Where(item => !item.Purchased).ToArray();
            var input = BerthResolver.MakePortInput(site, old.Boat, occupancy, saleBoats, liveScenery: true);
            var candidate = BerthTiers.Evaluate(input, old.Boat.Spec).Best;
            return candidate != null && candidate.Accepted ? BerthResolver.MakePlan(site, old.Boat, candidate) : null;
        }

        internal static bool SameAssignment(BerthPlan left, BerthPlan right) =>
            left != null && right != null && left.Tier == right.Tier && left.FrontId == right.FrontId && left.BackId == right.BackId &&
            left.Candidate.Side == right.Candidate.Side && (left.LocalPosition - right.LocalPosition).sqrMagnitude <= 1e-8f &&
            Mathf.Abs(Mathf.DeltaAngle(left.LocalYaw, right.LocalYaw)) <= 1e-4f;
    }

    // BoatHorizon sets isKinematic from distance and game state on every
    // update; a waiting boat stays frozen as if it were still far away.
    [HarmonyPatch(typeof(BoatHorizon), "UpdateKinematic")]
    internal static class PlacementFreezePatch
    {
        private static void Postfix(Rigidbody ___rigidbody)
        {
            if (PlacementCoordinator.Holds(___rigidbody) && !___rigidbody.isKinematic) ___rigidbody.isKinematic = true;
        }
    }
}
