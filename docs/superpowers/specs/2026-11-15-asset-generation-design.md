# Asset generation overhaul: Design

**Status:** written 2026-11-15. The owner approved all 50 ideas on 2026-11-15 (25 on versatility, ease of use and control, then 25 on quality) and asked for one spec covering them. The owner has not seen the details below: every choice the ideas left open is in §13 with the default this spec assumes. No plan is written yet.
**Progress (2026-11-15, branch `asset-gen/phase-0-1`):** phase 0 is built and waits for the owner's sound review (§2.1). Of phase 1: the strict reader (§3.4) for sound and song specs, the sound half of the golden check (§3.6, as `SpecJsonTests.EveryBuiltSoundIsItsSpecRendered`), the audio lint (§3.7) and the sound side of the review page (§3.3, `tools/sound-review.py`).
**Progress (2026-11-16):** the layered synth of §5.1 is built (`SfxLayers`: layers, PolyBLEP edges, exponential decays, white, pink and brown noise, tremolo, crush, soft clipper, reverb tail, DC blocker, edge fades; not yet the 4× oversampling or the multi-point pitch envelope), and the owner picked from two candidates for each of the 21 sounds (`docs/art/review/2026-11-15-redesign/`): 20 now use synth 2, `bomber_blast` keeps today's sound. Sounds without `"synth": 2` keep the old path byte for byte. The golden hashes of §3.6 are in (`Assets/Art/hashes.json`, `AssetHashes`, `AssetHashTests`, `WAR-2D/Art/Write hashes`): the generators reproduce the 12 shipped atlas files byte for byte. Not yet: `docs/art/asset-approvals.json`. The one registration point of §3.1 is in: each `ArtEntry` names its generator (`.Lit`, `.Fx`), the `ArtBuild.Generator` switch and `EffectGen.For` are gone, and the hashes show no sprite moved. The effects' entries sit in `Render/EffectGen.Manifest.cs` rather than an `ArtManifest.Effects.cs`, because some use `EffectGen`'s private helpers.
**Parents:** the [art style guide](../../art/style-guide.md) (v0.8; this changes its Finish and Palette rows), the [v0.20 asset review](../../art/v0.20-asset-review.md) (its process is reused, §10), [polish.md](../../polish.md).

Ideas are numbered as the owner saw them: **A1 to A25** are the first list, **B1 to B25** the second. §12 maps each one to its section.

## 1. Goal

Every sprite, effect, sound and track in the game is generated from code and parameter files (`RenderGen`, `EffectGen`, `Sfxr`, `SongWriter`). That works, but four things hold it back:

- **Quality.** Units are dark on a dark floor and their team areas are blotchy; ramps are one hue at five brightnesses; shadows are the silhouette shifted 4 px; movement is two frames. The synth is one aliasing oscillator with linear envelopes, and every shot of a type is the same WAV.
- **Versatility.** One oscillator per sound, one looping sound at a time, four fixed chip voices, two unrelated match tracks, one palette, one seed.
- **Ease of use.** Builds are editor menu items that rebuild everything. A new sprite is registered in three places. Each version has its own hand-written sample page. There is no preview.
- **Control.** Sprite proportions are C# constants. Which sound plays for which event is spread through client code. The mix is 50 separate `volume` numbers.

The overhaul fixes these in six phases (§2), tooling first, so that every later change to a shipped asset is seen before and after.

**Rules that still hold:**

- Nothing under `Assets/Resources` is hand-made, and no AI-made art or sound ships (`NoPlaceholderArtTests`, [ai-content.md](../../art/ai-content.md)).
- Builds are deterministic: the same code and parameters write the same bytes.
- Sounds and effects only follow events the client already draws, so nothing here can leak what a team doesn't see (`LeakTests`).
- **No asset ships without the owner's approval of that asset** (owner, 2026-11-15: "each and every"). Every changed sprite, effect, sound and track is approved one by one before it reaches `Assets/Resources` (§10). A bug fix, a rebuild or a batch that "looks fine" is not approval.
- Client changes that add per-frame or per-batch work re-run `tools/perf-10k.sh` and `tools/perf-100k.sh` and are compared with `tools/perf-compare.py` against a baseline taken first.
- No HUD or menu changes, so no Figma round. The preview window (§3.5) is an editor tool, not game UI.

**Exit criteria:** every phase's tests pass in both suites; `tools/check.sh` passes; the golden hashes (§3.6) match the committed assets; every row of the asset review is approved; `perf-10k` and `perf-100k` hold their targets (120 and 140 fps, mean and 1 % low) with every phase's client changes on; a full match on Linux.

**Out of scope:** new units, buildings or effects for new rules; recorded or sampled audio; AI-generated assets; voice lines; a settings screen for any of this; replacing Unity's audio engine.

## 2. Phases

| Phase | Name | Ideas | Changes shipped assets | Perf run |
| --- | --- | --- | --- | --- |
| 0 | Fix the wave bug | (found while writing this, §2.1) | Yes: 35 sounds | No |
| 1 | Tooling and checks | A9 to A11, A13 to A18, A21, A25, B24, B25 | No | No |
| 2 | Sprite finish | A7, A20, B1 to B7 | Yes: every sprite | No (atlas the same size) |
| 3 | Sound | A1 to A4, A8, A19, A22, B15 to B20 | Yes: every sound | 10k only (voice pool) |
| 4 | Music | A5, A6, A12, A23, B21 to B23 | Yes: every track | No |
| 5 | Effects, terrain, animation | B8, B10 to B14 | Yes | 10k and 100k |
| 6 | Lighting and themes | A24, B9 | Yes | 10k and 100k; B9 is cut if it fails |

Each phase is its own branch and PR and leaves a playable, tested build. Phases 3 and 4 don't depend on phase 2 and can run beside it.

### 2.1 Phase 0: the built sounds ignore their `wave`

Found on 2026-11-15 while reading the built WAVs for this spec. `shot_heavy.json` says `"wave": "Noise"` with no filter, but `Assets/Resources/Audio/Sfx/shot_heavy.wav` is a two-level square wave at 2,500 Hz. `gem_mined`, `ui_hover` and `sniper_aim` say `Sine` and are also square. The likely cause is that `AudioBuild.BuildSfx` reads the spec with `JsonUtility.FromJson`, which reads an enum only from a number, so a name leaves `wave` at its default, `Square`. If so, **all 35 sounds that ask for Noise (27), Saw (4) or Sine (4) ship as squares**, including every explosion, and the v0.20 sounds were approved that way.

- **Fix:** read the specs with the strict reader of §3.4 (the project already has `com.unity.nuget.newtonsoft-json`), and add a test that builds a Noise, a Saw and a Sine spec from JSON text and checks the result is not a square.
- **Then:** rebuild, and put all 35 changed sounds on a review page beside what ships today. The owner picks per sound: the corrected wave, or keep the square (by changing the JSON to `Square`, so the file says what plays).
- **Built 2026-11-15:** the cause is confirmed (in the editor, `JsonUtility` reads `"wave": "Noise"` as `Square`), the reader and tests are in, the 35 sounds are rebuilt and the music is byte-identical. Review page: https://claude.ai/artifact/UxGcFi2HVbWyfNAdwHDwRn (`docs/art/review/2026-11-15-wave-fix/`).
- **Owner's review (2026-11-15):** 13 sounds take the corrected wave; `radar_ping` keeps its square; 21 are to be redesigned (the owner liked neither, or found the noise distorted) and keep today's square until then, with their specs changed to `Square`. The list is in `known-issues.md`. The redesign uses synth 2 (§5.1), so §5.1 and those 21 sounds are the next sound work, ahead of the rest of phase 3.
- This comes first because phases 1 and 3 would otherwise measure and tune the wrong waveforms. It is listed in `known-issues.md`.

## 3. Phase 1: tooling and checks

Nothing in this phase changes a shipped byte. The golden hashes (§3.6) prove it.

### 3.1 One registration point for art (A15)

Today a sprite needs an `ArtManifest` entry, a `case` in the `ArtBuild.Generator` switch (about 35 cases) and, for an effect, a branch in `EffectGen.For`.

- `ArtEntry` gains a generator: `Func<ArtCell, LitCanvas>` for lit sprites or `Func<ArtCell, EffectFrame>` for effects, where `ArtCell` carries the frame, direction, cell index and the build seed. It also carries the 3/4 lift and a cache key.
- `ArtManifest` moves to a file per kind beside its generators (`ArtManifest.Units.cs` and so on) so the entry and the generator it names sit together. `WAR2D.Art` already holds `RenderGen`, so no assembly changes.
- The switch and `EffectGen.For` are deleted. An entry with no generator draws `RenderGen.Placeholder`, as now.
- **Tests:** `ArtManifestTests` gains "every entry has a generator or is on the placeholder list"; the hashes show the atlases didn't change.

### 3.2 Command-line builds and single assets (A13, A14)

- `tools/art-build.sh` and `tools/audio-build.sh` run the editor in batch mode (`-executeMethod`, as `tools/steam-upload.sh` does) with the editor closed, like `tools/check.sh`.
- Both take `--only <glob>` (`--only Tank*`, `--only shot_*`). For art, only the matching entries are re-rendered; the rest of the atlas is re-packed from a frame cache in `Library/ArtCache/`, keyed by the hash of the entry's parameters and the generator assembly's build id. Sounds are files, so `--only` just skips the others.
- Both print a **change report**: each sprite frame or sound whose hash changed, and write `TestResults/<time>-<commit>/assets/` with before and after PNGs (at 4×) and WAVs. Nothing changed prints one line.
- The menu items stay and call the same code.

### 3.3 Review pages from a list of ids (A16, B24, B25)

`RenderSamplesMenu` has separate code for v0.12, v0.13 and v0.20.

- One tool, `tools/asset-review.sh <ids or globs>`, writes a page to `docs/art/review/<date>-<name>/`:
  - **Sprites:** every player colour and facing at 1×, 2× and 4×, and a **contact sheet** on real floor and rock at near, default and far zoom, each beside the committed version.
  - **Effects:** frame strips and looping clips on a floor.
  - **Sounds:** a player per sound and variant, with its waveform and spectrogram (drawn by the tool from the samples), beside the committed version.
  - **Tracks:** a player, and the form (sections, chords, where the motif returns).
- The clip staging helpers of `RenderSamplesMenu.OnslaughtFx` (a floor, layered sprites, effects at a point) become the tool's library; the per-version menus stay only for the archived pages.
- The contact sheets are the working loop for phases 2 and 5: Claude views them and iterates before the owner is asked.

### 3.4 Strict, readable parameter files (A18)

- A small reader, `SpecJson`, over Newtonsoft: enums by name, unknown fields refused, numbers range-checked from attributes on the spec class, every error with the file name and field. It reads the sound specs, song specs, the sprite parameter files (§4.7) and the sound event table (§3.8).
- `MatchCalm.json`'s `"mood": 0` becomes `"mood": "Calm"`, and so on for the five tracks.
- **Tests:** every spec file under `Assets/Art/` loads; a misspelt field and an out-of-range value each fail with the file's name.

### 3.5 Preview window (A17)

`WAR-2D/Art/Preview` and `WAR-2D/Audio/Preview`: editor windows (IMGUI, editor assembly).

- **Art:** pick an entry; shows it at 1× to 8× on floor or rock, in a chosen player colour, animating; re-renders when its parameter file changes; a toggle shows the committed version.
- **Sound:** pick a spec; sliders for every `SfxrParams` field, play on change, A/B with the committed WAV, save back to the JSON.
- **Music:** pick a spec, render and play, with the form shown.

### 3.6 Golden hashes and approval tracking (A10, A25)

- `Assets/Art/hashes.json`: a SHA-256 per sprite (its finished frames, before packing, so a re-pack doesn't change it), per sound and per track. Written by the builds.
- **Test:** the hashes of what the generators produce now equal the file, and the file equals the committed assets. A helper change that shifts 40 sprites by a pixel fails with the 40 names.
- `docs/art/asset-approvals.json`: per asset, the hash that was approved, the date and the review page. `tools/asset-review.sh --pending` lists every asset whose hash differs from its approved one. The hand-kept tables in `docs/art/v0.20-asset-review.md` stay as history; new reviews are generated from this file.
- The first commit of the file records only approvals the owner actually gave (the rows of the v0.12, v0.13 and v0.20 asset reviews, and the 2026-11-15 wave review). An asset on `main` with no recorded approval is listed as "no record" for the owner to decide; nothing is marked approved by assumption.

### 3.7 Lint tests (A9, A11)

New EditMode tests, each first run in report-only mode to list today's failures, which become that phase's work list:

- **Sound (A9):** no sample at full scale for more than 2 in a row; mean within ±0.5 % (no DC offset); first and last sample under 1 % (no click); length within the category's budget; a loop's end joins its start (step under 2 %).
- **Art (A11):** no opaque pixel with no opaque 4-neighbour; the outline closed round every sprite; team pixels at least 8 % and at most 60 % of a team sprite; every sprite's mean OKLab lightness at least a set margin above the floor's and below the rock top's (§4.2); every player colour's team ramp at least ΔE 10 from the floor.

### 3.8 Sound event table (A21)

- `Assets/Resources/Audio/SoundEvents.json`, read with `SpecJson`: one row per sound id with its **category** (UI, Alert, Blast, Shot, Impact, Work, Ambient, Stinger), gain trim in dB, cooldown, priority and variant count.
- `AudioService` reads cooldown, gain and (from phase 3) priority from the row. Callers keep passing ids, so no call site changes.
- In phase 1 every row carries today's values (cooldown 0.05 s, gain 0 dB), so nothing sounds different.
- **Tests:** every sound spec has a row and every row a spec; every row's id appears in game code (a scan of `Assets/Scripts`, or the `AlertSounds` table), so a sound that is built but never played fails.

## 4. Phase 2: sprite finish

All of this is in `LitCanvas`, `PixelFinish`, `ArtStyle` and the generators. The atlases keep their sizes and frame tables, so the client doesn't change.

### 4.1 Hue-shifted ramps (B1)

`PixelFinish.Shades` multiplies RGB by 0.55 to 1.55. Each ramp becomes five steps in OKLCH: lightness as now, hue turned toward blue-violet in the two dark steps and toward yellow in the two light ones (starting at ±12° and ±6°), chroma raised slightly in the mid steps.

- The team key ramp stays value-only: the shaders shade the owner's colour by the mask step, and a hue shift there is a shader change (§13 D4).
- The ΔE 10 clearance from player colours is re-run; the dropped-shade table in the style guide is rewritten from the result.
- `Assets/Art/palette.png` is rewritten; `ArtStyleTests` keep code and file in step.

### 4.2 Contrast against the terrain (B2, with the A11 lint)

- The rule: a sprite's non-team pixels have a mean lightness at least **0.12 OKLab L** above the floor's mean. Today's tracks and hulls fail it.
- Met by lifting `Rubber` and `Hull` one ramp step and by a **rim light**: a second, weak light from the lower right in `LitCanvas.Render` that only brightens edges facing away from the main light.
- Scouts and Builders (the smallest sprites) are checked on the far-zoom contact sheet first.

### 4.3 Clean team surfaces (B3)

- `Grime` takes a mask so team material gets a tenth of the mottle and no height jitter.
- Each unit's team area gets one designed marking from a small set (`Stripe`, `Chevron`, `Band`), drawn as a groove and a ramp step, chosen per unit type in its parameter file.

### 4.4 Cleanup after quantising (B4)

A pass in `PixelFinish` after `PaletteQuantizer`:

- A pixel whose colour none of its 8 neighbours share takes the commonest neighbour colour.
- Clusters of 2 pixels inside a larger area of one ramp are merged into it, unless a generator marks the spot (rivets, lamps) through a new "keep" layer on the canvas.
- Runs before the outline and shadow.

### 4.5 Selective outlines (B5)

- The outer outline stays 1 px but takes one of three palette colours: the outline colour on the shadow side, one step lighter on the lit side (by the outward direction against `LightDir`), and the darkest step of the team ramp where it borders team pixels.
- **Inner lines:** where the height field steps by more than a threshold between neighbouring opaque pixels, the lower pixel takes the next darker step of its own ramp.
- All colours stay in the palette, so `AtlasPixelsArePaletteTeamGreyOrShadow` still holds.

### 4.6 Shadows from the height field (B6)

- The baked shadow becomes the height field's projection along the light's ground direction: a pixel is in shadow when a higher pixel lies toward the light within `height × ShadowSlope`. The alpha stays `ArtStyle.ShadowAlpha` (one value, as the atlas test needs).
- The same test inside the sprite darkens self-shadowed pixels one ramp step (a turret's shadow on its hull, a tower's on its roof).
- **Limit:** hull and turret layers are separate sprites, so a turning turret's shadow on its hull stays the existing `BarrelShadow` effect.

### 4.7 Supersampling, parameters and variation (B7, A20, A7)

- `LitCanvas.Supersample` 2 → 4. Build time only.
- **Sprite parameters (A20):** each generator's tunable numbers (lengths, radii, heights, materials, marking, grime) move to `Assets/Art/Sprites/<Id>.json`, read with `SpecJson` into a typed class per generator. The generator code keeps the structure; the file holds the numbers. A missing file fails the build.
- **Variation (A7):** the parameter file may hold a `variants` list of overrides and a `seed`. Used first for: three wear levels per building (drawn by health band, replacing nothing the client doesn't already know) and four floor and rock tone variants. The light direction stays fixed: the style guide requires it.
- Building wear by health uses the health the client already has for its bar; `LeakTests` are unaffected.

## 5. Phase 3: sound

### 5.1 The synth (A2, A3, B15, B16, B17, B18)

`Sfxr` gains a second path, chosen per spec by `"synth": 2`. A spec without it renders through today's path, byte for byte (the hashes prove it), so sounds move over one at a time, each reviewed. When all have moved, the old path is deleted.

Synth 2:

- **Layers (A2, B18):** a spec is a list of layers, each with its own wave, envelope, pitch, filters, offset and gain, mixed to one clip. Presets give the three-part impact: a **transient** (2 to 8 ms click), a **body** (pitched, sliding) and a **tail** (filtered noise), with an optional sine **sub** at 40 to 70 Hz for large blasts.
- **Band-limited oscillators (B15):** PolyBLEP on square and saw edges, and the whole sound rendered at 4× and filtered down.
- **Waves (A3):** triangle, pink noise, brown noise. A pitch envelope of up to four points in place of the single arpeggio step.
- **Envelopes (B16):** `"curve": "exp"` per stage; exponential is the default in synth 2.
- **Tone (A3, B17):** bit-crush (bits and sample-rate divide), a tanh soft clipper with a drive amount before the final gain in place of the hard clamp, and a short baked tail (feedback delay, or a small comb-and-all-pass reverb) for blasts and collapses.

### 5.2 Variants (A1)

- A spec may set `variants` (1 to 6) and a `jitter` block (pitch, decay, filter cutoff, each a ± range). The build renders `name.wav` and `name_2.wav` … with seeds derived from the spec's seed.
- `AudioService.Clip` picks a variant at random, never the one that id played last.
- Every positional play also turns the voice's pitch by a random ±4 % (a claimed voice is idle, so this only moves that sound, as with pan).
- `NoPlaceholderArtTests.EverySoundHasASourceSpec` accepts the variant names.
- Starting counts: shots 4, impacts and small blasts 3, large blasts and deaths 2, UI and alerts 1.

### 5.3 Loudness and the mix (A8, A22, B20)

- **Loudness (A8):** the build measures each sound's loudness as the RMS of its loudest 100 ms after an A-weighting filter, and sets its gain so its category's target is met. `volume` in the spec is replaced by `category` (from the event table) and an optional `trim` in dB.
- **Mix table:** the category targets live at the top of `SoundEvents.json`. Starting values, relative to Blast at 0 dB: Stinger 0, Alert −3, Impact −6, Shot −8, Work −12, UI −12, Ambient −16.
- **Priorities (A22):** when all 16 voices are busy, a new sound takes the voice of the lowest-priority sound playing (oldest first) if its own priority is higher. Order: Alert and Stinger, the player's own units' Blasts and deaths, other Blasts, Impacts, Shots, Work, Ambient. UI sounds keep their own source.
- **Ducking:** a Blast above a size, a Stinger or an Alert lowers the music 3 dB for 0.4 s (attack 30 ms, release 300 ms), in `MusicPlayer`.
- **Bus compressor (B20):** a compressor on the SFX group of `Main.mixer` (ratio 3:1, threshold −12 dB, attack 5 ms, release 120 ms). The mixer is a Unity asset, so it is edited in the editor, not by hand (§13 D9).

### 5.4 Space (A4, B19)

- **Loops (A4):** `AudioService.Loop` keeps up to three loops at once, keyed by id, each with its own source, fade and pan. Only `construct_loop` uses it today; `burn_loop` and `turret_fire_loop` are played as one-shots through `Sfx` and move to it.
- **Distance and zoom (B19):** each of the 16 voices gets an `AudioLowPassFilter`. The cutoff falls from open at the view's centre to 2.5 kHz at the edge of the audible margin, and the whole field's cutoff and gain fall as the camera zooms out (from the default zoom to the furthest: cutoff to 4 kHz, gain −6 dB).

### 5.5 Presets and mutate (A19)

In the sound preview window and as `tools/audio-build.sh --mutate <id> [n]`:

- **Presets:** shot, blast, impact, pickup, blip, alarm, loop bed. Each writes a starting spec with seeded random values in that preset's ranges.
- **Mutate:** writes `n` candidates of a spec (default 8), each with a few fields nudged, to a review page to audition. Picking one copies it over the spec.

## 6. Phase 4: music

### 6.1 The spec and the instruments (A5)

`SongSpec` gains, all optional with today's values as defaults (so the five tracks build unchanged until edited):

- `instruments`: per channel, the wave or duty, volume, envelope decay and octave; for drums, each drum's clock, decay and volume. These are constants in `ChiptuneRenderer` today.
- `progressions`: per section letter, an explicit list of scale degrees, in place of a random pick from the five built-in ones.
- `motif`: a short list of scale-degree and length pairs that the lead states and returns to.
- `swing`, `echo` and `vibrato` amounts (§6.3).

### 6.2 Structure (B21)

`SongWriter.Melody` today walks chord tones on strong beats and scale steps between. It becomes:

- Section A states the motif (given, or generated once from the seed) in its first bar and answers it in the second; later bars vary it by transposing to the chord, inverting or changing the rhythm.
- The last bar of every section ends on a cadence: the fifth or the tonic on the final strong beat, with the bass moving to the dominant before a repeat.
- At least one beat of rest in every two bars of the lead; Calm leaves more.
- A motif named in a spec can be shared: Menu, MatchCalm and MatchBattle use the same one, so the game has a theme.
- **Tests:** the motif appears at least twice per A section; every section's last lead note is the tonic or fifth; `LeadNotesAreInTheKey` still holds.

### 6.3 Performance and sound (A12, B22, B23)

- **Performance (B22):** vibrato on lead notes longer than a beat, after a delay; an echo voice (the lead repeated a dotted eighth later at a third of the volume); swing on off-beat sixteenths; accents on beats 1 and 3.
- **Stereo (A12):** tracks render in stereo at 44.1 kHz: bass and kick centred, lead slightly left, arpeggio right, echo opposite the lead, hats slightly right. The music import drops `forceToMono`. The WAVs in git grow about four times, from 8 MB to about 33 MB in total (§13 D7).
- **Mastering (B23):** the kick lowers the bass 3 dB for its length; a look-ahead limiter replaces the peak normalise, with the track's loudness set to a target.
- Pulse and triangle voices get the PolyBLEP of §5.1.

### 6.4 One match track in layers (A6, A23)

- MatchCalm and MatchBattle become **one song rendered as three stems**: a bed (bass, arpeggio, soft drums), a drive stem (full drums, a second arpeggio) and a lead stem (the battle melody). Same key, tempo, form and length, so they stay in step.
- `MusicPlayer` plays the three stems on sources started together (`PlayScheduled`) and fades each by an **intensity** from 0 to 1: bed always, drive from 0.35, lead from 0.7, each fading over 2 s.
- **Intensity (A23)** is worked out in `AudioService` from what the client already draws: visible deaths per second (as now), units on screen that fired in the last 2 s, and whether the player's own HQ or producers were hit in the last 10 s. It rises fast (1 s) and falls slowly (`BattleHoldSeconds`).
- The weights and thresholds go in `GameConfig.xml` under `<Audio>` beside `BattleDeathsPerSecond` and `BattleHoldSeconds`, read through `ConfigLoader`.
- The `MusicCue` values `MatchCalm` and `MatchBattle` merge into `Match`; `CurrentCue` callers and `AudioPlayModeTests` are updated.

## 7. Phase 5: effects, terrain and animation

### 7.1 Baked particle simulation (B11)

- A small deterministic simulator in `WAR2D.Art` (`EffectSim`): seeded particles with velocity, drag, buoyancy, lifetime, temperature and size, stepped at a fixed rate, splatted to a density and temperature field and then dithered to the effect palette with the existing Bayer pattern.
- Used first for the blasts, smoke, dust, fire and flame splash. Sparks, rings and tracers stay formula-drawn.
- Runs only at build time. The runtime still plays frames.

### 7.2 Frames, blending and glow (B12, B13)

- **Frames (B12):** effects get their own rate. `ArtEntry` gains `Fps` (default `ArtStyle.FramesPerSecond`, 10); blasts, fire and smoke move to 20 fps with twice the frames. `Effects.cs` reads the entry's rate where it reads the constant today.
- The Effects atlas is 4096 × 4096 now. If doubled frames don't fit, the large blasts are simulated at half resolution and drawn at 2×, or the atlas splits in two.
- **Glow (B13):** effects marked `Additive` in the manifest (fire, tracers, muzzle flashes, sparks, the hot core of blasts) draw in a second pass with additive blending. One more draw call per effect batch.

### 7.3 Terrain depth (B14)

- **Tone:** the `Terrain` shader multiplies floor and rock by a large-scale noise (wavelength about 24 tiles, ±6 % lightness), from world position, so no new art.
- **Contact shadow:** floor tiles next to rock get a darkened edge on that side: an overlay stamp per 4-neighbour mask, drawn like the feature stamps.
- **Cast shadow:** rock casts a shadow onto floor to the lower right, one stamp per mask of the three neighbours toward the light.
- Terrain changes are drawn only on cells the team sees or remembers, which is what the shader already draws.

### 7.4 Animation (B8)

- Move frames 2 → 4 for hulls; the tread groove advances a quarter link per frame.
- Recoil eases over 3 frames (out fast, back slow) in place of one recoil frame.
- Idle motion where it can be seen: exhaust shimmer on Heavies, the Radar's sweep, the Miner's drill (already 4 frames).
- The Units atlas is 4096 × 2048 with 8 or 16 facings per frame, so this is the phase's main memory cost. If it would pass 4096 × 4096, the 16-facing turret layers keep their frame counts and only hulls grow.
- `InstancedUnit` picks frames from the frame table already, so the renderer changes only in how it steps them.

### 7.5 Far-zoom sprites (B10)

- Each unit gets a `Far` sprite: 16 px per tile, a larger team area, no rivets or grooves, a thicker outline, drawn by the same generator with a `far` flag in its parameters.
- The unit renderer switches to the Far frames below a zoom threshold (where a tile is under about 20 screen pixels), for all units at once, so the cost is one comparison per frame.
- Buildings keep mipmaps: they are large enough to read.

## 8. Phase 6: lighting and themes

### 8.1 Normal and emissive atlases (B9)

- The build writes two more atlases per kind from data `LitCanvas` already has: normals (from the height field) and emission (`Glow`).
- `InstancedUnit` and `InstancedBuilding` add light from up to 16 **flash lights** (muzzle flashes, blasts, fire), passed as global shader arrays each frame from the effects the client is already drawing. The added light is snapped to three steps so sprites still read as pixel art.
- Emission adds to the sprite and feeds the additive pass of §7.2, so lamps and gems glow.
- **Gate:** `perf-10k` and `perf-100k` within their own run-to-run spread of the baseline (`perf-compare.py`), and both still at target. If not, B9 is cut down to emission only, and if that fails too it is dropped and recorded in `known-issues.md`. There is no settings toggle: that would be a menu change.

### 8.2 Style themes (A24)

- `ArtStyle.Palette` and `StylePalettes.Charcoal` become one **theme**: role colours, ramp hue shifts, outline colours and terrain tones, in `Assets/Art/Themes/<name>.json`.
- The build takes a theme and writes the atlases to `Resources/Art/<theme>/`; `ArtAtlas` loads the theme a map names (a new optional field in the map's data, default Charcoal).
- First use: one alternate terrain look (§13 D11). Player colours are not part of a theme: the three palette modes stay as they are, and "colourblind team colours" stays on `polish.md`'s not-pursued list.
- Each theme re-runs the ΔE clearance and the contrast lint.

## 9. Config and files

| What | Where |
| --- | --- |
| Sound specs, with `synth`, layers, `variants`, `jitter` | `Assets/Art/Audio/sfx/*.json` |
| Song specs, with instruments, progressions, motif | `Assets/Art/Audio/music/*.json` |
| Sound event table and mix targets | `Assets/Resources/Audio/SoundEvents.json` |
| Sprite parameters | `Assets/Art/Sprites/*.json` |
| Themes | `Assets/Art/Themes/*.json` |
| Golden hashes | `Assets/Art/hashes.json` |
| Approvals | `docs/art/asset-approvals.json` |
| Music intensity weights and thresholds | `GameConfig.xml` `<Audio>` |
| Review pages | `docs/art/review/<date>-<name>/` |

Mix levels and sprite proportions are presentation, not balance, so they stay out of `GameConfig.xml`. The music intensity numbers go there because the two it replaces already are.

## 10. Asset review

The v0.20 process, generated from §3.6:

- **Candidates never go into `Assets/Resources`.** A changed asset is rendered into the review folder only. The built file in `Resources` changes when, and only when, the owner has approved that asset; until then the game's copy stays byte-identical. A code change that would alter built output (the synth, the renderer, the palette) keeps the old output path until each affected asset is approved.
- Approval is per asset and in the owner's words. "Change", "not answered" and silence all mean the shipped asset stays as it is.
- A commit or PR that touches built assets lists each one with its approval.
- A phase's changed assets are rendered to the review folder, `tools/asset-review.sh --pending` writes the page (before and after for every one), and the page is published as a private artifact link.
- The owner approves or asks for changes per asset or per batch. Approvals are written to `asset-approvals.json` with the hash, so a later change to an approved asset shows as pending again.
- A phase doesn't merge while anything it changed is pending.
- Phases 2 and 3 change every sprite and every sound. They are shown in batches (units, buildings, terrain; shots, blasts, alerts and UI), each with one global before-and-after scene or battle recording first, so the owner sees the overall change before the details. A batch is only a way to present them: every asset in it still gets its own answer.

## 11. Testing, performance and docs

- **Tests:** listed per section. Both suites pass before each phase lands; `tools/check.sh` is the gate.
- **Performance:** a baseline of at least three `perf-10k` and three `perf-100k` runs is saved (`perf-compare.py --save`) before phase 3, and compared after phases 3, 5 and 6. The server tick is untouched, so `tools/perf-run.sh` is run once at the end as a check.
- **Memory:** atlas sizes before and after phases 5 and 6 go in the perf notes.
- **Docs:** `docs/art/style-guide.md` (finish, palette, outlines, shadows, frames, themes), `docs/art/inventory.md`, `CLAUDE.md` (the Art and sound paragraph and "Adding content", which name the three registration points and the menu builds), `docs/architecture.md` (audio service, music stems, lighting), `docs/known-issues.md`, and a new `docs/art/pipeline.md`: how to add a sprite, an effect, a sound and a track, and the review loop.

## 12. Where each idea is

| Idea | Section | Idea | Section |
| --- | --- | --- | --- |
| A1 Sound variants | 5.2 | B1 Hue-shifted ramps | 4.1 |
| A2 Layered sounds | 5.1 | B2 Contrast against terrain | 4.2 |
| A3 More synth blocks | 5.1 | B3 Clean team surfaces | 4.3 |
| A4 More than one loop | 5.4 | B4 Cleanup after quantising | 4.4 |
| A5 Music spec and instruments | 6.1 | B5 Selective outlines | 4.5 |
| A6 Layered match music | 6.4 | B6 Shadows from height | 4.6 |
| A7 Art variation | 4.7 | B7 Higher supersampling | 4.7 |
| A8 Loudness and mix table | 5.3 | B8 Smoother animation | 7.4 |
| A9 Audio lint | 3.7 | B9 Normal and emissive atlases | 8.1 |
| A10 Golden hashes | 3.6 | B10 Far-zoom sprites | 7.5 |
| A11 Art lint | 3.7, 4.2 | B11 Baked particle simulation | 7.1 |
| A12 Stereo, full-rate music | 6.3 | B12 More effect frames | 7.2 |
| A13 Command-line builds | 3.2 | B13 Additive glow | 7.2 |
| A14 Build one asset | 3.2 | B14 Terrain depth | 7.3 |
| A15 One registration point | 3.1 | B15 Band-limited oscillators | 5.1, 6.3 |
| A16 Review-page generator | 3.3 | B16 Exponential envelopes | 5.1 |
| A17 Preview window | 3.5 | B17 Soft saturation | 5.1 |
| A18 Validated JSON | 3.4 | B18 Transient, body, tail | 5.1 |
| A19 Presets and mutate | 5.5 | B19 Distance and zoom filtering | 5.4 |
| A20 Sprite parameters as data | 4.7 | B20 Bus compressor | 5.3 |
| A21 Event-to-sound table | 3.8 | B21 Musical structure | 6.2 |
| A22 Priorities and ducking | 5.3 | B22 Performance detail | 6.3 |
| A23 Music state from config | 6.4 | B23 Mastering | 6.3 |
| A24 Style themes | 8.2 | B24 Contact sheets | 3.3 |
| A25 Approval tracking | 3.6 | B25 Spectrogram sheets | 3.3 |

## 13. Decisions taken by default (the owner can still change any of them)

The owner approved the 50 ideas as listed. These are the choices the list left open.

| # | Question | Default |
| --- | --- | --- |
| D1 | Is this a numbered update | No: a side project like the AI overhaul, landing phase by phase beside the roadmap. It could instead be v0.21 |
| D2 | Order of phases | As §2: the wave fix, tooling, then sprites; sound and music can run beside sprites |
| D3 | The 35 sounds that ship as squares (§2.1) | Fix the reader, then the owner picks per sound between the corrected wave and the square that was approved |
| D4 | Hue shift on team colours | None: the team ramp stays value-only, so the shaders and the three palette modes are untouched |
| D5 | Moving existing sounds to synth 2 | One at a time with a review each; the old path stays until all have moved |
| D6 | Variant counts and pitch jitter | Shots 4, impacts 3, blasts 2, UI and alerts 1; ±4 % pitch per play |
| D7 | Music format | Stereo 44.1 kHz; the WAVs in git grow from about 8 MB to about 33 MB. The alternative is stereo 32 kHz (about 24 MB) |
| D8 | Loudness measure | A-weighted RMS of the loudest 100 ms: simple, and close enough for short sounds. Not full LUFS |
| D9 | Who edits `Main.mixer` for the compressor | Claude, through the live editor, since hand-editing the asset is avoided; the owner checks the result by ear |
| D10 | A settings toggle for dynamic lighting | None: it holds the perf targets or is cut (§8.1) |
| D11 | The first extra theme | One alternate terrain look (a warmer, sandier ground), offered on maps by name; no theme changes player colours |
| D12 | Building wear by health | Three levels at 100 to 67 %, 67 to 33 % and under 33 %, alongside the smoke and fire polish already shows. The owner left damage states out of v0.20's visual pass (roadmap §5), so this one needs a plain yes; without it A7 covers terrain variants only |
| D13 | Calm and Battle as one layered track | Yes, replacing both tracks. The owner approves the new track before the old two are deleted |
| D14 | What feeds music intensity | Visible deaths, units on screen firing, and hits on the player's own HQ and producers; nothing the client doesn't already draw |
| D15 | Far-zoom sprite switch | All units at once, where a tile is under about 20 screen pixels |
| D16 | Where sprite parameters live | JSON per sprite under `Assets/Art/Sprites/`, not `GameConfig.xml` (presentation, not balance) |
| D17 | Lints that fail on today's assets | Report-only until the phase that fixes them, then enforced |
| D18 | Effect frame rate | 20 fps for blasts, fire and smoke; everything else stays at 10 |
| D19 | The dynamic light's look | Snapped to three steps, so lit sprites stay pixel art; the alternative is smooth light |
| D20 | Old sample menus and pages | Kept for the archived v0.12, v0.13 and v0.20 pages; no new ones are written |

## 14. Risks

- **Review load.** Phases 2 and 3 change every sprite and sound. The batch-first review (§10) and the global before-and-after are there so this is a few sittings, not hundreds of rows.
- **Atlas memory.** More unit frames, far sprites, doubled effect frames, and two more atlases per kind for lighting. Each is measured as it lands, and §7.2 and §7.4 say what gives way first.
- **Two synth paths.** Kept only while sounds move over; the hashes show the old path never changes.
- **The palette test.** Hue-shifted ramps, selective outlines and the cleanup pass all have to stay inside the palette, and the clearance from player colours can drop different shades than today.
- **Lighting cost at 100,000 units.** The one item that may not ship (§8.1).
- **Stereo music in git.** About 25 MB more, once, and again each time a track changes.
