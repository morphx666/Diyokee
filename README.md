# Diyokee
A work in progress, DJ mixing webapp with streaming support, where the UI runs on your browser.

[![Watch the video](https://xfx.net/ftp/diyokee-releases/diyokee-s5.png)](https://xfx.net/ftp/diyokee-releases/diyokee-v2.mkv)
<sup>*You will notice that while loading some tracks the UI freezes. This has been greatly improved in version 2025.11.11</sup>

## App settings

To access the settings dialog, press the `Ctrl+Alt+S` key on your keyboard.  
Some settings cannot be yet configured by the Settings dialog, but you can edit the `settings.json` file manually.

## Basic usage

- To load a track into a player, drag & drop the track or select the track and click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/eject.svg" width="12"> button.
  You can also use `Ctrl+Alt+A` or `Ctrl+Alt+B` keys on your keyboard to load a track into the A or B player, respectively.
- Click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/play.svg" width="12"> button to start playing.
- Click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/stop.svg" width="12"> button to stop playback and move to the beginning of the track.
- Click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/down-left-and-up-right-to-center.svg" width="12"> button to define a cue point. At this moment, only one cue point can be defined, and it will always snap to the nearest beat marker.
- Click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/forward-step.svg" width="12"> button to jump to the cue point. Leave the button pressed to temporarily play the track from the cue point.
- Click the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/arrow-right-from-bracket.svg" width="12"> button to sync the track to the other player
- Use the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/volume-high.svg" width="12"> fader to change the volume.
- Use the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/music.svg" width="12"> fader to change the tempo (BPM).
- Use the Hi/Mid/Low knobs to change the track's equalization.  
  Right-click over the Eq control to [display a menu with several presets](http:/xfx.net/ftp/diyokee-releases/diyokee-switch-eq-profiles.mp4) from popular mixing consoles.
- Use the fader between the two players to cross-fade between them.
- Faders and knobs can be used by clicking and dragging or by moving the mouse over them and using the scroll wheel.
- Use the <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/left-long.svg" width="12"> and <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/right-long.svg" width="12"> buttons under the SYNC section to perform small tempo adjustments.
- You can click and drag over both waveforms (synced and full) to change the playback position.
- Search for files in the textbox at the bottom of the files list. The search is recursive.
- Double-click a track in the files list to open the Track Properties dialog.
  ![image](https://github.com/user-attachments/assets/ca1c7bf1-2785-458c-be03-500d362e8cde)
- Use the Jump and Cue buttons to switch between Loop/Jump and Cue tabs.
![image](https://github.com/user-attachments/assets/0487c141-c905-4c06-971a-43369ebf5403)

## Notable missing features

- A fancy screen for remote connections to the stream
- Searching is quite limited and a bit buggy
- Audio routing is partially implemented but not fully usable
- Key recognition/matching/adjustments are not yet supported
- State preservation is only partially implemented
- ...and many more

## Common issues
- If you run the program for the first time you may receive BASS-related errors.  
Just restart the server and try again. It may take several attempts.  
This is supposedly fixed in version 2025.11.10
- ~~The mouse handling is horrendous and sometimes controls may stop responding to mouse events.~~  
~~Resize the browser window to force a full refresh.~~  
It's still pretty bad, but hopefully improved in version 2025.11.11

## Nightly Releases

Platform|Architecture|Status|Download|Release Date
---|---|---|:---:|---
Windows|x64|Working|[<img src="https://xfx.net/ftp/diyokee-releases/dlicon.png">](https://xfx.net/ftp/diyokee-releases/diyokee-win-x64.zip)|2025-11-19
Linux|x64|Working[^1]|[<img src="https://xfx.net/ftp/diyokee-releases/dlicon.png">](https://xfx.net/ftp/diyokee-releases/diyokee-linux-x64.zip)|2025-11-19
Linux|Arm|Working[^1]|[<img src="https://xfx.net/ftp/diyokee-releases/dlicon.png">](https://xfx.net/ftp/diyokee-releases/diyokee-linux-arm64.zip)|2025-11-19
MacOS|x64|Working[^2]|[<img src="https://xfx.net/ftp/diyokee-releases/dlicon.png">](https://xfx.net/ftp/diyokee-releases/diyokee-osx-x64.zip)|2025-11-19
MacOS|Arm|Not Tested|[<img src="https://xfx.net/ftp/diyokee-releases/dlicon.png">](https://xfx.net/ftp/diyokee-releases/diyokee-osx-arm64.zip)|2025-11-19

## Acknowledgments

This project wouldn't have been possible without the following:
- [BASS](https://www.un4seen.com/bass.html) audio library
- [AspNetCore.SassCompiler](https://github.com/koenvzeijl/AspNetCore.SassCompiler)
- [BlazorExtensions.Canvas](https://github.com/BlazorExtensions/Canvas)
- [Icons8](https://icons8.com/)
- [Font Awesome](https://fontawesome.com/)

![Alt](https://repobeats.axiom.co/api/embed/c2c1360a9361b0aa67fab23ec95bcf536a4421b4.svg "Repobeats analytics image")

[^1]: File attributes may be lost when unzipping the app under Linux-like systems, including macOS.  
Use `chmod +x` to set the executable bit on the `diyokee-server` binary.

[^2]: Before running the program, open a Terminal and change to the directory where you unzipped the file (usually `~/Downloads/diyokee-osx-x64`).  
Next, set the executable attribute on the `pre-run.sh` file: `chmod +x pre-run.sh`.  
Then, run the script: `./pre-run.sh`.  
Now, you can launch the app by double-clicking the `Diyokee-server` file in the Finder or by running `./Diyokee-server` in the Terminal.
