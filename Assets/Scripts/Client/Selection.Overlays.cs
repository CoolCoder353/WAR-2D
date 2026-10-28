using System.Collections.Generic;

namespace WAR2D.Client
{
    public sealed partial class Selection
    {
        /// <summary>
        /// The selected unit ids without boxing an enumerator (the v0.15 world overlays walk the selection every frame;
        /// <c>foreach</c> over <see cref="Selected"/> as an interface would allocate).
        /// </summary>
        public HashSet<int>.Enumerator SelectedEnumerator() => selected.GetEnumerator();

        /// <summary>
        /// The selected unit with the longest list of queued goals in <paramref name="queue"/> (ties: the lowest id), or 0
        /// with nothing selected: whose goals the Shift-held numbered rings show (Figma v0.15 item 10: "the longest queue wins").
        /// </summary>
        public int LongestQueue(OrderQueueTracker queue)
        {
            int best = 0, bestCount = -1;
            foreach (int id in selected)
            {
                int count = queue != null ? queue.GoalsOf(id).Count : 0;
                if (count > bestCount || (count == bestCount && id < best))
                {
                    best = id;
                    bestCount = count;
                }
            }
            return best;
        }
    }
}
