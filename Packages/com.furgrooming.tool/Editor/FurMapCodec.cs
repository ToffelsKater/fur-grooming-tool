// The fur maps as a format, both ways.
//
// The paint window writes these three maps out of its buffers; this reads them back into
// the same buffers. That is what lets the outfit component resolve a groom it never saw
// painted: the avatar's material is the groom, as far as the resolver is concerned.
//
// Row convention follows the rest of the tool - buffer row 0 is the top of the canvas,
// V = 1 - so the maps round-trip with what the window exports and with the way the
// resolver rasterizes UVs.
//
// Going back out, nothing is re-encoded from the buffers wholesale. The resolver's result is
// laid over the source map at the source's own size and encoding, and every pixel it does not
// touch is copied as it was - so a bake only ever trims, and the rest of the groom keeps its
// detail. Where pixels are written, the arithmetic is the same as FurGroomingWindow.BuildNormalTex
// and BuildMaskTex. If one changes, change both, or a baked outfit stops matching the window's
// own preview.

using System.IO;
using UnityEditor;
using UnityEngine;

namespace FurGroomingTool
{
    public static class FurMapCodec
    {
        // ------------------------------------------------------------------ encode

        // The resolver's directions over the source normal map. Only texels it rewrote take the
        // new direction, blended in across one field texel so the seam does not show; every other
        // pixel is copied unchanged.
        public static Texture2D MergeNormal(Texture2D src, Vector2[] dir, float[] dirStr, bool[] changed,
                                            int FN, float maxAngle, bool flipG)
        {
            int w = src.width, h = src.height;
            Color32[] px = src.GetPixels32();
            var wt = new float[FN * FN];
            for (int i = 0; i < wt.Length; i++) wt[i] = changed[i] ? 1f : 0f;
            float ang = maxAngle * Mathf.Deg2Rad;
            for (int y = 0; y < h; y++)
            {
                float fy = (1f - (y + 0.5f) / h) * FN - 0.5f; // pixel row 0 is the bottom, field row 0 the top
                for (int x = 0; x < w; x++)
                {
                    float fx = (x + 0.5f) / w * FN - 0.5f;
                    float a = SampleBuf(wt, FN, fx, fy);
                    if (a <= 0f) continue;
                    Vector2 d; float st;
                    SampleDir(dir, dirStr, FN, fx, fy, out d, out st);
                    Vector3 n = Tilt(d, st, ang, flipG);
                    int k = y * w + x;
                    Color32 o = px[k];
                    if (a < 1f)
                        n = Vector3.Lerp(new Vector3(o.r / 127.5f - 1f, o.g / 127.5f - 1f, o.b / 127.5f - 1f), n, a).normalized;
                    px[k] = new Color32(EncN(n.x), EncN(n.y), EncN(n.z), o.a);
                }
            }
            return ToTex(px, w, h);
        }

        // The resolver's length cap over a source mask. A pixel only ever gets shorter: where the
        // cap is 1, or no lower than what is there already, the source byte is kept. srgb says the
        // shader linearizes this map, so the cap - a length - is compared and written in that
        // space. cull instead blacks out wherever the cap leaves no hair, for the alpha mask.
        public static Texture2D MergeMask(Texture2D src, float[] cap, int FN, bool srgb, bool cull)
        {
            int w = src.width, h = src.height;
            Color32[] px = src.GetPixels32();
            for (int y = 0; y < h; y++)
            {
                float fy = (1f - (y + 0.5f) / h) * FN - 0.5f;
                for (int x = 0; x < w; x++)
                {
                    float c = SampleBuf(cap, FN, (x + 0.5f) / w * FN - 0.5f, fy);
                    if (c >= 1f) continue;
                    int k = y * w + x;
                    float v = 0f;
                    if (cull) { if (c >= 0.004f) continue; } // the resolver's own cull line
                    else
                    {
                        if (c >= Lin(px[k].r / 255f, srgb)) continue;
                        v = srgb ? Mathf.LinearToGammaSpace(c) : c;
                    }
                    byte b = Enc(v);
                    px[k] = new Color32(b, b, b, px[k].a);
                }
            }
            return ToTex(px, w, h);
        }

        // Stands in for a map the material does not have: what an empty slot means to the shader.
        public static Texture2D Blank(int res, Color32 c)
        {
            var px = new Color32[res * res];
            for (int i = 0; i < px.Length; i++) px[i] = c;
            return ToTex(px, res, res);
        }
        public static readonly Color32 FlatNormal = new Color32(128, 128, 255, 255);
        public static readonly Color32 White = new Color32(255, 255, 255, 255);

        // ------------------------------------------------------------------ decode

        public static void DecodeNormal(Texture2D tex, Vector2[] dir, float[] dirStr, int FN,
                                        float maxAngle, bool flipG)
        {
            float ang = Mathf.Max(1e-4f, maxAngle * Mathf.Deg2Rad);
            for (int j = 0; j < FN; j++)
            {
                float v = 1f - (j + 0.5f) / FN;
                for (int i = 0; i < FN; i++)
                {
                    Color c = tex.GetPixelBilinear((i + 0.5f) / FN, v);
                    float nx = c.r * 2f - 1f, gy = c.g * 2f - 1f, nz = c.b * 2f - 1f;
                    float ny = flipG ? -gy : gy;
                    int idx = j * FN + i;
                    dirStr[idx] = Mathf.Clamp01(Mathf.Acos(Mathf.Clamp(nz, -1f, 1f)) / ang);
                    var d = new Vector2(nx, ny);
                    dir[idx] = d.sqrMagnitude > 1e-12f ? d.normalized : Vector2.zero;
                }
            }
        }

        public static void DecodeMask(Texture2D tex, float[] buf, int MN, bool srgb)
        {
            for (int j = 0; j < MN; j++)
            {
                float v = 1f - (j + 0.5f) / MN;
                for (int i = 0; i < MN; i++)
                    buf[j * MN + i] = Lin(tex.GetPixelBilinear((i + 0.5f) / MN, v).r, srgb);
            }
        }

        // Whether the shader sees this map linearized: an sRGB texture in a Linear project. Masks
        // from an avatar's creator usually keep Unity's sRGB default, and then a grey of 0.5 draws
        // as 0.21 of the length - read raw, the baked fur would come out more than twice as long.
        public static bool Linearized(Material m, string prop)
        {
            if (m == null || string.IsNullOrEmpty(prop) || !m.HasProperty(prop)) return false;
            return Linearized(AssetDatabase.GetAssetPath(m.GetTexture(prop)));
        }

        // The same for an image by project path. A file outside the project has no import
        // settings to go by, so it is read raw.
        public static bool Linearized(string assetPath)
        {
            if (PlayerSettings.colorSpace != ColorSpace.Linear || string.IsNullOrEmpty(assetPath)) return false;
            var imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            return imp != null && imp.sRGBTexture;
        }

        // ------------------------------------------------------------------ assets

        // Maps are read back from the file on disk, not from the imported texture: the import
        // may be compressed, and a normal map is swizzled into DXT5nm, which would decode to a
        // different groom than the one that was painted.
        public static Texture2D LoadMap(Material m, string prop, bool normal, out string err)
        {
            err = null;
            if (m == null || string.IsNullOrEmpty(prop) || !m.HasProperty(prop)) return null;
            var t = m.GetTexture(prop) as Texture2D;
            if (t == null) return null;

            string p = AssetDatabase.GetAssetPath(t);
            if (!string.IsNullOrEmpty(p) && File.Exists(p))
            {
                var copy = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (copy.LoadImage(File.ReadAllBytes(p))) return copy;
                Object.DestroyImmediate(copy);
            }
            if (normal)
            {
                err = prop + " (" + t.name + ") cannot be read from disk, so the painted direction " +
                      "cannot be recovered. Point it at a PNG inside the project.";
                return null;
            }
            try { t.GetPixelBilinear(0.5f, 0.5f); return t; }
            catch
            {
                err = prop + " (" + t.name + ") is not readable. Tick Read/Write on the texture, " +
                      "or point it at a PNG inside the project.";
                return null;
            }
        }

        // Drops the temporary copy LoadMap made, and leaves a real asset alone.
        public static void Release(Texture2D t)
        {
            if (t != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(t)))
                Object.DestroyImmediate(t);
        }

        // srgb carries the source map's flag over, so the kept pixels still mean what they meant.
        public static string WritePng(Texture2D tex, string path, bool normal, bool srgb)
        {
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp != null)
            {
                imp.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                imp.sRGBTexture = srgb;
                imp.SaveAndReimport();
            }
            return path;
        }

        // ------------------------------------------------------------------ helpers

        static void SampleDir(Vector2[] dir, float[] dirStr, int FN, float fx, float fy,
                              out Vector2 d, out float st)
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

        static float SampleBuf(float[] buf, int res, float fx, float fy)
        {
            fx = Mathf.Clamp(fx, 0, res - 1); fy = Mathf.Clamp(fy, 0, res - 1);
            int x0 = (int)fx, y0 = (int)fy, x1 = Mathf.Min(res - 1, x0 + 1), y1 = Mathf.Min(res - 1, y0 + 1);
            float tx = fx - x0, ty = fy - y0;
            float a = Mathf.Lerp(buf[y0 * res + x0], buf[y0 * res + x1], tx);
            float b = Mathf.Lerp(buf[y1 * res + x0], buf[y1 * res + x1], tx);
            return Mathf.Lerp(a, b, ty);
        }

        static Vector3 Tilt(Vector2 d, float st, float ang, bool flipG)
        {
            float len = d.magnitude;
            if (len < 1e-4f || st < 1e-4f) return new Vector3(0f, 0f, 1f);
            float t = st * ang, si = Mathf.Sin(t), ny = d.y / len * si;
            return new Vector3(d.x / len * si, flipG ? -ny : ny, Mathf.Cos(t));
        }

        static Texture2D ToTex(Color32[] px, int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(px); tex.Apply(); return tex;
        }

        static float Lin(float v, bool srgb) { return srgb ? Mathf.GammaToLinearSpace(v) : v; }
        static byte Enc(float v) { return (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255); }
        static byte EncN(float v) { return (byte)Mathf.Clamp(Mathf.RoundToInt((v * 0.5f + 0.5f) * 255f), 0, 255); }

        // ------------------------------------------------------------------ self-check

        // Encode a known groom, read it back, and check it survived. The outfit bake stands or
        // falls on this round-trip, so it is worth one button.
        [MenuItem("Tools/Fur Grooming Tool/Self-check: map codec")]
        public static void SelfCheck()
        {
            const int N = 64, res = 256;
            const float ang = 70f;
            var dir = new Vector2[N * N]; var str = new float[N * N]; var mask = new float[N * N];
            var all = new bool[N * N];
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                {
                    float u = (i + 0.5f) / N, v = (j + 0.5f) / N;
                    int idx = j * N + i;
                    float a = (u + v) * Mathf.PI;                      // smooth, so resampling is fair
                    dir[idx] = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    str[idx] = 0.3f + 0.6f * u;
                    mask[idx] = 0.2f + 0.7f * v;
                    all[idx] = true;
                }

            // Laid over a blank map everywhere, the groom must come back as itself - in both mask
            // encodings.
            var dir2 = new Vector2[N * N]; var str2 = new float[N * N]; var mask2 = new float[N * N];
            Texture2D flat = Blank(res, FlatNormal), white = Blank(res, White);
            Texture2D nt = MergeNormal(flat, dir, str, all, N, ang, true);
            DecodeNormal(nt, dir2, str2, N, ang, true);
            Object.DestroyImmediate(nt);

            float dAng = 0f, dStr = 0f, dMask = 0f;
            foreach (bool srgb in new[] { false, true })
            {
                Texture2D mt = MergeMask(white, mask, N, srgb, false);
                DecodeMask(mt, mask2, N, srgb);
                Object.DestroyImmediate(mt);
                for (int j = 2; j < N - 2; j++)   // the border is a resample edge, not a format test
                    for (int i = 2; i < N - 2; i++)
                    {
                        int idx = j * N + i;
                        dAng = Mathf.Max(dAng, Vector2.Angle(dir[idx], dir2[idx]));
                        dStr = Mathf.Max(dStr, Mathf.Abs(str[idx] - str2[idx]));
                        dMask = Mathf.Max(dMask, Mathf.Abs(mask[idx] - mask2[idx]));
                    }
            }
            Object.DestroyImmediate(flat); Object.DestroyImmediate(white);

            // And what the bake promises: where the cap is 1 the source survives byte for byte,
            // and no pixel anywhere gets longer than it was.
            var cap = new float[N * N];
            for (int i = 0; i < cap.Length; i++) cap[i] = (i % N) < N / 2 ? 1f : 0.3f;
            var srcPx = new Color32[res * res];
            for (int i = 0; i < srcPx.Length; i++) { byte g = (byte)((i * 37) & 255); srcPx[i] = new Color32(g, g, g, 255); }
            Texture2D srcTex = ToTex(srcPx, res, res), merged = MergeMask(srcTex, cap, N, true, false);
            Color32[] outPx = merged.GetPixels32();
            Object.DestroyImmediate(srcTex); Object.DestroyImmediate(merged);
            int bad = 0, trimmed = 0;
            for (int i = 0; i < outPx.Length; i++)
            {
                bool open = (i % res) < res / 2 - res / N;         // clear of the cap's bilinear edge
                if (outPx[i].r > srcPx[i].r || (open && outPx[i].r != srcPx[i].r)) bad++;
                if (outPx[i].r < srcPx[i].r) trimmed++;
            }

            bool ok = dAng < 2f && dStr < 0.02f && dMask < 0.02f && bad == 0 && trimmed > 0;
            string msg = string.Format("[Fur] codec self-check {0} - worst bearing {1:0.00} deg, " +
                                       "strength {2:0.0000}, mask {3:0.0000}, {4} pixels changed that " +
                                       "should not have been, {5} trimmed",
                                       ok ? "passed" : "FAILED", dAng, dStr, dMask, bad, trimmed);
            if (ok) Debug.Log(msg); else Debug.LogError(msg);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
