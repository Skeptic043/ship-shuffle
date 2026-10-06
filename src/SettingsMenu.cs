using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace ShipShuffle
{
    // Drawn by Configuration Manager only. No OnGUI, Update or polling object.
    internal sealed class SettingsMenu
    {
        private readonly ShuffleConfig config;
        private bool expanded, editingBoats, showUnavailableBoats;
        private SettingsCatalog snapshot;
        private SelectionDraft draft;
        private string search = "", notice = "";
        private Vector2 scroll;
        private GUIStyle labelStyle, toggleStyle;
        internal SettingsMenu(ShuffleConfig config) { this.config = config; }

        internal void Draw(ConfigEntryBase entry)
        {
            // Configuration Manager may retain a drawer after plugin teardown.
            if (Plugin.Instance == null || !ReferenceEquals(Plugin.Instance.Settings, config)) return;
            if (labelStyle == null) labelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            if (toggleStyle == null) toggleStyle = new GUIStyle(GUI.skin.toggle) { wordWrap = true };
            var enabled = GUI.enabled;
            try
            {
                // CM invokes custom drawers without a right-column wrapper.
                // Explicit stretch claims the complete row left by our hidden
                // setting-name and reset controls, per its documented contract.
                using (new GUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.MinWidth(0)))
                {
                    var open = GUILayout.Toggle(expanded, expanded ? "▼ Advanced" : "▶ Advanced", GUI.skin.button);
                    if (open != expanded)
                    {
                        expanded = open;
                        if (!expanded) draft = null; // collapse cancels any unapplied edits
                    }
                    if (expanded)
                    {
                        Note("Selection changes apply to the next new game. They do not reshuffle a saved layout.");
                        if (draft == null)
                        {
                            Choose("Boats", true);
                            Choose("Ports", false);
                            config.IncludeHiddenPorts.Value = GUILayout.Toggle(config.IncludeHiddenPorts.Value, "Include secret destinations");
                            config.IncludeModPorts.Value = GUILayout.Toggle(config.IncludeModPorts.Value, "Include mod ports");
                            config.DebugLogging.Value = GUILayout.Toggle(config.DebugLogging.Value, "Debug logging");
                        }
                        else DrawEditor();
                        if (!string.IsNullOrEmpty(notice)) Note(notice);
                    }
                }
            }
            finally { GUI.enabled = enabled; }
        }

        private void Note(string text) => GUILayout.Label(text, labelStyle, GUILayout.ExpandWidth(true));

        private void Choose(string label, bool boats)
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label(label);
                if (snapshot != null)
                {
                    var settings = config.ReadFleetSettings(new List<string>());
                    var choices = (boats ? snapshot.Boats : snapshot.Ports).Where(c => c.IsVisible(settings)).ToList();
                    var selected = SelectionExclusions.Parse(boats ? config.ExcludedBoats.Value : config.ExcludedPorts.Value);
                    GUILayout.Label(choices.Count(c => !selected.Contains(c.Name)) + " / " + choices.Count + " included");
                }
                if (GUILayout.Button("Choose...", GUILayout.ExpandWidth(false))) Open(boats);
            }
        }

        private void Open(bool boats)
        {
            editingBoats = boats;
            draft = new SelectionDraft(boats ? config.ExcludedBoats.Value : config.ExcludedPorts.Value);
            showUnavailableBoats = false;
            search = ""; scroll = new Vector2();
            Refresh();
        }

        private void Refresh()
        {
            if (SettingsCatalog.TryCapture(out var captured, out notice)) snapshot = captured;
            else if (snapshot != null) notice += " Showing the last available catalogue.";
            if (snapshot != null) draft.Refresh(editingBoats ? snapshot.Boats : snapshot.Ports, true);
        }

        private void DrawEditor()
        {
            var settings = config.ReadFleetSettings(new List<string>());
            GUILayout.Label(editingBoats ? "Choose boats" : "Choose ports");
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("Search", GUILayout.ExpandWidth(false));
                search = GUILayout.TextField(search, GUILayout.ExpandWidth(true));
                if (GUILayout.Button("Refresh", GUILayout.ExpandWidth(false))) Refresh();
            }
            var visible = draft.Visible(search, settings).ToList();
            if (draft.HasCatalogue)
                Note(visible.Count(c => !draft.Selection.Contains(c.Name)) + " / " + visible.Count + " shown choices included");
            var previous = GUI.enabled;
            using (new GUILayout.HorizontalScope())
            {
                GUI.enabled = previous && draft.HasCatalogue;
                if (GUILayout.Button("Check all")) draft.SetVisible(true, search, settings);
                if (GUILayout.Button("Uncheck all")) draft.SetVisible(false, search, settings);
                GUI.enabled = previous;
            }
            using (var view = new GUILayout.ScrollViewScope(scroll, GUILayout.Height(320), GUILayout.ExpandWidth(true), GUILayout.MinWidth(0)))
            {
                scroll = view.scrollPosition;
                string group = null;
                foreach (var row in visible)
                {
                    if (!string.Equals(group, row.GroupLabel, StringComparison.Ordinal))
                    {
                        group = row.GroupLabel;
                        GUILayout.Label(group, GUI.skin.box, GUILayout.ExpandWidth(true));
                    }
                    var reason = row.DisabledReason(settings);
                    var label = SettingsPresentation.RowLabel(row);
                    GUI.enabled = previous && reason == null;
                    var included = !draft.Selection.Contains(row.Name);
                    var value = GUILayout.Toggle(included, label, toggleStyle, GUILayout.ExpandWidth(true));
                    GUI.enabled = previous;
                    if (value != included) draft.Set(row, value, settings);
                }
                if (editingBoats && snapshot != null && snapshot.UnavailableBoats.Count > 0)
                {
                    if (GUILayout.Button(showUnavailableBoats ? "Hide unavailable boat details" : "Show unavailable boat details"))
                        showUnavailableBoats = !showUnavailableBoats;
                    if (showUnavailableBoats)
                        foreach (var boat in snapshot.UnavailableBoats.Where(item => string.IsNullOrEmpty(search) ||
                            item.Label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0))
                            Note(boat.Label + " — " + boat.Reason);
                }
                var unavailable = draft.Unavailable(settings, editingBoats).ToList();
                if (unavailable.Count > 0)
                {
                    Note("Unavailable saved choices are retained. Remove only if you want to forget that exclusion.");
                    foreach (var record in unavailable)
                    {
                        using (new GUILayout.HorizontalScope())
                        {
                            Note(record.Name ?? "Unrecognized saved choice (retained)");
                            if (GUILayout.Button("Remove", GUILayout.ExpandWidth(false))) draft.RemoveUnavailable(record);
                        }
                    }
                }
            }
            using (new GUILayout.HorizontalScope())
            {
                GUI.enabled = previous && draft.CanApply;
                if (GUILayout.Button("Apply")) Apply();
                GUI.enabled = previous;
                if (GUILayout.Button("Cancel")) { draft?.Cancel(); draft = null; notice = ""; }
            }
        }

        private void Apply()
        {
            var target = editingBoats ? config.ExcludedBoats : config.ExcludedPorts;
            if (!draft.TryApply(target.Value, out var saved))
            { notice = "Settings changed while this list was open. Cancel and reopen to keep those changes."; return; }
            target.Value = saved;
            draft = null;
            notice = "Selection saved for the next new game.";
        }
    }
}
