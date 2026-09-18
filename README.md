# Diyokee
A work in progress, DJ mixing webapp with streaming support, where the UI runs on your browser.

[![Watch the video](https://xfx.net/ftp/diyokee-releases/diyokee-s6.png)](https://xfx.net/ftp/diyokee-releases/diyokee-v2.mkv)
<sup>*You will notice that while loading some tracks the UI freezes. This has been greatly improved in version 2025.11.11</sup>

## Basic usage

The controls are the usual ones. What is worth knowing:

- Load a track by dragging it onto a player or selecting it and clicking <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/eject.svg" width="12">, or with `Shift+Ctrl+A` / `Shift+Ctrl+B`. `Shift+Ctrl+S` opens Settings.
- **Holding `Shift` suppresses beat snapping** wherever it would otherwise apply - setting the cue, jumping, dragging the playhead.
- Faders and knobs take the scroll wheel as well as click and drag.
- Right-click the Eq control for [presets from popular mixing consoles](http:/xfx.net/ftp/diyokee-releases/diyokee-switch-eq-profiles.mp4).
- The <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/down-left-and-up-right-to-center.svg" width="12"> button sets the deck's single cue point; hold <img src="https://raw.githubusercontent.com/morphx666/Diyokee/refs/heads/master/wwwroot/images/readme/forward-step.svg" width="12"> to play from it. The Cue tab keeps as many named cue points as you like.
- Use the Jump and Cue buttons to switch between the Loop/Jump and Cue tabs.
  ![image](https://github.com/user-attachments/assets/0487c141-c905-4c06-971a-43369ebf5403)
- The search box under the file list is recursive and matches file names.
- Drag the synced waveform to scratch. Touch and release behaviour is under Settings > Playback.
- Double-click a track for Track Properties.
  ![image](https://github.com/user-attachments/assets/ca1c7bf1-2785-458c-be03-500d362e8cde)
- In Track Properties, the Grid row edits the track's beat grid:
  - **Edit** - drag any beat line onto the audio it belongs on. Hover a beat and click **+** to pin it as a reference; nothing before a reference can move.
  - **Auto** - measures the track and corrects the grid for you, including the BPM if that was the error. It leaves the track alone when it cannot follow it.
  - **Reset** - undo every grid change.
- MIDI controllers are configured under Settings > MIDI: pick the device, then build a profile for it.
  ![image](https://github.com/user-attachments/assets/7c2dbdbd-50ed-4b91-b639-5a5c2206ab96)

## Notable missing features and known bugs

See [KNOWN-ISSUES.md](KNOWN-ISSUES.md).

## Latest Releases
Platform|Architecture|Status|Download|Release Date
---|---|---|:---:|---
Windows|x64|Working|[<img src="https://xfx.net/ftp/diyokee-releases/dlicon.png">](https://xfx.net/ftp/diyokee-releases/diyokee-win-x64.zip)|2026-09-17
Linux|x64|Working|[<img src="https://xfx.net/ftp/diyokee-releases/dlicon.png">](https://xfx.net/ftp/diyokee-releases/diyokee-linux-x64.zip)|2026-09-17
Linux|Arm64|Working|[<img src="https://xfx.net/ftp/diyokee-releases/dlicon.png">](https://xfx.net/ftp/diyokee-releases/diyokee-linux-arm64.zip)|2026-09-17
MacOS|x64|Working[^1]|[<img src="https://xfx.net/ftp/diyokee-releases/dlicon.png">](https://xfx.net/ftp/diyokee-releases/diyokee-osx-x64.zip)|2026-09-17
MacOS|Arm64|Working[^1]|[<img src="https://xfx.net/ftp/diyokee-releases/dlicon.png">](https://xfx.net/ftp/diyokee-releases/diyokee-osx-arm64.zip)|2026-09-17

## Acknowledgments

This project wouldn't have been possible without the following:
- [BASS](https://www.un4seen.com/bass.html) audio library
- [AspNetCore.SassCompiler](https://github.com/koenvzeijl/AspNetCore.SassCompiler)
- [BlazorExtensions.Canvas](https://github.com/BlazorExtensions/Canvas)
- [Icons8](https://icons8.com/)
- [Font Awesome](https://fontawesome.com/)

![Alt](https://repobeats.axiom.co/api/embed/c2c1360a9361b0aa67fab23ec95bcf536a4421b4.svg "Repobeats analytics image")

[^1]: Before running the program, open a Terminal and change to the directory where you unzipped the file (usually `~/Downloads/diyokee-osx-x64`).  
Next, run the script: `./pre-run.sh`.  
Now, you can launch the app by double-clicking the `Diyokee-server` file in the Finder or by running `./Diyokee-server` from the Terminal.




Is there anyone insterested in this? Hello!? Anyone?
