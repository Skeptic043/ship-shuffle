using System;
using HarmonyLib;
using UnityEngine;

namespace ShipShuffle
{
    // Holds an unpurchased tier 4 boat at an island-local spot.
    // Added at runtime only; never serialized or applied to native equipment.
    internal sealed class StationKeeper : MonoBehaviour
    {
        // Horizontal containment and yaw hold use acceleration so hull mass
        // does not change the correction. Heave, pitch and roll stay native.
        private const float YawStiffness = 0.2f;
        private const float YawDamping = 0.9f;
        private const float MaxYawAcceleration = 0.3f;

        private Transform island;
        private Vector3 localPosition;
        private float localYaw;
        private Rigidbody body;
        private PurchasableBoat boat;

        internal static StationKeeper Attach(BoatEntry entry, Transform island, Vector3 localPosition, float localYaw)
        {
            var keeper = entry.Boat.gameObject.GetComponent<StationKeeper>();
            if (keeper == null) keeper = entry.Boat.gameObject.AddComponent<StationKeeper>();
            keeper.island = island;
            keeper.localPosition = localPosition;
            keeper.localYaw = localYaw;
            keeper.body = entry.Body;
            keeper.boat = entry.Boat;
            keeper.enabled = true;
            return keeper;
        }

        internal static void Remove(GameObject boat)
        {
            var keeper = boat != null ? boat.GetComponent<StationKeeper>() : null;
            if (keeper == null) return;
            keeper.enabled = false;
            // Immediate, so a re-attach in the same frame cannot reuse a dying keeper.
            DestroyImmediate(keeper);
        }

        private void FixedUpdate()
        {
            // Enabled governs new games and loads only; a placed hold stays.
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.isActiveAndEnabled ||
                boat == null || body == null || island == null || boat.isPurchased())
            {
                enabled = false;
                Destroy(this);
                return;
            }
            if (body.isKinematic) return;
            var target = island.TransformPoint(localPosition);
            var acceleration = StationHold.Acceleration(target - body.position, body.velocity);
            if (HullFootprint.Finite(acceleration)) body.AddForce(acceleration, ForceMode.Acceleration);
            var yawError = Mathf.DeltaAngle(body.rotation.eulerAngles.y, island.eulerAngles.y + localYaw) * Mathf.Deg2Rad;
            var yawAcceleration = Mathf.Clamp(yawError * YawStiffness - body.angularVelocity.y * YawDamping,
                -MaxYawAcceleration, MaxYawAcceleration);
            if (DockGeometry.Finite(yawAcceleration)) body.AddTorque(0f, yawAcceleration, 0f, ForceMode.Acceleration);
        }
    }

    // Buying a station-kept boat releases its hold. A finalizer rather than a postfix:
    // vanilla PurchaseBoat can throw after marking the boat purchased (a
    // NullReferenceException in its sail loop), and a postfix would then
    // never run. The original exception is rethrown.
    [HarmonyPatch(typeof(PurchasableBoat), nameof(PurchasableBoat.PurchaseBoat))]
    internal static class PurchaseHandOff
    {
        private static Exception Finalizer(PurchasableBoat __instance, Exception __exception)
        {
            try
            {
                if (__instance == null || __instance.isHouse) return __exception;
                var note = __exception != null ? " (vanilla PurchaseBoat raised " + __exception.GetType().Name + " afterwards)" : "";
                var held = __instance.GetComponent<StationKeeper>() != null;
                if (held && __instance.isPurchased())
                {
                    StationKeeper.Remove(__instance.gameObject);
                    Plugin.Instance?.DebugLog("Purchase: released hold on " + __instance.name + note + ".");
                }
                else if (held)
                    Plugin.Instance?.Warn("Purchase: " + __instance.name + " is not marked purchased; its hold stays" + note + ".");
                else
                    Plugin.Instance?.DebugLog("Purchase: no hold present on " + __instance.name + note + ".");
            }
            catch (Exception exception)
            {
                Plugin.Instance?.Error("Purchase: releasing the hold failed; the keeper removes itself once the boat is purchased.", exception);
            }
            return __exception;
        }
    }
}
