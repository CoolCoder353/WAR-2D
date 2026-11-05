using System;
using System.Collections.Generic;
using Config;
using UnityEngine;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// The Field manual's screen (v0.18, Figma F1–F3): a side list of five sections, a search box and one page at a time, built
    /// from <see cref="ManualModel"/> each time it opens, so every number and key is current. It adds itself to the given root, so
    /// the menus and the HUD each have their own. Its motion is the v0.16 menus' (M1); Reduce motion turns all of it off.
    /// </summary>
    public sealed class ManualController
    {
        private const string LastPageKey = "war2d.manual.page";
        private static int openCount;

        /// <summary>True while any manual is open: the keyboard belongs to it, not to the game's order keys.</summary>
        public static bool AnyOpen => openCount > 0;

        private readonly VisualElement screen, window, nav, pageHost;
        private readonly TextField search;
        private readonly ScrollView scroll;
        private readonly Dictionary<string, Button> sections = new Dictionary<string, Button>();
        private readonly List<(VisualElement element, Texture2D[][] frames, bool turn)> animated = new List<(VisualElement, Texture2D[][], bool)>();
        private readonly IVisualElementScheduledItem ticker;
        private ManualModel model;
        private GameConfigData config;
        private string current;
        private int tick;
        private bool open;

        /// <summary>The Controls page's button.</summary>
        public event Action SettingsRequested;

        /// <summary>The manual closed.</summary>
        public event Action Closed;

        public bool IsOpen => open;

        /// <summary>The page showing (tests).</summary>
        public string CurrentPage => current;

        public ManualController(VisualElement root)
        {
            screen = new VisualElement { name = "manual" };
            screen.AddToClassList("overlay-screen");
            screen.AddToClassList("manual");
            window = new VisualElement { name = "manual-window" };
            window.AddToClassList("panel");
            window.AddToClassList("manual__window");
            screen.Add(window);

            var bar = new VisualElement();
            bar.AddToClassList("manual__bar-top");
            var title = new Label("Field manual");
            title.AddToClassList("manual__title");
            bar.Add(title);
            search = new TextField { name = "manual-search", textEdition = { placeholder = "Search units, buildings, rules", hidePlaceholderOnFocus = true } };
            search.AddToClassList("input__field");
            search.AddToClassList("manual__search");
            search.RegisterValueChangedCallback(e => OnSearch(e.newValue));
            bar.Add(search);
            Button close = Widgets.TextButton("manual-close", "Close", false, Close);
            bar.Add(close);
            window.Add(bar);

            var body = new VisualElement();
            body.AddToClassList("manual__body");
            nav = new VisualElement { name = "manual-nav" };
            nav.AddToClassList("manual__nav");
            foreach ((string id, string name) in ManualModel.SectionsInOrder)
            {
                string section = id;
                Button b = Widgets.TextButton("manual-section-" + id, name, false, () => Go(section));
                sections[id] = b;
                nav.Add(b);
            }
            body.Add(nav);
            scroll = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden, name = "manual-scroll" };
            scroll.AddToClassList("manual__scroll");
            pageHost = new VisualElement { name = "manual-page" };
            pageHost.AddToClassList("manual__page");
            scroll.Add(pageHost);
            body.Add(scroll);
            window.Add(body);

            root.Add(screen);
            screen.style.display = DisplayStyle.None;
            ticker = screen.schedule.Execute(Animate).Every(100); // the game's 10 fps
            ticker.Pause();
            // Open when its scene unloads, it would leave AnyOpen true for good, and all map input waits on that.
            screen.RegisterCallback<DetachFromPanelEvent>(_ => Close());
        }

        /// <summary>Opens the manual: on a page, or on the last one opened on this machine.</summary>
        /// <param name="fullScreen">From the main menu; otherwise it opens over what is behind it.</param>
        public void Open(string page = null, bool fullScreen = false)
        {
            config = ConfigLoader.LoadConfig();
            model = new ManualModel(config, KeyOf, Hex(WAR2D.Client.Overlays.OverlayColours.Readout(Palettes.Current)), Hex(Palettes.Accent(Palettes.Current)));
            screen.EnableInClassList("manual--full", fullScreen);
            screen.style.display = DisplayStyle.Flex;
            screen.AddToClassList("overlay-screen--visible");
            if (!open) openCount++;
            open = true;
            search.SetValueWithoutNotify("");
            MenuMotion.ApplySetting(screen.panel?.visualTree);
            MenuMotion.Enter(screen, "overlay-screen--enter");
            string start = page ?? PlayerPrefs.GetString(LastPageKey, "units");
            Show(model.Pages.ContainsKey(start) ? start : "units", 0);
            if (!SettingsStore.Current.ReduceMotion) ticker.Resume();
        }

        public void Close()
        {
            if (!open) return;
            open = false;
            openCount--;
            ticker.Pause();
            screen.RemoveFromClassList("overlay-screen--visible");
            screen.style.display = DisplayStyle.None;
            search.Blur();
            Closed?.Invoke();
        }

        /// <summary>Goes to a page, sliding in from the right (from the left when it goes back up to its section).</summary>
        public void Go(string id)
        {
            if (model == null || !model.Pages.TryGetValue(id, out ManualPage page)) return;
            bool back = current != null && model.Pages.TryGetValue(current, out ManualPage from) && from.Section == page.Section && page.Id == page.Section;
            search.SetValueWithoutNotify("");
            Show(id, back ? -1 : 1);
        }

        private static string KeyOf(string action)
        {
            UnityEngine.InputSystem.InputAction a = GameInput.Map?.FindAction(action);
            return a == null ? action : HoverText.KeyChip(Rebinding.Display(a));
        }

        private static string Hex(Color32 c) => $"#{c.r:X2}{c.g:X2}{c.b:X2}";

        private void Show(string id, int direction)
        {
            current = id;
            PlayerPrefs.SetString(LastPageKey, id);
            ManualPage page = model.Pages[id];
            foreach (KeyValuePair<string, Button> s in sections) s.Value.EnableInClassList("button--primary", s.Key == page.Section);
            animated.Clear();
            pageHost.Clear();
            Fill(page);
            scroll.scrollOffset = Vector2.zero;
            Slide(direction);
            Animate();
        }

        private void Slide(int direction)
        {
            if (direction == 0 || SettingsStore.Current.ReduceMotion) return;
            string from = direction > 0 ? "manual__page--from-right" : "manual__page--from-left";
            pageHost.AddToClassList(from);
            pageHost.schedule.Execute(() => pageHost.RemoveFromClassList(from)).ExecuteLater(16);
        }

        private void OnSearch(string query)
        {
            if (model == null) return;
            if (string.IsNullOrWhiteSpace(query))
            {
                Show(current ?? "units", 0);
                return;
            }
            animated.Clear();
            pageHost.Clear();
            List<ManualPage> hits = model.Search(query);
            pageHost.Add(Heading(hits.Count == 0 ? $"Nothing called \"{query.Trim()}\"" : $"{hits.Count} {(hits.Count == 1 ? "page" : "pages")} for \"{query.Trim()}\""));
            var list = new VisualElement();
            list.AddToClassList("manual__cards");
            foreach (ManualPage hit in hits) list.Add(CardFor(hit));
            pageHost.Add(list);
        }

        private void Fill(ManualPage page)
        {
            if (page.Cards.Count > 0)
            {
                pageHost.Add(Heading(page.Title));
                var cards = new VisualElement();
                cards.AddToClassList("manual__cards");
                foreach (string card in page.Cards) cards.Add(CardFor(model.Pages[card]));
                pageHost.Add(cards);
                return;
            }
            if (page.Id == "controls") { FillControls(); return; }
            if (page.Section != page.Id) pageHost.Add(Crumb(page));
            if (page.Sprite != null) { FillHeadPage(page); return; }
            pageHost.Add(Heading(page.Title));
            if (page.Lead != null) pageHost.Add(Rich(page.Lead, "manual__lead"));
            if (page.Grid.Count > 0) FillGrid(page);
            if (page.Diagram != null)
            {
                VisualElement diagram = ManualDiagrams.Build(page.Diagram, config);
                if (diagram != null) pageHost.Add(diagram);
            }
            foreach (string p in page.Body) pageHost.Add(Rich(p, "manual__text"));
            foreach (ManualSection s in page.Sections)
            {
                pageHost.Add(Sub(s.Heading));
                foreach (string p in s.Paragraphs) pageHost.Add(Rich(p, "manual__text"));
            }
        }

        private void FillHeadPage(ManualPage page)
        {
            var head = new VisualElement();
            head.AddToClassList("manual__head");
            head.Add(Art(page.Sprite, 104f, 120f, true, true));
            var words = new VisualElement();
            words.Add(Heading(page.Title));
            var role = new Label(page.Role);
            role.AddToClassList("manual__role");
            words.Add(role);
            head.Add(words);
            pageHost.Add(head);

            var split = new VisualElement();
            split.AddToClassList("manual__split");
            var stats = new VisualElement();
            stats.AddToClassList("manual__stats");
            foreach ((string label, string value, string link) in page.Stats)
            {
                var row = new VisualElement();
                row.AddToClassList("manual__stat");
                var k = new Label(label);
                k.AddToClassList("manual__stat-label");
                row.Add(k);
                if (link != null)
                {
                    string to = link;
                    Button b = Widgets.TextButton("manual-link-" + to, value, false, () => Go(to));
                    b.AddToClassList("manual__link");
                    row.Add(b);
                }
                else
                {
                    var v = new Label(value);
                    v.AddToClassList("manual__stat-value");
                    row.Add(v);
                }
                stats.Add(row);
            }
            split.Add(stats);

            var prose = new VisualElement();
            prose.AddToClassList("manual__prose");
            foreach (string p in page.Body) prose.Add(Rich(p, "manual__text"));
            if (page.Makes.Count > 0)
            {
                prose.Add(Sub("Makes"));
                prose.Add(Minis(page.Makes));
            }
            if (page.Damage.Count > 0)
            {
                prose.Add(Sub("Damage"));
                prose.Add(DamageTable(page));
            }
            if (page.HasCounters)
            {
                var counters = new VisualElement();
                counters.AddToClassList("manual__counters");
                var strong = new VisualElement();
                strong.Add(Sub("Strong against"));
                strong.Add(Minis(page.Strong));
                var weak = new VisualElement();
                weak.Add(Sub("Weak against"));
                weak.Add(Minis(page.Weak));
                counters.Add(strong);
                counters.Add(weak);
                prose.Add(counters);
                prose.Add(Rich(page.CounterNote, "manual__note"));
            }
            if (page.Note != null) prose.Add(Rich(page.Note, "manual__note"));
            split.Add(prose);
            pageHost.Add(split);
        }

        private static VisualElement DamageTable(ManualPage page)
        {
            bool blast = page.Damage[0].PerSecond == null;
            var table = new VisualElement();
            table.AddToClassList("manual__damage");
            table.Add(DamageRow("Against", "×", blast ? "Blast" : "Per shot", blast ? null : "Per second", true));
            foreach (ManualDamageRow r in page.Damage) table.Add(DamageRow(r.Against, r.Multiplier, r.PerShot, r.PerSecond, false));
            return table;
        }

        private static VisualElement DamageRow(string a, string b, string c, string d, bool head)
        {
            var row = new VisualElement();
            row.AddToClassList("manual__damage-row");
            row.EnableInClassList("manual__damage-row--head", head);
            var first = new Label(a);
            first.AddToClassList("manual__damage-name");
            row.Add(first);
            foreach (string cell in new[] { b, c, d })
            {
                if (cell == null) continue;
                var l = new Label(cell);
                l.AddToClassList("manual__damage-cell");
                row.Add(l);
            }
            return row;
        }

        private void FillGrid(ManualPage page)
        {
            var readout = new Label("Hover a cell for its damage.") { name = "manual-grid-readout" };
            readout.AddToClassList("manual__readout");
            var grid = new VisualElement { name = "manual-grid" };
            grid.AddToClassList("manual__grid");
            var head = new VisualElement();
            head.AddToClassList("manual__grid-row");
            foreach (string name in new[] { "Attacker", "Light units", "Heavy units", "Buildings", "Walls" })
            {
                var l = new Label(name);
                l.AddToClassList(name == "Attacker" ? "manual__grid-name" : "manual__grid-cell");
                l.AddToClassList("manual__grid-head");
                head.Add(l);
            }
            grid.Add(head);
            foreach (ManualGridRow row in page.Grid)
            {
                var r = new VisualElement();
                r.AddToClassList("manual__grid-row");
                string to = row.PageId;
                Button name = Widgets.TextButton("manual-grid-" + to, row.Name, false, () => Go(to));
                name.AddToClassList("manual__link");
                name.AddToClassList("manual__grid-name");
                r.Add(name);
                foreach (ManualGridCell cell in row.Cells)
                {
                    string detail = cell.Detail;
                    var c = new Label(ManualModel.Number(cell.Multiplier));
                    c.AddToClassList("manual__grid-cell");
                    c.AddToClassList("manual__heat-" + Heat(cell.Multiplier));
                    c.RegisterCallback<PointerEnterEvent>(_ => readout.text = detail);
                    r.Add(c);
                }
                grid.Add(r);
            }
            pageHost.Add(grid);
            pageHost.Add(readout);
        }

        /// <summary>A multiplier's step on the weak-to-strong scale, as on the approved frame.</summary>
        public static int Heat(float m) => m == 0f ? 0 : m < 0.5f ? 1 : m < 1f ? 2 : m == 1f ? 3 : m <= 1.5f ? 4 : m <= 3f ? 5 : 6;

        private void FillControls()
        {
            var title = new VisualElement();
            title.AddToClassList("manual__title-row");
            title.Add(Heading("Controls"));
            title.Add(Widgets.TextButton("manual-to-settings", "Change in Settings", false, () => { Close(); SettingsRequested?.Invoke(); }));
            pageHost.Add(title);
            pageHost.Add(Rich("Your keys as they are set now.", "manual__lead"));
            foreach ((string group, List<ManualBinding> rows) in model.Controls)
            {
                pageHost.Add(Sub(group));
                foreach (ManualBinding b in rows)
                {
                    var row = new VisualElement();
                    row.AddToClassList("manual__stat");
                    var label = new Label(b.Label);
                    label.AddToClassList("manual__bind-label");
                    row.Add(label);
                    var keys = new Label(b.Keys);
                    keys.AddToClassList("manual__stat-value");
                    row.Add(keys);
                    pageHost.Add(row);
                }
            }
        }

        private VisualElement CardFor(ManualPage page)
        {
            string id = page.Id;
            var card = new Button(() => Go(id)) { name = "manual-card-" + id, text = "" };
            card.AddToClassList("manual__card");
            if (page.Sprite != null) card.Add(Art(page.Sprite, 72f, 88f, false, true));
            var name = new Label(page.Title) { pickingMode = PickingMode.Ignore };
            name.AddToClassList("manual__card-name");
            card.Add(name);
            string line = page.Role ?? page.Lead;
            if (line != null)
            {
                var role = new Label(StripTags(line)) { pickingMode = PickingMode.Ignore };
                role.AddToClassList("manual__card-role");
                card.Add(role);
            }
            return card;
        }

        private static string StripTags(string s) => System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");

        private VisualElement Art(string type, float box, float frame, bool turn, bool animate)
        {
            var holder = new VisualElement { pickingMode = PickingMode.Ignore };
            holder.AddToClassList("manual__art");
            holder.style.height = frame;
            if (turn) holder.style.width = frame;
            Texture2D[][] frames = ManualSprites.Frames(type, PlayerPalette.Of(0), turn);
            if (frames == null)
            {
                var missing = new Label(ManualModel.NameOf(type)) { pickingMode = PickingMode.Ignore };
                missing.AddToClassList("manual__card-role");
                holder.Add(missing);
                return holder;
            }
            var sprite = new VisualElement { pickingMode = PickingMode.Ignore };
            Texture2D first = frames[0][0];
            float scale = Mathf.Min(box / first.width, box / first.height);
            sprite.style.width = first.width * scale;
            sprite.style.height = first.height * scale;
            sprite.style.backgroundImage = new StyleBackground(first);
            holder.Add(sprite);
            if (animate) animated.Add((sprite, frames, turn));
            return holder;
        }

        private void Animate()
        {
            tick++;
            foreach ((VisualElement element, Texture2D[][] frames, bool turn) in animated)
            {
                int facing = turn ? tick / 8 % frames.Length : 0; // a turn every 0.8 s
                Texture2D[] loop = frames[facing];
                element.style.backgroundImage = new StyleBackground(loop[tick % loop.Length]);
            }
        }

        private VisualElement Minis(List<string> types)
        {
            var row = new VisualElement();
            row.AddToClassList("manual__minis");
            if (types.Count == 0)
            {
                var none = new Label("No clear match-up");
                none.AddToClassList("manual__note");
                row.Add(none);
            }
            foreach (string type in types)
            {
                string id = type == "Wall" ? "building-Wall" : Enum.TryParse(type, out UnitType _) ? "unit-" + type : "building-" + type;
                var b = new Button(() => Go(id)) { name = "manual-mini-" + type, text = "" };
                b.AddToClassList("manual__mini");
                b.Add(Art(type, 32f, 32f, false, false));
                var l = new Label(ManualModel.NameOf(type)) { pickingMode = PickingMode.Ignore };
                l.AddToClassList("manual__mini-name");
                b.Add(l);
                row.Add(b);
            }
            return row;
        }

        private VisualElement Crumb(ManualPage page)
        {
            string name = "";
            foreach ((string id, string n) in ManualModel.SectionsInOrder) if (id == page.Section) name = n;
            string section = page.Section;
            Button b = Widgets.TextButton("manual-crumb", "‹ " + name, false, () => Go(section));
            b.AddToClassList("manual__link");
            b.AddToClassList("manual__crumb");
            return b;
        }

        private static Label Heading(string text)
        {
            var l = new Label(text);
            l.AddToClassList("manual__heading");
            return l;
        }

        private static Label Sub(string text)
        {
            var l = new Label(text);
            l.AddToClassList("manual__sub");
            return l;
        }

        private Label Rich(string text, string cls)
        {
            var l = new Label(text) { enableRichText = true };
            l.AddToClassList(cls);
            l.RegisterCallback<UnityEngine.UIElements.Experimental.PointerUpLinkTagEvent>(e => Go(e.linkID));
            return l;
        }
    }
}
