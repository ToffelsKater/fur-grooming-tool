# Fur Grooming Tool

A Unity editor tool for authoring **liltoon fur** textures by painting directly over a UV layout.
Three modes share one window and one UV background:

| Mode | What you paint | Output | liltoon slot (default, editable) |
|------|----------------|--------|----------------------------------|
| Direction | Fur flow + tilt strength | Tangent-space normal map | `_FurVectorTex` (or `_BumpMap`) |
| Length | Soft / gradient grayscale | Fur length mask | `_FurLengthMask` |
| Alpha | Hard black / white | Fur presence mask | `_FurMask` |

Editor-only, no runtime code. Works with liltoon out of the box, and with any shader since the target property names are editable.

## Install (VRChat Creator Companion)

1. Open the listing page and click **Add to VCC**:
   **https://toffelskater.github.io/fur-grooming-tool/**
2. Or in VCC → **Settings → Packages → Add Repository**, paste:
   ```
   https://toffelskater.github.io/fur-grooming-tool/index.json
   ```
3. Then in any avatar project, add **Fur Grooming Tool** from the package list.

Open it from the menu: **Tools ▸ Fur Grooming Tool**.

## Quick start
https://youtu.be/hLP4zzNyt4g?si=GOuS5RGrwHpL9ePX

1. Drag your UV-layout snapshot into **Background**.
2. (Optional) drag the avatar's liltoon **material** into **Target material** to auto-assign maps on save.
3. Paint: **left-drag** paints, **right-drag** marks black (on the bottom layer, or with Shift, it erases instead), **scroll** to zoom (cursor-anchored), **middle-drag** to pan.
   - **Direction** — drag to comb. `Pinch` converges a tuft; `Direction` / `Strength` / `Erase` edit one channel.
   - **Length** — `Paint` (soft gradient buildup), `Smudge`, `Gradient` (drag a root→tip ramp), `Fill white` / `Fill black`, `Smooth layer`.
   - **Alpha** — hard `Paint white` / `Paint black`, `Fill`, `Threshold` for crisp cutouts (paw pads, under clothing).
4. **Layers** — the Length and Alpha masks are layer stacks: add, reorder, hide, fade, and blend (`Normal` / `Min` / `Max`). Painting only ever touches the selected layer; right-drag marks black so it stacks, and Shift + right-drag erases through to the layers below.
5. **Symmetry** — paint one side, set **Mirror** + axis, then **Apply mirror** (direction vectors are flipped correctly).
6. **Save … map** writes a PNG into `/Assets` with correct import settings (Normal map / linear grayscale, sRGB off) and assigns it to the material if one is set.
7. **Save groom / Load groom** stores the direction field and both layer stacks so you can resume later.

See the [package README](Packages/com.furgrooming.tool/README.md) for full details.

## Notes

- **Green channel:** `Flip G (Unity/OpenGL)` is on by default (correct for Unity/liltoon). If fur lighting looks inverted, flip it and regenerate.
- For liltoon fur, enable the fur preset and point `_FurVectorTex` / `_FurLengthMask` / `_FurMask` at the exported maps.

## License

[MIT](Packages/com.furgrooming.tool/LICENSE) — free to use, modify, and redistribute.
