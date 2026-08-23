// The clothing, turned into a solid voxel volume once, up front.
//
// The occlusion bake asks one question millions of times: "starting here, going
// that way, how far until I am inside clothing?" Answering it by testing triangles
// is what made the first attempt unusable - every ray walked a candidate list of
// hundreds of triangles, so a single avatar cost hundreds of millions of triangle
// tests.
//
// Here the outfit is rasterized into an occupancy bitset once (a few milliseconds),
// after which a ray is a plain 3D-DDA walk over that bitset: a few adds and a bit
// test per step, no geometry at all. A coarse copy of the same grid answers "is
// there any clothing near this spot?" so that the large parts of the body nowhere
// near the outfit cost nothing.

using System.Collections.Generic;
using UnityEngine;

namespace FurGroomingTool
{
    internal class FurClothVolume
    {
        public Vector3 lo, hi;
        public float voxel;
        public int nx, ny, nz;
        public int solidVoxels;
        public int triangles;

        float inv;
        uint[] bits;

        // 4x coarser, for rejecting whole regions of the body in one test
        const int CoarseStep = 4;
        int cnx, cny, cnz;
        float cinv;
        uint[] coarse;

        public bool Empty { get { return bits == null || solidVoxels == 0; } }

        // ---------------------------------------------------------------- build

        // 'thickness' fattens the cloth in every direction; it stands in for the
        // separate front/back/horizontal margins, which a volume makes redundant.
        public bool Build(List<Vector3> A, List<Vector3> B, List<Vector3> C,
                          float thickness, int maxVoxels)
        {
            triangles = A.Count;
            if (triangles == 0) return false;

            Vector3 mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            double edge = 0;
            for (int i = 0; i < triangles; i++)
            {
                Vector3 tl = Vector3.Min(A[i], Vector3.Min(B[i], C[i]));
                Vector3 th = Vector3.Max(A[i], Vector3.Max(B[i], C[i]));
                mn = Vector3.Min(mn, tl); mx = Vector3.Max(mx, th);
                edge += (th - tl).magnitude;
            }
            float pad = thickness + 1e-4f;
            lo = mn - Vector3.one * pad;
            hi = mx + Vector3.one * pad;
            Vector3 size = hi - lo;

            // Aim at the average triangle size, then coarsen until the grid fits the
            // budget - a 2mm voxel over a whole outfit is only a few MB as a bitset.
            float avg = (float)(edge / triangles) * 0.5f;
            voxel = Mathf.Max(avg, 0.0015f);
            for (int guard = 0; guard < 24; guard++)
            {
                nx = Mathf.Max(1, Mathf.CeilToInt(size.x / voxel));
                ny = Mathf.Max(1, Mathf.CeilToInt(size.y / voxel));
                nz = Mathf.Max(1, Mathf.CeilToInt(size.z / voxel));
                long total = (long)nx * ny * nz;
                if (total <= maxVoxels) break;
                voxel *= 1.26f; // ~2x the voxel count per step
            }
            inv = 1f / voxel;

            bits = new uint[(nx * ny * nz + 31) / 32];
            cnx = (nx + CoarseStep - 1) / CoarseStep;
            cny = (ny + CoarseStep - 1) / CoarseStep;
            cnz = (nz + CoarseStep - 1) / CoarseStep;
            cinv = 1f / (voxel * CoarseStep);
            coarse = new uint[(cnx * cny * cnz + 31) / 32];

            float reach = thickness + voxel * 0.87f; // voxel half-diagonal
            float reach2 = reach * reach;

            for (int i = 0; i < triangles; i++)
            {
                Vector3 a = A[i], b = B[i], c = C[i];
                Vector3 tl = Vector3.Min(a, Vector3.Min(b, c)) - Vector3.one * reach;
                Vector3 th = Vector3.Max(a, Vector3.Max(b, c)) + Vector3.one * reach;

                int x0 = Mathf.Clamp(Mathf.FloorToInt((tl.x - lo.x) * inv), 0, nx - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt((tl.y - lo.y) * inv), 0, ny - 1);
                int z0 = Mathf.Clamp(Mathf.FloorToInt((tl.z - lo.z) * inv), 0, nz - 1);
                int x1 = Mathf.Clamp(Mathf.FloorToInt((th.x - lo.x) * inv), 0, nx - 1);
                int y1 = Mathf.Clamp(Mathf.FloorToInt((th.y - lo.y) * inv), 0, ny - 1);
                int z1 = Mathf.Clamp(Mathf.FloorToInt((th.z - lo.z) * inv), 0, nz - 1);

                for (int z = z0; z <= z1; z++)
                {
                    float pz = lo.z + (z + 0.5f) * voxel;
                    for (int y = y0; y <= y1; y++)
                    {
                        float py = lo.y + (y + 0.5f) * voxel;
                        for (int x = x0; x <= x1; x++)
                        {
                            int idx = (z * ny + y) * nx + x;
                            if ((bits[idx >> 5] & (1u << (idx & 31))) != 0) continue;
                            Vector3 p = new Vector3(lo.x + (x + 0.5f) * voxel, py, pz);
                            if ((ClosestOnTri(p, a, b, c) - p).sqrMagnitude > reach2) continue;
                            bits[idx >> 5] |= 1u << (idx & 31);
                            solidVoxels++;
                            int ci = ((z / CoarseStep) * cny + (y / CoarseStep)) * cnx + (x / CoarseStep);
                            coarse[ci >> 5] |= 1u << (ci & 31);
                        }
                    }
                }
            }
            return solidVoxels > 0;
        }

        // ---------------------------------------------------------------- query

        bool Solid(int x, int y, int z)
        {
            int idx = (z * ny + y) * nx + x;
            return (bits[idx >> 5] & (1u << (idx & 31))) != 0;
        }

        // Is there any clothing within 'r' of p? One coarse-grid sweep, so the parts
        // of the body far from the outfit drop out immediately.
        public bool AnyNear(Vector3 p, float r)
        {
            Vector3 tl = p - Vector3.one * r, th = p + Vector3.one * r;
            if (th.x < lo.x || th.y < lo.y || th.z < lo.z) return false;
            if (tl.x > hi.x || tl.y > hi.y || tl.z > hi.z) return false;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((tl.x - lo.x) * cinv), 0, cnx - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt((tl.y - lo.y) * cinv), 0, cny - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((tl.z - lo.z) * cinv), 0, cnz - 1);
            int x1 = Mathf.Clamp(Mathf.FloorToInt((th.x - lo.x) * cinv), 0, cnx - 1);
            int y1 = Mathf.Clamp(Mathf.FloorToInt((th.y - lo.y) * cinv), 0, cny - 1);
            int z1 = Mathf.Clamp(Mathf.FloorToInt((th.z - lo.z) * cinv), 0, cnz - 1);
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int ci = (z * cny + y) * cnx + x;
                        if ((coarse[ci >> 5] & (1u << (ci & 31))) != 0) return true;
                    }
            return false;
        }

        public bool SolidAt(Vector3 p)
        {
            int x = Mathf.FloorToInt((p.x - lo.x) * inv);
            int y = Mathf.FloorToInt((p.y - lo.y) * inv);
            int z = Mathf.FloorToInt((p.z - lo.z) * inv);
            if (x < 0 || y < 0 || z < 0 || x >= nx || y >= ny || z >= nz) return false;
            return Solid(x, y, z);
        }

        // Distance from o along dir until the first solid voxel, or maxT if the ray
        // gets that far in the clear. Amanatides-Woo 3D-DDA over the bitset.
        public float March(Vector3 o, Vector3 dir, float maxT)
        {
            float t0 = 0f, t1 = maxT;
            if (!ClipToBox(o, dir, ref t0, ref t1)) return maxT;

            Vector3 p = o + dir * (t0 + 1e-6f);
            int x = Mathf.Clamp(Mathf.FloorToInt((p.x - lo.x) * inv), 0, nx - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt((p.y - lo.y) * inv), 0, ny - 1);
            int z = Mathf.Clamp(Mathf.FloorToInt((p.z - lo.z) * inv), 0, nz - 1);

            int sx = dir.x >= 0f ? 1 : -1, sy = dir.y >= 0f ? 1 : -1, sz = dir.z >= 0f ? 1 : -1;
            const float big = float.MaxValue;
            float tdx = Mathf.Abs(dir.x) > 1e-12f ? Mathf.Abs(voxel / dir.x) : big;
            float tdy = Mathf.Abs(dir.y) > 1e-12f ? Mathf.Abs(voxel / dir.y) : big;
            float tdz = Mathf.Abs(dir.z) > 1e-12f ? Mathf.Abs(voxel / dir.z) : big;

            float tmx = Mathf.Abs(dir.x) > 1e-12f
                ? (lo.x + (x + (sx > 0 ? 1 : 0)) * voxel - o.x) / dir.x : big;
            float tmy = Mathf.Abs(dir.y) > 1e-12f
                ? (lo.y + (y + (sy > 0 ? 1 : 0)) * voxel - o.y) / dir.y : big;
            float tmz = Mathf.Abs(dir.z) > 1e-12f
                ? (lo.z + (z + (sz > 0 ? 1 : 0)) * voxel - o.z) / dir.z : big;

            float t = t0;
            while (true)
            {
                if (Solid(x, y, z)) return t;
                if (tmx < tmy)
                {
                    if (tmx < tmz) { x += sx; t = tmx; tmx += tdx; if (x < 0 || x >= nx) break; }
                    else { z += sz; t = tmz; tmz += tdz; if (z < 0 || z >= nz) break; }
                }
                else
                {
                    if (tmy < tmz) { y += sy; t = tmy; tmy += tdy; if (y < 0 || y >= ny) break; }
                    else { z += sz; t = tmz; tmz += tdz; if (z < 0 || z >= nz) break; }
                }
                if (t > t1) break;
            }
            return maxT;
        }

        bool ClipToBox(Vector3 o, Vector3 d, ref float t0, ref float t1)
        {
            for (int a = 0; a < 3; a++)
            {
                float od = a == 0 ? o.x : a == 1 ? o.y : o.z;
                float dd = a == 0 ? d.x : a == 1 ? d.y : d.z;
                float l = a == 0 ? lo.x : a == 1 ? lo.y : lo.z;
                float h = a == 0 ? hi.x : a == 1 ? hi.y : hi.z;
                if (Mathf.Abs(dd) < 1e-12f) { if (od < l || od > h) return false; continue; }
                float ta = (l - od) / dd, tb = (h - od) / dd;
                if (ta > tb) { float s = ta; ta = tb; tb = s; }
                if (ta > t0) t0 = ta;
                if (tb < t1) t1 = tb;
                if (t0 > t1) return false;
            }
            return true;
        }

        // Closest point on a triangle (Ericson, Real-Time Collision Detection).
        internal static Vector3 ClosestOnTri(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;

            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));

            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
                return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

            float denom = 1f / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }
    }
}
