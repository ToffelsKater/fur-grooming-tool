# Changelog

All notable changes to this package are documented here.

## [Unreleased]
### Added
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
