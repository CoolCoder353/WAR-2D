# System Architecture Diagrams

## Game State Flow Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                      GAME STATES                            │
└─────────────────────────────────────────────────────────────┘

                    ┌──────────┐
                    │  Lobby   │
                    │ (Waiting)│
                    └────┬─────┘
                         │
                    Start Game Command
                    (Server Owner)
                         │
                         ▼
                   ┌──────────────┐
                   │ PlacingHQ    │◄─────┐
                   │ (HQ Placement)│     │
                   └──────┬───────┘     │
                          │             │
                    All players         │
                    placed HQ           │
                          │             │
                          ▼             │
                   ┌──────────────┐     │
                   │ Countdown    │     │
                   │ (3 seconds)  │     │
                   └──────┬───────┘     │
                          │             │
                   Countdown done       │
                          │             │
                          ▼             │
                   ┌──────────────┐     │
                   │   Playing    │     │
                   │ (Active Game)│     │
                   └──────┬───────┘     │
                          │             │
                   Win/Loss/Draw        │
                   Detected             │
                          │             │
                          ▼             │
                   ┌──────────────┐     │
                   │  GameOver    │     │
                   │ (Show Screens)│────┘
                   └──────────────┘     (If reconnect)
```

---

## HQ Placement Sequence Diagram

```
Client                Mirror              Server              GameCore
  │                     │                   │                    │
  │  HQ Button Click    │                   │                    │
  ├────────────────────►│                   │                    │
  │                     │  CmdPlaceHQ()     │                    │
  │                     ├──────────────────►│                    │
  │                     │                   │                    │
  │                     │  Validate State   │                    │
  │                     │  (PlacingHQ?)     │                    │
  │                     │  Validate Position│                    │
  │                     │  Validate Resources
  │                     │                   │                    │
  │                     │  TryAddBuilding() │                    │
  │                     ├──────────────────►│                    │
  │                     │                   │                    │
  │                     │  Create Entity    │                    │
  │                     │  HQComponent{ownerId}                  │
  │                     │                   │                    │
  │                     │  PlayerPlacedHQ() │                    │
  │                     │                   ├───────────────────►│
  │                     │                   │  Validate:         │
  │                     │                   │  No duplicate      │
  │                     │                   │  Mark placed       │
  │                     │                   │                    │
  │  RpcPlayerPlacedHQ()│                   │  Broadcast RPC     │
  │◄────────────────────┼───────────────────┤                    │
  │                     │                   │  Calculate         │
  │                     │                   │  remaining         │
  │                     │                   │                    │
  │ RpcUpdateProgress() │                   │  Broadcast RPC     │
  │◄────────────────────┼───────────────────┤                    │
  │                     │                   │  Check all placed? │
  │                     │                   │  ├─Yes: StartCountdown
  │                     │                   │ └─No: continue
  │                     │                   │                    │
  ▼ onHQPlaced event    ▼                   ▼                    ▼
```

---

## Win/Loss Detection System

```
┌──────────────────────────────────────────────────────┐
│         WinLossSystem (Runs Every 1 Second)         │
└──────────────────────────────────────────────────────┘
         │
         ▼
    For Each ServerPlayer
         │
         ├─► Query HQComponent where ownerId == playerId
         │
         ├─► HQ Found?
         │   ├─ YES: Player alive
         │   │       ├─ Count += 1
         │   │       └─ Store player ID
         │   │
         │   └─ NO: Player has no HQ
         │       ├─ State != Eliminated?
         │       │   └─ EliminatePlayer()
         │       │       └─ RpcOnPlayerLost()
         │       │           └─ Show LossScreenUI
         │       │
         │       └─ Continue to next player
         │
         ▼
    Check Win/Loss Condition
         │
         ├─ playersWithHQ == 1 AND totalPlayers > 1?
         │   ├─ YES: DeclareWinner()
         │   │       └─ RpcOnPlayerWon()
         │   │           └─ Show WinScreenUI
         │   │
         │   └─ NO: Continue
         │
         ├─ playersWithHQ == 0 AND totalPlayers > 0?
         │   ├─ YES: EndGameDraw()
         │   │       └─ RpcGameDraw()
         │   │           └─ Show Draw Message
         │   │
         │   └─ NO: Continue
         │
         └─ playersWithHQ > 1?
             ├─ YES: Game continues
             └─ NO: Already handled above
```

---

## Network Communication Architecture

```
┌────────────────────────────────────────────────────────────┐
│                  CLIENT-SERVER ARCHITECTURE                │
└────────────────────────────────────────────────────────────┘

CLIENT SIDE                    MIRROR NETWORK              SERVER SIDE
──────────────                 ──────────────              ──────────

┌─────────────────┐                                    ┌──────────────┐
│  BuildingButton │                                    │ WorldState   │
│    Manager      │                                    │  Manager     │
└────────┬────────┘                                    └──────┬───────┘
         │                                                    │
         │ User clicks HQ                                    │
         │                                                    │
         ▼                                                    │
┌─────────────────┐                                    │     │
│ ClientPlayer    │                                    │     │
│ CmdPlaceHQ()    │──────────────────────────────────►│     │
│                 │     (Mirror Command)              │     │
└─────────────────┘                                    │     │
         ▲                                              │     ▼
         │                                              │  ┌──────────┐
         │                                              │  │Validation│
         │                                              │  │Placement │
         │                                              │  └─────┬────┘
         │                                              │        │
         │                                              │        ▼
         │                                              │  ┌──────────┐
         │                                              │  │ Create   │
         │                                              │  │ Building │
         │                                              │  └─────┬────┘
         │                                              │        │
         │                                              │        ▼
         │                                              │  ┌──────────┐
         │                                              │  │ GameCore │
         │                                              │  │PlayerPlaced
         │                                              │  │   HQ()   │
         │                                              │  └─────┬────┘
         │                                              │        │
         │  ◄─────────────────────────────────────────┼─────────┘
         │     (Mirror ClientRpc)
         │    RpcPlayerPlacedHQ()
         │    RpcUpdateProgress()
         │
         ▼
    ┌──────────────┐
    │ HQPlacement  │
    │    UI        │
    │  Updates &   │
    │ Shows Confirm│
    └──────────────┘


DURING GAMEPLAY - WIN/LOSS:

SERVER SIDE                        MIRROR NETWORK            CLIENT SIDE
──────────────                     ──────────────            ──────────

┌──────────────┐                                       ┌────────────┐
│ WinLossSystem│                                       │ GameCore   │
│ (Every 1s)   │                                       │ Events     │
└──────┬───────┘                                       └─────▲──────┘
       │                                                     │
       ├─ Query HQComponents                                │
       ├─ Determine eliminations                            │
       └─ Check win/loss condition                          │
              │                                             │
              ├─ DeclareWinner()                            │
              │   └─ RpcOnPlayerWon()────────────────────────┘
              │       │
              │       └────► Show WinScreenUI
              │
              ├─ EliminatePlayer()
              │   └─ RpcOnPlayerLost()────────────────────────┘
              │       │
              │       └────► Show LossScreenUI
              │
              └─ EndGameDraw()
                  └─ RpcGameDraw()────────────────────────┘
                      │
                      └────► Show Draw Message
```

---

## HQ Tracking Data Structure

```
┌─────────────────────────────────────────┐
│         GameCore (Singleton)            │
├─────────────────────────────────────────┤
│                                         │
│  ServerPlayers: Dictionary<NetworkId>  │
│  │                                      │
│  ├─ Player1 (netId=1)                  │
│  │   ├─ state: PlayerState.Playing     │
│  │   └─ resources: 100.0               │
│  │                                      │
│  ├─ Player2 (netId=2)                  │
│  │   ├─ state: PlayerState.Playing     │
│  │   └─ resources: 100.0               │
│  │                                      │
│  └─ Player3 (netId=3)                  │
│      ├─ state: PlayerState.Playing     │
│      └─ resources: 100.0               │
│                                         │
│  hqPlacedByPlayer: Dictionary<uint>    │
│  │                                      │
│  ├─ netId=1: true  (placed HQ)         │
│  ├─ netId=2: false (not placed)        │
│  └─ netId=3: true  (placed HQ)         │
│                                         │
│  remaining = 1 player still placing    │
│                                         │
└─────────────────────────────────────────┘
```

---

## ECS Entity Structure for HQ

```
┌──────────────────────────────────────┐
│         HQ Building Entity           │
├──────────────────────────────────────┤
│                                      │
│ BuildingData                         │
│ ├─ id: 12345                         │
│ ├─ ownerId: 2 (Player 2)             │
│ ├─ position: (10.5, 8.5)             │
│ ├─ buildingType: Base                │
│ └─ rotation: 0.0                     │
│                                      │
│ LocalTransform                       │
│ ├─ Position: (10.5, 8.5, 0)          │
│ ├─ Rotation: (0, 0, 0)               │
│ └─ Scale: 1.0                        │
│                                      │
│ HealthComponent                      │
│ ├─ currentHealth: 100                │
│ └─ maxHealth: 100                    │
│                                      │
│ BuildingResourceComponent            │
│ ├─ upfrontCost: 0 (HQ free)          │
│ ├─ runningCostPerSecond: 0           │
│ └─ timeSinceLastCost: 0              │
│                                      │
│ HQComponent ◄─────── KEY             │
│ └─ ownerId: 2 (MATCHES Player)       │
│                                      │
└──────────────────────────────────────┘
       │
       └─ Used in WinLossSystem to determine
          which player owns the HQ
```

---

## State Transition Details

```
PlacingHQ STATE LOOP:
═════════════════════════

Condition Check (Every update):
├─ CurrentState == PlacingHQ?
│  └─ YES: Show HQPlacementUI
│  
│  For each ServerPlayer:
│  ├─ PlayerPlaced = hqPlacedByPlayer[netId]
│  └─ If not placed: remaining++
│
│  RpcUpdateHQPlacementProgress(remaining)
│
│  If remaining == 0:
│  └─ CurrentState = Countdown
│     └─ countdownTimer = 3.0f
│
└─ NO: Hide HQPlacementUI


Countdown STATE LOOP:
════════════════════════

Condition Check (Every update):
├─ CurrentState == Countdown?
│  ├─ countdownTimer -= deltaTime
│  │
│  └─ countdownTimer <= 0?
│     ├─ CurrentState = Playing
│     └─ countdownTimer = 0
│
└─ NO: Countdown finished


Playing STATE LOOP:
═══════════════════════

WinLossSystem runs (Every 1 second):
├─ For each player:
│  ├─ Has HQ?
│  │  ├─ YES: playerCount++
│  │  └─ NO: Eliminate
│  │
│  └─ playersWithHQ == 1 AND totalPlayers > 1?
│     ├─ YES: DeclareWinner()
│     │       └─ CurrentState = GameOver
│     │
│     └─ playersWithHQ == 0?
│        ├─ YES: EndGameDraw()
│        │       └─ CurrentState = GameOver
│        │
│        └─ NO: Continue game


GameOver STATE LOOP:
═════════════════════

├─ Display appropriate UI
│  ├─ WinScreenUI (if won)
│  ├─ LossScreenUI (if lost)
│  └─ Draw Message (if draw)
│
└─ Wait for player action
   (Return to lobby, disconnect, etc.)
```

---

## UI State Visibility

```
┌─────────────────────────────────────────────────────────┐
│         UI Element Visibility By State                  │
├─────────────────────────────────────────────────────────┤
│                                                         │
│ Lobby         PlacingHQ      Countdown   Playing  Over  │
│ ─────────────────────────────────────────────────────── │
│                                                         │
│ Lobby UI        HQPlacement  Countdown   Game UI   Win  │
│ Active          Prompt       UI          Active    UI   │
│                 Shown        Shown       Shown     ──── │
│                                                   Loss  │
│                 Progress     (Optional)                 UI
│                 Text                                    │
│                 "X left"                           or   │
│                                                   Draw  │
│                                                   Msg   │
│                                                         │
│ Building ───── HIDDEN ─────► Building ─────────► HIDDEN
│ UI                           UI Active                  │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

---

## Performance Profile

```
┌──────────────────────────────────────────┐
│     System Performance Breakdown         │
├──────────────────────────────────────────┤
│                                          │
│ HQ Placement:                            │
│ ├─ Per Placement: O(n) tilemap check     │
│ ├─ Network: 2 RPC calls                  │
│ └─ Impact: Minimal (once per player)     │
│                                          │
│ Win/Loss Detection:                      │
│ ├─ Frequency: 1× per second              │
│ ├─ Per Check: O(p*h) where p=players     │
│ │            h=HQ entities               │
│ ├─ Query: 1 ECS query iteration          │
│ └─ Impact: Negligible (<1ms)             │
│                                          │
│ HQ Tracking Dictionary:                  │
│ ├─ Lookup: O(1)                          │
│ ├─ Size: 1 bool per player               │
│ └─ Impact: Minimal memory                │
│                                          │
│ Total Impact: ◄────────────────────────  │
│ ├─ Memory: +4-100 bytes per game         │
│ ├─ CPU: <1ms per second                  │
│ ├─ Network: 3-5 messages per game        │
│ └─ Result: Negligible performance hit    │
│                                          │
└──────────────────────────────────────────┘
```

