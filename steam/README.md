# Steam delivery (SteamPipe)

1. Install `steamcmd` (Arch: `yay -S steamcmd`).
2. `cp steam/steam.env.example steam/steam.env` and fill in the depot IDs and the Steam account that uploads (Steamworks dashboard → SteamPipe → Depots). `steam/steam.env` is gitignored.
3. Once: `tools/steam-upload.sh --login` and enter the password and Steam Guard code. steamcmd caches the session, so later uploads never ask again (rerun it if the cache expires).
4. Close the Unity editor, then `tools/steam-upload.sh <branch> ["description"]` (the Playtest branch name from the dashboard; `default` cannot be set live by script). It builds Linux and Windows headless (logs in `steam/output/build-*.log`; needs both Unity build modules), then uploads both depots. Pass `--no-build` to upload existing `Builds/Steam/linux` and `windows` (e.g. built from the editor's **WAR-2D → Build** menu). There is no `steam_appid.txt` in the depot.
5. In the Steamworks dashboard check that the build appears, the branch points at it, and the Playtest's access list has your friends. If Steam rejects the Linux executable name, set the launch option to `WAR-2D.x86_64` (Installation → Launch Options, OS Linux).

App ID: 1975300.
