using UnityEngine;

public enum ItemType
{
    Generic,
    Weapon,
    Consumable,
    Quest,
    Armor,
    Ammo
}   


[CreateAssetMenu(menuName = "D.E.P.T.H./Inventory/Item Definition")]
public class ItemDefinition : ScriptableObject
{
    [SerializeField] string itemId;
    [SerializeField] string displayName;
    [SerializeField] Vector2Int baseSize = new Vector2Int(1,1);
    [SerializeField] Sprite icon;
    [SerializeField] bool stackable;
    [SerializeField] int maxStack = 1;
    [SerializeField] ItemType itemType = ItemType.Generic;

    public string Id => string.IsNullOrWhiteSpace(itemId) ? name : itemId;
    public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? name : displayName;
    public Vector2Int BaseSize => new(Mathf.Max(1, baseSize.x), Mathf.Max(1, baseSize.y));
    public Sprite Icon => icon;
    public bool Stackable => stackable;
    public int MaxStack => Mathf.Max(1, maxStack);
    public ItemType Type => itemType;
}