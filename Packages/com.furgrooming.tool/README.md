# Fur Grooming Tool

A Unity editor tool for authoring liltoon fur textures by painting over a UV layout.
Three modes share one window and one UV background:

| Mode | What you paint | Output | liltoon slot (default, editable) |
|------|----------------|--------|----------------------------------|
| Direction | Fur flow + tilt strength | Tangent-space normal map | `_FurVectorTex` (or `_BumpMap`) |
| Length | Soft / gradient grayscale | Fur length mask | `_FurLengthMask` |
| Alpha | Hard black / white | Fur presence mask | `_FurMask` |

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

## Notes

- **Green channel:** `Flip G (Unity/OpenGL)` is on by default (correct for Unity/liltoon). If fur lighting looks inverted, flip it and regenerate.
- **liltoon keywords:** assigning to `_BumpMap` also sets `_UseBumpMap`, but you may still need to tick **Normal Map** once in the liltoon inspector. For fur, enable the fur preset and point `_FurVectorTex` / `_FurLengthMask` / `_FurMask` at the outputs.
- Property names are all editable in the UI, so the tool also works with other shaders.

## License

Provided as-is, with no warranty. Free to use, modify, and redistribute.
