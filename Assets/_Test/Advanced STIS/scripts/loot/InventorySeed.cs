using System;
using System.Collections.Generic;
using UnityEngine;

/// Optionale Startbefüllung. Liegt auf demselben GameObject wie MultiInventory.
/// Wird typischerweise einmal beim Start / beim ersten Öffnen angewendet (Server-seitig).
[DisallowMultipleComponent]
public class InventorySeed : MonoBehaviour
{
    [Serializable]
    public struct SeedItem
    {
        public ItemDefinition def;
        public int amount;
        public Vector2Int pos;
        public bool rotated;
    }

    [Serializable]
    public struct SeedGrid
    {
        public string gridId;
        public List<SeedItem> items;
    }

    [Tooltip("Ziel-MultiInventory für den Seed")]
    public MultiInventory Target;

    public List<SeedGrid> SeedGrids = new();


    private void Start()
    {
        ApplySeedIfNeeded();
    }

    /// <summary>
    /// Wende den Seed auf das Target an.
    /// Achtung: ruft KEIN Clear auf – d.h. es wird additiv eingefüllt.
    /// </summary>
    [ContextMenu("Apply Seed (Playmode)")]
    public void ApplySeed()
    {
        if (!Application.isPlaying || Target == null)
            return;

        Target.InitializeRuntime(); // falls noch nicht gebaut

        foreach (var sg in SeedGrids)
        {
            if (string.IsNullOrWhiteSpace(sg.gridId))
                continue;

            if (!Target.TryGetGrid(sg.gridId, out var grid))
                continue;

            if (sg.items == null) continue;

            foreach (var si in sg.items)
            {
                if (si.def == null || si.amount <= 0)
                    continue;

                var inst = new ItemInstance(si.def, si.amount)
                {
                    Rotated = si.rotated
                };

                // bewusst: wir ignorieren false (z.B. wenn überlappt oder out of bounds)
                grid.TryPlaceNew(inst, si.pos, si.rotated);
            }
        }
    }

    /// <summary>
    /// Convenience zum automatischen Aufruf z.B. im Start eines Container-Skripts.
    /// </summary>
    public void ApplySeedIfNeeded()
    {
        if (Target == null) return;
        ApplySeed();
    }
}
