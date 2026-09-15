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

1. Assign **Mesh (renderer)** to generate a UV layout, or drag a UV-layout snapshot into **Background**. **UV map** defaults to **UV0** for each newly assigned mesh; choose another available channel (UV1 through UV7) to display that channel alone. The Scene view marker follows the selected channel too. **Refresh UV** reloads the layout from the mesh. This selects the painting guide, not the material's texture UV settings; the Collision resolver still uses UV0.
2. (Optional) drag the avatar's liltoon **material** into **Target material** to auto-assign maps on save.
3. Pick a tab and paint:
   - **Left-drag** paints. **Right-drag** marks black (or erases, on the bottom layer / with Shift). **Scroll** to zoom (cursor-anchored). **Middle-drag** to pan.
   - **Direction** — drag to comb. `Pinch` converges a tuft to a point; `Direction` / `Strength` / `Erase` edit one channel.
   - **Length** — `Paint` (soft, builds gradients via flow + hardness), `Smudge` (melts edges smooth), `Gradient` (drag a line for a root→tip ramp). `Fill white` / `Fill black` fill the selected layer, `Smooth layer` blurs it.
   - **Alpha** — `Paint white` / `Paint black` hard brushes, `Fill white` / `Fill black`, `Threshold` + soft-edge toggle for crisp cutouts (paw pads, under clothing).
4. **Layers** (Length and Alpha tabs) — paint onto a stack instead of one flat mask, so an idea can be tried and then kept or dropped on its own. See below.
5. **Symmetry** — paint one side, choose the **Mirror** direction + axis, then **Apply mirror** to copy it across. Direction vectors are flipped correctly across the axis; every layer is mirrored.
6. **Save … map** (green button) writes a PNG into `/Assets` with the correct import settings (Normal map / linear grayscale, sRGB off) and assigns it to the material property if one is set.
7. **Save groom / Load groom** (Direction tab) stores the direction field and both layer stacks in a groom file (`.bytes`) so you can resume later. Groom files written by 1.0.x still load, into a single base layer per mask.

## Paint layers

The **Length** and **Alpha** masks are layer stacks. Painting goes to the selected layer only;
the tool composites bottom to top and everything downstream — the canvas, the output preview, the
exported PNG and the collision resolver's input — reads that composite, so what you see is what is
written.

| Control | What it does |
|---|---|
| Add / Duplicate / Delete | Adds above the selection, copies it, or removes it. The **bottom** layer cannot be deleted — it is the foundation the mask composites onto. Move another layer below it first if you really want it gone. |
| Up / Down | Moves the selected layer through the stack. |
| **Show** (checkbox) | Includes the layer in the mask. Several can be ticked at once — they stack. Hiding and showing again restores the composite exactly. Hide them all and the mask is empty; the panel says so. |
| **Paint** (radio) | The one layer the brush writes into. |
| Name | Rename freely; generated layers show their name in bold and cannot be painted on. |
| Blend | `Normal` replaces what is below, `Min` only ever darkens it, `Max` only ever brightens it. |
| Opacity | Fades the whole layer. `0` is an exact no-op, `1` an exact replace. |

Each layer holds a **coverage** channel next to its value — how much of the layer is actually
painted. That is what lets a mask layer tell "painted black" apart from "not painted", and it is
what an erase edits: lifting coverage reveals the layers below rather than stamping black over them.

### Marks versus holes

This is the one thing worth getting straight, because it is easy to get backwards:

- **Painting** makes a **mark** — it covers the layer, so it shows over everything below it.
- **Right-drag** also makes a **mark**, of the "away" value (black / zero), on every layer above the
  bottom one. A hole there would only show the layer below, which is usually painted too, so the
  erase would read as nothing at all.
- **On the bottom layer**, right-drag makes a **hole** — there is nothing underneath, where a hole
  and a black mark come out identical anyway.
- **Shift + right-drag** forces a real hole on any layer. That is how you take a mark back off a
  layer and let the one below show through.

Whole-canvas operations (`Fill white` / `Fill black`, `Gradient`, `Load mask`) take the layer
**solid**: covered edge to edge. A solid layer hides every layer below it, so erasing a hole in it
reveals nothing but black unless the layer underneath is painted at that spot. Two solid layers
with a hole erased in each therefore cancel out — each one plugs the other's hole, and you see no
holes at all. The panel warns when a layer is solid and something below it is being blocked.

The workflow that stacks properly: keep **one** solid layer at the bottom (`Fill white`), leave the
layers above it **empty**, and paint only the marks you want onto them — `Paint black (bald)` on
the Alpha tab, or any value on the Length tab. Then each layer can be shown and hidden on its own.

If you have already filled several layers and erased into them, **`Holes -> marks`** repairs it in
one click: it turns the selected layer inside out, keeping only what you erased as black marks and
going transparent everywhere else. The panel offers the same button by name in its warning. Each
layer still shows and hides independently afterwards. It is unavailable on the bottom layer, where
inverting would throw away the fill everything else sits on — move that layer up first if you
really mean to.

**`Merge shown`** flattens every shown layer into one, exactly as the mask composites, and leaves
hidden layers where they are.

Direction stays a single flat field: blending two vector fields raises questions this tool does not
need to answer.

## Fur collision resolver

Keeps groomed fur out of clothing, on its own **Collision** tab. Assign the fur mesh, add the
clothing renderers, press **Resolve**.

The result lands on **its own layer** in each mask — a `Min` layer whose coverage is exactly the
texels the resolve changed. So the tab's **Show resolve layer** toggle turns the resolve on and off
instantly against your untouched groom, the slider fades it, and **Drop layer** removes it. Running
Resolve again rewrites that one layer and nothing else. Your hand-painted length and alpha work is
never overwritten. The Direction field is still edited in place, so `Ctrl+Z` and **Revert** remain
the way back for that.

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
