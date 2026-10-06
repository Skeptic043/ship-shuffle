using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ShipShuffle
{
    internal sealed class BoatEntry
    {
        internal PurchasableBoat Boat;
        internal SaveableObject Saveable;
        internal Rigidbody Body;
        internal BoatMooringRopes Ropes;
        internal CapsuleCollider Hull;
        internal BoatSize? Size;
        internal bool Vanilla;
        internal HullSpec Spec;
        internal string DisplayName;
        internal string Origin;
        internal int Index => Saveable.sceneIndex;
        internal Identity Identity => new Identity(Index, IdentityName);
        internal string IdentityName => Boat.gameObject.name;
        internal bool Purchased => Boat.isPurchased();
        internal string Label => DisplayName + " [" + Index + "|" + IdentityName + "]";
    }

    // A sale boat the catalogue could not validate. It never moves, but it
    // still occupies (and blocks) its home port.
    internal sealed class UnusableBoat
    {
        internal PurchasableBoat Boat;
        internal SaveableObject Saveable;
        internal string Reason;
        internal bool IntentionalExclusion;
        internal int Index => Saveable != null ? Saveable.sceneIndex : -1;
        internal string Label => Boat.gameObject.name + " (" + Index + ")";
    }

    // Adapted from New Beginnings' SelectionCatalog. Identity is scene index
    // plus exact runtime name; an index shared by two active boats is unusable.
    internal sealed class BoatCatalog
    {
        internal readonly List<BoatEntry> Boats = new List<BoatEntry>();
        internal readonly List<string> Rejections = new List<string>();
        internal readonly List<UnusableBoat> Unusable = new List<UnusableBoat>();

        private static readonly FieldInfo SaveableField = AccessTools.Field(typeof(PurchasableBoat), "saveable");
        private static readonly FieldInfo PurchaseUiField = AccessTools.Field(typeof(PurchasableBoat), "purchaseUI");

        // PurchasableBoat also backs houses. Identify hulls by native boat
        // components; one is enough to reserve the save index.
        internal static bool IsBoatLike(SaveableObject saveable) =>
            saveable != null && (saveable.GetComponent<Rigidbody>() != null ||
                saveable.GetComponent<BoatDamage>() != null ||
                saveable.GetComponent("BoatProbes") != null ||
                saveable.GetComponent<BoatMooringRopes>() != null);

        private void Reject(PurchasableBoat boat, SaveableObject saveable, string reason, bool intentionalExclusion = false)
        {
            if (boat != null && saveable != null && !boat.isPurchased() && !Unusable.Any(item => item.Boat == boat))
                Unusable.Add(new UnusableBoat { Boat = boat, Saveable = saveable, Reason = reason, IntentionalExclusion = intentionalExclusion });
        }

        // The settings snapshot may inspect distance-hidden scene boats.
        // Placement keeps the default active-only catalogue.
        internal static BoatCatalog Discover(bool includeInactive = false)
        {
            var result = new BoatCatalog();
            if (SaveableField == null || PurchaseUiField == null)
            {
                result.Rejections.Add("PurchasableBoat ownership fields were not found in this game build.");
                return result;
            }
            var seenIndexes = new HashSet<int>();
            var ambiguous = new HashSet<int>();
            foreach (var boat in Resources.FindObjectsOfTypeAll<PurchasableBoat>())
            {
                try
                {
                    if (boat == null || !boat.gameObject.scene.IsValid() || !boat.gameObject.scene.isLoaded ||
                        (!includeInactive && (!boat.gameObject.activeInHierarchy || !boat.enabled)) || boat.isHouse) continue;
                    var saveable = SaveableField.GetValue(boat) as SaveableObject;
                    if (!IsBoatLike(saveable)) continue;
                    var index = saveable.sceneIndex;
                    // Reserve every active boat identity before validation. A
                    // duplicated index makes native save ownership ambiguous.
                    if (index >= 0 && !seenIndexes.Add(index))
                    {
                        if (ambiguous.Add(index))
                            result.Rejections.Add("Boat scene index " + index + " is used by more than one active boat.");
                        foreach (var duplicate in result.Boats.Where(item => item.Index == index).ToArray())
                            result.Reject(duplicate.Boat, duplicate.Saveable, "scene index " + index + " is ambiguous");
                        result.Boats.RemoveAll(item => item.Index == index);
                        result.Reject(boat, saveable, "scene index " + index + " is ambiguous");
                        continue;
                    }
                    var label = boat.gameObject.name + " (" + index + ")";
                    if (index < 0 || saveable.gameObject != boat.gameObject ||
                        PurchaseUiField.GetValue(boat) as GameObject == null)
                    {
                        result.Rejections.Add("Boat " + label + " lacks normal purchase state.");
                        result.Reject(boat, saveable, "lacks normal purchase state");
                        continue;
                    }
                    if (KnownIdentities.LeopardCompanion.Equals(new Identity(index, boat.gameObject.name)))
                    {
                        // Keep its resident hull and claims, but never offer it
                        // as an independent sale boat, even if later equipped.
                        const string reason = "Leopard's companion is not a separate sale boat";
                        result.Rejections.Add("Boat " + label + ": " + reason + ".");
                        result.Reject(boat, saveable, reason, intentionalExclusion: true);
                        continue;
                    }
                    var body = saveable.GetComponent<Rigidbody>();
                    var ropes = saveable.GetComponent<BoatMooringRopes>();
                    var missing = new List<string>();
                    if (body == null) missing.Add("Rigidbody");
                    if (!ProbeVelocityGuard.CanGuard(saveable)) missing.Add("compatible BoatProbes velocity guard");
                    if (saveable.GetComponent<BoatDamage>() == null) missing.Add("BoatDamage");
                    if (saveable.GetComponent<BoatLocalItems>() == null) missing.Add("BoatLocalItems");
                    if (ropes == null) missing.Add("BoatMooringRopes");
                    else
                    {
                        if (ropes.ropes == null || ropes.ropes.Length < 2 || ropes.ropes.Any(item => item == null))
                            missing.Add("at least two initialized mooring ropes");
                        if (ropes.GetAnchorController() == null) missing.Add("initialized native equipment controller");
                    }
                    if (missing.Count > 0)
                    {
                        var reason = "missing " + string.Join(", ", missing.ToArray());
                        result.Rejections.Add("Boat " + label + ": " + reason + ".");
                        result.Reject(boat, saveable, reason);
                        continue;
                    }
                    // A settings snapshot reads authored root geometry even
                    // when distance hiding removes its live physics attachment.
                    // GetComponents establishes root ownership without waking it.
                    // Movement still requires an enabled, attached collider.
                    var hulls = body.GetComponents<CapsuleCollider>().Where(item => item != null && !item.isTrigger &&
                        (includeInactive || item.enabled && item.attachedRigidbody == body)).ToArray();
                    if (hulls.Length != 1 || hulls[0].direction != 2)
                    {
                        result.Rejections.Add("Boat " + label + " has " + hulls.Length +
                            (includeInactive ? " root hull capsules" : " enabled attached root hull capsules") +
                            "; exactly one along Z is required.");
                        result.Reject(boat, saveable, includeInactive ? "no single root hull capsule along Z" :
                            "no single enabled attached root hull capsule along Z");
                        continue;
                    }
                    var stock = KnownIdentities.VanillaBoat(index, boat.gameObject.name);
                    var entry = new BoatEntry
                    {
                        Boat = boat, Saveable = saveable, Body = body, Ropes = ropes, Hull = hulls[0],
                        Vanilla = stock != null,
                        DisplayName = stock != null ? stock.DisplayName : boat.gameObject.name,
                        Origin = stock != null ? "Sailwind" : "Mod boat"
                    };
                    try
                    {
                        // Also reads the mooring ropes' rest positions.
                        entry.Spec = HullFootprint.MakeSpec(entry);
                        // Stock categories keep their established meaning. Every
                        // other validated boat uses its measured world hull, with
                        // no mod name, save-index range or loader dependency.
                        // Large hulls also keep extra clearance.
                        entry.Size = stock != null ? stock.Size : BoatSizing.FromHull(entry.Spec.Length, entry.Spec.Radius);
                        entry.Spec.Extra = entry.Size == BoatSize.Large ? DockGeometry.LargeExtra : 0f;
                    }
                    catch (Exception exception)
                    {
                        result.Rejections.Add("Boat " + label + ": " + exception.Message);
                        result.Reject(boat, saveable, exception.Message);
                        continue;
                    }
                    result.Boats.Add(entry);
                }
                catch (Exception exception)
                {
                    result.Rejections.Add("A purchasable boat could not be inspected: " +
                        exception.GetType().Name + ": " + exception.Message);
                    // Still let it hold (and block) its home port.
                    try
                    {
                        var saveable = SaveableField.GetValue(boat) as SaveableObject;
                        if (!boat.isHouse && IsBoatLike(saveable)) result.Reject(boat, saveable, "could not be inspected: " + exception.Message);
                    }
                    catch (Exception) { }
                }
            }
            result.Boats.Sort((left, right) => left.Index.CompareTo(right.Index));
            return result;
        }
    }
}
