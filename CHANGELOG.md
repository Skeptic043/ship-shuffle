# Changelog

## 1.0.4

### Added

- Add `Shuffle newly added boats`, off by default. With the setting enabled, newly added boats can be shuffled to free locations when you load a previously shuffled save (thanks to hydro on Discord for the suggestion)

## 1.0.3

### Added
- Boats can now be shuffled to Isle of Clear Mind, Lion's Fang, Oracle Island and Pondering Peak, plus two additional secret islands (secret islands are disabled by default and can be enabled in the config)

## 1.0.2

### Changed
- A shuffled boat's state is no longer checked every frame prior to visiting the port it was placed at
- Ship Shuffle no longer checks winches every frame for an entire session

## 1.0.1

### Changed
- Allow HMS Leopard at Neverdin, using the same placement checks as other large boats
- Use the game's normal sale-boat mooring lengths instead of adding extra slack

### Fixed
- Keep shuffled boats locked in place until you're close to them, then recheck the berth against the loaded scenery and move them to another clear berth at the same port if needed

## 1.0.0

### Initial Release
- Shuffle boats for sale between ports when starting a new game, with support for detected mod boats
- Choose boat types, size groups and destinations through the Advanced settings menu, or use a fixed seed to repeat a layout
- Fill selected destinations first, with options to shuffle remaining boats and allow up to three participating boats at the main shipyards
