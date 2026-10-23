using Mirror;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Client building placement: a preview follows the pointer (snapped to the footprint), turns red where
/// the server says it can't go, R rotates, left-click places and right-click cancels. Started by the
/// command card's build buttons; during HQ placement it starts on its own with the HQ.
/// </summary>
public class BuildingPlacement : MonoBehaviour
{
    public static BuildingPlacement Instance { get; private set; }

    public GameObject previewBuilding;

    /// <summary>The building being placed (None when not placing).</summary>
    public BuildingType PlacingType { get; private set; } = BuildingType.None;

    /// <summary>True while a preview follows the pointer.</summary>
    public bool IsPlacing => PlacingType != BuildingType.None;

    /// <summary>The server's answer for the spot under the cursor: null until it arrives (the cursor and tint use it).</summary>
    public bool? PreviewValid { get; private set; }

    /// <summary>The last frame a placement was in progress: that frame's clicks belong to placement, not selection.</summary>
    public int ActiveFrame { get; private set; } = -1;

    private int2 currentAnchor;
    private float currentRotation; // degrees: 0, 90, 180, 270
    private int2 displayedAnchor;
    private BuildingType displayedType;
    private float displayedRotation;
    private bool hasDisplayedTarget;
    private SpriteRenderer statusOutline;
    private SpriteRenderer previewHead; // the Gun turret's head over its base (v0.13)
    private WAR2D.Client.ClearingRing rangeRing; // the Gun turret's range or the Jammer's jam radius while placing (v0.13)

    private int2 lastQueriedAnchor = new int2(int.MinValue, int.MinValue);
    private float lastQueriedRotation = float.MinValue;
    private float queryTimer;
    private ClientPlayer localPlayer;

    private void Awake()
    {
        if (Instance == null) Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (localPlayer != null) localPlayer.onResponseFromCanBuildBuilding.RemoveListener(ResultFromCommand);
        WAR2D.Client.ClearingRing.Hide();
        if (rangeRing != null) Destroy(rangeRing.gameObject);
    }

    [ClientCallback]
    private void Start()
    {
        localPlayer = NetworkClient.localPlayer != null ? NetworkClient.localPlayer.GetComponent<ClientPlayer>() : null;
        if (localPlayer != null) localPlayer.onResponseFromCanBuildBuilding.AddListener(ResultFromCommand);
    }

    /// <summary>Starts placing a building (replacing any placement in progress).</summary>
    [Client]
    public void BeginPlacement(BuildingType type)
    {
        if (type == BuildingType.None) return;
        UnitCommander.Instance?.CancelWallDrag();
        PlacingType = type;
        SpriteRenderer sprite = previewBuilding.GetComponent<SpriteRenderer>();
        sprite.sprite = WAR2D.Client.Render.TeamSpriteMaterial.Sprite(type);
        sprite.sharedMaterial = WAR2D.Client.Render.TeamSpriteMaterial.ForColour(PlayerPalette.Of(localPlayer != null ? localPlayer.colourIndex : -1), false);
        sprite.sortingOrder = WAR2D.Client.Render.SortingOrders.PlacementPreview;
        EnsureStatusOutline(sprite);
        ShowHead(sprite);
        previewBuilding.SetActive(true);
        currentRotation = 0f;
        hasDisplayedTarget = false;
        SetPreviewTint(Color.white, false);
        lastQueriedAnchor = new int2(int.MinValue, int.MinValue);
        lastQueriedRotation = float.MinValue; // Sentinels guarantee the next change check differs.
        queryTimer = 1f; // Mature timer, so the first frame asks immediately.
    }

    /// <summary>Stops placing without building.</summary>
    [Client]
    public void Cancel()
    {
        PlacingType = BuildingType.None;
        previewBuilding.SetActive(false);
        previewBuilding.GetComponent<SpriteRenderer>().sprite = null;
        if (previewHead != null) previewHead.enabled = false;
        if (rangeRing != null) rangeRing.gameObject.SetActive(false);
        currentRotation = 0f;
    }

    /// <summary>The Gun turret's preview draws its head over the base, facing east (v0.13); other buildings have none.</summary>
    private void ShowHead(SpriteRenderer sprite)
    {
        bool turret = PlacingType == BuildingType.GunTurret;
        if (previewHead == null)
        {
            if (!turret) return;
            GameObject head = new GameObject("PreviewHead");
            head.transform.SetParent(previewBuilding.transform, false);
            previewHead = head.AddComponent<SpriteRenderer>();
        }
        previewHead.enabled = turret;
        if (!turret) return;
        previewHead.sprite = WAR2D.Art.ArtAtlas.Load(WAR2D.Art.ArtAtlas.Buildings)?.Sprite(WAR2D.Art.ArtManifest.TurretHead, 0);
        previewHead.sharedMaterial = sprite.sharedMaterial;
        previewHead.sortingLayerID = sprite.sortingLayerID;
        previewHead.sortingOrder = sprite.sortingOrder + 1;
    }

    /// <summary>While a Gun turret or Jammer is placed, its range ring follows the preview (Figma: Range rings – v0.13).</summary>
    private void ShowRangeRing(float2 centre)
    {
        float radius = PlacingType == BuildingType.GunTurret || PlacingType == BuildingType.Jammer ? WAR2D.Client.DefenceView.RingRadius(PlacingType) : 0f;
        if (radius <= 0f)
        {
            if (rangeRing != null) rangeRing.gameObject.SetActive(false);
            return;
        }
        if (rangeRing == null) rangeRing = WAR2D.Client.ClearingRing.Create("PlacementRange", WAR2D.Client.Render.SortingOrders.ClearingRing);
        rangeRing.Set(centre, radius, PlayerPalette.Of(localPlayer != null ? localPlayer.colourIndex : -1));
    }

    private readonly System.Collections.Generic.List<WAR2D.Client.ClearingRing> optionRings = new System.Collections.Generic.List<WAR2D.Client.ClearingRing>();

    /// <summary>While the HQ is still to be placed, rings each start option dealt to the local player (and their teammates' options in the teammates' colours, a teammate's pick solid).</summary>
    private void ShowOwnClearing(bool needHQ)
    {
        WorldStateManager wsm = WorldStateManager.Instance;
        int2[] sites = wsm != null && wsm.Map != null ? wsm.Map.HqSites : null;
        int shown = 0;
        if (needHQ && sites != null)
        {
            foreach (int option in localPlayer.OwnStartOptions)
                shown += ShowRing(sites, option, shown, PlayerPalette.Of(localPlayer.colourIndex));
            foreach (var teammate in localPlayer.TeammateStarts)
            {
                int colour = NetworkClient.spawned.TryGetValue((uint)teammate.Key, out NetworkIdentity id) && id.TryGetComponent(out ClientPlayer other) ? other.colourIndex : localPlayer.colourIndex;
                foreach (int option in teammate.Value.options)
                    shown += ShowRing(sites, option, shown, PlayerPalette.Of(colour), option == teammate.Value.pick);
            }
        }
        for (int i = shown; i < optionRings.Count; i++) if (optionRings[i] != null) optionRings[i].gameObject.SetActive(false);
    }

    private int ShowRing(int2[] sites, int option, int slot, Color32 colour, bool solid = false)
    {
        if (option < 0 || option >= sites.Length) return 0;
        while (optionRings.Count <= slot) optionRings.Add(null);
        if (optionRings[slot] == null) optionRings[slot] = WAR2D.Client.ClearingRing.Create("StartOptionRing", WAR2D.Client.Render.SortingOrders.ClearingRing);
        optionRings[slot].Set((float2)sites[option] + 0.5f, WAR2D.World.MapGenerator.HqClearRadius, colour, solid);
        return 1;
    }

    [ClientCallback]
    private void Update()
    {
        GameCore core = GameCore.Instance;
        if (core == null || localPlayer == null) return;

        // HQ placement: place the HQ until it is placed, and nothing else.
        bool needHQ = core.CurrentState == GameState.PlacingHQ && !localPlayer.hasPlacedHQ;
        if (needHQ && PlacingType != BuildingType.Base) BeginPlacement(BuildingType.Base);
        if (!needHQ && PlacingType == BuildingType.Base) Cancel();
        ShowOwnClearing(needHQ);
        if (!IsPlacing) return;
        ActiveFrame = Time.frameCount;

        int2 size = WorldStateManager.GetBuildingSize(PlacingType);
        Vector3 mouse = UnitCommander.GetMouseWorldPosition();
        currentAnchor = Footprint.SnapAnchor(new float2(mouse.x, mouse.y), size);
        float2 centre = Footprint.VisualCenter(currentAnchor, size);

        // Rotate turns only buildings drawn in several facings (the Miner); the sprite stays upright and
        // shows the facing, since the 3/4 view only reads one way up.
        if (GameInput.Rotate.WasPressedThisFrame() && WAR2D.Client.Render.TeamSpriteMaterial.Facing(PlacingType, 90f) != 0)
        {
            currentRotation = (currentRotation + 90f) % 360f;
            ShowFacing();
        }
        previewBuilding.transform.position = new Vector3(centre.x, centre.y, 0);
        ShowRangeRing(centre);

        if (!hasDisplayedTarget || math.any(currentAnchor != displayedAnchor) || PlacingType != displayedType || currentRotation != displayedRotation)
        {
            SetPreviewTint(Color.white, false);
            displayedAnchor = currentAnchor;
            displayedType = PlacingType;
            displayedRotation = currentRotation;
            hasDisplayedTarget = true;
        }
        QueryPlacement();

        if (GameInput.Command.WasPressedThisFrame() && PlacingType != BuildingType.Base)
        {
            Cancel();
            return;
        }
        if (GameInput.Select.WasPressedThisFrame() && !GameInput.PointerOverUI)
        {
            WorldStateManager.Instance?.TryAddBuilding(currentAnchor, PlacingType, currentRotation);
            WAR2D.Audio.AudioService.Ui("building_placed");
            // Selected Builders go and build it (Shift queues it behind their current job).
            if (PlacingType != BuildingType.Base) UnitCommander.Instance?.OrderBuilders(OrderKind.Build, currentAnchor, GameInput.QueueModifier.IsPressed());
            // Holding the queue modifier (Shift) keeps placing the same type, as it queues unit orders.
            if (PlacingType != BuildingType.Base && GameInput.QueueModifier.IsPressed())
            {
                hasDisplayedTarget = false;
                lastQueriedAnchor = new int2(int.MinValue, int.MinValue); // re-ask: this spot is now taken
                return;
            }
            Cancel();
        }
    }

    /// <summary>
    /// Asks the server whether the preview spot is valid. Change-gated and throttled: 10 Hz keeps an
    /// honest client below the server's 15/s refill, so cursor-speed motion can never trip the rate limit.
    /// </summary>
    [Client]
    private void QueryPlacement()
    {
        queryTimer += Time.unscaledDeltaTime;
        if ((math.any(currentAnchor != lastQueriedAnchor) || currentRotation != lastQueriedRotation) && queryTimer >= 0.1f && NetworkClient.ready && WorldStateManager.Instance != null)
        {
            WorldStateManager.Instance.CanBuildBuildingCommand(currentAnchor, PlacingType, currentRotation);
            lastQueriedAnchor = currentAnchor;
            lastQueriedRotation = currentRotation;
            queryTimer = 0f;
        }
    }

    [Client]
    public void ResultFromCommand(bool result)
    {
        if (!IsPlacing || math.any(localPlayer.CanBuildResponsePosition != currentAnchor) ||
            localPlayer.CanBuildResponseType != PlacingType || localPlayer.CanBuildResponseRotation != currentRotation) return;

        PreviewValid = result;
        SetPreviewTint(result ? Color.green : Color.red, true);
    }

    /// <summary>Shows the preview (and its status outline) in the facing of the current rotation.</summary>
    private void ShowFacing()
    {
        SpriteRenderer sprite = previewBuilding.GetComponent<SpriteRenderer>();
        sprite.sprite = WAR2D.Client.Render.TeamSpriteMaterial.FacingSprite(PlacingType,
            WAR2D.Client.Render.TeamSpriteMaterial.Facing(PlacingType, currentRotation));
        if (statusOutline != null) statusOutline.sprite = sprite.sprite;
    }

    private void EnsureStatusOutline(SpriteRenderer sprite)
    {
        if (statusOutline == null)
        {
            GameObject outline = new GameObject("PlacementStatusOutline");
            outline.transform.SetParent(previewBuilding.transform, false);
            outline.transform.localScale = Vector3.one * 1.1f;
            statusOutline = outline.AddComponent<SpriteRenderer>();
        }
        statusOutline.sprite = sprite.sprite;
        statusOutline.sortingLayerID = sprite.sortingLayerID;
        statusOutline.sortingOrder = sprite.sortingOrder - 1;
        statusOutline.enabled = false;
    }

    private void SetPreviewTint(Color tint, bool showStatus)
    {
        if (!showStatus) PreviewValid = null;
        SpriteRenderer sprite = previewBuilding.GetComponent<SpriteRenderer>();
        sprite.color = showStatus ? new Color(tint.r, tint.g, tint.b, 0.85f) : new Color(1f, 1f, 1f, 0.75f);
        if (previewHead != null) previewHead.color = sprite.color;
        if (statusOutline == null) return;
        statusOutline.color = tint;
        statusOutline.enabled = showStatus;
    }
}
