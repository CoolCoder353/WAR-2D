using System;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the Play screen (<c>Assets/UI/Menus/HostJoin.uxml</c>): the nickname, Host, and Join with its
    /// states (invalid address, connecting with Cancel, couldn't connect).
    /// </summary>
    public sealed class HostJoinController
    {
        private readonly TextField nickname, address;
        private readonly VisualElement addressInput, joinButtons, connecting;
        private readonly Label connectingText, failed;

        public event Action HostClicked, BackClicked, CancelClicked;

        /// <summary>Raised with a valid address.</summary>
        public event Action<string> JoinClicked;

        public HostJoinController(VisualElement root)
        {
            nickname = root.Q<TextField>("nickname-field");
            address = root.Q<TextField>("address-field");
            addressInput = root.Q("address-input");
            joinButtons = root.Q("join-buttons");
            connecting = root.Q("connecting");
            connectingText = root.Q<Label>("connecting-text");
            failed = root.Q<Label>("join-failed");

            nickname.value = MenuModel.Nickname;
            address.value = MenuModel.Address;
            nickname.RegisterValueChangedCallback(e => MenuModel.Nickname = e.newValue);
            address.RegisterValueChangedCallback(_ => addressInput.RemoveFromClassList("input--error"));
            root.Q<Button>("host-button").clicked += () => HostClicked?.Invoke();
            root.Q<Button>("back-button").clicked += () => BackClicked?.Invoke();
            root.Q<Button>("cancel-button").clicked += () => CancelClicked?.Invoke();
            root.Q<Button>("join-button").clicked += Join;
        }

        private void Join()
        {
            string value = address.value?.Trim();
            if (!MenuModel.IsAddressValid(value))
            {
                addressInput.AddToClassList("input--error");
                return;
            }
            MenuModel.Address = value;
            JoinClicked?.Invoke(value);
        }

        /// <summary>Shows the join form (no state).</summary>
        public void ShowIdle()
        {
            joinButtons.RemoveFromClassList("hidden");
            connecting.RemoveFromClassList("play-panel__state--visible");
            failed.RemoveFromClassList("play-panel__state--visible");
        }

        /// <summary>Shows "Connecting to …" with Cancel.</summary>
        public void ShowConnecting(string to)
        {
            joinButtons.AddToClassList("hidden");
            connectingText.text = $"Connecting to {to}…";
            connecting.AddToClassList("play-panel__state--visible");
            failed.RemoveFromClassList("play-panel__state--visible");
        }

        /// <summary>Shows why the last join failed, under the join form.</summary>
        public void ShowFailed(string to, int port)
        {
            ShowIdle();
            failed.text = $"No server answered at {to}:{port}. Check the address and that the host is running.";
            failed.AddToClassList("play-panel__state--visible");
        }
    }
}
