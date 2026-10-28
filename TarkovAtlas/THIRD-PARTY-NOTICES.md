# Third-Party Notices

Tarkov Atlas is an unofficial, non-commercial community tool. It is not
affiliated with, endorsed by, or sponsored by Battlestate Games Limited.
"Escape from Tarkov", its maps, names and game assets are the property of their
respective rights holders.

## Map artwork

### tarkov-dev-svg-maps

- Project: https://github.com/the-hideout/tarkov-dev-svg-maps
- Authors: Shebuka and contributors
- License: **Creative Commons Attribution-NonCommercial-ShareAlike 4.0
  International (CC BY-NC-SA 4.0)**
- Used for: the baked raster images of Customs, Ground Zero, Interchange,
  Lighthouse, Reserve, Shoreline, Streets of Tarkov, Terminal and Woods.
- Changes: the SVG source files were rasterized, rotated to a north-up view,
  flattened onto a dark background and saved as JPEG. Attribution and the
  share-alike/non-commercial terms continue to apply to these derived images.
- The upstream project explicitly prohibits use of its assets in cheating
  software, in-game radars/ESP overlays, or automation/pixel bots. This project
  is a map reader and quest/screenshot helper, not a radar or cheat; do not
  repurpose its assets for such software.

### tarkov.dev map data and tile layers

- Project: https://github.com/the-hideout/tarkov-dev
- Code license: MIT
- Used for: current map list, affine/coordinate metadata, place labels, map
  tile layers for Factory, The Lab, The Labyrinth and Icebreaker, and the
  latest 2D map images used during development.
- The tile/map imagery depicts locations from Escape from Tarkov and remains
  subject to the rights of the game's rights holders. It is bundled here for
  personal, non-commercial use only.

## Quest data

- Project: https://github.com/the-hideout/tarkov-data-manager
- File: `src/tarkov-data-manager/public/data/quest-data.json`
- Code license: MIT
- Used for: the offline quest snapshot in `Assets/quests.json`.
- Quest names, objectives and links are community-maintained data. Newer quests
  may be missing; the app has no custom-quest editor, so update the bundled
  snapshot by re-running tools/build_data.py instead.

## Runtime libraries

- **Avalonia UI** — MIT — https://avaloniaui.net/
- **SkiaSharp** — MIT — https://github.com/mono/SkiaSharp
- **HarfBuzzSharp** — MIT — https://github.com/mono/SkiaSharp
- **ANGLE / libEGL / libGLESv2** — BSD-style — https://chromium.googlesource.com/angle/angle

## Windows OCR

The optional quest-screenshot reader calls the locally installed
`Windows.Media.Ocr` engine through Windows PowerShell. No screenshot or text is
uploaded anywhere.