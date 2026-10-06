# Testing Ship Shuffle

Use a separate test save when checking placement or changing your boat selection.

1. Start a new game and check that your starting boat stays where it belongs. Confirm that sale boats appear at the selected destinations when suitable boats and berths are available.
2. Change boat and port choices in Advanced. Check Apply, Cancel, search and Refresh, then start another new game to check the selection.
3. Visit a moored sale boat and one held offshore. Check their position and mooring, then buy a boat and confirm you can sail it away.
4. Save and reload. Remaining sale boats should return to their assigned spots, while your purchased boat keeps its ownership and saved position.
5. Change the next-game filters and reload the same save. Its stored layout should still restore. Turn off `Enabled` before loading to check the return to native sale locations, then re-enable it and reload to restore the layout.
6. With Multiple boats at shipyard ports enabled, check that boats spread across selected ports before using the extra shipyard places, with at most one participating Large at each shared shipyard.

For a problem report, keep the mod version, seed, boat, destination and steps to reproduce. Turn on `DebugLogging` for a reproduction and include `BepInEx/LogOutput.log` and a screenshot of the affected berth or control.
