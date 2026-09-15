# Changelog

All notable changes to this package are documented here.

## [1.1.0] - 2026-09-15
### Added
- **UV map** dropdown in the paint window. New mesh selections default to UV0; available UV1–UV7 channels can be displayed individually, with the Scene view marker using the same channel.
- **Paint layers on the Length and Alpha tabs.** A small Photoshop-style stack: add, duplicate, delete,
  reorder, rename, show/hide, opacity, and a blend mode per layer (Normal / Min / Max). Painting goes to the
  selected layer, so work can be tried, kept or dropped without disturbing what is underneath. Direction stays
  a single flat field.
- Each layer carries a coverage channel beside its value, which is what lets a mask layer tell "painted black"
  apart from "not painted".
- The layer rows carry column headers, and the two per-row controls are now visually distinct: a **Show**
  checkbox for whether a layer is in the mask (any number may be ticked), and a **Paint** radio for the single
  layer the brush writes into. The selected row is highlighted, and the panel warns when every layer is hidden.
- The panel also warns when a layer is painted edge to edge at full opacity, since it then hides every layer
  below it - two filled layers with an erased hole in each cancel out, which is otherwise a puzzling thing to
  run into. `Fill`, `Gradient` and `Load mask` all take a layer solid; the README explains marks versus holes.
- **`Holes -> marks`** repairs exactly that stack in one click: it turns the selected layer inside out, keeping
  only what was erased as black marks and going transparent elsewhere, so every layer shows at once and each
  still toggles on its own. The warning offers the same button by name.
- **`Merge shown`** flattens the shown layers into one, exactly as the mask composites; hidden layers are kept.
- `Fill white` and `Fill black` on the Length tab, matching the Alpha tab: they fill the selected layer solid.
- The bottom layer cannot be deleted - it is the foundation the mask composites onto, and losing it would
  silently drop everything the layers above are painted over. Reorder to move a layer out of the bottom slot
  if it really has to go; a generated resolver layer stays droppable wherever it sits.
- **The occlusion resolver now owns a layer.** A resolve writes only that layer, with the exact texels it
  changed as its coverage and Min as its blend, so it can be hidden, faded or dropped without touching the
  groom below - and re-running rewrites nothing else. Show/hide/opacity/drop controls sit on the Collision tab.
- Groom files gained a v2 format that stores the whole stack. Files written by 1.0.x still load, into a single
  base layer per mask.
- **Fur occlusion resolver**, on its own **Collision** tab: keeps groomed fur out of clothing by measuring the
  outfit's shadow on the body. A fan of rays over the hemisphere at each texel gives how shadowed the spot is,
  how much clear headroom it has, and which way there is still room; the fur is then leaned over just far enough
  for its tip to fit under the headroom, its bearing nudged towards the open side, and its length capped by the
  room left along the direction it ends up in.
- The clothing is rasterized into a voxel volume once, so a ray is a plain 3D-DDA walk over a bitset rather than
  a triangle-list test. A 20k-triangle garment against a full 256x256 direction field resolves in about 0.15s at
  medium quality, 0.20s at fine. The texel pass is threaded, and a coarse copy of the volume rejects the parts of
  the body nowhere near the outfit.
- The coverage field is shown as a map on the tab, beside previews of the three masks the resolver writes, so
  what the tool makes of your avatar can be looked at rather than guessed at.
- Scene-view debug hairs for the last run: surface normal in green, fur as groomed in blue, fur after resolving
  in red.
- Revert, and a resolve counts as a single undo step.
### Changed
- **Right-drag on the Length and Alpha tabs now lays down a black mark** on any layer above the bottom one, so
  it stacks over the layers below instead of cutting a hole that shows a layer which is usually painted too.
  On the bottom layer - where a hole and a black mark composite identically, nothing being underneath - it
  stays a true erase that lifts coverage. **Shift + right-drag** forces a real hole anywhere, which is how a
  mark is taken back off a layer.
- `Holes -> marks` is unavailable on the bottom layer, since inverting that one throws away the fill every
  other layer sits on.
- Export, the material preview and the resolver's input all read the composite, so what is saved is what is
  shown. The Collision tab's Length and Alpha previews now refresh together instead of leaving one stale.
- Undo snapshots the edited layer for a stroke or a whole-canvas op, and the whole stack only for operations
  that change the stack itself (mirror, resolve, load, add/delete/reorder).
- Mirror applies to every layer of both masks, mirroring coverage along with the value.
- "Smooth all" is now "Smooth layer" and blurs coverage alongside the value, so the painted region's edge
  softens with it.

## [1.0.3] - 2026-06-22
### Added
- Undo / redo: Ctrl+Z, Ctrl+Y (or Ctrl+Shift+Z), and toolbar buttons. One step per stroke and per operation (clear, fill, smooth, mirror, load); snapshots only the layers that change. History is per session.
- Load mask: import an existing grayscale image into the Length or Alpha buffer to keep editing it (round-trips with the exported PNG).

## [1.0.2] - 2026-06-22
### Added
- Scene-view brush highlight: shows the brush position on the assigned mesh in the Scene view, marking every match (handles symmetric/overlapping UVs). Adjustable marker shape, color, and size.
- Smoothed camera follow with optional align-to-surface-normal, distance (zoom), and field-of-view controls.
- Grab the UV layout from the assigned mesh as the paint background — generated automatically when the mesh is assigned, with a manual Refresh UV button. Uses a direct mesh reference instead of guessing by material.
### Changed
- Settings reorganized into collapsible groups inside a height-capped scroll view, so the paint canvas keeps its space as more options are added.

## [1.0.1] - 2026-06-22
### Added
- Output folder + file base name fields: maps save directly to a set folder and overwrite existing files (no save dialog). Leave the folder blank to be asked each time.

## [1.0.0] - 2026-06-22
### Added
- Initial release.
- Direction mode: comb fur flow + tilt, export tangent-space normal map.
- Length mode: soft/gradient grayscale brushes, smudge, linear gradient, global smooth.
- Alpha mode: hard black/white presence painting with threshold + soft-edge option.
- Zoom & pan, deterministic mirror (with correct direction flipping), right-drag to erase/paint black, colored action buttons.
- PNG export with correct import settings and optional liltoon material auto-assign.
