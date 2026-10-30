using System.Collections.Generic;
using System.Linq;
using Mirror;
using Unity.Mathematics;

namespace WAR2D.AiView
{
    /// <summary>
    /// A team view's match facts from <see cref="GameCore"/>: what an owner's client learns outside replication (its
    /// resources from <c>TargetUpdateResources</c>, its start options from <c>TargetStartOptions</c> and
    /// <c>TargetTeammatePick</c>, and the public lobby state). Server only.
    /// </summary>
    public sealed class GameCoreFacts : ITeamViewFacts
    {
        private static readonly int[] NoOrder = new int[0];

        public GameState State => GameCore.Instance != null ? GameCore.Instance.CurrentState : GameState.Lobby;

        public IReadOnlyList<int> PlayerOrder => GameCore.Instance != null ? GameCore.Instance.PlayerOrder : (IReadOnlyList<int>)NoOrder;

        public void Fill(int owner, TeamView view)
        {
            GameCore core = GameCore.Instance;
            if (core == null) return;
            view.State = core.CurrentState;
            view.Mode = core.Settings.Mode;
            view.DiplomacyOn = core.Settings.Diplomacy;
            view.Team = core.TeamOf(owner);

            view.Players.Clear();
            foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in core.ServerPlayers)
                if (entry.Key != null) AddPlayer(core, view, BuildingData.UIntToInt(entry.Key.netId), entry.Value);
            foreach (KeyValuePair<int, AiSeat> seat in core.AiPlayers) AddPlayer(core, view, seat.Key, seat.Value.Player);

            ServerPlayer own = core.GetServerPlayerById(owner);
            if (own != null)
            {
                view.Resources = own.Resources;
                view.Income = own.IncomeLastSecond;
                view.Upkeep = own.UpkeepLastSecond;
            }
            // Start options only change while HQs are placed; the last ones stay in the view after.
            if (core.CurrentState == GameState.PlacingHQ && own != null) FillStarts(core, owner, own, view);
        }

        private static void AddPlayer(GameCore core, TeamView view, int owner, ServerPlayer player)
        {
            ClientPlayer client = core.ClientPlayerOf(owner);
            view.Players.Add(new AiPlayer
            {
                OwnerId = owner,
                Name = client != null ? client.nickname : null,
                Colour = client != null ? client.colourIndex : 0,
                IsAi = AiOwnerIds.IsAi(owner),
                Team = core.TeamOf(owner),
                Eliminated = player.state != PlayerState.Playing,
            });
        }

        /// <summary>Its own options and pick and its teammates', by the same rule the server sends a human (<see cref="StartPickRules.TeamView"/>).</summary>
        private static void FillStarts(GameCore core, int owner, ServerPlayer own, TeamView view)
        {
            view.StartOptions.Clear();
            int2[] sites = view.HqSites;
            Add(view, owner, own.StartOptions, own.StartPick, sites);
            var starts = core.AllPlayers().Select(p => (p.owner, p.player.StartOptions, p.player.StartPick));
            foreach ((int other, int[] options, int pick) in StartPickRules.TeamView(owner, starts, core.AreTeammates))
                Add(view, other, options, pick, sites);
        }

        private static void Add(TeamView view, int owner, int[] options, int pick, int2[] sites)
        {
            if (options == null) return;
            foreach (int site in options)
                view.StartOptions.Add(new AiStartOption
                {
                    OwnerId = owner, Site = site, Picked = site == pick,
                    Centre = site >= 0 && site < sites.Length ? sites[site] : default,
                });
        }
    }
}
