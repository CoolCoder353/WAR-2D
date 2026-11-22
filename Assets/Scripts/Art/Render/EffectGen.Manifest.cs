namespace WAR2D.Art.Render
{
    public static partial class EffectGen
    {
        /// <summary>
        /// The effects and overlays, each with the generator that draws its cells (<see cref="ArtManifest"/> lists them after the
        /// tiles). <c>c.T</c> is the cell's place in the effect, 0 to 1; a variant- or direction-major effect reads
        /// <c>c.Direction</c> and <c>c.Frame</c>.
        /// </summary>
        internal static ArtEntry[] Manifest() => new[]
        {
            // Effects and overlays.
            new ArtEntry("Explosion", ArtKind.Effect, 2, 2, 6, 1, false).Fx(c => Blast(2, c.T, 11)),
            new ArtEntry("BombBlast", ArtKind.Effect, 3, 3, 8, 1, false).Fx(c => Blast(3, c.T, 23)),
            new ArtEntry("MuzzleFlash", ArtKind.Effect, 1, 1, 2, 1, false).Fx(c => MuzzleFlash(c.Index)),
            // Combat animations (2026-11-06): a flash per gun size along the barrel, a thin shot per gun, the shell with its ground
            // shadow and trail, a crater, the flame and the raised barrel's shadow (the last three soft: EffectGen.IsSoft).
            new ArtEntry("BurstScout", ArtKind.Effect, 1, 1, 2, 1, false).Fx(c => MuzzleBurst(BurstScoutTiles, c.Index)),
            new ArtEntry("BurstSniper", ArtKind.Effect, 1, 1, 2, 1, false).Fx(c => MuzzleBurst(BurstSniperTiles, c.Index)),
            new ArtEntry("BurstTank", ArtKind.Effect, 1, 1, 2, 1, false).Fx(c => MuzzleBurst(BurstTankTiles, c.Index)),
            new ArtEntry("BurstHeavy", ArtKind.Effect, 1, 1, 2, 1, false).Fx(c => MuzzleBurst(BurstHeavyTiles, c.Index)),
            new ArtEntry("Tracer", ArtKind.Effect, 1, 1, 1, 1, false).Fx(c => Tracer()),
            new ArtEntry("ScoutTracer", ArtKind.Effect, 1, 1, 1, 1, false).Fx(c => ScoutTracer()),
            new ArtEntry("SniperTracer", ArtKind.Effect, 4, 1, 4, 1, false).Fx(c => SniperTrail(c.T)), // v0.20: the Sniper's hard trail, fading over its frames
            new ArtEntry("ArtilleryShell", ArtKind.Effect, 1, 1, 2, ArtManifest.TurretDirections, false).Fx(c => HeavyShell(c.Angle, c.Frame)),
            new ArtEntry("ShellShadow", ArtKind.Effect, 1, 1, 1, 1, false).Fx(c => ShellShadow()),
            new ArtEntry("ShellPuff", ArtKind.Effect, 1, 1, 6, 1, false).Fx(c => ShellPuff(c.T)),
            new ArtEntry("Crater", ArtKind.Effect, 2, 2, DecalFrames, CraterVariants, false).Fx(c => Crater(c.Direction, c.Frame)),
            new ArtEntry("FlameStream", ArtKind.Effect, 2, 1, 6, 1, false).Fx(c => FlameStream(c.Index)),
            new ArtEntry("FlameSplash", ArtKind.Effect, 6, 6, 2, 1, false).Fx(c => FlameSplashSized(c.Index, 2.5f)), // v0.20: the fire over the 2.5-tile spread
            new ArtEntry("BarrelShadow", ArtKind.Effect, 3, 3, 1, ArtManifest.TurretDirections, false).Fx(c => BarrelShadow(c.Index)),
            new ArtEntry("ShellImpact", ArtKind.Effect, 4, 4, 6, 1, false).Fx(c => ShellImpact(c.T, 37)),
            new ArtEntry("BomberBlast", ArtKind.Effect, 4, 4, 8, 1, false).Fx(c => Blast(4, c.T, 29)),
            new ArtEntry("DigDebris", ArtKind.Effect, 1, 1, 4, 1, false).Fx(c => DigDebris(c.Index)),
            new ArtEntry("RepairSparks", ArtKind.Effect, 1, 1, 4, 1, false).Fx(c => RepairSparks(c.Index)),
            new ArtEntry("ConstructionOverlay", ArtKind.Effect, 1, 1, 4, 1, false).Fx(c => ConstructionOverlay(c.Index)),
            new ArtEntry("SelectionRingSmall", ArtKind.Effect, 1, 1, 1, 1, false).Fx(c => SelectionRingSmall()),
            new ArtEntry("SelectionRingLarge", ArtKind.Effect, 2, 2, 1, 1, false).Fx(c => SelectionRingLarge()),
            new ArtEntry("RoamRing", ArtKind.Effect, 1, 1, 1, 1, false).Fx(c => RoamRing()), // playtest 2026-10-30: under a roaming unit
            new ArtEntry("WaypointFlag", ArtKind.Effect, 1, 1, 2, 1, false).Fx(c => Flag(c.Index, false)),
            new ArtEntry("RallyFlag", ArtKind.Effect, 1, 1, 2, 1, true).Fx(c => Flag(c.Index, true)),
            new ArtEntry("HealthBar", ArtKind.Effect, 1, 1, 2, 1, false).Fx(c => HealthBar(c.Index)),
            new ArtEntry("TurretMuzzle", ArtKind.Effect, 1, 1, 3, 1, false).Fx(c => TurretMuzzle(c.Index)),
            new ArtEntry("JamPulse", ArtKind.Effect, 3, 3, 6, 1, false).Fx(c => JamPulse(c.T)),
            // Polish (2026-10-30): ground marks that fade through their frames (scorch where a unit died, rubble sized to a
            // destroyed building's footprint, tread marks in 4 directions), smoke and fire on damaged buildings, a Miner's gem glint.
            new ArtEntry("Scorch", ArtKind.Effect, 1, 1, DecalFrames, 1, false).Fx(c => Scorch(1, c.Index, 41)),
            new ArtEntry("ScorchLarge", ArtKind.Effect, 2, 2, DecalFrames, 1, false).Fx(c => Scorch(2, c.Index, 43)),
            new ArtEntry("Rubble1", ArtKind.Effect, 1, 1, DecalFrames, 1, false).Fx(c => Rubble(1, c.Index, 47)),
            new ArtEntry("Rubble2", ArtKind.Effect, 2, 2, DecalFrames, 1, false).Fx(c => Rubble(2, c.Index, 53)),
            new ArtEntry("Rubble3", ArtKind.Effect, 3, 3, DecalFrames, 1, false).Fx(c => Rubble(3, c.Index, 59)),
            new ArtEntry("Rubble5", ArtKind.Effect, 5, 5, DecalFrames, 1, false).Fx(c => Rubble(5, c.Index, 61)),
            new ArtEntry("Tracks", ArtKind.Effect, 1, 1, DecalFrames, TrackDirections, false).Fx(c => Tracks(c.Direction, c.Frame)),
            new ArtEntry("Smoke", ArtKind.Effect, 1, 1, 6, 1, false).Fx(c => Smoke(c.T, 67)),
            new ArtEntry("Fire", ArtKind.Effect, 1, 1, 4, 1, false).Fx(c => Fire(c.Index)),
            new ArtEntry("GemSparkle", ArtKind.Effect, 1, 1, 4, 1, false).Fx(c => GemSparkle(c.Index)),
            // Polish A, E and J (2026-11-01): a spark where a bullet lands, dust where a unit is made and around a finished building.
            new ArtEntry("HitSpark", ArtKind.Effect, 1, 1, 3, 1, false).Fx(c => HitSpark(c.Index)),
            new ArtEntry("DustPuff", ArtKind.Effect, 1, 1, 5, 1, false).Fx(c => DustPuff(c.T, 71)),
            // v0.20 Onslaught (approved 2026-11-06, docs/art/v0.20-asset-review.md): the leap, the Bomber's landing, fire on units and
            // buildings (sized 1, 2 and 3 tiles), soot, the flame's ground scorch, the aim laser and glint, the shot's hit, armour
            // sparks, the shell falling onto a player who can't see the gun, the launch puff, the big blast, the larger crater, the
            // 2×2 turret's twin flash and tracer stream, and the deaths (small, large, flung debris, dust).
            new ArtEntry("LeapSparks", ArtKind.Effect, 1, 1, 5, 1, false).Fx(c => LeapSparks(c.T, 7)),
            new ArtEntry("LeapShadow", ArtKind.Effect, 1, 1, 4, 1, false).Fx(c => LeapShadow(c.T)),
            new ArtEntry("BomberLanding", ArtKind.Effect, 5, 5, 8, 1, false).Fx(c => ShockBlast(5, 2f, c.T, 29)),
            new ArtEntry("BurnFlames1", ArtKind.Effect, 1, 2, 4, 1, false).Fx(c => Padded(BurnFlames(1, c.Index), T, 2 * T)),
            new ArtEntry("BurnFlames2", ArtKind.Effect, 2, 3, 4, 1, false).Fx(c => Padded(BurnFlames(2, c.Index), 2 * T, 3 * T)),
            new ArtEntry("BurnFlames3", ArtKind.Effect, 3, 4, 4, 1, false).Fx(c => Padded(BurnFlames(3, c.Index), 3 * T, 4 * T)),
            new ArtEntry("SootSmoke", ArtKind.Effect, 1, 1, 5, 1, false).Fx(c => SootSmoke(c.T, 3)),
            new ArtEntry("FlameScorch", ArtKind.Effect, 3, 1, 4, 1, false).Fx(c => FlameScorch(c.Index, 9)),
            new ArtEntry("AimLaser", ArtKind.Effect, 4, 1, 5, 1, false).Fx(c => AimLaser(c.T)),
            new ArtEntry("MuzzleGlint", ArtKind.Effect, 1, 1, 3, 1, false).Fx(c => Padded(MuzzleGlint(c.Index), T, T)),
            new ArtEntry("SniperHit", ArtKind.Effect, 1, 1, 4, 1, false).Fx(c => SniperHit(c.Index)),
            new ArtEntry("ArmourSparks", ArtKind.Effect, 1, 1, 4, 1, false).Fx(c => Snapped(ArmourSparks(c.Index))),
            new ArtEntry("IncomingShell", ArtKind.Effect, 1, 2, 2, 1, false).Fx(c => IncomingShell(c.Index)),
            new ArtEntry("LaunchPuff", ArtKind.Effect, 2, 2, 6, 1, false).Fx(c => LaunchPuff(c.T, 5)),
            new ArtEntry("ArtilleryBlast", ArtKind.Effect, 8, 8, 8, 1, false).Fx(c => ShockBlast(8, 3.5f, c.T, 37, dust: true)),
            new ArtEntry("CraterLarge", ArtKind.Effect, 3, 3, DecalFrames, CraterVariants, false).Fx(c => CraterLarge(c.Direction, c.Frame)),
            new ArtEntry("TwinMuzzle", ArtKind.Effect, 1, 1, 2, 1, false).Fx(c => TwinMuzzle(c.Index)),
            new ArtEntry("TracerStream", ArtKind.Effect, 2, 1, 3, 1, false).Fx(c => TracerStream(c.Index)),
            new ArtEntry("DeathSmall", ArtKind.Effect, 2, 2, 7, 1, false).Fx(c => DeathSmall(c.T, 41)),
            new ArtEntry("DeathLarge", ArtKind.Effect, 4, 4, 8, 1, false).Fx(c => DeathLarge(c.T, 43)),
            new ArtEntry("Debris", ArtKind.Effect, 3, 3, 6, 1, false).Fx(c => Debris(c.T, 47)),
            new ArtEntry("DustCloud", ArtKind.Effect, 3, 3, 6, 1, false).Fx(c => DustCloud(2, c.T, 53)),
        };
    }
}
