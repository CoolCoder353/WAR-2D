using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the gift dialog (<c>Assets/UI/Hud/GiftDialog.uxml</c>): pick any other live player (allies and
    /// enemies, by the local player's own attack mask), enter an amount up to what you have, and send.
    /// </summary>
    public sealed class GiftDialogController : IDisposable
    {
        private readonly HudModel model;
        private readonly VisualElement dialog, targets, amountInput;
        private readonly Label amountLabel, amountError, note;
        private readonly TextField amount;
        private int selected;
        private readonly Button send;
        private readonly Label targetError;

        /// <summary>Raised with a valid (target owner, amount).</summary>
        public event Action<int, float> GiftRequested;

        public GiftDialogController(VisualElement root, HudModel model, float cooldownSeconds)
        {
            this.model = model;
            dialog = root.Q("gift-dialog");
            targets = root.Q("gift-targets");
            amountInput = root.Q("gift-amount-input");
            amountLabel = root.Q<Label>("gift-amount-label");
            amountError = root.Q<Label>("gift-amount-error");
            note = root.Q<Label>("gift-note");
            amount = root.Q<TextField>("gift-amount");
            note.text = $"One gift every {cooldownSeconds:0.#} s. Only you and the recipient are told.";
            amount.RegisterValueChangedCallback(_ => { amountInput.RemoveFromClassList("input--error"); ShowSend(); });
            root.Q<Button>("gift-cancel").clicked += Close;
            send = root.Q<Button>("gift-send");
            send.clicked += Send;
            // Playtest 2026-11-10: "Pick who to gift to" outlined the Amount field; the recipient list has its own message.
            targetError = new Label { name = "gift-target-error" };
            targetError.AddToClassList("text--danger");
            targetError.AddToClassList("hidden");
            targets.parent.Insert(targets.parent.IndexOf(targets) + 1, targetError);
            // Playtest 2026-11-10: quick amounts, so a gift isn't typed digit by digit.
            var quick = new VisualElement { name = "gift-quick" };
            quick.AddToClassList("row");
            quick.AddToClassList("gift-dialog__quick");
            foreach ((string label, Func<float> value) in new (string, Func<float>)[]
                     { ("100", () => 100f), ("500", () => 500f), ("1,000", () => 1000f), ("Half", () => Mathf.Floor(model.Resources * 0.5f)), ("All", () => Mathf.Floor(model.Resources)) })
            {
                var b = new Button(() => amount.value = Mathf.Max(0f, Mathf.Min(value(), Mathf.Floor(model.Resources))).ToString("0", CultureInfo.InvariantCulture))
                    { name = "gift-quick-" + label.Replace(",", "").ToLowerInvariant(), text = label };
                b.AddToClassList("button");
                quick.Add(b);
            }
            amountInput.parent.Insert(amountInput.parent.IndexOf(amountInput) + 1, quick);
            model.PlayersChanged += Show;
            model.DiplomacyChanged += Show;
            model.ResourcesChanged += ShowBalance;
        }

        public bool IsOpen => dialog.ClassListContains("gift-dialog--open");

        public void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        public void Open()
        {
            dialog.AddToClassList("gift-dialog--open");
            amountInput.RemoveFromClassList("input--error");
            targetError.AddToClassList("hidden");
            Show();
        }

        public void Close() => dialog.RemoveFromClassList("gift-dialog--open");

        private void ShowBalance() => amountLabel.text = $"Amount (you have {model.Resources:N0})";

        private void Show()
        {
            ShowBalance();
            targets.Clear();
            bool selectedLive = false;
            foreach (PlayerRow player in model.Players)
            {
                if (player.OwnerId == model.LocalOwnerId) continue;
                int owner = player.OwnerId;
                bool enemy = (model.AttackMask & (1 << player.OrderIndex)) != 0;
                var row = new Button(() => { selected = owner; targetError.AddToClassList("hidden"); Show(); }) { name = "gift-target-" + owner };
                row.AddToClassList("list-row");
                row.EnableInClassList("list-row--selected", owner == selected && !player.Eliminated);
                var swatch = new VisualElement();
                swatch.AddToClassList("list-row__swatch");
                swatch.AddToClassList(player.Eliminated ? "swatch--eliminated" : "player-" + (player.ColourIndex % 8 + 1));
                VisualElement label = PlayerNameLabel.Build(player.Nickname, player.AiTag, null); // AIs are gift targets like anyone (decision 13)
                label.AddToClassList("list-row__label");
                var trailing = new Label(player.Eliminated ? "Eliminated" : enemy ? "Enemy" : "Ally");
                trailing.AddToClassList("list-row__trailing");
                row.Add(swatch);
                row.Add(label);
                row.Add(trailing);
                row.SetEnabled(!player.Eliminated);
                targets.Add(row);
                selectedLive |= owner == selected && !player.Eliminated;
            }
            if (!selectedLive) selected = 0;
            ShowSend();
        }

        /// <summary>Send is live once a recipient is picked and an amount typed (playtest 2026-11-10: it stayed live with nothing filled in).</summary>
        private void ShowSend() => send?.SetEnabled(selected != 0 && !string.IsNullOrWhiteSpace(amount.value));

        private void Send()
        {
            if (selected == 0)
            {
                targetError.text = "Pick who to gift to";
                targetError.RemoveFromClassList("hidden");
                return;
            }
            if (!float.TryParse(amount.value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !GiftRules.IsValid(value, model.Resources))
            {
                Error(value > model.Resources ? $"You only have {model.Resources:N0}" : "Enter an amount of at least 1");
                return;
            }
            GiftRequested?.Invoke(selected, value);
            amount.SetValueWithoutNotify("");
            Close();
        }

        private void Error(string message)
        {
            amountError.text = message;
            amountInput.AddToClassList("input--error");
        }

        public void Dispose()
        {
            model.PlayersChanged -= Show;
            model.DiplomacyChanged -= Show;
            model.ResourcesChanged -= ShowBalance;
        }
    }
}
