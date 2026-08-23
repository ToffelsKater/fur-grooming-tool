# Fur Grooming Tool

A Unity editor tool for authoring liltoon fur textures by painting over a UV layout.
Three modes share one window and one UV background:

| Mode | What you paint | Output | liltoon slot (default, editable) |
|------|----------------|--------|----------------------------------|
| Direction | Fur flow + tilt strength | Tangent-space normal map | `_FurVectorTex` (or `_BumpMap`) |
| Length | Soft / gradient grayscale | Fur length mask | `_FurLengthMask` |
| Alpha | Hard black / white | Fur presence mask | `_FurMask` |

A fourth **Collision** tab holds the fur occlusion resolver instead of a brush - see below.

This is a **local Unity package**, so one copy can be added to any number of projects.

## Install

1. Place this package folder somewhere stable (keeping it outside your projects is fine).
2. In the target project: `Window ▸ Package Manager`.
3. Click `+` (top-left) ▸ **Add package from disk…**
4. Select the `package.json` inside this folder.

Unity records it in that project's `Packages/manifest.json` as a `file:` reference.
Repeat per project. Keep the folder where it is — moving it breaks the reference.

Open the tool from the menu: **Tools ▸ Fur Grooming Tool**.

## Workflow

1. Drag your UV-layout snapshot into **Background**.
2. (Optional) drag the avatar's liltoon **material** into **Target material** to auto-assign maps on save.
3. Pick a tab and paint:
   - **Left-drag** paints. **Right-drag** paints black / erases. **Scroll** to zoom (cursor-anchored). **Middle-drag** to pan.
   - **Direction** — drag to comb. `Pinch` converges a tuft to a point; `Direction` / `Strength` / `Erase` edit one channel.
   - **Length** — `Paint` (soft, builds gradients via flow + hardness), `Smudge` (melts edges smooth), `Gradient` (drag a line for a root→tip ramp). `Smooth all` blurs the whole mask.
   - **Alpha** — `Paint white` / `Paint black` hard brushes, `Fill white` / `Fill black`, `Threshold` + soft-edge toggle for crisp cutouts (paw pads, under clothing).
4. **Symmetry** — paint one side, choose the **Mirror** direction + axis, then **Apply mirror** to copy it across. Direction vectors are flipped correctly across the axis.
5. **Save … map** (green button) writes a PNG into `/Assets` with the correct import settings (Normal map / linear grayscale, sRGB off) and assigns it to the material property if one is set.
6. **Save groom / Load groom** (Direction tab) stores all three layers in a groom file (`.bytes`) so you can resume later.

## Fur collision resolver

Keeps groomed fur out of clothing, on its own **Collision** tab. Assign the fur mesh, add the
clothing renderers, press **Resolve**. It edits the Direction and Length layers (and the Alpha
mask, if you let it) in place, so you can keep painting afterwards, export from the tab's own save
buttons, `Ctrl+Z` it, or press **Revert**.

### How it works

Rather than testing whether each hair pierces a triangle, it measures **the outfit's shadow on the
body**. Every texel of the direction field is sampled back onto the fur mesh for a world-space root,
normal and tangent frame, and a fan of rays is cast over the hemisphere there. That gives three
things per spot: how much of the sky the clothing blocks, how much clear headroom there is straight
out from the skin, and which way along the surface there is still room.

The fur is then leaned over just far enough for its tip to fit under the headroom, its bearing
nudged towards the open side, and its length capped by the room left along the direction it ends up
in. Being a continuous field rather than a yes/no per hair, it copes with garments that clip into
the body or overlap themselves, and it produces smooth maps rather than speckle.

The clothing is turned into a **voxel volume** once, up front, so a ray is a plain walk over a
bitset rather than a test against a list of triangles. A 20,000-triangle garment against the full
256x256 field resolves in about **0.15s** at medium quality.

### Reading the result

The tab shows the **coverage map** next to previews of the three masks the resolver writes. The
coverage map is the one to look at first: it draws the outfit's shadow over your UV layout, dark red
where clothing covers the body and grey in the open. If it does not outline your outfit, the problem
is the meshes or **Fur length**, not the grooming.

The result line reports how many texels landed on the mesh, how many are near clothing, how many are
shadowed, and what was done to them. **Debug hairs** draws the last run in the Scene view: surface
normal in green, fur as groomed in blue, fur after resolving in red.

| Setting | What it does |
|---------|--------------|
| Fur length (mm) | World length of a hair where the length mask is fully white. Match your shader, or the shadow is measured over the wrong distance. |
| Cloth thickness (mm) | Fattens the clothing in every direction. Raise it if fur slips through thin or badly fitted garments. |
| Length margin (mm) | Slack kept between the fur tip and the clothing. Raise it if the fur is clean in the editor but pokes through once the body moves. |
| Ray quality | Rays per texel (11 / 25 / 55). More reads thin gaps better; all three are fast. |
| Max tilt angle | Hard cap on how far a hair may lean from the surface normal. Also limited by the Direction tab's own cap, since that is what the normal map can encode. |
| Shadow threshold | How shadowed a spot must be before its fur is touched at all. |
| Comb into open space | How hard the fur is steered into the room that is left. `0` keeps your groom exactly and only shortens. |
| Smooth result | 3x3 blur passes over the texels the resolver touched. |
| Culled fur -> alpha black | Punch fully trimmed fur out of the Alpha mask, so no shell is drawn there at all. |

Both meshes are read **in their current pose**, so pose the avatar before pressing Resolve. Avatar
UVs are often mirrored, so one texel can land on several places on the body at once; all of them are
kept and the worst case wins, otherwise one side ends up groomed against the other side's geometry.


## Notes

- **Green channel:** `Flip G (Unity/OpenGL)` is on by default (correct for Unity/liltoon). If fur lighting looks inverted, flip it and regenerate.
- **liltoon keywords:** assigning to `_BumpMap` also sets `_UseBumpMap`, but you may still need to tick **Normal Map** once in the liltoon inspector. For fur, enable the fur preset and point `_FurVectorTex` / `_FurLengthMask` / `_FurMask` at the outputs.
- Property names are all editable in the UI, so the tool also works with other shaders.

## License

Provided as-is, with no warranty. Free to use, modify, and redistribute.
