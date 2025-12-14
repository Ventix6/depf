using System;
using UnityEngine;

[Serializable]
public class ItemInstance
{
    public string Guid;
    public ItemDefinition Definition;
    public int Amount = 1;
    public bool Rotated; // true = 90° gedreht (W/H getauscht)
    // frei für Meta:
    public float Durability = 1f;

    public ItemInstance(ItemDefinition def, int amount = 1)
    {
        Guid = System.Guid.NewGuid().ToString("N");
        Definition = def;
        Amount = Mathf.Clamp(amount, 1, def.MaxStack);
    }

    public Vector2Int GetSize()
    {
        if (Definition == null) return Vector2Int.zero;
        var sz = Definition.BaseSize;
        return Rotated ? new Vector2Int(sz.y, sz.x) : sz;
    }
}