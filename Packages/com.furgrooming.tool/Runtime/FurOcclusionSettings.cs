// Settings for the fur occlusion resolver.
//
// Lives in the runtime assembly rather than beside the resolver so that a scene component
// can carry a set of its own - FurOutfitOccluder keeps one per outfit.

using UnityEngine;

namespace FurGroomingTool
{
    [System.Serializable]
    public class FurOcclusionSettings
    {
        public float furLengthMm = 30f;         // world length of a fully white length texel
        public int rayQuality = 1;              // 0 fast, 1 medium, 2 fine
        public float clothThicknessMm = 2f;     // fattens the clothing in every direction
        public float lengthMarginMm = 3f;       // slack kept between fur tip and clothing
        public float maxTiltAngle = 80f;        // hard cap on lean from the surface normal
        public float shadowThreshold = 0.15f;   // ignore anything less shadowed than this
        public float combStrength = 0.85f;      // how hard to steer fur into the open space
        public int smoothPasses = 1;            // 3x3 blur over the texels that changed
        public bool writeAlpha = true;          // punch culled fur out of the alpha mask
        public bool collectDebug = true;

        public FurOcclusionSettings Clone()
        {
            return (FurOcclusionSettings)MemberwiseClone();
        }
    }
}
