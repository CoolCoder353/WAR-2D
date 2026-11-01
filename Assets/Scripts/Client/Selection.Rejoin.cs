using System.Collections.Generic;

namespace WAR2D.Client
{
    /// <summary>
    /// Squads and building groups kept across a rejoin. Both live only on the client (the server keeps the squads' units, not
    /// which keys the client gave them, and knows nothing of building groups), and the scene that holds the selection is
    /// unloaded when the connection drops. Ids are the server's, so the copy is still right when the player comes back.
    /// </summary>
    public sealed partial class Selection
    {
        private static string rejoinToken;
        private static List<int>[] rejoinSquads, rejoinGroups;
        private static bool rejoinArmed;

        /// <summary>Keeps this selection's squads and building groups under the rejoin token the client holds as the match closes.</summary>
        public void RememberForRejoin(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            rejoinToken = token;
            rejoinSquads = Copy(squads);
            rejoinGroups = Copy(buildingGroups);
            rejoinArmed = false;
        }

        /// <summary>The client is rejoining with <paramref name="token"/>: the next selection takes back what was kept under it.</summary>
        public static void ArmRejoin(string token) => rejoinArmed = !string.IsNullOrEmpty(token) && token == rejoinToken;

        /// <summary>Takes back the squads and building groups kept for this rejoin, once. False when there is nothing to restore.</summary>
        public bool RestoreAfterRejoin()
        {
            if (!rejoinArmed) return false;
            rejoinArmed = false;
            for (int i = 0; i < squads.Length; i++)
            {
                squads[i] = rejoinSquads[i];
                buildingGroups[i] = rejoinGroups[i];
            }
            rejoinToken = null;
            rejoinSquads = rejoinGroups = null;
            return true;
        }

        private static List<int>[] Copy(List<int>[] slots)
        {
            var copy = new List<int>[slots.Length];
            for (int i = 0; i < slots.Length; i++) copy[i] = slots[i] != null ? new List<int>(slots[i]) : null;
            return copy;
        }
    }
}
