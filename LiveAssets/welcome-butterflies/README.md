# OlaChat welcome butterflies

This folder holds the source art and palette definitions. The welcome effect is
implemented in `UnityProject/Assets/Scripts/WelcomeToast.cs`. Runtime copies of the
atlas and manifest are bundled under `Assets/Resources/Welcome/`.

## Files

- `butterfly-flap-atlas.png`: white butterfly wing poses on transparent alpha, intended for tinting.
- `color-themes-preview.png`: still concept showing many butterflies around a small welcome card.
- `asset-manifest.json`: verified dimensions, frame rectangles and proposed palette/motion settings.
- `generation-prompts.txt`: complete prompts and image-generation provenance.

## Runtime behavior

- Draw 12 small butterflies per welcome. Randomize how many occupy each edge, their starting positions and which butterflies are larger; consecutive viewers never reuse the same distribution. Each has a newly generated B-spline flight with independent turns, depth, speed and wingbeat.
- Randomize the dominant movement between wandering flutter, small loops and wider swoops, with individual variations within each flock. Production uses an unseeded random stream; the capture harness seeds that stream once for reproducibility, never once per viewer.
- Trails have a tapered colored glow, a brighter fine core and drifting four-point sparkles. Particles stay at their emission position and fade over 0.8 s.
- Butterflies fly close to and over the card edges/background. Paint the card first, the flock second, then avatar/name/subtitle for legibility. Wing bounds use the measured atlas pivots and furthest frame corners.
- Choose a new position in the lower screen area for each welcome. Keep it still unless the viewport changes or a growing chat feed requires moving it up.
- Shuffle through 12 coordinated palettes before repeating one. Background, border and name use separate colors (for example, mint name on violet or peach name on blue); every name/background pairing exceeds 4.5:1 contrast.
- Do not change theme every frame. Tint the white butterfly sprites from the selected palette.
- Timing: 0.3 s entrance, 2 s readable hold, 0.4 s exit. Background, border, ring, avatar, name and butterflies share the fade; explicit-color rounded texture draws multiply the parent alpha exactly once.
- Wingbeat: blend atlas frames 0 → 1 → 2 → 3 at 9–16 frames/s with an individual phase and bank into turns.
- A fixed array holds the flock and control points. Two small procedural light textures are created once and disposed with the component. No butterfly GameObjects or textures are created per frame. Five trail samples and at most three stars per butterfly cap the flock at 132 IMGUI texture draws per repaint.
- Up to six pending welcomes are queued. Overflow does not mark a viewer as greeted: they can retry on their next interaction. NPCs and spectators are excluded. Snapshots mark existing viewers as greeted while preserving the active animation and pending queue; reset clears both.
- Real avatars share the existing avatar download/cache service; missing avatars show an initial.

## Import handoff

The manifest uses pixel rectangles with a **top-left image origin**. Unity Sprite Editor
uses a bottom-left origin: convert y as `imageHeight - y - height`.
`WelcomeTextureImporter.cs` imports the atlas as an uncompressed transparent texture,
with Clamp wrapping, Bilinear filtering and no mipmaps. The renderer uses atlas UVs and
the supplied **custom pivots** (not cell-center pivots), without creating Sprite objects.
The verified atlas is 1254 × 1254, with four 627 × 627 cells. Preserve its dimensions
when importing (disable non-power-of-two rescaling). The generated poses have small
placement differences; the manifest includes suggested pivots based on visible content.
The four poses retain their original pixel scale when drawn, so folding the wings does
not stretch the body. Rotation is applied before the parent GUI scale to keep flight
paths aligned at portrait and landscape resolutions.

The card background and border are separate UI elements. This preserves dynamic viewer
names/avatars and per-welcome colors. `ClubSceneBuilder` loads the bundled OlaChat background,
with an optional `olachat2.png` override beside the executable.

## Try it

In the game, press F1, open **THÊM KHÁCH VIP**, enter a new name and press **THÊM VÀO**.
Each new name gets one welcome; use another name to see a different theme and position.

For an isolated capture and runtime check, launch a new build with
`-welcomePreviewPath <output-directory>`. This mode bypasses the live bridge, exercises
snapshot/reset/queue/filtering rules, overflow retry, growing-feed clearance, texture ownership, malformed manifests/flags, rendered alpha pixels, flight continuity/bounds and text contrast. It also compares 12 viewers' trajectories after removing card position and butterfly ordering, and checks that consecutive distributions/patterns change. It captures all 12 palettes with a fixed random seed and exits. It needs a rendering
desktop; a fully hidden or batch-mode player can suppress OnGUI and produce black frames.
Optional `-welcomeAvatarUrl <http-url>` checks avatar loading as well, waiting for download completion before capture instead of using an animation frame as the deadline. An invalid or missing preview directory is an explicit launch error. The bootstrap omits the live transport only when the same parsed directory is passed to the harness.

## Review checkpoint

Code checkpoint: `c75a07c` — live diagnostics, welcome UI, Unity compile fixes,
Windows editor discovery, existing OlaChat backgrounds and observed gift metadata.

Review corrections: preserve known gift names when incoming metadata is incomplete;
restore five existing gift names; use Unicode text elements for welcome-name truncation,
measurement and drawing.

Validation: 22 Node tests passed, isolated HTTP/WebSocket security smoke passed, Windows
Unity build succeeded, editor discovery resolved Unity 6000.2.10f1 on the local D: drive.
Those checkpoint checks did not verify a real TikTok live connection. The butterfly
implementation has a separate local rendering and event-flow capture described above.
