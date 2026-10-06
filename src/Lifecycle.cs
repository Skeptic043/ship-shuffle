using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ShipShuffle
{
    // New game: generate after the starter boat is owned. Load: re-apply the
    // stored moves, because SaveableObject.Load never restores unpurchased
    // boats and they reappear at their authored berths on every load.
    internal static class Lifecycle
    {
        private const float NewGameTimeoutSeconds = 180f;
        private static readonly FieldInfo WaitingForFInputField = AccessTools.Field(typeof(StartMenu), "waitingForFInput");
        private static int generation;

        internal static void Cancel()
        {
            ++generation;
            LeopardSaleRig.Cancel();
        }

        [HarmonyPatch(typeof(StartMenu), "StartNewGame")]
        private static class NewGamePatch
        {
            // GameState.modData is static and survives into a new game.
            [HarmonyPriority(Priority.First)]
            private static void Prefix()
            {
                var plugin = Plugin.Instance;
                if (plugin == null || !plugin.isActiveAndEnabled || plugin.Settings == null) return;
                try
                {
                    Cancel();
                    if (GameState.modData != null && GameState.modData.Remove(AssignmentStore.Key))
                        Plugin.Instance?.DebugLog("New game: cleared stale Ship Shuffle assignment data.");
                }
                catch (Exception exception)
                {
                    Plugin.Instance?.Error("New game: stale assignment data could not be cleared.", exception);
                }
            }

            // Runs even when Scrambled Seas' prefix skips the original.
            private static void Postfix(StartMenu __instance)
            {
                var plugin = Plugin.Instance;
                if (plugin == null) return;
                if (!plugin.Settings.Enabled.Value)
                {
                    plugin.Report("Ship Shuffle is disabled; sale boats stay at their native berths.");
                    return;
                }
                if (WaitingForFInputField == null || WaitingForFInputField.FieldType != typeof(bool))
                {
                    plugin.Warn("StartMenu.waitingForFInput was not found; new-game generation skipped.");
                    return;
                }
                try
                {
                    plugin.Run(AwaitNewGame(__instance, generation));
                }
                catch (Exception exception)
                {
                    plugin.Error("New-game generation could not be scheduled.", exception);
                }
            }
        }

        [HarmonyPatch(typeof(SaveLoadManager), "LoadGame")]
        private static class LoadGamePatch
        {
            // A save without modData leaves the previous session's static
            // dictionary in place; never apply another session's moves.
            [HarmonyPriority(Priority.First)]
            private static void Prefix()
            {
                var plugin = Plugin.Instance;
                if (plugin == null || !plugin.isActiveAndEnabled || plugin.Settings == null) return;
                try
                {
                    Cancel();
                    GameState.modData?.Remove(AssignmentStore.Key);
                }
                catch (Exception exception)
                {
                    Plugin.Instance?.Error("Load: stale assignment data could not be cleared.", exception);
                }
            }
        }

        [HarmonyPatch(typeof(SaveLoadManager), "LoadModData")]
        [HarmonyAfter(Plugin.ScrambledSeasId)]
        private static class LoadModDataPatch
        {
            private static void Postfix()
            {
                try
                {
                    ApplyStored();
                }
                catch (Exception exception)
                {
                    Plugin.Instance?.Error("Load: stored Ship Shuffle assignments could not be applied; affected boats stay native.", exception);
                }
            }
        }

        private static IEnumerator AwaitNewGame(StartMenu menu, int token)
        {
            var started = Time.realtimeSinceStartup;
            while (true)
            {
                if (token != generation) yield break;
                if (menu == null)
                {
                    Plugin.Instance?.Warn("New game: start menu disappeared before the starter boat was owned; generation skipped.");
                    yield break;
                }
                if (GameState.currentlyLoading)
                {
                    Plugin.Instance?.Warn("New game: a save started loading; generation skipped.");
                    yield break;
                }
                if (GameState.playing || (bool)WaitingForFInputField.GetValue(menu)) break;
                if (Time.realtimeSinceStartup - started > NewGameTimeoutSeconds)
                {
                    Plugin.Instance?.Warn("New game: timed out waiting for the start prompt; sale boats stay native.");
                    yield break;
                }
                yield return null;
            }
            try
            {
                GenerateNewGame();
            }
            catch (Exception exception)
            {
                Plugin.Instance?.Error("New game: generation failed; sale boats stay native.", exception);
            }
        }

        private static void GenerateNewGame()
        {
            var plugin = Plugin.Instance;
            plugin.DebugLog("New game: starting fleet generation (playing " + GameState.playing + ", Scrambled Seas " +
                plugin.ScrambledSeasInstalled + ", New Beginnings " + plugin.NewBeginningsInstalled + ").");
            if (GameState.modData == null)
            {
                plugin.Warn("New game: GameState.modData is unavailable, so moves could not be saved; sale boats stay native.");
                return;
            }
            var document = FleetGenerator.GenerateNewGame(plugin.Settings);
            GameState.modData[AssignmentStore.Key] = AssignmentStore.Format(document);
            plugin.DebugLog("New game: stored " + document.Records.Count + " assignment(s) in GameState.modData[\"" +
                AssignmentStore.Key + "\"].");
        }

        private static void ApplyStored()
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return;
            if (GameState.modData == null || !GameState.modData.TryGetValue(AssignmentStore.Key, out var text))
            {
                plugin.DebugLog("Load: no Ship Shuffle data in this save; sale boats left native.");
                return;
            }
            if (!plugin.Settings.Enabled.Value)
            {
                plugin.Report("Load: Ship Shuffle is disabled; stored assignments ignored and kept in the save.");
                return;
            }
            var errors = new List<string>();
            var document = AssignmentStore.Parse(text, errors);
            if (document == null)
            {
                var reason = errors.Count > 0 ? errors[0].TrimEnd('.') : "unknown format";
                plugin.Warn("Load: this save's Ship Shuffle layout could not be read (" + reason + "); sale boats stay at their original ports.");
                return;
            }
            foreach (var error in errors) plugin.Warn("Load: " + error);
            FleetGenerator.ApplyStored(document);
        }
    }
}
