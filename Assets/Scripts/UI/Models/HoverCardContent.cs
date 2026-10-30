using System.Collections.Generic;
using Config;

namespace WAR2D.UI
{
    /// <summary>How a value on a hover card's detail line is coloured.</summary>
    public enum HoverTone
    {
        /// <summary>Primary text.</summary>
        Plain,
        /// <summary>A cost the player can pay (readout gold).</summary>
        Cost,
        /// <summary>The value that greys the button out (danger red).</summary>
        Danger,
    }

    /// <summary>
    /// What the shared hover card shows (Figma v0.15 QoL redesign, frame 3 · Command hover cards): a title with the current key
    /// chip, a role line, label/value pairs, secondary lines, an amber warning and a red reason when the button is greyed out.
    /// </summary>
    public sealed class HoverCardContent
    {
        public string Title = "";
        /// <summary>The key chip's text (the current binding), or null for none.</summary>
        public string Key;
        public string Role;
        public readonly List<(string label, string value, HoverTone tone)> Pairs = new List<(string, string, HoverTone)>();
        public readonly List<string> Lines = new List<string>();
        /// <summary>A warning in amber ("Switching clears the queue"), or null.</summary>
        public string Warning;
        /// <summary>Why the button is greyed out, in danger red, or null.</summary>
        public string Reason;

        public HoverCardContent() { }

        public HoverCardContent(string title, string key = null, string role = null)
        {
            Title = title ?? "";
            Key = key;
            Role = role;
        }

        /// <summary>Adds a label/value pair to the detail line.</summary>
        public HoverCardContent Pair(string label, string value, HoverTone tone = HoverTone.Plain)
        {
            Pairs.Add((label, value, tone));
            return this;
        }

        /// <summary>Adds a secondary line.</summary>
        public HoverCardContent Line(string text)
        {
            if (!string.IsNullOrEmpty(text)) Lines.Add(text);
            return this;
        }
    }

    /// <summary>The words on the v0.15 hover cards and key chips: pure, so they are tested without a HUD.</summary>
    public static class HoverText
    {
        /// <summary>Seconds of hover before a card shows (Settings → Gameplay → Tooltips delay), or null when cards are off.</summary>
        public static float? DelaySeconds(TooltipDelayMode mode) => mode switch
        {
            TooltipDelayMode.Fast => 0.25f,
            TooltipDelayMode.Off => (float?)null,
            _ => 0.6f,
        };

        /// <summary>
        /// A binding's display string as a key chip shows it: mouse buttons as LMB/RMB/MMB, long key names shortened
        /// ("Delete" → "Del", "Backspace" → "Bksp"), "Control" → "Ctrl".
        /// </summary>
        public static string KeyChip(string display)
        {
            if (string.IsNullOrEmpty(display)) return "";
            switch (display)
            {
                case "Left Button": case "LMB": return "LMB";
                case "Right Button": case "RMB": return "RMB";
                case "Middle Button": case "MMB": return "MMB";
                case "Delete": return "Del";
                case "Backspace": return "Bksp";
                case "Escape": return "Esc";
                case "Control": case "Left Control": case "Ctrl": return "Ctrl";
                case "Shift": case "Left Shift": return "Shift";
                case "Period": return ".";
                case "Comma": return ",";
                case "Space": return "Space";
            }
            return display.Length > 5 ? display.Substring(0, 5) : display;
        }

        /// <summary>The hover card of an order button, with its current key (and the queue key for Attack-move).</summary>
        public static HoverCardContent Order(OrderKind kind, string key, string queueKey, float repairCostPerHealth)
        {
            switch (kind)
            {
                case OrderKind.Move: return new HoverCardContent("Move", key, "Drive to a point. Right-clicking the map does the same.");
                case OrderKind.AttackMove:
                    return new HoverCardContent("Attack-move", key, "Move there and fight anything on the way.").Line($"{queueKey} + {key}: add to the queue");
                case OrderKind.Stop: return new HoverCardContent("Stop", key, "Drop every order now.");
                case OrderKind.Hold: return new HoverCardContent("Hold", key, "Stay put; fire only at what is in range.");
                case OrderKind.Repair:
                    return new HoverCardContent("Repair", key, "Builders fix an own or allied building or wall.").Pair("Cost", $"{repairCostPerHealth:0.##} per health", HoverTone.Cost);
                case OrderKind.Dig: return new HoverCardContent("Dig", key, "Diggers dig toward a rock, gem or wall.").Line("Drag a box: dig the whole area");
                case OrderKind.Detonate: return new HoverCardContent("Detonate", key, "Every selected bomber explodes where it stands. Hurts friends too.");
                default: return new HoverCardContent(kind.ToString(), key);
            }
        }

        /// <summary>Why an order button is greyed out, or null.</summary>
        public static string OrderReason(OrderKind kind, bool units)
        {
            if (units) return null;
            return kind switch
            {
                OrderKind.Repair => "Needs a Builder selected",
                OrderKind.Dig => "Needs a Digger selected",
                OrderKind.Detonate => "Needs a Suicide bomber selected",
                _ => "Needs units selected",
            };
        }

        /// <summary>"Need 150 (you have 90)" when the player can't afford <paramref name="cost"/>, else null.</summary>
        public static string Afford(float cost, float have) => have >= cost ? null : $"Need {cost:0} (you have {System.Math.Floor(have):0})";

        /// <summary>One sentence on what a unit type is for (the production hover card's role line).</summary>
        public static string UnitRole(UnitType type) => type switch
        {
            UnitType.Builder => "Builds, repairs and lays walls.",
            UnitType.Tank => "The all-round fighting vehicle.",
            UnitType.Digger => "Digs through rock, gems and enemy walls.",
            UnitType.Scout => "Fast and far-sighted; lightly armed.",
            UnitType.Heavy => "Slow, armoured front-line tank.",
            UnitType.Artillery => "Long-range splash; can't fire up close.",
            UnitType.Sniper => "Very long range; needs something to spot for it.",
            UnitType.Flamethrower => "Short-range splash that burns through groups.",
            UnitType.SuicideBomber => "Drives in and explodes. Hurts friends too.",
            _ => null,
        };

        /// <summary>A production type's card: role, then cost, build time, health, damage (or blast) and range.</summary>
        public static HoverCardContent Unit(UnitType type, UnitConfig u, float have)
        {
            var card = new HoverCardContent(AlertFeedModel.UnitName(type), null, UnitRole(type));
            if (u == null) return card;
            card.Pair("Cost", $"{u.UpfrontCost:0}", have >= u.UpfrontCost ? HoverTone.Cost : HoverTone.Danger)
                .Pair("Build", $"{u.BuildSeconds:0.#} s")
                .Pair("Health", $"{u.Health:0}");
            if (u.BlastRadius > 0f) card.Pair("Blast", $"{u.BlastDamage:0} · {u.BlastRadius:0.#}");
            else if (u.Damage > 0)
            {
                card.Pair("Damage", $"{u.Damage}").Pair("Range", $"{u.Range:0.#}");
                if (u.MinRange > 0f) card.Pair("Min", $"{u.MinRange:0.#}");
                if (u.Splash > 0f) card.Pair("Splash", $"{u.Splash:0.#}");
            }
            if (u.RunningCost > 0f) card.Pair("Upkeep", $"{u.RunningCost:0.#}/s");
            return card;
        }

        /// <summary>The Rally all button's card (the production card's Rally point row).</summary>
        public static HoverCardContent RallyAll(string key) =>
            new HoverCardContent("Rally all", key, "Next click sets the rally point of every producer you own, not just these.")
                .Line("On an own blueprint: Builders help build it.");

        /// <summary>The next-alert bar's card.</summary>
        public static HoverCardContent NextAlert(string key) =>
            new HoverCardContent("Next alert", key, "Centres on the newest unvisited alert with a place, then the one before it.");

        /// <summary>The quick-select counts' cards (frame 1).</summary>
        public static HoverCardContent IdleBuilders(string key, string assignKey) =>
            new HoverCardContent("Idle Builders", key, "Builders with nothing to do.").Line("Press again: next idle Builder").Line($"{assignKey} + {key}: all Builders");

        public static HoverCardContent AllBuilders(string key, string assignKey) =>
            new HoverCardContent("All Builders", $"{assignKey} {key}", "Selects every Builder you own.");

        public static HoverCardContent Army(string key) =>
            new HoverCardContent("Army", key, "Every unit that can attack.").Line("Press again: add Diggers and bombers");

        /// <summary>The HQ button's card (frame 2).</summary>
        public static HoverCardContent Hq(string key) =>
            new HoverCardContent("Jump to HQ", key, "Centres the camera on your HQ.").Line("Press twice: also select it");

        /// <summary>The net readout's card in state B and C (frame 4).</summary>
        public static HoverCardContent Net(EconomyReadout economy)
        {
            int net = (int)System.Math.Round(economy.NetPerSecond);
            var card = new HoverCardContent($"Net {(net >= 0 ? "+" : "−")}{System.Math.Abs(net):N0}/s", null, "Income minus upkeep, averaged over 5 s.");
            if (economy.State == EconomyState.Negative || economy.State == EconomyState.RunningLow)
                card.Role = "Upkeep is higher than income.";
            if (economy.State != EconomyState.Healthy) card.Line("Units and buildings decay when you run out.");
            if (economy.State == EconomyState.Decaying) card.Line("Click: select what is decaying");
            return card;
        }

        /// <summary>A squad slot's card: a unit squad, a building group, or an empty slot (frame 6).</summary>
        public static HoverCardContent Slot(int slot, int units, int buildings, string buildingName, string assignKey)
        {
            int key = (slot + 1) % 10;
            string title = buildings > 0 ? $"Group {key}: {buildings} {Plural(buildingName, buildings)}"
                : units > 0 ? $"Squad {key}: {units:N0} unit{(units == 1 ? "" : "s")}" : $"Group {key}: empty";
            var card = new HoverCardContent(title, key.ToString());
            if (buildings > 0 || units > 0) card.Line("Press twice: centre on the first");
            card.Line($"{assignKey} + {key}: save the selection here");
            return card;
        }

        private static string Plural(string name, int n) => n == 1 || string.IsNullOrEmpty(name) || name.EndsWith("s") ? name : name + "s";
    }
}
