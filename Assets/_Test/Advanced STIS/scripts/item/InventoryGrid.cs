using System;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting.FullSerializer;
using UnityEngine;

[System.Serializable]
public struct GridCoord { public int x, y; public GridCoord(int X,int Y){x=X;y=Y;} public static implicit operator Vector2Int(GridCoord c)=>new(c.x,c.y); }

[System.Serializable]
public class InventoryGrids
{
    public string GridId;
    public Vector2Int Size;
    public HashSet<Vector2Int> Blocked = new(); // optionale gesperrte Slots

    // Lookup: Zelle -> ItemGuid
    readonly Dictionary<Vector2Int, string> cellToItem = new();
    // Lookup: ItemGuid -> belegtes Rechteck
    readonly Dictionary<string, RectInt> itemToRect = new();
    // Lookup: Guid -> ItemInstance
    readonly Dictionary<string, ItemInstance> items = new();

    public event Action Changed;
    void Notify() => Changed?.Invoke();
    
    public InventoryGrids(string id, Vector2Int size)
    {
        GridId = id;
        Size = new Vector2Int(Mathf.Max(1,size.x), Mathf.Max(1,size.y));
    }

    public IEnumerable<ItemInstance> Items => items.Values;
    public bool Contains(string guid) => items.ContainsKey(guid);
    public RectInt GetRect(string guid) => itemToRect[guid];
    public ItemInstance GetItem(string guid) => items[guid];

    public bool InBounds(RectInt r)
    {
        if (r.xMin < 0 || r.yMin < 0) return false;
        if (r.xMax > Size.x || r.yMax > Size.y) return false;
        foreach (var b in Blocked)
            if (r.Contains(b)) return false;
        return true;
    }

    bool OverlapsOthers(RectInt r, string ignoreGuid = null)
    {
        for (int x=r.xMin; x<r.xMax; x++)
            for (int y=r.yMin; y<r.yMax; y++)
            {
                var c = new Vector2Int(x,y);
                if (cellToItem.TryGetValue(c, out var g))
                    if (g != ignoreGuid) return true;
            }
        return false;
    }

    public bool CanPlace(ItemInstance item, Vector2Int pos, bool rotated, string ignoreGuid = null)
    {
        var sz = item.Definition.BaseSize;
        if (rotated) sz = new Vector2Int(sz.y, sz.x);
        var r = new RectInt(pos, sz);
        return InBounds(r) && !OverlapsOthers(r, ignoreGuid);
    }

    public bool TryPlaceNew(ItemInstance item, Vector2Int pos, bool rotated)
    {
        if (!CanPlace(item, pos, rotated)) return false;
        item.Rotated = rotated;
        var rect = new RectInt(pos, item.GetSize());
        items[item.Guid] = item;
        itemToRect[item.Guid] = rect;
        for (int x=rect.xMin; x<rect.xMax; x++)
            for (int y=rect.yMin; y<rect.yMax; y++)
                cellToItem[new Vector2Int(x,y)] = item.Guid;
        
        Notify();
        return true;
    }

    public bool Remove(string guid)
    {
        if (!items.ContainsKey(guid)) return false;
        var rect = itemToRect[guid];
        for (int x=rect.xMin; x<rect.xMax; x++)
            for (int y=rect.yMin; y<rect.yMax; y++)
                cellToItem.Remove(new Vector2Int(x,y));
        items.Remove(guid);
        itemToRect.Remove(guid);
        Notify();
        return true;
    }

    public bool TryMove(string guid, Vector2Int newPos, bool newRot, out string swappedGuid)
{
    swappedGuid = null;
    if (!items.TryGetValue(guid, out var it)) return false;

    var sz = it.Definition.BaseSize;
    if (newRot) sz = new Vector2Int(sz.y, sz.x);
    var target = new RectInt(newPos, sz);
    if (!InBounds(target)) return false;

    // Kollisionen zählen
    string blocking = null;
    HashSet<string> blockers = new();
    for (int x=target.xMin; x<target.xMax; x++)
        for (int y=target.yMin; y<target.yMax; y++)
        {
            var c = new Vector2Int(x,y);
            if (cellToItem.TryGetValue(c, out var g) && g != guid)
                blockers.Add(g);
        }
    
    if (blockers.Count >= 1) return false;          // mehrere Items blockieren -> kein Move
    if (blockers.Count == 1) blocking = blockers.First();
    
    // vorhandene Belegung des bewegten Items lösen
    var oldRect = itemToRect[guid];
    for (int x=oldRect.xMin; x<oldRect.xMax; x++)
        for (int y=oldRect.yMin; y<oldRect.yMax; y++)
            cellToItem.Remove(new Vector2Int(x,y));

    // Bei Swap: blockierendes Item temporär räumen
    RectInt? swappedRect = null;
    if (blocking != null)
    {
        swappedGuid = blocking;
        swappedRect = itemToRect[blocking];
        var r = swappedRect.Value;
        for (int x=r.xMin; x<r.xMax; x++)
            for (int y=r.yMin; y<r.yMax; y++)
                cellToItem.Remove(new Vector2Int(x,y));
    }

    // Prüfe erneut, ob Ziel jetzt frei ist
    if (OverlapsOthers(target))
    {
        // rollback bewegtes Item
        for (int x=oldRect.xMin; x<oldRect.xMax; x++)
            for (int y=oldRect.yMin; y<oldRect.yMax; y++)
                cellToItem[new Vector2Int(x,y)] = guid;
        return false;
    }

    // Belege Ziel
    for (int x=target.xMin; x<target.xMax; x++)
        for (int y=target.yMin; y<target.yMax; y++)
            cellToItem[new Vector2Int(x,y)] = guid;
    itemToRect[guid] = target;
    it.Rotated = newRot;

    // Swap zurück in altes Rechteck
    if (swappedRect.HasValue)
    {
        var other = items[swappedGuid];
        var r = swappedRect.Value;

        // Rotation des anderen an Rechteckgröße anpassen
        var baseSz = other.Definition.BaseSize;
        if (baseSz == r.size) other.Rotated = false;
        else if (new Vector2Int(baseSz.y, baseSz.x) == r.size) other.Rotated = true;
        else
        {
            // passt nicht -> Rollback alles
            Remove(guid);
            for (int x=oldRect.xMin; x<oldRect.xMax; x++)
                for (int y=oldRect.yMin; y<oldRect.yMax; y++)
                    cellToItem[new Vector2Int(x,y)] = guid;
            itemToRect[guid] = oldRect;
            it.Rotated = (oldRect.size == baseSz ? false : true);
            return false;
        }

        for (int x=r.xMin; x<r.xMax; x++)
            for (int y=r.yMin; y<r.yMax; y++)
                cellToItem[new Vector2Int(x,y)] = swappedGuid;
        itemToRect[swappedGuid] = r;
    }
    Notify();
    return true;
}


    public bool FindFirstFit(ItemInstance item, out Vector2Int pos, out bool rot)
    {
        var a = item.Definition.BaseSize;
        var b = new Vector2Int(a.y, a.x);

        // Simple First-Fit
        foreach (var test in new []{ (a,false), (b,true) })
        {
            for (int y=0; y<=Size.y - test.Item1.y; y++)
            for (int x=0; x<=Size.x - test.Item1.x; x++)
            {
                var r = new RectInt(new Vector2Int(x,y), test.Item1);
                if (InBounds(r) && !OverlapsOthers(r))
                { pos = new Vector2Int(x,y);
                    rot = test.Item2;
                    Notify();
                    return true; 
                }
            }
        }
        pos = default; rot = false; return false;
    }
}
