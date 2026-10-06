using System;
using System.Collections.Generic;
using System.Linq;

namespace ShipShuffle
{
    // A stock Sailwind sale boat: exact identity, size class and display name.
    internal sealed class VanillaBoat
    {
        internal Identity Identity;
        internal BoatSize Size;
        internal string DisplayName;
    }

    // Every fixed boat and port identity the mod knows, with the per-port
    // rules attached to them. Unity-free; nothing here is read from config.
    internal static class KnownIdentities
    {
        // ------------------------------------------------------------ boats

        internal static readonly Identity Leopard = new Identity(207, "BOAT LEOPARD (207)(Clone)");
        // Ships with the Leopard; never sold separately, never moved.
        internal static readonly Identity LeopardCompanion = new Identity(212, "BOAT CUTTER (212)(Clone)");

        private static readonly VanillaBoat[] VanillaBoats =
        {
            Stock(10, "BOAT dhow small (10)", BoatSize.Small, "Dhow"),
            Stock(20, "BOAT dhow medium (20)", BoatSize.Medium, "Sanbuq"),
            Stock(30, "BOAT dhow large (30)", BoatSize.Large, "Baghlah (Bigbuq)"),
            Stock(40, "BOAT medi small (40)", BoatSize.Small, "Cog"),
            Stock(50, "BOAT medi medium (50)", BoatSize.Medium, "Brig"),
            Stock(70, "BOAT junk large (70)", BoatSize.Large, "Jong"),
            Stock(80, "BOAT junk medium (80)", BoatSize.Medium, "Junk"),
            Stock(90, "BOAT junk small singleroof(90)", BoatSize.Small, "Kakam")
        };

        private static VanillaBoat Stock(int index, string name, BoatSize size, string displayName) =>
            new VanillaBoat { Identity = new Identity(index, name), Size = size, DisplayName = displayName };

        // The stock boat at this scene index with this runtime name (case-insensitive), or null.
        internal static VanillaBoat VanillaBoat(int index, string name) => VanillaBoats.FirstOrDefault(boat =>
            boat.Identity.Index == index && string.Equals(boat.Identity.Name, name, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------ ports

        internal static readonly Identity GoldRock = new Identity(0, "port A0 (Gold Rock)");
        internal static readonly Identity Neverdin = new Identity(2, "port A2 (Neverdin)");
        internal static readonly Identity Oasis = new Identity(6, "port A/M 6 (Oasis)");
        internal static readonly Identity DragonCliffs = new Identity(9, "port E 9 (Dragon cliffs)");
        internal static readonly Identity SageHills = new Identity(13, "port E 13 Sage Hills");
        internal static readonly Identity Fort = new Identity(15, "port M 15 Fort");
        internal static readonly Identity SirenSong = new Identity(18, "port M 18 Siren Song");
        internal static readonly Identity Chronos = new Identity(21, "port 21 M chronos");
        internal static readonly Identity Monastery = new Identity(26, "port M 26 (Monastery)");
        internal static readonly Identity FeyValley = new Identity(27, "port M 27 (Valley)");
        internal static readonly Identity DeadCove = new Identity(30, "port E 30 (swamp)");
        internal static readonly Identity Coffee = new Identity(31, "port A 31 (coffee)");
        internal static readonly Identity MirageMountain = new Identity(32, "port A 32 (mirage mountain)");
        internal static readonly Identity Saffron = new Identity(33, "port A 33 (flower)");
        // Sailwind's developer test port; never catalogued (name matched case-insensitively).
        internal static readonly Identity TestPort = new Identity(7, "port 7 (test port)");

        // Sailwind's own ports (index + exact name), from New Beginnings'
        // KnownPorts plus Onna. Anything else is a mod-added port.
        internal static readonly HashSet<Identity> VanillaPorts = new HashSet<Identity>
        {
            GoldRock, new Identity(1, "port A1 (Alnilem)"), Neverdin,
            new Identity(3, "port A3 (Fish Island)"), new Identity(4, "port A4 (alchemist)"), new Identity(5, "port A5 (Academy)"),
            Oasis, DragonCliffs, new Identity(10, "port E 10 sanctuary"),
            new Identity(11, "port E 11 crab beach"), new Identity(12, "port E 12 New Port"), SageHills,
            new Identity(14, "port E 14 Serpent Isle"), Fort, new Identity(16, "port M 16 Sunspire"),
            new Identity(17, "port M 17 Mount Malefic"), SirenSong, new Identity(19, "port M 19 (Eastwind)"),
            new Identity(20, "port M/E 20 Happy Bay"), Chronos, new Identity(22, "port L 22 Lagoon Bay"),
            new Identity(23, "port L 23 Lagoon Fire Fish Town"), new Identity(24, "port L 24 Lagoon Onna"),
            new Identity(25, "port L 25 Lagoon Senna"), Monastery, FeyValley,
            new Identity(28, "port M 28 (Cave)"), new Identity(29, "port E 29 (jungle)"), DeadCove,
            Coffee, MirageMountain, Saffron
        };

        internal static bool IsVanillaPort(Identity port) => VanillaPorts.Contains(port);

        // Gold Rock City, Dragon Cliffs and Fort Aestrin hold up to
        // ShipyardCapacity sale boats when MultipleBoatsAtShipyards is on.
        internal static readonly HashSet<Identity> Shipyards = new HashSet<Identity> { GoldRock, DragonCliffs, Fort };
        internal const int ShipyardCapacity = 3;

        // Limited room for large boats (New Beginnings' README list: Sage
        // Hills, Mirage Mountain, Aestra Abbey, Old Ankh Town).
        internal static readonly HashSet<Identity> LargeBoatExcludedPorts = new HashSet<Identity> { SageHills, MirageMountain, Monastery, Coffee };

        // Boat/port pairs to avoid: HMS Leopard grounds at Neverdin.
        internal static readonly HashSet<Tuple<Identity, Identity>> BoatPortExclusions = new HashSet<Tuple<Identity, Identity>>
        {
            Tuple.Create(Leopard, Neverdin)
        };

        // Secret destinations, offered only with IncludeHiddenPorts.
        internal static bool IsHiddenPort(Identity port) => port.Equals(Chronos) || port.Equals(Saffron);
        internal static bool IsHiddenPortName(string name) =>
            string.Equals(name, Chronos.Name, StringComparison.Ordinal) || string.Equals(name, Saffron.Name, StringComparison.Ordinal);

        // Placement priority only: larger boats remain eligible when needed.
        internal static BoatSize? PreferredMaximumSize(Identity port) => port.Equals(DeadCove) ? BoatSize.Medium : (BoatSize?)null;

        // Fey's short quay faces shoreline beyond the modeled hull capsule.
        // Shuffled arrivals use open water; native boats staying home are untouched.
        internal static bool OffshoreOnly(Identity port) => port.Equals(FeyValley);

        // Siren Song holds arrivals one hull length farther out; Dead Cove keeps
        // its original heading, two hull widths left and three quarters back.
        internal static StationPolicy StationPolicyFor(Identity port)
        {
            if (port.Equals(SirenSong))
                return new StationPolicy { OutwardHullLengths = 1f };
            if (port.Equals(DeadCove))
                return new StationPolicy { ForwardOnly = true, OutwardHullLengths = 0.25f, LeftHullWidths = 2f, BackHullLengths = 0.75f };
            return default;
        }

        // The water gap on the far side of Mirage's middle islet, mapped from
        // the current authored terrain and the native recovery marker.
        internal static StationRegion? StationRegionFor(Identity port) => port.Equals(MirageMountain)
            ? new StationRegion { MinimumRight = 38f, MaximumRight = 50f, MinimumForward = 0f, MaximumForward = 24f,
                TargetRight = 44f, TargetForward = 10f } : (StationRegion?)null;

        // Oasis's native sale side: the Baghlah's bow points from cleat 7 toward 5.
        internal const string OasisSaleFrontCleat = "dock_mooring (5)#1";
        internal const string OasisSaleBackCleat = "dock_mooring (7)#1";
        internal static bool NativeSaleHeading(Identity port) => port.Equals(Oasis);
    }
}
