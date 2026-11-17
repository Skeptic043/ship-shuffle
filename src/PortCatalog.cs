using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ShipShuffle
{
    internal sealed class PortEntry
    {
        internal Port Port;
        internal RecoveryPort Recovery;
        // An authored pose may guide placement without enabling native recovery.
        internal RecoveryPort PlacementReference;
        internal IslandHorizon Island;
        internal string DisplayName;
        // A portless island destination (Port is null): one fixed offshore hold.
        internal IslandHold Hold;
        // No registered usable RecoveryPort: vanilla never recovers a boat here.
        internal bool NoRecovery => Recovery == null;
        // Port index and object name, or island index and name for an island hold.
        internal Identity Identity;
        internal int Index => Identity.Index;
        internal string IdentityName => Identity.Name;
        internal string Label => DisplayName + " [" + Index + "|" + IdentityName + "]";
    }

    // New Beginnings' port rules plus one registered port per island, so a
    // berth on that island's docks belongs to exactly this port.
    internal sealed class PortCatalog
    {
        internal readonly List<PortEntry> Ports = new List<PortEntry>();
        internal readonly List<string> Rejections = new List<string>();

        private static readonly FieldInfo RecoveryPortsField = AccessTools.Field(typeof(Recovery), "ports");

        internal static bool IsTestPort(Port port) => port != null && port.portIndex == KnownIdentities.TestPort.Index &&
            string.Equals(port.gameObject.name, KnownIdentities.TestPort.Name, StringComparison.OrdinalIgnoreCase);

        internal static PortCatalog Discover()
        {
            var result = new PortCatalog();
            var registered = RecoveryPortsField?.GetValue(null) as List<RecoveryPort>;
            if (RecoveryPortsField == null || Port.ports == null || registered == null)
            {
                result.Rejections.Add("Ports or recovery berths are not registered in this game state.");
                return result;
            }
            var seen = new HashSet<int>();
            var candidates = new List<PortEntry>();
            foreach (var port in Port.ports.Where(item => item != null))
            {
                var index = port.portIndex;
                if (IsTestPort(port))
                {
                    result.Rejections.Add("Port 7 is a scene test port.");
                    continue;
                }
                if (!seen.Add(index))
                {
                    result.Rejections.Add("Port index " + index + " is registered more than once.");
                    candidates.RemoveAll(item => item.Index == index);
                    continue;
                }
                // Island performance switching can deactivate a distant port
                // after registration; a loaded scene is the stable check.
                if (!port.gameObject.scene.IsValid() || !port.gameObject.scene.isLoaded || !port.enabled)
                {
                    result.Rejections.Add("Port " + index + " is disabled or outside the loaded scene.");
                    continue;
                }
                if (index < 0 || index >= Port.ports.Length || !ReferenceEquals(Port.ports[index], port))
                {
                    result.Rejections.Add("Port " + index + " does not match its Port.ports registration.");
                    continue;
                }
                var island = FindIsland(port.transform);
                if (island == null)
                {
                    result.Rejections.Add("Port " + index + " is not inside an IslandHorizon island.");
                    continue;
                }
                var reference = AuthoredReference(port, island);
                var berths = registered.Where(item => item != null && item.parentPort == port &&
                    item.gameObject.scene.IsValid() && item.gameObject.scene.isLoaded && item.enabled &&
                    item.boatPos != null && item.mooringFront != null && item.mooringBack != null).ToArray();
                // A port with no RecoveryPort at all is a "no-recovery port"; an
                // incomplete or duplicated recovery berth is still rejected.
                var any = registered.Count(item => item != null && item.parentPort == port &&
                    item.gameObject.scene.IsValid() && item.gameObject.scene.isLoaded);
                if (berths.Length > 1 || berths.Length == 0 && any > 0 && reference == null)
                {
                    result.Rejections.Add("Port " + index + " has " + berths.Length + " complete of " + any + " recovery berths; expected one, or none at all.");
                    continue;
                }
                var name = port.GetPortName();
                candidates.Add(new PortEntry
                {
                    Port = port, Identity = new Identity(index, port.gameObject.name), Recovery = berths.Length == 1 ? berths[0] : null, Island = island,
                    PlacementReference = berths.Length == 1 ? berths[0] : reference,
                    DisplayName = string.IsNullOrEmpty(name) ? port.gameObject.name : name
                });
            }
            foreach (var entry in candidates)
            {
                var sharing = Port.ports.Where(port => port != null && !IsTestPort(port) && port.enabled &&
                    port.gameObject.scene.IsValid() && port.gameObject.scene.isLoaded &&
                    FindIsland(port.transform) == entry.Island).Distinct().Count();
                if (sharing != 1)
                {
                    result.Rejections.Add("Port " + entry.Index + " shares island " + entry.Island.name + " with " +
                        (sharing - 1) + " other registered port(s).");
                    continue;
                }
                result.Ports.Add(entry);
            }
            result.Ports.Sort((left, right) => left.Index.CompareTo(right.Index));
            AddIslandHolds(result);
            return result;
        }

        // Known islands without a Port, after the ports. An island that has
        // gained a Port is left to it.
        private static void AddIslandHolds(PortCatalog result)
        {
            var islands = Resources.FindObjectsOfTypeAll<IslandHorizon>().Where(item => item != null &&
                item.gameObject.scene.IsValid() && item.gameObject.scene.isLoaded).ToArray();
            foreach (var hold in KnownIdentities.IslandHolds)
            {
                var matches = islands.Where(item => item.islandIndex == hold.Island.Index &&
                    string.Equals(item.name, hold.Island.Name, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1)
                {
                    result.Rejections.Add(hold.DisplayName + ": " + matches.Length + " loaded islands match " + hold.Island + "; expected one.");
                    continue;
                }
                var island = matches[0];
                if (Port.ports != null && Port.ports.Any(port => port != null && FindIsland(port.transform) == island))
                {
                    result.Rejections.Add(hold.DisplayName + ": island " + hold.Island + " has a registered port.");
                    continue;
                }
                result.Ports.Add(new PortEntry { Island = island, Hold = hold, Identity = hold.Island, DisplayName = hold.DisplayName });
            }
        }

        // Saffron's dormant child has a usable authored pose, but no native berth.
        // Reject ambiguous references and never activate or register their objects.
        private static RecoveryPort AuthoredReference(Port port, IslandHorizon island)
        {
            if (!new Identity(port.portIndex, port.gameObject.name).Equals(KnownIdentities.Saffron)) return null;
            var references = island.GetComponentsInChildren<RecoveryPort>(true).Where(item => item != null &&
                item.parentPort == port && FindIsland(item.transform) == island && item.boatPos != null &&
                FindIsland(item.boatPos) == island && item.gameObject.scene.IsValid() && item.gameObject.scene.isLoaded &&
                item.boatPos.gameObject.scene.IsValid() && item.boatPos.gameObject.scene.isLoaded &&
                HullFootprint.Finite(item.boatPos.position) && HullFootprint.Finite(item.boatPos.rotation) &&
                HullFootprint.Flat(item.boatPos.forward).IsFinite &&
                HullFootprint.Flat(item.boatPos.forward).Length >= 0.5f &&
                HullFootprint.Flat(item.boatPos.TransformDirection(item.goLeft ? Vector3.left : Vector3.right)).IsFinite &&
                HullFootprint.Flat(item.boatPos.TransformDirection(item.goLeft ? Vector3.left : Vector3.right)).Length >= 0.5f)
                .Distinct().ToArray();
            return references.Length == 1 ? references[0] : null;
        }

        // GetComponentInParent skips inactive parents in this Unity version;
        // walk the hierarchy so a performance-hidden island still resolves.
        internal static IslandHorizon FindIsland(Transform transform)
        {
            for (var current = transform; current != null; current = current.parent)
            {
                var island = current.GetComponent<IslandHorizon>();
                if (island != null) return island;
            }
            return null;
        }
    }
}
