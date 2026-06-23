# Changelog

All notable changes to this package are documented here.

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
