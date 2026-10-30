# AI overhaul Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the v0.14 AI spend its money, judge fights from the damage table, pick soft targets, vary and adapt, and make its difficulties one policy plus handicaps.

**Architecture:** The managers keep reading a resolved `AiDifficultyConfig`, now built by `AiPolicy.Resolve` from a `<Policy>` block and per-difficulty handicaps. New pure units (`FightModel`, `AiDice`, `AiVariation`, `EnemyModel`, `KillLedger`) carry the new reasoning; two new managers (`StrategyManager`, `RaidManager`) use them; `ArmyManager`, `ProductionManager`, `BuilderManager`, `EconomyManager` and `AiState` change in place. Everything still reads only the `TeamView` and acts only through `AiAction`.

**Tech Stack:** Unity 6000.4.1f1, C# (`WAR2D.Ai` assembly), NUnit EditMode/PlayMode tests, Node for `tools/ai-tune.js`.

**Spec:** [docs/superpowers/specs/2026-10-30-ai-overhaul-design.md](../specs/2026-10-30-ai-overhaul-design.md)

## Global Constraints

- The AI reads only its `TeamView` and blackboard and acts only through `AiAction` / `PlayerActions` (`AiLeakTests`, `AiAssemblyTests` must keep passing; the `Ai` assembly references no `SimCommandQueue`).
- Every number lives in `GameConfig.xml` `<Ai>` and is parsed strictly (`ConfigParser.ParseAi`); no new hard-coded balance or tuning values.
- A brain made with variation seed 0 is not varied (tests stay deterministic).
- `perf.part.ai` ≤ 2 ms p95 with 7 Hard AIs.
- Repo rules: no commits unless the owner asks (no co-author lines when they do); `tools/unity-compile.sh` while working, `tools/unity-test.sh EditMode` and `PlayMode` once at the end of the plan (owner, 2026-10-11 and 2026-10-16); `WarLog`, never `Debug.Log`; XML `/// <summary>` on public members.

## File map

| File | Responsibility |
| --- | --- |
| `Assets/Scripts/Config/AiConfig.cs` | `AiHandicapConfig`, `AiPolicyConfig`, resolved `AiDifficultyConfig` (new fields), `AiStyleConfig.BuildOrders`/`Raids`/`Clone()`; drop `AiCounters` |
| `Assets/Scripts/Config/AiPolicy.cs` (new) | `AiPolicy.Resolve(AiConfig, AiDifficulty)` |
| `Assets/Scripts/Config/ConfigParser.cs` | parse handicaps, `<Policy>`, several `<BuildOrder>`s, `<Raids>`; resolve difficulties; refuse old knobs |
| `Assets/Scripts/Ai/AiDice.cs` (new) | seeded stream: `Slip`, `Noise`, attention roll |
| `Assets/Scripts/Ai/AiVariation.cs` (new) | varied style clones and gather angle |
| `Assets/Scripts/Ai/ActionBudget.cs` | economy and army pools with lending |
| `Assets/Scripts/Ai/FightModel.cs` (new) | `Force`, `Add*`, `Advantage`, `Survivors` |
| `Assets/Scripts/Ai/AiForces.cs` (new) | forces from the view and blackboard (own group, enemy near a point, enemy model army) |
| `Assets/Scripts/Ai/EnemyModel.cs` (new) | per-enemy composition, buildings, style guess |
| `Assets/Scripts/Ai/Blackboard.cs` | kill ledger, enemy models, avoided targets, memory scaled |
| `Assets/Scripts/Ai/AiState.cs` | projected income, building upkeep share, bank flag, strategy fields, raiders |
| `Assets/Scripts/Ai/AiBrain.cs`, `IAiManager.cs` | resolve, dice, variation, styles, attention, manager order, status |
| `Assets/Scripts/Ai/Managers/Army.cs` | fight model decisions, scored targets, siege, home guard, focus fire, trades, slips |
| `Assets/Scripts/Ai/Managers/Raid.cs` (new) | raiders |
| `Assets/Scripts/Ai/Managers/Strategy.cs` (new) | scouting reactions, style switches |
| `Assets/Scripts/Ai/Managers/Production.cs`, `Economy.cs`, `Builder.cs`, `Opening.cs` | counters from the model, bank rule, upkeep planner, build-order pick |
| `Assets/Scripts/Net/AiPlayers.cs`, `Assets/Scripts/Ai/AiRegistration.cs` | `AiSeat.VariationSeed` |
| `Assets/Resources/GameConfig.xml` | the new `<Ai>` values |
| `tools/ai-tune.js` | sequential stopping, paired confirm |
| `Assets/Tests/EditMode/Ai*Tests.cs`, `ConfigParserTests.cs`, `AiSandbox.cs` | tests |
| `docs/*.md` | §10 of the spec |

---

### Task 1: Policy, handicaps and the resolved difficulty

**Files:** Modify `Config/AiConfig.cs`, `Config/ConfigParser.cs`, `Resources/GameConfig.xml`; Create `Config/AiPolicy.cs`; Test `Tests/EditMode/ConfigParserTests.cs`, Create `Tests/EditMode/AiPolicyTests.cs`.

**Interfaces — Produces:**
- `class AiHandicapConfig { float ThinkSeconds; int ActionsPerMinute; float ReactionSeconds; float Insight, Attention, Ambition, Mistakes; AiPeace Peace; bool GiftsSurplus; }`
- `class AiPolicyConfig` with every policy number of the spec (§2.2–§6): `Builders, BuildersPerExpansion, MaxExpansions, ExpandAfterSeconds, SaturateAmbition, UpkeepReserveSeconds, Retreats, FocusFire, KeepsBacklineBack, DigsPastWalls, Scouts, RescoutSeconds, DefendsChokepoints, EarlyTurrets, IncomePerProducer, AttackAdvantage, RetreatAdvantage, CounterAttackAdvantage, EstimateNoise, SlipSeconds, SplashTargets, RangeWeight, BomberFightSeconds, BuildingUpkeepShare, BankHigh, BankSeconds, ExtraProducers, TargetDistanceScale, HqValue, MinerIncomeValue, AvoidFactor, AvoidSeconds, HomeGuardShare, HomeGuardSeconds, RaidSize, RaidMinSpeed, RaidAdvantage, RaidSeconds, BadTrade, GoodTrade, ModelHalfLife, RushSeconds, FastExpandSeconds, PunishFactor, StyleSwitchSeconds, TurtleBreakoutSeconds, EconomyShare, Variation, GatherAngle`.
- `AiConfig.Handicaps : Dictionary<AiDifficulty, AiHandicapConfig>`, `AiConfig.Policy : AiPolicyConfig`; `AiConfig.Difficulties` holds the **resolved** configs, filled by `AiPolicy.ResolveAll(config)` at the end of `ParseAi`; `GetDifficulty` unchanged.
- `AiDifficultyConfig` gains `Insight, Attention, Ambition, Mistakes, CounterWeight, BuildingCounterWeight, ArmyMemoryScale, AttackAdvantage, RetreatAdvantage, ExtraProducers`; loses `Counters`, `AttackRatio`, `RetreatRatio`. `AiCounters` is deleted.
- `AiStyleConfig.BuildOrders : AiBuildStep[][]` (parsed, ≥ 1), `BuildOrder` = the chosen one (the first until varied), `Raids : bool`, `AiStyleConfig Clone()` (deep copy of dictionaries and arrays).
- `static AiDifficultyConfig AiPolicy.Resolve(AiPolicyConfig policy, AiHandicapConfig h)` per the spec's §2.2 table.

- [ ] **Step 1: Tests.** `AiPolicyTests`: `HardIsThePolicy` (Hard's handicaps are 1/1/1/0: every resolved number equals the policy's); `EveryResolvedNumberIsMonotone` (Easy ≤ Normal ≤ Hard for `Builders, BuildersPerExpansion, MaxExpansions, Scouts, CounterWeight, BuildingCounterWeight, Attention, Insight, ExtraProducers`; ≥ for `ExpandAfterSeconds, RescoutSeconds, Mistakes, ReactionSeconds, ThinkSeconds`); `LowInsightScoutsOnce`. `ConfigParserTests`: fixture XML moves to the new `<Difficulty>` shape plus a `<Policy>`; `[TestCase]` for a bad `Insight` (1.5) and for a stale `<Builders>` inside `<Difficulty>` (refused with "moved to <Policy>"); a style with two `<BuildOrder>`s parses both.
- [ ] **Step 2:** `tools/unity-compile.sh` → fails (types missing).
- [ ] **Step 3: Implement** the types, the parser (`ParseAi`: handicaps per difficulty with 0..1 checks; `<Policy>` with the same `Num` helpers the `<Play>` block uses; all `<BuildOrder>` nodes; `<Raids>` 0/1; refuse any old knob name inside `<Difficulty>`), `AiPolicy.Resolve`, and the new XML (`<Policy>` from Hard's v0.14 values; handicaps from the spec's §2.1 table).
- [ ] **Step 4:** fix every compile error from removed fields (`Counters` → `CounterWeight`/`BuildingCounterWeight`; `AttackRatio`/`RetreatRatio` → advantages) in managers and tests; `tools/unity-compile.sh` → passes.

### Task 2: Dice, variation and the variation seed

**Files:** Create `Ai/AiDice.cs`, `Ai/AiVariation.cs`; Modify `Ai/AiBrain.cs`, `Ai/IAiManager.cs` (`AiContext`), `Net/AiPlayers.cs`; Test Create `Tests/EditMode/AiDiceTests.cs`.

**Interfaces — Produces:**
- `enum AiSlip : byte { Production, Upkeep, AttackTiming, Retreat, Target }`
- `sealed class AiDice { AiDice(uint seed, float mistakes, float slipSeconds); float Next(); bool Slip(AiSlip kind, double now); float Noise(float insight, float amount); }` — seed 0 uses stream seed 1 (rolls still work) but `AiDice.Varies` is false.
- `static class AiVariation { static AiStyleConfig Vary(AiStyleConfig style, AiDice dice, float amount, bool vary); }` — clones, multiplies `FirstAttackSeconds, AttackArmySize (rounded, ≥ 1), AttackIntervalSeconds, ExpandTiming` and each `ArmyMix` weight by `1 ± amount`, picks `BuildOrder` from `BuildOrders`; with `vary` false returns the clone with `BuildOrders[0]`.
- `AiContext`: `Dice`, `bool Attentive` (rolled each think), `bool Slip(AiSlip)`, `float Noise()`, `AiStyle StyleName`, `Style` settable internally by `AiBrain.SetStyle(AiStyle)`; `float GatherAngle` (radians, 0 unless varied).
- `AiSeat.VariationSeed : uint` (server randomness at match start; under `DevApi.Allowed` a hash of the lobby seed and owner id, so bench pairs are reproducible); `AiBrain(…, uint seed = 0)`; `AiBrain.For(seat)` passes it.

- [ ] **Step 1: Tests:** `SeedZeroDoesNotVary`; `SameSeedSameStyle`; `VariedNumbersStayInBounds` (100 seeds, every factor within ±`Variation`); `ABuildOrderIsPickedFromTheStyles`; `MistakesZeroNeverSlips`; `SlipsRollOncePerWindow` (two calls in the same window: at most one true); `SlipRateMatchesMistakes` (10,000 windows, 0.25 ± 0.03); `NoiseIsOneAtFullInsight`.
- [ ] **Step 2–4:** implement, wire into `AiBrain` (dice from seed; the four styles varied once with the same dice sequence so a switch keeps the variation; `Attentive = dice.Next() < Difficulty.Attention` each think), compile.

### Task 3: Two action budget pools

**Files:** Modify `Ai/ActionBudget.cs`, `Ai/AiAction.cs`; Test `Tests/EditMode/AiDirectorTests.cs` (budget tests live there).

**Interfaces — Produces:** `AiAction.IsArmy` (Order, OrderSquad, AssignSquad, Detonate, DigArea); `ActionBudget(int apm, float burstSeconds, float waitSeconds, float economyShare = 0.5f)`; `Tokens` stays the sum of both pools; `EconomyTokens`, `ArmyTokens`.

- [ ] **Step 1: Tests:** `EconomyIsTakenWhenArmyOrdersFlood` (APM 30, 20 army proposals at High and one production at Low: the production is taken this think); `APoolWithNothingWaitingLendsItsTokens` (only army proposals: all tokens are spent on them); existing budget tests unchanged.
- [ ] **Step 2–4:** two token counts refilled at `share` and `1 − share` of the rate, capacity likewise (each ≥ 1); `Spend`: pass 1 takes each proposal in priority order from its own pool; pass 2 takes the rest in priority order from whatever tokens remain. `AiBrain` passes `Policy.EconomyShare`.

### Task 4: The fight model and forces

**Files:** Create `Ai/FightModel.cs`, `Ai/AiForces.cs`; Test Create `Tests/EditMode/AiFightModelTests.cs`.

**Interfaces — Produces:**
- `struct Force { float Hp, HpLight, HpHeavy, HpBuilding, DpsLight, DpsHeavy, DpsBuilding, RangeHp, Value; int Count; }`
- `static class FightModel { void AddUnit(ref Force f, GameConfigData g, UnitType t, float healthShare, AiPolicyConfig p); void AddTurret(ref Force f, GameConfigData g, BuildingType t, float healthShare); float Advantage(in Force ours, in Force theirs, float rangeWeight); float Survivors(float advantage); }` — `Advantage` is `+∞` against an empty force and 0 for an empty own force.
- `static class AiForces { Force Own(AiContext ai, Func<AiUnit,bool> pick); Force VisibleEnemy(AiContext ai, float2 at, float radius); Force KnownTurrets(AiContext ai, float2 at, float radius, int owner = -1); Force EnemyArmy(AiContext ai, int owner); void Add(ref Force into, in Force more); }` — `EnemyArmy` is the enemy model's peak counts (Task 5), else its last sighting.

- [ ] **Step 1: Tests:** `EqualForcesAreEven` (10 Tanks v 10 Tanks: 1 ± 0.01); `DoubleTheForceIsFourTimesTheStrength` (square law); `FlamethrowersBeatScoutsAtEqualCost`; `TanksBeatSnipersAtEqualCost`; `TurretsCountForTheDefenders`; `RangeHelpsTheLongerSide` (Artillery v Heavies with `RangeWeight` 0 vs 0.5); `NothingToFightIsInfinite`; `SurvivorsAtAdvantageFourIsAboutEightySevenPercent`.
- [ ] **Step 2–4:** implement per spec §3.1 (`BomberFightSeconds` turns a bomber's blast into a damage rate), compile.

### Task 5: Kill ledger and enemy models

**Files:** Create `Ai/EnemyModel.cs`; Modify `Ai/Blackboard.cs`, `Ai/AiBrain.cs`; Test Create `Tests/EditMode/AiMemoryTests.cs`.

**Interfaces — Produces:**
- `Blackboard.EnemyLost`, `Blackboard.OwnLost` (cumulative value, floats), `Blackboard.TurretsLostValue`; `Blackboard.Models : Dictionary<int, EnemyModel>`; `Blackboard.Avoided : List<(int2 at, double until)>`, `bool IsAvoided(int2 at, float radius)`; `Blackboard.LastBaseThreat : double`.
- `enum EnemyStyleGuess : byte { Unknown, Rusher, Turtle, Expander }`
- `sealed class EnemyModel { int Owner; float[] Peak; int[] Buildings; double[] FirstBuildingSeen; double FirstCombatSeen; bool Expands; EnemyStyleGuess Guess; float ArmyValue(GameConfigData g); }`
- `Blackboard.UpdateModels(TeamView view, double playingSeconds, AiPolicyConfig policy, GameConfigData game)` called by the brain after `State.Update`; `Blackboard(owner, reactionSeconds, brain, float armyMemorySeconds)`.

- [ ] **Step 1: Tests:** `AUnitThatVanishesInSightDied`; `AUnitThatLeavesSightDidNot`; `ADestroyedBuildingCountsAGhostDoesNot`; `OwnLossesCount`; `PeakCountsDecayByHalfLife`; `EarlyArmyGuessesRusher`; `ThreeTurretsGuessTurtle`; `AnEarlyExpansionGuessesExpander`.
- [ ] **Step 2–4:** implement in `Observe` (ledger needs last think's enemy units and buildings by id) and `UpdateModels`; compile.

### Task 6: Upkeep planner and the bank rule

**Files:** Modify `Ai/AiState.cs`, `Ai/Managers/Economy.cs`, `Ai/Managers/Builder.cs`, `Ai/Managers/Production.cs`; Test `Tests/EditMode/AiManagerTests.cs` (new tests), `AiSandbox.cs` if helpers are needed.

**Interfaces — Produces:** `AiState.IncomeSoon`, `BuildingUpkeep`, `bool MayAddBuildingUpkeep(float upkeep)` (true during the opening), `bool TrySpendBuilding(float cost, float upkeep)`, `bool Banked` (balance above `BankHigh` for `BankSeconds`), `bool Deficit`; upkeep slip inside `MayAddUpkeep`.

- [ ] **Step 1: Tests:** `ProducersLeaveRoomForAnArmy` (sandbox: income 45, a Barracks and a Heavy Factory wanted: the second isn't placed while building upkeep would pass `BuildingUpkeepShare`); `EasyPlansUpkeepToo` (Easy in a deficit asks for a Miner at Critical and queues no unit); `ABankAddsAProducerBeyondTheStyles`; `ABankFillsQueues`.
- [ ] **Step 2–4:** implement; `BuilderManager.WantBuildings`: the producer count wanted is the style's plus `ExtraProducers` while `Banked`, the `IncomePerProducer` gate is skipped while `Banked`, non-Miner requests go through `TrySpendBuilding` after the opening; `EconomyManager`: `squeezed |= Banked && capped`, Miner priority Critical in a `Deficit`; `ProductionManager`: while `Banked`, an army producer with fewer than `QueueBatch` queued takes a batch.

### Task 7: Production from the enemy model

**Files:** Modify `Ai/Managers/Production.cs`, `Ai/Managers/Builder.cs` (`LightEnemies`); Test `Tests/EditMode/AiManagerTests.cs` (`CountersComeFromSeenUnitsAndByDifficulty` rewritten).

**Interfaces — Consumes:** `Blackboard.Models`, `AiState.MixBias : float[]` (multipliers by unit type, 1 by default, set by Task 10), `AiState.TurretCounterBoost` (Task 8). **Produces:** unchanged `Mix`.

- [ ] **Step 1: Tests:** `CountersFollowTheEnemyModel` (Heavies in the model: Snipers and Artillery share rises; Easy's is smaller than Hard's but above zero; with `Insight` 0.2 building counters are off); `MixBiasShiftsTheMix`; `AProductionSlipSkipsARefill`.
- [ ] **Step 2–4:** `BuildMix` uses the models' peak counts, `CounterShift × CounterWeight` for units, `BuildingCounterWeight × BuildingCounterWeight` (config) for buildings (GunTurret entries × `TurretCounterBoost`), then multiplies by `MixBias` and renormalises.

### Task 8: The army

**Files:** Modify `Ai/Managers/Army.cs`; Test `Tests/EditMode/AiManagerTests.cs`, Create `Tests/EditMode/AiArmyTests.cs`.

**Interfaces — Consumes:** `FightModel`, `AiForces`, `Blackboard` ledger/models/avoided, `AiContext.Slip/Noise/Attentive/GatherAngle`, `AiState.FirstAttackFactor`, `AiState.HoldFirstAttack`, `AiState.Raiders`. **Produces:** `ArmyManager.Caution`, `BadTradeStreak`, `LastTrade`, `Trades` (list for status), `internal static float ScoreTarget(...)`, `ChooseTarget(ai, from, preferOwner, out id, out owner, out target)` (scored).

- [ ] **Step 1: Tests:** `ASoftMinerBeatsATurretGuardedHq`; `ABeatableHqWins`; `AnAvoidedTargetScoresLess`; `NoAttackItCannotWin` (enemy army seen twice its size: no attack); `ABadTradeRaisesCautionAndAvoidsTheTarget`; `AGoodTradeLowersCaution`; `FocusFireOrdersOnlyUnitsInRange`; `SiegeHoldsShortRangeUnits`; `AHomeGuardStaysAfterABaseThreat`; `AnAttackTimingSlipAttacksAnyway`; existing `AnAttackTargetsOnlyBuildingsItKnows` still passes.
- [ ] **Step 2–4:** replace every `StrengthEstimate` comparison with `FightModel.Advantage × ai.Noise()` per spec §3.1; scored `ChooseTarget` (§3.4; `Target` slip takes the second best); siege; home guard; focus fire (§3.6); attack records from the ledger with caution and avoidance (§4.1); micro behind `ai.Attentive`; gather point turned by `GatherAngle`; first attack at `Style.FirstAttackSeconds × FirstAttackFactor`, held while `HoldFirstAttack`.

### Task 9: Raiders

**Files:** Create `Ai/Managers/Raid.cs`; Modify `Ai/AiBrain.cs` (manager list), `Ai/AiState.cs` (`Raiders`); Test `Tests/EditMode/AiArmyTests.cs`.

**Interfaces — Produces:** `RaidManager : IAiManager` with `Active`, `Target`, `Raids` (count).

- [ ] **Step 1: Tests:** `ARaidGoesForAnUndefendedMiner`; `NoRaidIntoTurrets`; `ARaidComesHomeWhenItsTargetIsGone`; `TurtleNeverRaids`.
- [ ] **Step 2–4:** implement per spec §3.5 (attention-gated, one raid at a time, keyed proposals `AiKeys.Raid`).

### Task 10: Strategy

**Files:** Create `Ai/Managers/Strategy.cs`; Modify `Ai/AiState.cs` (`RushExpected, HoldFirstAttack, FirstAttackFactor, ExtraTurrets, MixBias, TurretCounterBoost`), `Ai/AiBrain.cs`, `Ai/Managers/Builder.cs` (`EarlyTurrets + ExtraTurrets`); Test Create `Tests/EditMode/AiStrategyTests.cs`.

**Interfaces — Produces:** `StrategyManager` with `Switches` (log), `CurrentStyle`.

- [ ] **Step 1: Tests:** `ARushExpectedAddsTurretsAndHoldsTheFirstAttack`; `AGreedyEnemyIsPunishedSooner`; `ATurtleEnemyShiftsToArtillery`; `AMaxedTurtleBreaksOut`; `TwoBadTradesTurnARusherIntoAnExpander`; `StylesSwitchAtMostOncePerWindow`.
- [ ] **Step 2–4:** implement per spec §4.3–§4.4.

### Task 11: Status, bench output and the tuning tool

**Files:** Modify `Ai/AiBrain.cs` (`Status`), `Dev/AiBench.cs` (seat result: caution, trades, switches, raids), `tools/ai-bench-brief.js` (show them if present), `tools/ai-tune.js`.

- [ ] **Step 1:** `aistate` gains `style` (current), `switches`, `caution`, `trades`, `raid`, `strategy`.
- [ ] **Step 2:** `ai-tune.js`: `bench.step` (8) and `bench.maxSeeds` (32): a screen candidate is extended in steps while its Wilson interval overlaps the best's and stops when it is clearly above or below; the confirm stage plays the base config on the same fresh seeds; default `confirm.seeds` 32. `--dry-run` prints the schedule.
- [ ] **Step 3:** `node tools/ai-tune.js tools/ai-tune.example.json --dry-run` runs.

### Task 12: Config values and docs

**Files:** `Resources/GameConfig.xml` (two or three build orders per style, `Raids`), `docs/gameplay.md`, `docs/architecture.md`, `docs/tuning.md`, `docs/known-issues.md`, `docs/playtesting.md`, the v0.14 spec (pointer).

- [ ] **Step 1:** build-order variants (Rusher: two-Barracks / Scout-first; Turtle: turret-first / Heavy-first; Expander: double-expand / Barracks-first; Balanced: two-Barracks / fast Heavy Factory / fast expand).
- [ ] **Step 2:** docs per spec §10 and §11.

### Task 13: Verify

- [ ] `tools/unity-compile.sh`; `tools/unity-test.sh EditMode`; `tools/unity-test.sh PlayMode` — all pass.
- [ ] A Linux build and `tools/ai-bench.sh --brief` (Hard-Normal, Normal-Easy, all sizes), compared with spec §1's exit criteria; results recorded in `docs/perf/v0.14/ai-bench.md` and `known-issues.md`, whatever they show.
- [ ] Style balance (spec §6.3): `ai-tune.js` with the `style` objective on the style knobs, if the bench shows one style deciding matchups.
- [ ] The 7-Hard-AI perf run if a release build is available (`perf.part.ai` ≤ 2 ms p95).
