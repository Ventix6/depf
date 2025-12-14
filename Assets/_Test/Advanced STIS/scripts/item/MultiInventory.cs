using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Container für mehrere Grids inkl. Zugriffs- & Einlege-Restriktionen.
/// - inventoryType:   TakeOnly  -> nur herausnehmen (keine neuen Items einlegen)
///                    InsertOnly-> nur einlegen (nichts herausnehmen)
///                    Normal    -> beides
/// - restrictToDefinition: wenn true, dürfen nur Items mit genau dieser Definition rein
/// 
/// WICHTIG:
/// - Verwende CanInsert(...) / CanTake(...) vor UI-Drops/Entnahmen.
/// - Für Cross-Grid-Transfers: prüfe CanTake(from) & CanInsert(to, item).
/// </summary>
[DisallowMultipleComponent]
public class MultiInventory : MonoBehaviour
{
    public enum Type
    {
        TakeOnly,
        InsertOnly,
        Normal
    }

    [System.Serializable]
    public struct GridSpec
    {
        public string id;
        public Vector2Int size;

        [Header("Access Mode")]
        public Type inventoryType;            // ersetzt altes "takeOnly"

        [Header("Optional Item Restriction")]
        public bool restrictToDefinition;     // wenn true -> nur allowedDefinition zulassen
        public List<ItemDefinition> allowedDefinition;
    }

    [Header("Grids (Authoring)")]
    public List<GridSpec> gridsSpec = new();

    [Header("Loot (optional)")]
    public LootTable lootTable;
    [Tooltip("0 = zufälliger Seed bei erster Generierung")]
    public int lootSeed;
    [Tooltip("Wurde die LootTable bereits angewendet?")]
    public bool lootGenerated;
    
    // id -> runtime grid (nicht serialisiert, zur Laufzeit aus gridsSpec gebaut)
    readonly Dictionary<string, InventoryGrids> grids = new();

    // id -> spec lookup (schneller als jedes Mal zu suchen)
    readonly Dictionary<string, GridSpec> specById = new();

    bool runtimeBuilt;

    void Awake()
    {
        InitializeRuntime();
    }

    /// <summary>
    /// Runtime-Struktur initialisieren (Grids aus Specs bauen).
    /// Sicher mehrfach aufrufbar.
    /// </summary>
    public void InitializeRuntime()
    {
        if (runtimeBuilt) return;
        runtimeBuilt = true;
        BuildRuntimeGrids();
    }

    /// <summary>
    /// Baut die Runtime-Grids aus den Gridspecs neu.
    /// Kann z.B. nach Deserialization / Reset erneut aufgerufen werden.
    /// </summary>
    public void BuildRuntimeGrids()
    {
        specById.Clear();
        grids.Clear();

        foreach (var spec in gridsSpec)
        {
            if (string.IsNullOrWhiteSpace(spec.id))
                continue;

            var size = new Vector2Int(
                Mathf.Max(1, spec.size.x),
                Mathf.Max(1, spec.size.y)
            );

            specById[spec.id] = spec;
            grids[spec.id]    = new InventoryGrids(spec.id, size);
        }
    }

    /// <summary>
    /// Versucht Grid mit gegebener Id zu holen. Kein Throw, nur false bei Fehler.
    /// </summary>
    public bool TryGetGrid(string id, out InventoryGrids grid)
    {
        if (string.IsNullOrEmpty(id))
        {
            grid = null;
            return false;
        }

        return grids.TryGetValue(id, out grid);
    }

    /// <summary>
    /// Hol das Grid oder wirf eine Exception, wenn nicht vorhanden.
    /// Für Editor-/Debug-Code ok, für Seed/Netcode lieber TryGetGrid verwenden.
    /// </summary>
    public InventoryGrids GetGrid(string id)
    {
        if (TryGetGrid(id, out var g))
            return g;

        throw new KeyNotFoundException(
            $"[MultiInventory] Grid '{id}' not found on '{name}'.");
    }

    public IEnumerable<InventoryGrids> AllGrids()
    {
        foreach (var g in grids.Values)
            yield return g;
    }

    /// <summary>
    /// Loot einmalig generieren (z.B. beim ersten Öffnen auf dem Server callen).
    /// </summary>
    [ContextMenu("Ensure Loot Generated")]
    public void EnsureLoot()
    {
        if (lootTable == null || lootGenerated)
            return;

        // Seed wählen (0 = auto)
        int seed = lootSeed != 0
            ? lootSeed
            : (Environment.TickCount ^ GetInstanceID());

        lootTable.Fill(this, seed);
        lootGenerated = true;
    }


    bool TryPlaceItemRandom(InventoryGrids grid, ItemInstance item, System.Random rng)
    {
        if (grid == null || item == null || item.Definition == null)
            return false;

        // Itemgröße bestimmen
        Vector2Int size;
        try
        {
            size = item.GetSize();
        }
        catch
        {
            size = Vector2Int.one;
        }

        // Alle möglichen Top-Left-Positionen sammeln
        var positions = new List<Vector2Int>();
        int maxX = Mathf.Max(0, grid.Size.x - size.x);
        int maxY = Mathf.Max(0, grid.Size.y - size.y);

        for (int y = 0; y <= maxY; y++)
        {
            for (int x = 0; x <= maxX; x++)
            {
                positions.Add(new Vector2Int(x, y));
            }
        }

        if (positions.Count == 0)
            return false;

        // Fisher-Yates Shuffle
        for (int i = positions.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (positions[i], positions[j]) = (positions[j], positions[i]);
        }

        // In zufälliger Reihenfolge versuchen zu platzieren
        foreach (var tl in positions)
        {
            if (grid.CanPlace(item, tl, item.Rotated))
            {
                grid.TryPlaceNew(item, tl, item.Rotated);
                return true;
            }
        }

        return false;
    }

    
    /// <summary> Für alte Callsites: "ReadOnly" im Sinne von "kann nicht einlegen". </summary>
    public bool IsReadOnly(string gridId)
    {
        if (!specById.TryGetValue(gridId, out var s)) return true;
        return s.inventoryType == Type.TakeOnly;
    }

    /// <summary> Darf aus diesem Grid entnommen werden? (TakeOnly/InsertOnly-Logik) </summary>
    public bool CanTake(string gridId)
    {
        if (!specById.TryGetValue(gridId, out var s)) return false;
        return s.inventoryType != Type.InsertOnly;
    }

    /// <summary>
    /// Darf in dieses Grid etwas eingelegt werden? (Access-Typ + Item-Restriktion)
    /// </summary>
    public bool CanInsert(string gridId, ItemInstance item)
    {
        if (!specById.TryGetValue(gridId, out var s)) return false;
        if (s.inventoryType == Type.TakeOnly) return false;

        if (s.restrictToDefinition)
        {
            if (item == null) return false;
            var def = item.Definition;
            if (def == null) return false;

            if (s.allowedDefinition != null)
            {
                for (int i = 0; i < s.allowedDefinition.Count; i++)
                {
                    if (s.allowedDefinition[i] == def)
                        return true;
                }
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Optionaler Komfort-Wrapper: erster passender Platz in irgendeinem Grid dieses Inventars.
    /// Nutzt CanInsert + InventoryGrids.FindFirstFit.
    /// </summary>
    public bool TryAutoStash(ItemInstance item, MultiInventory target,
        out (string gridId, Vector2Int pos, bool rot) placed)
    {
        placed = default;

        if (item == null || target == null)
            return false;

        foreach (var kv in target.grids)
        {
            var gridId = kv.Key;
            var grid   = kv.Value;

            if (!target.CanInsert(gridId, item)) continue;

            if (grid.FindFirstFit(item, out var p, out var r))
            {
                placed = (gridId, p, r);
                return true;
            }
        }

        return false;
    }

    // ---------- Enforcement-Wrapper für Gameplay-Logik ----------

    /// <summary> Sichere Einlage in ein bestimmtes Grid mit Rechte-/Restriktionsprüfung. </summary>
    public bool TryInsert(string gridId, ItemInstance item, Vector2Int pos, bool rot)
    {
        if (item == null) return false;
        if (!CanInsert(gridId, item)) return false;
        if (!TryGetGrid(gridId, out var g)) return false;

        if (!g.CanPlace(item, pos, rot)) return false;
        return g.TryPlaceNew(item, pos, rot);
    }

    /// <summary> Sichere Entnahme aus Grid (z. B. für Looten/Drop auf Boden). </summary>
    public bool TryRemove(string gridId, string guid)
    {
        if (string.IsNullOrEmpty(guid)) return false;
        if (!CanTake(gridId)) return false;
        if (!TryGetGrid(gridId, out var g)) return false;
        return g.Remove(guid);
    }

    /// <summary> Interner Move im selben Grid (Sortieren). Erlauben wir immer. </summary>
    public bool TryMoveWithin(string gridId, string guid, Vector2Int pos, bool rot, out string swappedGuid)
    {
        swappedGuid = null;
        if (string.IsNullOrEmpty(guid)) return false;
        if (!TryGetGrid(gridId, out var g)) return false;
        return g.TryMove(guid, pos, rot, out swappedGuid);
    }

    /// <summary> Transfer von Grid A -> Grid B unter Zugriffskontrolle. </summary>
    public bool TryTransfer(string fromGridId, string toGridId, ItemInstance item, Vector2Int pos, bool rot)
    {
        if (item == null) return false;

        if (!CanTake(fromGridId)) return false;
        if (!CanInsert(toGridId, item)) return false;

        if (!TryGetGrid(fromGridId, out var from)) return false;
        if (!TryGetGrid(toGridId,   out var to))   return false;

        if (!from.Contains(item.Guid)) return false;

        var oldRect = from.GetRect(item.Guid);
        var oldRot  = item.Rotated;

        if (!from.Remove(item.Guid)) return false;

        if (to.CanPlace(item, pos, rot) && to.TryPlaceNew(item, pos, rot))
        {
            item.Rotated = rot;
            return true;
        }

        // Rollback
        item.Rotated = oldRot;
        from.TryPlaceNew(item, oldRect.position, oldRot);
        return false;
    }
}
