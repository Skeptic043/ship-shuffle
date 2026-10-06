# Ship Shuffle

Randomize boat spawns for a different experience in a fresh save.

## Install

### Mod managers

Install Ship Shuffle through [r2modman](https://github.com/ebkr/r2modmanPlus) or [Thunderstore Mod Manager](https://get.thunderstore.io/), then launch Sailwind through your manager. BepInEx is installed automatically. [BepInEx Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager) is optional and gives you an in-game settings menu.

### Manual installation

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) in your Sailwind game folder, following its installation instructions.
2. Download and extract the [Ship Shuffle ZIP](https://github.com/Skeptic043/ship-shuffle/releases). Copy its `BepInEx/plugins/ShipShuffle` folder into `BepInEx/plugins` in your Sailwind folder or mod profile.
3. Launch Sailwind.

## How it works

Each new game draws from your enabled boats and selected destinations, configurable in the config file. Selected destinations take priority, with boats being spread across your selection first. Any leftover boats once your selected ports are filled are placed at non-selected ports by default. A boat rolling its default spawn is also a possibility. Boats are moored at a free, non-recovery berth with a little extra line. If there's no room on the dock, the boat waits offshore and is held in place until you buy it.

## Configuration

Open Ship Shuffle in [BepInEx Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager). The main controls cover boat groups, the seed and shared shipyards. Expand Advanced to choose individual boats and destinations, allow mod ports or enable diagnostics.

Seed, boat filters and destination choices apply to your next new game. `Enabled` also controls whether an existing layout is restored when you load its save.

### Main settings

| Setting | Default | Description |
| --- | --- | --- |
| Enabled | On | Shuffle new games and restore saved layouts. |
| Include vanilla boats | On | Include vanilla boats. |
| Include mod boats | On | Include detected mod boats. |
| Small boats | On | Include Small boats. |
| Medium boats | On | Include Medium boats. |
| Large boats | On | Include Large boats. |
| Multiple boats at shipyard ports | Off | Allow up to three participating boats at Gold Rock City, Dragon Cliffs and Fort Aestrin. |
| Randomize remaining boats | On | Shuffle leftover boats among unchecked compatible destinations. |
| Seed | `0` | Zero or negative numbers roll a new seed each new game. |

### Advanced

| Setting | Default | Description |
| --- | --- | --- |
| Boats | All detected types included | Choose individual boat types. Checked means included. |
| Ports | All eligible destinations included | Choose individual destinations. Checked means included. |
| Include secret destinations | Off | Show secret destinations. |
| Include mod ports | Off | Allow destinations added by mods. |
| Debug logging | Off | Write detailed placement information. While enabled, `Ctrl+F9` writes a boat and port report. |

Without Configuration Manager, close the game and edit `BepInEx/config/com.skeptic043.sailwind.shipshuffle.cfg` in your installation or profile. The file is created after the first launch.

## Saves and removing the mod

Each save remembers where each boat was placed. On load, Ship Shuffle puts the remaining boats left for sale back in the spots they were given. Ship Shuffle requires a new game and doesn't work on previously unshuffled saves.

If you disable or remove Ship Shuffle, unsold boats return to their original ports the next time you load. Disabling keeps the stored layout, so re-enabling restores the remaining sale boats on the next load. Adding a boat mod to an existing save doesn't reshuffle it either, so newly added boats stay at their original locations until you start a new game.

## Compatibility and limitations

Boats added by mods are detected automatically, but a boat that can't be moved stays at its original location. Mod boats are grouped by measured hull dimensions. Small hulls are at most 13 m long and 4.4 m across, Medium hulls are at most 26 m long and 6.8 m across, and larger hulls belong to the Large group.

- Large boats aren't sent to Sage Hills, Mirage Mountain, Aestra Abbey or Old Ankh Town. [HMS Leopard](https://github.com/winterspices/HMSLeopard) isn't sent to Neverdin.
- An empty HMS Leopard for sale receives one furled medium lateen on its mainmast.

## AI Use

AI was used to write all of the code in this project. The original concept, design direction, testing, debugging, and release decisions are my own. If you prefer not to use mods developed with AI assistance, I understand and respect that choice.

## Issues and links

[Report an issue](https://github.com/Skeptic043/ship-shuffle/issues)

[Source code](https://github.com/Skeptic043/ship-shuffle) · [Release notes](https://github.com/Skeptic043/ship-shuffle/blob/HEAD/CHANGELOG.md) · [Build instructions](https://github.com/Skeptic043/ship-shuffle/blob/HEAD/BUILD.md) · [MIT License](https://github.com/Skeptic043/ship-shuffle/blob/HEAD/LICENSE) · [Support on Ko-fi](https://ko-fi.com/skeptic043) · skeptic043
