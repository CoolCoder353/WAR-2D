namespace WAR2D.UI
{
    /// <summary>
    /// The hover cards on the lobby's match settings (playtest 2026-11-02, Figma K1): what each setting does and what its
    /// values mean, in gameplay.md's words. Pure.
    /// </summary>
    public static class LobbySettingHelp
    {
        public static HoverCardContent MapSize() => new HoverCardContent("Map size")
            .Line("Tiles per side. Bigger maps have more start sites, expansions and central areas, and longer marches.")
            .Pair("256", "Quick, close fights").Pair("512", "The standard match").Pair("1024", "Long games, up to 8 players");

        public static HoverCardContent Seed() => new HoverCardContent("Seed")
            .Line("The number the map is generated from: the same seed and size always make the same map. Reroll picks a new one.");

        public static HoverCardContent Resources() => new HoverCardContent("Starting resources")
            .Line("What every player starts with. More means a faster opening and earlier armies.");

        public static HoverCardContent StartOptions() => new HoverCardContent("Options per player")
            .Line("How many start sites each player is dealt to choose their HQ from. Only you and your teammates see yours.")
            .Line("Values the map can't fit for this many players are greyed out.");

        public static HoverCardContent PickTime() => new HoverCardContent("HQ pick time")
            .Line("How long players have to place their HQ in one of their start options.")
            .Pair("Random", "HQs go down at once, at a random option")
            .Pair("15–90 s", "Unplaced HQs go to a random option when time runs out")
            .Pair("Unlimited", "The match waits for everyone");

        public static HoverCardContent Visibility() => new HoverCardContent("Visibility")
            .Line("Who can find and join this server.")
            .Pair("Public", "Anyone can find it").Pair("Friends", "Your Steam friends see it").Pair("Private", "Nobody can join: play against AIs");

        public static HoverCardContent Mode() => new HoverCardContent("Mode")
            .Line("Free-for-all: everyone for themselves. Teams: players on a team share vision and don't attack each other.");

        public static HoverCardContent Diplomacy() => new HoverCardContent("Diplomacy")
            .Line("On: during the match each player chooses whom they attack and with whom they share vision. Off: the starting teams hold.");
    }
}
