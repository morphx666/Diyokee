# Diyokee
A work in progress, DJ mixing webapp with streaming support

[![Watch the video](https://xfx.net/ftp/diyokee-releases/diyokee-s2.png)](https://xfx.net/ftp/diyokee-releases/diyokee-v1.mp4)

## App settings

To access the settings dialog, press the `Ctrl+Alt+S` key on your keyboard.  
[Audio routing](https://xfx.net/ftp/diyokee-releases/diyokee-settings-audiomatrix.png) has not yet been implemented, but you can select the audio device to use for playback.
Some settings cannot be yet configured by the Settings dialog, but you can edit the `settings.json` file manually.

## Basic usage

- To load a track into a player, click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/eject.svg" width="12"> button.  
  You can also use `Ctrl+Alt+A` or `Ctrl+Alt+B` keys on your keyboard to load a track into the A or B player, respectively.
- Click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/play.svg" width="12"> button to start playing.
- Click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/stop.svg" width="12"> button to stop playback and move to the beginning of the track.
- Click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/down-left-and-up-right-to-center.svg" width="12"> button to define a cue point. At this moment, only one cue point can be defined, and it will always snap to the nearest beat marker.
- Click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/forward-step.svg" width="12"> button to jump to the cue point. Leave the button pressed to temporarily play the track from the cue point.
- Click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/arrow-right-from-bracket.svg" width="12"> button to sync the track to the other player
- Use the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/volume-high.svg" width="12"> fader to change the volume.
- Use the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/music.svg" width="12"> fader to change the tempo (BPM).
- Use the Lo/Mid/Hi knobs to change the track's equalization.  
  Right-click over the Eq control to [display a menu with several presets](http:/xfx.net/ftp/diyokee-releases/diyokee-switch-eq-profiles.mp4) from popular mixing consoles.
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
Windows|x64|Working|[<img src="https://xfx.net/ftp/diyokee-releases/dlicon.png">](https://xfx.net/ftp/diyokee-releases/diyokee-win-x64.zip)|2025-04-14
Linux|x64|Working[^1]|[<img src="https://xfx.net/ftp/diyokee-releases/dlicon.png">](https://xfx.net/ftp/diyokee-releases/diyokee-linux-x64.zip)|2025-04-11
Linux|Arm|Working[^1]|[<img src="https://xfx.net/ftp/diyokee-releases/dlicon.png">](https://xfx.net/ftp/diyokee-releases/diyokee-linux-arm64.zip)|2025-04-11
MacOS|x64|Working[^1]|[<img src="https://xfx.net/ftp/diyokee-releases/dlicon.png">](https://xfx.net/ftp/diyokee-releases/diyokee-osx-x64.zip)|2025-04-11
MacOS|Arm|Not Tested[^1]|[<img src="https://xfx.net/ftp/diyokee-releases/dlicon.png">](https://xfx.net/ftp/diyokee-releases/diyokee-osx-arm64.zip)|2025-04-03

## Acknowledgments

This project wouldn't have been possible without the following:
- [BASS](https://www.un4seen.com/bass.html) audio library
- [AspNetCore.SassCompiler](https://github.com/koenvzeijl/AspNetCore.SassCompiler)
- [BlazorExtensions.Canvas](https://github.com/BlazorExtensions/Canvas)
- [Icons8](https://icons8.com/)
- [Font Awesome](https://fontawesome.com/)

![Alt](https://repobeats.axiom.co/api/embed/c2c1360a9361b0aa67fab23ec95bcf536a4421b4.svg "Repobeats analytics image")

[^1]: File attributes may be lost when running the app under Linux-like systems, including macOS.  
Use `chmod +x` to set the executable bit on the `diyokee-server` binary.
