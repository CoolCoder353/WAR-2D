using UnityEngine;
using WAR2D.Art;
using WAR2D.Art.Render;

namespace WAR2D.Client.Render
{
    /// <summary>
    /// How a unit's turret turns and where its gun's muzzle is, on screen only: the sim knows nothing of turrets. Angles are in radians,
    /// 0 east and anticlockwise; muzzle positions are in art pixels from the unit's centre (<see cref="ArtStyle.PixelsPerUnit"/> to a tile).
    /// </summary>
    public static class TurretAim
    {
        /// <summary>Rendered turret facings.</summary>
        public const int Facings = ArtManifest.TurretDirections;

        /// <summary>Seconds a turret keeps aiming past when its gun would next fire, before it turns back to the way the hull faces.</summary>
        public const float HoldSeconds = 1f;

        /// <summary>Seconds after a shot the barrel recoils: from when the flash's first frame, which touches the barrel, has played.</summary>
        public const float RecoilStart = 0.1f, RecoilEnd = 0.3f;

        /// <summary>Art pixels along the raised Artillery barrel from its bore to the flash, and the bore's place at its facing: forward from the unit's centre, and up the screen.</summary>
        public const float ArtilleryFlashAhead = 5f, ArtilleryBoreForward = 40f, ArtilleryBoreUp = 17f;

        /// <summary>The barrel's root on the raised Artillery: forward from the unit's centre, and up the screen.</summary>
        private const float ArtilleryRootForward = 8f, ArtilleryRootUp = 1f;

        /// <summary>True for the types whose turret turns on its own: not the Flamethrower, Builder, Digger or Suicide bomber.</summary>
        public static bool Has(UnitType type) => type == UnitType.Tank || type == UnitType.Scout || type == UnitType.Sniper || type == UnitType.Heavy || type == UnitType.Artillery;

        /// <summary>How fast a turret turns, in radians a second: the bigger the gun, the slower it swings.</summary>
        public static float TurnRate(UnitType type) => type switch
        {
            UnitType.Scout => 300f * Mathf.Deg2Rad,
            UnitType.Tank => 180f * Mathf.Deg2Rad,
            UnitType.Sniper => 150f * Mathf.Deg2Rad,
            UnitType.Heavy => 120f * Mathf.Deg2Rad,
            UnitType.Artillery => 90f * Mathf.Deg2Rad,
            _ => 0f,
        };

        /// <summary>The way a turret should point: at what it fired at while it is <paramref name="aiming"/>, else the way the hull faces.</summary>
        public static float Goal(float hull, float aim, bool aiming) => aiming ? aim : hull;

        /// <summary><paramref name="current"/> turned the short way toward <paramref name="goal"/>, by at most <paramref name="maxTurn"/>, stopping on it.</summary>
        public static float Step(float current, float goal, float maxTurn) => current + Mathf.Clamp(Shortest(goal - current), -maxTurn, maxTurn);

        /// <summary>A turn wrapped to half a circle either way.</summary>
        private static float Shortest(float turn) => Mathf.Repeat(turn + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;

        /// <summary>The rendered facing nearest an angle.</summary>
        public static int Facing(float angle) => UnitFrames.Direction(angle, Facings);

        /// <summary>The angle a rendered facing draws: flashes and shots leave the barrel as it is drawn.</summary>
        public static float FacingAngle(int facing) => facing * 2f * Mathf.PI / Facings;

        /// <summary>The angle a hull is drawn at for its rotation (8 facings).</summary>
        public static float HullAngle(float rotation) => UnitFrames.Direction(rotation, ArtManifest.UnitDirections) * 2f * Mathf.PI / ArtManifest.UnitDirections;

        /// <summary>
        /// The turret's pivot from the unit's centre, on the hull's centre line, in whole art pixels so hull and turret snap to the
        /// art's grid together and never swim against each other.
        /// </summary>
        public static Vector2 PivotPixels(UnitType type, float hull)
        {
            float pivot = RenderGen.TurretPivot(type);
            return new Vector2(Mathf.Round(Mathf.Cos(hull) * pivot), Mathf.Round(Mathf.Sin(hull) * pivot));
        }

        /// <summary>How many barrels fire at once.</summary>
        public static int Barrels(UnitType type) => type == UnitType.Heavy ? 2 : 1;

        /// <summary>Art pixels the flash's centre stands from the unit's centre with the turret straight ahead, as the owner tuned them.</summary>
        public static float MuzzleForward(UnitType type) => type switch
        {
            UnitType.Scout => 17f,
            UnitType.Sniper => 33.5f,
            UnitType.Heavy => 60f,
            UnitType.Artillery => ArtilleryBoreForward,
            _ => 34f,
        };

        /// <summary>Art pixels a barrel's flash stands to the turret's left (negative: its right).</summary>
        public static float MuzzleSide(UnitType type, int barrel) => type switch
        {
            UnitType.Scout => -3f,
            UnitType.Heavy => barrel == 0 ? 6f : -6f,
            _ => 0f,
        };

        /// <summary>
        /// Where a barrel's flash stands (not the raised Artillery's: see <see cref="ArtilleryFlash"/>). With the turret straight ahead
        /// that is <see cref="MuzzleForward"/> and <see cref="MuzzleSide"/> along it; turned, it swings about the turret's real pivot.
        /// </summary>
        public static Vector2 Muzzle(UnitType type, float hull, float turret, int barrel)
        {
            float pivot = RenderGen.TurretPivot(type);
            Vector2 along = new Vector2(Mathf.Cos(turret), Mathf.Sin(turret)), left = new Vector2(-along.y, along.x);
            return new Vector2(Mathf.Cos(hull), Mathf.Sin(hull)) * pivot + along * (MuzzleForward(type) - pivot) + left * MuzzleSide(type, barrel);
        }

        /// <summary>The bore of the raised Artillery barrel (where its shell leaves), which stands up the screen because it is raised.</summary>
        public static Vector2 ArtilleryBore(float hull, float turret) =>
            Muzzle(UnitType.Artillery, hull, turret, 0) + Vector2.up * ArtilleryBoreUp;

        /// <summary>The way the raised Artillery barrel points on screen, foreshortened by its height.</summary>
        public static Vector2 ArtilleryBarrelAxis(float turret) =>
            (new Vector2(Mathf.Cos(turret), Mathf.Sin(turret)) * (ArtilleryBoreForward - ArtilleryRootForward) + Vector2.up * (ArtilleryBoreUp - ArtilleryRootUp)).normalized;

        /// <summary>Where the raised Artillery's flash stands: a little along the barrel from its bore.</summary>
        public static Vector2 ArtilleryFlash(float hull, float turret) => ArtilleryBore(hull, turret) + ArtilleryBarrelAxis(turret) * ArtilleryFlashAhead;

        /// <summary>True while a barrel is back in its recoil, <paramref name="sinceShot"/> seconds after a shot.</summary>
        public static bool Recoiling(float sinceShot) => sinceShot >= RecoilStart && sinceShot < RecoilEnd;

        /// <summary>A turret's cell within its facing: frame 1 recoils; the Artillery's three frames are level, raised while it aims, and raised in recoil.</summary>
        public static int TurretFrame(int framesPerFacing, bool aiming, bool recoil) =>
            framesPerFacing >= 3 ? (aiming ? (recoil ? 2 : 1) : 0) : (recoil ? 1 : 0);
    }
}
