using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ShipShuffle
{
    // Adapted from New Beginnings LeopardStarterRig.cs at
    // 1aba5a09148f9f4a44fe7b754848d93905b617a1, Skeptic043/sailwind-new-beginnings.
    // Leopard 1.6.0 ships without sails. An admitted unsold Leopard receives
    // this rig at its native or shuffled position. Owned customization saves it later.
    internal static class LeopardSaleRig
    {
        private const string MastPath = "boat leopard/structure_container/MAINMAST/mainmast";
        private const int SailIndex = 60;
        private const string SailName = "60 SAIL Am lateen mid";
        private static readonly MethodInfo AttachInitialSail = AccessTools.Method(typeof(Mast),
            "AttachInitialSail", new[] { typeof(GameObject), typeof(float) });
        private static readonly MethodInfo UpdateSailUnroll = AccessTools.Method(typeof(RopeControllerSailReef),
            "UpdateSailUnroll", Type.EmptyTypes);
        private static readonly FieldInfo VisualAnimator = AccessTools.Field(typeof(ReefEffectAnimUniversal), "anim");
        private static readonly FieldInfo VisualRefreshing = AccessTools.Field(typeof(ReefEffectAnimUniversal), "refreshing");
        private static readonly FieldInfo WinchClicked = AccessTools.Field(typeof(GoPointerButton), "isClicked");
        private static readonly FieldInfo WinchStickyClickedBy = AccessTools.Field(typeof(GoPointerButton), "stickyClickedBy");
        private static GPButtonRopeWinch pendingWinch;
        private static ShipyardSailColChecker pendingChecker;
        private static BoatEntry pendingBoat;
        private static readonly HashSet<SaveableObject> attempted = new HashSet<SaveableObject>();
        private static int pendingRefreshGeneration;
        private static int generation;

        internal static void Cancel()
        {
            CancelPending();
            attempted.Clear();
        }

        private static void CancelPending()
        {
            ++generation;
            pendingWinch = null;
            pendingBoat = null;
            ClearClothRestart();
        }

        private static void ClearClothRestart()
        {
            pendingChecker = null;
        }

        [HarmonyPatch(typeof(ShipyardSailColChecker), "ParentToWalkColMast")]
        private static class ResumeClothAfterNativeReparent
        {
            private static void Postfix(ShipyardSailColChecker __instance)
            {
                RestartInterruptedCloth(__instance);
            }
        }

        private static void RestartInterruptedCloth(ShipyardSailColChecker checker)
        {
            if (!ReferenceEquals(checker, pendingChecker) || pendingChecker == null) return;
            var selected = pendingBoat;
            var token = pendingRefreshGeneration;
            // Consume before invoking native code: one attachment transition,
            // never a repair loop or a hook on later player sail changes.
            ClearClothRestart();
            try
            {
                if (selected == null || !StillSelected(selected, token) ||
                    !GameState.playing || GameState.currentlyLoading || pendingWinch == null) return;
                if (WinchInUse(pendingWinch))
                {
                    ReleaseForWinchInput(pendingWinch);
                    return;
                }
                var reef = pendingWinch.rope as RopeControllerSailReef;
                if (reef == null || reef.sail == null || reef.sail.shipRigidbody != selected.Body ||
                    !HasInitialFurlLength(reef.reverseReefing, reef.currentLength)) return;
                var visual = reef.sail.GetComponent<ReefEffectAnimUniversal>();
                if (visual == null || !visual.isActiveAndEnabled ||
                    !(VisualAnimator.GetValue(visual) is Animator animator) || animator == null ||
                    !(bool)VisualRefreshing.GetValue(visual)) return;
                // Native ParentToWalkColMast deactivates/reactivates the sail,
                // stopping Start's cloth coroutine without clearing refreshing.
                // Restart its own finite refresh after that exact transition.
                // If Start has not run, it remains responsible for initialization.
                visual.RefreshCloth();
                Plugin.Instance.DebugLog("Leopard sale sail: resumed native cloth refresh after initial collision-checker reparenting.");
            }
            catch (Exception exception)
            {
                Plugin.Instance?.Warn("Leopard native cloth refresh could not resume: " + exception.Message);
            }
        }

        [HarmonyPatch(typeof(GPButtonRopeWinch), "OnActivate")]
        private static class ReleaseOnWinchActivation
        {
            private static void Prefix(GPButtonRopeWinch __instance)
            {
                ReleaseForWinchInput(__instance);
            }
        }

        [HarmonyPatch(typeof(GPButtonRopeWinch), "Update")]
        private static class ReleaseOnWinchInput
        {
            private static void Prefix(GPButtonRopeWinch __instance)
            {
                if (!ReferenceEquals(__instance, pendingWinch) || pendingWinch == null) return;
                try
                {
                    if (WinchInUse(__instance)) ReleaseForWinchInput(__instance);
                }
                catch (Exception exception)
                {
                    CancelPending();
                    Plugin.Instance?.Warn("Leopard sale furl input check released pending initialization: " + exception.Message);
                }
            }
        }

        private static bool WinchInUse(GPButtonRopeWinch winch) =>
            (bool)WinchClicked.GetValue(winch) ||
            WinchStickyClickedBy.GetValue(winch) is GoPointer pointer && pointer != null ||
            winch.rotHandle != null && winch.rotHandle.IsGrabbed();

        private static void ReleaseForWinchInput(GPButtonRopeWinch winch)
        {
            // Startup release must never prevent the native winch from handling input.
            try
            {
                if (ReferenceEquals(winch, pendingWinch)) ReleaseToPlayer();
            }
            catch (Exception exception)
            {
                if (ReferenceEquals(winch, pendingWinch)) CancelPending();
                Plugin.Instance?.Warn("Leopard sale furl release needed fallback cancellation: " + exception.Message);
            }
        }

        private static void ReleaseToPlayer()
        {
            Plugin.Instance?.DebugLog("Leopard sale furl initialization released to player winch input.");
            CancelPending();
        }

        internal static void Arm(BoatEntry selected)
        {
            if (selected == null || selected.Saveable == null || selected.Boat == null ||
                !KnownIdentities.Leopard.Equals(new Identity(selected.Saveable.sceneIndex, selected.Saveable.name)) ||
                selected.Purchased || attempted.Contains(selected.Saveable) ||
                ReferenceEquals(pendingBoat?.Saveable, selected.Saveable)) return;
            CancelPending();
            pendingBoat = selected;
            var token = generation;
            try { Plugin.Instance.Run(InstallAfterStart(selected, token)); }
            catch (Exception exception)
            {
                CancelPending();
                Plugin.Instance?.Warn("Leopard sale sail could not be scheduled: " + exception.Message);
            }
        }

        [HarmonyPatch(typeof(PurchasableBoat), nameof(PurchasableBoat.PurchaseBoat))]
        private static class ReleaseOnPurchase
        {
            private static void Prefix(PurchasableBoat __instance)
            {
                // Release before native purchase code, including purchases that
                // throw after marking ownership. Existing sail equipment stays.
                if (ReferenceEquals(__instance, pendingBoat?.Boat)) CancelPending();
            }
        }

        private static bool StillSelected(BoatEntry selected, int token)
        {
            var plugin = Plugin.Instance;
            return token == generation && ReferenceEquals(selected, pendingBoat) &&
                plugin != null && plugin.isActiveAndEnabled &&
                selected.Saveable != null && selected.Boat != null && selected.Body != null &&
                KnownIdentities.Leopard.Equals(new Identity(selected.Saveable.sceneIndex, selected.Saveable.name)) &&
                selected.Saveable.gameObject == selected.Boat.gameObject && !selected.Purchased;
        }

        private static IEnumerator InstallAfterStart(BoatEntry selected, int token)
        {
            try
            {
                // The sale catalogue callback covers fresh and loaded boats,
                // including a Leopard left at its provider's native position.
                // Do not wake distance-hidden objects. Wait for normal activation
                // and for native reef loading overrides to end before attaching.
                while (true)
                {
                    if (!StillSelected(selected, token)) yield break;
                    if (GameState.playing && !GameState.currentlyLoading && selected.Boat.isActiveAndEnabled) break;
                    yield return null;
                }
                // Let the existing hull settling pass complete before creating a
                // hinged sail. Native attachment handles ropes, registration and angles.
                for (var frame = 0; frame < 4; ++frame) yield return new WaitForFixedUpdate();
                yield return new WaitForEndOfFrame();
                yield return new WaitForEndOfFrame();
                if (!StillSelected(selected, token) || !GameState.playing || GameState.currentlyLoading) yield break;
                // Awake/Start may be delayed until this island first becomes active.
                // Readiness gets a finite window of active time, never reactivation.
                var activeSeconds = 0f;
                var previous = Time.realtimeSinceStartup;
                while (true)
                {
                    if (!StillSelected(selected, token) || !GameState.playing || GameState.currentlyLoading) yield break;
                    var now = Time.realtimeSinceStartup;
                    if (selected.Boat.isActiveAndEnabled)
                    {
                        if (MainmastReady(selected)) break;
                        activeSeconds += Math.Max(0f, now - previous);
                        if (activeSeconds >= 3f)
                        {
                            Plugin.Instance.Warn("Leopard sale sail skipped: mainmast native initialization was not ready within the active initialization window.");
                            CancelPending();
                            yield break;
                        }
                    }
                    previous = now;
                    yield return null;
                }
                RopeControllerSailReef reef = null;
                // Claim the one attempt before any native attachment can partly
                // succeed. Player handoff never clears this session's attempt.
                if (!attempted.Add(selected.Saveable)) yield break;
                try { reef = Install(selected); }
                catch (Exception exception)
                {
                    // Do not retry an attachment that could have partly succeeded.
                    Plugin.Instance.Warn("Leopard sale sail needs an in-game check; attachment was not retried: " + exception);
                }
                if (reef != null)
                {
                    pendingChecker = reef.sail.GetComponent<SailConnections>().colChecker;
                    pendingRefreshGeneration = token;
                    yield return FinalizeFurl(selected, token, reef);
                }
            }
            finally
            {
                if (token == generation) CancelPending();
            }
        }

        private static bool MainmastReady(BoatEntry selected)
        {
            var target = selected.Saveable.transform.Find(MastPath);
            var mast = target != null ? target.GetComponent<Mast>() : null;
            var refs = selected.Saveable.GetComponent<BoatRefs>();
            return mast != null && mast.isActiveAndEnabled && mast.shipRigidbody == selected.Body &&
                refs != null && refs.masts != null && refs.masts.Length > 7 && refs.masts[7] == mast;
        }

        private static IEnumerator FinalizeFurl(BoatEntry selected,
            int token, RopeControllerSailReef reef)
        {
            var deadline = Time.realtimeSinceStartup + 3f;
            var frames = 0;
            var lastState = "native visual Start not observed";
            try
            {
                // Newly attached sails run Start and an asynchronous cloth
                // refresh. Numeric unroll alone is not a rendered furl check.
                while (Time.realtimeSinceStartup < deadline)
                {
                    if (!StillSelected(selected, token) || !GameState.playing ||
                        GameState.currentlyLoading || reef == null || reef.sail == null ||
                        pendingWinch == null || pendingWinch.rope != reef) yield break;
                    // Native visual refresh needs an active sail. Leave hidden
                    // objects to their normal scene lifecycle.
                    if (!reef.sail.isActiveAndEnabled) yield break;
                    var failed = false;
                    var complete = false;
                    try
                    {
                        if (WinchInUse(pendingWinch))
                        {
                            ReleaseForWinchInput(pendingWinch);
                            failed = true;
                        }
                        else if (!HasInitialFurlLength(reef.reverseReefing, reef.currentLength))
                        {
                            // A winch or another owner changed the rope after
                            // installation. Do not claim its new setting.
                            Plugin.Instance.Warn($"Leopard sale furl initialization released: reef length changed to {reef.currentLength:F3}, unroll {reef.sail.currentUnroll:F3}.");
                            failed = true;
                        }
                        else
                        {
                            var visual = reef.sail.GetComponent<ReefEffectAnimUniversal>();
                            var initialized = visual != null && VisualAnimator.GetValue(visual) is Animator animator && animator != null;
                            var refreshing = visual == null || (bool)VisualRefreshing.GetValue(visual);
                            lastState = $"reef {reef.currentLength:F3}, unroll {reef.sail.currentUnroll:F3}, animator ready {initialized}, refreshing {refreshing}, native visual update {visual != null && visual.debugToggleCloth}";
                            if (frames >= 2 && initialized && !refreshing && visual.debugToggleCloth)
                            {
                                // Native animation alone owns the visuals.
                                Plugin.Instance.DebugLog("Leopard native sail initialization readiness observed.");
                                complete = true;
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        Plugin.Instance.Warn("Leopard sale furl could not be confirmed: " + exception.Message);
                        failed = true;
                    }
                    if (failed || complete) yield break;
                    ++frames;
                    yield return null;
                }
                Plugin.Instance.Warn("Leopard sale sail installed, but native visual readiness was not observed within the bounded initialization window: " + lastState + ".");
            }
            finally
            {
                if (token == generation)
                {
                    pendingWinch = null;
                    ClearClothRestart();
                }
            }
        }

        private static bool HasInitialFurlLength(bool reverse, float length) =>
            length == (reverse ? 1f : 0f);

        private static RopeControllerSailReef Install(BoatEntry selected)
        {
            if (selected.Purchased || !selected.Boat.isActiveAndEnabled) return null;
            var root = selected.Saveable.transform;
            var masts = root.GetComponentsInChildren<Mast>(true);
            if (root.GetComponentsInChildren<Sail>(true).Length != 0 ||
                masts.Any(mast => mast.sails == null || mast.sails.Count != 0 ||
                    mast.startSailPrefab != null || mast.startSailPrefabs == null || mast.startSailPrefabs.Length != 0) ||
                GameState.modData != null && GameState.modData.ContainsKey("SEboatSails.207"))
            {
                Plugin.Instance.DebugLog("Leopard sale sail skipped: existing sails or rig configuration preserved.");
                return null;
            }
            var target = root.Find(MastPath);
            var mast = target != null ? target.GetComponent<Mast>() : null;
            var refs = root.GetComponent<BoatRefs>();
            var customization = root.GetComponent<SaveableBoatCustomization>();
            var directory = PrefabsDirectory.instance != null ? PrefabsDirectory.instance.sails : null;
            var prefab = directory != null && directory.Length > SailIndex ? directory[SailIndex] : null;
            var sail = prefab != null ? prefab.GetComponent<Sail>() : null;
            var connections = prefab != null ? prefab.GetComponent<SailConnections>() : null;
            var prefabReef = connections != null ? connections.reefController as RopeControllerSailReef : null;
            if (AttachInitialSail == null || UpdateSailUnroll == null ||
                VisualAnimator == null || VisualRefreshing == null ||
                WinchClicked == null || WinchStickyClickedBy == null || mast == null || !mast.isActiveAndEnabled ||
                mast.orderIndex != 7 || mast.shipRigidbody != selected.Body ||
                refs == null || refs.masts == null || refs.masts.Length <= 7 || refs.masts[7] != mast ||
                customization == null || !customization.enabled ||
                mast.maxSails < 1 || mast.onlySquareSails || mast.onlyStaysails || mast.walkColMast == null ||
                !Present(mast.reefWinch) || !Present(mast.leftAngleWinch) || !Present(mast.rightAngleWinch) ||
                !Present(mast.midAngleWinch) || !Present(mast.midRopeAtt) || !Present(mast.mastReefAtt) ||
                prefab == null || prefab.name != SailName || sail == null || sail.prefabIndex != SailIndex ||
                sail.category != SailCategory.lateen || connections == null || connections.colChecker == null ||
                prefabReef == null || !prefabReef.enabled || prefabReef.sail != sail ||
                prefab.GetComponent<Rigidbody>() == null || prefab.GetComponent<HingeJoint>() == null ||
                !DockGeometry.Finite(sail.installHeight) || sail.installHeight <= 0f ||
                !DockGeometry.Finite(mast.mastHeight) || sail.installHeight > mast.mastHeight)
            {
                Plugin.Instance.Warn("Leopard sale sail skipped: inspected mainmast or medium lateen structure is unavailable or incompatible.");
                return null;
            }
            AttachInitialSail.Invoke(mast, new object[] { prefab, sail.installHeight - mast.mastHeight });
            var installed = mast.sails.Count == 1 && mast.sails[0] != null ? mast.sails[0].GetComponent<Sail>() : null;
            if (installed == null || installed.prefabIndex != SailIndex || installed.shipRigidbody != selected.Body || !installed.IsInstalled())
                throw new InvalidOperationException("Native medium lateen attachment did not report one installed sail on Leopard.");
            var installedConnections = installed.GetComponent<SailConnections>();
            var reef = installedConnections != null ? installedConnections.reefController as RopeControllerSailReef : null;
            if (reef == null || reef.sail != installed || mast.reefWinch[0].rope != reef)
                throw new InvalidOperationException("Native medium lateen reef control was not attached to the mainmast winch.");
            FurlInstalledSail(reef);
            pendingWinch = mast.reefWinch[0];
            Plugin.Instance.DebugLog($"Leopard sale sail: installed one medium lateen (prefab {SailIndex}) on mainmast 7 at {sail.installHeight:F2}m; initial reef value set, native visual initialization pending.");
            return reef;
        }

        private static void FurlInstalledSail(RopeControllerSailReef reef)
        {
            // The winch edits this controller length. Update the native sail
            // state immediately as well, before its first physics/render update.
            reef.currentLength = reef.reverseReefing ? 1f : 0f;
            reef.changed = true;
            UpdateSailUnroll.Invoke(reef, null);
            if (reef.sail.currentUnroll != 0f)
                throw new InvalidOperationException("Native reef control did not fully furl the Leopard sale sail.");
        }

        private static bool Present<T>(T[] values) where T : UnityEngine.Object =>
            values != null && values.Length > 0 && values[0] != null;
    }
}
