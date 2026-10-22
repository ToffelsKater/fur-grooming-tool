// The bake behind FurOutfitOccluder, and its inspector.
//
// One press does the whole round trip for one outfit:
//
//   1. every mesh under the outfit root is collected as clothing
//   2. the groom is read back out of the maps on the avatar's fur material
//   3. the occlusion resolver trims and leans the fur out of that clothing
//   4. the three maps are written next to a copy of the fur material, which uses them
//   5. an animator layer is built that swaps the copy in while a bool parameter is on
//
// Nothing is written to the material or the maps the groom was read from, so a bake can be
// repeated, and re-baking after more painting picks the new groom up. Step 5 is the only
// part that needs anything from the avatar's animator; without a parameter the copy is
// still built and can be driven by hand, or by a Modular Avatar material setter.

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace FurGroomingTool
{
    public static class FurOutfitBaker
    {
        const int FN = 256;   // direction field resolution, as in the paint window
        const int MN = 512;   // mask buffer resolution

        public static List<Renderer> CollectOutfit(FurOutfitOccluder c)
        {
            var list = new List<Renderer>();
            if (c == null) return list;
            foreach (Renderer r in c.GetComponentsInChildren<Renderer>(true))
            {
                if (r == c.furRenderer) continue;
                if (!(r is SkinnedMeshRenderer) && r.GetComponent<MeshFilter>() == null) continue;
                if (!list.Contains(r)) list.Add(r);
            }
            return list;
        }

        public static bool Bake(FurOutfitOccluder c)
        {
            if (c == null) return false;
            if (c.furRenderer == null) return Fail(c, "Assign the fur mesh - the avatar's body renderer.");

            Material src = c.ResolvedSource();
            if (src == null) return Fail(c, "The fur mesh has no material in slot " + c.materialIndex + ".");

            List<Renderer> cloth = CollectOutfit(c);
            if (cloth.Count == 0) return Fail(c, "No meshes found under " + c.name + ". Put this component on the outfit root.");

            string folder = OutputFolder(c, src);
            if (folder == null) return Fail(c, "Output folder must be inside this project's Assets folder.");
            string b = BaseName(c);

            // The groom lives in the maps. Whatever is missing starts at "fur everywhere, full
            // length, straight out", which is what an unpainted channel means to the shader too.
            var dir = new Vector2[FN * FN];
            var dirStr = new float[FN * FN];
            var len = new float[MN * MN];
            var alpha = new float[MN * MN];
            for (int i = 0; i < len.Length; i++) { len[i] = 1f; alpha[i] = 1f; }

            string err;
            Texture2D nrmMap = FurMapCodec.LoadMap(src, c.normalProp, true, out err);
            if (err != null) return Fail(c, err);
            Texture2D lenMap = FurMapCodec.LoadMap(src, c.lengthProp, false, out err);
            if (err != null) { FurMapCodec.Release(nrmMap); return Fail(c, err); }
            Texture2D alphaMap = FurMapCodec.LoadMap(src, c.alphaProp, false, out err);
            if (err != null) { FurMapCodec.Release(nrmMap); FurMapCodec.Release(lenMap); return Fail(c, err); }

            // Refuse to write over the maps we just read: that would make the first bake
            // irreversible and every later one cumulative.
            string clash = Clash(folder, b, nrmMap, lenMap, alphaMap, src, c);
            if (clash != null)
            {
                FurMapCodec.Release(nrmMap); FurMapCodec.Release(lenMap); FurMapCodec.Release(alphaMap);
                return Fail(c, clash);
            }

            if (nrmMap != null) FurMapCodec.DecodeNormal(nrmMap, dir, dirStr, FN, c.maxAngle, c.flipG);
            if (lenMap != null) FurMapCodec.DecodeMask(lenMap, len, MN);
            if (alphaMap != null) FurMapCodec.DecodeMask(alphaMap, alpha, MN);
            FurMapCodec.Release(nrmMap); FurMapCodec.Release(lenMap); FurMapCodec.Release(alphaMap);

            FurOcclusionSettings s = c.occlusion.Clone();
            s.collectDebug = false;

            // An outfit is usually sitting toggled off while it is being set up, and a disabled
            // renderer has nothing dependable to bake a posed mesh from. Switch it on for the
            // measurement and put it back exactly as it was.
            bool wasActive = c.gameObject.activeSelf;
            FurOcclusionReport rep;
            if (!wasActive) c.gameObject.SetActive(true);
            try
            {
                rep = FurOcclusionResolver.Resolve(
                    c.furRenderer, cloth, dir, dirStr, FN,
                    len, s.writeAlpha ? alpha : null, MN,
                    c.maxAngle, c.flipG, s, null, null);
            }
            finally { if (!wasActive) c.gameObject.SetActive(false); }

            if (rep.error != null) return Fail(c, rep.error);
            if (rep.canceled) return Fail(c, "Canceled. Nothing was written.");

            int res = Mathf.Clamp(Mathf.ClosestPowerOfTwo(c.exportRes), 64, 4096);
            string nPath = FurMapCodec.WritePng(FurMapCodec.NormalTex(dir, dirStr, FN, res, c.maxAngle, c.flipG),
                                                folder + "/" + b + "_Normal.png", true);
            string lPath = FurMapCodec.WritePng(FurMapCodec.MaskTex(len, MN, res),
                                                folder + "/" + b + "_Length.png", false);
            string aPath = s.writeAlpha
                ? FurMapCodec.WritePng(FurMapCodec.MaskTex(alpha, MN, res), folder + "/" + b + "_Alpha.png", false)
                : null;

            Undo.RecordObject(c, "Bake outfit fur");
            Material baked = EnsureCopy(c, src, folder + "/" + b + ".mat");
            Assign(baked, c.normalProp, nPath);
            Assign(baked, c.lengthProp, lPath);
            Assign(baked, c.alphaProp, aPath);
            EditorUtility.SetDirty(baked);

            string toggle = Wire(c, baked, src, folder, b);
            AssetDatabase.SaveAssets();
            EditorUtility.SetDirty(c);

            c.lastReport = string.Format("{0} outfit mesh{1}.  {2}\n{3} -> {4}  {5}",
                cloth.Count, cloth.Count == 1 ? "" : "es", rep.Summary(),
                src.name, baked.name, toggle);
            Debug.Log("[Fur] " + c.name + ": " + c.lastReport, baked);
            return true;
        }

        // ------------------------------------------------------------------ pieces

        static Material EnsureCopy(FurOutfitOccluder c, Material src, string path)
        {
            Material baked = c.bakedMaterial;
            if (baked == null) baked = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (baked == null)
            {
                baked = new Material(src);
                AssetDatabase.CreateAsset(baked, path);
            }
            else
            {
                // Keep the copy following the original, so edits to the avatar's material
                // (colour, fur settings, anything) survive a re-bake. Maps are set after.
                if (baked.shader != src.shader) baked.shader = src.shader;
                baked.CopyPropertiesFromMaterial(src);
            }
            c.bakedMaterial = baked;
            return baked;
        }

        static void Assign(Material m, string prop, string path)
        {
            if (path == null || string.IsNullOrEmpty(prop) || !m.HasProperty(prop)) return;
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null) return;
            m.SetTexture(prop, tex);
            if (prop == "_BumpMap" && m.HasProperty("_UseBumpMap")) m.SetFloat("_UseBumpMap", 1f);
        }

        // The swap: a layer that listens to one bool avatar parameter. "None" holds the original
        // material and is the default state; each outfit gets a state of its own holding its
        // baked copy, reached while that outfit's parameter is on.
        //
        // The layer is named after the fur mesh and slot, not after the outfit, so every outfit
        // on an avatar shares it. That matters: two layers both animating one material slot would
        // fight, and the higher one would always win, which is how per-outfit material swaps
        // usually come apart. Sharing one layer means the states take turns instead. Two outfits
        // on at once still only get one of the two swaps, which is all a single slot can hold.
        //
        // Each state's clip is one object-reference key at t=0 - the same curve the animation
        // window writes when a material is dragged onto a renderer slot, so it needs no runtime
        // script and survives an avatar upload.
        static string Wire(FurOutfitOccluder c, Material baked, Material src, string folder, string b)
        {
            if (string.IsNullOrEmpty(c.parameter))
                return "no avatar parameter set - drive " + baked.name + " yourself, or point a Modular Avatar material setter at it.";

            var ac = c.controller as AnimatorController;
            if (ac == null)
                return "WARNING: assign the avatar's FX animator controller, and " + c.parameter + " will be wired up on the next bake.";

            Transform root = c.avatarRoot;
            if (root == null)
            {
                Animator anim = c.furRenderer.GetComponentInParent<Animator>();
                root = anim != null ? anim.transform : c.furRenderer.transform.root;
            }
            if (!c.furRenderer.transform.IsChildOf(root))
                return "WARNING: the fur mesh is not under " + root.name + ", so no swap could be built.";

            foreach (AnimatorControllerParameter p in ac.parameters)
                if (p.name == c.parameter && p.type != AnimatorControllerParameterType.Bool)
                    return "WARNING: " + c.parameter + " already exists in " + ac.name + " and is not a Bool.";
            if (!HasParameter(ac, c.parameter)) ac.AddParameter(c.parameter, AnimatorControllerParameterType.Bool);

            string path = AnimationUtility.CalculateTransformPath(c.furRenderer.transform, root);
            var binding = EditorCurveBinding.PPtrCurve(path, c.furRenderer.GetType(),
                                                       "m_Materials.Array.data[" + c.materialIndex + "]");

            string lname = string.IsNullOrEmpty(c.layerName)
                ? "Fur " + c.furRenderer.name + " " + c.materialIndex
                : c.layerName;
            AnimatorStateMachine sm = EnsureLayer(ac, lname);

            AnimatorState none = EnsureState(ac, sm, "None", folder + "/" + lname + "_None.anim", binding, src);
            AnimatorState on = EnsureState(ac, sm, c.name, folder + "/" + b + "_On.anim", binding, baked);
            sm.defaultState = none;

            Link(none, on, c.parameter, true);
            Link(on, none, c.parameter, false);

            EditorUtility.SetDirty(ac);
            return "swapped by " + c.parameter + " in " + ac.name + ", layer " + lname +
                   ", on " + path + " slot " + c.materialIndex + ".";
        }

        static bool HasParameter(AnimatorController ac, string name)
        {
            foreach (AnimatorControllerParameter p in ac.parameters) if (p.name == name) return true;
            return false;
        }

        // AddLayer(string) leaves the layer at weight 0, which would make the swap do nothing, so
        // the layer is built by hand - including its state machine, which has to be put into the
        // controller asset itself or it is lost on reload.
        static AnimatorStateMachine EnsureLayer(AnimatorController ac, string name)
        {
            foreach (AnimatorControllerLayer l in ac.layers)
                if (l.name == name && l.stateMachine != null) return l.stateMachine;

            var sm = new AnimatorStateMachine { name = name, hideFlags = HideFlags.HideInHierarchy };
            AssetDatabase.AddObjectToAsset(sm, ac);
            ac.AddLayer(new AnimatorControllerLayer
            {
                name = name,
                defaultWeight = 1f,
                blendingMode = AnimatorLayerBlendingMode.Override,
                stateMachine = sm
            });
            return sm;
        }

        static AnimatorState EnsureState(AnimatorController ac, AnimatorStateMachine sm, string name,
                                         string clipPath, EditorCurveBinding binding, Material m)
        {
            AnimatorState st = null;
            foreach (ChildAnimatorState cs in sm.states)
                if (cs.state != null && cs.state.name == name) { st = cs.state; break; }
            if (st == null) st = sm.AddState(name);

            var clip = st.motion as AnimationClip;
            if (clip == null)
            {
                clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                if (clip == null)
                {
                    clip = new AnimationClip { name = System.IO.Path.GetFileNameWithoutExtension(clipPath) };
                    AssetDatabase.CreateAsset(clip, clipPath);
                }
                st.motion = clip;
            }
            AnimationUtility.SetObjectReferenceCurve(clip, binding,
                new[] { new ObjectReferenceKeyframe { time = 0f, value = m } });
            EditorUtility.SetDirty(clip);
            return st;
        }

        static void Link(AnimatorState from, AnimatorState to, string parameter, bool on)
        {
            foreach (AnimatorStateTransition old in from.transitions)
                if (old.destinationState == to) from.RemoveTransition(old);

            AnimatorStateTransition t = from.AddTransition(to);
            t.hasExitTime = false;
            t.exitTime = 0f;
            t.hasFixedDuration = true;
            t.duration = 0f;
            t.canTransitionToSelf = false;
            t.AddCondition(on ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0f, parameter);
        }

        static string Clash(string folder, string b, Texture2D nrm, Texture2D len, Texture2D alpha,
                            Material src, FurOutfitOccluder c)
        {
            var outPaths = new[] { folder + "/" + b + "_Normal.png", folder + "/" + b + "_Length.png",
                                   folder + "/" + b + "_Alpha.png" };
            var ins = new List<string>();
            foreach (Texture2D t in new[] { nrm, len, alpha })
                if (t != null) ins.Add(AssetDatabase.GetAssetPath(t));
            // LoadMap hands back a detached copy, so ask the material for the real assets too.
            foreach (string prop in new[] { c.normalProp, c.lengthProp, c.alphaProp })
                if (!string.IsNullOrEmpty(prop) && src.HasProperty(prop))
                    ins.Add(AssetDatabase.GetAssetPath(src.GetTexture(prop)));

            foreach (string o in outPaths)
                foreach (string i in ins)
                    if (!string.IsNullOrEmpty(i) && i.Replace('\\', '/') == o)
                        return "The bake would write over " + o + ", which is one of the maps it reads. " +
                               "Change the output folder or the base name.";
            if (AssetDatabase.GetAssetPath(src).Replace('\\', '/') == folder + "/" + b + ".mat")
                return "The material copy would write over the source material. Change the output folder or the base name.";
            return null;
        }

        static string OutputFolder(FurOutfitOccluder c, Material src)
        {
            string f = (c.outputFolder ?? "").Replace('\\', '/').TrimEnd('/');
            if (f.Length == 0)
            {
                string p = AssetDatabase.GetAssetPath(src);
                f = string.IsNullOrEmpty(p) ? "Assets" : System.IO.Path.GetDirectoryName(p).Replace('\\', '/');
            }
            if (f != "Assets" && !f.StartsWith("Assets/")) return null;
            if (!System.IO.Directory.Exists(f)) System.IO.Directory.CreateDirectory(f);
            return f;
        }

        static string BaseName(FurOutfitOccluder c)
        {
            string n = string.IsNullOrEmpty(c.mapName) ? c.name + "_Fur" : c.mapName;
            foreach (char bad in System.IO.Path.GetInvalidFileNameChars()) n = n.Replace(bad, '_');
            return n.Trim().Length == 0 ? "Outfit_Fur" : n.Trim();
        }

        static bool Fail(FurOutfitOccluder c, string msg)
        {
            c.lastReport = msg;
            EditorUtility.SetDirty(c);
            EditorUtility.DisplayDialog("Fur Outfit Occluder", msg, "OK");
            return false;
        }
    }

    [CustomEditor(typeof(FurOutfitOccluder))]
    public class FurOutfitOccluderEditor : Editor
    {
        static bool foldProps, foldResolver = true, foldOutput;
        static readonly int[] resValues = { 512, 1024, 2048, 4096 };
        static readonly string[] resLabels = { "512", "1024", "2048", "4096" };

        public override void OnInspectorGUI()
        {
            var c = (FurOutfitOccluder)target;
            serializedObject.Update();

            EditorGUILayout.LabelField(
                "Trims the avatar's fur out of every mesh under this object, into a copy of the fur " +
                "material that the outfit's toggle swaps in. Pose the avatar first - both meshes are " +
                "read as they stand.", EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(2);
            P("furRenderer");
            DrawSlot(c);
            P("sourceMaterial");

            Material src = c.ResolvedSource();
            if (c.furRenderer != null && src == null)
                EditorGUILayout.HelpBox("No material in that slot.", MessageType.Warning);
            else if (src != null && !src.HasProperty(c.normalProp) && !src.HasProperty(c.lengthProp))
                EditorGUILayout.HelpBox(src.name + " has neither " + c.normalProp + " nor " + c.lengthProp +
                                        ". Check the property names, or pick the material that carries the fur.",
                                        MessageType.Warning);

            int n = FurOutfitBaker.CollectOutfit(c).Count;
            EditorGUILayout.LabelField("Outfit meshes", n + (n == 1 ? " mesh" : " meshes") + " under this object");

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Swap", EditorStyles.boldLabel);
            P("parameter", "Avatar parameter");
            DrawParameter(c);
            P("controller", "FX controller");
            P("avatarRoot");
            P("layerName", "Layer");
            if (string.IsNullOrEmpty(c.parameter))
                EditorGUILayout.HelpBox("Without a parameter the bake still builds the material copy - drive it " +
                                        "with your own toggle, or with a Modular Avatar material setter.",
                                        MessageType.Info);
            else if (!(c.controller is UnityEditor.Animations.AnimatorController))
                EditorGUILayout.HelpBox("Assign the avatar's FX animator controller to have the swap layer built there.",
                                        MessageType.Info);

            EditorGUILayout.Space(2);
            foldResolver = EditorGUILayout.Foldout(foldResolver, "Resolver", true);
            if (foldResolver)
            {
                EditorGUI.indentLevel++;
                P("occlusion.furLengthMm", "Fur length (mm)",
                  "World length of a hair where the length mask is fully white. Match your shader, or the shadow is measured over the wrong distance.");
                P("occlusion.clothThicknessMm", "Cloth thickness (mm)",
                  "Fattens the clothing in every direction. Raise it if fur slips through thin or badly fitted garments.");
                P("occlusion.lengthMarginMm", "Length margin (mm)",
                  "Slack kept between the fur tip and the clothing. Raise it if fur is clean in the editor but pokes through once the body moves.");
                var q = serializedObject.FindProperty("occlusion.rayQuality");
                q.intValue = EditorGUILayout.IntPopup("Ray quality", q.intValue,
                    new[] { "Fast (11 rays)", "Medium (25 rays)", "Fine (55 rays)" }, new[] { 0, 1, 2 });
                P("occlusion.maxTiltAngle", "Max tilt angle",
                  "Hard cap on how far a hair may lean from the surface normal. Also limited by the angle below, since that is all the normal map can encode.");
                P("occlusion.shadowThreshold", "Shadow threshold",
                  "How shadowed a spot must be before its fur is touched at all.");
                P("occlusion.combStrength", "Comb into open space",
                  "How hard the fur is steered towards where there is still room. 0 keeps the groom and only shortens.");
                P("occlusion.smoothPasses", "Smooth result", "3x3 blur passes over the texels the resolver touched.");
                P("occlusion.writeAlpha", "Culled fur -> alpha black",
                  "Punch fully trimmed fur out of the alpha mask, so no shell is drawn there at all.");
                EditorGUI.indentLevel--;
            }

            foldProps = EditorGUILayout.Foldout(foldProps, "Material properties", true);
            if (foldProps)
            {
                EditorGUI.indentLevel++;
                P("normalProp", "Normal map");
                P("lengthProp", "Length mask");
                P("alphaProp", "Alpha mask");
                P("maxAngle", "Max angle (deg)");
                P("flipG", "Flip G (Unity/OpenGL)");
                EditorGUI.indentLevel--;
            }

            foldOutput = EditorGUILayout.Foldout(foldOutput, "Output", true);
            if (foldOutput)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.BeginHorizontal();
                P("outputFolder", "Folder");
                if (GUILayout.Button("...", GUILayout.Width(26))) Browse(c);
                EditorGUILayout.EndHorizontal();
                P("mapName", "Base name");
                var r = serializedObject.FindProperty("exportRes");
                r.intValue = EditorGUILayout.IntPopup("Resolution", r.intValue, resLabels, resValues);
                P("bakedMaterial", "Baked copy");
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(4);
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.55f, 0.9f, 0.55f);
            if (GUILayout.Button("Bake", GUILayout.Height(26))) FurOutfitBaker.Bake(c);
            GUI.backgroundColor = prev;

            if (!string.IsNullOrEmpty(c.lastReport))
                EditorGUILayout.LabelField(c.lastReport, EditorStyles.wordWrappedMiniLabel);
        }

        // A typo here fails silently - the layer is built and never fires - so the bools the
        // controller already has are offered to pick from.
        void DrawParameter(FurOutfitOccluder c)
        {
            var ac = c.controller as AnimatorController;
            if (ac == null) return;
            var names = new List<string>();
            foreach (AnimatorControllerParameter p in ac.parameters)
                if (p.type == AnimatorControllerParameterType.Bool) names.Add(p.name);
            if (names.Count == 0) return;

            var prop = serializedObject.FindProperty("parameter");
            int cur = names.IndexOf(prop.stringValue);
            int picked = EditorGUILayout.Popup(new GUIContent("Bools in " + ac.name,
                "The bool parameters already in that controller. Picking one fills the field above."),
                cur, names.ToArray());
            if (picked != cur && picked >= 0 && picked < names.Count) prop.stringValue = names[picked];
        }

        void DrawSlot(FurOutfitOccluder c)
        {
            var p = serializedObject.FindProperty("materialIndex");
            Material[] ms = c.furRenderer != null ? c.furRenderer.sharedMaterials : null;
            if (ms == null || ms.Length < 2) { EditorGUILayout.PropertyField(p, new GUIContent("Material slot")); return; }
            var labels = new string[ms.Length];
            var values = new int[ms.Length];
            for (int i = 0; i < ms.Length; i++)
            {
                labels[i] = i + ": " + (ms[i] != null ? ms[i].name : "(none)");
                values[i] = i;
            }
            p.intValue = EditorGUILayout.IntPopup("Material slot", Mathf.Clamp(p.intValue, 0, ms.Length - 1), labels, values);
        }

        void Browse(FurOutfitOccluder c)
        {
            string start = Application.dataPath;
            string picked = EditorUtility.OpenFolderPanel("Choose output folder (inside Assets)", start, "");
            if (string.IsNullOrEmpty(picked)) return;
            picked = picked.Replace('\\', '/');
            string data = Application.dataPath.Replace('\\', '/');
            string rel = picked == data ? "Assets" : picked.StartsWith(data + "/") ? "Assets" + picked.Substring(data.Length) : null;
            if (rel == null)
            {
                EditorUtility.DisplayDialog("Fur Outfit Occluder", "Pick a folder inside this project's Assets folder.", "OK");
                return;
            }
            serializedObject.FindProperty("outputFolder").stringValue = rel;
        }

        void P(string path, string label = null, string tip = null)
        {
            var p = serializedObject.FindProperty(path);
            if (p == null) return;
            if (label == null) EditorGUILayout.PropertyField(p);
            else EditorGUILayout.PropertyField(p, new GUIContent(label, tip ?? p.tooltip));
        }
    }
}
