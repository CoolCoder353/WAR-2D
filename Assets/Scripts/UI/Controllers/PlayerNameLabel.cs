using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// A player's name as every v0.14 screen shows it (Figma <c>v0.14 AI opponents</c>, decision 1): the nickname, and
    /// for an AI its tag right after it, the <c>IconAi</c> robot head (16 px) and "AI · Hard" in the secondary colour,
    /// then any suffix ("(eliminated)", "· Turtle (random)"). One builder, so the lobby, top bar, diplomacy panel, gift
    /// dialog, alerts, end screen and replay viewer all read the same.
    /// </summary>
    public static class PlayerNameLabel
    {
        /// <summary>The tag text after the icon: "AI · Hard".</summary>
        public static string TagText(AiDifficulty difficulty) => $"AI · {AiLobbyRules.DifficultyName(difficulty)}";

        /// <summary>
        /// The end screen's tag text: "AI · Hard · Turtle", with "(random)" when the host left the style on Random and
        /// this is what the server picked (revealed only at GameOver).
        /// </summary>
        public static string ResultTagText(AiDifficulty difficulty, AiStyle picked, AiStyle played)
        {
            if (played == AiStyle.Random) return TagText(difficulty);
            return $"{TagText(difficulty)} · {AiLobbyRules.StyleName(played)}{(picked == AiStyle.Random ? " (random)" : "")}";
        }

        /// <summary>
        /// A row of the nickname (with <paramref name="nameClass"/>), the AI tag when <paramref name="aiTag"/> is not
        /// null, and <paramref name="suffix"/> (secondary colour after a tag, the name's style otherwise).
        /// <paramref name="disabled"/> greys all of it (an eliminated player).
        /// </summary>
        public static VisualElement Build(string nickname, string aiTag, string nameClass, string suffix = null, bool disabled = false)
        {
            var root = new VisualElement { name = "player-name", pickingMode = PickingMode.Ignore };
            root.AddToClassList("player-name");
            root.EnableInClassList("player-name--disabled", disabled);
            var name = new Label(aiTag == null && suffix != null ? $"{nickname} {suffix}" : nickname) { name = "player-name-text", pickingMode = PickingMode.Ignore };
            if (nameClass != null) name.AddToClassList(nameClass);
            name.AddToClassList("player-name__text");
            root.Add(name);
            if (aiTag == null) return root;

            root.Add(Tag(suffix != null ? $"{aiTag} {suffix}" : aiTag));
            return root;
        }

        /// <summary>The tag alone (<c>ai-tag</c>): the icon and its text.</summary>
        public static VisualElement Tag(string text)
        {
            var tag = new VisualElement { name = "ai-tag", pickingMode = PickingMode.Ignore };
            tag.AddToClassList("ai-tag");
            tag.Add(new AiIcon());
            var label = new Label(text) { name = "ai-tag-text", pickingMode = PickingMode.Ignore };
            label.AddToClassList("ai-tag__label");
            tag.Add(label);
            return tag;
        }

        /// <summary>The plain-text form ("Cmdr. Vasquez · AI · Hard"), for lines that can't hold an icon.</summary>
        public static string Plain(string nickname, string aiTag) => aiTag == null ? nickname : $"{nickname} · {aiTag}";
    }
}
