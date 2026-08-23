// Fur occlusion resolver - keeps groomed fur out of clothing by measuring the
// outfit's shadow on the body.
//
// For every texel of the direction field the fur mesh is sampled in UV space to
// recover a world-space root, normal and tangent frame. A fan of rays is then cast
// over the hemisphere there, against a voxel volume of the clothing, giving:
//
//   coverage   how much of the sky above the spot the outfit blocks  (0 = open)
//   ceiling    clear headroom straight out from the skin             (metres)
//   openXY     which way along the surface there is still room
//
// The hair is leaned over just far enough for its tip to fit under the headroom,
// its bearing nudged towards the open side, and its length capped by the room left
// along the direction it ends up in.
//
// Being a continuous field rather than a yes/no per hair, this copes with garments
// that clip into the body or overlap themselves, and it produces smooth maps rather
// than speckle. It is also visible: the coverage field is drawn onto the UV canvas,
// so a groom that is not working can be looked at instead of guessed at.
//
// Avatar UVs are often mirrored, so one texel can land on several places on the
// body at once. All of them are kept and the worst case wins, otherwise one side
// gets groomed against the other side's geometry.

using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
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
    }

    public struct FurHairDebug
    {
        public Vector3 root, normal, before, after;
        public byte state; // 1 kept, 2 leaned, 3 shortened, 4 culled
    }

    public class FurCoverage
    {
        public int res;
        public float[] coverage;
        public byte[] hits;
        public bool Valid { get { return coverage != null; } }
    }

    public class FurOcclusionReport
    {
        public int covered, multiSampled, inReach, shadowed, groomed, leaned, shortened, culled;
        public int clothTris, voxels, solidVoxels;
        public float voxelMm;
        public bool canceled;
        public string error;
        public double seconds;
        public readonly List<string> notes = new List<string>();

        public string Summary()
        {
            if (error != null) return error;
            string s = string.Format(
                "{0} texels on the mesh ({1} shared by mirrored UVs), {2} near clothing, {3} shadowed, " +
                "{4} groomed  ->  {5} leaned over, {6} shortened, {7} culled.\n" +
                "{8} clothing triangles in a {9:0.0}mm voxel volume ({10} solid).  {11:0.00}s{12}",
                covered, multiSampled, inReach, shadowed, groomed, leaned, shortened, culled,
                clothTris, voxelMm, solidVoxels, seconds, canceled ? ", canceled" : "");
            for (int i = 0; i < notes.Count; i++) s += "\n" + notes[i];
            return s;
        }
    }

    // One place on the body that a UV texel maps to.
    internal struct Surf { public Vector3 p, n, t, b; }

    public static class FurOcclusionResolver
    {
        public const int MaxSamples = 4;
        const int MaxVoxels = 64 << 20; // 64M voxels = 8MB as a bitset

        struct Ray { public Vector3 local; public float w; }

        static readonly float[][] RingTilt = {
            new[] { 0f, 35f, 65f },
            new[] { 0f, 25f, 48f, 70f },
            new[] { 0f, 18f, 36f, 54f, 72f },
        };
        static readonly int[][] RingCount = {
            new[] { 1, 4, 6 },
            new[] { 1, 6, 8, 10 },
            new[] { 1, 8, 12, 16, 18 },
        };

        static Ray[] Fan(int quality)
        {
            quality = Mathf.Clamp(quality, 0, RingTilt.Length - 1);
            float[] tilts = RingTilt[quality];
            int[] counts = RingCount[quality];
            var list = new List<Ray>();
            for (int r = 0; r < tilts.Length; r++)
            {
                float th = tilts[r] * Mathf.Deg2Rad;
                float st = Mathf.Sin(th), ct = Mathf.Cos(th);
                for (int a = 0; a < counts[r]; a++)
                {
                    float az = (a + r * 0.5f) / counts[r] * Mathf.PI * 2f;
                    list.Add(new Ray { local = new Vector3(st * Mathf.Cos(az), st * Mathf.Sin(az), ct), w = ct });
                }
            }
            return list.ToArray();
        }

        // ---------------------------------------------------------------- resolve

        public static FurOcclusionReport Resolve(
            Renderer skin, IList<Renderer> clothing,
            Vector2[] dir, float[] dirStr, int FN,
            float[] lenBuf, float[] alphaBuf, int MN,
            float maxAngleDeg, bool flipG,
            FurOcclusionSettings s,
            FurCoverage outCoverage,
            List<FurHairDebug> debug)
        {
            var rep = new FurOcclusionReport();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (debug != null) debug.Clear();

            if (skin == null) { rep.error = "Assign the fur mesh first."; return rep; }

            Vector3[] sv, sn; Vector2[] suv; int[] stri; Bounds skinBounds;
            if (!BakeWorld(skin, out sv, out sn, out suv, out stri, out skinBounds, rep) ||
                suv == null || suv.Length == 0)
            { rep.error = "The fur mesh has no readable mesh data or no UV map."; return rep; }

            const float mm = 0.001f;
            float thick = Mathf.Max(0f, s.clothThicknessMm) * mm;
            float lenMargin = Mathf.Max(0f, s.lengthMarginMm) * mm;
            float furLen = Mathf.Max(1e-5f, s.furLengthMm * mm);

            // ---- clothing -> voxels, once
            var ca = new List<Vector3>(); var cb = new List<Vector3>(); var cc = new List<Vector3>();
            for (int r = 0; r < clothing.Count; r++)
            {
                Vector3[] v, nrm; Vector2[] uv; int[] tri; Bounds cbnd;
                if (!BakeWorld(clothing[r], out v, out nrm, out uv, out tri, out cbnd, rep)) continue;
                for (int i = 0; i + 2 < tri.Length; i += 3)
                {
                    Vector3 p0 = v[tri[i]], p1 = v[tri[i + 1]], p2 = v[tri[i + 2]];
                    if (Vector3.Cross(p1 - p0, p2 - p0).sqrMagnitude < 1e-16f) continue;
                    ca.Add(p0); cb.Add(p1); cc.Add(p2);
                }
            }
            if (ca.Count == 0) { rep.error = "No usable clothing triangles. Add at least one clothing renderer."; return rep; }

            var vol = new FurClothVolume();
            if (!vol.Build(ca, cb, cc, thick, MaxVoxels))
            { rep.error = "The clothing volume came out empty."; return rep; }
            rep.clothTris = ca.Count;
            rep.voxels = vol.nx * vol.ny * vol.nz;
            rep.solidVoxels = vol.solidVoxels;
            rep.voxelMm = vol.voxel * 1000f;

            byte[] hits;
            Surf[] surf = RasterizeSurface(sv, sn, suv, stri, FN, skinBounds, out hits);

            // ---- fields
            float maxDist = furLen + lenMargin;
            Ray[] fan = Fan(s.rayQuality);
            float wTotal = 0f;
            for (int i = 0; i < fan.Length; i++) wTotal += fan[i].w;

            float maxAngleRad = Mathf.Max(1e-4f, maxAngleDeg * Mathf.Deg2Rad);
            float capRad = Mathf.Min(s.maxTiltAngle, maxAngleDeg) * Mathf.Deg2Rad;
            float comb = Mathf.Clamp01(s.combStrength);
            float thresh = Mathf.Clamp01(s.shadowThreshold);

            var coverage = new float[FN * FN];
            var lenCap = new float[FN * FN];
            var changed = new bool[FN * FN];
            for (int i = 0; i < lenCap.Length; i++) lenCap[i] = 1f;

            int dbgStride = Mathf.Max(1, FN * FN / 8000);
            var dbgArr = new FurHairDebug[FN * FN / dbgStride + 1];
            var dbgOn = new bool[dbgArr.Length];

            // counters, summed after the parallel pass
            var cCovered = new int[FN]; var cMulti = new int[FN]; var cReach = new int[FN];
            var cShadow = new int[FN]; var cGroom = new int[FN];
            var cLean = new int[FN]; var cShort = new int[FN]; var cCull = new int[FN];

            const int Chunk = 16; // rows per progress step, so the bar still moves and cancels
            try
            {
                for (int j0 = 0; j0 < FN; j0 += Chunk)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Fur occlusion resolver",
                            "Measuring the outfit's shadow...", (float)j0 / FN))
                    { rep.canceled = true; break; }

                    int j1 = Mathf.Min(FN, j0 + Chunk);
                    Parallel.For(j0, j1, j =>
                    {
                        for (int i = 0; i < FN; i++)
                        {
                            int idx = j * FN + i;
                            int n = hits[idx];
                            if (n == 0) continue;
                            cCovered[j]++;
                            if (n > 1) cMulti[j]++;

                            Surf s0 = surf[idx * MaxSamples];
                            if (!vol.AnyNear(s0.p, maxDist)) continue;
                            cReach[j]++;

                            float len01 = SampleBuf(lenBuf, MN, ((i + 0.5f) / FN) * MN - 0.5f,
                                                                ((j + 0.5f) / FN) * MN - 0.5f);

                            // --- the shadow, worst case over every place this texel lands
                            float blocked = 0f, ceiling = maxDist;
                            Vector2 openXY = Vector2.zero;
                            for (int k = 0; k < n; k++)
                            {
                                Surf su = surf[idx * MaxSamples + k];
                                float b = 0f;
                                Vector2 acc = Vector2.zero;
                                for (int q = 0; q < fan.Length; q++)
                                {
                                    Vector3 wd = su.t * fan[q].local.x + su.b * fan[q].local.y + su.n * fan[q].local.z;
                                    float d = vol.March(su.p, wd, maxDist);
                                    if (d < maxDist - 1e-6f) b += fan[q].w;
                                    float open = d / maxDist;
                                    acc += new Vector2(fan[q].local.x, fan[q].local.y) * (fan[q].w * open * open);
                                    if (q == 0 && d < ceiling) ceiling = d;
                                }
                                b /= wTotal;
                                if (b > blocked) { blocked = b; openXY = acc; }
                            }

                            coverage[idx] = blocked;
                            if (blocked > 0.02f) cShadow[j]++;
                            if (blocked <= thresh || len01 <= 0f) continue;
                            cGroom[j]++;

                            // --- bearing: the painted flow, nudged towards room
                            Vector2 d0 = dir[idx];
                            float dv0 = flipG ? -d0.y : d0.y;
                            float phi0 = Mathf.Clamp01(dirStr[idx]) * maxAngleRad;

                            Vector2 bearing = new Vector2(d0.x, dv0);
                            bearing = bearing.sqrMagnitude > 1e-12f ? bearing.normalized : new Vector2(1f, 0f);
                            float pull = comb * Mathf.Clamp01((blocked - thresh) / Mathf.Max(1e-4f, 1f - thresh));
                            if (openXY.sqrMagnitude > 1e-10f)
                            {
                                Vector2 mix = Vector2.Lerp(bearing, openXY.normalized, pull * 0.5f);
                                if (mix.sqrMagnitude > 1e-12f) bearing = mix.normalized;
                            }

                            // --- lean: far enough over for the tip to duck under the headroom
                            float wantLen = len01 * furLen;
                            float needed = phi0;
                            if (ceiling < wantLen && wantLen > 1e-6f)
                                needed = Mathf.Acos(Mathf.Clamp01(ceiling / wantLen));
                            float tilt = Mathf.Min(Mathf.Lerp(phi0, Mathf.Max(phi0, needed), comb), capRad);

                            Vector3 aim = new Vector3(bearing.x * Mathf.Sin(tilt), bearing.y * Mathf.Sin(tilt), Mathf.Cos(tilt));

                            // --- room actually left along the direction we settled on
                            float freeLen = maxDist;
                            for (int k = 0; k < n; k++)
                            {
                                Surf su = surf[idx * MaxSamples + k];
                                Vector3 wd = su.t * aim.x + su.b * aim.y + su.n * aim.z;
                                float d = vol.March(su.p, wd, maxDist);
                                if (d < freeLen) freeLen = d;
                            }

                            float cap = Mathf.Clamp01(Mathf.Max(0f, freeLen - lenMargin) / furLen);
                            byte state;
                            if (cap <= 1e-4f) { cap = 0f; state = 4; cCull[j]++; }
                            else if (cap < len01) { state = 3; cShort[j]++; }
                            else { state = 2; cLean[j]++; }
                            lenCap[idx] = cap;

                            dirStr[idx] = Mathf.Clamp01(tilt / maxAngleRad);
                            Vector2 nd = new Vector2(aim.x, aim.y);
                            if (nd.sqrMagnitude > 1e-12f)
                            {
                                nd = nd.normalized;
                                dir[idx] = new Vector2(nd.x, flipG ? -nd.y : nd.y);
                            }
                            changed[idx] = true;

                            if (s.collectDebug && (idx % dbgStride) == 0)
                            {
                                Vector3 cur = new Vector3(bearing.x * Mathf.Sin(phi0), bearing.y * Mathf.Sin(phi0), Mathf.Cos(phi0));
                                int di = idx / dbgStride;
                                dbgArr[di] = new FurHairDebug {
                                    root = s0.p, normal = s0.n,
                                    before = (s0.t * cur.x + s0.b * cur.y + s0.n * cur.z) * (len01 * furLen),
                                    after = (s0.t * aim.x + s0.b * aim.y + s0.n * aim.z) * (Mathf.Min(len01, cap) * furLen),
                                    state = state
                                };
                                dbgOn[di] = true;
                            }
                        }
                    });
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            for (int j = 0; j < FN; j++)
            {
                rep.covered += cCovered[j]; rep.multiSampled += cMulti[j]; rep.inReach += cReach[j];
                rep.shadowed += cShadow[j]; rep.groomed += cGroom[j];
                rep.leaned += cLean[j]; rep.shortened += cShort[j]; rep.culled += cCull[j];
            }
            if (debug != null)
                for (int i = 0; i < dbgArr.Length; i++) if (dbgOn[i]) debug.Add(dbgArr[i]);

            for (int p = 0; p < Mathf.Max(0, s.smoothPasses); p++) SmoothChanged(dir, dirStr, changed, FN);

            for (int y = 0; y < MN; y++)
                for (int x = 0; x < MN; x++)
                {
                    float c = SampleBuf(lenCap, FN, ((x + 0.5f) / MN) * FN - 0.5f, ((y + 0.5f) / MN) * FN - 0.5f);
                    if (c >= 1f) continue;
                    int k = y * MN + x;
                    if (c < lenBuf[k]) lenBuf[k] = c;
                    if (s.writeAlpha && alphaBuf != null && lenBuf[k] < 0.004f) alphaBuf[k] = 0f;
                }

            if (outCoverage != null)
            {
                outCoverage.res = FN; outCoverage.coverage = coverage; outCoverage.hits = hits;
            }

            if (rep.covered == 0)
                rep.notes.Add("WARNING: no texel landed on the fur mesh. Check it has a UV map in 0-1 space.");
            else if (rep.inReach == 0)
                rep.notes.Add("WARNING: no part of the fur mesh is anywhere near the clothing. Check both are " +
                              "posed and that the right renderers are listed.");
            else if (rep.shadowed == 0)
                rep.notes.Add("The clothing never shadows the fur. Raise Fur length (mm) to match what your " +
                              "shader draws, or Cloth thickness.");

            clock.Stop();
            rep.seconds = clock.Elapsed.TotalSeconds;
            return rep;
        }

        // The coverage field as a picture on the UV atlas.
        public static Texture2D CoverageTexture(FurCoverage c)
        {
            if (c == null || !c.Valid) return null;
            int res = c.res;
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[res * res];
            for (int j = 0; j < res; j++)
            {
                int outRow = res - 1 - j; // field row 0 is the top of the canvas
                for (int i = 0; i < res; i++)
                {
                    int idx = j * res + i;
                    if (c.hits[idx] == 0) px[outRow * res + i] = new Color32(24, 26, 40, 255);
                    else
                    {
                        float v = Mathf.Clamp01(c.coverage[idx]);
                        px[outRow * res + i] = new Color32(
                            (byte)Mathf.RoundToInt(Mathf.Lerp(105f, 255f, v)),
                            (byte)Mathf.RoundToInt(Mathf.Lerp(105f, 60f, v)),
                            (byte)Mathf.RoundToInt(Mathf.Lerp(105f, 40f, v)), 255);
                    }
                }
            }
            tex.SetPixels32(px); tex.Apply();
            return tex;
        }

        // ---------------------------------------------------------------- surface

        internal static Surf[] RasterizeSurface(Vector3[] v, Vector3[] nrm, Vector2[] uv, int[] tris, int FN,
                                                Bounds bounds, out byte[] hits)
        {
            var surf = new Surf[FN * FN * MaxSamples];
            hits = new byte[FN * FN];
            bool hasN = nrm != null && nrm.Length == v.Length;
            float same = Mathf.Max(1e-4f, bounds.size.magnitude * 0.01f);
            float same2 = same * same;

            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
                Vector2 w0 = uv[i0], w1 = uv[i1], w2 = uv[i2];
                Vector3 p0 = v[i0], p1 = v[i1], p2 = v[i2];

                Vector2 duv1 = w1 - w0, duv2 = w2 - w0;
                float det = duv1.x * duv2.y - duv2.x * duv1.y;
                if (Mathf.Abs(det) < 1e-12f) continue;
                float rdet = 1f / det;
                Vector3 dp1 = p1 - p0, dp2 = p2 - p0;
                Vector3 tanU = (dp1 * duv2.y - dp2 * duv1.y) * rdet;
                Vector3 tanV = (dp2 * duv1.x - dp1 * duv2.x) * rdet;

                Vector3 geo = Vector3.Cross(dp1, dp2);
                if (geo.sqrMagnitude < 1e-16f) continue;

                Vector2 f0 = new Vector2(w0.x * FN - 0.5f, (1f - w0.y) * FN - 0.5f);
                Vector2 f1 = new Vector2(w1.x * FN - 0.5f, (1f - w1.y) * FN - 0.5f);
                Vector2 f2 = new Vector2(w2.x * FN - 0.5f, (1f - w2.y) * FN - 0.5f);

                int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(f0.x, Mathf.Min(f1.x, f2.x))));
                int x1 = Mathf.Min(FN - 1, Mathf.CeilToInt(Mathf.Max(f0.x, Mathf.Max(f1.x, f2.x))));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(f0.y, Mathf.Min(f1.y, f2.y))));
                int y1 = Mathf.Min(FN - 1, Mathf.CeilToInt(Mathf.Max(f0.y, Mathf.Max(f1.y, f2.y))));
                if (x1 < x0 || y1 < y0) continue;

                bool any = false;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float wa, wb, wc;
                        if (!Bary(new Vector2(x, y), f0, f1, f2, out wa, out wb, out wc)) continue;
                        any = true;
                        Store(surf, hits, y * FN + x, p0 * wa + p1 * wb + p2 * wc,
                              hasN ? nrm[i0] * wa + nrm[i1] * wb + nrm[i2] * wc : geo, tanU, tanV, same2);
                    }

                if (!any) // triangles smaller than a texel would leave a hole
                {
                    int cx = Mathf.Clamp(Mathf.RoundToInt((f0.x + f1.x + f2.x) / 3f), 0, FN - 1);
                    int cy = Mathf.Clamp(Mathf.RoundToInt((f0.y + f1.y + f2.y) / 3f), 0, FN - 1);
                    Store(surf, hits, cy * FN + cx, (p0 + p1 + p2) / 3f,
                          hasN ? (nrm[i0] + nrm[i1] + nrm[i2]) / 3f : geo, tanU, tanV, same2);
                }
            }

            for (int pass = 0; pass < 2; pass++) Dilate(surf, hits, FN);
            return surf;
        }

        static void Store(Surf[] surf, byte[] hits, int cell, Vector3 p, Vector3 n,
                          Vector3 tanU, Vector3 tanV, float same2)
        {
            if (n.sqrMagnitude < 1e-12f) return;
            int at = cell * MaxSamples, count = hits[cell];
            for (int k = 0; k < count; k++)
                if ((surf[at + k].p - p).sqrMagnitude < same2) return; // same spot already stored
            if (count >= MaxSamples) return;

            n.Normalize();
            Vector3 t = tanU - n * Vector3.Dot(n, tanU);
            if (t.sqrMagnitude < 1e-12f)
            {
                t = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right);
                if (t.sqrMagnitude < 1e-12f) return;
            }
            t.Normalize();
            Vector3 b = Vector3.Cross(n, t);
            if (Vector3.Dot(b, tanV) < 0f) b = -b; // keep the mesh's UV handedness
            surf[at + count] = new Surf { p = p, n = n, t = t, b = b };
            hits[cell] = (byte)(count + 1);
        }

        static void Dilate(Surf[] surf, byte[] hits, int FN)
        {
            var add = new List<int>(); var from = new List<int>();
            for (int y = 0; y < FN; y++)
                for (int x = 0; x < FN; x++)
                {
                    int cell = y * FN + x;
                    if (hits[cell] != 0) continue;
                    bool found = false;
                    for (int dy = -1; dy <= 1 && !found; dy++)
                        for (int dx = -1; dx <= 1 && !found; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= FN || ny >= FN) continue;
                            int src = ny * FN + nx;
                            if (hits[src] == 0) continue;
                            add.Add(cell); from.Add(src); found = true;
                        }
                }
            for (int i = 0; i < add.Count; i++)
            {
                int dst = add[i] * MaxSamples, src = from[i] * MaxSamples;
                byte c = hits[from[i]];
                for (int k = 0; k < c; k++) surf[dst + k] = surf[src + k];
                hits[add[i]] = c;
            }
        }

        // ---------------------------------------------------------------- helpers

        static void SmoothChanged(Vector2[] dir, float[] dirStr, bool[] changed, int FN)
        {
            var nd = new Vector2[dir.Length];
            var ns = new float[dirStr.Length];
            System.Array.Copy(dir, nd, dir.Length);
            System.Array.Copy(dirStr, ns, dirStr.Length);
            for (int y = 0; y < FN; y++)
                for (int x = 0; x < FN; x++)
                {
                    int idx = y * FN + x;
                    if (!changed[idx]) continue;
                    Vector2 sd = Vector2.zero; float ss = 0f; int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = Mathf.Clamp(x + dx, 0, FN - 1), yy = Mathf.Clamp(y + dy, 0, FN - 1);
                            int k = yy * FN + xx;
                            sd += dir[k]; ss += dirStr[k]; n++;
                        }
                    sd /= n;
                    nd[idx] = sd.sqrMagnitude > 1e-8f ? sd.normalized : dir[idx];
                    ns[idx] = ss / n;
                }
            System.Array.Copy(nd, dir, dir.Length);
            System.Array.Copy(ns, dirStr, dirStr.Length);
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

        // ---------------------------------------------------------------- baking

        // Skinned meshes are baked in their current pose. Which matrix then places that
        // bake in the world depends on how the rig is built, and guessing wrong silently
        // moves the whole mesh - so ask the rig itself, through the skinning formula,
        // where a sample of vertices really are, and keep whichever placement agrees.
        // Renderer.bounds is deliberately not used: on an avatar it is the culling box,
        // routinely inflated by hand.
        public static bool BakeWorld(Renderer r, out Vector3[] verts, out Vector3[] norms,
                                     out Vector2[] uvs, out int[] tris, out Bounds worldBounds,
                                     FurOcclusionReport rep)
        {
            verts = null; norms = null; uvs = null; tris = null;
            worldBounds = new Bounds();
            if (r == null) return false;

            Mesh m; bool temp = false, skinned = false;
            var smr = r as SkinnedMeshRenderer;
            if (smr != null)
            {
                if (smr.sharedMesh == null) return false;
                m = new Mesh(); smr.BakeMesh(m); temp = true; skinned = true;
            }
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) return false;
                m = mf.sharedMesh;
            }

            var lv = m.vertices; var ln = m.normals;
            uvs = m.uv; tris = m.triangles;

            Transform t = r.transform;
            Matrix4x4 mtx = t.localToWorldMatrix;
            float err = 0f;
            if (skinned)
            {
                int[] probe; Vector3[] truth;
                if (SkinnedTruth(smr, out probe, out truth))
                {
                    var cands = new[] {
                        t.localToWorldMatrix,
                        Matrix4x4.TRS(t.position, t.rotation, Vector3.one),
                        Matrix4x4.identity };
                    err = float.MaxValue;
                    for (int i = 0; i < cands.Length; i++)
                    {
                        float e = 0f;
                        for (int k = 0; k < probe.Length; k++)
                            e += (cands[i].MultiplyPoint3x4(lv[probe[k]]) - truth[k]).magnitude;
                        e /= probe.Length;
                        if (e < err) { err = e; mtx = cands[i]; }
                    }
                }
            }

            verts = new Vector3[lv.Length];
            for (int i = 0; i < lv.Length; i++) verts[i] = mtx.MultiplyPoint3x4(lv[i]);
            if (ln != null && ln.Length == lv.Length)
            {
                norms = new Vector3[ln.Length];
                for (int i = 0; i < ln.Length; i++)
                {
                    Vector3 w = mtx.MultiplyVector(ln[i]);
                    norms[i] = w.sqrMagnitude > 1e-12f ? w.normalized : Vector3.up;
                }
            }
            if (temp) Object.DestroyImmediate(m);

            Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < verts.Length; i++) { lo = Vector3.Min(lo, verts[i]); hi = Vector3.Max(hi, verts[i]); }
            worldBounds = new Bounds((lo + hi) * 0.5f, hi - lo);

            float scale = Mathf.Max(1e-4f, worldBounds.size.magnitude);
            if (rep != null && err > scale * 0.05f)
                rep.notes.Add("WARNING: could not place '" + r.name + "' reliably in the world (off by " +
                              (err * 1000f).ToString("0") + "mm). Results for it may be wrong.");
            return verts.Length > 0 && tris.Length > 0;
        }

        static bool SkinnedTruth(SkinnedMeshRenderer smr, out int[] probe, out Vector3[] truth)
        {
            probe = null; truth = null;
            Mesh sm = smr.sharedMesh;
            if (sm == null) return false;
            Transform[] bones = smr.bones;
            Matrix4x4[] bind = sm.bindposes;
            BoneWeight[] bw = sm.boneWeights;
            Vector3[] bv = sm.vertices;
            if (bones == null || bones.Length == 0 || bind == null || bind.Length == 0 ||
                bw == null || bv == null || bw.Length != bv.Length) return false;

            int n = Mathf.Min(16, bv.Length);
            int stride = Mathf.Max(1, bv.Length / n);
            probe = new int[n]; truth = new Vector3[n];
            for (int k = 0; k < n; k++)
            {
                int i = Mathf.Min(bv.Length - 1, k * stride);
                probe[k] = i;
                BoneWeight w = bw[i];
                Vector3 p = Vector3.zero; float tot = 0f;
                p += Skin(bones, bind, w.boneIndex0, w.weight0, bv[i], ref tot);
                p += Skin(bones, bind, w.boneIndex1, w.weight1, bv[i], ref tot);
                p += Skin(bones, bind, w.boneIndex2, w.weight2, bv[i], ref tot);
                p += Skin(bones, bind, w.boneIndex3, w.weight3, bv[i], ref tot);
                if (tot < 1e-6f) return false;
                truth[k] = p / tot;
            }
            return true;
        }

        static Vector3 Skin(Transform[] bones, Matrix4x4[] bind, int bi, float w, Vector3 v, ref float tot)
        {
            if (w <= 0f || bi < 0 || bi >= bones.Length || bi >= bind.Length || bones[bi] == null)
                return Vector3.zero;
            tot += w;
            return (bones[bi].localToWorldMatrix * bind[bi]).MultiplyPoint3x4(v) * w;
        }
    }
}
