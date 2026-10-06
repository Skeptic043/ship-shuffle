using System;
using System.Collections;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

[assembly: System.Reflection.AssemblyVersion(ShipShuffle.Plugin.Version)]
[assembly: System.Reflection.AssemblyFileVersion(ShipShuffle.Plugin.Version)]

namespace ShipShuffle
{
    [BepInPlugin(Id, "Ship Shuffle", Version)]
    [BepInDependency(ScrambledSeasId, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(NewBeginningsId, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.skeptic043.sailwind.shipshuffle";
        // Bump on every user-facing build.
        public const string Version = "1.0.0";
        internal const string ScrambledSeasId = "com.nandbrew.scrambledseas";
        internal const string NewBeginningsId = "com.skeptic043.sailwind.newbeginnings";

        private Harmony harmony;
        internal static Plugin Instance { get; private set; }
        internal ShuffleConfig Settings { get; private set; }
        internal bool ScrambledSeasInstalled { get; private set; }
        internal bool NewBeginningsInstalled { get; private set; }

        private void Awake()
        {
            Instance = this;
            try
            {
                Settings = new ShuffleConfig(Config);
                ScrambledSeasInstalled = Chainloader.PluginInfos.ContainsKey(ScrambledSeasId);
                NewBeginningsInstalled = Chainloader.PluginInfos.ContainsKey(NewBeginningsId);
                harmony = new Harmony(Id);
                harmony.PatchAll(typeof(Plugin).Assembly);
                Report("Ship Shuffle " + Version + " (build " + BuildInfo.Utc + ") loaded. Enabled: " + Settings.Enabled.Value +
                    "; Scrambled Seas installed: " + ScrambledSeasInstalled +
                    "; New Beginnings installed: " + NewBeginningsInstalled + ".");
            }
            catch (Exception exception)
            {
                // A changed Sailwind method may fail after earlier patches were
                // installed. Leave sale boats native rather than half-patched.
                Instance = null;
                enabled = false;
                try { harmony?.UnpatchSelf(); }
                catch (Exception unpatchException)
                {
                    Logger.LogError("Ship Shuffle could not remove its partial patches: " + unpatchException);
                }
                Logger.LogError("Ship Shuffle disabled after initialization failed: " + exception);
            }
        }

        private void Update()
        {
            try
            {
                if (Settings == null || !GameState.playing || GameState.currentlyLoading) return;
                if (DebugEnabled && ShuffleConfig.DumpShortcut.IsDown()) Diagnostics.Dump();
            }
            catch (Exception exception)
            {
                Error("Diagnostics dump failed.", exception);
            }
        }

        internal Coroutine Run(IEnumerator routine) => StartCoroutine(routine);

        internal void Report(string message) => Logger.LogInfo(message);

        internal bool DebugEnabled => Settings != null && Settings.DebugLogging.Value;

        // Detail is emitted at Info, prefixed, so it reaches the default
        // BepInEx log levels; DebugLogging (off by default) gates it.
        internal void DebugLog(string message)
        {
            if (DebugEnabled) Logger.LogInfo("[detail] " + message);
        }

        internal void Warn(string message) => Logger.LogWarning(message);
        internal void Error(string message, Exception exception) => Logger.LogError(message + " " + exception);

        private void OnDestroy()
        {
            if (!ReferenceEquals(Instance, this)) return;
            Instance = null;
            try
            {
                Lifecycle.Cancel();
                StopAllCoroutines();
                BoatRelocator.RestoreAllGuards();
            }
            catch (Exception exception) { Logger.LogError("Ship Shuffle shutdown cleanup failed: " + exception); }
            finally
            {
                try { harmony?.UnpatchSelf(); }
                catch (Exception exception) { Logger.LogError("Ship Shuffle could not remove its patches: " + exception); }
            }
        }
    }
}
