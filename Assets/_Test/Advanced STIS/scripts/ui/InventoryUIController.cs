using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class InventoryUIController : MonoBehaviour
{
    [Header("Canvas + Drag Layer")] 
    public RectTransform canvas;

    [Header("Bindings (pro Seite/Panel)")]
    public List<PanelBinding> panels = new();

    [Header("Skin (Zell-Size/Abstand + Prefabs)")]
    public GridSkin skin;

    [Header("Input Actions (UI-Map)")]
    public InputActionReference pointAction;
    public InputActionReference clickAction;
    public InputActionReference rightClickAction;
    public InputActionReference cancelAction;

    [Header("Panel Auto Size")]
    [Tooltip("Wenn true, wird die Panelgröße aus dem Layout und den Grids berechnet.")]
    public bool autoPanelSize = true;

    [Tooltip("Zusätzliches Padding um alle Grids herum (links/rechts = x, oben/unten = y).")]
    public Vector2 panelOuterPadding = new Vector2(8, 8);

    [Header("Restrictions / Debug")]
    [Tooltip("Wenn true, ignoriert der UI-Controller CanTake/CanInsert/IsReadOnly.\nGut zum Debuggen, ob MultiInventory die Bewegung blockiert.")]
    public bool ignoreInventoryRestrictions = true;

    [System.Serializable]
    public class PanelBinding
    {
        [Tooltip("Label rein für Debugging / Namensgebung der Instanzen.")]
        public string label;

        [Tooltip("MultiInventory-Root (muss auch InventoryLayoutData enthalten oder Legacy-Layout benutzen).")]
        public MultiInventory inventory;

        [Tooltip("Panel im Canvas, in das die Grids gebaut werden (z. B. Left/Right).")]
        public RectTransform parentPanel;

        [Tooltip("LayoutData vom Root. Wenn leer, wird versucht, sie per GetComponent an 'inventory' zu holen.")]
        public InventoryLayoutData layoutData;

        [System.Serializable]
        public class GridPlacement
        {
            public string   gridId;
            public Vector2  posPxTL;
            public bool     overrideSize;
            public Vector2Int sizeOverride;
            public Color    bgColor = new(0.16f, 0.16f, 0.18f, 1f);
            public float    framePx = 1f;
        }

        [Tooltip("ALT: Layout-Liste für den alten Layout-Designer. Wenn kein InventoryLayoutData vorhanden ist, wird diese Liste verwendet.")]
        public List<GridPlacement> layout = new();
    }

    [System.Serializable]
    public class GridSkin
    {
        public Vector2 cellSize    = new(96, 96);
        public Vector2 cellPadding = new(4, 4);
        public Image   cellPrefab;
        public Image   itemPrefab;
        public Image   ghostPrefab;
    }

    class GridView
    {
        public string id;
        public MultiInventory inv;
        public InventoryGrids grid;
        public RectTransform root;
        public Image ghost;
        public readonly Dictionary<string, RectTransform> itemViews = new();
    }

    bool isReady;
    readonly List<GridView> views = new();
    RectTransform dragLayer, dragVisual;
    Image dragImage;
    Vector2 pointerPos, lastScreenPos, dragTargetLocal;
    ItemInstance dragging;
    GridView dragView, hoverView;
    RectTransform hiddenItemView;
    bool dragRot;
    float dragFollowSpeed = 25f;

    // Quick-Stash: erstes Inventory in panels = Primary (z. B. Player)
    MultiInventory primaryInventory;

    // ---------- Lifecycle ----------

    void OnEnable()
    {
        isReady = false;

        pointAction?.action.Enable();
        clickAction?.action.Enable();
        rightClickAction?.action.Enable();
        cancelAction?.action.Enable();

        dragging = null;
        dragView = null;
        hoverView = null;
        hiddenItemView = null;
        StopDragVisual();
    }

    void Start()
    {
        Build();
        RedrawAll();
        Canvas.ForceUpdateCanvases();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        isReady = true;
    }

    void OnDisable()
    {
        isReady = false;
        CleanupDragUI();
        cancelAction?.action.Disable();
        rightClickAction?.action.Disable();
        clickAction?.action.Disable();
        pointAction?.action.Disable();
    }

    void Update()
    {
        if (!isReady) return;

        if (pointAction != null && pointAction.action.enabled)
        {
            var p = pointAction.action.ReadValue<Vector2>();
            if (p != pointerPos)
            {
                pointerPos = p;
                if (dragging != null && dragging.Definition != null) OnHover(pointerPos);
            }
        }

        if (clickAction != null && clickAction.action.enabled)
        {
            if (clickAction.action.WasPressedThisFrame())  TryHandleClick(pointerPos, false);
            if (clickAction.action.WasReleasedThisFrame()) TryHandleRelease(pointerPos, false);
        }

        if (rightClickAction != null && rightClickAction.action.enabled)
        {
            if (rightClickAction.action.WasPressedThisFrame())  TryHandleClick(pointerPos, true);
            if (rightClickAction.action.WasReleasedThisFrame()) TryHandleRelease(pointerPos, true);
        }

        if (cancelAction != null && cancelAction.action.enabled &&
            cancelAction.action.WasPressedThisFrame())
        {
            CleanupDragUI();
        }

        if (dragging != null && dragging.Definition != null &&
            Keyboard.current?.rKey.wasPressedThisFrame == true)
        {
            dragRot = !dragRot;
            dragging.Rotated = dragRot;
            if (hoverView != null) OnHover(pointerPos);
        }
    }

    // ---------- Build ----------

    void Build()
    {
        foreach (var pb in panels)
        {
            if (pb == null || pb.parentPanel == null) continue;

            for (int i = pb.parentPanel.childCount - 1; i >= 0; i--)
                DestroyImmediate(pb.parentPanel.GetChild(i).gameObject);

            pb.parentPanel.anchorMin = pb.parentPanel.anchorMax = new Vector2(0f, 1f);
            pb.parentPanel.pivot = new Vector2(0f, 1f);
        }

        views.Clear();
        primaryInventory = null;

        foreach (var pb in panels)
        {
            if (pb == null || pb.inventory == null || pb.parentPanel == null) continue;

            // erstes Inventory als "Primary"
            if (primaryInventory == null)
                primaryInventory = pb.inventory;

            pb.inventory.InitializeRuntime();

            var layoutData = pb.layoutData;
            if (layoutData == null)
                layoutData = pb.inventory.GetComponent<InventoryLayoutData>();

            if (layoutData != null)
            {
                BuildPanelFromLayoutData(pb, layoutData);
            }
            else
            {
                BuildPanelFromLegacyLayout(pb);
            }
        }

        if (dragLayer == null)
        {
            var dragGo = new GameObject("DragLayer", typeof(RectTransform));
            dragLayer = dragGo.GetComponent<RectTransform>();
            dragLayer.SetParent(canvas, false);
            dragLayer.anchorMin = dragLayer.anchorMax = new Vector2(0.5f, 0.5f);
            dragLayer.pivot = new Vector2(0.5f, 0.5f);
            dragLayer.sizeDelta = Vector2.zero;
            dragLayer.SetAsLastSibling();
        }
    }

    void BuildPanelFromLayoutData(PanelBinding pb, InventoryLayoutData layoutData)
    {
        if (layoutData == null) return;

        skin.cellSize    = layoutData.cellSize;
        skin.cellPadding = layoutData.cellPadding;

        var byId = layoutData.layout.ToDictionary(x => x.gridId, x => x);

        Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 max = new Vector2(float.MinValue, float.MinValue);
        bool any = false;

        foreach (var spec in pb.inventory.gridsSpec)
        {
            if (!byId.TryGetValue(spec.id, out var pl)) continue;

            any = true;
            var cellCount = pl.overrideSize ? pl.sizeOverride : spec.size;
            var sizePx    = CalcGridPixelSize(cellCount);

            var tl = pl.posPxTL;
            var br = tl + sizePx;

            if (tl.x < min.x) min.x = tl.x;
            if (tl.y < min.y) min.y = tl.y;
            if (br.x > max.x) max.x = br.x;
            if (br.y > max.y) max.y = br.y;
        }

        Vector2 usedPanelSize;
        Vector2 offsetBase = Vector2.zero;

        if (autoPanelSize && any)
        {
            usedPanelSize = new Vector2(
                (max.x - min.x) + panelOuterPadding.x * 2f,
                (max.y - min.y) + panelOuterPadding.y * 2f
            );

            offsetBase = new Vector2(
                panelOuterPadding.x - min.x,
                panelOuterPadding.y - min.y
            );
        }
        else
        {
            usedPanelSize = layoutData.panelSize;
            offsetBase = Vector2.zero;
        }

        if (pb.parentPanel != null)
        {
            pb.parentPanel.sizeDelta = usedPanelSize;
        }

        foreach (var spec in pb.inventory.gridsSpec)
        {
            if (!byId.TryGetValue(spec.id, out var pl)) continue;

            var cellCount = pl.overrideSize ? pl.sizeOverride : spec.size;
            var sizePx    = CalcGridPixelSize(cellCount);

            var go = new GameObject($"{pb.label}_{spec.id}", typeof(RectTransform), typeof(Image));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(pb.parentPanel, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);

            var localTL = pl.posPxTL + offsetBase;
            rt.sizeDelta = sizePx;
            rt.anchoredPosition = new Vector2(
                Mathf.Round(localTL.x),
                -Mathf.Round(localTL.y)
            );

            var bg = go.GetComponent<Image>();
            bg.color = pl.bgColor;
            if (pl.framePx > 0f) AddFrame(rt, new Color(1, 1, 1, 0.9f), pl.framePx);
            bg.raycastTarget = false;

            var view = new GridView
            {
                id   = spec.id,
                inv  = pb.inventory,
                grid = pb.inventory.GetGrid(spec.id),
                root = rt
            };

            view.grid.Changed += () =>
            {
                if (this && isActiveAndEnabled) RedrawGrid(view);
            };

            for (int y = 0; y < cellCount.y; y++)
            for (int x = 0; x < cellCount.x; x++)
            {
                var cell = Instantiate(skin.cellPrefab, rt);
                cell.raycastTarget = false;
                PositionRect(cell.rectTransform, x, y, cellCount);
            }

            view.ghost = Instantiate(skin.ghostPrefab, rt);
            view.ghost.gameObject.SetActive(false);

            views.Add(view);
            RedrawGrid(view);
        }
    }

    void BuildPanelFromLegacyLayout(PanelBinding pb)
    {
        if (pb.layout == null || pb.layout.Count == 0) return;

        var byId = pb.layout.ToDictionary(x => x.gridId, x => x);

        foreach (var spec in pb.inventory.gridsSpec)
        {
            if (!byId.TryGetValue(spec.id, out var pl)) continue;

            var cellCount = pl.overrideSize ? pl.sizeOverride : spec.size;
            var sizePx    = CalcGridPixelSize(cellCount);

            var go = new GameObject($"{pb.label}_{spec.id}", typeof(RectTransform), typeof(Image));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(pb.parentPanel, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = sizePx;
            rt.anchoredPosition = new Vector2(Mathf.Round(pl.posPxTL.x), -Mathf.Round(pl.posPxTL.y));

            var bg = go.GetComponent<Image>();
            bg.color = pl.bgColor;
            if (pl.framePx > 0f) AddFrame(rt, new Color(1, 1, 1, 0.9f), pl.framePx);
            bg.raycastTarget = false;

            var view = new GridView
            {
                id   = spec.id,
                inv  = pb.inventory,
                grid = pb.inventory.GetGrid(spec.id),
                root = rt
            };

            view.grid.Changed += () =>
            {
                if (this && isActiveAndEnabled) RedrawGrid(view);
            };

            for (int y = 0; y < cellCount.y; y++)
            for (int x = 0; x < cellCount.x; x++)
            {
                var cell = Instantiate(skin.cellPrefab, rt);
                cell.raycastTarget = false;
                PositionRect(cell.rectTransform, x, y, cellCount);
            }

            view.ghost = Instantiate(skin.ghostPrefab, rt);
            view.ghost.gameObject.SetActive(false);

            views.Add(view);
            RedrawGrid(view);
        }
    }

    // ---------- Draw ----------

    void RedrawAll()
    {
        foreach (var v in views) RedrawGrid(v);
    }

    void RedrawGrid(GridView v)
    {
        foreach (var rt in v.itemViews.Values)
            if (rt) Destroy(rt.gameObject);
        v.itemViews.Clear();

        foreach (var it in v.grid.Items)
        {
            if (v.root != null)
            {
               var img = Instantiate(skin.itemPrefab, v.root);
                
                img.sprite = it.Definition.Icon;
                var rect = v.grid.GetRect(it.Guid);
                PositionRect(img.rectTransform, rect.x, rect.y, v.grid.Size, rect.size);
                v.itemViews[it.Guid] = img.rectTransform; 
            }
            
        }
    }

    
    // ---------- Geometry ----------

    Vector2 CalcGridPixelSize(Vector2Int gridSize)
    {
        var cs = skin.cellSize;
        var pad = skin.cellPadding;
        float w = gridSize.x * cs.x + Mathf.Max(0, gridSize.x - 1) * pad.x;
        float h = gridSize.y * cs.y + Mathf.Max(0, gridSize.y - 1) * pad.y;
        return new Vector2(w, h);
    }

    void PositionRect(RectTransform rt, int x, int y, Vector2Int gridSize, Vector2Int size = default)
    {
        if (size == default) size = Vector2Int.one;
        var cs = skin.cellSize;
        var pad = skin.cellPadding;
        var pos = new Vector2(x * (cs.x + pad.x), -y * (cs.y + pad.y));
        var sz = new Vector2(size.x * cs.x + (size.x - 1) * pad.x, size.y * cs.y + (size.y - 1) * pad.y);
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.sizeDelta = sz;
        rt.anchoredPosition = pos;
    }

    static Camera CamFor(RectTransform rt)
    {
        var c = rt.GetComponentInParent<Canvas>();
        if (!c || c.renderMode == RenderMode.ScreenSpaceOverlay) return null;
        return c.worldCamera ? c.worldCamera : Camera.main;
    }

    // ---------- Input → Drag / Quick-Stash ----------

    void TryHandleClick(Vector2 pos, bool rightClick)
    {
        if (dragging != null) return;

        lastScreenPos = pos;
        hoverView = null;
        dragView = null;

        if (!TryFindGridUnderMouse(pos, out var view)) return;

        if (!ignoreInventoryRestrictions && view.inv != null && !view.inv.CanTake(view.id))
            return;

        var cell = ScreenToCell(view, pos);
        ItemInstance picked = null;

        foreach (var it in view.grid.Items)
        {
            var r = view.grid.GetRect(it.Guid);
            if (r.Contains(cell))
            {
                picked = it;
                break;
            }
        }

        if (picked == null) return;

        bool shift = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;

        // QUICK STASH: Shift + LMB
        if (shift && !rightClick)
        {
            if (QuickStash(picked, view))
                return; // erfolgreich gestasht
            // wenn kein Ziel → normaler Drag
        }

        dragging = picked;
        dragView = view;
        dragRot  = picked.Rotated;
        OnBeginDrag(view, pos);
    }

    void TryHandleRelease(Vector2 pos, bool rightClick)
    {
        if (dragging == null)
        {
            CleanupDragUI();
            return;
        }

        if (!TryFindGridUnderMouse(pos, out var hv))
        {
            CleanupDragUI();
            return;
        }

        var dv = dragView;
        if (hv == null || hv.root == null || hv.grid == null)
        {
            CleanupDragUI();
            return;
        }

        var size = SafeGetSize(dragging);

        if (!ignoreInventoryRestrictions && hv.inv != null && !hv.inv.CanInsert(hv.id, dragging))
        {
            CleanupDragUI();
            return;
        }

        if (!FindNearestValidTopLeft(hv, pos, size, hv == dv ? dragging.Guid : null, out var topLeft))
        {
            CleanupDragUI();
            return;
        }

        bool canPlace = (hv == dv)
            ? hv.grid.CanPlace(dragging, topLeft, dragging.Rotated, dragging.Guid)
            : hv.grid.CanPlace(dragging, topLeft, dragging.Rotated);

        if (canPlace)
        {
            if (hv == dv)
            {
                if (hv.grid.TryMove(dragging.Guid, topLeft, dragging.Rotated, out _))
                    RedrawGrid(hv);
            }
            else
            {
                TryTransferBetweenGrids(dragging, dv, hv, topLeft, dragging.Rotated);
            }
        }

        CleanupDragUI();
    }

    // QUICK-STASH LOGIK
    bool QuickStash(ItemInstance item, GridView fromView)
    {
        if (item == null || fromView == null || fromView.inv == null) return false;

        // alle unterschiedlichen Inventories sammeln
        var allInvs = views
            .Select(v => v.inv)
            .Where(inv => inv != null)
            .Distinct()
            .ToList();

        if (allInvs.Count <= 1)
            return false;

        MultiInventory fromInv = fromView.inv;
        MultiInventory targetInv = null;

        // Wenn wir nicht im Primary sind → stash in Primary
        if (primaryInventory != null && fromInv != primaryInventory && allInvs.Contains(primaryInventory))
        {
            targetInv = primaryInventory;
        }
        else
        {
            // sonst: erstes anderes Inventory als Ziel
            targetInv = allInvs.FirstOrDefault(inv => inv != fromInv);
        }

        if (targetInv == null || targetInv == fromInv)
            return false;

        var targetViews = views.Where(v => v.inv == targetInv).ToList();
        if (targetViews.Count == 0)
            return false;

        foreach (var tv in targetViews)
        {
            if (!ignoreInventoryRestrictions && !targetInv.CanInsert(tv.id, item))
                continue;

            if (tv.grid.FindFirstFit(item, out var pos, out var rot))
            {
                var oldRect = fromView.grid.GetRect(item.Guid);
                bool removed = fromView.grid.Remove(item.Guid);
                if (!removed)
                    return false;

                item.Rotated = rot;
                tv.grid.TryPlaceNew(item, pos, rot);

                RedrawGrid(fromView);
                RedrawGrid(tv);
                return true;
            }
        }

        return false;
    }

    void OnBeginDrag(GridView view, Vector2 screenPos)
    {
        if (dragging == null) return;

        if (!ignoreInventoryRestrictions && view.inv != null && !view.inv.CanTake(view.id))
        {
            dragging = null;
            CleanupDragUI();
            return;
        }

        if (view.itemViews.TryGetValue(dragging.Guid, out var rt) && rt)
        {
            hiddenItemView = rt;
            rt.gameObject.SetActive(false);
        }

        hoverView = view;
        view.ghost.transform.SetAsLastSibling();
        view.ghost.gameObject.SetActive(true);

        StartDragVisual(dragging, screenPos);
        OnHover(screenPos);
    }

    void OnHover(Vector2 screenPos)
    {
        if (dragging == null || dragging.Definition == null) return;

        lastScreenPos = screenPos;
        UpdateDragVisual(screenPos, dragging);

        if (!TryFindGridUnderMouse(screenPos, out var over))
        {
            if (hoverView != null) hoverView.ghost.gameObject.SetActive(false);
            hoverView = null;
            return;
        }

        if (hoverView != over)
        {
            if (hoverView != null) hoverView.ghost.gameObject.SetActive(false);
            hoverView = over;
            hoverView.ghost.transform.SetAsLastSibling();
            hoverView.ghost.gameObject.SetActive(true);
        }

        var size = SafeGetSize(dragging);

        var cam = CamFor(over.root);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(over.root, screenPos, cam, out var local);
        var stride = new Vector2(skin.cellSize.x + skin.cellPadding.x, skin.cellSize.y + skin.cellPadding.y);
        var half = new Vector2(
            size.x * skin.cellSize.x + Mathf.Max(0, size.x - 1) * skin.cellPadding.x,
            size.y * skin.cellSize.y + Mathf.Max(0, size.y - 1) * skin.cellPadding.y
        ) * 0.5f;

        int snapX = Mathf.Clamp(Mathf.RoundToInt((local.x - half.x) / stride.x), 0, over.grid.Size.x - size.x);
        int snapY = Mathf.Clamp(Mathf.RoundToInt((-local.y - half.y) / stride.y), 0, over.grid.Size.y - size.y);
        var snapTL = new Vector2Int(snapX, snapY);

        Vector2Int bestTL = default;
        bool ok = (ignoreInventoryRestrictions || over.inv == null || over.inv.CanInsert(over.id, dragging)) &&
                  FindNearestValidTopLeft(over, screenPos, size,
                      (over == dragView) ? dragging.Guid : null,
                      out bestTL,
                      maxRadiusCells: 2);

        if (ok)
        {
            PositionRect(over.ghost.rectTransform, bestTL.x, bestTL.y, over.grid.Size, size);
            over.ghost.color = new Color(0, 1, 0, 0.25f);
        }
        else
        {
            PositionRect(over.ghost.rectTransform, snapTL.x, snapTL.y, over.grid.Size, size);
            over.ghost.color = new Color(1, 0, 0, 0.25f);
        }
    }

    bool TryFindGridUnderMouse(Vector2 screenPos, out GridView view)
    {
        for (int i = 0; i < views.Count; i++)
        {
            var v = views[i];
            if (v.root == null || !v.root.gameObject.activeInHierarchy) continue;

            var cam = CamFor(v.root);

            if (RectTransformUtility.RectangleContainsScreenPoint(v.root, screenPos, cam))
            {
                view = v;
                return true;
            }
        }

        view = null;
        return false;
    }

    Vector2Int ScreenToCell(GridView view, Vector2 screenPos)
    {
        var cam = CamFor(view.root);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(view.root, screenPos, cam, out var local);

        var cs = skin.cellSize;
        var pad = skin.cellPadding;
        int x = Mathf.FloorToInt(Mathf.Clamp(local.x, 0, view.root.sizeDelta.x - 1) / (cs.x + pad.x));
        int y = Mathf.FloorToInt(Mathf.Clamp(-local.y, 0, view.root.sizeDelta.y - 1) / (cs.y + pad.y));
        return new Vector2Int(x, y);
    }

    // ---------- Drag visuals ----------

    void StartDragVisual(ItemInstance item, Vector2 screenPos)
    {
        if (dragVisual != null) StopDragVisual();

        var go = new GameObject("DragVisual", typeof(RectTransform), typeof(Image));
        dragVisual = go.GetComponent<RectTransform>();
        dragImage = go.GetComponent<Image>();

        dragVisual.SetParent(dragLayer, false);
        dragVisual.anchorMin = dragVisual.anchorMax = new Vector2(0.5f, 0.5f);
        dragVisual.pivot = new Vector2(0.5f, 0.5f);
        dragImage.raycastTarget = false;
        dragImage.sprite = item.Definition.Icon;
        dragImage.preserveAspect = true;

        var sz = SafeGetSize(item);
        dragVisual.sizeDelta = CalcItemPixelSize(sz);

        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(canvas, screenPos, null, out var local))
        {
            dragVisual.anchoredPosition = local;
            dragTargetLocal = local;
        }

        dragLayer.SetAsLastSibling();
    }

    void UpdateDragVisual(Vector2 screenPos, ItemInstance item)
    {
        if (dragVisual == null) return;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(canvas, screenPos, null, out var local))
            dragTargetLocal = local;

        dragVisual.sizeDelta = CalcItemPixelSize(SafeGetSize(item));
        dragVisual.anchoredPosition = Vector2.Lerp(
            dragVisual.anchoredPosition,
            dragTargetLocal,
            1f - Mathf.Exp(-dragFollowSpeed * Time.unscaledDeltaTime)
        );
    }

    Vector2 CalcItemPixelSize(Vector2Int itemSize)
    {
        var cs = skin.cellSize;
        var pad = skin.cellPadding;
        float w = itemSize.x * cs.x + Mathf.Max(0, itemSize.x - 1) * pad.x;
        float h = itemSize.y * cs.y + Mathf.Max(0, itemSize.y - 1) * pad.y;
        return new Vector2(w, h);
    }

    Vector2Int SafeGetSize(ItemInstance it)
    {
        if (it == null || it.Definition == null) return Vector2Int.one;
        try { return it.GetSize(); } catch { return Vector2Int.one; }
    }

    void StopDragVisual()
    {
        if (dragVisual != null)
        {
            Destroy(dragVisual.gameObject);
            dragVisual = null;
            dragImage = null;
        }
    }

    void CleanupDragUI()
    {
        if (hiddenItemView != null)
        {
            hiddenItemView.gameObject.SetActive(true);
            hiddenItemView = null;
        }

        if (dragView != null && dragView.ghost != null) dragView.ghost.gameObject.SetActive(false);
        if (hoverView != null && hoverView != dragView && hoverView.ghost != null)
            hoverView.ghost.gameObject.SetActive(false);
        StopDragVisual();
        dragging = null;
        dragView = null;
        hoverView = null;
    }

    // ---------- Placement/Transfer ----------

    bool FindNearestValidTopLeft(
        GridView view,
        Vector2 screenPos,
        Vector2Int itemSize,
        string ignoreGuid,
        out Vector2Int bestTopLeft,
        int maxRadiusCells = 2)
    {
        bestTopLeft = default;
        if (view == null || view.root == null || view.grid == null) return false;

        var cam = CamFor(view.root);

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(view.root, screenPos, cam, out var local))
            return false;

        var cs = skin.cellSize;
        var pad = skin.cellPadding;
        var stride = new Vector2(cs.x + pad.x, cs.y + pad.y);

        var half = new Vector2(
            itemSize.x * cs.x + Mathf.Max(0, itemSize.x - 1) * pad.x,
            itemSize.y * cs.y + Mathf.Max(0, itemSize.y - 1) * pad.y
        ) * 0.5f;

        float fx = (local.x - half.x) / stride.x;
        float fy = (-local.y - half.y) / stride.y;

        int fx0 = Mathf.FloorToInt(fx), fx1 = Mathf.CeilToInt(fx);
        int fy0 = Mathf.FloorToInt(fy), fy1 = Mathf.CeilToInt(fy);

        Vector2Int ClampTL(int x, int y)
        {
            x = Mathf.Clamp(x, 0, view.grid.Size.x - itemSize.x);
            y = Mathf.Clamp(y, 0, view.grid.Size.y - itemSize.y);
            return new Vector2Int(x, y);
        }

        float Score(Vector2Int tl)
        {
            var center = new Vector2(
                tl.x * stride.x + half.x,
                tl.y * stride.y + half.y
            );
            var cursor = new Vector2(local.x, -local.y);
            return (center - cursor).sqrMagnitude;
        }

        bool Legal(Vector2Int tl)
        {
            return (ignoreGuid != null)
                ? view.grid.CanPlace(dragging, tl, dragging.Rotated, ignoreGuid)
                : view.grid.CanPlace(dragging, tl, dragging.Rotated);
        }

        Vector2Int best = default;
        float bestScore = float.PositiveInfinity;
        bool found = false;
        var seed = new Vector2Int[]
        {
            ClampTL(fx0, fy0), ClampTL(fx1, fy0),
            ClampTL(fx0, fy1), ClampTL(fx1, fy1)
        };
        foreach (var c in seed)
        {
            if (Legal(c))
            {
                var s = Score(c);
                if (s < bestScore) { bestScore = s; best = c; found = true; }
            }
        }

        if (found) { bestTopLeft = best; return true; }

        for (int r = 1; r <= maxRadiusCells; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                int dy = r - Mathf.Abs(dx);
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    int x = Mathf.RoundToInt(fx) + dx;
                    int y = Mathf.RoundToInt(fy) + dy * sy;
                    var tl = ClampTL(x, y);
                    if (!Legal(tl)) continue;
                    var s = Score(tl);
                    if (s < bestScore) { bestScore = s; best = tl; found = true; }
                }
            }
            if (found) { bestTopLeft = best; return true; }
        }

        return false;
    }

    void TryTransferBetweenGrids(ItemInstance item, GridView from, GridView to, Vector2Int pos, bool rot)
    {
        if (!ignoreInventoryRestrictions && to.inv != null && to.inv.IsReadOnly(to.id))
            return;

        var oldRect = from.grid.GetRect(item.Guid);
        var oldRot = item.Rotated;

        if (!from.grid.Remove(item.Guid)) return;

        if (to.grid.CanPlace(item, pos, rot))
        {
            item.Rotated = rot;
            to.grid.TryPlaceNew(item, pos, rot);
            hiddenItemView = null;

            RedrawGrid(from);
            RedrawGrid(to);
        }
        else
        {
            item.Rotated = oldRot;
            from.grid.TryPlaceNew(item, oldRect.position, oldRot);
            RedrawGrid(from);
        }
    }

    // ---------- Visual helpers ----------

    static void AddFrame(RectTransform root, Color col, float thick)
    {
        void Edge(string n, Vector2 aMin,  Vector2 aMax)
        {
            var go = new GameObject(n, typeof(RectTransform), typeof(Image));
            var rt = go.GetComponent<RectTransform>();
            var img = go.GetComponent<Image>();
            img.color = col;
            rt.SetParent(root, false);
            rt.anchorMin = aMin; rt.anchorMax = aMax; rt.pivot = new Vector2(0, 1);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        Edge("_Top",    new(0, 1), new(1, 1));
        root.Find("_Top").GetComponent<RectTransform>().sizeDelta = new Vector2(0, thick);

        Edge("_Bottom", new(0, 0), new(1, 0));
        root.Find("_Bottom").GetComponent<RectTransform>().sizeDelta = new Vector2(0, thick);

        Edge("_Left",   new(0, 0), new(0, 1));
        root.Find("_Left").GetComponent<RectTransform>().sizeDelta = new Vector2(thick, 0);

        Edge("_Right",  new(1, 0), new(1, 1));
        root.Find("_Right").GetComponent<RectTransform>().sizeDelta = new Vector2(thick, 0);
    }
    // Am Ende der Klasse InventoryUIController

    public void RebuildUI()
    {
        isReady = false;
        Build();
        RedrawAll();
        Canvas.ForceUpdateCanvases();
        isReady = true;
    }
}


