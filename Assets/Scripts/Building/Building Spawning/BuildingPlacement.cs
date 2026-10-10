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

    /// <summary>The last frame a placement was in progress: that frame's clicks belong to placement, not selection.</summary>
    public int ActiveFrame { get; private set; } = -1;

    private int2 currentAnchor;
    private float currentRotation; // degrees: 0, 90, 180, 270

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
        PlacingType = type;
        previewBuilding.GetComponent<SpriteRenderer>().sprite = Resources.Load<Sprite>(type.ToString());
        previewBuilding.SetActive(true);
        currentRotation = 0f;
        previewBuilding.transform.rotation = Quaternion.identity;
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
        currentRotation = 0f;
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
        if (!IsPlacing) return;
        ActiveFrame = Time.frameCount;

        int2 size = WorldStateManager.GetBuildingSize(PlacingType);
        Vector3 mouse = UnitCommander.GetMouseWorldPosition();
        currentAnchor = Footprint.SnapAnchor(new float2(mouse.x, mouse.y), size);
        float2 centre = Footprint.VisualCenter(currentAnchor, size);
        previewBuilding.transform.position = new Vector3(centre.x, centre.y, 0);

        if (GameInput.Rotate.WasPressedThisFrame())
        {
            currentRotation = (currentRotation + 90f) % 360f;
            previewBuilding.transform.rotation = Quaternion.Euler(0, 0, currentRotation);
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
        previewBuilding.GetComponent<SpriteRenderer>().color = result ? new Color(0.8f, 0.8f, 0.8f, 0.75f) : new Color(1, 0, 0, 0.75f);
    }
}
