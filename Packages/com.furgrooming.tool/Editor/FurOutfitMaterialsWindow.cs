// The panel for making every outfit's fur material at once.
//
// One base fur material, one body mesh, any number of outfit roots. Each root gets what
// FurOutfitBaker does for one outfit: every mesh under it is collected as clothing, the groom
// is read out of the base material's maps, and the resolver's result lands in a copy of the
// material with that outfit's length and normal maps.
//
// The panel stops at the materials and puts nothing on the avatar. Swapping them in belongs to
// whatever already drives the outfit, such as a VRC-Fury material swap. A re-bake updates the
// same assets in place, so a swap pointed at them does not have to be set up again.

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FurGroomingTool
{
    public class FurOutfitMaterialsWindow : EditorWindow
    {
        class Result { public string outfit; public Material material; public string report; }

        [SerializeField] Renderer bodyMesh;
        [SerializeField] Material baseMaterial;
        [SerializeField] List<GameObject> outfits = new List<GameObject>();

        // Named as on FurOutfitOccluder, whose inspector drawers these share.
        [SerializeField] string normalProp = "_FurVectorTex";
        [SerializeField] string lengthProp = "_FurLengthMask";
        [SerializeField] string alphaProp = "_FurMask";
        [SerializeField] float maxAngle = 70f;
        [SerializeField] bool flipG = true;
        [SerializeField] int exportRes = 1024;
        [SerializeField] string outputFolder = "";
        [SerializeField] FurOcclusionSettings occlusion = new FurOcclusionSettings { writeAlpha = false, collectDebug = false };

        readonly List<Result> results = new List<Result>();
        SerializedObject so;
        Vector2 scroll;
        static bool foldResolver = true, foldProps, foldOutput;
        static readonly int[] resValues = { 512, 1024, 2048, 4096 };
        static readonly string[] resLabels = { "512", "1024", "2048", "4096" };

        [MenuItem("Tools/Fur Outfit Materials")]
        static void Open() => GetWindow<FurOutfitMaterialsWindow>("Fur Outfit Materials");

        void OnGUI()
        {
            if (so == null) so = new SerializedObject(this);
            so.Update();
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.LabelField(
                "Makes one fur material per outfit, trimmed to fit it. Pose the avatar first - the " +
                "meshes are read as they stand. Swap the results in with VRC-Fury or your own toggle.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(2);
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(so.FindProperty("bodyMesh"));
            bool bodyChanged = EditorGUI.EndChangeCheck();
            EditorGUILayout.PropertyField(so.FindProperty("baseMaterial"), new GUIContent("Base fur material"));
            EditorGUILayout.PropertyField(so.FindProperty("outfits"), new GUIContent("Outfit roots"), true);

            EditorGUILayout.Space(2);
            foldResolver = EditorGUILayout.Foldout(foldResolver, "Resolver", true);
            if (foldResolver)
            {
                EditorGUI.indentLevel++;
                FurOutfitOccluderEditor.DrawResolver(so);
                EditorGUI.indentLevel--;
            }

            foldProps = EditorGUILayout.Foldout(foldProps, "Material properties", true);
            if (foldProps)
            {
                EditorGUI.indentLevel++;
                FurOutfitOccluderEditor.DrawMapProps(so);
                EditorGUI.indentLevel--;
            }

            foldOutput = EditorGUILayout.Foldout(foldOutput, "Output", true);
            if (foldOutput)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.BeginHorizontal();
                FurOutfitOccluderEditor.Prop(so, "outputFolder", "Folder", "Empty puts the files beside the base material.");
                if (GUILayout.Button("...", GUILayout.Width(26))) FurOutfitOccluderEditor.Browse(so);
                EditorGUILayout.EndHorizontal();
                var r = so.FindProperty("exportRes");
                r.intValue = EditorGUILayout.IntPopup("Size of new maps", r.intValue, resLabels, resValues);
                EditorGUI.indentLevel--;
            }

            so.ApplyModifiedProperties();
            if (bodyChanged && baseMaterial == null) GuessBaseMaterial();

            if (baseMaterial != null && !baseMaterial.HasProperty(normalProp) && !baseMaterial.HasProperty(lengthProp))
                EditorGUILayout.HelpBox(baseMaterial.name + " has neither " + normalProp + " nor " + lengthProp +
                                        ". Check the property names, or pick the material that carries the fur.",
                                        MessageType.Warning);

            EditorGUILayout.Space(4);
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.55f, 0.9f, 0.55f);
            using (new EditorGUI.DisabledScope(bodyMesh == null || baseMaterial == null || outfits.Count == 0))
                if (GUILayout.Button("Bake outfit materials", GUILayout.Height(26))) BakeAll();
            GUI.backgroundColor = prev;

            foreach (Result r in results)
            {
                EditorGUILayout.Space(2);
                // Not read back: the field is here to ping the material or drag it into a swap.
                EditorGUILayout.ObjectField(r.outfit, r.material, typeof(Material), false);
                EditorGUILayout.LabelField(r.report, EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        void GuessBaseMaterial()
        {
            if (bodyMesh == null) return;
            foreach (Material m in bodyMesh.sharedMaterials)
                if (m != null && m.HasProperty(normalProp)) { baseMaterial = m; return; }
        }

        void BakeAll()
        {
            results.Clear();
            string folder = FurOutfitBaker.OutputFolder(outputFolder, baseMaterial);
            if (folder == null)
            {
                results.Add(new Result { outfit = "Output", report = "Output folder must be inside this project's Assets folder." });
                return;
            }

            // Windows file names ignore case, so two outfits differing only by it would share files.
            var taken = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (GameObject root in outfits)
            {
                if (root == null) continue;
                var res = new Result { outfit = root.name };
                results.Add(res);

                if (EditorUtility.IsPersistent(root))
                {
                    res.report = "This is a prefab asset. Use the outfit in the scene, on the posed avatar.";
                    continue;
                }
                string b = FurOutfitBaker.CleanName(baseMaterial.name + "_" + root.name);
                if (!taken.Add(b))
                {
                    res.report = "Skipped: an earlier outfit in the list has this name, and would be written over.";
                    continue;
                }
                List<Renderer> cloth = FurOutfitBaker.CollectOutfit(root, bodyMesh);
                if (cloth.Count == 0)
                {
                    res.report = "No meshes found under this object.";
                    continue;
                }

                FurOcclusionReport rep;
                string error = FurOutfitBaker.BakeMaps(bodyMesh, baseMaterial, cloth, root, occlusion,
                                                       normalProp, lengthProp, alphaProp, maxAngle, flipG, exportRes,
                                                       folder, b, ref res.material, out rep);
                res.report = error ?? string.Format("{0} outfit mesh{1}.  {2}", cloth.Count, cloth.Count == 1 ? "" : "es", rep.Summary());
                Debug.Log("[Fur] " + root.name + ": " + res.report, res.material);
                if (rep != null && rep.canceled) break;
            }
            AssetDatabase.SaveAssets();
        }

        // ------------------------------------------------------------------ self-check

        // Two outfits over a flat body, each covering one half of it, run through the panel's own
        // bake. Everything is built here and removed again, so it needs no avatar.
        [MenuItem("Tools/Fur Grooming Tool/Self-check: outfit materials")]
        public static void SelfCheck()
        {
            const string dir = "Assets/__FurOutfitSelfCheck";
            var made = new List<Object>();
            var w = CreateInstance<FurOutfitMaterialsWindow>();
            made.Add(w);
            int fails = 0;
            System.Action<bool, string> check = (ok, what) =>
            {
                if (ok) return;
                fails++;
                Debug.LogError("[Fur] outfit materials self-check: " + what);
            };

            try
            {
                Directory.CreateDirectory(dir);
                string lenPath = FurMapCodec.WritePng(FurMapCodec.Blank(256, FurMapCodec.White), dir + "/Base_Length.png", false, false);
                string nrmPath = FurMapCodec.WritePng(FurMapCodec.Blank(256, FurMapCodec.FlatNormal), dir + "/Base_Normal.png", true, false);
                byte[] lenBefore = File.ReadAllBytes(lenPath);

                var mat = new Material(Shader.Find("Standard"));
                mat.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>(lenPath));
                mat.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(nrmPath));
                AssetDatabase.CreateAsset(mat, dir + "/Base.mat");

                w.normalProp = "_BumpMap";
                w.lengthProp = "_MetallicGlossMap";
                w.alphaProp = "_OcclusionMap";
                w.baseMaterial = mat;

                GameObject body = Sheet("Body", 0f, 1f, 0f, made);
                w.bodyMesh = body.GetComponent<Renderer>();
                GameObject left = new GameObject("Left"), right = new GameObject("Right");
                made.Add(left); made.Add(right);
                Sheet("Piece", 0f, 0.5f, -0.005f, made).transform.SetParent(left.transform, false);
                Sheet("Piece", 0.5f, 1f, -0.005f, made).transform.SetParent(right.transform, false);
                right.SetActive(false);   // an outfit toggled off still bakes, and stays off
                w.outfits = new List<GameObject> { left, right, null, left };

                w.BakeAll();

                check(w.results.Count == 3, "expected 3 results, not " + w.results.Count);
                Material ml = w.results[0].material, mr = w.results[1].material;
                check(ml != null && mr != null && ml != mr, "each outfit should get a material of its own");
                check(w.results[2].material == null, "a repeated outfit name should be refused");
                check(!right.activeSelf, "an outfit that was off should be off again");
                check(mat.GetTexture("_MetallicGlossMap") == AssetDatabase.LoadAssetAtPath<Texture2D>(lenPath), "the base material was changed");
                check(System.Linq.Enumerable.SequenceEqual(lenBefore, File.ReadAllBytes(lenPath)), "the base length map was changed");
                check(!File.Exists(dir + "/Base_Left_Alpha.png"), "alpha should be off by default");

                if (ml != null && mr != null)
                {
                    check(ml.GetTexture("_BumpMap") != mat.GetTexture("_BumpMap"), "the copy should have its own normal map");
                    float[] l = Halves(dir + "/Base_Left_Length.png"), r = Halves(dir + "/Base_Right_Length.png");
                    check(l[0] < 0.5f && l[1] > 0.99f, "left outfit should trim only the left half, got " + l[0] + " / " + l[1]);
                    check(r[1] < 0.5f && r[0] > 0.99f, "right outfit should trim only the right half, got " + r[0] + " / " + r[1]);

                    // VRC-Fury and friends point at the material, so a re-bake has to keep it.
                    string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(ml));
                    w.BakeAll();
                    check(w.results[0].material == ml, "a re-bake should update the same material");
                    check(AssetDatabase.AssetPathToGUID(dir + "/Base_Left.mat") == guid, "a re-bake changed the material's GUID");
                }
                foreach (Result r in w.results) Debug.Log("[Fur] " + r.outfit + ": " + r.report);
            }
            finally
            {
                foreach (Object o in made) if (o != null) DestroyImmediate(o);
                AssetDatabase.DeleteAsset(dir);
            }

            if (fails == 0) Debug.Log("[Fur] outfit materials self-check: passed.");
            else Debug.LogError("[Fur] outfit materials self-check: " + fails + " failed.");
            if (Application.isBatchMode) EditorApplication.Exit(fails == 0 ? 0 : 1);
        }

        // A flat sheet from u0 to u1 of a 1m square, whose UVs match its position.
        internal static GameObject Sheet(string name, float u0, float u1, float z, List<Object> made)
        {
            var m = new Mesh
            {
                vertices = new[] { new Vector3(u0, 0, z), new Vector3(u0, 1, z), new Vector3(u1, 1, z), new Vector3(u1, 0, z) },
                uv = new[] { new Vector2(u0, 0), new Vector2(u0, 1), new Vector2(u1, 1), new Vector2(u1, 0) },
                normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = m;
            go.AddComponent<MeshRenderer>();
            made.Add(m); made.Add(go);
            return go;
        }

        // Mean red of the left and right halves, staying clear of the seam between them.
        internal static float[] Halves(string path)
        {
            var t = new Texture2D(2, 2);
            t.LoadImage(File.ReadAllBytes(path));
            float a = 0f, b = 0f;
            int n = 0;
            for (int y = 16; y < t.height - 16; y += 8)
            {
                a += t.GetPixelBilinear(0.25f, y / (float)t.height).r;
                b += t.GetPixelBilinear(0.75f, y / (float)t.height).r;
                n++;
            }
            DestroyImmediate(t);
            return new[] { a / n, b / n };
        }
    }
}
