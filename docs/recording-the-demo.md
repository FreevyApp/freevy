# Recording the demo

The animation in the README is a real recording of Freevy, not a mock-up. `build/record-demo.ps1` starts a separate Freevy with the made-up snippets in `build/demo/data` (your own library stays closed and untouched), opens `build/demo/message.html` in an Edge app window, types a short email with real key presses and records the window with ffmpeg.

1. Build the app: `dotnet build src/Freevy.App -c Release`.
2. Run `powershell -NoProfile -ExecutionPolicy Bypass -File build/record-demo.ps1` and keep your hands off the keyboard and mouse for about 40 seconds. If the demo window loses focus, the script stops typing at once.
3. Find where typing starts and ends in `build/out/demo/demo.mkv` (in the last recording: 0.8 s to 12 s), then make the GIF and the MP4:

```powershell
ffmpeg -ss 0.8 -to 12 -i build/out/demo/demo.mkv -vf "fps=15,scale=720:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle" -loop 0 docs/images/freevy-demo.gif
ffmpeg -ss 0.8 -to 12 -i build/out/demo/demo.mkv -vf "scale=864:-2" -c:v libx264 -crf 26 -preset slow -pix_fmt yuv420p -movflags +faststart -an docs/images/freevy-demo.mp4
```

The script records a screen region rather than the window itself: Chromium draws with the GPU, and window capture would only see a blank frame.
