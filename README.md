# Diyokee
A work in progress DJ mixing webapp with streaming support 

![image](https://github.com/user-attachments/assets/435fc47a-e2f8-4267-8253-e6aecf751a54)

## App settings
The program uses the settings defined in the `settings.json` file.

```json
{
  "cert-file": "<path to cert file>",
  "cert-password": "<cert file password>",
  "bassnet-reg-email": "<bass registered email>",
  "bassnet-reg-key": "<bass registration key>",
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

- To load a track into a player, click the ⏏ button.
- Click the [▶] button to start playing.
- Click the [↨] button to define a cue point. At this moment, only one cue point can be defined, and it will always snap to the nearest beat marker.
- Click the [CUE] button to jump to the cue point. Leave the button pressed to temporarily play the track from the cue point.
- Click the [⏹] button to stop playback and move to the beginning of the track.
- Click the [➞] button to sync the track to the other player
- Use the 🔊 fader to change the volume.
- Use the 🎶 fader to change the tempo (BPM).
- Use the Lo/Mid/Hi knobs to change the track's equalization.
- Use the fader between the two players to cross-fade between them.
- Faders and knobs can be used by clicking and dragging or by moving the mouse over them and using the scroll wheel.
- You can click and drag over both waveforms (synced and full) to change the playback position.
- Use the textbox at the bottom of the files list to search for files. The search is recursive.
- Click the [⟳] button in the search bar to re-analyze a track. When re-analyzing a track, the program will ignore the BPM from the track's id3 tags and will recalculate it.

## Notable missing features

- Configuration dialog (global settings)
- Track metadata editor (artist, title, genre, downbeat position, replay gain, bpm, etc...)
- Loops
- A fancy screen for remote connections to the stream
- Synced playback  
  At this moment, when clicking the [▶] button, playback will start immediately instead of auto-syncing to the nearest beat marker.  
- You can use the [⇠] and [⇢] buttons under the SYNC section to perform small tempo adjustments.
- No drag & drop support to load files into a player.
  Use the [↨] button to load a track or press A or B on your keyboard.
- Searching is quite limited and a bit buggy.
- Audio device selection for audio monitoring (configuration dialog).
- Selecting which tracks should be sent to the monitor (with volume adjustment)
- ...and many more

## Known issues

- Plenty... but the worst one is that if the window is resized, mouse interactions with the waveform displayed, the faders and the knobs will stop working and the page needs to be refreshed.

---

This project wouldn't have been possible without the following:
- [BASS](https://www.un4seen.com/bass.html) audio library
- [Icons8](https://icons8.com/)
- [AspNetCore.SassCompiler](https://github.com/koenvzeijl/AspNetCore.SassCompiler)
- [BlazorExtensions.Canvas](https://github.com/BlazorExtensions/Canvas)
