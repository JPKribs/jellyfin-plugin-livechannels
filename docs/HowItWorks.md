# How It Works

You define **channels** in the plugin configuration. Each channel resolves to an ordered list of items that loops forever on a fixed schedule. The plugin registers directly with Jellyfin's **Live TV**, so the channels, their guide, and their streams are all served by Jellyfin itself. Saving a channel refreshes the guide, so edits show up right away.

The Channels tab picks a channel to edit and groups its options into collapsible sections.

![Channels](screenshots/Channels.png)

* **Content from your library.** A channel pulls from libraries, collections, genres, or hand-picked items, then narrows by ratings, years, studios, people, audio language, and content type. Time of day rating blocks change what may air by the clock, so daytime stays family friendly on its own.
* **A built-in Popular channel.** Channel 0 programs itself from the server's recently played, recently added, and highest rated movies and shows, measured across every user, and stays fresh with rotating picks. Its guide is shared by every user, so it only airs titles every Live TV user may see; see [Popular tab](Settings.md#popular-tab).
* **A full guide.** Every program carries its description, genres, ratings, air dates, and episode numbers, plus every artwork shape the content has, with recently added items flagged as new.
* **Native streaming.** Channels are encoded on demand with Jellyfin's own hardware acceleration (fully GPU-resident on Intel), HDR is tone-mapped to SDR with Jellyfin's own tone-mapping settings, pacing is automatic, and an encoder that dies or stalls mid-watch is replaced in place. Every item leaves in one picture format (standard HD colour, limited range, correct shape and orientation), whatever its source, so colours stay true from a DVD to a phone recording held upright. Optional subtitle burn-in bakes a track into the picture, styled once for every channel.
* **Up Next cards.** Before each item, a 15 second card shows its artwork, title, episode, details and review score, with its parental rating in the corner and a countdown to the start, over a soft chord. The card is part of the item's slot in the guide (a one hour movie airs in a slot of one hour and fifteen seconds), so it never pushes the item later: tune in partway through a card and the screen stays black until the item starts on time. Cards render in the background while the previous item plays, only on channels someone is watching. Each channel can turn them off.
* **Standby that recovers.** A channel that cannot play (its storage is unreachable, or its items keep failing) shows a standby card saying why, then tries its schedule again after a minute, backing off to five minutes while the problem lasts, so it comes back on its own when the problem clears. A network share that stops answering reaches standby within seconds instead of freezing the picture, and an item whose encoder fails partway resumes where it stopped, in software.
* **Recording.** A program or a whole series can be recorded from the guide. Recordings are materialized from the library files into Jellyfin's Live TV recording folders, just like the built-in DVR.
* **Sessions dashboard.** Every active encoder is listed with its live speed and full ffmpeg log, and caps, time limits, and idle cleanup keep CPU and disk bounded on their own.
* **Import and export.** Every channel (filters, appearance, loop behaviour, and logos) moves between servers as one JSON file.

Every option on the **Channels**, **Popular**, **Sessions**, and **Settings** tabs is covered in [Settings](Settings.md), with defaults and valid ranges.
