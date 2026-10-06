using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ShipShuffle
{
    // Live snapshot of the sale boats and ports used for one generation or load.
    internal sealed class FleetWorld
    {
        internal BoatCatalog KnownBoats;
        internal PortCatalog KnownPorts;
        internal readonly List<FleetPort> Ports = new List<FleetPort>();
        internal readonly List<DockSite> Sites = new List<DockSite>();
        // Unpurchased sale boats only: catalogued (Tag BoatEntry) and unusable (Tag UnusableBoat).
        internal readonly List<FleetBoat> Boats = new List<FleetBoat>();
        private const float HomeRadius = 400f;

        internal string PortKey(int port) => port >= 0 && port < Ports.Count ? Ports[port].Id.ToString() : null;
        internal static string CleatKey(Transform cleat) => cleat != null ? cleat.GetInstanceID().ToString() : null;

        internal static FleetWorld Survey()
        {
            var world = new FleetWorld { KnownBoats = BoatCatalog.Discover(), KnownPorts = PortCatalog.Discover() };
            foreach (var port in world.KnownPorts.Ports)
            {
                BerthResolver.TryBuildSite(port, out var site, out var reason);
                world.Sites.Add(site);
                world.Ports.Add(new FleetPort
                {
                    Id = port.Identity, Label = port.Label,
                    HasSite = site != null, SiteReason = reason, Tag = port,
                    Vanilla = KnownIdentities.IsVanillaPort(port.Identity)
                });
            }
            foreach (var entry in world.KnownBoats.Boats.Where(item => !item.Purchased))
                world.Boats.Add(new FleetBoat
                {
                    Id = entry.Identity, Label = entry.Label, Size = entry.Size, Vanilla = entry.Vanilla,
                    Home = world.HomeOf(entry.Ropes, entry.Saveable.transform.position),
                    Claims = Claims(entry.Ropes), Tag = entry
                });
            foreach (var unusable in world.KnownBoats.Unusable)
            {
                var ropes = unusable.Saveable.GetComponent<BoatMooringRopes>();
                world.Boats.Add(new FleetBoat
                {
                    Id = new Identity(unusable.Index, unusable.Boat.gameObject.name), Label = unusable.Label,
                    Catalogued = false, CatalogReason = unusable.Reason,
                    Home = world.HomeOf(ropes, unusable.Saveable.transform.position), Claims = Claims(ropes), Tag = unusable
                });
            }
            return world;
        }

        // The island holding the authored berth, else the nearest recovery
        // marker within 400 m.
        internal int HomeOf(BoatMooringRopes ropes, Vector3 position)
        {
            if (ropes != null)
                foreach (var mooring in new[] { ropes.mooringFront, ropes.mooringBack })
                {
                    if (mooring == null) continue;
                    var island = PortCatalog.FindIsland(mooring);
                    var index = island != null ? KnownPorts.Ports.FindIndex(port => port.Island == island) : -1;
                    if (index >= 0) return index;
                }
            var flat = HullFootprint.Flat(position);
            var best = -1;
            var bestDistance = HomeRadius;
            for (var i = 0; i < KnownPorts.Ports.Count; ++i)
            {
                if (KnownPorts.Ports[i].NoRecovery) continue;
                var distance = Vec2.Distance(HullFootprint.Flat(KnownPorts.Ports[i].Recovery.boatPos.position), flat);
                if (distance <= bestDistance) { best = i; bestDistance = distance; }
            }
            return best;
        }

        private static string[] Claims(BoatMooringRopes ropes) =>
            ropes == null ? new string[0] : new[] { ropes.mooringFront, ropes.mooringBack }
                .Where(item => item != null).Select(CleatKey).Distinct().ToArray();

        internal IEnumerable<BoatEntry> SaleBoats => KnownBoats.Boats;
        internal FleetBoat FindBoat(Identity id) => Boats.FirstOrDefault(item => item.Catalogued && item.Id.Equals(id));
        internal int FindPort(Identity id) => Ports.FindIndex(item => item.Id.Equals(id));
    }
}
