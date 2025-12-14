using System.Linq;
using UnityEngine;

public class CrateInventory : MonoBehaviour, IInteractable
{
    [Header("Inventory")]
    public MultiInventory crate;
    public string crateId = "crate_01";

    [Header("Loot Respawn")]
    public bool wasOpened = false;

    [Tooltip("Time in seconds after the crate is completely emptied before it can respawn (-1 = never).")]
    public float respawnDelay = -1f;

    private float nextRespawnTime = -1f;
    private bool isInteracting;

    // Merkt sich, welcher Player gerade mit der Kiste interagiert
    private InteractionController _currentUser;

    void Awake()
    {
        if (crate != null)
            crate.InitializeRuntime();
    }

    void Update()
    {
        if (respawnDelay > 0f && nextRespawnTime > 0f && Time.time >= nextRespawnTime)
        {
            ResetLootState();
        }
    }

    // ---------------- Loot logic ----------------

    public void OpenCrate()
    {
        if (crate == null)
            return;

        if (respawnDelay > 0f && nextRespawnTime > 0f && Time.time >= nextRespawnTime)
        {
            ResetLootState();
        }

        if (crate.lootTable != null && !crate.lootGenerated)
        {
            crate.EnsureLoot();
        }

        wasOpened       = true;
        nextRespawnTime = -1f;
    }

    public void CloseCrate()
    {
        if (crate == null)
            return;

        if (respawnDelay > 0f && IsCompletelyEmpty())
        {
            nextRespawnTime = Time.time + respawnDelay;
        }
    }

    public bool IsCompletelyEmpty()
    {
        if (crate == null)
            return true;

        foreach (var g in crate.AllGrids())
        {
            if (g.Items != null && g.Items.Any())
                return false;
        }

        return true;
    }

    void ResetLootState()
    {
        if (crate == null)
            return;

        foreach (var g in crate.AllGrids())
        {
            var current = g.Items.ToList();
            foreach (var it in current)
                g.Remove(it.Guid);
        }

        crate.lootGenerated = false;
        wasOpened           = false;
        nextRespawnTime     = -1f;
    }

    // ---------------- IInteractable implementation ----------------

    public bool IsInteracting()
    {
        return isInteracting;
    }

    public void StartInteraction(InteractionController controller = null)
    {
        if (isInteracting)
            return;

        if (controller == null)
            return;

        // InventoryUIManager des Spielers finden, der interagiert
        var inv = controller.GetComponentInChildren<InventoryUIManager>(true);
        if (inv == null)
            return;

        OpenCrate();
        inv.OpenCrateFromInteraction(this);

        _currentUser  = controller;
        isInteracting = true;
    }

    public void StopInteraction()
    {
        if (!isInteracting)
            return;

        if (_currentUser != null)
        {
            var inv = _currentUser.GetComponentInChildren<InventoryUIManager>(true);
            if (inv != null)
                inv.CloseFromInteractable(this);
        }

        _currentUser  = null;
        isInteracting = false;
    }

    public void OnHoverEnter() { }
    public void OnHoverExit() { }
}
