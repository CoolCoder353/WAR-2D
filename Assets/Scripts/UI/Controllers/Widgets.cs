using System;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>Small builders for the approved components that are made in code.</summary>
    public static class Widgets
    {
        /// <summary>The Toggle component: a track with a knob and a label ("On"/"Off" unless given).</summary>
        public static Button Switch(string name, bool on, bool enabled, Action clicked, string label = null)
        {
            var button = new Button(clicked) { name = name };
            button.AddToClassList("switch");
            button.EnableInClassList("switch--on", on);
            var track = new VisualElement();
            track.AddToClassList("switch__track");
            var knob = new VisualElement();
            knob.AddToClassList("switch__knob");
            track.Add(knob);
            var text = new Label(label ?? (on ? "On" : "Off"));
            text.AddToClassList("switch__label");
            button.Add(track);
            button.Add(text);
            button.SetEnabled(enabled);
            return button;
        }

        /// <summary>
        /// One option of a segmented choice: Primary when selected, Secondary otherwise. Read-only groups
        /// keep the selected option looking selected and disable the rest.
        /// </summary>
        public static Button Option(string name, string text, bool selected, bool editable, Action clicked)
        {
            var button = new Button(clicked) { name = name, text = text };
            button.AddToClassList("button");
            if (selected) button.AddToClassList("button--primary");
            button.SetEnabled(editable || selected);
            if (!editable) button.pickingMode = PickingMode.Ignore;
            return button;
        }
    }
}
