# Viewer chat bubbles

White bubble with animated champagne sparkles, based on selected concept 02. Runtime assets and prompts: [asset notes](../design/chat-bubbles/assets.md).

## Behavior

- Only `chat` events for joined viewers display a bubble. NPC, `demo-*`, `master-test`, blank comments and spectators without an actor are excluded. Joining rules and camera focus triggers are unchanged.
- One bubble per person, 4.5 seconds after their latest nonempty chat. New comments replace the current text at most every 0.65 seconds; one pending slot keeps only the latest message. No message queue or coroutine per chat.
- Pending text and expiry are processed once in `LateUpdate`, after the transport delivers the frame's events. An incoming chat replaces an overdue pending message before it renders; no full-room scan runs for every received message.
- Single line, bold dark text on white; pixel-measured overflow ends in `…`. Whitespace collapses, rich text is disabled, Unicode truncation preserves accents, surrogate pairs, flags, modifiers and ZWJ emoji groups. Long input is bounded before layout.
- Up to six nonoverlapping bubbles can render simultaneously. Recent messages have priority; obstructed bubbles retain their normal expiry, so they can appear when space clears. State is capped at 400 entries.
- Position is projected during `OnGUI` repaint, after camera `LateUpdate`, above the actual sprite and visible name/avatar/rank. Bubbles follow jumps, scale changes and camera motion without screen-position interpolation lag. Off-screen, near-plane, dimmed and edge-clipped bubbles are hidden instead of clamped away from their owners. The pointer stays horizontally centered on its actor. Visible TOP and open controls reserve their screen space.
- Entry and exit fade; three separate sparkles pulse and move along the outer rim. Existing bubble entrance does not restart on every message.
- Reset/reconnect clears all state. Removed or deactivated actors cannot leave orphan bubbles.

## Automated built-player checks

Build with Unity 6000.2.10f1, `TikTokLiveGame.Editor.CreateTikTokScene.BuildWindowsGame`. Then launch the resulting `UnityProject/Builds/TikTokBarGame.exe` with:

```text
-welcomePreviewPath D:/TEST/tiktok-live-bar/UnityProject/Logs/chat-bubbles-preview -chatBubblePreview -logFile D:/TEST/tiktok-live-bar/UnityProject/Logs/chat-bubbles-player.log
```

This explicit preview mode creates **no WebSocket client**. It writes PNG captures and `verification.txt`, exits 0 on success, or writes `failure.txt` and exits 2 on failure. Normal game launches do not run the harness.

The harness exercises real-user gating, spectator join policy, normalization/Unicode, rendered ellipsis, same-user flood, overlap arbitration, eight director shots at five samples, grow/jump, behind-camera removal, actor deactivation, fade/expiry, reconnect, reset, and a 400-person / 5,000-update load. Inspect the actual screenshots as well as the assertions.

## Verified on 2026-09-18

- Reviewed Windows build succeeded; built-player harness exited 0: **477 assertions, 84 visible placements, 40 camera samples**, 1,000 comments from one viewer and 400 viewers / 5,000 updates. The update burst took 66 ms in this synthetic, single-frame loop; it is not an FPS measurement or a live-server throughput guarantee.
- Inspected the rendered white bubble, ellipsis, simultaneous viewers, camera shots, opaque crowd bubbles and exit fade. Twelve captured animation frames cover the moving/pulsing rim sparkles.
- `CameraFocusChecks.Run` also passed the existing bot-focus and sparse-viewer framing regression checks.
- Logs/screenshots: `UnityProject/Logs/chat-review-final/`, `chat-review-final.log`, `chat-review-build-final.log`, `chat-review-camera.log` (local ignored artifacts).
- Delivered to `Build/TikTokBarGame.exe`; all 138 runtime files match the tested build. Non-runtime files, including custom image/music/video, retain their SHA-256 hashes; changed runtime files are backed up under `Build/Backups/`.
- Player assembly SHA-256: `28A52E381FB7566B7E93FB757DE43EBB1D6A333D8CC74C0F16817506C28EFD3B`.
- Review removed unused `PlayerActor.SetDimmed`, temporary render-state logging, duplicate fixture positioning, redundant variation-selector checks and redundant text-cache invalidation. Regression checks cover overdue pending text, duplicate replacement, re-entry after expiry, huge blank input and removal from the rendered frame.

For an automated hidden launch on this Windows machine, the rendering test used `-popupwindow -screen-width 540 -screen-height 960 -screen-fullscreen 0` and showed the preview window after the initial six seconds. Keeping the window hidden produces black captures; that is not a valid visual check. This only applies to the explicit offline preview.
