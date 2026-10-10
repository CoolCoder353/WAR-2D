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
    /// (master, music, sound effects) and Controls (rebinding with Press-a-key, conflicts offered as a swap,
    /// and edge scrolling). Every change applies and saves at once (<see cref="SettingsStore"/>).
    /// </summary>
    public sealed class SettingsController : IDisposable
    {
        private enum Page { Graphics, Audio, Controls }

        /// <summary>Rebindable actions (by <c>GameInput</c> name) and their labels; the rest are shown fixed.</summary>
        private static readonly (string label, string action, string fixedText)[] Bindings =
        {
            ("Pan camera", null, "Arrow keys"),
            ("Select / box select", "Select", null),
            ("Move", "Command", null),
            ("Attack-move", "AttackMove", null),
            ("Stop", "Stop", null),
            ("Hold", "Hold", null),
            ("Queue order", "QueueModifier", null),
            ("Assign squad", "AssignModifier", null),
            ("Rotate building", "Rotate", null),
            ("Menu", "Menu", null),
        };

        private readonly VisualElement screen, tabs, graphics, audio, controls, bindingRows, conflict;
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
            bindingRows = root.Q("binding-rows");
            conflict = root.Q("conflict");
            hint = root.Q<Label>("rebind-hint");
            conflictText = root.Q<Label>("conflict-text");
            conflictHelp = root.Q<Label>("conflict-help");
            root.Q<Button>("settings-back").clicked += Close;
            root.Q<Button>("settings-reset").clicked += ResetToDefaults;
            root.Q<Button>("conflict-cancel").clicked += CancelConflict;
            root.Q<Button>("conflict-swap").clicked += SwapConflict;
            Show();
        }

        private static GameSettings S => SettingsStore.Current;

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
            graphics.EnableInClassList("settings__page--visible", page == Page.Graphics);
            audio.EnableInClassList("settings__page--visible", page == Page.Audio);
            controls.EnableInClassList("settings__page--visible", page == Page.Controls);
            ShowGraphics();
            ShowAudio();
            ShowControls();
        }

        private void ShowGraphics()
        {
            VisualElement resolutions = graphics.Q("resolution-options");
            resolutions.Clear();
            foreach (Vector2Int r in GameSettings.Resolutions)
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

        /// <summary>The Slider component: a track with a fill and a knob, and the value in percent; click or drag to set.</summary>
        private static VisualElement Slider(string name, float value, Action<float> changed)
        {
            var slider = new VisualElement { name = name };
            slider.AddToClassList("slider");
            var track = new VisualElement();
            track.AddToClassList("slider__track");
            var fill = new VisualElement();
            fill.AddToClassList("slider__fill");
            var knob = new VisualElement();
            knob.AddToClassList("slider__knob");
            fill.style.width = Length.Percent(value * 100f);
            knob.style.left = Length.Percent(value * 100f);
            track.Add(fill);
            track.Add(knob);
            var text = new Label($"{value * 100f:0}%");
            text.AddToClassList("slider__value");
            slider.Add(track);
            slider.Add(text);
            bool dragging = false;
            void Set(Vector2 local)
            {
                float v = Mathf.Clamp01(local.x / Mathf.Max(1f, track.layout.width));
                fill.style.width = Length.Percent(v * 100f);
                knob.style.left = Length.Percent(v * 100f);
                text.text = $"{v * 100f:0}%";
            }
            track.RegisterCallback<PointerDownEvent>(e => { dragging = true; track.CapturePointer(e.pointerId); Set(e.localPosition); });
            track.RegisterCallback<PointerMoveEvent>(e => { if (dragging) Set(e.localPosition); });
            track.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!dragging) return;
                dragging = false;
                track.ReleasePointer(e.pointerId);
                changed(Mathf.Clamp01(e.localPosition.x / Mathf.Max(1f, track.layout.width)));
            });
            return slider;
        }

        private void ShowControls()
        {
            bindingRows.Clear();
            foreach ((string label, string actionName, string fixedText) in Bindings)
            {
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
            Slot(controls, "edge-slot", Widgets.Switch("edge-scroll", S.EdgeScroll, true, () => { S.EdgeScroll = !S.EdgeScroll; Changed(); }));
            hint.text = listening != null ? $"Press a key for {LabelOf(listening)}. Esc cancels." : "";
        }

        private static string LabelOf(string action)
        {
            foreach ((string label, string name, _) in Bindings) if (name == action) return label;
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
