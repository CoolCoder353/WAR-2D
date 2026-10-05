using System;
using NaughtyAttributes;
using UnityEngine;

namespace TIM
{
    [CreateAssetMenu(fileName = "Console Settings", menuName = "WAR/Console/Settings", order = 1)]
    public class ConsoleSetting : ScriptableObject
    {
        [BoxGroup("0")] public bool Enabled = true;
        [Tooltip("Console will work in build only if you check 'DevelopmentBuild' in BuildSettings")]
        [BoxGroup("0"), ShowIf(nameof(Enabled))] public bool EnabledInDevelopmentBuildOnly = true;
        [Tooltip("messages from Debug.Log(), print(), exceptions etc will be displayed")]
        [BoxGroup("0"), ShowIf(nameof(Enabled))] public bool CopyDebugLogs = true;
        [Tooltip("Disable if you don't need nice audio of Console")]
        [BoxGroup("0"), ShowIf(nameof(Enabled))] public bool AudioEnabled = true;
        [Tooltip("If you don't need Console's EventSystem, if you have yours, you can disable it.")]
        [BoxGroup("0"), ShowIf(nameof(Enabled))] public bool EventSystemEnabled = true;

        [BoxGroup("Parameters")] public UnityEngine.InputSystem.Key ToggleKey = UnityEngine.InputSystem.Key.Backquote;
        [BoxGroup("Parameters")] public int MaxTitleLength = 3000;
        [BoxGroup("Parameters")] public int MaxStacktraceLength = 3000;

        [Label("Stacktrace: Highlights file paths and code paths in stacktrace."), ReadOnly]
        public bool StacktraceHighlight = true;
        [Foldout("Colors")] public Color StacktraceDefaultColor = Color.white;
        [Foldout("Colors")] public Color StacktraceFilePathColor = Color.cyan;
        [Foldout("Colors")] public Color StacktraceCodePathColor = Color.yellow;

        [Label("Messages: You can't change it for now."), ReadOnly]
        public bool MessageHighlight = true;
        [Foldout("Colors"), DisableIf("@true")] public Color DefaultColor = Color.white;
        [Foldout("Colors"), DisableIf("@true")] public Color ErrorColor = Color.red;
        [Foldout("Colors"), DisableIf("@true")] public Color WarningColor = Color.yellow;
        [Foldout("Colors"), DisableIf("@true")] public Color NetworkColor = new Color(0, 0.5f, 1);

        [Foldout("Links")] public Console ConsoleCanvasPrefab;
        [Foldout("Links")] public Sprite ErrorMessageIcon;
        [Foldout("Links")] public Sprite WarningMessageIcon;
        [Foldout("Links")] public Sprite DefaultMessageIcon;
        [Foldout("Links")] public Sprite NetworkMessageIcon;

        public Sprite GetIcon(MessageType messageType)
        {
            switch (messageType)
            {
                case MessageType.Default: return DefaultMessageIcon;
                case MessageType.Error: return ErrorMessageIcon;
                case MessageType.Warning: return WarningMessageIcon;
                case MessageType.Network: return NetworkMessageIcon;
                default:
                    throw new ArgumentOutOfRangeException(nameof(messageType), messageType, null);
            }
        }

        public Color GetColor(MessageType messageType)
        {
            switch (messageType)
            {
                case MessageType.Default: return DefaultColor;
                case MessageType.Error: return ErrorColor;
                case MessageType.Warning: return WarningColor;
                case MessageType.Network: return NetworkColor;
                default:
                    throw new ArgumentOutOfRangeException(nameof(messageType), messageType, null);
            }
        }

        public Color GetBgColor(MessageType messageType, bool highlight = false)
        {
            Color c = GetColor(messageType);
            c.a = highlight ? 25 / 255f : 5 / 255f;
            return c;
        }

        #region Static realization

        public static ConsoleSetting Instance
        {
            get
            {
                if (!_instance)
                {
                    _instance = Resources.Load<ConsoleSetting>("TIM/Console Setting");
                }

                return _instance;
            }
        }

        private static ConsoleSetting _instance;

        #endregion
    }
}