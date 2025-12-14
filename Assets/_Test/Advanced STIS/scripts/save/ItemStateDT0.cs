using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class ItemStateDTO
{
    public string guid;
    public string defId;
    public int amount;
    public bool rotated;
    public string gridId;
    public Vector2Int pos;
    public float durability;
}

[System.Serializable]
public class InventoryStateDTO
{
    public List<ItemStateDTO> items = new();
}

public static class InventorySerializer
{
    public static InventoryStateDTO Capture(MultiInventory inv)
    {
        var dto = new InventoryStateDTO();
        foreach (var grid in inv.AllGrids())
        {
            foreach (var it in grid.Items)
            {
                var rect = grid.GetRect(it.Guid);
                dto.items.Add(new ItemStateDTO {
                    guid = it.Guid,
                    defId = it.Definition.Id,
                    amount = it.Amount,
                    rotated = it.Rotated,
                    gridId = grid.GridId,
                    pos = rect.position,
                    durability = it.Durability
                });
            }
        }
        return dto;
    }

    public static void Restore(MultiInventory inv, InventoryStateDTO dto, Dictionary<string, ItemDefinition> registry)
    {
        // Clear
        foreach (var gspec in inv.AllGrids())
        {
            var toRemove = new List<string>();
            foreach (var it in gspec.Items) toRemove.Add(it.Guid);
            foreach (var guid in toRemove) gspec.Remove(guid);
        }

        // Place
        foreach (var s in dto.items)
        {
            if (!registry.TryGetValue(s.defId, out var def)) { Debug.LogWarning($"Missing def {s.defId}"); continue; }
            var it = new ItemInstance(def, s.amount){ Rotated = s.rotated, Durability = s.durability };
            var grid = inv.GetGrid(s.gridId);
            if (!grid.TryPlaceNew(it, s.pos, s.rotated))
                Debug.LogWarning($"Restore failed for {s.guid} in grid {s.gridId} at {s.pos}");
        }
    }
}
