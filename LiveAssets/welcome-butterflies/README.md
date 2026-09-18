# OlaChat welcome butterflies — asset preparation

This folder is an asset handoff, not an installed gameplay feature.
The existing welcome UI is unchanged by this pack.

## Files

- `butterfly-flap-atlas.png`: white butterfly wing poses on transparent alpha, intended for tinting.
- `color-themes-preview.png`: still concept showing many butterflies around a small welcome card.
- `asset-manifest.json`: verified dimensions, frame rectangles and proposed palette/motion settings.
- `generation-prompts.txt`: complete prompts and image-generation provenance.

## Intended behavior

- Use 10–12 small butterflies per welcome, with different sizes, flap phases and curved paths.
- Keep butterflies outside the card's avatar and text; keep the whole effect inside the viewport.
- Choose a new position in the lower screen area for each welcome. Hold the card still while its name is readable.
- Choose one coordinated background/border palette per welcome. Keep text ivory for contrast.
- Do not change theme every frame. Tint the white butterfly sprites from the selected palette.
- Suggested timing: 0.3 s entrance, 2 s readable hold, 0.4 s exit. Fade butterflies out with their card.
- Suggested wingbeat: loop atlas frames 0 → 1 → 2 → 3 at 12 frames/s with a random initial phase.
- Pool butterfly objects when integrated; do not download or generate assets during a live session.

## Import handoff

The manifest uses pixel rectangles with a **top-left image origin**. Unity Sprite Editor
uses a bottom-left origin: convert y as `imageHeight - y - height`.
Use transparent Sprite (2D/UI) textures, Multiple mode for the atlas, Clamp wrapping,
Bilinear filtering and the supplied **custom pivots** (not cell-center pivots).
The verified atlas is 1254 × 1254, with four 627 × 627 cells. Preserve its dimensions
when importing (disable non-power-of-two rescaling). The generated poses have small
placement differences; the manifest includes suggested pivots based on visible content.
Inspect and fine-tune the flap loop before tuning particle motion.
The atlas is concept-generated art; timing and smoothness still need an in-game preview.

The card background and border should be drawn as separate UI elements rather than baked
into a butterfly image. This preserves dynamic viewer names/avatars and per-welcome colors.

## Review checkpoint

Code checkpoint: `c75a07c` — live diagnostics, welcome UI, Unity compile fixes,
Windows editor discovery, existing OlaChat backgrounds and observed gift metadata.

Review corrections: preserve known gift names when incoming metadata is incomplete;
restore five existing gift names; use Unicode text elements for welcome-name truncation,
measurement and drawing.

Validation: 22 Node tests passed, isolated HTTP/WebSocket security smoke passed, Windows
Unity build succeeded, editor discovery resolved Unity 6000.2.10f1 on the local D: drive.
This did not verify a real TikTok live connection or the visual behavior of the proposed
butterfly UI. The current scene still uses its existing background path; this pack does not
switch it to OlaChat or install the proposed card.
