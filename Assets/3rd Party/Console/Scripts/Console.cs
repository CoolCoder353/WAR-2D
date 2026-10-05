using System;
using System.Collections;
using System.Collections.Generic;

using NaughtyAttributes;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;

namespace TIM
{
    public class Console : MonoBehaviour
    {
        [BoxGroup("Events")] public UnityEvent<bool> ToggleEvent = new UnityEvent<bool>();

        [BoxGroup("Formulas")] public CmdFormula ClearFormula;

        [BoxGroup("Links")] public CommandsList CommandsList;
        [BoxGroup("Links")] public ConsoleInputPanel InputPanel;
        [BoxGroup("Links")] public GameObject GlobalWindow;
        [BoxGroup("Links")] public LogchainWindow LogchainWindow;
        [BoxGroup("Links")] public ConsoleMessageInspector MessageInspector;
        [BoxGroup("Links")] public ConsoleConfigurator ConsoleConfigurator;
        [BoxGroup("Links")] public ConsoleMessagesContent MessagesContent;
        [BoxGroup("Links")] public ConsoleAudio ConsoleAudio;
        [BoxGroup("Links")] public Animation Animation;
        [BoxGroup("Links")] public EventSystem EventSystem;

        public static Console Instance { private set; get; }
        public static Dictionary<CmdFormula, UnityAction<CmdInputResult>> RegisteredCommands = new Dictionary<CmdFormula, UnityAction<CmdInputResult>>();

        public bool Opened { private set; get; }
        public int MessageCount_Default { private set; get; }
        public int MessageCount_Error { private set; get; }
        public int MessageCount_Warning { private set; get; }
        public int MessageCount_Network { private set; get; }

        [NonSerialized] public List<Logchain> LogchainsList = new List<Logchain>();
        [NonSerialized] public List<ConsoleMessage> MessagesList = new List<ConsoleMessage>();



        #region Unity's methods

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            EventSystem.gameObject.SetActive(ConsoleSetting.Instance.EventSystemEnabled);
            Application.logMessageReceived += OnDebugLog;

            Opened = false;
            GlobalWindow.SetActive(false);

            RegisterCommand(ClearFormula, result => Clear());
        }

        private void Update()
        {
            if ((UnityEngine.InputSystem.Keyboard.current?[ConsoleSetting.Instance.ToggleKey].wasPressedThisFrame ?? false))
            {
                ToggleConsole();
            }
            else if (Opened && (UnityEngine.InputSystem.Keyboard.current?.enterKey.wasPressedThisFrame ?? false))
            {
                InputPanel.OnEnterPressed();
            }
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= OnDebugLog;
        }

        #endregion

        #region Static methods

        /// <summary>
        /// 
        /// </summary>
        /// <param name="title">Your log. For example: "Player died"</param>
        /// <param name="messageType">type of the message. If it's simple log, use MessageType.Default. If you are creating multiplayer game, you can use MessageType.Network to control synchronization</param>
        /// <param name="logchain">name of logchain. You you want to group your logs into Logchain you can name it and inspect in console.</param>
        /// <param name="hidden">true if you want to mark message as hidden. Hidden messages useful for large amount of messages. Hidden messages can't be hidden from logchain. You can toggle their visibility in console.</param>
        public static void Log(string title, MessageType messageType = MessageType.Default, string logchain = null, bool hidden = false)
        {
            if (!Instance)
                return;

            Instance.PrintMessage(title, messageType, System.Environment.StackTrace, logchain, hidden);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void OnLoad()
        {
            if (!Debug.isDebugBuild && ConsoleSetting.Instance.EnabledInDevelopmentBuildOnly)
                return;

            if (Instance)
                return;

            var obj = Instantiate(ConsoleSetting.Instance.ConsoleCanvasPrefab);
            DontDestroyOnLoad(obj.gameObject);
        }

        /// <summary>
        /// Register command. You need a CmdFormula to register command.
        /// </summary>
        /// <param name="formula">Formula of your command. You can modify it in runtime.</param>
        /// <param name="callback">The method that will be executed when the command is entered into the console.</param>
        public static void RegisterCommand(CmdFormula formula, UnityAction<CmdInputResult> callback)
        {
            if (RegisteredCommands.ContainsKey(formula))
            {
                RegisteredCommands[formula] = callback;
                if (Instance)
                    Instance.CommandsList.RefreshCommands();
            }
            else
            {
                RegisteredCommands.Add(formula, callback);
                if (Instance)
                    Instance.CommandsList.AddNewCommand(formula);
            }
        }

        /// <summary>
        /// Use it if you want to remove command from list of commands.
        /// </summary>
        /// <param name="formula">Formula which was used to register command</param>
        public static void UnregisterCommand(CmdFormula formula)
        {
            RegisteredCommands.Remove(formula);
            if (Instance)
                Instance.CommandsList.RemoveCommand(formula);
        }

        /// <summary>
        /// Clear the console
        /// </summary>
        public static void Clear()
        {
            Debug.ClearDeveloperConsole();

            if (Instance)
            {
                Instance.MessageCount_Default = 0;
                Instance.MessageCount_Warning = 0;
                Instance.MessageCount_Error = 0;
                Instance.MessageCount_Network = 0;
                Instance.MessagesList.Clear();
                Instance.LogchainsList.Clear();
                Instance.MessagesContent.Repaint();
                Instance.LogchainWindow.Clear();
                Instance.ConsoleConfigurator.OnMessageCountChanged();
            }
        }

        #endregion

        #region Public methods

        /// <summary>
        /// Method to create new message in console.
        /// </summary>
        /// <param name="title">title of message</param>
        /// <param name="messageType">type of messsage</param>
        /// <param name="stacktrace">stacktrace of message. To get it you can use System.Environment.StackTrace</param>
        /// <param name="logchain">name of logchain. You you want to group your logs into Logchain you can name it and inspect in console.</param>
        /// <param name="hidden">true if you want to mark message as hidden. Hidden messages useful for large amount of messages. Hidden messages can't be hidden from logchain. You can toggle their visibility in console.</param>
        public void PrintMessage(string title, MessageType messageType, string stacktrace, string logchain = null, bool hidden = false)
        {
            ConsoleMessage consoleMessage = new ConsoleMessage(title, messageType, stacktrace, MessagesList.Count, logchain, hidden);

            // add 1 to counter:
            switch (messageType)
            {
                case MessageType.Default:
                    MessageCount_Default++;
                    break;
                case MessageType.Error:
                    MessageCount_Error++;
                    break;
                case MessageType.Warning:
                    MessageCount_Warning++;
                    break;
                case MessageType.Network:
                    MessageCount_Network++;
                    break;
                default:
                    messageType = MessageType.Default;
                    MessageCount_Default++;
                    break;
            }
            ConsoleConfigurator.OnMessageCountChanged();

            // find original message if its exists:
            for (var i = 0; i < MessagesList.Count; i++)
            {
                ConsoleMessage m = MessagesList[i];
                if (m.OriginalMessage != null)
                    continue;

                if (ConsoleAlgorithms.CompareMessages(m, consoleMessage))
                {
                    m.CopiesCount++;
                    consoleMessage.OriginalMessage = m;
                    consoleMessage.LogchainReference = m.LogchainReference;
                    break;
                }
            }

            // find logchain:
            if (consoleMessage.OriginalMessage == null && !string.IsNullOrWhiteSpace(consoleMessage.Logchain))
            {
                foreach (Logchain l in LogchainsList)
                {
                    if (l.Title == consoleMessage.Logchain)
                    {
                        consoleMessage.LogchainReference = l;
                        break;
                    }
                }

                if (consoleMessage.LogchainReference == null)
                {
                    // create new Logchain:
                    Logchain logchainObject = new Logchain(logchain, LogchainsList.Count);
                    LogchainsList.Add(logchainObject);
                    LogchainWindow.AddLogchain(logchainObject);
                    consoleMessage.LogchainReference = logchainObject;
                }
            }

            // add message:
            MessagesList.Add(consoleMessage);
            MessagesContent.TryAddMessage(consoleMessage);
            if (consoleMessage.LogchainReference != null)
                consoleMessage.LogchainReference.RegisterNewMessage(consoleMessage);
        }

        /// <summary>
        /// If you want to inspect message. You can call this method. Message will be opened on full console's screen.
        /// </summary>
        /// <param name="consoleMessage"></param>
        public void OpenMessage(ConsoleMessage consoleMessage)
        {
            MessageInspector.Open(consoleMessage);
        }

        /// <summary>
        /// Open/Close console. If opened it will close.
        /// </summary>
        public void ToggleConsole()
        {
            if (!ConsoleSetting.Instance.Enabled)
                return;

            SetOpened(!Opened);
        }

        /// <summary>
        /// Open or close console
        /// </summary>
        /// <param name="opened"></param>
        public void SetOpened(bool opened)
        {
            bool changed = opened != Opened;
            Opened = opened;
            GlobalWindow.SetActive(opened);

            if (opened && ConsoleConfigurator.AnimationToggle.Selected)
            {
                Animation.Play();
            }

            if (changed)
            {
                if (opened)
                {
                    InputPanel.OnConsoleOpened();
                    CommandsList.OnConsoleOpened();
                }

                ToggleEvent?.Invoke(Opened);
            }
        }

        #endregion

        private void OnDebugLog(string condition, string stacktrace, LogType type)
        {
            if (!ConsoleSetting.Instance.CopyDebugLogs)
                return;

            StartCoroutine(OnDebugLogAsync(condition, stacktrace, type));
        }

        private IEnumerator OnDebugLogAsync(string condition, string stacktrace, LogType type)
        {
            yield return null;

            MessageType messageType;

            if (type == LogType.Log)
                messageType = MessageType.Default;
            else if (type == LogType.Warning)
                messageType = MessageType.Warning;
            else
                messageType = MessageType.Error;

            PrintMessage(condition, messageType, stacktrace);
        }
    }
}