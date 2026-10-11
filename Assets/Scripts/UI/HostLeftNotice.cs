using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// The approved <c>Dialog / Host left – v0.8</c>: when a joined player's connection drops mid-match (the
    /// host's game closed; Mirror's host-client model ends the match with it), Mirror loads the main menu,
    /// and this dialog says why instead of leaving the player wondering. Leaving on purpose (the in-match
    /// menu or the end screen) never shows it.
    /// </summary>
    public sealed class HostLeftNotice : MonoBehaviour
    {
        public const string MenuScene = "Main_Menu";

        /// <summary>Set when the dialog should show on the next main-menu load.</summary>
        public static bool Pending { get; private set; }

        /// <summary>Set by the leave buttons just before they disconnect.</summary>
        public static bool LeavingOnPurpose;

        /// <summary>True when a client that is not the host lost its connection during a match it did not leave.</summary>
        public static bool ShouldNotify(bool isHost, bool onPurpose, GameState state) =>
            !isHost && !onPurpose && (state == GameState.PlacingHQ || state == GameState.Countdown || state == GameState.Playing);

        /// <summary>Called as the local client stops.</summary>
        public static void ClientStopped(bool isHost, GameState state)
        {
            Pending = ShouldNotify(isHost, LeavingOnPurpose, state);
            LeavingOnPurpose = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!Pending || scene.name != MenuScene) return;
            Pending = false;
            UIDocument menu = Object.FindAnyObjectByType<UIDocument>();
            if (menu == null || menu.panelSettings == null) return;
            var go = new GameObject("HostLeftNotice");
            var document = go.AddComponent<UIDocument>();
            document.panelSettings = menu.panelSettings;
            document.sortingOrder = menu.sortingOrder + 10;
            go.AddComponent<HostLeftNotice>();
        }

        private void Start()
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            root.style.alignItems = Align.Center;
            root.style.justifyContent = Justify.Center;
            root.style.position = Position.Absolute;
            root.style.left = root.style.right = root.style.top = root.style.bottom = 0;

            var panel = new VisualElement { name = "host-left-dialog" };
            panel.AddToClassList("panel");
            panel.style.width = 460;
            var title = new Label("The host left the match") { name = "host-left-title" };
            title.AddToClassList("panel__title");
            var body = new VisualElement();
            body.style.paddingLeft = body.style.paddingRight = body.style.paddingTop = body.style.paddingBottom = 12;
            var line = new Label("The match ended because the host closed their game.");
            line.AddToClassList("text--secondary");
            var ok = new Button(() => Destroy(gameObject)) { text = "Main menu", name = "host-left-ok" };
            ok.AddToClassList("button");
            ok.style.width = 150;
            ok.style.marginTop = 12;
            body.Add(line);
            body.Add(ok);
            panel.Add(title);
            panel.Add(body);
            root.Add(panel);
        }
    }
}
