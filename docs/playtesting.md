# Playtesting

## With Steam (the normal way)

Host with **Play → Host**. Friends join from the Play screen's **Friends' games** list (press Refresh), or from an **Invite friends** invite sent from the lobby. Steam's relay carries the traffic, so nobody opens a port or needs a tunnel. Both sides need Steam running and the same game version (a mismatched build is refused at sign-in).

- Builds go to a Steam branch with `tools/steam-upload.sh` ([steam/README.md](../steam/README.md)); the Playtest branch's access list controls who can play.
- Release builds accept the old IP join only with `-direct`; `-noSteam` runs without Steam. In the editor, Direct is always on.
- Logs: Linux `~/.config/unity3d/<company>/<product>/Player.log`, Windows `%USERPROFILE%\AppData\LocalLow\<company>\<product>\Player.log`.
- Rich presence needs the `#Status` token in the Steamworks dashboard.

## Direct play (without Steam)

For testing, LAN, or when Steam isn't available. The rest of this page covers it. If Steam is running, the menu uses the Steam lobby instead and skips the router mapping below.

One player **hosts**. Everyone else **joins** the host's address. Only the host's network needs to accept incoming connections. Joining players need nothing special.

## 1. Host and read the lobby line

Press **Play → Host**. The game asks your router to open UDP port **7778** (UPnP), and the lobby subtitle says how it went:

| The lobby says | What to do |
|---|---|
| `internet: 203.0.113.5:7778` | Send that address to the other players. You're done. |
| `opening port 7778 on your router…` | Wait a few seconds. |
| `forward UDP 7778 to 192.168.x.y on your router (…)` | Your router has no UPnP or refused it. Forward the port by hand (step 2), or use a tunnel (step 3). |
| `port 7778 opened, but the router reports no public IP` or `your ISP shares your address` | Your router has no internet address of its own (carrier-grade NAT, DS-Lite, or a second router in front of it). Opening ports won't help. Use a tunnel (step 3), or let a player whose lobby shows an internet address host instead. |

On Windows, allow WAR-2D through the firewall when Windows asks. On Linux with a firewall (ufw, firewalld), allow UDP 7778, for example `sudo ufw allow 7778/udp`.

## 2. Forward the port by hand

In your router's admin page (often `192.168.0.1` or `192.168.1.1`), add a port forward:

- protocol **UDP**, external port **7778**, internal port **7778**
- to your PC's LAN address, the `LAN:` address in the lobby line

Then find your public IP (search "what is my IP") and send `that-ip:7778` to the other players. If your router's status page shows a WAN address starting with `100.64`–`100.127`, `10.`, `172.16`–`172.31` or `192.168.`, you're behind another NAT and need a tunnel.

## 3. Use a tunnel

These work through any NAT. Joining players then type the tunnel's address in **Join**. A port is allowed: `host:port`, or `[ipv6]:port` for IPv6.

- **[playit.gg](https://playit.gg):** only the host installs it. Create a **UDP** tunnel to local port `7778`. Players join the address it gives, such as `something.playit.gg:12345`.
- **[Tailscale](https://tailscale.com) or [ZeroTier](https://www.zerotier.com):** everyone installs it and joins the same network. Players join the host's Tailscale/ZeroTier IP, such as `100.101.102.103` (no port needed).

## Joining

Press **Play**, type the host's address in **Join** (`1.2.3.4`, `1.2.3.4:7778`, `name.example.com:12345` or `[2001:db8::1]:7778`) and press **Join**. "No server answered" means the host's port isn't reachable: check the host's lobby line.

## Notes

- The port mapping is leased for 2 hours and renewed while you host, so it closes on its own if the game crashes. Leaving the lobby removes it.
- Start the game with `-noUpnp` to stop it from touching the router.
- The lobby is limited to 8 players, and players can't join a match that has started.
