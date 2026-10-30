# AI overhaul: Design

**Status:** written 2026-10-30 from the owner's answers (§13). The owner asked for the spec and plan to be written and built straight away, reviewing them later; every choice not in §13 is listed in §14 with the default that was built. Numbers are starting values to tune with `tools/ai-tune.js`.
**Parent:** [v0.14 AI opponents](2026-10-23-v0.14-ai-opponents-design.md) (this changes its §5 and §6; §1 to §4 and §7 to §12 still hold). **Plan:** [2026-10-30-ai-overhaul.md](../plans/2026-10-30-ai-overhaul.md).

## 1. Goal

The v0.14 AI plays a whole game but the owner is unhappy with it. The bench ([perf/v0.14/ai-bench.md](../../perf/v0.14/ai-bench.md)) and the known issues show why:

- **It doesn't spend.** Every difficulty ends matches with 4,900 to 5,800 unspent.
- **It judges fights by cost.** `StrengthEstimate` values a unit at its cost times its health share, blind to armour, range and splash, so attacks, retreats and recalls are often wrong.
- **It attacks the nearest HQ first,** whatever defends it, so big armies die on turret lines and 1024 games reach the 40-minute cap.
- **Its economy can starve itself:** producers' upkeep (a Heavy Factory costs 30/s, three Miners' income) outgrows its income and it never attacks.
- **It is predictable:** one build order per style, fixed numbers, one army group, no change of plan.
- **The difficulty ladder doesn't hold:** Hard beats Normal in 38 to 88% of games because each difficulty is ~25 separate knobs.

The overhaul fixes these in five areas (intelligence, variability, adaptability, speed and the difficulty model) without breaking v0.14's rules: **the AI sees only what its team sees and acts only through `PlayerActions` within its action budget** (`AiLeakTests`, `AiAssemblyTests` keep passing).

**Exit criteria:** over `tools/ai-bench.sh` with at least 16 games per cell, Hard beats Normal and Normal beats Easy in ≥ 80% of games on every map size (spec v0.14 §6), no difficulty's mean highest balance (the bench's "Highest balance" column, 4,900 to 5,800 today) above 2,500, and no more than 1 in 16 matches reaching the cap. How hard each level feels to a person is the owner's playtests (§11).

**Out of scope:** a targeted "attack this unit" order for players (§13), sim determinism (the ±job-timing noise between runs), machine-learned play, AI chat.

## 2. Difficulty: one policy plus handicaps

### 2.1 The model

The AI has **one policy**: the best play the managers know, with its numbers in a new `<Ai><Policy>` block. **Hard plays the policy.** Normal and Easy run the same code with **handicaps**, five numbers per difficulty that only ever make it play worse:

| Handicap | Meaning | Easy | Normal | Hard |
| --- | --- | --- | --- | --- |
| `ThinkSeconds` | Seconds between thinks (unchanged) | 1.0 | 0.5 | 0.25 |
| `ActionsPerMinute` | The action budget (unchanged, now split, §5.1) | 30 | 80 | 200 |
| `ReactionSeconds` | Delay before acting on an event (unchanged) | 6 | 2 | 0.5 |
| `Insight` (0..1) | How much it makes of what it sees: counters, enemy memory, scouting rate, fight-estimate accuracy | 0.2 | 0.6 | 1 |
| `Attention` (0..1) | Share of thinks its army micro runs (retreat and regroup checks, focus fire, backline, bombers, digging, raids) | 0.3 | 0.7 | 1 |
| `Ambition` (0..1) | Scale of its economy: Builders, expansions, Miner saturation, producers beyond the style's | 0.4 | 0.75 | 1 |
| `Mistakes` (0..1) | Chance a key decision slips (§2.3) | 0.25 | 0.1 | 0 |

`Peace` and `GiftsSurplus` stay per difficulty: they are diplomacy temperament (v0.14 §7, owner decisions), not strength.

### 2.2 Resolving a difficulty

`AiPolicy.Resolve(config, difficulty)` turns the policy and a difficulty's handicaps into the `AiDifficultyConfig` the managers already read (so managers change little). Every resolved number is monotone in its handicap, so **a better handicap can never make it play worse by construction**; a test checks Easy ≤ Normal ≤ Hard on every resolved number in its "better" direction.

| Resolved setting | From |
| --- | --- |
| `Builders` | `max(2, round(Policy.Builders × Ambition))` |
| `BuildersPerExpansion`, `MaxExpansions` | `round(Policy.X × Ambition)`, `MaxExpansions` at least 1 |
| `ExpandAfterSeconds` | `Policy.ExpandAfterSeconds / max(0.25, Ambition)` |
| `SaturatesMiners` | `Ambition ≥ Policy.SaturateAmbition` (0.5) |
| `IncomePerProducer`, `KeepsUpkeepPaid`, `UpkeepReserveSeconds`, `DefendsChokepoints`, `EarlyTurrets` | the policy's (everyone plans upkeep, §3.2; a slip can still skip a check) |
| `CounterWeight` (replaces `Counters`) | `Insight`: the unit counters' share of `CounterShift`; building counters weigh `max(0, 2·Insight − 1)` |
| `Scouts` | `max(1, round(Policy.Scouts × Insight))` |
| `RescoutSeconds` | `Policy.RescoutSeconds / max(0.2, Insight)`; a Radar whatever the style when `Insight ≥ 0.9` |
| `Scouting` | `Once` below `Insight` 0.3, else `Regular` (`RegularWithRadar` from 0.9) |
| `SpecialUse` | `Attention` (specials need micro) |
| `AttackAdvantage`, `RetreatAdvantage` | the policy's (§3.1); `Insight` adds estimate noise, §2.3 |
| `Retreats`, `KeepsBacklineBack`, `FocusFire`, `DigsPastWalls` | the policy's flags; they run on `Attention`'s share of thinks |
| `ArmyMemorySeconds` (per brain) | `Brain.ArmyMemorySeconds × max(0.3, Insight)` |

### 2.3 Slips and noise

A brain has a seeded random stream (§6.1). `ai.Slip(kind)` is true with probability `Mistakes`, rolled at most once per kind per `SlipSeconds` (10 s, so a slip lasts a moment, not a match). Slips:

- **Production:** a producer isn't refilled this think.
- **Upkeep:** the planner's upkeep check is skipped for one purchase (Easy can still go unpaid now and then).
- **Attack timing:** an attack starts without waiting for the advantage it wants (it overextends).
- **Retreat:** it misses one retreat check.
- **Target:** it picks the second-best target.

Its fight estimates (§3.1) are multiplied by a factor drawn per estimate from `1 ± (1 − Insight) × Policy.EstimateNoise` (0.4): Easy misjudges fights, Hard sees them as they are.

`Attention` is rolled once per think: on a miss the army micro of that think is skipped (its macro still runs).

## 3. Intelligence

### 3.1 A fight model from the damage table

`FightModel` (new, `Ai/FightModel.cs`, pure) estimates who wins a fight between two forces from the public numbers:

- Each side's **damage per second** against the other: each unit's `Damage / AttackInterval` times the damage-table multiplier against the other side's armour classes, weighted by the other side's health in each class. Splash units get `× (1 + Splash × Policy.SplashTargets)`; Suicide bombers count their blast once.
- Each side's **hit points**, counted by armour class.
- **Range:** a side whose health-weighted mean range beats the other's gets `× (1 + Policy.RangeWeight × min(1, Δrange / theirs))` on its damage (it hits first and longer).
- **Turrets** (finished Gun turrets it knows) fight on the defenders' side with their building damage-table entries; they are Building armour.
- **Lanchester's square law:** strength = damage per second × hit points; `Advantage = ours / theirs` (>1 favours us), and the share of its force it expects to keep is `sqrt(1 − 1/Advantage)` when it wins.

It replaces cost-based strength in the army's decisions: **attack** when `Advantage ≥ Policy.AttackAdvantage × style scale × caution (§4.1)`, **retreat** when the local `Advantage < Policy.RetreatAdvantage`, **recall** when home's advantage against a threat at its base is below 1, **counter-attack** from Defend when home's advantage is at least `CounterAttackAdvantage`. With no enemy seen, the advantage is infinite (it attacks, as today). Diplomacy and conceding keep `StrengthEstimate` (they ask "who is bigger", for which cost is fine).

### 3.2 One upkeep planner

Upkeep decisions already go through `AiState.MayAddUpkeep`; the planner makes them **the same for every difficulty** and adds three rules:

1. **Projected income** counts Miners being built (as the Economy manager does today) and drops Miners whose gem is gone.
2. **Building upkeep share:** buildings that cost upkeep (producers, Radars, Jammers, turrets, bombs) may together take at most `Policy.BuildingUpkeepShare` (0.5) of its projected income, so producers always leave room for an army. This is the cause of the 2026-10-26 starvation (building upkeep 50 to 54 against 45 income).
3. **Miners first in a deficit:** while its projected upkeep is above its income, a Miner request is Critical and no unit or upkeep building is bought (today this is Normal and Hard only).

### 3.3 Spend the bank

When its balance stays above `Policy.BankHigh` (1,500) for `Policy.BankSeconds` (30 s), it is wasting money, and in this order of preference it:

1. queues `QueueBatch` on every army producer that has room (production capacity it already has);
2. if its upkeep allows another unit's worth: adds a producer beyond the style's count, up to `round(Policy.ExtraProducers × Ambition)` (20; the building upkeep share of §3.2 limits them first, so they scale with income), skipping the `IncomePerProducer` gate;
3. if its upkeep doesn't (capped): expands early and saturates Miners (more income, more upkeep room), and its next attack starts as a maxed attack (v0.14's rule, kept).

### 3.4 Choosing targets

`ChooseTarget` scores every target it knows (enemy buildings, remembered HQs, last-seen armies) instead of taking the nearest HQ:

`score = value / (1 + distance / Policy.TargetDistanceScale)`, kept only if the fight model's advantage against what defends it (turrets it knows within `ArmyRadius`, plus a last-seen army whose centre is that close) reaches the attack threshold.

- **Value:** an HQ `Policy.HqValue` (it wins the game), a producer or Radar its cost, a Miner its cost plus `Policy.MinerIncomeValue` (income denied), a turret its cost, a last-seen army the value of the units in it.
- The attack itself still needs the advantage of §3.1 over the target's enemy: its army as the enemy model sees it (§4.2) plus the turrets it knows at the target. The local filter above only ranks and rules out targets.
- An HQ whose defences it can't beat is skipped for the softest target it can beat; with none it can beat it doesn't attack (unless maxed). It stops throwing armies at turret lines.
- **Finishing blow:** when its whole force's advantage over an enemy's remembered army reaches `Policy.FinishAdvantage` (4), that enemy's HQ is worth that many times more, so a far stronger AI goes for the kill instead of picking off outlying buildings (found in the first bench, §15).
- **Unreached targets:** a building target the group's front hasn't reached in `Play.MarchSeconds` (walled off, across rock, a ghost it never sees again) is avoided and another picked; if it picks the same one, the attack ends (found in the second bench, §15).
- **Avoided targets:** a target where an attack lost badly (§4.1) scores `× Policy.AvoidFactor` (0.25) for `Policy.AvoidSeconds` (240 s).
- **Siege:** if the target's defence is mostly turrets and its group has units that outrange them (Artillery, Snipers against a 7-tile turret), its short-range units hold at the staging point while the long-range ones attack-move the turrets; once the turrets it knew there are gone, the whole group engages.

### 3.5 Squads: the main army, a home guard and raiders

The army keeps its one main attack group and adds two squads:

- **Home guard:** after it has seen a threat at its base, an attack leaves `Policy.HomeGuardShare` (0.2) of its army's value at the gathering point for `Policy.HomeGuardSeconds` (300 s) after that threat.
- **Raiders:** when its style raids (`<Style><Raids>`, Rusher and Balanced) and the attention roll allows, up to `Policy.RaidSize` (4) of its fastest units (move speed ≥ `Policy.RaidMinSpeed`, 6: Scouts and Suicide bombers) form a raid. The raid targets the known enemy Miner or expansion building with the best `value / distance` whose known defence the raiders beat (`Advantage ≥ Policy.RaidAdvantage`, 1.5), attack-moves there, and comes home when its advantage drops below 1, its target is gone, or `Policy.RaidSeconds` (120 s) pass. Raiders are left out of the main group the way scouts are. At most one raid at a time.

### 3.6 Focus fire (an approximation)

There is no targeted attack order, so focus fire stays an attack-move (owner, §13). It changes from "the whole group attack-moves to the weakest enemy's tile" (which dragged the group, so v0.14's tuning turned it off) to: **only its units already in range of the weakest enemy it sees near the front** (distance ≤ the unit's range + 1) attack-move to that enemy's tile, at least 2 of them, at most every `OrderSeconds`; the rest keep the group order. `FocusFire` is the policy's (on); turn it off in `<Policy>` if the bench disagrees.

## 4. Adaptability

### 4.1 Learning from its attacks

The blackboard keeps a **kill ledger** from its view: an enemy unit that was in sight last think and isn't now, where the cell it stood on is still visible, died (its value counts as the enemy's loss); an enemy building that was seen (not as a ghost) and is gone while its cell is visible was destroyed. Its own losses are its own units and buildings that vanish. Each attack records both ledgers at its start and end (end: the group gone, a retreat, or no target left), giving a **trade ratio** (enemy value lost / own value lost).

- A bad trade (`< Policy.BadTrade`, 0.6) raises its **caution** (the attack threshold's factor) by `× 1.25`, up to 2, and marks the target avoided (§3.4); if turrets did most of the defence, it raises its building counters against turrets (`× 2` for that match).
- A good trade (`> Policy.GoodTrade`, 1.5) lowers caution `× 0.9`, down to 0.8.
- The army log and `aistate` show each attack's trade and the caution.

### 4.2 An enemy model

`EnemyModel` (per enemy owner, on the blackboard) replaces the single last sighting as the AI's picture of each enemy:

- **Peak counts by unit type** seen within `ArmyMemorySeconds`, decaying (a count not seen again halves every `Policy.ModelHalfLife`, 120 s): its composition, not just what one glance showed.
- **Buildings seen by type**, the time it first saw each type, and whether it has a building in an expansion region (it expands).
- **Estimated army value**, from the peak counts.
- **A style guess:** Rusher (combat units seen before `Policy.RushSeconds`, 240 s, or two Barracks), Turtle (more Gun turrets than the policy's `EarlyTurrets` + 1, which every AI builds, or walls), Expander (a building at an expansion before `Policy.FastExpandSeconds`, 360 s), else unknown.

Counters (`ProductionManager.BuildMix`), the light-enemy turret rule and the fight model's picture of an enemy's army use the model's peak counts; target positions still come from sightings and known buildings. `ArmySighting` stays for where an army was.

### 4.3 Reacting to what scouting finds

A `StrategyManager` (new) reads the enemy model each think:

- **Rush expected** (any enemy's guess is Rusher while it has under `AttackArmySize` units): `EarlyTurrets + 1` turrets in front of its HQ at High priority, its own first attack waits until it has seen off a rush wave (a threat at its base that ended) or `FirstAttackSeconds + 120` passed, and its mix shifts towards Flamethrowers and Tanks.
- **Greedy enemy** (Expander guess, or an enemy expansion before its own): its first attack comes `× Policy.PunishFactor` (0.75) sooner.
- **Turtle enemy:** its mix shifts towards Artillery and its style's Diggers (×2), and it raids more (the raid's target ignores the defended base).

### 4.4 Changing style mid-game

The style an AI starts with can change once per `Policy.StyleSwitchSeconds` (180 s), by the `StrategyManager`:

| From | When | To |
| --- | --- | --- |
| Turtle | past its style's `FirstAttackSeconds` and maxed out (upkeep-capped, bank above `BankHigh`), or play time over `Policy.TurtleBreakoutSeconds` (900 s) with an advantage over the enemy it knows | Balanced |
| Rusher | two attacks in a row with a bad trade | Expander |
| Expander | a rush expected before its first attack | Balanced |

A switch keeps everything built and takes the new style's numbers (with the same variation, §6.1); the build order is not rerun. The end screen still shows the style it started with (owner O3 is about the resolved Random style); `aistate` shows the current one and the switches.

## 5. Speed

### 5.1 Two action budgets

The action budget splits into an **economy pool** (production, placements, builders, rallies, cancels, diplomacy) and an **army pool** (orders, detonations, digging, squads), `Policy.EconomyShare` (0.4) of the APM going to the economy. Each pool spends its own tokens, highest priority first; a pool with nothing waiting lends its spare tokens to the other, so Hard's APM is never wasted and Easy's production never waits behind army orders (the known issue).

### 5.2 Faster, fewer capped matches

The target choice (§3.4), the bank rule (§3.3) and the Turtle breakout (§4.4) are what shorten matches. The bench's cap stays 40 minutes.

### 5.3 A tuning loop that can tell values apart

`tools/ai-tune.js`:

- **Paired seeds:** every candidate plays the same seeds and seat order (already true for the screen; the confirm stage now plays the base config on the same fresh seeds as each candidate).
- **Sequential stopping:** a screen candidate plays `bench.seeds` games, then more in steps of `step` (default 8) up to `maxSeeds` (default 32), and stops as soon as its Wilson 95% interval is entirely below the best so far (clearly worse) or entirely above it (clearly better).
- **Confirm by default on 32 fresh seeds** (was 16).

`AiThink` time stays within v0.14's budget (≤ 2 ms p95 for 7 Hard AIs, `perf.part.ai`); the fight model is a sum over the units in the fight, and the enemy model and kill ledger are per-think passes over the view the AI already makes.

## 6. Variability

### 6.1 Per-match variation

Each AI seat gets a **variation seed** at match start (server randomness, like the Random style; never sent to clients). The brain's random stream comes from it, and at brain creation its style's numbers are varied: `FirstAttackSeconds`, `AttackArmySize`, `AttackIntervalSeconds`, `ExpandTiming` and each `ArmyMix` weight are multiplied by a factor in `1 ± Policy.Variation` (0.2), and its gathering point turns by up to `± Policy.GatherAngle` (30°) from the line to the enemy. A brain made with seed 0 (tests) is not varied.

### 6.2 Several openings per style

A `<Style>` may list several `<BuildOrder>`s; the brain picks one with its random stream. Each style gets two or three (for example Balanced: two-Barracks, fast Heavy Factory, fast expand).

### 6.3 Style balance

The styles are re-tuned so no style decides the matchup: with `ai-tune.js`'s `style` objective, each style's mirror-pairing win rate against the others in 35 to 65% (today Rusher wins about 6 in 7, Turtle 0 in 4).

## 7. What each manager changes

| Manager | Changes |
| --- | --- |
| `AiBrain` | resolves the difficulty (§2.2), owns the random stream and the variation (§6), the attention roll, two budget pools (§5.1), the current style (§4.4) |
| `Blackboard` | kill ledger (§4.1), `EnemyModel`s (§4.2), memory scaled by `Insight` |
| `ArmyManager` | fight model for attack, retreat, recall and counter-attack (§3.1), scored targets and siege (§3.4), home guard (§3.5), focus fire (§3.6), attack records, trade and caution (§4.1), slips (§2.3) |
| `RaidManager` (new) | raiders (§3.5) |
| `StrategyManager` (new) | scouting reactions (§4.3), style switches (§4.4) |
| `EconomyManager` | upkeep planner rules (§3.2), bank rule's expansion step (§3.3) |
| `BuilderManager` | bank rule's extra producers (§3.3), building upkeep share (§3.2), rush turrets (§4.3) |
| `ProductionManager` | counters from the enemy model and `CounterWeight` (§2.2, §4.2), strategy shifts (§4.3), bank batches (§3.3), production slips |
| `OpeningManager` | picks among the style's build orders (§6.2) |
| `ScoutingManager` | scouts and rescout time from `Insight` |

## 8. Config

```xml
<Ai>
  <Difficulty name="Easy">  <ThinkSeconds>1.0</ThinkSeconds> <ActionsPerMinute>30</ActionsPerMinute> <ReactionSeconds>6</ReactionSeconds>
    <Insight>0.2</Insight> <Attention>0.3</Attention> <Ambition>0.4</Ambition> <Mistakes>0.25</Mistakes>
    <Peace>Always</Peace> <GiftsSurplus>0</GiftsSurplus> </Difficulty>
  … Normal, Hard …
  <Policy> … every number of §2.2 to §6 the policy plays by (Hard's v0.14 values where one existed) … </Policy>
  <Style name="Balanced"> <BuildOrder>…</BuildOrder> <BuildOrder>…</BuildOrder> … <Raids>1</Raids> </Style>
  … Diplomacy, Concede, Brain, Play, Counters unchanged …
</Ai>
```

The difficulty's old behaviour knobs leave `<Difficulty>` (the parser refuses them, so a stale config fails loudly); their best values move to `<Policy>`. Everything is parsed strictly; `ConfigParserTests` cover the new blocks.

## 9. Testing

- **EditMode:** `FightModel` (equal forces even; damage-table counters win: Flamethrowers beat Scouts, Snipers lose to Tanks at equal cost; turrets count; range bonus); `AiPolicy.Resolve` (Hard equals the policy; every resolved number monotone from Easy to Hard); the budget pools (economy proposals are taken when army proposals flood; lending); slips (rate and once per `SlipSeconds`; Mistakes 0 never slips); variation (seed 0 changes nothing; same seed same result; bounds); several build orders parsed and picked; target scoring (a soft Miner beats a turret-guarded HQ; an HQ wins when beatable; avoided targets); the kill ledger (death only counted when the cell is visible); trade and caution; the enemy model (decay, style guesses); the strategy's reactions and style switches; the upkeep planner (building upkeep share; Miners first in a deficit, at every difficulty); the bank rule; raids pick undefended Miners and come home; focus fire orders only in-range units.
- **Existing:** `AiLeakTests` and `AiAssemblyTests` unchanged and passing (nothing new reads past the view or acts outside `PlayerActions`); the v0.14 manager, diplomacy, director and PlayMode tests updated where a difficulty knob moved.
- **Bench:** `tools/ai-bench.sh 16` on all three sizes; results into `docs/perf/v0.14/ai-bench.md` (carrying the tuning history over), §1's exit criteria.
- **Perf:** the 7-Hard-AI gate run keeps `perf.part.ai` ≤ 2 ms p95.

## 10. Docs

`gameplay.md` (the AI section: difficulties as handicaps), `architecture.md` (the AI managers), `tuning.md` (the policy and handicap knobs, the new tune options), `known-issues.md` (the AI strength row, the 2026-10-26 starvation, focus fire), and a pointer from the v0.14 spec's §5 and §6 to this one.

## 11. Owner playtests

Not something the code can do: a short checklist goes into [playtesting.md](../../playtesting.md): one match per difficulty against each style, noting how soon it attacks, whether it feels fair, whether Easy is beatable by a new player and Hard is not, and anything it does that looks dumb (with the `aistate` army log).

## 12. Risks

- **Strength regressions while the ladder is re-tuned.** Handicap values are guesses; the bench may need a few `ai-tune.js` passes on `Difficulty.*.Insight/Attention/Ambition/Mistakes`.
- **The kill ledger can misattribute** a unit that left vision at the edge of a visible cell; it only counts a vanish whose last cell is still visible, which errs towards counting fewer kills.
- **Raids and the home guard take units off the main attack**, which can lose an even fight it used to win; both are policy numbers, off when set to 0.

## 13. Decisions (owner, 2026-10-30)

| Topic | Decision |
| --- | --- |
| Difficulty model | One policy plus handicaps: Hard plays the policy; Normal and Easy run the same code handicapped |
| Focus fire | An AI-only approximation (only in-range units attack-move to the weakest enemy's tile); no new player order |
| Process | Spec and plan written in full, built at once; the owner reviews later |
| Branch | This branch (`release/v0.13-v0.14`), on top of the owner's uncommitted `GameConfig.xml` tuning |
| Scope | All of the recommendations (intelligence, variability, adaptability, speed, the difficulty model) |

## 14. Open decisions (defaults built)

| # | Question | Default |
| --- | --- | --- |
| D1 | Version name: a v0.14 follow-up or its own update number | A v0.14 follow-up (this spec), no roadmap entry |
| D2 | `IncomePerProducer` in the policy: the owner's working-tree 90, or 0 (the first tune's 16/16) now that the bank rule adds producers when money piles up | 90, with the bank rule; the bench decides |
| D3 | Does Easy retreat and micro at all (v0.14 said no)? | Yes, on its `Attention` share (0.3) of thinks: simpler, not absent |
| D4 | Does the end screen show the style an AI switched to? | No, the style it started with (`aistate` shows switches) |
| D5 | Variation seed: server randomness or from the lobby seed | Server randomness (the lobby seed is public, so a client could work out the AI's numbers); under `-perf` (the bench) a hash of the lobby seed and owner id, so paired bench runs see the same AIs |
| D6 | Raiding styles | Rusher and Balanced |
| D7 | Should Hard ever slip? | No (`Mistakes` 0) |
| D8 | §1's balance criterion (mean highest balance under 2,500) when AIs earn 1,000+/s late on | Keep measuring it, but judge spending by producers kept busy rather than the peak balance |
| D9 | 1024 games between two strong economies reach the 40-minute cap (§15, third run) | Leave the AI as it is and count a cap the stronger side leads as unfinished, not lost; make big-map games end through the economy (Miner output or upkeep late on), which is a balance change for the owner |

## 15. First bench (2026-10-30)

`tools/ai-bench.sh --brief 16` on a fresh build (Hard-Normal and Normal-Easy, 256/512/1024, 16 games a cell, 96 matches): **Normal beat Easy 90%** (256 16/16, 512 14/16, 1024 13/16: §1's target met on every size); **Hard beat Normal 69%** (14/16, 12/16, 7/16), with 14 of its 48 matches capped, mostly on 1024. v0.14's last run for comparison: 50 to 67% each. The caps showed two faults, fixed after it: a strung-out army regrouped for ever (its vanguard met a defence, fell back, re-engaged, ran ahead again: now one regroup per target, then it fights while the whole group wins), and a far stronger Hard picked off outlying buildings for 30 minutes without closing on the HQ (the finishing blow above). Balances stayed high (mean highest: Hard 12.5k, Normal 23.7k, Easy 12.3k) because the AIs now run economies of 1,000+/s and three extra producers couldn't spend it; `ExtraProducers` went to 20, limited by the building upkeep share. §1's balance criterion (2,500) looks wrong for economies this size and is an open question (D8).

The second run (same seeds, with those fixes): **Normal beat Easy 94%** (16/16, 16/16, 13/16); **Hard beat Normal 56%** (13/16, 10/16, 4/16) with 18 caps. Its logs showed the real cause: Hard attacked a building it could never reach (or a ghost it never saw again), and with no timeout on building targets its whole army, reinforcements included, waited in Attack mode for 30 minutes. v0.14's nearest-HQ targeting had hidden this. Fixed with the unreached-target rule above. Turtles also switched to Balanced within minutes, because their starting money counted as a bank; the breakout now waits for the Turtle's own first-attack time.

The third run (same seeds, with the unreached-target rule and the later Turtle breakout): **Normal beat Easy 79%** (15/16, 11/16, 12/16) and **Hard beat Normal 56%** (14/16, 11/16, 2/16), with 7 and 18 caps. The rates count a cap as a loss. Of the games that finished, the stronger side won almost all: Hard 27 of 30 and Normal 38 of 41 (first run 33 of 34 and 43 of 45; second run 27 of 30 and 45 of 45; v0.14 about 60%). At the cap it led on buildings in 16 of Hard's 18 and in all 7 of Normal's. So the difficulty ladder holds, and what fails §1 is games that don't end, 12 of Hard-Normal's 16 on 1024. Their logs show one attack lasting 35 minutes: two economies of 1,000+/s rebuild faster than either army wears the other down. The army gives up on a target it can't reach, then picks it again once `AvoidSeconds` runs out. The unreached-target rule stopped the waiting, but the trade stays even. Making these games end is D9.
