// Fur Grooming Tool - three-mode UV painter for liltoon fur.
//   Direction : comb fur flow + tilt -> tangent-space normal map
//   Length    : soft/gradient grayscale -> fur length mask
//   Alpha     : hard black/white       -> fur presence mask
//
// Open from: Tools -> Fur Grooming Tool
// See README.md in the package root for install/usage.

using System.IO;
using UnityEditor;
using UnityEngine;

namespace FurGroomingTool
{
    public class FurGroomingWindow : EditorWindow
    {
        const int FN = 256;   // direction field resolution
        const int MN = 512;   // mask buffer resolution
        const float REF = 512f;

        enum Tab { Direction, Length, Alpha, Collision }
        enum DirMode { DirStrength, Direction, Strength, Pinch, Erase }
        enum LenMode { Paint, Smudge, Gradient }
        enum AlphaMode { White, Black }
        enum MirrorDir { LeftToRight, RightToLeft, TopToBottom, BottomToTop }
        enum MarkerShape { Sphere, Cube, Disc, Cross }

        // ---- direction layer
        [SerializeField] Vector2[] dir;
        [SerializeField] float[] dirStr;
        [SerializeField] DirMode dirMode = DirMode.DirStrength;
        [SerializeField] float furStrength = 0.8f;
        [SerializeField] float maxAngle = 70f;
        [SerializeField] bool flipG = true;
        [SerializeField] Color arrowColor = Color.magenta;
        [SerializeField] bool showArrows = true;

        // ---- length layer
        [SerializeField] MaskLayerStack lenStack = new MaskLayerStack();
        [SerializeField] LenMode lenMode = LenMode.Paint;
        [SerializeField] float lenValue = 1f;

        // ---- alpha layer
        [SerializeField] MaskLayerStack alphaStack = new MaskLayerStack();
        [SerializeField] AlphaMode alphaMode = AlphaMode.White;
        [SerializeField] float alphaThreshold = 0.5f;
        [SerializeField] bool alphaAA = true;

        // ---- shared
        [SerializeField] Tab tab = Tab.Direction;
        [SerializeField] Texture2D bg;
        [SerializeField] Material targetMat;
        [SerializeField] Renderer targetRenderer;
        [SerializeField] int uvChannel = 0;
        [SerializeField] Texture2D uvBackground;
        [SerializeField] bool showOnMesh = false;
        [SerializeField] MarkerShape markerShape = MarkerShape.Sphere;
        [SerializeField] Color markerColor = new Color(1f, 0.7f, 0.2f, 1f);
        [SerializeField] float markerSize = 0.06f;
        [SerializeField] bool followCam = false;
        [SerializeField] bool alignToNormal = false;
        [SerializeField] float camDistance = 0.35f;
        [SerializeField] float fov = 60f;
        [SerializeField] string normalProp = "_FurVectorTex";
        [SerializeField] string lengthProp = "_FurLengthMask";
        [SerializeField] string alphaProp = "_FurMask";
        [SerializeField] int brushSize = 60;
        [SerializeField] float brushFlow = 0.6f;
        [SerializeField] float brushHardness = 0.35f;
        [SerializeField] float bgOpacity = 0.6f;
        [SerializeField] float smoothRadius = 4f;
        [SerializeField] int exportRes = 1024;
        [SerializeField] string outputFolder = "Assets";
        [SerializeField] string baseName = "Fur";
        [SerializeField] MirrorDir mirrorDir = MirrorDir.LeftToRight;
        [SerializeField] float symAxis = 0.5f;
        [SerializeField] float zoom = 1f;
        [SerializeField] Vector2 panCenter = new Vector2(0.5f, 0.5f);

        // ---- collision resolver
        [SerializeField] System.Collections.Generic.List<Renderer> clothing = new System.Collections.Generic.List<Renderer>();
        [SerializeField] FurOcclusionSettings occl = new FurOcclusionSettings();
        [SerializeField] bool debugDraw = true, debugChangedOnly = true;
        [SerializeField] int debugBudget = 600;
        [SerializeField] string resolveMsg = "";
        [SerializeField] Vector2[] preDir; [SerializeField] float[] preDirStr;
        [SerializeField] MaskLayerStack preLenStack, preAlphaStack;
        [SerializeField] bool hasPreResolve;

        // ---- runtime
        readonly System.Collections.Generic.List<FurHairDebug> resolveDebug = new System.Collections.Generic.List<FurHairDebug>();
        FurCoverage coverage;
        Texture2D coverageTex, normalMaskTex;
        Vector2 collideScroll;
        Texture2D normalPreview, lenTex, alphaTex;
        bool maskDirty = true;
        bool painting, panning, paintErase, forceHoleErase;
        Vector2 lastUv, lastDir = Vector2.right;
        Vector2 gradStart, gradEnd;
        bool gradActive;
        float canvasSize = REF;
        Vector3[] meshVerts; Vector2[] meshUVs; Vector3[] meshNorms; int[] meshTris; bool meshSkinned;
        readonly System.Collections.Generic.List<Vector3> sceneHits = new System.Collections.Generic.List<Vector3>();
        readonly System.Collections.Generic.List<Vector3> sceneNormals = new System.Collections.Generic.List<Vector3>();
        [SerializeField] bool foldBrush = true, foldTool = true, foldLayers = true, foldSym = false, foldExport = false, foldScene = false;
        Vector2 settingsScroll;

        // Undo grain: a brush stroke or a whole-canvas op touches one layer, so only that
        // layer's two channels are kept. Ops that change the stack itself (mirror, resolve,
        // load, add/delete/reorder) keep a deep clone instead.
        enum Cap { None, Layer, Whole }
        class StackSnap { public int index = -1; public float[] value; public byte[] cover; public MaskLayerStack whole; }
        class Snapshot { public Vector2[] dir; public float[] dirStr; public StackSnap len, alpha; }
        const int MaxUndo = 30;
        readonly System.Collections.Generic.List<Snapshot> undoStack = new System.Collections.Generic.List<Snapshot>();
        readonly System.Collections.Generic.List<Snapshot> redoStack = new System.Collections.Generic.List<Snapshot>();

        [MenuItem("Tools/Fur Grooming Tool")]
        static void Open() => GetWindow<FurGroomingWindow>("Fur Grooming");

        void OnEnable()
        {
            wantsMouseMove = true;
            minSize = new Vector2(620, 720);
            if (dir == null || dir.Length != FN * FN) dir = new Vector2[FN * FN];
            if (dirStr == null || dirStr.Length != FN * FN) dirStr = new float[FN * FN];
            if (lenStack == null) lenStack = new MaskLayerStack();
            if (alphaStack == null) alphaStack = new MaskLayerStack();
            lenStack.Ensure(MN * MN, "Base");
            alphaStack.Ensure(MN * MN, "Base");
            if (clothing == null) clothing = new System.Collections.Generic.List<Renderer>();
            if (occl == null) occl = new FurOcclusionSettings();
            maskDirty = true;
            SceneView.duringSceneGui += OnSceneGUI;
            if (targetRenderer != null) CacheMesh();
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
        }

        // ---- layer access
        // Everything downstream of painting - preview, export, the resolver's input and the
        // groom that is saved - reads a composite, so what is shown is what is written.

        MaskLayerStack ActiveStack() { return tab == Tab.Alpha ? alphaStack : lenStack; }
        MaskLayer ActiveLayer() { return ActiveStack().Active; }
        float[] CompositeLen() { return lenStack.Composite(); }
        float[] CompositeAlpha() { return alphaStack.Composite(); }

        // What right-drag does. On a layer with something under it, cutting a hole is almost
        // never what is wanted: the hole shows the layer below, and if that one is painted too
        // the erase reads as nothing at all. So above the bottom layer right-drag lays down a
        // black mark instead - the same result as running 'Holes -> marks' on the stroke, but
        // applied as you drag, so the canvas does not flip when the button comes up.
        // The bottom layer has nothing underneath, where a hole and a black mark composite
        // identically, so it keeps the true erase. Hold Shift to force a real hole anywhere.
        bool EraseCutsHole(MaskLayerStack st) { return forceHoleErase || st.active == 0; }

        // True when the tab paints into a layer stack and that layer refuses edits.
        bool ActiveLayerLocked()
        {
            if (tab != Tab.Length && tab != Tab.Alpha) return false;
            MaskLayer L = ActiveLayer();
            return L != null && L.locked;
        }

        // =========================================================== GUI

        void OnGUI()
        {
            HandleShortcuts();
            Tab nt = (Tab)GUILayout.Toolbar((int)tab, new[] { "Direction", "Length", "Alpha", "Collision" });
            if (nt != tab) { tab = nt; maskDirty = true; }

            if (tab == Tab.Collision) { DrawCollisionTab(); return; }

            float maxSettings = Mathf.Min(position.height * 0.5f, 400f);
            settingsScroll = EditorGUILayout.BeginScrollView(settingsScroll, GUILayout.MaxHeight(maxSettings));
            DrawSettings();
            EditorGUILayout.EndScrollView();

            DrawActions();
            EditorGUILayout.Space(4);
            ClampView();

            // Grab all remaining window space and lay the big paint canvas in it,
            // with the output preview as a thumbnail column on the right.
            Rect area = GUILayoutUtility.GetRect(10, 10, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            const float pad = 8f, capH = 16f;
            float previewW = Mathf.Clamp(area.width * 0.22f, 160f, 320f);
            float availW = area.width - previewW - pad;
            float availH = area.height - capH;
            float paintSize = Mathf.Max(200f, Mathf.Min(availW, availH));
            canvasSize = paintSize;

            Rect paint = new Rect(area.x, area.y, paintSize, paintSize);
            float pv = Mathf.Min(previewW, paintSize);
            Rect prev = new Rect(paint.xMax + pad, area.y, pv, pv);

            if (Event.current.type == EventType.Repaint && maskDirty) RebuildMaskTex();

            DrawCanvas(paint);
            DrawPreview(prev);
            GUI.Label(new Rect(paint.x, paint.yMax + 1, paintSize, capH), "Paint  (" + tab + ")", EditorStyles.miniLabel);
            GUI.Label(new Rect(prev.x, prev.yMax + 1, pv, capH), "Output preview", EditorStyles.miniLabel);

            HandleInput(paint);
        }

        static bool Foldout(bool state, string title) => EditorGUILayout.Foldout(state, title, true);

        void DrawSettings()
        {
            foldBrush = Foldout(foldBrush, "Canvas & brush");
            if (foldBrush)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.BeginHorizontal();
                bg = (Texture2D)EditorGUILayout.ObjectField("Background", bg, typeof(Texture2D), false);
                if (ColorButton("Clear layer", colRed, GUILayout.Width(90))) ClearLayer();
                EditorGUILayout.EndHorizontal();
                targetMat = (Material)EditorGUILayout.ObjectField("Target material", targetMat, typeof(Material), false);
                EditorGUILayout.BeginHorizontal();
                Renderer rendNew = (Renderer)EditorGUILayout.ObjectField(new GUIContent("Mesh (renderer)", "The exact mesh to read UVs from and to highlight in the Scene view. Drag the avatar's mesh object from the Hierarchy."), targetRenderer, typeof(Renderer), true);
                if (rendNew != targetRenderer) SetTargetRenderer(rendNew);
                if (ColorButton("Refresh UV", colTeal, GUILayout.Width(90))) GenerateUvBackground();
                EditorGUILayout.EndHorizontal();
                DrawUvChannelSelector();
                brushSize = EditorGUILayout.IntSlider("Brush size", brushSize, 2, 200);
                brushFlow = EditorGUILayout.Slider("Brush flow", brushFlow, 0.02f, 1f);
                bgOpacity = EditorGUILayout.Slider("BG opacity", bgOpacity, 0f, 1f);
                EditorGUILayout.BeginHorizontal();
                zoom = EditorGUILayout.Slider("Zoom", zoom, 1f, 16f);
                if (ColorButton("Reset view", colGray, GUILayout.Width(90))) { zoom = 1f; panCenter = new Vector2(0.5f, 0.5f); }
                EditorGUILayout.EndHorizontal();
                EditorGUI.indentLevel--;
            }

            foldTool = Foldout(foldTool, "Tool  -  " + tab);
            if (foldTool)
            {
                EditorGUI.indentLevel++;
                switch (tab)
                {
                    case Tab.Direction: DrawDirectionControls(); break;
                    case Tab.Length: DrawLengthControls(); break;
                    case Tab.Alpha: DrawAlphaControls(); break;
                }
                EditorGUI.indentLevel--;
            }

            if (tab == Tab.Length || tab == Tab.Alpha)
            {
                foldLayers = Foldout(foldLayers, "Layers  -  " + tab);
                if (foldLayers) { EditorGUI.indentLevel++; DrawLayerPanel(ActiveStack()); EditorGUI.indentLevel--; }
            }

            foldSym = Foldout(foldSym, "Symmetry / mirror");
            if (foldSym)
            {
                EditorGUI.indentLevel++;
                mirrorDir = (MirrorDir)EditorGUILayout.EnumPopup("Mirror direction", mirrorDir);
                symAxis = EditorGUILayout.Slider("Axis", symAxis, 0f, 1f);
                if (ColorButton("Apply mirror", colPurple, GUILayout.Width(120))) ApplyMirror();
                EditorGUI.indentLevel--;
            }

            foldExport = Foldout(foldExport, "Export");
            if (foldExport)
            {
                EditorGUI.indentLevel++;
                exportRes = EditorGUILayout.IntPopup("Resolution", exportRes,
                    new[] { "512", "1024", "2048", "4096" }, new[] { 512, 1024, 2048, 4096 });
                EditorGUILayout.BeginHorizontal();
                outputFolder = EditorGUILayout.TextField(new GUIContent("Output folder", "Maps save here (inside Assets) and overwrite existing files. Leave blank to be asked each time."), outputFolder);
                if (ColorButton("Browse", colGray, GUILayout.Width(70))) BrowseOutputFolder();
                EditorGUILayout.EndHorizontal();
                baseName = EditorGUILayout.TextField(new GUIContent("File base name", "Saved as <base>_Normal / _Length / _Alpha .png"), baseName);
                EditorGUI.indentLevel--;
            }

            foldScene = Foldout(foldScene, "Scene view preview");
            if (foldScene)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.BeginHorizontal();
                showOnMesh = EditorGUILayout.ToggleLeft("Show on mesh", showOnMesh, GUILayout.Width(120));
                if (ColorButton("Refresh mesh", colGray, GUILayout.Width(100))) CacheMesh();
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField("Mesh is set in 'Canvas & brush'.", EditorStyles.miniLabel);
                if (showOnMesh)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Marker", GUILayout.Width(45));
                    markerShape = (MarkerShape)EditorGUILayout.EnumPopup(markerShape, GUILayout.Width(80));
                    markerColor = EditorGUILayout.ColorField(markerColor, GUILayout.Width(60));
                    markerSize = EditorGUILayout.Slider(markerSize, 0.01f, 0.3f);
                    EditorGUILayout.EndHorizontal();
                    followCam = EditorGUILayout.ToggleLeft("Camera follows brush (smoothed)", followCam);
                    if (followCam)
                    {
                        EditorGUI.indentLevel++;
                        alignToNormal = EditorGUILayout.ToggleLeft("Align to surface normal", alignToNormal);
                        camDistance = EditorGUILayout.Slider("Distance (zoom)", camDistance, 0.02f, 3f);
                        fov = EditorGUILayout.Slider("Field of view", fov, 10f, 90f);
                        EditorGUI.indentLevel--;
                    }
                }
                EditorGUI.indentLevel--;
            }
        }

        void DrawActions()
        {
            string saveLabel = tab == Tab.Direction ? "Save normal map" : tab == Tab.Length ? "Save length mask" : "Save alpha mask";
            EditorGUILayout.BeginHorizontal();
            DrawUndoButtons();
            if (tab == Tab.Direction && ColorButton("Generate preview", colAmber)) normalPreview = BuildNormalTex(Mathf.Min(exportRes, 1024));
            if (ColorButton(saveLabel, colGreen)) SaveCurrent();
            if (tab == Tab.Direction)
            {
                if (ColorButton("Save groom", colBlue)) SaveGroom();
                if (ColorButton("Load groom", colTeal)) LoadGroom();
            }
            else if (ColorButton("Load mask", colTeal)) LoadMask(ActiveStack());
            EditorGUILayout.EndHorizontal();
        }

        void DrawDirectionControls()
        {
            dirMode = (DirMode)GUILayout.Toolbar((int)dirMode,
                new[] { "Dir + Str", "Direction", "Strength", "Pinch", "Erase" });
            furStrength = EditorGUILayout.Slider("Fur strength", furStrength, 0.02f, 1f);
            maxAngle = EditorGUILayout.Slider("Max tilt angle", maxAngle, 5f, 90f);
            EditorGUILayout.BeginHorizontal();
            showArrows = EditorGUILayout.ToggleLeft("Arrows", showArrows, GUILayout.Width(80));
            arrowColor = EditorGUILayout.ColorField(arrowColor, GUILayout.Width(60));
            flipG = EditorGUILayout.ToggleLeft("Flip G (Unity/OpenGL)", flipG, GUILayout.Width(180));
            EditorGUILayout.LabelField("Target: " + normalProp, GUILayout.Width(180));
            EditorGUILayout.EndHorizontal();
            normalProp = EditorGUILayout.TextField("Normal property", normalProp);
        }

        void DrawLengthControls()
        {
            lenMode = (LenMode)GUILayout.Toolbar((int)lenMode, new[] { "Paint", "Smudge", "Gradient" });
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(ActiveLayerLocked()))
            {
                if (ColorButton(new GUIContent("Fill white", "Fills the selected layer with full length, edge to edge."),
                                new Color(0.95f, 0.95f, 0.95f))) FillActiveLayer(1f);
                if (ColorButton(new GUIContent("Fill black", "Fills the selected layer with zero length, edge to edge."),
                                new Color(0.45f, 0.45f, 0.45f))) FillActiveLayer(0f);
            }
            EditorGUILayout.EndHorizontal();
            lenValue = EditorGUILayout.Slider(new GUIContent("Length value", "Left-drag paints this value. Right-drag paints 0 as a mark, so it shows over the layers below; on the bottom layer, and with Shift held, it cuts a real hole instead."), lenValue, 0f, 1f);
            brushHardness = EditorGUILayout.Slider("Brush hardness", brushHardness, 0f, 1f);
            EditorGUILayout.BeginHorizontal();
            smoothRadius = EditorGUILayout.Slider("Smooth radius", smoothRadius, 1f, 24f);
            using (new EditorGUI.DisabledScope(ActiveLayerLocked()))
                if (ColorButton("Smooth layer", colAmber, GUILayout.Width(100))) SmoothActiveLayer();
            EditorGUILayout.EndHorizontal();
            lengthProp = EditorGUILayout.TextField("Length property", lengthProp);
        }

        void DrawAlphaControls()
        {
            alphaMode = (AlphaMode)GUILayout.Toolbar((int)alphaMode, new[] { "Paint white (fur)", "Paint black (bald)" });
            EditorGUILayout.LabelField("Right-drag marks bald; on the bottom layer, and with Shift, it erases.", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(ActiveLayerLocked()))
            {
                if (ColorButton("Fill white", new Color(0.95f, 0.95f, 0.95f))) FillActiveLayer(1f);
                if (ColorButton("Fill black", new Color(0.45f, 0.45f, 0.45f))) FillActiveLayer(0f);
            }
            EditorGUILayout.EndHorizontal();
            alphaThreshold = EditorGUILayout.Slider("Threshold", alphaThreshold, 0.05f, 0.95f);
            alphaAA = EditorGUILayout.ToggleLeft("Soft 1px edge (anti-alias)", alphaAA);
            alphaProp = EditorGUILayout.TextField("Alpha property", alphaProp);
        }

        // ---- layer panel (Length and Alpha tabs)
        //
        // Rows are drawn top of stack first, the way a layer palette reads. Visibility,
        // opacity, blend and name are not pushed onto the undo stack: they are one click
        // to put back, and snapshotting a whole stack per slider frame would be wasteful.
        void DrawLayerPanel(MaskLayerStack st)
        {
            int size = MN * MN;

            // The rows lay themselves out edge to edge, so the settings pane's indent has to
            // come off first: IMGUI shifts each control by the indent without shrinking the
            // width it reserved, which walks the leading controls on top of each other.
            int indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            float pad = indent * 15f;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(pad);
            if (ColorButton("Add", colGreen, GUILayout.Width(48)))
            { PushUndoStackStructure(); st.Add("Layer " + (st.Count + 1), size); maskDirty = true; }
            if (ColorButton("Duplicate", colTeal, GUILayout.Width(78)))
            { PushUndoStackStructure(); st.Duplicate(st.active); maskDirty = true; }
            using (new EditorGUI.DisabledScope(!st.CanDelete(st.active)))
                if (ColorButton(new GUIContent("Delete",
                    "Removes the selected layer. The bottom layer is the foundation the mask composites onto, " +
                    "so it cannot be deleted - move another layer below it first if you really want it gone."),
                    colRed, GUILayout.Width(60)))
                { PushUndoStackStructure(); st.Delete(st.active); maskDirty = true; }
            using (new EditorGUI.DisabledScope(st.active >= st.Count - 1))
                if (ColorButton("Up", colGray, GUILayout.Width(40)))
                { PushUndoStackStructure(); st.Move(st.active, 1); maskDirty = true; }
            using (new EditorGUI.DisabledScope(st.active <= 0))
                if (ColorButton("Down", colGray, GUILayout.Width(48)))
                { PushUndoStackStructure(); st.Move(st.active, -1); maskDirty = true; }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(pad);
            using (new EditorGUI.DisabledScope(ActiveLayerLocked() || st.active == 0))
                if (ColorButton(new GUIContent("Holes -> marks",
                    "Turns this layer inside out: it keeps only the parts you erased, as black marks, and goes " +
                    "transparent everywhere else. Use it on a filled layer whose erased holes vanish as soon as " +
                    "another filled layer is shown.\n\nNot available on the bottom layer - inverting that one " +
                    "throws away the fill everything else sits on. Move it up first if you really mean to."),
                    colPurple, GUILayout.Width(110)))
                    HolesToMarks(st);
            using (new EditorGUI.DisabledScope(VisibleCount(st) < 2))
                if (ColorButton(new GUIContent("Merge shown",
                    "Flattens every shown layer into one, exactly as the mask composites today. Hidden layers are kept."),
                    colGray, GUILayout.Width(100)))
                    MergeVisible(st);
            EditorGUILayout.EndHorizontal();

            st.Composite();   // cached; refreshes each layer's `solid` flag for the hint below

            // Two per-row controls that are easy to mistake for one another, so they are
            // spelled out: a checkbox for "is this layer shown", a radio for "is this the
            // layer the brush paints into". Exactly one layer can be the paint target.
            const float wShow = 24f, wPaint = 34f, wBlend = 84f, wOpacity = 110f;
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(pad);
            GUILayout.Label(new GUIContent("Show", "Tick to include this layer in the mask."),
                            EditorStyles.miniLabel, GUILayout.Width(wShow + 6f));
            GUILayout.Label(new GUIContent("Paint", "The layer the brush writes into."),
                            EditorStyles.miniLabel, GUILayout.Width(wPaint));
            GUILayout.Label("Layer", EditorStyles.miniLabel, GUILayout.MinWidth(60));
            GUILayout.Label("Blend", EditorStyles.miniLabel, GUILayout.Width(wBlend));
            GUILayout.Label("Opacity", EditorStyles.miniLabel, GUILayout.MinWidth(wOpacity));
            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginChangeCheck();
            for (int i = st.Count - 1; i >= 0; i--)
            {
                MaskLayer L = st.At(i);
                bool sel = i == st.active;

                Rect row = EditorGUILayout.BeginHorizontal();
                if (sel && Event.current.type == EventType.Repaint)
                    EditorGUI.DrawRect(row, new Color(0.35f, 0.5f, 0.8f, 0.22f));
                GUILayout.Space(pad);

                L.visible = EditorGUILayout.Toggle(L.visible, GUILayout.Width(wShow));   // no label: it would claim the prefix column
                GUILayout.Space(6);
                if (GUILayout.Toggle(sel, GUIContent.none, EditorStyles.radioButton, GUILayout.Width(wPaint)) && !sel)
                { st.active = i; GUI.FocusControl(null); }

                if (L.locked)
                    EditorGUILayout.LabelField(new GUIContent(L.name, "Generated layer - regenerate it instead of painting on it."),
                                               EditorStyles.boldLabel, GUILayout.MinWidth(60));
                else
                    L.name = EditorGUILayout.TextField(L.name, GUILayout.MinWidth(60));
                L.blend = (MaskBlend)EditorGUILayout.EnumPopup(L.blend, GUILayout.Width(wBlend));
                L.opacity = EditorGUILayout.Slider(L.opacity, 0f, 1f, GUILayout.MinWidth(wOpacity));
                EditorGUILayout.EndHorizontal();
            }
            if (EditorGUI.EndChangeCheck()) { st.MarkDirty(); maskDirty = true; Repaint(); }

            EditorGUI.indentLevel = indent;

            if (!AnyVisible(st))
                EditorGUILayout.HelpBox("Every layer is hidden, so this mask is empty - the canvas, the preview " +
                    "and the exported PNG will all be black. Tick 'Show' on the layers you want.", MessageType.Warning);
            else
            {
                int blocker = SolidBlocker(st);
                if (blocker >= 0)
                {
                    EditorGUILayout.HelpBox("'" + st.At(blocker).name + "' is painted over the whole canvas, so no " +
                        "layer below it can show. Erasing (right-drag) only cuts a hole in this layer - the hole shows " +
                        "the layer underneath, which here is painted too, so the two cancel out. " +
                        "'Holes -> marks' turns this layer into stackable marks.", MessageType.Warning);
                    if (ColorButton(new GUIContent("Turn '" + st.At(blocker).name + "' into marks",
                        "Keeps only what you erased out of this layer, as marks, so every layer shows at once."),
                        colPurple))
                    { st.active = blocker; HolesToMarks(st); }
                }
                else if (ActiveLayerLocked())
                    EditorGUILayout.HelpBox("'" + st.Active.name + "' is generated by the collision resolver. " +
                        "Select another layer to paint, or hide this one to see the groom without it.", MessageType.Info);
            }
        }

        static bool AnyVisible(MaskLayerStack st)
        {
            for (int i = 0; i < st.Count; i++)
            {
                MaskLayer L = st.At(i);
                if (L.visible && L.opacity > 0f) return true;
            }
            return false;
        }

        // An erased hole shows the layer underneath. On a layer painted edge to edge there is
        // nothing under the hole but the next filled layer, so two such layers plug each
        // other's holes and neither shows. This turns the layer inside out - it keeps only the
        // erased parts, as black marks, and goes transparent everywhere else - so the marks
        // from every layer stack up instead of cancelling.
        void HolesToMarks(MaskLayerStack st)
        {
            MaskLayer L = st.Active;
            if (L == null || L.locked) return;
            if (st.active == 0) return;   // the bottom layer is the fill everything sits on
            PushUndoActive();
            for (int i = 0; i < L.value.Length; i++)
            {
                L.cover[i] = (byte)(255 - L.cover[i]);
                L.value[i] = 0f;
            }
            L.blend = MaskBlend.Normal;
            L.opacity = Mathf.Max(L.opacity, 0.0001f);
            st.MarkDirty(); maskDirty = true; Repaint();
        }

        // Flatten the shown layers into one, exactly as the mask composites today. Hidden
        // layers are left where they are.
        void MergeVisible(MaskLayerStack st)
        {
            int lowest = -1;
            for (int i = 0; i < st.Count; i++)
                if (st.At(i).visible && st.At(i).opacity > 0f) { lowest = i; break; }
            if (lowest < 0 || VisibleCount(st) < 2) return;

            PushUndoStackStructure();
            st.MarkDirty();                 // never flatten a stale cache
            float[] flat = st.Composite();
            var merged = new MaskLayer("Merged", flat.Length);
            System.Array.Copy(flat, merged.value, flat.Length);
            for (int i = 0; i < merged.cover.Length; i++) merged.cover[i] = 255;

            for (int i = st.Count - 1; i >= 0; i--)
                if (st.At(i).visible && st.At(i).opacity > 0f) st.layers.RemoveAt(i);
            st.layers.Insert(Mathf.Clamp(lowest, 0, st.Count), merged);
            st.active = st.layers.IndexOf(merged);
            st.MarkDirty(); maskDirty = true; Repaint();
        }

        static int VisibleCount(MaskLayerStack st)
        {
            int n = 0;
            for (int i = 0; i < st.Count; i++) if (st.At(i).visible && st.At(i).opacity > 0f) n++;
            return n;
        }

        // The lowest solid layer that has a visible layer underneath it doing nothing.
        static int SolidBlocker(MaskLayerStack st)
        {
            for (int i = 1; i < st.Count; i++)
            {
                if (!st.At(i).solid) continue;
                for (int j = i - 1; j >= 0; j--)
                {
                    MaskLayer below = st.At(j);
                    if (below.visible && below.opacity > 0f) return i;
                }
            }
            return -1;
        }

        // =========================================================== collision tab

        // Self-contained: only what the resolver needs, and the three maps it writes.
        // No brush, no canvas, no export settings.
        void DrawCollisionTab()
        {
            float listH = Mathf.Min(position.height * 0.55f, 460f);
            collideScroll = EditorGUILayout.BeginScrollView(collideScroll, GUILayout.MaxHeight(listH));
            DrawResolverSettings();
            EditorGUILayout.EndScrollView();

            EditorGUILayout.BeginHorizontal();
            DrawUndoButtons();
            if (ColorButton("Resolve", colGreen)) RunResolve();
            using (new EditorGUI.DisabledScope(!hasPreResolve))
                if (ColorButton("Revert", colRed, GUILayout.Width(80))) RevertResolve();
            EditorGUILayout.EndHorizontal();

            DrawResolverLayerControls();

            EditorGUILayout.BeginHorizontal();
            if (ColorButton("Save normal map", colBlue)) SaveMap(Tab.Direction);
            if (ColorButton("Save length mask", colBlue)) SaveMap(Tab.Length);
            if (ColorButton("Save alpha mask", colBlue)) SaveMap(Tab.Alpha);
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(resolveMsg))
                EditorGUILayout.LabelField(resolveMsg, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(4);
            DrawResolverPreviews();
        }

        void DrawResolverSettings()
        {
            EditorGUILayout.LabelField(
                "Measures how much of the sky above each spot on the body the outfit blocks, then leans the fur " +
                "over and trims it until it fits in the room that is left. Both meshes are read in their current " +
                "pose, so pose the avatar first.", EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(2);
            Renderer rendNew = (Renderer)EditorGUILayout.ObjectField(new GUIContent("Fur mesh",
                "The mesh the fur grows on. Same renderer as on the paint tabs."), targetRenderer, typeof(Renderer), true);
            if (rendNew != targetRenderer) SetTargetRenderer(rendNew);

            EditorGUILayout.LabelField("Clothing meshes");
            EditorGUI.indentLevel++;
            for (int i = 0; i < clothing.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                clothing[i] = (Renderer)EditorGUILayout.ObjectField(clothing[i], typeof(Renderer), true);
                if (ColorButton("-", colRed, GUILayout.Width(24))) { clothing.RemoveAt(i); i--; }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.BeginHorizontal();
            if (ColorButton("Add slot", colGray, GUILayout.Width(90))) clothing.Add(null);
            if (ColorButton("Add selected", colTeal, GUILayout.Width(110))) AddSelectedClothing();
            if (clothing.Count > 0 && ColorButton("Clear list", colRed, GUILayout.Width(90))) clothing.Clear();
            EditorGUILayout.EndHorizontal();
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(4);
            occl.furLengthMm = EditorGUILayout.FloatField(new GUIContent("Fur length (mm)",
                "World length of a hair where the length mask is fully white. Match your shader, or the shadow is measured over the wrong distance."),
                occl.furLengthMm);
            occl.clothThicknessMm = EditorGUILayout.FloatField(new GUIContent("Cloth thickness (mm)",
                "Fattens the clothing in every direction. Raise it if fur slips through thin or badly fitted garments."),
                occl.clothThicknessMm);
            occl.lengthMarginMm = EditorGUILayout.FloatField(new GUIContent("Length margin (mm)",
                "Slack kept between the fur tip and the clothing. Raise it if fur is clean in the editor but pokes through once the body moves."),
                occl.lengthMarginMm);

            EditorGUILayout.Space(2);
            occl.rayQuality = EditorGUILayout.IntPopup("Ray quality", occl.rayQuality,
                new[] { "Fast (11 rays)", "Medium (25 rays)", "Fine (55 rays)" }, new[] { 0, 1, 2 });
            occl.maxTiltAngle = EditorGUILayout.Slider(new GUIContent("Max tilt angle",
                "Hard cap on how far a hair may lean from the surface normal. Also limited by the Direction tab's own cap."),
                occl.maxTiltAngle, 0f, 90f);
            if (occl.maxTiltAngle > maxAngle)
                EditorGUILayout.HelpBox("The normal map can only encode " + maxAngle.ToString("0") +
                    "° (Direction tab), so the resolver stops there.", MessageType.Info);
            occl.shadowThreshold = EditorGUILayout.Slider(new GUIContent("Shadow threshold",
                "How shadowed a spot must be before its fur is touched at all."), occl.shadowThreshold, 0f, 0.9f);
            occl.combStrength = EditorGUILayout.Slider(new GUIContent("Comb into open space",
                "How hard the fur is steered towards where there is still room. 0 keeps your groom and only shortens."),
                occl.combStrength, 0f, 1f);
            occl.smoothPasses = EditorGUILayout.IntSlider(new GUIContent("Smooth result",
                "3x3 blur passes over the texels the resolver touched."), occl.smoothPasses, 0, 4);
            occl.writeAlpha = EditorGUILayout.ToggleLeft(new GUIContent("Culled fur -> alpha black",
                "Punch fully trimmed fur out of the alpha mask, so no shell is drawn there at all."), occl.writeAlpha);

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            occl.collectDebug = EditorGUILayout.ToggleLeft(new GUIContent("Debug hairs",
                "Green: surface normal.  Blue: fur as groomed.  Red: after resolving."), occl.collectDebug, GUILayout.Width(95));
            debugDraw = EditorGUILayout.ToggleLeft("Show in Scene", debugDraw, GUILayout.Width(105));
            debugChangedOnly = EditorGUILayout.ToggleLeft("Changed only", debugChangedOnly, GUILayout.Width(105));
            EditorGUILayout.EndHorizontal();
            debugBudget = EditorGUILayout.IntSlider("Debug hair count", debugBudget, 50, 4000);
        }

        // Coverage plus the three maps the resolver writes, side by side.
        void DrawResolverPreviews()
        {
            if (Event.current.type == EventType.Repaint && maskDirty) RebuildMaskTex();
            if (normalMaskTex == null) normalMaskTex = BuildNormalTex(256);

            Rect area = GUILayoutUtility.GetRect(10, 10, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            const float pad = 6f, capH = 15f;
            float w = (area.width - pad * 3f) / 4f;
            float h = Mathf.Min(w, Mathf.Max(60f, area.height - capH));
            var names = new[] { "Coverage", "Normal", "Length", "Alpha" };
            var maps = new[] { coverageTex, normalMaskTex, lenTex, alphaTex };

            for (int i = 0; i < 4; i++)
            {
                Rect r = new Rect(area.x + i * (w + pad), area.y, w, h);
                EditorGUI.DrawRect(r, new Color(0.13f, 0.13f, 0.15f));
                if (maps[i] != null) GUI.DrawTexture(r, maps[i], ScaleMode.ScaleToFit);
                else GUI.Label(new Rect(r.x, r.y + r.height * 0.5f - 8f, r.width, 16f),
                               i == 0 ? "run Resolve" : "-", EditorStyles.centeredGreyMiniLabel);
                GUI.Label(new Rect(r.x, r.yMax + 1f, w, capH), names[i], EditorStyles.miniLabel);
            }
        }

        void AddSelectedClothing()
        {
            foreach (GameObject go in Selection.gameObjects)
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>())
                    if ((r is SkinnedMeshRenderer || r.GetComponent<MeshFilter>() != null) && !clothing.Contains(r))
                        clothing.Add(r);
        }

        void RunResolve()
        {
            if (targetRenderer == null)
            { EditorUtility.DisplayDialog("Fur Grooming Tool", "Assign the fur mesh first.", "OK"); return; }
            var cloth = new System.Collections.Generic.List<Renderer>();
            foreach (Renderer r in clothing) if (r != null && !cloth.Contains(r)) cloth.Add(r);
            if (cloth.Count == 0)
            { EditorUtility.DisplayDialog("Fur Grooming Tool", "Add at least one clothing renderer.", "OK"); return; }

            preDir = (Vector2[])dir.Clone(); preDirStr = (float[])dirStr.Clone();
            preLenStack = lenStack.CloneDeep(); preAlphaStack = alphaStack.CloneDeep();
            hasPreResolve = true;
            PushUndoAll();

            // The resolver only ever caps length down and punches alpha to black - that is a
            // Min blend. So it keeps working on flat buffers: hand it the composite of the
            // layers below its own, and record the difference as that layer's coverage.
            MaskLayer lenLayer = lenStack.EnsureResolver(MN * MN, "Occlusion");
            float[] beforeLen = lenStack.CompositeBelow(lenStack.ResolverIndex());
            float[] workLen = (float[])beforeLen.Clone();

            MaskLayer alphaLayer = null; float[] beforeAlpha = null, workAlpha = null;
            if (occl.writeAlpha)
            {
                alphaLayer = alphaStack.EnsureResolver(MN * MN, "Occlusion");
                beforeAlpha = alphaStack.CompositeBelow(alphaStack.ResolverIndex());
                workAlpha = (float[])beforeAlpha.Clone();
            }

            coverage = new FurCoverage();
            FurOcclusionReport rep = FurOcclusionResolver.Resolve(
                targetRenderer, cloth, dir, dirStr, FN, workLen, workAlpha, MN,
                maxAngle, flipG, occl, coverage, resolveDebug);

            resolveMsg = rep.Summary();
            if (rep.error != null)
            {
                RevertResolve();
                resolveMsg = rep.error;
                EditorUtility.DisplayDialog("Fur Grooming Tool", rep.error, "OK");
                return;
            }

            StampResolverLayer(lenLayer, beforeLen, workLen);
            if (alphaLayer != null) StampResolverLayer(alphaLayer, beforeAlpha, workAlpha);
            lenStack.MarkDirty(); alphaStack.MarkDirty();
            Debug.Log("[Fur] " + resolveMsg);
            if (coverageTex != null) DestroyImmediate(coverageTex);
            coverageTex = FurOcclusionResolver.CoverageTexture(coverage);
            RefreshResolverMaps();
        }

        void RevertResolve()
        {
            if (!hasPreResolve || preDir == null || preLenStack == null || preAlphaStack == null)
            { hasPreResolve = false; return; }   // an assembly reload can drop the pre-resolve copy
            System.Array.Copy(preDir, dir, dir.Length);
            System.Array.Copy(preDirStr, dirStr, dirStr.Length);
            lenStack.CopyFrom(preLenStack);
            alphaStack.CopyFrom(preAlphaStack);
            hasPreResolve = false;
            resolveDebug.Clear();
            if (coverageTex != null) { DestroyImmediate(coverageTex); coverageTex = null; }
            coverage = null;
            resolveMsg = "Reverted to the groom from before the last resolve.";
            RefreshResolverMaps();
        }

        // The resolve result becomes one Min layer whose coverage is exactly the texels it
        // touched, so hiding it restores the pre-resolve composite bit for bit.
        static void StampResolverLayer(MaskLayer L, float[] before, float[] after)
        {
            L.blend = MaskBlend.Min;
            L.locked = true; L.isResolver = true;
            L.visible = true; L.opacity = 1f;
            for (int i = 0; i < after.Length; i++)
            {
                L.value[i] = after[i];
                L.cover[i] = after[i] < before[i] - 1e-6f ? (byte)255 : (byte)0;
            }
        }

        // Show/hide/drop the layer the last resolve wrote, without touching the groom under it.
        void DrawResolverLayerControls()
        {
            int li = lenStack.ResolverIndex(), ai = alphaStack.ResolverIndex();
            if (li < 0 && ai < 0) return;

            MaskLayer probe = li >= 0 ? lenStack.At(li) : alphaStack.At(ai);
            EditorGUILayout.BeginHorizontal();
            bool vis = EditorGUILayout.ToggleLeft(new GUIContent("Show resolve layer",
                "The resolve lives on its own layer. Untick to see the groom exactly as it was before."),
                probe.visible, GUILayout.Width(150));
            if (vis != probe.visible)
            {
                if (li >= 0) lenStack.At(li).visible = vis;
                if (ai >= 0) alphaStack.At(ai).visible = vis;
                lenStack.MarkDirty(); alphaStack.MarkDirty();
                RefreshResolverMaps();
            }
            float op = EditorGUILayout.Slider(probe.opacity, 0f, 1f);
            if (!Mathf.Approximately(op, probe.opacity))
            {
                if (li >= 0) lenStack.At(li).opacity = op;
                if (ai >= 0) alphaStack.At(ai).opacity = op;
                lenStack.MarkDirty(); alphaStack.MarkDirty();
                RefreshResolverMaps();
            }
            if (ColorButton("Drop layer", colRed, GUILayout.Width(90)))
            {
                PushUndoStackStructure();
                if (li >= 0) lenStack.Delete(li);
                if (ai >= 0) alphaStack.Delete(ai);
                resolveDebug.Clear();
                resolveMsg = "Dropped the resolve layer.";
                RefreshResolverMaps();
            }
            EditorGUILayout.EndHorizontal();
        }

        void RefreshResolverMaps()
        {
            if (normalMaskTex != null) DestroyImmediate(normalMaskTex);
            normalMaskTex = BuildNormalTex(256);
            if (normalPreview != null) normalPreview = BuildNormalTex(Mathf.Min(exportRes, 1024));
            maskDirty = true;
            Repaint(); SceneView.RepaintAll();
        }

        // Same read as the reference tool's debug mode.
        void DrawResolverDebug()
        {
            if (!debugDraw || resolveDebug.Count == 0) return;
            int total = resolveDebug.Count;
            int wanted = Mathf.Min(debugBudget, total);
            int stride = Mathf.Max(1, total / Mathf.Max(1, wanted));
            int drawn = 0;
            for (int i = 0; i < total && drawn < wanted; i += stride)
            {
                FurHairDebug hd = resolveDebug[i];
                if (debugChangedOnly && hd.state <= 1) continue;
                drawn++;
                float nl = Mathf.Max(1e-4f, hd.before.magnitude) * 0.5f;
                Handles.color = new Color(0.3f, 1f, 0.3f, 0.7f);
                Handles.DrawLine(hd.root, hd.root + hd.normal * nl);
                Handles.color = new Color(0.45f, 0.8f, 1f, 0.9f);
                Handles.DrawLine(hd.root, hd.root + hd.before);
                Handles.color = new Color(1f, 0.3f, 0.25f, 0.95f);
                if (hd.after.sqrMagnitude > 1e-10f) Handles.DrawLine(hd.root, hd.root + hd.after);
                else Handles.DrawSolidDisc(hd.root, hd.normal, nl * 0.12f);
            }
        }

        // =========================================================== drawing

        void DrawCanvas(Rect r)
        {
            EditorGUI.DrawRect(r, new Color(0.16f, 0.16f, 0.16f));
            if (bg != null)
            {
                Color c = GUI.color; GUI.color = new Color(1, 1, 1, bgOpacity);
                DrawView(r, bg); GUI.color = c;
            }

            if (tab != Tab.Direction)
            {
                Texture2D m = tab == Tab.Length ? lenTex : alphaTex;
                if (m != null)
                {
                    Color c = GUI.color; GUI.color = new Color(1, 1, 1, 0.82f);
                    DrawView(r, m); GUI.color = c;
                }
            }

            if (Event.current.type != EventType.Repaint) return;

            if (tab == Tab.Direction && showArrows) DrawArrows(r);
            if (tab == Tab.Length && gradActive) DrawGradientGuide(r);
            DrawCursor(r);
        }

        void DrawArrows(Rect r)
        {
            float pxPerCell = r.width / FN / ViewSize;
            int step = Mathf.Max(1, Mathf.RoundToInt(16f / Mathf.Max(0.0001f, pxPerCell)));
            Vector2 vmin = ViewMin; float vs = ViewSize;
            int iMin = Mathf.Clamp(Mathf.FloorToInt(vmin.x * FN) - 1, 0, FN - 1);
            int iMax = Mathf.Clamp(Mathf.CeilToInt((vmin.x + vs) * FN) + 1, 0, FN - 1);
            int jMin = Mathf.Clamp(Mathf.FloorToInt(vmin.y * FN) - 1, 0, FN - 1);
            int jMax = Mathf.Clamp(Mathf.CeilToInt((vmin.y + vs) * FN) + 1, 0, FN - 1);
            float len0 = pxPerCell * step * 0.8f;
            Handles.color = arrowColor;
            for (int j = jMin; j <= jMax; j += step)
                for (int i = iMin; i <= iMax; i += step)
                {
                    int idx = j * FN + i; float st = dirStr[idx];
                    if (st < 0.03f) continue;
                    Vector2 d = dir[idx]; if (d.sqrMagnitude < 1e-6f) continue;
                    Vector2 ctr = UvToRect(new Vector2((i + 0.5f) / FN, (j + 0.5f) / FN), r);
                    if (ctr.x < r.x || ctr.x > r.xMax || ctr.y < r.y || ctr.y > r.yMax) continue;
                    float ang = Mathf.Atan2(d.y, d.x);
                    float len = len0 * (0.35f + 0.65f * st);
                    Vector2 e = ctr + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * len;
                    float ah = Mathf.Max(3f, len * 0.34f);
                    Handles.DrawAAPolyLine(2f, V3(ctr), V3(e));
                    Handles.DrawAAPolyLine(2f, V3(e), V3(e - new Vector2(Mathf.Cos(ang - 0.5f), Mathf.Sin(ang - 0.5f)) * ah));
                    Handles.DrawAAPolyLine(2f, V3(e), V3(e - new Vector2(Mathf.Cos(ang + 0.5f), Mathf.Sin(ang + 0.5f)) * ah));
                }
        }

        void DrawGradientGuide(Rect r)
        {
            Vector2 a = UvToRect(gradStart, r), b = UvToRect(gradEnd, r);
            Handles.color = Color.black; Handles.DrawAAPolyLine(5f, V3(a), V3(b));
            Handles.color = Color.white; Handles.DrawAAPolyLine(2f, V3(a), V3(b));
            Handles.DrawSolidDisc(V3(b), Vector3.forward, 4f);
        }

        void DrawCursor(Rect r)
        {
            Vector2 m = Event.current.mousePosition;
            if (!r.Contains(m)) return;
            float rad = brushSize / REF * (r.width / ViewSize);
            Handles.color = Color.black; Handles.DrawWireDisc(V3(m), Vector3.forward, rad + 1f);
            Handles.color = Color.white; Handles.DrawWireDisc(V3(m), Vector3.forward, rad);
        }

        void DrawPreview(Rect r)
        {
            EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 1f));
            Texture2D t = tab == Tab.Direction ? normalPreview : (tab == Tab.Length ? lenTex : alphaTex);
            if (t != null) GUI.DrawTexture(r, t, ScaleMode.ScaleToFit);
        }

        // =========================================================== input

        void HandleInput(Rect r)
        {
            Event e = Event.current;
            Vector2 m = e.mousePosition;
            bool inside = r.Contains(m);

            if (e.type == EventType.ScrollWheel && inside)
            {
                Vector2 uvUnder = RectToUv(m, r);
                Vector2 f = new Vector2((m.x - r.x) / r.width, (m.y - r.y) / r.height);
                zoom = Mathf.Clamp(zoom * (1f - e.delta.y * 0.08f), 1f, 16f);
                float vs = 1f / zoom;
                panCenter = uvUnder - new Vector2(f.x * vs, f.y * vs) + Vector2.one * (vs * 0.5f);
                ClampView(); e.Use(); Repaint(); return;
            }
            if (e.type == EventType.MouseDown && e.button == 2 && inside) { panning = true; e.Use(); return; }
            if (e.type == EventType.MouseDrag && panning)
            {
                panCenter -= new Vector2(e.delta.x / r.width, e.delta.y / r.height) * ViewSize;
                ClampView(); e.Use(); Repaint(); return;
            }
            if (e.type == EventType.MouseUp && e.button == 2) panning = false;
            if (e.type == EventType.ContextClick && inside) { e.Use(); return; }

            Vector2 uv = RectToUv(m, r);

            if (showOnMesh)
            {
                if (inside && (e.type == EventType.MouseMove || e.type == EventType.MouseDrag || e.type == EventType.MouseDown)) UpdateSceneHit(uv);
                else if (e.type == EventType.MouseLeaveWindow && sceneHits.Count > 0) { sceneHits.Clear(); SceneView.RepaintAll(); }
            }

            if (e.type == EventType.MouseDown && (e.button == 0 || e.button == 1) && inside)
            {
                if (ActiveLayerLocked()) { e.Use(); return; }
                paintErase = e.button == 1;
                forceHoleErase = e.shift;
                PushUndoActive();
                if (tab == Tab.Length && lenMode == LenMode.Gradient && !paintErase)
                { gradActive = true; gradStart = gradEnd = uv; }
                else { painting = true; lastUv = uv; Stroke(uv, uv); }
                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseDrag && (painting || gradActive))
            {
                if (gradActive) gradEnd = uv;
                else { Stroke(lastUv, uv); lastUv = uv; }
                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseUp)
            {
                if (gradActive) { CommitGradient(gradStart, uv); gradActive = false; maskDirty = true; }
                painting = false; Repaint();
            }
            else if (e.type == EventType.MouseMove && inside) Repaint();
        }

        void Stroke(Vector2 a, Vector2 b)
        {
            if (tab == Tab.Direction)
            {
                if (paintErase) WalkField(a, b, FN, fc => DabPaint(dirStr, FN, fc, 0f, brushHardness));
                else
                {
                    Vector2 d = b - a; float l = d.magnitude;
                    float thr = 0.001f * ViewSize; // same on-screen distance at any zoom
                    Vector2 sd = l > thr ? d / l : lastDir; if (l > thr) lastDir = sd;
                    WalkField(a, b, FN, fc => DabDir(fc, sd));
                }
            }
            else if (tab == Tab.Length)
            {
                MaskLayer L = lenStack.Active;
                if (L == null || L.locked) return;
                if (paintErase)
                {
                    if (EraseCutsHole(lenStack)) WalkField(a, b, MN, fc => DabErase(L.cover, MN, fc, brushHardness));
                    else WalkField(a, b, MN, fc => DabPaint(L.value, MN, fc, 0f, brushHardness, false, L.cover));
                }
                else if (lenMode == LenMode.Smudge) WalkField(a, b, MN, fc => DabSmudge(L.value, L.cover, MN, fc));
                else WalkField(a, b, MN, fc => DabPaint(L.value, MN, fc, lenValue, brushHardness, false, L.cover));
                lenStack.MarkDirty(); maskDirty = true;
            }
            else
            {
                MaskLayer L = alphaStack.Active;
                if (L == null || L.locked) return;
                if (paintErase)
                {
                    if (EraseCutsHole(alphaStack)) WalkField(a, b, MN, fc => DabErase(L.cover, MN, fc, 1f));
                    else WalkField(a, b, MN, fc => DabPaint(L.value, MN, fc, 0f, 1f, true, L.cover));
                }
                else
                {
                    float target = alphaMode == AlphaMode.White ? 1f : 0f;
                    WalkField(a, b, MN, fc => DabPaint(L.value, MN, fc, target, 1f, true, L.cover));
                }
                alphaStack.MarkDirty(); maskDirty = true;
            }
        }

        void WalkField(Vector2 uvA, Vector2 uvB, int res, System.Action<Vector2> dab)
        {
            Vector2 a = uvA * res, b = uvB * res;
            float l = (b - a).magnitude;
            float step = Mathf.Max(0.5f, BrushCells(res) * 0.33f);
            int n = Mathf.Max(1, Mathf.FloorToInt(l / step));
            for (int k = 0; k <= n; k++) dab(Vector2.Lerp(a, b, (float)k / n));
        }

        float BrushCells(int res) => brushSize * (res / REF);

        float Falloff(float d, float r, float h)
        {
            if (d >= r) return 0f;
            float ri = r * h;
            if (d <= ri) return 1f;
            float x = (r - d) / (r - ri);
            return x * x * (3f - 2f * x);
        }

        // ---- per-mode dabs

        void DabDir(Vector2 fc, Vector2 sd)
        {
            float r = BrushCells(FN);
            int i0 = Mathf.Max(0, Mathf.FloorToInt(fc.x - r)), i1 = Mathf.Min(FN - 1, Mathf.CeilToInt(fc.x + r));
            int j0 = Mathf.Max(0, Mathf.FloorToInt(fc.y - r)), j1 = Mathf.Min(FN - 1, Mathf.CeilToInt(fc.y + r));
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    float dist = Vector2.Distance(new Vector2(i + 0.5f, j + 0.5f), fc);
                    if (dist > r) continue;
                    float w = Mathf.Clamp01(Falloff(dist, r, brushHardness) * brushFlow);
                    int idx = j * FN + i;
                    Vector2 t = sd;
                    if (dirMode == DirMode.Pinch)
                    {
                        Vector2 v = fc - new Vector2(i + 0.5f, j + 0.5f);
                        t = v.sqrMagnitude > 1e-6f ? v.normalized : sd;
                    }
                    if (dirMode == DirMode.DirStrength || dirMode == DirMode.Direction || dirMode == DirMode.Pinch)
                    {
                        Vector2 nd = Vector2.Lerp(dir[idx], t, w);
                        if (nd.sqrMagnitude > 1e-8f) nd.Normalize();
                        dir[idx] = nd;
                    }
                    if (dirMode == DirMode.Erase) dirStr[idx] = Mathf.Lerp(dirStr[idx], 0f, w);
                    else if (dirMode != DirMode.Direction) dirStr[idx] = Mathf.Lerp(dirStr[idx], furStrength, w);
                }
        }

        // `cover` is optional: the Direction field has none, mask layers pass theirs so the
        // painted value and the coverage that reveals it rise by the same weight.
        void DabPaint(float[] buf, int res, Vector2 fc, float target, float hardness, bool full = false, byte[] cover = null)
        {
            float r = BrushCells(res);
            int i0 = Mathf.Max(0, Mathf.FloorToInt(fc.x - r)), i1 = Mathf.Min(res - 1, Mathf.CeilToInt(fc.x + r));
            int j0 = Mathf.Max(0, Mathf.FloorToInt(fc.y - r)), j1 = Mathf.Min(res - 1, Mathf.CeilToInt(fc.y + r));
            float flow = full ? 1f : brushFlow;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    float dist = Vector2.Distance(new Vector2(i + 0.5f, j + 0.5f), fc);
                    if (dist > r) continue;
                    float w = Mathf.Clamp01(Falloff(dist, r, hardness) * flow);
                    int idx = j * res + i;
                    buf[idx] = Mathf.Lerp(buf[idx], target, w);
                    if (cover != null) cover[idx] = MaskLayer.Raise(cover[idx], w);
                }
        }

        // Right-drag on a mask tab: lower this layer's coverage, leaving its value alone,
        // so the layers below show through where the brush passed.
        void DabErase(byte[] cover, int res, Vector2 fc, float hardness)
        {
            float r = BrushCells(res);
            int i0 = Mathf.Max(0, Mathf.FloorToInt(fc.x - r)), i1 = Mathf.Min(res - 1, Mathf.CeilToInt(fc.x + r));
            int j0 = Mathf.Max(0, Mathf.FloorToInt(fc.y - r)), j1 = Mathf.Min(res - 1, Mathf.CeilToInt(fc.y + r));
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    float dist = Vector2.Distance(new Vector2(i + 0.5f, j + 0.5f), fc);
                    if (dist > r) continue;
                    float w = Mathf.Clamp01(Falloff(dist, r, hardness) * brushFlow);
                    int idx = j * res + i;
                    cover[idx] = MaskLayer.Lower(cover[idx], w);
                }
        }

        void DabSmudge(float[] buf, byte[] cover, int res, Vector2 fc)
        {
            float r = BrushCells(res);
            int i0 = Mathf.Max(0, Mathf.FloorToInt(fc.x - r)), i1 = Mathf.Min(res - 1, Mathf.CeilToInt(fc.x + r));
            int j0 = Mathf.Max(0, Mathf.FloorToInt(fc.y - r)), j1 = Mathf.Min(res - 1, Mathf.CeilToInt(fc.y + r));
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    float dist = Vector2.Distance(new Vector2(i + 0.5f, j + 0.5f), fc);
                    if (dist > r) continue;
                    float w = Mathf.Clamp01(Falloff(dist, r, 0f) * brushFlow * 0.8f);
                    int idx = j * res + i;
                    float s = 0f, sc = 0f; int cnt = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int x = Mathf.Clamp(i + dx, 0, res - 1), y = Mathf.Clamp(j + dy, 0, res - 1);
                            s += buf[y * res + x];
                            if (cover != null) sc += cover[y * res + x];
                            cnt++;
                        }
                    buf[idx] = Mathf.Lerp(buf[idx], s / cnt, w);
                    // Smear coverage with the value, so a smudge drags the painted region's
                    // edge instead of only shuffling values inside it.
                    if (cover != null)
                        cover[idx] = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(cover[idx], sc / cnt, w)), 0, 255);
                }
        }

        void CommitGradient(Vector2 uvA, Vector2 uvB)
        {
            GradientInto(uvA, uvB, true);
        }

        // Whole-canvas ops write the active layer and take it solid: they set a value
        // everywhere, so coverage everywhere is what makes that value visible.
        void GradientInto(Vector2 uvA, Vector2 uvB, bool reset)
        {
            Vector2 d = uvB - uvA; float len2 = d.sqrMagnitude;
            if (len2 < 1e-6f) return;
            MaskLayer L = lenStack.Active;
            if (L == null || L.locked) return;
            for (int r = 0; r < MN; r++)
                for (int c = 0; c < MN; c++)
                {
                    Vector2 uv = new Vector2((c + 0.5f) / MN, (r + 0.5f) / MN);
                    float t = Mathf.Clamp01(Vector2.Dot(uv - uvA, d) / len2);
                    int idx = r * MN + c;
                    L.value[idx] = reset ? t : Mathf.Max(L.value[idx], t);
                    L.cover[idx] = 255;
                }
            lenStack.MarkDirty();
        }

        // =========================================================== buffers / fx

        void ClearLayer()
        {
            if (ActiveLayerLocked()) return;
            PushUndoActive();
            if (tab == Tab.Direction) { System.Array.Clear(dir, 0, dir.Length); System.Array.Clear(dirStr, 0, dirStr.Length); }
            else { ActiveLayer().Clear(); ActiveStack().MarkDirty(); }
            maskDirty = true; Repaint();
        }

        void FillActiveLayer(float v)
        {
            MaskLayer L = ActiveLayer();
            if (L == null || L.locked) return;
            PushUndoActive();
            for (int i = 0; i < L.value.Length; i++) { L.value[i] = v; L.cover[i] = 255; }
            ActiveStack().MarkDirty(); maskDirty = true;
        }

        void SmoothActiveLayer()
        {
            MaskLayer L = ActiveLayer();
            if (L == null || L.locked) return;
            PushUndoActive();
            SmoothBuffer(L.value, MN, smoothRadius);
            SmoothCover(L.cover, MN, smoothRadius);
            ActiveStack().MarkDirty(); maskDirty = true;
        }

        void DrawUndoButtons()
        {
            using (new EditorGUI.DisabledScope(undoStack.Count == 0))
                if (ColorButton("Undo", colGray, GUILayout.Width(60))) DoUndo();
            using (new EditorGUI.DisabledScope(redoStack.Count == 0))
                if (ColorButton("Redo", colGray, GUILayout.Width(60))) DoRedo();
        }

        // ---- undo / redo (snapshots only the layer(s) an op touches)
        void HandleShortcuts()
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown || !(e.control || e.command)) return;
            if (e.keyCode == KeyCode.Z) { if (e.shift) DoRedo(); else DoUndo(); e.Use(); }
            else if (e.keyCode == KeyCode.Y) { DoRedo(); e.Use(); }
        }

        StackSnap CaptureStack(MaskLayerStack st, Cap c, int index)
        {
            if (c == Cap.None) return null;
            if (c == Cap.Whole) return new StackSnap { whole = st.CloneDeep() };
            MaskLayer L = st.At(index < 0 ? st.active : index);
            if (L == null) return new StackSnap { whole = st.CloneDeep() };
            return new StackSnap
            {
                index = index < 0 ? st.active : index,
                value = (float[])L.value.Clone(),
                cover = (byte[])L.cover.Clone()
            };
        }

        void ApplyStack(MaskLayerStack st, StackSnap s)
        {
            if (s == null) return;
            if (s.whole != null) { st.CopyFrom(s.whole); return; }
            MaskLayer L = st.At(s.index);
            if (L != null)   // the layer may have been deleted since; then there is nothing to restore
            {
                System.Array.Copy(s.value, L.value, L.value.Length);
                System.Array.Copy(s.cover, L.cover, L.cover.Length);
            }
            st.MarkDirty();
        }

        // Re-capture the same shape a snapshot has, so undo and redo stay symmetric.
        static Cap ShapeOf(StackSnap s) { return s == null ? Cap.None : (s.whole != null ? Cap.Whole : Cap.Layer); }

        Snapshot Capture(bool d, Cap l, Cap a, int lIdx = -1, int aIdx = -1)
        {
            var s = new Snapshot();
            if (d) { s.dir = (Vector2[])dir.Clone(); s.dirStr = (float[])dirStr.Clone(); }
            s.len = CaptureStack(lenStack, l, lIdx);
            s.alpha = CaptureStack(alphaStack, a, aIdx);
            return s;
        }

        void ApplySnapshot(Snapshot s)
        {
            if (s.dir != null) { System.Array.Copy(s.dir, dir, dir.Length); System.Array.Copy(s.dirStr, dirStr, dirStr.Length); }
            ApplyStack(lenStack, s.len);
            ApplyStack(alphaStack, s.alpha);
            if (s.dir != null && normalPreview != null) normalPreview = BuildNormalTex(Mathf.Min(exportRes, 1024));
            maskDirty = true; Repaint();
        }

        void PushUndo(bool d, Cap l, Cap a)
        {
            undoStack.Add(Capture(d, l, a));
            while (undoStack.Count > MaxUndo) undoStack.RemoveAt(0);
            redoStack.Clear();
        }

        // A stroke or a whole-canvas op: only the layer being edited.
        void PushUndoActive()
        {
            if (tab == Tab.Collision) { PushUndoAll(); return; }
            PushUndo(tab == Tab.Direction,
                     tab == Tab.Length ? Cap.Layer : Cap.None,
                     tab == Tab.Alpha ? Cap.Layer : Cap.None);
        }

        // Add / delete / duplicate / reorder change the stack itself. The Collision tab's
        // layer controls drive both masks at once, so there both are kept.
        void PushUndoStackStructure()
        {
            if (tab == Tab.Collision) { PushUndo(false, Cap.Whole, Cap.Whole); return; }
            PushUndo(false,
                     tab == Tab.Alpha ? Cap.None : Cap.Whole,
                     tab == Tab.Alpha ? Cap.Whole : Cap.None);
        }

        void PushUndoAll() => PushUndo(true, Cap.Whole, Cap.Whole);

        void DoUndo()
        {
            if (undoStack.Count == 0) return;
            Snapshot s = undoStack[undoStack.Count - 1]; undoStack.RemoveAt(undoStack.Count - 1);
            redoStack.Add(Capture(s.dir != null, ShapeOf(s.len), ShapeOf(s.alpha),
                                  s.len != null ? s.len.index : -1, s.alpha != null ? s.alpha.index : -1));
            ApplySnapshot(s);
        }

        void DoRedo()
        {
            if (redoStack.Count == 0) return;
            Snapshot s = redoStack[redoStack.Count - 1]; redoStack.RemoveAt(redoStack.Count - 1);
            undoStack.Add(Capture(s.dir != null, ShapeOf(s.len), ShapeOf(s.alpha),
                                  s.len != null ? s.len.index : -1, s.alpha != null ? s.alpha.index : -1));
            ApplySnapshot(s);
        }

        void SmoothBuffer(float[] buf, int res, float radius)
        {
            int rad = Mathf.Max(1, Mathf.RoundToInt(radius));
            float[] tmp = new float[buf.Length];
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                {
                    float s = 0; int n = 0;
                    for (int k = -rad; k <= rad; k++) { int xx = Mathf.Clamp(x + k, 0, res - 1); s += buf[y * res + xx]; n++; }
                    tmp[y * res + x] = s / n;
                }
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                {
                    float s = 0; int n = 0;
                    for (int k = -rad; k <= rad; k++) { int yy = Mathf.Clamp(y + k, 0, res - 1); s += tmp[yy * res + x]; n++; }
                    buf[y * res + x] = s / n;
                }
        }

        // Blurred alongside the value, so smoothing softens the painted region's edge
        // instead of leaving a hard coverage cut across a now-soft value.
        static void SmoothCover(byte[] buf, int res, float radius)
        {
            int rad = Mathf.Max(1, Mathf.RoundToInt(radius));
            var tmp = new float[buf.Length];
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                {
                    float s = 0; int n = 0;
                    for (int k = -rad; k <= rad; k++) { int xx = Mathf.Clamp(x + k, 0, res - 1); s += buf[y * res + xx]; n++; }
                    tmp[y * res + x] = s / n;
                }
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                {
                    float s = 0; int n = 0;
                    for (int k = -rad; k <= rad; k++) { int yy = Mathf.Clamp(y + k, 0, res - 1); s += tmp[yy * res + x]; n++; }
                    buf[y * res + x] = (byte)Mathf.Clamp(Mathf.RoundToInt(s / n), 0, 255);
                }
        }

        float AlphaResolve(float v)
        {
            if (!alphaAA) return v >= alphaThreshold ? 1f : 0f;
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(alphaThreshold - 0.03f, alphaThreshold + 0.03f, v));
        }

        // =========================================================== textures

        // Both masks are rebuilt from their composites: the Collision tab shows the two side
        // by side, so refreshing only the active tab's texture would leave one of them stale.
        void RebuildMaskTex()
        {
            Ensure(ref lenTex, MN); Ensure(ref alphaTex, MN);
            BlitMask(lenTex, CompositeLen(), false);
            BlitMask(alphaTex, CompositeAlpha(), true);
            maskDirty = false;
        }

        void BlitMask(Texture2D dst, float[] src, bool alpha)
        {
            var px = new Color32[MN * MN];
            for (int r = 0; r < MN; r++)
            {
                int outRow = MN - 1 - r;
                for (int c = 0; c < MN; c++)
                {
                    float v = src[r * MN + c];
                    if (alpha) v = AlphaResolve(v);
                    byte b = Enc(v);
                    px[outRow * MN + c] = new Color32(b, b, b, 255);
                }
            }
            dst.SetPixels32(px); dst.Apply();
        }

        Texture2D BuildNormalTex(int res)
        {
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
            var px = new Color32[res * res]; float ang = maxAngle * Mathf.Deg2Rad;
            for (int irow = 0; irow < res; irow++)
            {
                float fy = (irow + 0.5f) / res * FN - 0.5f; int outRow = res - 1 - irow;
                for (int x = 0; x < res; x++)
                {
                    float fx = (x + 0.5f) / res * FN - 0.5f;
                    SampleDir(fx, fy, out Vector2 d, out float st);
                    float len = d.magnitude; float nx, ny, nz;
                    if (len < 1e-4f || st < 1e-4f) { nx = 0; ny = 0; nz = 1; }
                    else { float t = st * ang, si = Mathf.Sin(t); nx = d.x / len * si; ny = d.y / len * si; nz = Mathf.Cos(t); }
                    float gy = flipG ? -ny : ny;
                    px[outRow * res + x] = new Color32(EncN(nx), EncN(gy), EncN(nz), 255);
                }
            }
            tex.SetPixels32(px); tex.Apply(); return tex;
        }

        Texture2D BuildMaskTex(int res, float[] buf, bool alpha)
        {
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
            var px = new Color32[res * res];
            for (int irow = 0; irow < res; irow++)
            {
                float fy = (irow + 0.5f) / res * MN - 0.5f; int outRow = res - 1 - irow;
                for (int x = 0; x < res; x++)
                {
                    float fx = (x + 0.5f) / res * MN - 0.5f;
                    float v = SampleBuf(buf, MN, fx, fy);
                    if (alpha) v = AlphaResolve(v);
                    byte b = Enc(v);
                    px[outRow * res + x] = new Color32(b, b, b, 255);
                }
            }
            tex.SetPixels32(px); tex.Apply(); return tex;
        }

        void SampleDir(float fx, float fy, out Vector2 d, out float st)
        {
            fx = Mathf.Clamp(fx, 0, FN - 1); fy = Mathf.Clamp(fy, 0, FN - 1);
            int x0 = (int)fx, y0 = (int)fy, x1 = Mathf.Min(FN - 1, x0 + 1), y1 = Mathf.Min(FN - 1, y0 + 1);
            float tx = fx - x0, ty = fy - y0;
            d = Vector2.Lerp(Vector2.Lerp(dir[y0 * FN + x0], dir[y0 * FN + x1], tx),
                             Vector2.Lerp(dir[y1 * FN + x0], dir[y1 * FN + x1], tx), ty);
            float a = Mathf.Lerp(dirStr[y0 * FN + x0], dirStr[y0 * FN + x1], tx);
            float b = Mathf.Lerp(dirStr[y1 * FN + x0], dirStr[y1 * FN + x1], tx);
            st = Mathf.Lerp(a, b, ty);
        }

        float SampleBuf(float[] buf, int res, float fx, float fy)
        {
            fx = Mathf.Clamp(fx, 0, res - 1); fy = Mathf.Clamp(fy, 0, res - 1);
            int x0 = (int)fx, y0 = (int)fy, x1 = Mathf.Min(res - 1, x0 + 1), y1 = Mathf.Min(res - 1, y0 + 1);
            float tx = fx - x0, ty = fy - y0;
            float a = Mathf.Lerp(buf[y0 * res + x0], buf[y0 * res + x1], tx);
            float b = Mathf.Lerp(buf[y1 * res + x0], buf[y1 * res + x1], tx);
            return Mathf.Lerp(a, b, ty);
        }

        // =========================================================== save

        void SaveCurrent() { SaveMap(tab); }

        void SaveMap(Tab which)
        {
            string b = string.IsNullOrEmpty(baseName) ? "Fur" : baseName;
            if (which == Tab.Direction) SaveTexture(BuildNormalTex(exportRes), b + "_Normal", true, normalProp);
            else if (which == Tab.Length) SaveTexture(BuildMaskTex(exportRes, CompositeLen(), false), b + "_Length", false, lengthProp);
            else if (which == Tab.Alpha) SaveTexture(BuildMaskTex(exportRes, CompositeAlpha(), true), b + "_Alpha", false, alphaProp);
        }

        void SaveTexture(Texture2D tex, string name, bool normal, string prop)
        {
            string path;
            if (!string.IsNullOrEmpty(outputFolder) && IsInsideAssets(outputFolder))
            {
                string folder = outputFolder.Replace('\\', '/').TrimEnd('/');
                if (!System.IO.Directory.Exists(folder)) System.IO.Directory.CreateDirectory(folder);
                path = folder + "/" + name + ".png"; // overwrites if it already exists
            }
            else
            {
                path = EditorUtility.SaveFilePanelInProject("Save " + name, name, "png", "Choose a location inside Assets");
                if (string.IsNullOrEmpty(path)) { Object.DestroyImmediate(tex); return; }
            }
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp != null)
            {
                imp.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                imp.sRGBTexture = false;
                imp.SaveAndReimport();
            }
            var loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (targetMat != null && loaded != null)
            {
                if (targetMat.HasProperty(prop))
                {
                    targetMat.SetTexture(prop, loaded);
                    if (prop == "_BumpMap" && targetMat.HasProperty("_UseBumpMap")) targetMat.SetFloat("_UseBumpMap", 1f);
                    EditorUtility.SetDirty(targetMat); AssetDatabase.SaveAssets();
                }
                else Debug.LogWarning("[Fur] Material has no property '" + prop + "'. Saved PNG but did not assign.");
            }
            EditorGUIUtility.PingObject(loaded);
            Debug.Log("[Fur] Saved " + name + " -> " + path);
        }

        // Groom file v2 keeps the whole layer stack. A v1 file opens with FN (256), so the
        // negative magic tells the two apart and v1 grooms keep loading, into one base layer.
        const int GroomV2Magic = -2;

        void SaveGroom()
        {
            string p = EditorUtility.SaveFilePanel("Save groom data", "", "groom", "bytes");
            if (string.IsNullOrEmpty(p)) return;
            using (var w = new BinaryWriter(File.Open(p, FileMode.Create))) WriteGroom(w);
            Debug.Log("[Fur] Groom saved -> " + p);
        }

        void WriteGroom(BinaryWriter w)
        {
            w.Write(GroomV2Magic);
            w.Write(FN); w.Write(MN);
            for (int i = 0; i < FN * FN; i++) { w.Write(dir[i].x); w.Write(dir[i].y); w.Write(dirStr[i]); }
            lenStack.Write(w);
            alphaStack.Write(w);
        }

        void LoadGroom()
        {
            string p = EditorUtility.OpenFilePanel("Load groom data", "", "bytes");
            if (string.IsNullOrEmpty(p)) return;
            PushUndoAll();
            using (var r = new BinaryReader(File.Open(p, FileMode.Open))) ReadGroom(r);
            maskDirty = true; Repaint();
        }

        bool ReadGroom(BinaryReader r)
        {
            int first = r.ReadInt32();
            bool v2 = first == GroomV2Magic;
            int fn = v2 ? r.ReadInt32() : first;
            int mn = r.ReadInt32();
            if (fn != FN || mn != MN) { Debug.LogError("[Fur] Groom resolution mismatch."); return false; }
            for (int i = 0; i < FN * FN; i++) { dir[i] = new Vector2(r.ReadSingle(), r.ReadSingle()); dirStr[i] = r.ReadSingle(); }
            if (v2)
            {
                lenStack.Read(r, MN * MN);
                alphaStack.Read(r, MN * MN);
            }
            else
            {
                var flat = new float[MN * MN];
                for (int i = 0; i < MN * MN; i++) flat[i] = r.ReadSingle();
                lenStack.SetSingle("Base", flat);
                for (int i = 0; i < MN * MN; i++) flat[i] = r.ReadSingle();
                alphaStack.SetSingle("Base", flat);
            }
            lenStack.Ensure(MN * MN, "Base"); alphaStack.Ensure(MN * MN, "Base");
            return true;
        }

        // Import a grayscale image into the active layer (reads the red channel), taking the
        // layer solid. Oriented to round-trip with the exported PNG (row 0 = top = V=1).
        void LoadMask(MaskLayerStack st)
        {
            MaskLayer L = st.Active;
            if (L == null || L.locked) return;
            string p = EditorUtility.OpenFilePanelWithFilters("Load mask image", Application.dataPath,
                new[] { "Image", "png,jpg,jpeg,tga,bmp", "All files", "*" });
            if (string.IsNullOrEmpty(p)) return;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(File.ReadAllBytes(p))) { Object.DestroyImmediate(tex); Debug.LogError("[Fur] Could not load image: " + p); return; }
            PushUndoActive();
            for (int y = 0; y < MN; y++)
            {
                float v = 1f - y / (float)(MN - 1);
                for (int x = 0; x < MN; x++)
                {
                    int idx = y * MN + x;
                    L.value[idx] = tex.GetPixelBilinear(x / (float)(MN - 1), v).r;
                    L.cover[idx] = 255;
                }
            }
            Object.DestroyImmediate(tex);
            st.MarkDirty();
            maskDirty = true; Repaint();
            Debug.Log("[Fur] Loaded mask <- " + p);
        }

        // =========================================================== helpers

        static byte Enc(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
        static byte EncN(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt((v * 0.5f + 0.5f) * 255f), 0, 255);
        static Vector3 V3(Vector2 v) => new Vector3(v.x, v.y, 0);

        static readonly Color colGreen = new Color(0.55f, 0.9f, 0.55f);
        static readonly Color colBlue = new Color(0.55f, 0.7f, 1f);
        static readonly Color colTeal = new Color(0.5f, 0.9f, 0.85f);
        static readonly Color colAmber = new Color(1f, 0.82f, 0.4f);
        static readonly Color colPurple = new Color(0.8f, 0.6f, 1f);
        static readonly Color colRed = new Color(1f, 0.5f, 0.5f);
        static readonly Color colGray = new Color(0.82f, 0.82f, 0.82f);

        static bool ColorButton(string label, Color col, params GUILayoutOption[] opts)
        {
            return ColorButton(new GUIContent(label), col, opts);
        }

        static bool ColorButton(GUIContent label, Color col, params GUILayoutOption[] opts)
        {
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = col;
            bool clicked = GUILayout.Button(label, opts);
            GUI.backgroundColor = prev;
            return clicked;
        }

        void BrowseOutputFolder()
        {
            string start = !string.IsNullOrEmpty(outputFolder) && System.IO.Directory.Exists(outputFolder) ? outputFolder : Application.dataPath;
            string picked = EditorUtility.OpenFolderPanel("Choose output folder (inside Assets)", start, "");
            if (string.IsNullOrEmpty(picked)) return;
            string rel = AbsoluteToProject(picked);
            if (rel != null) outputFolder = rel;
            else EditorUtility.DisplayDialog("Fur Grooming Tool", "Please pick a folder inside this project's Assets folder.", "OK");
        }

        static bool IsInsideAssets(string p)
        {
            p = p.Replace('\\', '/');
            return p == "Assets" || p.StartsWith("Assets/");
        }

        static string AbsoluteToProject(string abs)
        {
            abs = abs.Replace('\\', '/');
            string data = Application.dataPath.Replace('\\', '/');
            if (abs == data) return "Assets";
            if (abs.StartsWith(data + "/")) return "Assets" + abs.Substring(data.Length);
            return null;
        }

        float ViewSize => 1f / zoom;
        Vector2 ViewMin => panCenter - Vector2.one * (0.5f / zoom);

        void ClampView()
        {
            zoom = Mathf.Clamp(zoom, 1f, 16f);
            float h = 0.5f / zoom;
            panCenter = new Vector2(Mathf.Clamp(panCenter.x, h, 1f - h), Mathf.Clamp(panCenter.y, h, 1f - h));
        }

        Vector2 UvToRect(Vector2 uv, Rect r)
        {
            Vector2 vmin = ViewMin; float vs = ViewSize;
            return new Vector2(r.x + (uv.x - vmin.x) / vs * r.width, r.y + (uv.y - vmin.y) / vs * r.height);
        }

        Vector2 RectToUv(Vector2 s, Rect r)
        {
            Vector2 vmin = ViewMin; float vs = ViewSize;
            return new Vector2(vmin.x + (s.x - r.x) / r.width * vs, vmin.y + (s.y - r.y) / r.height * vs);
        }

        void DrawView(Rect r, Texture t)
        {
            Vector2 vmin = ViewMin; float vs = ViewSize;
            GUI.DrawTextureWithTexCoords(r, t, new Rect(vmin.x, 1f - (vmin.y + vs), vs, vs));
        }

        // Deterministic mirror: copy the source half onto the other half.
        // For the direction field the component along the mirror axis is flipped
        // so the flow stays symmetric; masks are plain value copies.
        void ApplyMirror()
        {
            PushUndoAll();
            bool isX = mirrorDir == MirrorDir.LeftToRight || mirrorDir == MirrorDir.RightToLeft;
            bool sourceLow = mirrorDir == MirrorDir.LeftToRight || mirrorDir == MirrorDir.TopToBottom;
            MirrorBakeDir(isX, sourceLow);
            MirrorStack(lenStack, isX, sourceLow);
            MirrorStack(alphaStack, isX, sourceLow);
            if (normalPreview != null) normalPreview = BuildNormalTex(Mathf.Min(exportRes, 1024));
            maskDirty = true; Repaint();
        }

        // Mirror is a groom-wide op, so every layer gets it. Coverage is mirrored with the
        // value rather than filled in - the mirrored half must be as painted as its source.
        void MirrorStack(MaskLayerStack st, bool isX, bool sourceLow)
        {
            for (int i = 0; i < st.Count; i++)
            {
                MaskLayer L = st.At(i);
                MirrorBakeBuffer(L.value, MN, isX, sourceLow);
                MirrorBakeCover(L.cover, MN, isX, sourceLow);
            }
            st.MarkDirty();
        }

        void MirrorBakeBuffer(float[] buf, int res, bool isX, bool sourceLow)
        {
            float ax = symAxis;
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                {
                    float u = ((isX ? x : y) + 0.5f) / res;
                    bool inDest = sourceLow ? u > ax : u < ax;
                    if (!inDest) continue;
                    int sx = x, sy = y;
                    if (isX) sx = Mathf.Clamp(Mathf.RoundToInt(2f * ax * res - x - 1f), 0, res - 1);
                    else sy = Mathf.Clamp(Mathf.RoundToInt(2f * ax * res - y - 1f), 0, res - 1);
                    buf[y * res + x] = buf[sy * res + sx];
                }
        }

        void MirrorBakeCover(byte[] buf, int res, bool isX, bool sourceLow)
        {
            float ax = symAxis;
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                {
                    float u = ((isX ? x : y) + 0.5f) / res;
                    bool inDest = sourceLow ? u > ax : u < ax;
                    if (!inDest) continue;
                    int sx = x, sy = y;
                    if (isX) sx = Mathf.Clamp(Mathf.RoundToInt(2f * ax * res - x - 1f), 0, res - 1);
                    else sy = Mathf.Clamp(Mathf.RoundToInt(2f * ax * res - y - 1f), 0, res - 1);
                    buf[y * res + x] = buf[sy * res + sx];
                }
        }

        void MirrorBakeDir(bool isX, bool sourceLow)
        {
            float ax = symAxis;
            for (int y = 0; y < FN; y++)
                for (int x = 0; x < FN; x++)
                {
                    float u = ((isX ? x : y) + 0.5f) / FN;
                    bool inDest = sourceLow ? u > ax : u < ax;
                    if (!inDest) continue;
                    int sx = x, sy = y;
                    if (isX) sx = Mathf.Clamp(Mathf.RoundToInt(2f * ax * FN - x - 1f), 0, FN - 1);
                    else sy = Mathf.Clamp(Mathf.RoundToInt(2f * ax * FN - y - 1f), 0, FN - 1);
                    Vector2 d = dir[sy * FN + sx];
                    if (isX) d.x = -d.x; else d.y = -d.y;
                    dir[y * FN + x] = d;
                    dirStr[y * FN + x] = dirStr[sy * FN + sx];
                }
        }

        void Ensure(ref Texture2D t, int size)
        {
            if (t != null && t.width == size) return;
            t = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        }

        // =========================================================== mesh highlight

        void SetTargetRenderer(Renderer renderer)
        {
            targetRenderer = renderer;
            uvChannel = 0;
            ClearUvBackground();
            if (FindUvMesh() != null) GenerateUvBackground();
            else CacheMesh();
        }

        static bool HasUvChannel(Mesh mesh, int channel)
        {
            return mesh != null && channel >= 0 && channel < 8 &&
                mesh.HasVertexAttribute((UnityEngine.Rendering.VertexAttribute)
                    ((int)UnityEngine.Rendering.VertexAttribute.TexCoord0 + channel));
        }

        void DrawUvChannelSelector()
        {
            Mesh mesh = FindUvMesh();
            var labels = new System.Collections.Generic.List<GUIContent>();
            var channels = new System.Collections.Generic.List<int>();
            // Keep UV0 as the default even when absent; never silently display another map.
            labels.Add(new GUIContent(mesh != null && !HasUvChannel(mesh, 0) ? "UV0 (missing)" : "UV0"));
            channels.Add(0);
            for (int channel = 1; channel < 8; channel++)
            {
                if (!HasUvChannel(mesh, channel)) continue;
                labels.Add(new GUIContent("UV" + channel));
                channels.Add(channel);
            }
            using (new EditorGUI.DisabledScope(mesh == null))
            {
                int selected = EditorGUILayout.IntPopup(new GUIContent("UV map",
                    "Display one UV channel in the paint window and use it for the Scene view marker. Defaults to UV0. This does not change the material's UV settings."),
                    uvChannel, labels.ToArray(), channels.ToArray());
                if (selected != uvChannel)
                {
                    uvChannel = selected;
                    GenerateUvBackground();
                }
            }
            if (mesh != null && !HasUvChannel(mesh, uvChannel))
                EditorGUILayout.HelpBox("This mesh has no UV" + uvChannel + ". Select an available UV map.", MessageType.Info);
        }

        Vector2[] ReadSelectedUvs(Mesh mesh)
        {
            var uvs = new System.Collections.Generic.List<Vector2>();
            if (HasUvChannel(mesh, uvChannel)) mesh.GetUVs(uvChannel, uvs);
            return uvs.ToArray();
        }

        void ClearUvBackground()
        {
            if (uvBackground == null) return;
            if (bg == uvBackground) bg = null;
            DestroyImmediate(uvBackground);
            uvBackground = null;
        }

        void CacheMesh()
        {
            meshVerts = null; meshUVs = null; meshNorms = null; meshTris = null; meshSkinned = false;
            sceneHits.Clear(); sceneNormals.Clear();
            SceneView.RepaintAll();
            Mesh source = FindUvMesh();
            if (!HasUvChannel(source, uvChannel)) uvChannel = 0;
            if (source == null) return;
            Mesh m = source;
            var smr = targetRenderer as SkinnedMeshRenderer;
            if (smr != null) { m = new Mesh(); smr.BakeMesh(m); meshSkinned = true; }
            // UVs belong to the source mesh; baking is only needed for posed positions/normals.
            meshVerts = m.vertices; meshUVs = ReadSelectedUvs(source); meshNorms = m.normals; meshTris = source.triangles;
            if (meshSkinned) DestroyImmediate(m);
        }

        // Render the mesh's UV islands into a wireframe texture and use it as the background.
        void GenerateUvBackground()
        {
            CacheMesh();
            ClearUvBackground();
            Mesh m = FindUvMesh();
            if (m == null)
            {
                EditorUtility.DisplayDialog("Fur Grooming Tool",
                    "Assign the mesh's renderer in 'Mesh (renderer)' first (drag the avatar's mesh object from the Hierarchy).", "OK");
                return;
            }
            Vector2[] uvs = meshUVs; int[] tris = meshTris;
            if (uvs == null || uvs.Length == 0 || uvs.Length != m.vertexCount)
            {
                Repaint();
                return;
            }
            const int size = 1024;
            var px = new Color32[size * size]; // transparent background; canvas dark shows through
            Color32 col = new Color32(210, 210, 210, 255);
            for (int i = 0; i < tris.Length; i += 3)
            {
                Vector2 a = uvs[tris[i]], b = uvs[tris[i + 1]], c = uvs[tris[i + 2]];
                DrawLinePx(px, size, size, a, b, col);
                DrawLinePx(px, size, size, b, c, col);
                DrawLinePx(px, size, size, c, a, col);
            }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            tex.SetPixels32(px); tex.Apply();
            uvBackground = tex;
            bg = uvBackground;
            Repaint();
        }

        Mesh FindUvMesh()
        {
            if (targetRenderer == null) return null;
            var s = targetRenderer as SkinnedMeshRenderer;
            if (s != null) return s.sharedMesh;
            var f = targetRenderer.GetComponent<MeshFilter>();
            return f != null ? f.sharedMesh : null;
        }

        static void DrawLinePx(Color32[] px, int w, int h, Vector2 ua, Vector2 ub, Color32 col)
        {
            int x0 = Mathf.RoundToInt(ua.x * (w - 1)), y0 = Mathf.RoundToInt(ua.y * (h - 1));
            int x1 = Mathf.RoundToInt(ub.x * (w - 1)), y1 = Mathf.RoundToInt(ub.y * (h - 1));
            int dx = Mathf.Abs(x1 - x0), dy = -Mathf.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                if ((uint)x0 < (uint)w && (uint)y0 < (uint)h) px[y0 * w + x0] = col;
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        // Map the brush UV to every matching spot on the mesh (symmetric/overlapping
        // UVs yield several hits) and store world positions for the Scene view marker.
        void UpdateSceneHit(Vector2 toolUv)
        {
            sceneHits.Clear(); sceneNormals.Clear();
            if (showOnMesh && targetRenderer != null && meshVerts != null && meshUVs != null && meshUVs.Length > 0 && meshUVs.Length == meshVerts.Length)
            {
                Vector2 q = new Vector2(toolUv.x, 1f - toolUv.y); // tool y is top-down; mesh V is bottom-up
                Transform t = targetRenderer.transform;
                Matrix4x4 mtx = meshSkinned ? Matrix4x4.TRS(t.position, t.rotation, Vector3.one) : targetRenderer.localToWorldMatrix;
                bool hasN = meshNorms != null && meshNorms.Length == meshVerts.Length;
                for (int i = 0; i < meshTris.Length; i += 3)
                {
                    int a = meshTris[i], b = meshTris[i + 1], c = meshTris[i + 2];
                    if (Bary(q, meshUVs[a], meshUVs[b], meshUVs[c], out float wa, out float wb, out float wc))
                    {
                        Vector3 local = meshVerts[a] * wa + meshVerts[b] * wb + meshVerts[c] * wc;
                        sceneHits.Add(mtx.MultiplyPoint3x4(local));
                        if (hasN)
                        {
                            Vector3 ln = meshNorms[a] * wa + meshNorms[b] * wb + meshNorms[c] * wc;
                            Vector3 wn = meshSkinned ? t.rotation * ln : targetRenderer.localToWorldMatrix.MultiplyVector(ln);
                            sceneNormals.Add(wn.sqrMagnitude > 1e-9f ? wn.normalized : Vector3.up);
                        }
                        else sceneNormals.Add(Vector3.zero);
                        if (sceneHits.Count >= 16) break;
                    }
                }
            }
            FollowCamera();
            SceneView.RepaintAll();
        }

        void OnSceneGUI(SceneView sv)
        {
            DrawResolverDebug();
            if (!showOnMesh || sceneHits.Count == 0) return;
            Vector3 cam = sv.camera != null ? sv.camera.transform.position : Vector3.zero;
            Handles.color = markerColor;
            for (int i = 0; i < sceneHits.Count; i++)
            {
                Vector3 p = sceneHits[i];
                float s = HandleUtility.GetHandleSize(p) * markerSize;
                Vector3 n = (cam - p).sqrMagnitude > 1e-6f ? (cam - p).normalized : Vector3.up;
                switch (markerShape)
                {
                    case MarkerShape.Sphere: Handles.SphereHandleCap(0, p, Quaternion.identity, s * 2f, EventType.Repaint); break;
                    case MarkerShape.Cube: Handles.CubeHandleCap(0, p, Quaternion.identity, s * 2f, EventType.Repaint); break;
                    case MarkerShape.Disc: Handles.DrawSolidDisc(p, n, s); break;
                    case MarkerShape.Cross:
                        float c = s * 1.6f;
                        Handles.DrawLine(p - Vector3.right * c, p + Vector3.right * c);
                        Handles.DrawLine(p - Vector3.up * c, p + Vector3.up * c);
                        Handles.DrawLine(p - Vector3.forward * c, p + Vector3.forward * c);
                        break;
                }
            }
        }

        void FollowCamera()
        {
            if (!followCam || sceneHits.Count == 0) return;
            SceneView sv = SceneView.lastActiveSceneView;
            if (sv == null) return;
            Quaternion rot = sv.rotation;
            if (alignToNormal && sceneNormals.Count > 0 && sceneNormals[0].sqrMagnitude > 1e-6f)
                rot = Quaternion.LookRotation(-sceneNormals[0], Vector3.up);
            sv.cameraSettings.fieldOfView = fov;
            sv.LookAt(sceneHits[0], rot, camDistance, sv.orthographic, false); // instant:false = smoothed
        }

        static bool Bary(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out float wa, out float wb, out float wc)
        {
            Vector2 v0 = b - a, v1 = c - a, v2 = p - a;
            float den = v0.x * v1.y - v1.x * v0.y;
            wa = wb = wc = 0f;
            if (Mathf.Abs(den) < 1e-12f) return false;
            wb = (v2.x * v1.y - v1.x * v2.y) / den;
            wc = (v0.x * v2.y - v2.x * v0.y) / den;
            wa = 1f - wb - wc;
            const float e = -0.0005f;
            return wa >= e && wb >= e && wc >= e;
        }
    }
}
