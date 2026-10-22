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
// The encoders are deliberately the same arithmetic as FurGroomingWindow.BuildNormalTex and
// BuildMaskTex. If one changes, change both, or a baked outfit stops matching the window's
// own preview.

using System.IO;
using UnityEditor;
using UnityEngine;

namespace FurGroomingTool
{
    public static class FurMapCodec
    {
        // ------------------------------------------------------------------ encode

        public static Texture2D NormalTex(Vector2[] dir, float[] dirStr, int FN, int res,
                                          float maxAngle, bool flipG)
        {
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
            var px = new Color32[res * res];
            float ang = maxAngle * Mathf.Deg2Rad;
            for (int irow = 0; irow < res; irow++)
            {
                float fy = (irow + 0.5f) / res * FN - 0.5f; int outRow = res - 1 - irow;
                for (int x = 0; x < res; x++)
                {
                    float fx = (x + 0.5f) / res * FN - 0.5f;
                    Vector2 d; float st;
                    SampleDir(dir, dirStr, FN, fx, fy, out d, out st);
                    float len = d.magnitude; float nx, ny, nz;
                    if (len < 1e-4f || st < 1e-4f) { nx = 0; ny = 0; nz = 1; }
                    else { float t = st * ang, si = Mathf.Sin(t); nx = d.x / len * si; ny = d.y / len * si; nz = Mathf.Cos(t); }
                    float gy = flipG ? -ny : ny;
                    px[outRow * res + x] = new Color32(EncN(nx), EncN(gy), EncN(nz), 255);
                }
            }
            tex.SetPixels32(px); tex.Apply(); return tex;
        }

        public static Texture2D MaskTex(float[] buf, int MN, int res)
        {
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
            var px = new Color32[res * res];
            for (int irow = 0; irow < res; irow++)
            {
                float fy = (irow + 0.5f) / res * MN - 0.5f; int outRow = res - 1 - irow;
                for (int x = 0; x < res; x++)
                {
                    float fx = (x + 0.5f) / res * MN - 0.5f;
                    byte b = Enc(SampleBuf(buf, MN, fx, fy));
                    px[outRow * res + x] = new Color32(b, b, b, 255);
                }
            }
            tex.SetPixels32(px); tex.Apply(); return tex;
        }

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

        public static void DecodeMask(Texture2D tex, float[] buf, int MN)
        {
            for (int j = 0; j < MN; j++)
            {
                float v = 1f - (j + 0.5f) / MN;
                for (int i = 0; i < MN; i++)
                    buf[j * MN + i] = tex.GetPixelBilinear((i + 0.5f) / MN, v).r;
            }
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

        public static string WritePng(Texture2D tex, string path, bool normal)
        {
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
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                {
                    float u = (i + 0.5f) / N, v = (j + 0.5f) / N;
                    int idx = j * N + i;
                    float a = (u + v) * Mathf.PI;                      // smooth, so resampling is fair
                    dir[idx] = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    str[idx] = 0.3f + 0.6f * u;
                    mask[idx] = 0.2f + 0.7f * v;
                }

            var dir2 = new Vector2[N * N]; var str2 = new float[N * N]; var mask2 = new float[N * N];
            Texture2D nt = NormalTex(dir, str, N, res, ang, true);
            DecodeNormal(nt, dir2, str2, N, ang, true);
            Texture2D mt = MaskTex(mask, N, res);
            DecodeMask(mt, mask2, N);
            Object.DestroyImmediate(nt); Object.DestroyImmediate(mt);

            float dAng = 0f, dStr = 0f, dMask = 0f;
            for (int j = 2; j < N - 2; j++)       // the border is a resample edge, not a format test
                for (int i = 2; i < N - 2; i++)
                {
                    int idx = j * N + i;
                    dAng = Mathf.Max(dAng, Vector2.Angle(dir[idx], dir2[idx]));
                    dStr = Mathf.Max(dStr, Mathf.Abs(str[idx] - str2[idx]));
                    dMask = Mathf.Max(dMask, Mathf.Abs(mask[idx] - mask2[idx]));
                }

            bool ok = dAng < 2f && dStr < 0.02f && dMask < 0.02f;
            string msg = string.Format("[Fur] codec self-check {0} - worst bearing {1:0.00} deg, " +
                                       "strength {2:0.0000}, mask {3:0.0000}",
                                       ok ? "passed" : "FAILED", dAng, dStr, dMask);
            if (ok) Debug.Log(msg); else Debug.LogError(msg);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
