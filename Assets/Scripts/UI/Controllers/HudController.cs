using UnityEngine;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// The match HUD: owns the <see cref="HudModel"/>, refreshes it from client data, and wires the
    /// section controllers to <c>Assets/UI/Hud/Hud.uxml</c>. Lives on the <see cref="UIDocument"/> in Map_2.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class HudController : MonoBehaviour
    {
        private const float PlayerRefreshSeconds = 0.25f;

        private TopBarController topBar;
        private float playerTimer;

        /// <summary>The HUD's data.</summary>
        public HudModel Model { get; } = new HudModel();

        /// <summary>The top bar's controller (its buttons are wired by later HUD sections).</summary>
        public TopBarController TopBar => topBar;

        private void OnEnable()
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            topBar = new TopBarController(root, Model);
        }

        private void OnDisable()
        {
            topBar?.Dispose();
            topBar = null;
        }

        private void Update()
        {
            Model.PullResources();
            playerTimer -= Time.unscaledDeltaTime;
            if (playerTimer > 0f) return;
            playerTimer = PlayerRefreshSeconds;
            Model.PullPlayers();
        }
    }
}
