# Notable missing features and known bugs

Nothing here is a surprise to us - this is the list we work from. If you hit something that is
not on it, please open an issue.

- Pitch/key adjustments don't seem to work on platforms other than Windows
- Accessing shared folders from Linux/macOS appears to be broken - you can still manually type/paste the folder path, but the file browser is lacking support to detect such sources
- The stream encoder is skipped on Apple Silicon, so there is no stream on those Macs
- A fancy screen for remote connections to the stream
- The settings dialog does not yet support streaming configuration - the encoder's port, mount point and bitrate have to be edited by hand in `settings.json`
- Searching is quite limited and a bit buggy - it matches file names only, so an artist or a title that lives in the track's tags rather than in its name will not be found
- State preservation is only partially implemented and the way it works sucks
- Scratching with a hand on the platter is not yet reliable on every controller - some report the touch on and off again while you are still turning, which can drop the scratch mid-gesture
- Resting your palm on the platter to stop the track dead only lands if the controller happens to report the touch while the platter is already still
- Scratching with the mouse is looser than scratching on a controller - the gesture goes through the browser and the render cycle before it reaches the audio engine
- The MIDI section of the settings dialog is crowded and hard to read
- Only one MIDI controller can be used at a time, and Diyokee holds its input port exclusively while it runs, so nothing else can capture from it
- `settings.json` and `controllers/*.json` are rewritten every few seconds while Diyokee runs, so editing either of them by hand is silently undone - close the app first
- A track that fails to open says nothing on screen; only the log mentions it
- ...and probably many more
