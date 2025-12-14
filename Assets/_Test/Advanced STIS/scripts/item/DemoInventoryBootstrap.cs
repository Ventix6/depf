using UnityEngine;

public class DemoInventoryBootstrap : MonoBehaviour
{
    public MultiInventory player;
    public MultiInventory crate;
    public ItemDatabases database;
    public LootTable crateLoot;
    public int lootSeed = 12345;

    void Start()
    {
        // Player: ein paar feste Items
        var wrench = new ItemInstance(database.Map["wrench"]);
        var battery = new ItemInstance(database.Map["battery"], 2);

        var backpack = player.GetGrid("backpack");
        backpack.TryPlaceNew(wrench, new Vector2Int(0,0), false);
        if (backpack.FindFirstFit(battery, out var p, out var r))
            backpack.TryPlaceNew(battery, p, r);

        // Crate: Loot generieren (read-only)
        if (crateLoot != null && crate != null)
            crateLoot.Fill(crate, lootSeed);
    }
    
    
}