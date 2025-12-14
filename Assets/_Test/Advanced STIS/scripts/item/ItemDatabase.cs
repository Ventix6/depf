using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName="D.E.P.T.H./Inventory/Item Database")]
public class ItemDatabases : ScriptableObject
{
    public List<ItemDefinition> items = new();

    Dictionary<string, ItemDefinition> _map;

    void OnValidate() { _map = null; } // neu bauen, wenn du in der Liste etwas änderst

    public Dictionary<string, ItemDefinition> Map
    {
        get
        {
            if (_map == null)
            {
                _map = new Dictionary<string, ItemDefinition>();
                foreach (var d in items)
                {
                    if (d == null) continue;
                    _map[d.Id] = d;
                }
            }
            return _map;
        }
    }

    public ItemDefinition Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        Map.TryGetValue(id, out var def);
        return def;
    }

    public Dictionary<string, ItemDefinition> BuildMap() => new(Map); // für Serializer
}