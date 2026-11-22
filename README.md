# Ship Shuffle

Randomize boat spawns for a different experience in a fresh save.

## Install

### Mod managers

Install Ship Shuffle through [Sailwind Mod Synchronizer](https://github.com/foxyv/SailwindModSynchronizer/releases/latest), r2modman or Thunderstore Mod Manager, then launch Sailwind through your manager. Dependencies are installed automatically.

### Manual installation

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) in your Sailwind game folder, following its installation instructions.
2. Download and extract the [Ship Shuffle ZIP](https://github.com/Skeptic043/ship-shuffle/releases/latest). Copy its `BepInEx/plugins/ShipShuffle` folder into `BepInEx/plugins` in your Sailwind folder.
3. Launch Sailwind normally.

## How it works

Each new game draws from your enabled boats and selected destinations, configurable in the config file. Selected destinations take priority, with boats being spread across your selection first. Any leftover boats once your selected ports are filled are placed at non-selected ports by default. A boat rolling its default spawn is also a possibility. Boats are moored at a free, non-recovery berth. If there's no room on the dock, the boat waits offshore and is held in place until you buy it.

## Configuration

After the first launch with the mod installed, you can adjust settings in `BepInEx/config/com.skeptic043.sailwind.shipshuffle.cfg` or they can be adjusted in game with the [BepInEx 5 version of Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager/releases). Changes made in Configuration Manager apply immediately. Seed, boat filters and destination choices apply to your next new game.

| Setting | Default | Description |
| --- | --- | --- |
| Enabled | On | Shuffle new games and restore saved layouts. |
| Shuffle newly added boats | Off | Use the current boat and destination settings to shuffle newly added sale boats when loading an already shuffled save. |
| Include vanilla boats | On | Include vanilla boats. |
| Include mod boats | On | Include detected mod boats. |
| Small boats | On | Include Small boats. |
| Medium boats | On | Include Medium boats. |
| Large boats | On | Include Large boats. |
| Multiple boats at shipyard ports | Off | Allow up to three participating boats at Gold Rock City, Dragon Cliffs and Fort Aestrin. |
| Randomize remaining boats | On | Shuffle leftover boats among unchecked compatible destinations. |
| Seed | `0` | Zero or negative numbers roll a new seed each new game. |
| Boats | All detected types included | Choose individual boat types. Checked means included. |
| Ports | All eligible destinations included | Choose individual destinations. Checked means included. |
| Include secret destinations | Off | Show secret destinations. |
| Include mod ports | Off | Allow destinations added by mods. |
| Debug logging | Off | Write detailed placement information. While enabled, `Ctrl+F9` writes a boat and port report. |

## Saves and removing the mod

Each save remembers where each boat was placed. On load, Ship Shuffle puts the remaining boats left for sale back in the spots they were given. If you disable or remove Ship Shuffle, unsold boats return to their original ports the next time you load. Disabling keeps the stored layout, so re-enabling the mod restores the boats still up for sale to their shuffled locations on the next load.

## Compatibility and limitations

No known incompatibilities.

Boats added by mods are detected automatically, but a boat that can't be moved stays at its original location. Mod boats are grouped by measured hull dimensions. Small hulls are at most 13 m long and 4.4 m across, Medium hulls are at most 26 m long and 6.8 m across, and larger hulls belong to the Large group.

- Large boats aren't sent to Sage Hills, Mirage Mountain, Aestra Abbey or Old Ankh Town.
- An empty HMS Leopard for sale receives one furled medium lateen on its mainmast.

## AI Use

AI was used to write all of the code in this project. The original concept, design direction, testing, debugging, and release decisions are my own. If you prefer not to use mods developed with AI assistance, I understand and respect that choice.

## Links

[Report an issue](https://github.com/Skeptic043/ship-shuffle/issues) · [Source code](https://github.com/Skeptic043/ship-shuffle) · [Release notes](https://github.com/Skeptic043/ship-shuffle/blob/main/CHANGELOG.md) · [Build instructions](https://github.com/Skeptic043/ship-shuffle/blob/main/BUILDING.md) · [License](https://github.com/Skeptic043/ship-shuffle/blob/main/LICENSE) · [Support on Ko-fi](https://ko-fi.com/skeptic043) · skeptic043
