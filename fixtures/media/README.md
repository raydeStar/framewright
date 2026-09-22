# Director Mode playback fixture

`director-preview.mp4` is a synthetic four-second solid-color video, generated locally for playback/layout regression checks. It contains no third-party media.

```sh
ffmpeg -f lavfi -i color=c=0x326773:s=160x90:r=10:d=4 -c:v libx264 -pix_fmt yuv420p -movflags +faststart director-preview.mp4
```
