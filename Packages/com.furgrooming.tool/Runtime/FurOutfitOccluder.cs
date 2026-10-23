// Per-outfit fur occlusion, driven from the outfit root.
//
// Drop this on the root of an outfit, press Bake, and the fur on the avatar underneath is
// resolved against every mesh the outfit contains: a fresh set of fur maps is written, a
// copy of the fur material is pointed at them, and - if a bool avatar parameter is named -
// an animator layer is built that swaps that copy in while the parameter is on, so the
// groomed-down fur follows the outfit instead of having to be swapped by hand.
//
// The groom is read back out of the maps already assigned to the avatar's fur material, so
// the paint window does not have to be open and no groom file has to live beside the scene.
// Only the maps are needed; the originals are never written to.
//
// This component holds settings only. All the work is in FurOutfitBaker (editor side).

using UnityEngine;

namespace FurGroomingTool
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Fur Grooming/Fur Outfit Occluder")]
    public class FurOutfitOccluder : MonoBehaviour
    {
        [Tooltip("The mesh the fur grows on - the avatar's body renderer. Every mesh under this " +
                 "outfit root is treated as clothing to groom the fur away from.")]
        public Renderer furRenderer;

        [Tooltip("Which material slot on the fur mesh carries the fur.")]
        public int materialIndex;

        [Tooltip("The fur material to read the groom from and copy. Left empty, the slot above is used.")]
        public Material sourceMaterial;

        [Tooltip("The copy the bake writes. Created on the first bake; kept and updated after that.")]
        public Material bakedMaterial;

        [Tooltip("The animator controller the swap layer is built in - the avatar's FX controller.")]
        public RuntimeAnimatorController controller;

        [Tooltip("The bool avatar parameter that turns this outfit on. The swap layer listens to it, " +
                 "so the trimmed fur follows the same parameter as the outfit itself. Optional.")]
        public string parameter = "";

        [Tooltip("Layer the swap states live in. Empty names it after the fur mesh and slot, which is " +
                 "what lets several outfits share one layer instead of overriding each other.")]
        public string layerName = "";

        [Tooltip("Root the animated paths are relative to - the object the animator sits on.")]
        public Transform avatarRoot;

        [Tooltip("Material properties the maps are read from and written to. Same defaults as the paint window.")]
        public string normalProp = "_FurVectorTex";
        public string lengthProp = "_FurLengthMask";
        public string alphaProp = "_FurMask";

        [Tooltip("The tilt a fully white direction texel means, in degrees. Must match the Direction " +
                 "tab's cap the normal map was painted with, or the groom is read back at the wrong angle.")]
        public float maxAngle = 70f;

        [Tooltip("Green channel convention. On for Unity/liltoon, as in the paint window.")]
        public bool flipG = true;

        [Tooltip("Size of a map the bake has to create because the fur material has none. Maps it " +
                 "does have are written at their own size.")]
        public int exportRes = 1024;

        [Tooltip("Folder for the maps and the material copy. Empty puts them beside the source material.")]
        public string outputFolder = "";

        [Tooltip("Base name for the maps and the material copy. Empty uses this object's name.")]
        public string mapName = "";

        public FurOcclusionSettings occlusion = new FurOcclusionSettings();

        [HideInInspector] public string lastReport = "";

        // The fur material actually used: the explicit one, else the slot on the fur mesh.
        public Material ResolvedSource()
        {
            if (sourceMaterial != null) return sourceMaterial;
            if (furRenderer == null) return null;
            Material[] ms = furRenderer.sharedMaterials;
            return materialIndex >= 0 && materialIndex < ms.Length ? ms[materialIndex] : null;
        }

        // A fresh component guesses the two things that are tedious to hunt for: the animator
        // root, and which renderer outside this outfit wears a fur material.
        void Reset()
        {
            occlusion.collectDebug = false;
            Animator anim = GetComponentInParent<Animator>();
            avatarRoot = anim != null ? anim.transform : transform.root;
            if (anim != null) controller = anim.runtimeAnimatorController;
            if (avatarRoot == null) return;

            foreach (Renderer r in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (r.transform == transform || r.transform.IsChildOf(transform)) continue; // that is the outfit
                Material[] ms = r.sharedMaterials;
                for (int i = 0; i < ms.Length; i++)
                    if (ms[i] != null && ms[i].HasProperty(normalProp))
                    { furRenderer = r; materialIndex = i; return; }
            }
        }
    }
}
