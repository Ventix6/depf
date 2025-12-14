using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "D.E.P.T.H./Inventory/Loot dingens")]
public class LootTable : ScriptableObject
{
    [Serializable]
    public struct Entry
    {
        public ItemDefinition def;

        [Tooltip("Gesamtmenge dieses Items, die im Container landen darf (inklusive).")]
        public Vector2Int amountRange;

        [Tooltip("Relative Gewichtung, wenn weniger Einträge gezogen werden als vorhanden.")]
        public float weight;
    }

    [Tooltip("Maximale Anzahl verschiedener Item-Typen aus dieser Tabelle. 0 = alle gültigen Einträge.")]
    public int rolls = 0;

    public List<Entry> entries = new();

    /// <summary>
    /// Füllt ein MultiInventory mit Loot.
    /// - Jeder Entry wird höchstens einmal gewählt.
    /// - amountRange = Gesamtmenge des Items (wird bei Bedarf in mehrere Stacks gesplittet).
    /// - Items werden zufällig über alle Grids verteilt.
    /// - MultiInventory-Access-Typen (TakeOnly/InsertOnly) werden für Spawn IGNORIERT,
    ///   damit auch TakeOnly-Container gefüllt werden können.
    /// </summary>
    public void Fill(MultiInventory inv, int seed)
    {
        if (inv == null || entries == null || entries.Count == 0)
            return;

        // Runtime-Grids aus dem Inventory holen
        var grids = new List<InventoryGrids>();
        foreach (var g in inv.AllGrids())
        {
            if (g != null)
                grids.Add(g);
        }
        if (grids.Count == 0)
            return;

        // nur gültige Einträge
        var validEntries = new List<Entry>();
        foreach (var e in entries)
        {
            if (e.def != null)
                validEntries.Add(e);
        }
        if (validEntries.Count == 0)
            return;

        var rnd = new System.Random(seed);

        // 1) Welche Item-Typen kommen überhaupt rein? → zufällige Auswahl ohne Replacement.
        var picked = PickEntries(validEntries, rolls, rnd);

        // 2) Für jeden gewählten Typ: Gesamtmenge aus amountRange würfeln und verteilen.
        foreach (var e in picked)
        {
            int min = Mathf.Min(e.amountRange.x, e.amountRange.y);
            int max = Mathf.Max(e.amountRange.x, e.amountRange.y);

            if (max <= 0)
                continue;

            int totalAmount = rnd.Next(Mathf.Max(0, min), max + 1);
            if (totalAmount <= 0)
                continue;

            int maxStack = Mathf.Max(1, e.def.MaxStack);

            while (totalAmount > 0)
            {
                int stackAmount = Mathf.Min(totalAmount, maxStack);
                var item = new ItemInstance(e.def, stackAmount);

                // Versuche, diese Stack-Menge irgendwo random im Inventory zu platzieren
                if (!TryPlaceItemRandomAcrossInventory(grids, item, rnd))
                {
                    // Kein Platz mehr → Rest verwerfen
                    break;
                }

                totalAmount -= stackAmount;
            }
        }
    }

    /// <summary>
    /// Wählt bis zu 'rolls' verschiedene Entries ohne Replacement, gewichtet nach Entry.weight.
    /// rolls <= 0 → alle Entries.
    /// </summary>
    static List<Entry> PickEntries(List<Entry> entries, int rolls, System.Random rnd)
    {
        var remaining = new List<Entry>(entries);
        var picked = new List<Entry>();

        int maxPicks = (rolls <= 0) ? remaining.Count : Mathf.Min(rolls, remaining.Count);

        for (int n = 0; n < maxPicks && remaining.Count > 0; n++)
        {
            // Gewichtssumme
            float totalW = 0f;
            for (int i = 0; i < remaining.Count; i++)
                totalW += Mathf.Max(0.0001f, remaining[i].weight);

            double r = rnd.NextDouble() * totalW;
            float accum = 0f;
            int chosenIndex = 0;

            for (int i = 0; i < remaining.Count; i++)
            {
                accum += Mathf.Max(0.0001f, remaining[i].weight);
                if (r <= accum)
                {
                    chosenIndex = i;
                    break;
                }
            }

            picked.Add(remaining[chosenIndex]);
            remaining.RemoveAt(chosenIndex);
        }

        return picked;
    }

    /// <summary>
    /// Versucht, einen Stack irgendwo in einem der Grids zufällig zu platzieren.
    /// Ignoriert MultiInventory-Access-Logik; prüft nur Grid-Kollisionen.
    /// </summary>
    static bool TryPlaceItemRandomAcrossInventory(List<InventoryGrids> grids, ItemInstance item, System.Random rnd)
    {
        if (grids == null || grids.Count == 0 || item == null || item.Definition == null)
            return false;

        // Reihenfolge der Grids randomisieren
        Shuffle(grids, rnd);

        foreach (var g in grids)
        {
            if (TryPlaceItemRandomInGrid(g, item, rnd))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Versucht, einen Stack zufällig in EINEM Grid zu platzieren (random Position + Rotation).
    /// </summary>
    static bool TryPlaceItemRandomInGrid(InventoryGrids g, ItemInstance item, System.Random rnd)
    {
        if (g == null || item == null || item.Definition == null)
            return false;

        Vector2Int baseSize;
        try
        {
            baseSize = item.GetSize();
        }
        catch
        {
            baseSize = Vector2Int.one;
        }

        bool[] rotations;
        if (baseSize.x == baseSize.y)
            rotations = new[] { false };
        else
            rotations = new[] { false, true };

        foreach (bool rot in rotations)
        {
            Vector2Int size = rot ? new Vector2Int(baseSize.y, baseSize.x) : baseSize;

            int maxX = g.Size.x - size.x;
            int maxY = g.Size.y - size.y;
            if (maxX < 0 || maxY < 0)
                continue;

            var positions = new List<Vector2Int>();
            for (int y = 0; y <= maxY; y++)
            {
                for (int x = 0; x <= maxX; x++)
                    positions.Add(new Vector2Int(x, y));
            }

            if (positions.Count == 0)
                continue;

            Shuffle(positions, rnd);

            foreach (var tl in positions)
            {
                if (g.CanPlace(item, tl, rot))
                {
                    item.Rotated = rot;
                    g.TryPlaceNew(item, tl, rot);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Fisher-Yates Shuffle.</summary>
    static void Shuffle<T>(IList<T> list, System.Random rnd)
    {
        int n = list.Count;
        for (int i = n - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
