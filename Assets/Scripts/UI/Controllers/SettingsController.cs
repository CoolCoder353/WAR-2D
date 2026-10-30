using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the Settings screen (<c>Assets/UI/Menus/Settings.uxml</c>, in the main menu and the match):
    /// Graphics (resolution, fullscreen, VSync, FPS cap, the menu battle, the colour palette), Audio
    /// (master, music, sound effects), Controls (rebinding with Press-a-key, conflicts offered as a swap) and
    /// Gameplay (v0.15: UI scale, health bars, tracers, tooltip delay, camera speeds and edge scrolling,
    /// confirmations, and the per-alert Show and Sound switches; Figma frame 123:938). Every change applies and
    /// saves at once (<see cref="SettingsStore"/>).
    /// </summary>
    public sealed class SettingsController : IDisposable
    {
        private enum Page { Graphics, Audio, Controls, Gameplay }

        /// <summary>Rebindable actions (by <c>GameInput</c> name) and their labels; the rest are shown fixed.</summary>
        private static readonly (string label, string action, string fixedText)[] Bindings =
        {
            ("Pan camera", null, "Arrow keys"),
            ("Select / box select", "Select", null),
            ("Move", "Command", null),
            ("Attack-move", "AttackMove", null),
            ("Stop", "Stop", null),
            ("Hold", "Hold", null),
            ("Roam (scout on their own)", "Roam", null),
            ("Repair (Builders)", "Repair", null),
            ("Dig (Diggers)", "Dig", null),
            ("Detonate (Suicide bombers)", "Detonate", null), // shown once GameInput has the action (v0.12 plan)
            ("Cancel / demolish", "Cancel", null),
            ("Queue order", "QueueModifier", null),
            ("Assign squad", "AssignModifier", null),
            ("Rotate building", "Rotate", null),
            ("Menu", "Menu", null),
            ("Select idle Builders", "SelectIdleBuilders", null),
            ("Select army", "SelectArmy", null),
            ("Jump to HQ", "JumpToHq", null),
            ("Next alert", "NextAlert", null),
            // v0.15.1 card slots (Figma H3): what each does with Builders / with producers selected.
            ("Card top 1 · Miner / 1st unit type", "CardTop1", null),
            ("Card top 2 · Barracks / 2nd unit type", "CardTop2", null),
            ("Card top 3 · Heavy Factory / 3rd unit type", "CardTop3", null),
            ("Card top 4 · Siege Workshop", "CardTop4", null),
            ("Card bottom 1 · Wall", "CardBottom1", null),
            ("Card bottom 2 · Bomb", "CardBottom2", null),
            ("Card bottom 3 · Radar", "CardBottom3", null),
            ("Card bottom 4 · Gun turret", "CardBottom4", null),
            ("Card bottom 5 · Jammer", "CardBottom5", null),
        };

        /// <summary>The alert kinds in the Gameplay tab's table, in the frame's order, with their names.</summary>
        private static readonly (AlertKind kind, string label)[] AlertRows =
        {
            (AlertKind.UnderAttack, "Under attack"),
            (AlertKind.BombTriggered, "Bomb triggered"),
            (AlertKind.EnemySpotted, "Enemy spotted"),
            (AlertKind.ConstructionFinished, "Construction finished"),
            (AlertKind.UpkeepUnpaid, "Upkeep unpaid"),
            (AlertKind.GiftReceived, "Gift received"),
            (AlertKind.ResourcesLow, "Resources will run out"),
            (AlertKind.RepairsStopped, "Repairs stopped"),
            (AlertKind.VisionSharedWithYou, "Vision shared with you"),
            (AlertKind.VisionUnshared, "Vision unshared"),
        };

        /// <summary>
        /// True when Confirm surrender may not be switched off: a match with teams (the host set Teams), so a
        /// misclick can't surrender for a side. Replaceable for tests and for a finer rule.
        /// </summary>
        public static Func<bool> SurrenderAlwaysConfirms = InTeamMatch;

        private static bool InTeamMatch()
        {
            GameCore core = GameCore.Instance;
            if (core == null) return false;
            var teams = new HashSet<int>();
            foreach (KeyValuePair<int, int> entry in core.Teams)
                if (!teams.Add(entry.Value)) return true;
            return false;
        }

        private readonly VisualElement screen, tabs, graphics, audio, controls, gameplay, bindingRows, conflict;
        private readonly Label hint, conflictText, conflictHelp;
        private Page page;
        private InputActionRebindingExtensions.RebindingOperation rebinding;
        private string listening;
        private (InputAction action, InputAction other, string path, string previous)? pendingConflict;

        /// <summary>Raised by Back.</summary>
        public event Action Closed;

        public SettingsController(VisualElement root)
        {
            screen = root.Q("settings");
            tabs = root.Q("settings-tabs");
            graphics = root.Q("settings-graphics");
            audio = root.Q("settings-audio");
            controls = root.Q("settings-controls");
            gameplay = root.Q("settings-gameplay");
            bindingRows = root.Q("binding-rows");
            conflict = root.Q("conflict");
            hint = root.Q<Label>("rebind-hint");
            conflictText = root.Q<Label>("conflict-text");
            conflictHelp = root.Q<Label>("conflict-help");
            root.Q<Button>("settings-back").clicked += Close;
            root.Q<Button>("settings-reset").clicked += ResetToDefaults;
            root.Q<Button>("conflict-cancel").clicked += CancelConflict;
            root.Q<Button>("conflict-swap").clicked += SwapConflict;
            root.Q<Button>("edge-link").clicked += () => { page = Page.Gameplay; Show(); };
            gameplay.Q<Button>("pan-speed-reset").clicked += () => { S.PanSpeed = Defaults.PanSpeed; Changed(); };
            gameplay.Q<Button>("edge-speed-reset").clicked += () => { S.EdgeScrollSpeed = Defaults.EdgeScrollSpeed; Changed(); };
            gameplay.Q<Button>("zoom-speed-reset").clicked += () => { S.ZoomSpeed = Defaults.ZoomSpeed; Changed(); };
            gameplay.Q<Button>("shift-multiplier-reset").clicked += () => { S.ShiftMultiplier = Defaults.ShiftMultiplier; Changed(); };
            Show();
        }

        private static GameSettings S => SettingsStore.Current;

        private static readonly GameSettings Defaults = new GameSettings();

        public bool IsOpen => screen.ClassListContains("overlay-screen--visible");

        public void Open()
        {
            page = Page.Graphics;
            screen.AddToClassList("overlay-screen--visible");
            Show();
        }

        public void Close()
        {
            CancelRebind();
            CancelConflict();
            screen.RemoveFromClassList("overlay-screen--visible");
            Closed?.Invoke();
        }

        private void Changed()
        {
            S.Bindings = Rebinding.Save(GameInput.Map);
            S.Clamp();
            SettingsStore.Apply(S);
            SettingsStore.Save(S);
            Show();
        }

        private void ResetToDefaults()
        {
            CancelRebind();
            CancelConflict();
            GameInput.Map.RemoveAllBindingOverrides();
            SettingsStore.Current = new GameSettings();
            Changed();
        }

        private void Show()
        {
            tabs.Clear();
            tabs.Add(Widgets.Option("tab-graphics", "Graphics", page == Page.Graphics, true, () => { page = Page.Graphics; Show(); }));
            tabs.Add(Widgets.Option("tab-audio", "Audio", page == Page.Audio, true, () => { page = Page.Audio; Show(); }));
            tabs.Add(Widgets.Option("tab-controls", "Controls", page == Page.Controls, true, () => { page = Page.Controls; Show(); }));
            tabs.Add(Widgets.Option("tab-gameplay", "Gameplay", page == Page.Gameplay, true, () => { page = Page.Gameplay; Show(); }));
            graphics.EnableInClassList("settings__page--visible", page == Page.Graphics);
            audio.EnableInClassList("settings__page--visible", page == Page.Audio);
            controls.EnableInClassList("settings__page--visible", page == Page.Controls);
            gameplay.EnableInClassList("settings__page--visible", page == Page.Gameplay);
            ShowGraphics();
            ShowAudio();
            ShowControls();
            ShowGameplay();
        }

        private void ShowGraphics()
        {
            VisualElement resolutions = graphics.Q("resolution-options");
            resolutions.Clear();
            resolutions.style.flexWrap = Wrap.Wrap;
            foreach (Vector2Int r in GameSettings.ResolutionChoices(new Vector2Int(S.Width, S.Height)))
            {
                Vector2Int res = r;
                resolutions.Add(Widgets.Option($"resolution-{r.x}", $"{r.x}×{r.y}", S.Width == r.x && S.Height == r.y, true, () => { S.Width = res.x; S.Height = res.y; Changed(); }));
            }
            Slot(graphics, "fullscreen-slot", Widgets.Switch("fullscreen", S.Fullscreen, true, () => { S.Fullscreen = !S.Fullscreen; Changed(); }));
            Slot(graphics, "vsync-slot", Widgets.Switch("vsync", S.VSync, true, () => { S.VSync = !S.VSync; Changed(); }));
            Slot(graphics, "battle-slot", Widgets.Switch("menu-battle", S.MenuBattle, true, () => { S.MenuBattle = !S.MenuBattle; Changed(); }));
            VisualElement fps = graphics.Q("fps-options");
            fps.Clear();
            foreach (int cap in GameSettings.FpsCaps)
            {
                int value = cap;
                fps.Add(Widgets.Option($"fps-{cap}", cap == 0 ? "None" : cap.ToString(), S.FpsCap == cap, true, () => { S.FpsCap = value; Changed(); }));
            }
            VisualElement palettes = graphics.Q("palette-picker");
            palettes.Clear();
            foreach (string name in Palettes.Names)
            {
                string palette = name;
                var card = new Button(() => { S.Palette = palette; Changed(); }) { name = "palette-" + name };
                card.AddToClassList("palette-card");
                string uss = Palettes.UssClass(name);
                if (uss != null) card.AddToClassList(uss); // the card shows its own palette's tokens
                card.EnableInClassList("palette-card--selected", S.Palette == name);
                var title = new Label(name == Palettes.Standard ? "Standard" : name == Palettes.Colourblind ? "Colourblind" : "High contrast");
                title.AddToClassList("palette-card__name");
                card.Add(title);
                var players = new VisualElement();
                players.AddToClassList("palette-card__swatches");
                for (int i = 1; i <= 8; i++) players.Add(Swatch("player-" + i));
                card.Add(players);
                var semantic = new VisualElement();
                semantic.AddToClassList("palette-card__swatches");
                foreach (string s in new[] { "accent", "readout", "success", "warning", "danger" }) semantic.Add(Swatch("swatch--" + s));
                card.Add(semantic);
                palettes.Add(card);
            }
        }

        private static VisualElement Swatch(string cls)
        {
            var swatch = new VisualElement();
            swatch.AddToClassList("palette-card__swatch");
            swatch.AddToClassList(cls);
            return swatch;
        }

        private void ShowAudio()
        {
            Slot(audio, "master-slider", Slider("master", S.Master, v => { S.Master = v; Changed(); }));
            Slot(audio, "music-slider", Slider("music", S.Music, v => { S.Music = v; Changed(); }));
            Slot(audio, "sfx-slider", Slider("sfx", S.Sfx, v => { S.Sfx = v; Changed(); }));
        }

        /// <summary>The Slider component for a 0–1 volume, shown in percent.</summary>
        private static VisualElement Slider(string name, float value, Action<float> changed) => Slider(name, value, 0f, 1f, 0f, Percent, changed);

        private static string Percent(float v) => $"{v * 100f:0}%";

        /// <summary>
        /// The Slider component: a track with a fill and a knob, and the value as <paramref name="format"/> gives it;
        /// click or drag to set (the value applies on release). It runs from <paramref name="min"/> to
        /// <paramref name="max"/> in steps of <paramref name="step"/> (0 = continuous).
        /// </summary>
        private static VisualElement Slider(string name, float value, float min, float max, float step, Func<float, string> format, Action<float> changed, bool enabled = true)
        {
            var slider = new VisualElement { name = name };
            slider.AddToClassList("slider");
            var track = new VisualElement();
            track.AddToClassList("slider__track");
            var fill = new VisualElement();
            fill.AddToClassList("slider__fill");
            var knob = new VisualElement();
            knob.AddToClassList("slider__knob");
            float At(float t)
            {
                float v = Mathf.Lerp(min, max, Mathf.Clamp01(t));
                if (step > 0f) v = Mathf.Clamp(min + Mathf.Round((v - min) / step) * step, min, max);
                return v;
            }
            float Fraction(float v) => max > min ? Mathf.Clamp01((v - min) / (max - min)) : 0f;
            fill.style.width = Length.Percent(Fraction(value) * 100f);
            knob.style.left = Length.Percent(Fraction(value) * 100f);
            track.Add(fill);
            track.Add(knob);
            var text = new Label(format(value));
            text.AddToClassList("slider__value");
            slider.Add(track);
            slider.Add(text);
            bool dragging = false;
            void Set(Vector2 local)
            {
                float v = At(local.x / Mathf.Max(1f, track.layout.width));
                fill.style.width = Length.Percent(Fraction(v) * 100f);
                knob.style.left = Length.Percent(Fraction(v) * 100f);
                text.text = format(v);
            }
            track.RegisterCallback<PointerDownEvent>(e => { dragging = true; track.CapturePointer(e.pointerId); Set(e.localPosition); });
            track.RegisterCallback<PointerMoveEvent>(e => { if (dragging) Set(e.localPosition); });
            track.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!dragging) return;
                dragging = false;
                track.ReleasePointer(e.pointerId);
                changed(At(e.localPosition.x / Mathf.Max(1f, track.layout.width)));
            });
            slider.SetEnabled(enabled);
            return slider;
        }

        private void ShowControls()
        {
            bindingRows.Clear();
            foreach ((string label, string actionName, string fixedText) in Bindings)
            {
                if (actionName != null && GameInput.Map.FindAction(actionName) == null) continue;
                var row = new VisualElement();
                row.AddToClassList("settings-row");
                var text = new Label(label);
                text.AddToClassList("text");
                text.AddToClassList("settings-row__label");
                row.Add(text);
                InputAction action = actionName != null ? GameInput.Map[actionName] : null;
                string shown = fixedText ?? (actionName == "AssignModifier" ? Rebinding.Display(action) + " + 1–0" : Rebinding.Display(action));
                var binding = new Button(() => StartRebind(actionName)) { name = "binding-" + (actionName ?? "pan"), text = listening == actionName && actionName != null ? "Press a key…" : shown };
                binding.AddToClassList("binding");
                binding.EnableInClassList("binding--listening", listening != null && listening == actionName);
                bool conflicted = pendingConflict.HasValue && (pendingConflict.Value.action.name == actionName || pendingConflict.Value.other.name == actionName);
                binding.EnableInClassList("binding--conflict", conflicted);
                if (actionName == null) binding.pickingMode = PickingMode.Ignore;
                row.Add(binding);
                bindingRows.Add(row);
            }
            hint.text = listening != null ? $"Press a key for {LabelOf(listening)}. Esc cancels." : "";
        }

        private void ShowGameplay()
        {
            Slot(gameplay, "ui-scale-slot", Slider("ui-scale", S.UiScale, 0.75f, 1.5f, 0.05f, Percent, v => { S.UiScale = v; Changed(); }));
            Segmented("health-bar-options", "health-bars", S.HealthBars, v => S.HealthBars = v, "Always", "Damaged only", "Selected only");
            Segmented("tracer-options", "tracers", S.Tracers, v => S.Tracers = v, "Full", "Reduced", "Off");
            Segmented("tooltip-options", "tooltip-delay", S.TooltipDelay, v => S.TooltipDelay = v, "Fast", "Normal", "Off");

            CameraSlider("pan-speed", S.PanSpeed, Defaults.PanSpeed, 0.5f, 2f, 0.05f, Percent, v => S.PanSpeed = v);
            Slot(gameplay, "edge-slot", Widgets.Switch("edge-scroll", S.EdgeScroll, true, () => { S.EdgeScroll = !S.EdgeScroll; Changed(); }));
            CameraSlider("edge-speed", S.EdgeScrollSpeed, Defaults.EdgeScrollSpeed, 0.5f, 2f, 0.05f, Percent, v => S.EdgeScrollSpeed = v, S.EdgeScroll);
            gameplay.Q<Label>("edge-speed-label").EnableInClassList("text--disabled", !S.EdgeScroll);
            CameraSlider("zoom-speed", S.ZoomSpeed, Defaults.ZoomSpeed, 0.5f, 2f, 0.05f, Percent, v => S.ZoomSpeed = v);
            CameraSlider("shift-multiplier", S.ShiftMultiplier, Defaults.ShiftMultiplier, 1.5f, 4f, 0.1f, v => $"{v:0.0}×", v => S.ShiftMultiplier = v);

            Slot(gameplay, "confirm-demolish-slot", Widgets.Switch("confirm-demolish", S.ConfirmDemolish, true, () => { S.ConfirmDemolish = !S.ConfirmDemolish; Changed(); }));
            bool surrenderLocked = SurrenderAlwaysConfirms != null && SurrenderAlwaysConfirms();
            Slot(gameplay, "confirm-surrender-slot", Widgets.Switch("confirm-surrender", S.ConfirmSurrender || surrenderLocked, !surrenderLocked, () => { S.ConfirmSurrender = !S.ConfirmSurrender; Changed(); }));
            gameplay.Q<Label>("confirm-surrender-label").EnableInClassList("text--disabled", surrenderLocked);
            gameplay.Q("confirm-surrender-hint").style.display = surrenderLocked ? DisplayStyle.Flex : DisplayStyle.None;

            VisualElement rows = gameplay.Q("alert-filter-rows");
            rows.Clear();
            foreach ((AlertKind kind, string label) in AlertRows) rows.Add(AlertRow(kind, label));
            InputAction next = GameInput.Map.FindAction("NextAlert");
            gameplay.Q<Label>("next-alert-hint").text = next != null ? $"Next alert key: {Rebinding.Display(next)} (rebind in Controls)." : "";
        }

        /// <summary>A row of the Alerts table: the kind's stripe and name, then its Show and Sound switches.</summary>
        private VisualElement AlertRow(AlertKind kind, string label)
        {
            int i = (int)kind;
            bool show = S.ShowsAlert(kind), sound = S.SoundsAlert(kind);
            // Under attack and Upkeep unpaid keep at least one of the two: the last one on is locked.
            bool mustHear = Array.IndexOf(GameSettings.MustHear, kind) >= 0;
            var row = new VisualElement { name = "alert-filter-" + kind };
            row.AddToClassList("alert-filter__row");
            row.AddToClassList(AlertFeedController.KindClass(kind));
            var name = new VisualElement();
            name.AddToClassList("alert-filter__name");
            var stripe = new VisualElement();
            stripe.AddToClassList("alert__stripe");
            name.Add(stripe);
            var text = new Label(label);
            text.AddToClassList("text");
            text.AddToClassList("alert-filter__label");
            name.Add(text);
            row.Add(name);
            row.Add(Cell(Widgets.Switch($"alert-show-{kind}", show, !(mustHear && show && !sound), () => { S.Clamp(); S.AlertShow[i] = !show; Changed(); }, "")));
            row.Add(Cell(Widgets.Switch($"alert-sound-{kind}", sound, !(mustHear && sound && !show), () => { S.Clamp(); S.AlertSound[i] = !sound; Changed(); }, "")));
            return row;
        }

        private static VisualElement Cell(VisualElement content)
        {
            var cell = new VisualElement();
            cell.AddToClassList("alert-filter__cell");
            cell.Add(content);
            return cell;
        }

        /// <summary>A segmented choice of an int setting (the option's index is its value).</summary>
        private void Segmented(string slot, string name, int current, Action<int> set, params string[] labels)
        {
            VisualElement options = gameplay.Q(slot);
            options.Clear();
            for (int i = 0; i < labels.Length; i++)
            {
                int value = i;
                options.Add(Widgets.Option($"{name}-{i}", labels[i], current == i, true, () => { set(value); Changed(); }));
            }
        }

        /// <summary>A camera slider and its Reset (shown only while the value differs from the default).</summary>
        private void CameraSlider(string name, float value, float fallback, float min, float max, float step, Func<float, string> format, Action<float> set, bool enabled = true)
        {
            Slot(gameplay, name + "-slot", Slider(name, value, min, max, step, format, v => { set(v); Changed(); }, enabled));
            Button reset = gameplay.Q<Button>(name + "-reset");
            reset.EnableInClassList("settings-gameplay__reset--hidden", Mathf.Approximately(value, fallback));
            reset.SetEnabled(enabled);
        }

        private static string LabelOf(string action)
        {
            foreach ((string label, string name, _) in Bindings) if (name == action) return label;
            if (action.StartsWith("Squad") && action.Length == 6) return "Squad " + action[5] + " (select)";
            return action;
        }

        /// <summary>Listens for the next key or button for an action (Esc cancels).</summary>
        private void StartRebind(string actionName)
        {
            if (actionName == null) return;
            CancelRebind();
            CancelConflict();
            InputAction action = GameInput.Map[actionName];
            string previous = action.bindings[0].effectivePath;
            listening = actionName;
            GameInput.Map.Disable();
            rebinding = action.PerformInteractiveRebinding(0)
                .WithCancelingThrough("<Keyboard>/escape")
                .WithControlsExcluding("<Pointer>/position")
                .WithControlsExcluding("<Mouse>/scroll")
                .OnMatchWaitForAnother(0.05f)
                .OnCancel(_ => FinishRebind())
                .OnComplete(op =>
                {
                    string path = action.bindings[0].effectivePath;
                    InputAction other = Rebinding.ConflictWith(action, path);
                    FinishRebind();
                    if (other != null)
                    {
                        pendingConflict = (action, other, path, previous);
                        conflictText.text = $"{Rebinding.Display(action)} is already used by {LabelOf(other.name)}.";
                        conflictHelp.text = $"Swap them so {LabelOf(other.name)} gets {InputControlPath.ToHumanReadableString(previous, InputControlPath.HumanReadableStringOptions.OmitDevice)}, or cancel.";
                        conflict.AddToClassList("settings__conflict--visible");
                        ShowControls();
                        return;
                    }
                    Changed();
                });
            rebinding.Start();
            ShowControls();
        }

        private void FinishRebind()
        {
            rebinding?.Dispose();
            rebinding = null;
            listening = null;
            GameInput.Map.Enable();
            ShowControls();
        }

        private void CancelRebind()
        {
            if (rebinding == null) return;
            rebinding.Cancel();
            FinishRebind();
        }

        /// <summary>Keeps the old binding (undoes the new one).</summary>
        private void CancelConflict()
        {
            if (pendingConflict is { } c) c.action.ApplyBindingOverride(0, c.previous);
            pendingConflict = null;
            conflict.RemoveFromClassList("settings__conflict--visible");
            ShowControls();
        }

        /// <summary>Gives the other action the old binding.</summary>
        private void SwapConflict()
        {
            if (pendingConflict is { } c) c.other.ApplyBindingOverride(0, c.previous);
            pendingConflict = null;
            conflict.RemoveFromClassList("settings__conflict--visible");
            Changed();
        }

        private static void Slot(VisualElement page, string name, VisualElement content)
        {
            VisualElement slot = page.Q(name);
            slot.Clear();
            slot.Add(content);
        }

        public void Dispose() => CancelRebind();
    }
}
