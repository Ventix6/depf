using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class InventoryLayoutData : MonoBehaviour
{
    [Header("Grid Skin")]
    public Vector2 cellSize    = new(64, 64);
    public Vector2 cellPadding = new(4, 4);

    [Header("Panel Size (für UI-Panel)")]
    public Vector2 panelSize = new(1200, 800);

    [Serializable]
    public struct GridLayout
    {
        public string     gridId;
        public Vector2    posPxTL;
        public bool       overrideSize;
        public Vector2Int sizeOverride;
        public Color      bgColor;
        public float      framePx;
    }

    [Header("Layout")]
    public List<GridLayout> layout = new();
}