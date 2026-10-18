# How It Works

You define **channels** in the plugin configuration. Each channel resolves to an ordered list of items that loops forever on a fixed schedule. The plugin registers directly with Jellyfin's **Live TV**, so the channels, their guide, and their streams are all served by Jellyfin itself. Saving a channel refreshes the guide, so edits show up right away.

* **Content from your library.** A channel pulls from libraries, collections, genres, or hand-picked items, then narrows by ratings, years, studios, people, audio language, and content type. Time of day rating blocks change what may air by the clock, so daytime stays family friendly on its own.
* **A built-in Popular channel.** Channel 0 programs itself from the server's recently played, recently added, and highest rated movies and shows, measured across every user, and stays fresh with rotating picks.
* **A full guide.** Every program carries its description, genres, ratings, air dates, and episode numbers, plus every artwork shape the content has, with recently added items flagged as new.
* **Native streaming.** Channels are encoded on demand with Jellyfin's own hardware acceleration (fully GPU-resident on Intel), HDR is tone-mapped to SDR, pacing is automatic, and an encoder that dies mid-watch is replaced in place. Optional subtitle burn-in bakes a track into the picture, styled once for every channel.
* **Recording.** A program or a whole series can be recorded from the guide. Recordings are materialized from the library files into Jellyfin's Live TV recording folders, just like the built-in DVR.
* **Sessions dashboard.** Every active encoder is listed with its live speed and full ffmpeg log, and caps, time limits, and idle cleanup keep CPU and disk bounded on their own.
* **Import and export.** Every channel (filters, appearance, loop behaviour, and logos) moves between servers as one JSON file.

Every option on the **Channels**, **Popular**, **Sessions**, and **Settings** tabs is covered in [Settings](Settings.md), with defaults and valid ranges.
