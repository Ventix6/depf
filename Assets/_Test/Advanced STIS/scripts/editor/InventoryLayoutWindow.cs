#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// Inventory Layout Designer (arbeitet direkt auf einem INVENTAR-ROOT)
/// Erwartet am Target:
///   - MultiInventory      (Grids/Access + LootTable)
///   - InventoryLayoutData (Layout/Look, Panel/Cell-Skin)
///   - InventorySeed       (optional Startbefüllung)
///
/// Tabs:
///   - Layout:   Grids + Panel + Access + Seeds
///   - Loot:     LootTable des MultiInventory bearbeiten/erzeugen
///   - Items:    ItemDefinition-Assets erstellen + Übersicht
public class InventoryLayoutDesignerWindow : EditorWindow
{
    // ----------------- Tabs -----------------
    enum MainTab { Layout, Loot, Items }
    MainTab mainTab = MainTab.Layout;

    // ----------------- Preview-Model (Layout) -----------------
    [Serializable]
    class GridPreview
    {
        public string id;
        public Vector2Int sizeCells = new(3, 3);
        public Vector2 posPx;            // Top-Left (content px)
        public bool overrideSize = true;
        public Vector2Int sizeOverride = new(3, 3);
        public Color color = new(0.16f, 0.16f, 0.18f, 1);
        public float framePx = 0f;
        public bool selected;
        public List<SeedItem> initialItems = new();
    }

    [Serializable]
    class SeedItem
    {
        public ItemDefinition def;
        public int amount = 1;
        public Vector2Int pos;
        public bool rotated;
    }

    // ----------------- Targets -----------------
    GameObject          targetInventoryRoot;
    MultiInventory      multi;
    InventoryLayoutData layoutData;
    InventorySeed       seedData;
    CrateInventory      crateInv;
    SerializedObject    soMulti, soLayout, soSeed, soCrate;

    // ----------------- View/Selection -----------------
    float   zoom = 1f;
    Vector2 pan  = Vector2.zero;
    Rect previewRect, previewContent;

    readonly List<GridPreview> grids = new();
    List<int>    selIdx  = new();
    Vector2[]    selStartPos;
    Vector2Int[] selStartSize;
    Vector2 dragOriginContent;
    bool resizing; int resizeHandle = -1; int resizingGridIndex = -1;
    bool marquee; Rect marqueeRectLocal;

    // Skin cache (aus InventoryLayoutData)
    Vector2 cellSize  = new(64, 64);
    Vector2 cellPad   = new(4, 4);
    Vector2 panelSize = new(1200, 800);

    // Panel-Padding: x=Left, y=Right, z=Top, w=Bottom
    Vector4 panelPadding = new Vector4(16, 16, 16, 16);

    enum SnapMode { CellStride, CellStrideMul, Pixel }
    SnapMode snapMode = SnapMode.CellStride;
    float snapMul = 1f; int snapPx = 8;

    bool pruneSpecsOnSave = true;

    ItemDefinition pickCandidate;
    const int AllowedPickerId = 991001;

    // ----------------- Items-Tab State -----------------
    DefaultAsset itemFolder;
    string      newItemName     = "NewItem";
    Sprite      newItemIcon;
    Vector2Int  newItemBaseSize = Vector2Int.one;
    int         newItemMaxStack = 1;
    int         newItemType     = 0;
    Vector2     itemScrollPos;

    // ----------------- Layout-Left Scroll -----------------
    Vector2 leftScrollPos;

    // Styles
    static GUIStyle _midTitle, _badge, _center, _toolbarBox, _miniRight;
    static GUIStyle MidTitle  => _midTitle  ??= new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter, fontSize = 12 };
    static GUIStyle Badge     => _badge     ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color(1, 1, 1, .65f) } };
    static GUIStyle Center    => _center    ??= new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter };
    static GUIStyle TBBox     => _toolbarBox??= new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(6, 6, 4, 6) };
    static GUIStyle MiniRight => _miniRight ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };

    [MenuItem("Tools/Inventory/Layout Designer (Inventory Root)")]
    static void Open() => GetWindow<InventoryLayoutDesignerWindow>("Inventory Layout");

    void OnGUI()
    {
        // Tabs
        mainTab = (MainTab)GUILayout.Toolbar((int)mainTab, new[] { "Layout", "Loot", "Items" });
        GUILayout.Space(4);

        switch (mainTab)
        {
            case MainTab.Layout:
                DrawLayoutGUI();
                HandleShortcuts();   // nur für Layout-Tab relevant
                break;

            case MainTab.Loot:
                DrawLootGUI();
                break;

            case MainTab.Items:
                DrawItemsGUI();
                break;
        }

        // Object Picker (Allowed Definitions)
        if ((Event.current.commandName == "ObjectSelectorUpdated" || Event.current.commandName == "ObjectSelectorClosed")
            && EditorGUIUtility.GetObjectPickerControlID() == AllowedPickerId)
        {
            pickCandidate = EditorGUIUtility.GetObjectPickerObject() as ItemDefinition;
            Repaint();
        }
    }

    // ================= Layout-Tab Gesamtlayout =================
    void DrawLayoutGUI()
    {
        EditorGUILayout.BeginHorizontal();

        DrawLeft(340);
        DrawPreview();
        DrawRight(420);

        EditorGUILayout.EndHorizontal();
    }

    // ================= Left (Layout) =================
    void DrawLeft(float width)
    {
        EditorGUILayout.BeginVertical(GUILayout.Width(width));
        // ScrollView um alles herum, damit unten nichts abgeschnitten wird
        using (var scroll = new EditorGUILayout.ScrollViewScope(leftScrollPos))
        {
            leftScrollPos = scroll.scrollPosition;

            GUILayout.Space(4);

            targetInventoryRoot = (GameObject)EditorGUILayout.ObjectField("Inventory Root", targetInventoryRoot, typeof(GameObject), true);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Ensure Components")) EnsureComponents(createIfMissing: true);
                if (GUILayout.Button("Load from Components")) { if (EnsureComponents()) LoadFromComponents(); }
            }

            if (!EnsureComponents())
            {
                EditorGUILayout.HelpBox("Wähle ein GameObject. Klicke 'Ensure Components', um fehlende Komponenten anzulegen.", MessageType.Info);
                return;
            }

            GUILayout.Space(6);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Skin / Panel", EditorStyles.boldLabel);
                var cs = EditorGUILayout.Vector2Field("Cell Size", cellSize);
                var cp = EditorGUILayout.Vector2Field("Cell Padding", cellPad);
                var ps = EditorGUILayout.Vector2Field("Panel Size", panelSize);

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Auto Panel Padding (px)", EditorStyles.miniBoldLabel);
                panelPadding.x = EditorGUILayout.FloatField("Left",   panelPadding.x);
                panelPadding.y = EditorGUILayout.FloatField("Right",  panelPadding.y);
                panelPadding.z = EditorGUILayout.FloatField("Top",    panelPadding.z);
                panelPadding.w = EditorGUILayout.FloatField("Bottom", panelPadding.w);

                if (cs != cellSize || cp != cellPad || ps != panelSize)
                {
                    cellSize  = cs;
                    cellPad   = cp;
                    panelSize = ps;
                    Repaint();
                }
            }

            // --------- Inventory-Typ (Player / Crate) ----------
            GUILayout.Space(6);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Inventory Root Type", EditorStyles.boldLabel);

                int typeIndex = crateInv ? 1 : 0;
                typeIndex = GUILayout.Toolbar(typeIndex, new[] { "Player Inventory", "Crate Inventory" });

                // Umschalten: Player -> Crate
                if (typeIndex == 1 && crateInv == null)
                {
                    crateInv = Undo.AddComponent<CrateInventory>(targetInventoryRoot);
                    if (crateInv != null && multi != null)
                        crateInv.crate = multi;

                    soCrate = crateInv ? new SerializedObject(crateInv) : null;
                    EditorUtility.SetDirty(targetInventoryRoot);
                }
                // Umschalten: Crate -> Player (nur optionales Entfernen per Button)
                else if (typeIndex == 0 && crateInv != null)
                {
                    EditorGUILayout.HelpBox("Dieses Root hat aktuell ein CrateInventory. Wenn es kein Container mehr sein soll, kannst du die Komponente entfernen.", MessageType.None);
                    if (GUILayout.Button("CrateInventory-Komponente entfernen"))
                    {
                        Undo.DestroyObjectImmediate(crateInv);
                        crateInv = null;
                        soCrate = null;
                        EditorUtility.SetDirty(targetInventoryRoot);
                    }
                }

                if (crateInv != null)
                {
                    GUILayout.Space(4);
                    EditorGUILayout.LabelField("Crate Settings", EditorStyles.miniBoldLabel);

                    if (soCrate == null)
                        soCrate = new SerializedObject(crateInv);

                    soCrate.Update();

                    // crate (MultiInventory-Referenz)
                    var crateProp = soCrate.FindProperty("crate");
                    if (crateProp != null)
                    {
                        // Standardmäßig auf dieses MultiInventory setzen
                        if (crateProp.objectReferenceValue == null && multi != null)
                            crateProp.objectReferenceValue = multi;

                        EditorGUILayout.PropertyField(crateProp, new GUIContent("Crate Inventory"));
                    }

                    // optionale Felder nur anzeigen, wenn sie existieren
                    var crateIdProp = soCrate.FindProperty("crateId");
                    if (crateIdProp != null)
                        EditorGUILayout.PropertyField(crateIdProp, new GUIContent("Crate Id"));

                    var respawnProp = soCrate.FindProperty("respawnDelay");
                    if (respawnProp != null)
                        EditorGUILayout.PropertyField(respawnProp, new GUIContent("Respawn Delay (s)"));

                    if (soCrate.ApplyModifiedProperties())
                    {
                        EditorUtility.SetDirty(crateInv);
                    }
                }
            }

            GUILayout.Space(6);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("MultiInventory / Loot (Basics)", EditorStyles.boldLabel);

                if (multi != null)
                {
                    EditorGUI.BeginChangeCheck();
                    var lt          = (LootTable)EditorGUILayout.ObjectField("Loot Table", multi.lootTable, typeof(LootTable), false);
                    int lootSeed    = EditorGUILayout.IntField("Loot Seed (0=auto)", multi.lootSeed);
                    bool lootGenerated = EditorGUILayout.Toggle("Loot Generated", multi.lootGenerated);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(multi, "MultiInventory Loot Settings");
                        multi.lootTable     = lt;
                        multi.lootSeed      = lootSeed;
                        multi.lootGenerated = lootGenerated;
                        EditorUtility.SetDirty(multi);
                    }

                    EditorGUILayout.HelpBox("Details zur LootTable gibt es im 'Loot'-Tab.", MessageType.None);
                }
                else
                {
                    EditorGUILayout.HelpBox("Kein MultiInventory gefunden.", MessageType.Info);
                }
            }

            GUILayout.Space(6);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Save / Options", EditorStyles.boldLabel);
                pruneSpecsOnSave = EditorGUILayout.ToggleLeft("Prune specs not in layout on Save", pruneSpecsOnSave);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Save to Components")) SaveToComponents();
                    if (GUILayout.Button("Save as Prefab"))     SaveInventoryPrefab();
                }
            }

            GUILayout.Space(6);
            EditorGUILayout.LabelField("Snap", EditorStyles.boldLabel);
            snapMode = (SnapMode)EditorGUILayout.EnumPopup("Mode", snapMode);
            if (snapMode == SnapMode.CellStrideMul) snapMul = Mathf.Max(0.25f, EditorGUILayout.FloatField("× stride", snapMul));
            if (snapMode == SnapMode.Pixel)         snapPx  = Mathf.Max(1,      EditorGUILayout.IntField("Pixel", snapPx));

            GUILayout.Space(8);
            EditorGUILayout.LabelField("Tools", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add all from specs")) AddAllFromSpecs();
                if (GUILayout.Button("New Grid"))           CreateGrid();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Duplicate")) DuplicateSelection();
                if (GUILayout.Button("Delete"))    DeleteSelection();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Align L")) AlignLeft();
                if (GUILayout.Button("Align T")) AlignTop();
                if (GUILayout.Button("Dist X"))  Distribute(true);
                if (GUILayout.Button("Dist Y"))  Distribute(false);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Fit Panel")) AutoFitPanelSize();
                if (GUILayout.Button("Refresh"))   Repaint();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.HelpBox(
                "Wheel=Zoom, MMB=Pan, LMB leer=Marquee, LMB Grid=Move, Resize=Griffe,\n" +
                "Arrow=Nudge, Ctrl+D=Dup, Del=Delete.",
                MessageType.None);
        }

        EditorGUILayout.EndVertical();
    }

    // ================= Preview (Layout) =================
    void DrawPreview()
    {
        EditorGUILayout.BeginVertical();
        GUILayout.Space(4);
        EditorGUILayout.LabelField("Panel Preview", MidTitle);

        previewRect = GUILayoutUtility.GetRect(10, 10, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        EditorGUI.DrawRect(previewRect, new Color(0.105f, 0.105f, 0.11f, 1f));

        float pad = 8f;
        previewContent = new Rect(previewRect.x + pad, previewRect.y + pad, previewRect.width - pad * 2, previewRect.height - pad * 2);
        EditorGUI.DrawRect(previewContent, new Color(0.08f, 0.08f, 0.09f, 1f));

        Handles.color = new Color(1, 1, 1, 0.06f);
        Handles.DrawAAPolyLine(2f,
            new Vector3(previewContent.x,     previewContent.y),
            new Vector3(previewContent.xMax,  previewContent.y),
            new Vector3(previewContent.xMax,  previewContent.yMax),
            new Vector3(previewContent.x,     previewContent.yMax),
            new Vector3(previewContent.x,     previewContent.y));

        HandlePreview();

        EditorGUILayout.EndVertical();
    }

    // ================= Right (Layout) =================
    void DrawRight(float width)
    {
        EditorGUILayout.BeginVertical(GUILayout.Width(width));
        GUILayout.Space(4);
        EditorGUILayout.LabelField("Selection", MidTitle);

        var sel = grids.Where(g => g.selected).ToList();
        if (sel.Count == 0)
        {
            EditorGUILayout.HelpBox("Nichts ausgewählt.", MessageType.None);
            EditorGUILayout.EndVertical();
            return;
        }

        if (sel.Count == 1)
        {
            var g = sel[0];
            g.id = EditorGUILayout.TextField("Grid Id", g.id);
            if (string.IsNullOrWhiteSpace(g.id))
                EditorGUILayout.HelpBox("ID darf nicht leer sein.", MessageType.Warning);

            g.posPx       = EditorGUILayout.Vector2Field("Top-Left (px)", g.posPx);
            g.overrideSize= EditorGUILayout.Toggle("Override Size", g.overrideSize);
            if (g.overrideSize)
                g.sizeCells = g.sizeOverride = EditorGUILayout.Vector2IntField("Size Cells", g.sizeCells);
            else
            {
                var _ = EditorGUILayout.Vector2IntField("Size Cells", g.sizeCells);
            }
            g.color   = EditorGUILayout.ColorField("BG Color", g.color);
            g.framePx = EditorGUILayout.Slider("Frame px", g.framePx, 0, 6);

            GUILayout.Space(6);
            EditorGUILayout.LabelField("Access (MultiInventory)", EditorStyles.boldLabel);
            DrawAccessUIForGrid(g.id);

            GUILayout.Space(6);
            EditorGUILayout.LabelField("Initial Items", EditorStyles.boldLabel);
            DrawInitialItemsUI(g);
        }
        else
        {
            EditorGUILayout.LabelField($"{sel.Count} Grids", EditorStyles.boldLabel);
            if (GUILayout.Button("Same Color"))
            {
                var c = sel[0].color;
                foreach (var g in sel) g.color = c;
            }
            if (GUILayout.Button("Normalize Size from first"))
            {
                var s = sel[0].sizeCells;
                foreach (var g in sel)
                {
                    g.sizeCells    = s;
                    g.overrideSize = true;
                    g.sizeOverride = s;
                }
            }
        }

        EditorGUILayout.EndVertical();
    }

    // -------- Initial Items UI --------
    void DrawInitialItemsUI(GridPreview g)
    {
        if (seedData == null)
        {
            EditorGUILayout.HelpBox("InventorySeed fehlt (optional).", MessageType.None);
            return;
        }

        int remove = -1;
        for (int i = 0; i < g.initialItems.Count; i++)
        {
            var it = g.initialItems[i];
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            using (new EditorGUILayout.HorizontalScope())
            {
                it.def = (ItemDefinition)EditorGUILayout.ObjectField("Definition", it.def, typeof(ItemDefinition), false);
                if (GUILayout.Button("✕", GUILayout.Width(24))) remove = i;
            }
            it.amount  = Mathf.Clamp(EditorGUILayout.IntField("Amount", it.amount), 1, it.def ? it.def.MaxStack : 999);
            it.pos     = EditorGUILayout.Vector2IntField("Cell TL", it.pos);
            it.rotated = EditorGUILayout.Toggle("Rotated", it.rotated);

            if (it.def)
            {
                var baseSize = it.def.BaseSize;
                var sz = it.rotated ? new Vector2Int(baseSize.y, baseSize.x) : baseSize;
                it.pos.x = Mathf.Clamp(it.pos.x, 0, Mathf.Max(0, g.sizeCells.x - sz.x));
                it.pos.y = Mathf.Clamp(it.pos.y, 0, Mathf.Max(0, g.sizeCells.y - sz.y));
            }

            g.initialItems[i] = it;
            EditorGUILayout.EndVertical();
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Add Item"))
                g.initialItems.Add(new SeedItem { amount = 1, pos = Vector2Int.zero, rotated = false });

            GUILayout.FlexibleSpace();
            if (g.initialItems.Count > 0 && GUILayout.Button("Clear All", GUILayout.Width(90)))
                g.initialItems.Clear();
        }

        if (remove >= 0 && remove < g.initialItems.Count)
            g.initialItems.RemoveAt(remove);
    }

    // -------- Access/Restrict UI --------
    void DrawAccessUIForGrid(string gridId)
    {
        if (soMulti == null) return;

        var specs = soMulti.FindProperty("gridsSpec");
        if (specs == null || !specs.isArray)
        {
            EditorGUILayout.HelpBox("gridsSpec nicht gefunden.", MessageType.None);
            return;
        }

        int idx = -1;
        for (int i = 0; i < specs.arraySize; i++)
        {
            var el = specs.GetArrayElementAtIndex(i);
            if ((el.FindPropertyRelative("id")?.stringValue ?? "") == gridId)
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
        {
            EditorGUILayout.HelpBox("GridSpec existiert noch nicht – wird beim Save angelegt.", MessageType.Info);
            return;
        }

        var spec     = specs.GetArrayElementAtIndex(idx);
        var typeProp = spec.FindPropertyRelative("inventoryType");
        var restProp = spec.FindPropertyRelative("restrictToDefinition");
        var allowProp= spec.FindPropertyRelative("allowedDefinition");

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("Mode", GUILayout.Width(38));
            int cur   = typeProp.enumValueIndex; // 0=TakeOnly,1=InsertOnly,2=Normal
            int uiCur = cur == 2 ? 0 : (cur == 1 ? 1 : 2);
            uiCur     = GUILayout.Toolbar(uiCur, new[] { "Normal", "InsertOnly", "TakeOnly" });
            int newEnum = uiCur == 0 ? 2 : (uiCur == 1 ? 1 : 0);
            if (newEnum != typeProp.enumValueIndex)
                typeProp.enumValueIndex = newEnum;
        }

        restProp.boolValue = EditorGUILayout.ToggleLeft("Restrict To Definition", restProp.boolValue);
        if (restProp.boolValue)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Allow", GUILayout.Width(40));
                pickCandidate = (ItemDefinition)EditorGUILayout.ObjectField(pickCandidate, typeof(ItemDefinition), false);

                if (GUILayout.Button("Pick...", GUILayout.Width(64)))
                    EditorGUIUtility.ShowObjectPicker<ItemDefinition>(pickCandidate, false, "", AllowedPickerId);

                bool already = pickCandidate && ContainsDefinition(allowProp, pickCandidate);
                using (new EditorGUI.DisabledScope(pickCandidate == null || already))
                {
                    if (GUILayout.Button("➕ Add", GUILayout.Width(70)))
                    {
                        int n = allowProp.arraySize;
                        allowProp.InsertArrayElementAtIndex(n);
                        allowProp.GetArrayElementAtIndex(n).objectReferenceValue = pickCandidate;
                        soMulti.ApplyModifiedPropertiesWithoutUndo();
                        EditorUtility.SetDirty(soMulti.targetObject);
                        pickCandidate = null;
                        GUI.FocusControl(null);
                    }
                }
                if (already) GUILayout.Label("already in list", MiniRight);
            }

            var dropRect = GUILayoutUtility.GetRect(GUIContent.none, GUI.skin.box, GUILayout.Height(22));
            GUI.Box(dropRect, "Drop ItemDefinition here");
            if ((Event.current.type == EventType.DragUpdated || Event.current.type == EventType.DragPerform) &&
                dropRect.Contains(Event.current.mousePosition))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (Event.current.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var obj in DragAndDrop.objectReferences)
                    {
                        var def = obj as ItemDefinition;
                        if (!def) continue;
                        if (ContainsDefinition(allowProp, def)) continue;
                        int n = allowProp.arraySize;
                        allowProp.InsertArrayElementAtIndex(n);
                        allowProp.GetArrayElementAtIndex(n).objectReferenceValue = def;
                    }
                    soMulti.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(soMulti.targetObject);
                }
                Event.current.Use();
            }

            int removeIndex = -1;
            for (int i = 0; i < allowProp.arraySize; i++)
            {
                var e = allowProp.GetArrayElementAtIndex(i);
                var def = e.objectReferenceValue as ItemDefinition;

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.ObjectField(def, typeof(ItemDefinition), false);
                    if (GUILayout.Button("✕", GUILayout.Width(24))) removeIndex = i;
                }
            }
            if (removeIndex >= 0)
            {
                allowProp.DeleteArrayElementAtIndex(removeIndex);
                soMulti.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(soMulti.targetObject);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(allowProp.arraySize == 0))
                {
                    if (GUILayout.Button("Remove All", GUILayout.Width(100)))
                    {
                        allowProp.ClearArray();
                        soMulti.ApplyModifiedPropertiesWithoutUndo();
                        EditorUtility.SetDirty(soMulti.targetObject);
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        soMulti.ApplyModifiedProperties();
    }

    static bool ContainsDefinition(SerializedProperty listProp, ItemDefinition def)
    {
        if (listProp == null || !listProp.isArray) return false;
        for (int i = 0; i < listProp.arraySize; i++)
        {
            var e = listProp.GetArrayElementAtIndex(i).objectReferenceValue as ItemDefinition;
            if (e == def) return true;
        }
        return false;
    }

    // ================= Preview Interaction (Layout) =================
    Rect BoxOf(GridPreview g)
    {
        var sizePx = GridPxSize(g.sizeCells, cellSize, cellPad);
        var tl     = pan + g.posPx * zoom;
        var sz     = sizePx * zoom;
        return new Rect(tl.x, tl.y, sz.x, sz.y);
    }

    Vector2 SnapStride()
    {
        return snapMode switch
        {
            SnapMode.CellStride    => cellSize + cellPad,
            SnapMode.CellStrideMul => (cellSize + cellPad) * Mathf.Max(0.25f, snapMul),
            SnapMode.Pixel         => new Vector2(snapPx, snapPx),
            _                      => cellSize + cellPad
        };
    }

    Vector2 ApplySnap(Vector2 p, Vector2 stride)
    {
        return new Vector2(
            Mathf.Round(p.x / stride.x) * stride.x,
            Mathf.Round(p.y / stride.y) * stride.y
        );
    }

    void HandlePreview()
    {
        if (Event.current == null) return;

        GUI.BeginGroup(previewContent);
        var e       = Event.current;
        var mLocal  = e.mousePosition;
        var inWorkArea = new Rect(Vector2.zero, previewContent.size).Contains(mLocal);

        // Panel background
        var pRect = new Rect(pan.x, pan.y, panelSize.x * zoom, panelSize.y * zoom);
        EditorGUI.DrawRect(pRect, new Color(0.06f, 0.06f, 0.065f, 1f));
        Handles.color = new Color(1, 1, 1, 0.10f);
        Handles.DrawAAPolyLine(2f,
            new Vector3(pRect.x,    pRect.y),
            new Vector3(pRect.xMax, pRect.y),
            new Vector3(pRect.xMax, pRect.yMax),
            new Vector3(pRect.x,    pRect.yMax),
            new Vector3(pRect.x,    pRect.y));

        // Grids
        for (int i = 0; i < grids.Count; i++)
        {
            var g   = grids[i];
            var box = BoxOf(g);

            EditorGUI.DrawRect(box, g.color);
            Handles.color = g.selected ? new Color(0.15f, 0.8f, 1f, 1f) : new Color(0.5f, 0.5f, 0.5f, 1f);
            Handles.DrawAAPolyLine(2f,
                new Vector3(box.x,    box.y),
                new Vector3(box.xMax, box.y),
                new Vector3(box.xMax, box.yMax),
                new Vector3(box.x,    box.yMax),
                new Vector3(box.x,    box.y));
            GUI.Label(box, g.id, Center);

            // Zellenlinien als Vorschau
            float sx = (cellSize.x + cellPad.x) * zoom;
            float sy = (cellSize.y + cellPad.y) * zoom;
            Handles.color = new Color(0, 0, 0, 0.35f);
            for (int x = 1; x < g.sizeCells.x; x++)
            {
                float xx = box.x + x * sx - cellPad.x * 0.5f * zoom;
                Handles.DrawLine(new Vector3(xx, box.y), new Vector3(xx, box.yMax));
            }
            for (int y = 1; y < g.sizeCells.y; y++)
            {
                float yy = box.y + y * sy - cellPad.y * 0.5f * zoom;
                Handles.DrawLine(new Vector3(box.x, yy), new Vector3(box.xMax, yy));
            }
        }

        DrawToolbar();

        // Zoom
        if (e.type == EventType.ScrollWheel && inWorkArea)
        {
            float zOld = zoom;
            float zNew = Mathf.Clamp(zoom * Mathf.Exp(-e.delta.y * 0.1f), 0.25f, 6f);
            Vector2 worldBefore = (mLocal - pan) / zOld;
            zoom = zNew;
            pan  = mLocal - worldBefore * zNew;
            Repaint();
            e.Use();
        }

        // Pan (MMB)
        if (e.type == EventType.MouseDrag && e.button == 2)
        {
            pan += e.delta;
            Repaint();
            e.Use();
        }

        // MouseDown: Handles > Box > Marquee
        if (e.type == EventType.MouseDown && e.button == 0 && inWorkArea)
        {
            bool hit = false;
            for (int i = grids.Count - 1; i >= 0; i--)
            {
                var g   = grids[i];
                var box = BoxOf(g);
                if (!box.Contains(mLocal)) continue;

                // Handles
                var hs = HandleRects(box);
                for (int h = 0; h < hs.Length; h++)
                {
                    if (hs[h].Contains(mLocal))
                    {
                        if (!e.shift && !e.control) DeselectAll();
                        g.selected = true;
                        BeginResize(i, h, (mLocal - pan) / Mathf.Max(0.0001f, zoom));
                        hit = true;
                        e.Use();
                        break;
                    }
                }
                if (hit) break;

                // Select/Move
                if (!e.shift && !e.control) DeselectAll();
                g.selected = true;
                StartDragSelection((mLocal - pan) / Mathf.Max(0.0001f, zoom));
                hit = true;
                e.Use();
                break;
            }
            if (!hit)
            {
                if (!e.shift && !e.control) DeselectAll();
                marquee         = true;
                marqueeRectLocal= new Rect(mLocal, Vector2.zero);
                e.Use();
            }
        }

        if (e.type == EventType.MouseUp && e.button == 0)
        {
            marquee            = false;
            selStartPos        = null;
            selStartSize       = null;
            selIdx.Clear();
            resizing           = false;
            resizeHandle       = -1;
            resizingGridIndex  = -1;
        }

        // Marquee
        if (marquee && e.type == EventType.MouseDrag)
        {
            marqueeRectLocal.size = mLocal - marqueeRectLocal.position;
            var norm = NormalizeRect(marqueeRectLocal);
            Handles.BeginGUI();
            Handles.color = new Color(0.2f, 0.7f, 1f, 0.2f);
            Handles.DrawSolidRectangleWithOutline(norm,
                new Color(0.2f, 0.7f, 1f, 0.08f),
                new Color(0.2f, 0.7f, 1f, 0.9f));
            Handles.EndGUI();
            for (int i = 0; i < grids.Count; i++)
                if (norm.Overlaps(BoxOf(grids[i])))
                    grids[i].selected = true;
            Repaint();
            e.Use();
        }

        // Move
        if (e.type == EventType.MouseDrag && selStartPos != null && !resizing && e.button == 0)
        {
            var curContent   = (mLocal - pan) / Mathf.Max(0.0001f, zoom);
            var deltaContent = curContent - dragOriginContent;
            var stride       = SnapStride();
            for (int k = 0; k < selIdx.Count; k++)
            {
                int idx = selIdx[k];
                if (idx < 0 || idx >= grids.Count) continue;
                var g = grids[idx];
                var p = selStartPos[k] + deltaContent;
                g.posPx = ApplySnap(p, stride);
                g.posPx = Vector2.Max(Vector2.zero, g.posPx);
            }
            Repaint();
            e.Use();
        }

        // Resize
        if (resizing && e.type == EventType.MouseDrag && e.button == 0 && resizingGridIndex >= 0)
        {
            DoResize((mLocal - pan) / Mathf.Max(0.0001f, zoom));
            Repaint();
            e.Use();
        }

        GUI.EndGroup();
    }

    void DrawToolbar()
    {
        var r = new Rect(previewContent.width - 260, 8, 252, 66);
        GUILayout.BeginArea(r, TBBox);
        GUILayout.Label($"Zoom {zoom:0.00}", EditorStyles.miniBoldLabel);

        using (new GUILayout.HorizontalScope())
        {
            GUILayout.Label("Snap", GUILayout.Width(36));
            int mode = (int)snapMode;
            mode = GUILayout.Toolbar(mode, new[] { "Stride", "×Stride", "Pixel" }, GUILayout.Width(180));
            if ((int)snapMode != mode)
            {
                snapMode = (SnapMode)mode;
                Repaint();
            }
        }
        using (new GUILayout.HorizontalScope())
        {
            if (snapMode == SnapMode.CellStrideMul)
            {
                GUILayout.Label("×", GUILayout.Width(12));
                var nm = Mathf.Max(0.25f, EditorGUILayout.FloatField(snapMul, GUILayout.Width(60)));
                if (!Mathf.Approximately(nm, snapMul))
                {
                    snapMul = nm;
                    Repaint();
                }
            }
            else if (snapMode == SnapMode.Pixel)
            {
                GUILayout.Label("px", GUILayout.Width(16));
                var np = Mathf.Max(1, EditorGUILayout.IntField(snapPx, GUILayout.Width(60)));
                if (np != snapPx)
                {
                    snapPx = np;
                    Repaint();
                }
            }
            else GUILayout.FlexibleSpace();
        }
        GUILayout.EndArea();
    }

    Rect[] HandleRects(Rect b)
    {
        const float s = 8f;
        return new[]
        {
            new Rect(b.x - s,             b.y - s,             s, s), // TL
            new Rect(b.center.x - s*0.5f, b.y - s,             s, s), // TM
            new Rect(b.xMax,              b.y - s,             s, s), // TR
            new Rect(b.x - s,             b.center.y - s*0.5f, s, s), // ML
            new Rect(b.xMax,              b.center.y - s*0.5f, s, s), // MR
            new Rect(b.x - s,             b.yMax,              s, s), // BL
            new Rect(b.center.x - s*0.5f, b.yMax,              s, s), // BM
            new Rect(b.xMax,              b.yMax,              s, s), // BR
        };
    }

    void BeginResize(int gridIndex, int handle, Vector2 mouseContent)
    {
        resizing           = true;
        resizingGridIndex  = gridIndex;
        resizeHandle       = handle;
        dragOriginContent  = mouseContent;
        selStartSize       = new[] { grids[gridIndex].sizeCells };
        selStartPos        = new[] { grids[gridIndex].posPx };
    }

    void DoResize(Vector2 mouseContent)
    {
        if (resizingGridIndex < 0 || resizingGridIndex >= grids.Count) return;
        var g = grids[resizingGridIndex];

        var stride = cellSize + cellPad;
        var size   = selStartSize[0];
        var delta  = mouseContent - dragOriginContent;

        bool left   = resizeHandle == 0 || resizeHandle == 3 || resizeHandle == 5; // TL ML BL
        bool right  = resizeHandle == 2 || resizeHandle == 4 || resizeHandle == 7; // TR MR BR
        bool top    = resizeHandle == 0 || resizeHandle == 1 || resizeHandle == 2; // TL TM TR
        bool bottom = resizeHandle == 5 || resizeHandle == 6 || resizeHandle == 7; // BL BM BR

        if (right)
            size.x = Mathf.Max(1, Mathf.RoundToInt(selStartSize[0].x + delta.x / stride.x));
        if (bottom)
            size.y = Mathf.Max(1, Mathf.RoundToInt(selStartSize[0].y + delta.y / stride.y));

        if (left)
        {
            int dx = Mathf.RoundToInt(-delta.x / stride.x);
            if (dx != 0)
            {
                g.posPx.x = selStartPos[0].x + dx * stride.x;
                size.x = Mathf.Max(1, selStartSize[0].x + dx);
            }
        }
        if (top)
        {
            int dy = Mathf.RoundToInt(-delta.y / stride.y);
            if (dy != 0)
            {
                g.posPx.y = selStartPos[0].y + dy * stride.y;
                size.y = Mathf.Max(1, selStartSize[0].y + dy);
            }
        }

        g.sizeCells    = size;
        g.overrideSize = true;
        g.sizeOverride = size;
    }

    void DeselectAll()
    {
        foreach (var g in grids) g.selected = false;
    }

    void StartDragSelection(Vector2 mouseContent)
    {
        dragOriginContent = mouseContent;
        selIdx = grids.Select((g, i) => (g, i)).Where(t => t.g.selected).Select(t => t.i).ToList();
        selStartPos = new Vector2[selIdx.Count];
        for (int k = 0; k < selIdx.Count; k++) selStartPos[k] = grids[selIdx[k]].posPx;
    }

    void HandleShortcuts()
    {
        var e = Event.current;
        if (e == null || e.type != EventType.KeyDown) return;

        if (e.control && e.keyCode == KeyCode.A)
        {
            foreach (var g in grids) g.selected = true;
            Repaint();
            e.Use();
        }
        if (e.keyCode == KeyCode.Escape)
        {
            DeselectAll();
            Repaint();
            e.Use();
        }

        Vector2 stride = SnapStride();
        Vector2 d = Vector2.zero;
        if (e.keyCode == KeyCode.LeftArrow)  d = new Vector2(-stride.x, 0);
        if (e.keyCode == KeyCode.RightArrow) d = new Vector2(+stride.x, 0);
        if (e.keyCode == KeyCode.UpArrow)    d = new Vector2(0, -stride.y);
        if (e.keyCode == KeyCode.DownArrow)  d = new Vector2(0, +stride.y);
        if (d != Vector2.zero)
        {
            foreach (var g in grids) if (g.selected) g.posPx = Vector2.Max(Vector2.zero, g.posPx + d);
            Repaint();
            e.Use();
        }

        if (e.control && e.keyCode == KeyCode.D)
        {
            DuplicateSelection();
            e.Use();
        }
        if (e.keyCode == KeyCode.Delete)
        {
            DeleteSelection();
            e.Use();
        }
    }

    // ================= Commands (Layout) =================
    void CreateGrid()
    {
        var id   = UniqueId("grid");
        var size = new Vector2Int(3, 3);
        var pos  = Vector2.zero;
        if (EditorUtility.DisplayDialog("New Grid", "Neues Grid anlegen?", "Create", "Cancel"))
        {
            grids.Add(new GridPreview
            {
                id           = id,
                sizeCells    = size,
                overrideSize = true,
                sizeOverride = size,
                posPx        = pos
            });
            Repaint();
        }
    }

    void DuplicateSelection()
    {
        var sel = grids.Where(g => g.selected).ToList();
        if (sel.Count == 0) return;
        GridPreview last = null;
        foreach (var g in sel)
        {
            var clone = new GridPreview
            {
                id           = UniqueId(g.id),
                sizeCells    = g.sizeCells,
                overrideSize = g.overrideSize,
                sizeOverride = g.sizeOverride,
                posPx        = g.posPx + new Vector2(12, 12),
                color        = g.color,
                framePx      = g.framePx
            };
            grids.Add(clone);
            last = clone;
        }
        DeselectAll();
        if (last != null) last.selected = true;
        Repaint();
    }

    void DeleteSelection()
    {
        if (grids.RemoveAll(g => g.selected) > 0)
            Repaint();
    }

    string UniqueId(string baseId)
    {
        if (string.IsNullOrWhiteSpace(baseId)) baseId = "grid";
        string id = baseId;
        int i = 1;
        while (grids.Any(x => x.id == id))
            id = $"{baseId}_{i++}";
        return id;
    }

    void AlignLeft()
    {
        var sel = grids.Where(g => g.selected).ToList();
        if (sel.Count < 2) return;
        float x = sel.Min(g => g.posPx.x);
        foreach (var g in sel) g.posPx.x = x;
        Repaint();
    }

    void AlignTop()
    {
        var sel = grids.Where(g => g.selected).ToList();
        if (sel.Count < 2) return;
        float y = sel.Min(g => g.posPx.y);
        foreach (var g in sel) g.posPx.y = y;
        Repaint();
    }

    void Distribute(bool horizontal)
    {
        var sel = grids.Where(g => g.selected).OrderBy(g => horizontal ? g.posPx.x : g.posPx.y).ToList();
        if (sel.Count < 3) return;
        float start = horizontal ? sel.First().posPx.x : sel.First().posPx.y;
        float end   = horizontal ? sel.Last().posPx.x  : sel.Last().posPx.y;
        float span  = Mathf.Max(1, end - start);
        for (int i = 1; i < sel.Count - 1; i++)
        {
            float t = (float)i / (sel.Count - 1);
            if (horizontal) sel[i].posPx.x = Mathf.Round(start + span * t);
            else            sel[i].posPx.y = Mathf.Round(start + span * t);
        }
        Repaint();
    }

    // ================= Load/Save (Layout) =================
    void LoadFromComponents()
    {
        grids.Clear();
        selIdx.Clear();
        selStartPos  = null;
        selStartSize = null;
        pan          = Vector2.zero;
        zoom         = 1f;

        // Skin
        cellSize  = layoutData.cellSize;
        cellPad   = layoutData.cellPadding;
        panelSize = layoutData.panelSize;

        // Layout
        foreach (var el in layoutData.layout)
        {
            var g = new GridPreview
            {
                id           = string.IsNullOrWhiteSpace(el.gridId) ? UniqueId("grid") : el.gridId,
                posPx        = el.posPxTL,
                color        = el.bgColor,
                framePx      = el.framePx,
                overrideSize = el.overrideSize,
                sizeOverride = el.sizeOverride,
            };
            g.sizeCells = g.overrideSize ? g.sizeOverride : GetSpecSize(multi, g.id);
            if (g.sizeCells.x < 1 || g.sizeCells.y < 1)
                g.sizeCells = new Vector2Int(1, 1);
            grids.Add(g);
        }

        // Seeds -> Vorschau spiegeln
        if (seedData != null)
        {
            foreach (var g in grids)
            {
                var sg = seedData.SeedGrids.Find(s => s.gridId == g.id);
                if (sg.items != null)
                {
                    g.initialItems = sg.items.ConvertAll(s => new SeedItem
                    {
                        def     = s.def,
                        amount  = s.amount,
                        pos     = s.pos,
                        rotated = s.rotated
                    });
                }
            }
        }

        Repaint();
    }

    void SaveToComponents()
    {
        if (!ValidateAndFixIds()) return;

        // Skin zurückschreiben
        layoutData.cellSize    = cellSize;
        layoutData.cellPadding = cellPad;
        layoutData.panelSize   = panelSize;

        // Layout schreiben
        layoutData.layout.Clear();
        foreach (var g in grids)
        {
            layoutData.layout.Add(new InventoryLayoutData.GridLayout
            {
                gridId      = g.id,
                posPxTL     = g.posPx,
                bgColor     = g.color,
                framePx     = g.framePx,
                overrideSize= g.overrideSize,
                sizeOverride= g.sizeOverride
            });
        }
        EditorUtility.SetDirty(layoutData);

        // Specs sync + optional prune
        SyncSpecs(pruneSpecsOnSave);

        // Seeds schreiben
        if (seedData != null)
        {
            seedData.Target = multi;
            seedData.SeedGrids.Clear();
            foreach (var g in grids)
            {
                var s = new InventorySeed.SeedGrid
                {
                    gridId = g.id,
                    items  = new List<InventorySeed.SeedItem>()
                };
                foreach (var it in g.initialItems)
                {
                    if (!it.def || it.amount <= 0) continue;
                    s.items.Add(new InventorySeed.SeedItem
                    {
                        def     = it.def,
                        amount  = it.amount,
                        pos     = it.pos,
                        rotated = it.rotated
                    });
                }
                seedData.SeedGrids.Add(s);
            }
            EditorUtility.SetDirty(seedData);
        }

        AssetDatabase.SaveAssets();
        ShowNotification(new GUIContent("Saved to components"));
    }

    void SyncSpecs(bool prune)
    {
        if (multi == null) return;

        var indexById = new Dictionary<string, int>();
        for (int i = 0; i < multi.gridsSpec.Count; i++)
        {
            var id = multi.gridsSpec[i].id;
            if (!string.IsNullOrWhiteSpace(id) && !indexById.ContainsKey(id))
                indexById[id] = i;
        }

        var idsInLayout = new HashSet<string>();
        foreach (var g in grids)
        {
            idsInLayout.Add(g.id);
            if (!indexById.TryGetValue(g.id, out var idx))
            {
                multi.gridsSpec.Add(new MultiInventory.GridSpec
                {
                    id                   = g.id,
                    size                 = g.sizeCells,
                    inventoryType        = MultiInventory.Type.Normal,
                    restrictToDefinition = false,
                    allowedDefinition    = new List<ItemDefinition>()
                });
            }
            else
            {
                var s = multi.gridsSpec[idx];
                s.size = g.sizeCells;
                multi.gridsSpec[idx] = s;
            }
        }

        if (prune)
            multi.gridsSpec.RemoveAll(s => string.IsNullOrWhiteSpace(s.id) || !idsInLayout.Contains(s.id));

        EditorUtility.SetDirty(multi);
    }

    void SaveInventoryPrefab()
    {
        if (!ValidateAndFixIds()) return;
        SaveToComponents();

        string path = EditorUtility.SaveFilePanelInProject(
            "Save Inventory Prefab",
            targetInventoryRoot.name,
            "prefab",
            "Choose save location for the inventory prefab.");
        if (string.IsNullOrEmpty(path)) return;

        var prefab = PrefabUtility.SaveAsPrefabAsset(targetInventoryRoot, path);
        if (prefab)
        {
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(prefab);
            ShowNotification(new GUIContent("Prefab saved"));
        }
        else
        {
            EditorUtility.DisplayDialog("Error", "SaveAsPrefabAsset failed.", "OK");
        }
    }

    // ================= Helpers (Layout) =================
    bool EnsureComponents(bool createIfMissing = false)
    {
        if (!targetInventoryRoot) return false;

        multi      = targetInventoryRoot.GetComponent<MultiInventory>();
        layoutData = targetInventoryRoot.GetComponent<InventoryLayoutData>();
        seedData   = targetInventoryRoot.GetComponent<InventorySeed>();
        crateInv   = targetInventoryRoot.GetComponent<CrateInventory>();

        if (!multi      && createIfMissing) multi      = targetInventoryRoot.AddComponent<MultiInventory>();
        if (!layoutData && createIfMissing) layoutData = targetInventoryRoot.AddComponent<InventoryLayoutData>();
        if (!seedData   && createIfMissing) seedData   = targetInventoryRoot.AddComponent<InventorySeed>();
        // CrateInventory wird nur auf Wunsch (Tab) angelegt, nicht automatisch

        if (!multi || !layoutData) return false;

        soMulti  = new SerializedObject(multi);
        soLayout = new SerializedObject(layoutData);
        soSeed   = seedData ? new SerializedObject(seedData) : null;
        soCrate  = crateInv ? new SerializedObject(crateInv) : null;

        // wenn CrateInventory existiert und kein Inventory zugewiesen hat, auf dieses setzen
        if (crateInv != null && crateInv.crate == null)
        {
            crateInv.crate = multi;
            EditorUtility.SetDirty(crateInv);
        }

        // lokale Skin-Werte aus Komponenten übernehmen (für Preview)
        cellSize  = layoutData.cellSize;
        cellPad   = layoutData.cellPadding;
        panelSize = layoutData.panelSize;

        return true;
    }

    void AddAllFromSpecs()
    {
        if (multi == null) return;
        grids.Clear();
        selIdx.Clear();

        float x = 8, y = 8;
        float spX = cellPad.x, spY = cellPad.y;
        float lineH = 0;
        float panelW = panelSize.x;

        foreach (var sp in multi.gridsSpec)
        {
            string id = string.IsNullOrEmpty(sp.id) ? UniqueId("grid") : sp.id;
            var size  = new Vector2Int(Mathf.Max(1, sp.size.x), Mathf.Max(1, sp.size.y));
            var px    = GridPxSize(size, cellSize, cellPad);
            if (x + px.x > panelW - 8f)
            {
                x = 8;
                y += lineH + spY;
                lineH = 0;
            }
            grids.Add(new GridPreview
            {
                id        = id,
                sizeCells = size,
                posPx     = new Vector2(x, y)
            });
            x += px.x + spX;
            lineH = Mathf.Max(lineH, px.y);
        }

        Repaint();
    }

    void AutoFitPanelSize()
    {
        if (grids.Count == 0) return;

        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;

        foreach (var g in grids)
        {
            var sz = GridPxSize(g.sizeCells, cellSize, cellPad);
            minX = Mathf.Min(minX, g.posPx.x);
            minY = Mathf.Min(minY, g.posPx.y);
            maxX = Mathf.Max(maxX, g.posPx.x + sz.x);
            maxY = Mathf.Max(maxY, g.posPx.y + sz.y);
        }

        if (minX == float.MaxValue || minY == float.MaxValue)
            return;

        // Alle Grids so verschieben, dass die Bounding-Box bei (Left/Top)-Padding startet
        float offsetX = panelPadding.x - minX;
        float offsetY = panelPadding.z - minY;
        foreach (var g in grids)
        {
            g.posPx += new Vector2(offsetX, offsetY);
        }

        // Panel-Size = Inhalt + Padding links/rechts/oben/unten
        float width  = (maxX - minX) + panelPadding.x + panelPadding.y;
        float height = (maxY - minY) + panelPadding.z + panelPadding.w;
        panelSize = new Vector2(Mathf.Max(width, 0f), Mathf.Max(height, 0f));

        Repaint();
    }

    bool ValidateAndFixIds()
    {
        // leere IDs reparieren
        for (int i = 0; i < grids.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(grids[i].id))
                grids[i].id = UniqueId("grid");
        }
        // Duplikate reparieren
        var seen = new HashSet<string>();
        for (int i = 0; i < grids.Count; i++)
        {
            var id = grids[i].id;
            if (!seen.Add(id))
                grids[i].id = UniqueId(id);
        }
        // Sizes clampen
        for (int i = 0; i < grids.Count; i++)
        {
            grids[i].sizeCells = new Vector2Int(Mathf.Max(1, grids[i].sizeCells.x), Mathf.Max(1, grids[i].sizeCells.y));
            if (grids[i].overrideSize) grids[i].sizeOverride = grids[i].sizeCells;
        }
        // InitialItems clampen
        foreach (var g in grids)
        {
            foreach (var it in g.initialItems)
            {
                if (!it.def) continue;
                var baseSize = it.def.BaseSize;
                var size     = it.rotated ? new Vector2Int(baseSize.y, baseSize.x) : baseSize;
                it.pos.x = Mathf.Clamp(it.pos.x, 0, Mathf.Max(0, g.sizeCells.x - size.x));
                it.pos.y = Mathf.Clamp(it.pos.y, 0, Mathf.Max(0, g.sizeCells.y - size.y));
            }
        }
        return true;
    }

    static Vector2Int GetSpecSize(MultiInventory inv, string id)
    {
        if (inv == null) return new Vector2Int(1, 1);
        foreach (var s in inv.gridsSpec)
            if (s.id == id)
                return new Vector2Int(Mathf.Max(1, s.size.x), Mathf.Max(1, s.size.y));
        return new Vector2Int(1, 1);
    }

    static Vector2 GridPxSize(Vector2Int cells, Vector2 cell, Vector2 pad)
    {
        float w = cells.x * cell.x + Mathf.Max(0, cells.x - 1) * pad.x;
        float h = cells.y * cell.y + Mathf.Max(0, cells.y - 1) * pad.y;
        return new Vector2(w, h);
    }

    Rect NormalizeRect(Rect r)
    {
        if (r.width < 0)
        {
            r.x += r.width;
            r.width = -r.width;
        }
        if (r.height < 0)
        {
            r.y += r.height;
            r.height = -r.height;
        }
        return r;
    }

    // ============================================================
    // ====================== LOOT TAB ============================
    // ============================================================
    void DrawLootGUI()
    {
        // zentrierte Spalte
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        EditorGUILayout.BeginVertical(GUILayout.MaxWidth(700));
        GUILayout.Space(4);

        targetInventoryRoot = (GameObject)EditorGUILayout.ObjectField(
            "Inventory Root",
            targetInventoryRoot,
            typeof(GameObject),
            true);

        if (!EnsureComponents())
        {
            EditorGUILayout.HelpBox("Wähle ein Inventory-Root mit MultiInventory, um den Loot-Tab zu nutzen.", MessageType.Info);
            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            return;
        }

        GUILayout.Space(8);
        EditorGUILayout.LabelField("LootTable für dieses Inventory", EditorStyles.boldLabel);

        float oldLabel = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = 180f;

        // LootTable Asset auswählen / erstellen
        EditorGUI.BeginChangeCheck();
        var lt = (LootTable)EditorGUILayout.ObjectField(
            new GUIContent("Loot Table Asset"),
            multi.lootTable,
            typeof(LootTable),
            false);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(multi, "Assign LootTable");
            multi.lootTable = lt;
            EditorUtility.SetDirty(multi);
        }

        if (multi.lootTable == null)
        {
            EditorGUILayout.HelpBox("Kein LootTable-Asset zugewiesen.", MessageType.Info);
            if (GUILayout.Button("Neue LootTable anlegen...", GUILayout.Height(22)))
            {
                string defaultName = (targetInventoryRoot != null ? targetInventoryRoot.name : "Inventory") + "_Loot";
                string path = EditorUtility.SaveFilePanelInProject(
                    "Create LootTable",
                    defaultName,
                    "asset",
                    "Wähle Pfad für neue LootTable.");
                if (!string.IsNullOrEmpty(path))
                {
                    var asset = ScriptableObject.CreateInstance<LootTable>();
                    AssetDatabase.CreateAsset(asset, path);
                    AssetDatabase.SaveAssets();
                    Undo.RecordObject(multi, "Assign LootTable");
                    multi.lootTable = asset;
                    EditorUtility.SetDirty(multi);
                }
            }

            EditorGUIUtility.labelWidth = oldLabel;
            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            return;
        }

        GUILayout.Space(6);
        DrawLootTableEditor(multi.lootTable);

        EditorGUIUtility.labelWidth = oldLabel;

        EditorGUILayout.EndVertical();
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
    }

    void DrawLootTableEditor(LootTable lt)
    {
        if (lt == null) return;

        float oldLabel = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = 190f;

        EditorGUI.BeginChangeCheck();

        lt.rolls = EditorGUILayout.IntField(
            new GUIContent("Max unterschiedliche Items (rolls, 0 = alle)"),
            lt.rolls);

        GUILayout.Space(4);
        EditorGUILayout.LabelField("Entries", EditorStyles.boldLabel);

        int removeIndex = -1;

        for (int i = 0; i < lt.entries.Count; i++)
        {
            var e = lt.entries[i];

            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.MaxWidth(680));

            using (new EditorGUILayout.HorizontalScope())
            {
                e.def = (ItemDefinition)EditorGUILayout.ObjectField(
                    new GUIContent("Definition"),
                    e.def,
                    typeof(ItemDefinition),
                    false);

                if (GUILayout.Button("✕", GUILayout.Width(22)))
                    removeIndex = i;
            }

            // Amount-Range kompakter darstellen
            EditorGUILayout.LabelField("Amount Range (total)");
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(10);
                e.amountRange.x = EditorGUILayout.IntField("Min", e.amountRange.x, GUILayout.MaxWidth(250));
                e.amountRange.y = EditorGUILayout.IntField("Max", e.amountRange.y, GUILayout.MaxWidth(250));
            }

            e.weight = EditorGUILayout.FloatField("Weight", e.weight);

            // Range normalisieren
            if (e.amountRange.x > e.amountRange.y)
            {
                var tmp = e.amountRange.x;
                e.amountRange.x = e.amountRange.y;
                e.amountRange.y = tmp;
            }

            lt.entries[i] = e;

            EditorGUILayout.EndVertical();
            GUILayout.Space(2);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Add Entry", GUILayout.Width(100)))
            {
                lt.entries.Add(new LootTable.Entry
                {
                    def         = null,
                    amountRange = new Vector2Int(1, 1),
                    weight      = 1f
                });
            }

            GUILayout.FlexibleSpace();
            if (lt.entries.Count > 0 && GUILayout.Button("Clear All", GUILayout.Width(90)))
            {
                lt.entries.Clear();
            }
        }

        if (removeIndex >= 0 && removeIndex < lt.entries.Count)
            lt.entries.RemoveAt(removeIndex);

        if (EditorGUI.EndChangeCheck())
        {
            EditorUtility.SetDirty(lt);
            AssetDatabase.SaveAssets();
        }

        GUILayout.Space(6);
        EditorGUILayout.HelpBox(
            "LootTable.Fill(MultiInventory, seed) nutzt:\n" +
            "- 'rolls' = wie viele unterschiedliche Item-Typen max. gezogen werden (0 = alle)\n" +
            "- amountRange = Gesamtmenge pro Item-Typ (wird bei Bedarf in mehrere Stacks gesplittet)",
            MessageType.None);

        EditorGUIUtility.labelWidth = oldLabel;
    }

    // ============================================================
    // ====================== ITEMS TAB ===========================
    // ============================================================
    void DrawItemsGUI()
    {
        // zentrierte Spalte
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        EditorGUILayout.BeginVertical(GUILayout.MaxWidth(700));
        GUILayout.Space(4);
        EditorGUILayout.LabelField("Item Definitions", MidTitle);
        GUILayout.Space(4);

        float oldLabel = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = 150f;

        // Create-Box
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Neues ItemDefinition-Asset erstellen", EditorStyles.boldLabel);

            itemFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                new GUIContent("Ziel-Ordner"),
                itemFolder,
                typeof(DefaultAsset),
                false);

            newItemName     = EditorGUILayout.TextField("Name", newItemName);
            newItemIcon     = (Sprite)EditorGUILayout.ObjectField("Icon", newItemIcon, typeof(Sprite), false);
            newItemBaseSize = EditorGUILayout.Vector2IntField("Base Size", newItemBaseSize);
            newItemMaxStack = Mathf.Max(1, EditorGUILayout.IntField("Max Stack", newItemMaxStack));
            

            GUILayout.Space(4);
            if (GUILayout.Button("ItemDefinition Asset erstellen", GUILayout.Height(22)))
            {
                CreateItemDefinitionAsset();
            }
        }

        GUILayout.Space(8);
        EditorGUILayout.LabelField("Vorhandene Items (t:ItemDefinition)", EditorStyles.boldLabel);

        using (var scroll = new EditorGUILayout.ScrollViewScope(itemScrollPos))
        {
            itemScrollPos = scroll.scrollPosition;

            string[] guids = AssetDatabase.FindAssets("t:ItemDefinition");
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
                if (!item) continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.ObjectField(item, typeof(ItemDefinition), false, GUILayout.MaxWidth(260));
                    GUILayout.Space(8);
                    GUILayout.Label(path, EditorStyles.miniLabel);
                }
            }
        }

        EditorGUIUtility.labelWidth = oldLabel;

        EditorGUILayout.EndVertical();
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
    }

    void CreateItemDefinitionAsset()
    {
        string basePath = "Assets";
        if (itemFolder != null)
        {
            var fp = AssetDatabase.GetAssetPath(itemFolder);
            if (!string.IsNullOrEmpty(fp) && AssetDatabase.IsValidFolder(fp))
                basePath = fp;
        }

        string name = string.IsNullOrWhiteSpace(newItemName) ? "NewItem" : newItemName.Trim();
        string path = System.IO.Path.Combine(basePath, name + ".asset");
        path = AssetDatabase.GenerateUniqueAssetPath(path);

        var item = ScriptableObject.CreateInstance<ItemDefinition>();

        // Per SerializedObject auf gängige Felder schreiben, falls vorhanden
        var so = new SerializedObject(item);

        var iconProp = so.FindProperty("Icon");
        if (iconProp != null)
            iconProp.objectReferenceValue = newItemIcon;

        var baseSizeProp = so.FindProperty("BaseSize");
        if (baseSizeProp != null)
            baseSizeProp.vector2IntValue = newItemBaseSize;

        var maxStackProp = so.FindProperty("MaxStack");
        if (maxStackProp != null)
            maxStackProp.intValue = newItemMaxStack;
        
        var itemTypeProp = so.FindProperty("Type");
        if (itemTypeProp != null)
            itemTypeProp.enumValueIndex = newItemType;

        var displayNameProp = so.FindProperty("DisplayName");
        if (displayNameProp != null)
            displayNameProp.stringValue = name;

        so.ApplyModifiedPropertiesWithoutUndo();

        AssetDatabase.CreateAsset(item, path);
        AssetDatabase.SaveAssets();
        EditorGUIUtility.PingObject(item);
    }
}
#endif
