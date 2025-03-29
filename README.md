# Diyokee
A work in progress DJ mixing webapp with streaming support 

![image](https://github.com/user-attachments/assets/435fc47a-e2f8-4267-8253-e6aecf751a54)

## Important notes

- The app uses media providers as the source of music files. Currently, only one media provider is available (for local files and network shares), and it's hardcoded to a folder on my computer.
If you want to run the app on your computer, change the hardcoded path defined in the `MainConsole.razor` page:
  ```csharp
  private IMediaProvider mediaProvider = new MediaProviderLocal("Local", @"Z:\Music");
  ```
- The server for the webapp supports secure connections. You can define the location of your CERT file and its password by creating a `secrets.json` file:
  ```json
  {
    "cert-file": "C:\\path-to-my-cert-file\\cert-file-name.pfx",
    "cert-password": "my-cert-file-password",
  }
  ```
- The audio subsystem is provided by the [BASS library](https://www.un4seen.com/bass.html). If you have a valid license, you can specify your credentials in the `secrets.json` file:
  ```json
  {
    "bassnet-reg-email": "my-registered-email-address",
    "bassnet-reg-key": "my-registration-key",
  }
  ```
- The streaming URL is hardcoded to http://localhost:2132/stream (can be changed in the `SetupBASS()` method in `Program.cs`)
- The streaming encoding is hardcoded to 320kbps (can be changed in the `SetupBASS()` method in `Program.cs`)

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
  At this moment, when clicking the [▶] button, playback will start immediately instead of auto-syncing to the nearest beat marker point.
- No drag & drop support to load files into a player
  Use the [↨] button to load a track
- Searching is quite limited and a bit buggy
- Audio device selection for audio monitoring
- Selecting which tracks should be sent to the monitor (with volume adjustment)
- ...and many more
