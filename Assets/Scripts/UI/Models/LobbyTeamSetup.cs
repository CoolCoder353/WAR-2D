using System;
using System.Collections.Generic;

namespace WAR2D.UI
{
    /// <summary>The lobby's quick setup buttons (playtest 2026-11-05, Figma L1).</summary>
    public enum QuickSetup
    {
        /// <summary>The host on Team 1, everyone else on Team 2.</summary>
        MeVsTheAis,
        /// <summary>Every human on Team 1, every AI on Team 2.</summary>
        HumansVsAis,
        /// <summary>As many teams as humans (2 to 4): humans dealt one per team, then AIs to the smallest team.</summary>
        SplitEvenly,
        /// <summary>The same team sizes, players dealt at random.</summary>
        Shuffle,
        /// <summary>Everyone Solo.</summary>
        EveryoneSolo,
    }

    /// <summary>
    /// Works out the teams a quick setup gives (Teams mode). Pure: the lobby sends the result as one
    /// <c>GameCore.Cmd_SetTeams</c>, which checks every team again.
    /// </summary>
    public static class LobbyTeamSetup
    {
        /// <summary>The button text for each setup, in button order.</summary>
        public static readonly (QuickSetup setup, string text)[] Buttons =
        {
            (QuickSetup.MeVsTheAis, "Me vs the AIs"),
            (QuickSetup.HumansVsAis, "Humans vs AIs"),
            (QuickSetup.SplitEvenly, "Split evenly"),
            (QuickSetup.Shuffle, "Shuffle"),
            (QuickSetup.EveryoneSolo, "Everyone solo"),
        };

        /// <summary>
        /// The new team of every player in <paramref name="players"/> (lobby order). <paramref name="me"/> is the host's
        /// owner id; <paramref name="random"/> is used only by Shuffle.
        /// </summary>
        public static int[] Teams(QuickSetup setup, IReadOnlyList<LobbyPlayer> players, int me, Random random)
        {
            var teams = new int[players.Count];
            switch (setup)
            {
                case QuickSetup.MeVsTheAis:
                    for (int i = 0; i < players.Count; i++) teams[i] = players[i].OwnerId == me ? 0 : 1;
                    break;
                case QuickSetup.HumansVsAis:
                    for (int i = 0; i < players.Count; i++) teams[i] = players[i].IsAi ? 1 : 0;
                    break;
                case QuickSetup.SplitEvenly:
                    SplitEvenly(players, teams);
                    break;
                case QuickSetup.Shuffle:
                    for (int i = 0; i < players.Count; i++) teams[i] = players[i].Team;
                    for (int i = teams.Length - 1; i > 0; i--)
                    {
                        int j = random.Next(i + 1);
                        (teams[i], teams[j]) = (teams[j], teams[i]);
                    }
                    break;
                default:
                    for (int i = 0; i < players.Count; i++) teams[i] = TeamRules.NoTeam;
                    break;
            }
            return teams;
        }

        private static void SplitEvenly(IReadOnlyList<LobbyPlayer> players, int[] teams)
        {
            int humans = 0;
            foreach (LobbyPlayer p in players) if (!p.IsAi) humans++;
            int count = Math.Clamp(humans, 2, LobbyRules.PickableTeams);
            var sizes = new int[count];
            int next = 0;
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].IsAi) continue;
                teams[i] = next % count;
                sizes[teams[i]]++;
                next++;
            }
            for (int i = 0; i < players.Count; i++)
            {
                if (!players[i].IsAi) continue;
                int best = 0;
                for (int t = 1; t < count; t++) if (sizes[t] < sizes[best]) best = t;
                teams[i] = best;
                sizes[best]++;
            }
        }
    }
}
