using UnityEngine;
using UnityEngine.InputSystem;

public class InventoryUIManager : MonoBehaviour
{
    [Header("Ownership")]
    [Tooltip("Optional. Wenn leer, wird im Parent nach Player_Controller gesucht.")]
    [SerializeField] private Player.Player_Controller ownerController;

    [Header("UI Root")]
    public CanvasGroup          rootCanvas;     // full inventory canvas
    public InventoryUIController uiController;  // your existing controller

    [Header("Panel Indices")]
    [Tooltip("Index in uiController.panels for crate panel (left).")]
    public int cratePanelIndex = 0;

    [Tooltip("Index in uiController.panels for player panel (right).")]
    public int playerPanelIndex = 1;

    [Header("Inventories")]
    public MultiInventory playerInventory;

    [Header("Input Actions")]
    public InputActionReference openInventoryAction; // e.g. Tab
    public CrateInventory       debugCrate;          // optional for testing

    [HideInInspector] public bool isOpen;
    CrateInventory currentCrate;

    void Awake()
    {
        if (ownerController == null)
            ownerController = GetComponentInParent<Player.Player_Controller>();

        // Falls der Manager an einem Remote-Player hängt → komplett deaktivieren
        if (ownerController != null && !ownerController.isOwner)
        {
            if (rootCanvas != null)
            {
                rootCanvas.alpha          = 0f;
                rootCanvas.interactable   = false;
                rootCanvas.blocksRaycasts = false;
            }

            enabled = false;
            return;
        }
    }

    void OnEnable()
    {
        if (openInventoryAction != null)
            openInventoryAction.action.Enable();
    }

    void OnDisable()
    {
        if (openInventoryAction != null)
            openInventoryAction.action.Disable();
    }

    void Update()
    {
        if (openInventoryAction != null &&
            openInventoryAction.action.WasPressedThisFrame())
        {
            if (!isOpen)
                OpenPlayerOnly();
            else
                CloseAll();
        }
    }

    // ---------------- Player-only inventory (Tab) ----------------

    public void OpenPlayerOnly()
    {
        currentCrate = null;

        // Disable crate panel
        if (cratePanelIndex >= 0 && cratePanelIndex < uiController.panels.Count)
        {
            var cratePanel = uiController.panels[cratePanelIndex];
            cratePanel.inventory  = null;
            cratePanel.layoutData = null;

            if (cratePanel.parentPanel != null)
                cratePanel.parentPanel.gameObject.SetActive(false);
        }

        // Enable player panel
        if (playerPanelIndex >= 0 && playerPanelIndex < uiController.panels.Count)
        {
            var playerPanel = uiController.panels[playerPanelIndex];
            playerPanel.inventory = playerInventory;

            if (playerPanel.parentPanel != null)
                playerPanel.parentPanel.gameObject.SetActive(true);
        }

        OpenUIRoot();
        uiController.RebuildUI();
    }

    // ---------------- Open crate + player ----------------

    public void OpenCrateFromInteraction(CrateInventory crate)
    {
        if (crate == null || crate.crate == null)
            return;

        crate.OpenCrate();
        currentCrate = crate;

        // Left panel: crate
        if (cratePanelIndex >= 0 && cratePanelIndex < uiController.panels.Count)
        {
            var cratePanel = uiController.panels[cratePanelIndex];
            cratePanel.inventory = crate.crate;

            var layout = crate.crate.GetComponent<InventoryLayoutData>();
            if (layout == null)
                layout = crate.GetComponent<InventoryLayoutData>();

            cratePanel.layoutData = layout;

            if (cratePanel.parentPanel != null)
                cratePanel.parentPanel.gameObject.SetActive(true);
        }

        // Right panel: player inventory
        if (playerPanelIndex >= 0 && playerPanelIndex < uiController.panels.Count)
        {
            var playerPanel = uiController.panels[playerPanelIndex];
            playerPanel.inventory = playerInventory;

            if (playerPanel.parentPanel != null)
                playerPanel.parentPanel.gameObject.SetActive(true);
        }

        OpenUIRoot();
        uiController.RebuildUI();
    }

    // ---------------- Close all ----------------

    public void CloseAll()
    {
        if (!isOpen)
            return;

        if (currentCrate != null)
        {
            currentCrate.CloseCrate();

            if (cratePanelIndex >= 0 && cratePanelIndex < uiController.panels.Count)
            {
                var cratePanel = uiController.panels[cratePanelIndex];
                cratePanel.inventory  = null;
                cratePanel.layoutData = null;

                if (cratePanel.parentPanel != null)
                    cratePanel.parentPanel.gameObject.SetActive(false);
            }

            currentCrate = null;
        }

        uiController.RebuildUI();
        CloseUIRoot();
    }

    public void CloseFromInteractable(CrateInventory crate)
    {
        if (crate == currentCrate)
            CloseAll();
    }

    // ---------------- Canvas helpers ----------------

    void OpenUIRoot()
    {
        if (rootCanvas != null)
        {
            rootCanvas.alpha          = 1f;
            rootCanvas.interactable   = true;
            rootCanvas.blocksRaycasts = true;
        }

        isOpen = true;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible   = true;
    }

    void CloseUIRoot()
    {
        if (rootCanvas != null)
        {
            rootCanvas.alpha          = 0f;
            rootCanvas.interactable   = false;
            rootCanvas.blocksRaycasts = false;
        }

        isOpen = false;
        // Cursor wird vom Player_Controller wieder gelockt.
    }
}
