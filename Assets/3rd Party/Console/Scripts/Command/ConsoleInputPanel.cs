using System;

using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace TIM
{

    public class ConsoleInputPanel : MonoBehaviour
    {
        public TMP_InputField InputField;
        public TMP_Text HelpText;
        public ConsoleTipMaster TipMaster;


        public void OnEnterPressed()
        {
            if (!InputField.IsActive())
            {
                InputField.ActivateInputField();
                return;
            }

            // Help with AutoComplete:
            var cmdInputResult = TipMaster.GetHighlightedResult;
            if (cmdInputResult != null)
            {
                if (ApplyHelp(cmdInputResult))
                    TipMaster.PerfectlySuitedResult = cmdInputResult;
                else
                    return;
            }


            // Get selected command:
            CmdInputResult perfectlySuitedResult = null;
            if (!string.IsNullOrWhiteSpace(InputField.text))
            {
                if (TipMaster.PerfectlySuitedResult != null)
                {
                    perfectlySuitedResult = TipMaster.PerfectlySuitedResult;
                }
                else
                {
                    Console.Log("Incorrect command: " + InputField.text, MessageType.Error);
                    ConsoleAudio.PlayCommandFailSound();
                }
            }

            InputField.text = "";
            InputField.ActivateInputField();

            // Execute command:
            if (perfectlySuitedResult != null)
            {
                try
                {
                    perfectlySuitedResult.Execute();
                    ConsoleAudio.PlayCommandExecuteSound();
                }
                catch (Exception e)
                {
                    ConsoleAudio.PlayCommandFailSound();
                    throw;
                }
            }
        }

        public void OnConsoleOpened()
        {
            InputField.text = "";
            InputField.ActivateInputField();
        }

        public void OnInputFieldChanged()
        {
            TipMaster.RefreshTips(InputField.text);
            RefreshHelpText();
        }

        public void RefreshHelpText()
        {
            HelpText.text = TipMaster.GetHighlightedResult?.HelpString ?? "";
        }

        private void Update()
        {
            if (InputField.IsActive())
            {
                if ((UnityEngine.InputSystem.Keyboard.current?.upArrowKey.wasPressedThisFrame ?? false))
                {
                    InputField.caretPosition = InputField.text.Length;
                    TipMaster.HighlightNextElement();
                }
                else if ((UnityEngine.InputSystem.Keyboard.current?.downArrowKey.wasPressedThisFrame ?? false))
                {
                    InputField.caretPosition = InputField.text.Length;

                    TipMaster.HighlightPreviousElement();
                }
            }
        }

        private bool ApplyHelp(CmdInputResult cmdInputResult)
        {
            bool parsed = cmdInputResult.TryInit(cmdInputResult.InputString, cmdInputResult.Formula, true);
            InputField.text = cmdInputResult.AutoCompletedString;
            if (!parsed)
                InputField.text += cmdInputResult.Formula.GetSpaceString();
            InputField.ActivateInputField();
            InputField.caretPosition = InputField.text.Length;
            InputField.MoveToEndOfLine(false, true);
            TipMaster.HighlightElement(0);
            return parsed;
        }
    }
}
