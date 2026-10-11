# Steam delivery (SteamPipe)

1. Install `steamcmd` (Arch: `yay -S steamcmd`).
2. `cp steam/steam.env.example steam/steam.env` and fill in the depot IDs and the Steam account that uploads (Steamworks dashboard → SteamPipe → Depots). `steam/steam.env` is gitignored.
3. Build both targets from the editor: **WAR-2D → Build → Linux (Steam)** and **Windows (Steam)** (needs Unity's Windows build module). Output goes to `Builds/Steam/linux/` and `Builds/Steam/windows/`; there is no `steam_appid.txt` in the depot.
4. `tools/steam-upload.sh <branch> ["description"]` (the Playtest branch name from the dashboard; `default` cannot be set live by script). `steamcmd` asks for the password and Steam Guard code on first use.
5. In the Steamworks dashboard check that the build appears, the branch points at it, and the Playtest's access list has your friends. If Steam rejects the Linux executable name, set the launch option to `WAR-2D.x86_64` (Installation → Launch Options, OS Linux).

App ID: 1975300.
