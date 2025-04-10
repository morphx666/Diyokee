# Diyokee
A work in progress DJ mixing webapp with streaming support

[![Watch the video](https://xfx.net/ftp/diyokee-releases/diyokee-s2.png)](https://xfx.net/ftp/diyokee-releases/diyokee-v1.mp4)

## App settings
The program uses the settings defined in the `settings.json` file.  
Preliminary support for an integrated settings dialog is being worked on, but for now, you can edit the file manually.
To access the settings dialog press the "S" key on your keyboard.

```json
{
  "cert-file": "<optional path to cert file>",
  "cert-password": "<optional cert file password>",
  "bassnet-reg-email": "<optional bass registered email>",
  "bassnet-reg-key": "<optional bass registration key>",
  "webhost-url": "http[s]://[host|ip]:port",
  "encoder": {
    "enabled": true|false,
    "port": <valid port number>,
    "url": "<optional path to the stream>",
    "bitrate": <64|128|192|320>
  },
  "media-providers": [
    {
      "type": "<local is the only supported type>",
      "name": "<optional name of the provider>",
      "root-directory": "<path to the folder contaning the media files>"
    }
  ]
}
```

## Basic usage

- To load a track into a player, click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/eject.svg" width="12"> button.
- Click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/play.svg" width="12"> button to start playing.
- Click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/stop.svg" width="12"> button to stop playback and move to the beginning of the track.
- Click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/down-left-and-up-right-to-center.svg" width="12"> button to define a cue point. At this moment, only one cue point can be defined, and it will always snap to the nearest beat marker.
- Click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/forward-step.svg" width="12"> button to jump to the cue point. Leave the button pressed to temporarily play the track from the cue point.
- Click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/arrow-right-from-bracket.svg" width="12"> button to sync the track to the other player
- Use the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/volume-high.svg" width="12"> fader to change the volume.
- Use the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/music.svg" width="12"> fader to change the tempo (BPM).
- Use the Lo/Mid/Hi knobs to change the track's equalization.
- Use the fader between the two players to cross-fade between them.
- Faders and knobs can be used by clicking and dragging or by moving the mouse over them and using the scroll wheel.
- Use the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/left-long.svg" width="12"> and <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/right-long.svg" width="12"> buttons under the SYNC section to perform small tempo adjustments.
- You can click and drag over both waveforms (synced and full) to change the playback position.
- Use the textbox at the bottom of the files list to search for files. The search is recursive.
- Click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/arrow-rotate-right.svg" width="12"> button in the search bar to re-analyze a track. When re-analyzing a track, the program will ignore the BPM from the track's id3 tags and will recalculate it.
- Double-click a track in the files list to open the Track Properties dialog.
  ![image](https://github.com/user-attachments/assets/fda34783-9973-49c9-8210-37f331cb5c5c)

## Notable missing features

- A fancy screen for remote connections to the stream
- No drag & drop support to load files into a player.
  Use the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/eject.svg" width="12"> button to load a track or press A or B on your keyboard.
- Searching is quite limited and a bit buggy.
- Audio routing for main output, monitor, and stream (with volume adjustment)
- ...and many more

## Releases

Platform|Architecture|Status|Download|Release Date
---|---|---|:---:|---
Windows|x64|Working|[⭳](https://xfx.net/ftp/diyokee-releases/diyokee-win-x64.zip)|2025-04-10
MacOS|x64|Working[^1]|[⭳](https://xfx.net/ftp/diyokee-releases/diyokee-osx-x64.zip)|2025-04-03
MacOS|Arm|Not Tested[^1]|[⭳](https://xfx.net/ftp/diyokee-releases/diyokee-osx-arm64.zip)|2025-04-03
Linux|x64|Working[^1]|[⭳](https://xfx.net/ftp/diyokee-releases/diyokee-linux-x64.zip)|2025-04-03
Linux|Arm|Working[^1]|[⭳](https://xfx.net/ftp/diyokee-releases/diyokee-linux-arm64.zip)|2025-04-03

## Acknowledgements

This project wouldn't have been possible without the following:
- [BASS](https://www.un4seen.com/bass.html) audio library
- [AspNetCore.SassCompiler](https://github.com/koenvzeijl/AspNetCore.SassCompiler)
- [BlazorExtensions.Canvas](https://github.com/BlazorExtensions/Canvas)
- [Icons8](https://icons8.com/)
- [Font Awesome](https://fontawesome.com/)

[^1]: File attributes may be lost when running the app under linux-like systems, including MacOS.  
Use `chmod +x` to set the executable bit on the `diyokee-server` binary.
