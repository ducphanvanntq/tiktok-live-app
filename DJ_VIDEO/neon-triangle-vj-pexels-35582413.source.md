# Stage video

`neon-triangle-vj-pexels-35582413.mp4` is the selected stage loop, converted for Unity's Windows video player. It loops silently; game music plays separately and does not drive the animation.

- Title: Vibrant Neon Triangle Animation Loop
- Creator: Nicola Narracci
- Source: https://www.pexels.com/video/vibrant-neon-triangle-animation-loop-35582413/
- Original download: https://videos.pexels.com/video-files/35582413/15077955_1920_1080_30fps.mp4
- License: https://www.pexels.com/license/
- Downloaded: 2026-09-19
- Original SHA-256: `F2C63A2C9A81BB73770403AD347676030964654FAEDC3975F4EC7885B96FFA2B`
- Installed format: H.264 Constrained Baseline, level 4.0, yuv420p, 1920 x 1080, 30 fps, 10 seconds, MP4 faststart, no audio.
- Installed size: 13,996,338 bytes.
- Installed SHA-256: `F2B263A481A761B054C1D24F3F9EFABED100A861C24BA700C2E0A4AB2174BBF9`

The original fragmented MP4 did not expose duration correctly to Unity's Windows media backend. Conversion with FFmpeg 7.1 also removes timestamp warnings:

```sh
ffmpeg -i original.mp4 -map 0:v:0 -c:v libx264 -profile:v baseline -level:v 4.0 -crf 18 -preset medium -pix_fmt yuv420p -r 30 -bf 0 -an -movflags +faststart neon-triangle-vj-pexels-35582413.mp4
```

Place this folder beside the executable (inside `Contents/Resources` on macOS). The release packaging scripts include it automatically.
