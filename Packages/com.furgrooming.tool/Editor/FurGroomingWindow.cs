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

        enum Tab { Direction, Length, Alpha }
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
        [SerializeField] float[] lenBuf;
        [SerializeField] LenMode lenMode = LenMode.Paint;
        [SerializeField] float lenValue = 1f;

        // ---- alpha layer
        [SerializeField] float[] alphaBuf;
        [SerializeField] AlphaMode alphaMode = AlphaMode.White;
        [SerializeField] float alphaThreshold = 0.5f;
        [SerializeField] bool alphaAA = true;

        // ---- shared
        [SerializeField] Tab tab = Tab.Direction;
        [SerializeField] Texture2D bg;
        [SerializeField] Material targetMat;
        [SerializeField] Renderer targetRenderer;
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

        // ---- runtime
        Texture2D normalPreview, lenTex, alphaTex;
        bool maskDirty = true;
        bool painting, panning, paintErase;
        Vector2 lastUv, lastDir = Vector2.right;
        Vector2 gradStart, gradEnd;
        bool gradActive;
        float canvasSize = REF;
        Vector3[] meshVerts; Vector2[] meshUVs; Vector3[] meshNorms; int[] meshTris; bool meshSkinned;
        readonly System.Collections.Generic.List<Vector3> sceneHits = new System.Collections.Generic.List<Vector3>();
        readonly System.Collections.Generic.List<Vector3> sceneNormals = new System.Collections.Generic.List<Vector3>();
        [SerializeField] bool foldBrush = true, foldTool = true, foldSym = false, foldExport = false, foldScene = false;
        Vector2 settingsScroll;

        [MenuItem("Tools/Fur Grooming Tool")]
        static void Open() => GetWindow<FurGroomingWindow>("Fur Grooming");

        void OnEnable()
        {
            wantsMouseMove = true;
            minSize = new Vector2(620, 720);
            if (dir == null || dir.Length != FN * FN) dir = new Vector2[FN * FN];
            if (dirStr == null || dirStr.Length != FN * FN) dirStr = new float[FN * FN];
            if (lenBuf == null || lenBuf.Length != MN * MN) lenBuf = new float[MN * MN];
            if (alphaBuf == null || alphaBuf.Length != MN * MN) alphaBuf = new float[MN * MN];
            maskDirty = true;
            SceneView.duringSceneGui += OnSceneGUI;
            if (targetRenderer != null) CacheMesh();
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
        }

        // =========================================================== GUI

        void OnGUI()
        {
            Tab nt = (Tab)GUILayout.Toolbar((int)tab, new[] { "Direction", "Length", "Alpha" });
            if (nt != tab) { tab = nt; maskDirty = true; }

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
                if (rendNew != targetRenderer) { targetRenderer = rendNew; CacheMesh(); if (targetRenderer != null) GenerateUvBackground(); }
                if (ColorButton("Refresh UV", colTeal, GUILayout.Width(90))) GenerateUvBackground();
                EditorGUILayout.EndHorizontal();
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
            if (tab == Tab.Direction && ColorButton("Generate preview", colAmber)) normalPreview = BuildNormalTex(Mathf.Min(exportRes, 1024));
            if (ColorButton(saveLabel, colGreen)) SaveCurrent();
            if (tab == Tab.Direction)
            {
                if (ColorButton("Save groom", colBlue)) SaveGroom();
                if (ColorButton("Load groom", colTeal)) LoadGroom();
            }
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
            lenValue = EditorGUILayout.Slider(new GUIContent("Length value", "Left-drag paints this value. Right-drag paints black (0)."), lenValue, 0f, 1f);
            brushHardness = EditorGUILayout.Slider("Brush hardness", brushHardness, 0f, 1f);
            EditorGUILayout.BeginHorizontal();
            smoothRadius = EditorGUILayout.Slider("Smooth radius", smoothRadius, 1f, 24f);
            if (ColorButton("Smooth all", colAmber, GUILayout.Width(90)))
            { SmoothBuffer(lenBuf, MN, smoothRadius); maskDirty = true; }
            EditorGUILayout.EndHorizontal();
            lengthProp = EditorGUILayout.TextField("Length property", lengthProp);
        }

        void DrawAlphaControls()
        {
            alphaMode = (AlphaMode)GUILayout.Toolbar((int)alphaMode, new[] { "Paint white (fur)", "Paint black (bald)" });
            EditorGUILayout.BeginHorizontal();
            if (ColorButton("Fill white", new Color(0.95f, 0.95f, 0.95f))) { Fill(alphaBuf, 1f); maskDirty = true; }
            if (ColorButton("Fill black", new Color(0.45f, 0.45f, 0.45f))) { Fill(alphaBuf, 0f); maskDirty = true; }
            EditorGUILayout.EndHorizontal();
            alphaThreshold = EditorGUILayout.Slider("Threshold", alphaThreshold, 0.05f, 0.95f);
            alphaAA = EditorGUILayout.ToggleLeft("Soft 1px edge (anti-alias)", alphaAA);
            alphaProp = EditorGUILayout.TextField("Alpha property", alphaProp);
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
                paintErase = e.button == 1;
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
                if (paintErase) WalkField(a, b, MN, fc => DabPaint(lenBuf, MN, fc, 0f, brushHardness));
                else if (lenMode == LenMode.Smudge) WalkField(a, b, MN, fc => DabSmudge(lenBuf, MN, fc));
                else WalkField(a, b, MN, fc => DabPaint(lenBuf, MN, fc, lenValue, brushHardness));
                maskDirty = true;
            }
            else
            {
                float target = paintErase ? 0f : (alphaMode == AlphaMode.White ? 1f : 0f);
                WalkField(a, b, MN, fc => DabPaint(alphaBuf, MN, fc, target, 1f, true));
                maskDirty = true;
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

        void DabPaint(float[] buf, int res, Vector2 fc, float target, float hardness, bool full = false)
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
                }
        }

        void DabSmudge(float[] buf, int res, Vector2 fc)
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
                    float s = 0f; int cnt = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int x = Mathf.Clamp(i + dx, 0, res - 1), y = Mathf.Clamp(j + dy, 0, res - 1);
                            s += buf[y * res + x]; cnt++;
                        }
                    buf[idx] = Mathf.Lerp(buf[idx], s / cnt, w);
                }
        }

        void CommitGradient(Vector2 uvA, Vector2 uvB)
        {
            GradientInto(uvA, uvB, true);
        }

        void GradientInto(Vector2 uvA, Vector2 uvB, bool reset)
        {
            Vector2 d = uvB - uvA; float len2 = d.sqrMagnitude;
            if (len2 < 1e-6f) return;
            for (int r = 0; r < MN; r++)
                for (int c = 0; c < MN; c++)
                {
                    Vector2 uv = new Vector2((c + 0.5f) / MN, (r + 0.5f) / MN);
                    float t = Mathf.Clamp01(Vector2.Dot(uv - uvA, d) / len2);
                    int idx = r * MN + c;
                    lenBuf[idx] = reset ? t : Mathf.Max(lenBuf[idx], t);
                }
        }

        // =========================================================== buffers / fx

        void ClearLayer()
        {
            if (tab == Tab.Direction) { System.Array.Clear(dir, 0, dir.Length); System.Array.Clear(dirStr, 0, dirStr.Length); }
            else if (tab == Tab.Length) System.Array.Clear(lenBuf, 0, lenBuf.Length);
            else System.Array.Clear(alphaBuf, 0, alphaBuf.Length);
            maskDirty = true; Repaint();
        }

        void Fill(float[] buf, float v) { for (int i = 0; i < buf.Length; i++) buf[i] = v; }

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

        float AlphaResolve(float v)
        {
            if (!alphaAA) return v >= alphaThreshold ? 1f : 0f;
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(alphaThreshold - 0.03f, alphaThreshold + 0.03f, v));
        }

        // =========================================================== textures

        void RebuildMaskTex()
        {
            Ensure(ref lenTex, MN); Ensure(ref alphaTex, MN);
            float[] src = tab == Tab.Alpha ? alphaBuf : lenBuf;
            Texture2D dst = tab == Tab.Alpha ? alphaTex : lenTex;
            var px = new Color32[MN * MN];
            for (int r = 0; r < MN; r++)
            {
                int outRow = MN - 1 - r;
                for (int c = 0; c < MN; c++)
                {
                    float v = src[r * MN + c];
                    if (tab == Tab.Alpha) v = AlphaResolve(v);
                    byte b = Enc(v);
                    px[outRow * MN + c] = new Color32(b, b, b, 255);
                }
            }
            dst.SetPixels32(px); dst.Apply();
            maskDirty = false;
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

        void SaveCurrent()
        {
            string b = string.IsNullOrEmpty(baseName) ? "Fur" : baseName;
            if (tab == Tab.Direction) SaveTexture(BuildNormalTex(exportRes), b + "_Normal", true, normalProp);
            else if (tab == Tab.Length) SaveTexture(BuildMaskTex(exportRes, lenBuf, false), b + "_Length", false, lengthProp);
            else SaveTexture(BuildMaskTex(exportRes, alphaBuf, true), b + "_Alpha", false, alphaProp);
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

        void SaveGroom()
        {
            string p = EditorUtility.SaveFilePanel("Save groom data", "", "groom", "bytes");
            if (string.IsNullOrEmpty(p)) return;
            using (var w = new BinaryWriter(File.Open(p, FileMode.Create)))
            {
                w.Write(FN); w.Write(MN);
                for (int i = 0; i < FN * FN; i++) { w.Write(dir[i].x); w.Write(dir[i].y); w.Write(dirStr[i]); }
                for (int i = 0; i < MN * MN; i++) w.Write(lenBuf[i]);
                for (int i = 0; i < MN * MN; i++) w.Write(alphaBuf[i]);
            }
            Debug.Log("[Fur] Groom saved -> " + p);
        }

        void LoadGroom()
        {
            string p = EditorUtility.OpenFilePanel("Load groom data", "", "bytes");
            if (string.IsNullOrEmpty(p)) return;
            using (var r = new BinaryReader(File.Open(p, FileMode.Open)))
            {
                if (r.ReadInt32() != FN || r.ReadInt32() != MN) { Debug.LogError("[Fur] Groom resolution mismatch."); return; }
                for (int i = 0; i < FN * FN; i++) { dir[i] = new Vector2(r.ReadSingle(), r.ReadSingle()); dirStr[i] = r.ReadSingle(); }
                for (int i = 0; i < MN * MN; i++) lenBuf[i] = r.ReadSingle();
                for (int i = 0; i < MN * MN; i++) alphaBuf[i] = r.ReadSingle();
            }
            maskDirty = true; Repaint();
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
            bool isX = mirrorDir == MirrorDir.LeftToRight || mirrorDir == MirrorDir.RightToLeft;
            bool sourceLow = mirrorDir == MirrorDir.LeftToRight || mirrorDir == MirrorDir.TopToBottom;
            MirrorBakeDir(isX, sourceLow);
            MirrorBakeBuffer(lenBuf, MN, isX, sourceLow);
            MirrorBakeBuffer(alphaBuf, MN, isX, sourceLow);
            if (normalPreview != null) normalPreview = BuildNormalTex(Mathf.Min(exportRes, 1024));
            maskDirty = true; Repaint();
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

        void CacheMesh()
        {
            meshVerts = null; meshUVs = null; meshNorms = null; meshTris = null; meshSkinned = false;
            sceneHits.Clear(); sceneNormals.Clear();
            if (targetRenderer == null) return;
            Mesh m = null;
            var smr = targetRenderer as SkinnedMeshRenderer;
            if (smr != null) { m = new Mesh(); smr.BakeMesh(m); meshSkinned = true; }
            else { var mf = targetRenderer.GetComponent<MeshFilter>(); if (mf != null) m = mf.sharedMesh; }
            if (m == null) return;
            meshVerts = m.vertices; meshUVs = m.uv; meshNorms = m.normals; meshTris = m.triangles;
        }

        // Render the mesh's UV islands into a wireframe texture and use it as the background.
        void GenerateUvBackground()
        {
            Mesh m = FindUvMesh();
            if (m == null)
            {
                EditorUtility.DisplayDialog("Fur Grooming Tool",
                    "Assign the mesh's renderer in 'Mesh (renderer)' first (drag the avatar's mesh object from the Hierarchy).", "OK");
                return;
            }
            Vector2[] uvs = m.uv; int[] tris = m.triangles;
            if (uvs == null || uvs.Length == 0)
            {
                EditorUtility.DisplayDialog("Fur Grooming Tool", "That mesh has no UV map.", "OK");
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
            bg = tex;
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
            if (showOnMesh && targetRenderer != null && meshVerts != null && meshUVs != null && meshUVs.Length > 0)
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
