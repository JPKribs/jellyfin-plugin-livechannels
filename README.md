# ![Live Channels](Jellyfin.Plugin.LiveChannels/Assets/Logo.png)

**Looping virtual TV channels built from your own library and served natively in Jellyfin's Live TV.**

## Why Does This Exist

Most pseudo-TV programs run as a separate application that you then wire into Jellyfin as a tuner. Live Channels lives inside the server instead: define a channel, and it appears in Live TV with a full guide, ready to watch.

**While all are welcome to use this plugin, my primary goal for this plugin is to test and develop on top of Jellyfin's Live TV. This is purely for testing and there are many items that are incorrect or broken. It is not advisable to use this in a non-test server. For this reason, this plugin is offered as is, with no guarantee of support, bug fixes, or troubleshooting.**

**I do NOT recommend forking or building on top of this plugin!**

**For a supported & maintained tool that will not brick your server, please use [tunarr](https://github.com/chrisbenincasa/tunarr)**

## How It Works

You define **channels** in the plugin configuration. Each channel resolves to an ordered list of items that loops forever on a fixed schedule. The plugin registers directly with Jellyfin's **Live TV**, so the channels, their guide, and their streams are all served by Jellyfin itself. Saving a channel refreshes the guide, so edits show up right away.

* **Content from your library.** A channel pulls from libraries, collections, genres, or hand-picked items, then narrows by ratings, years, studios, people, audio language, and content type. Time of day rating blocks change what may air by the clock, so daytime stays family friendly on its own.
* **A built-in Popular channel.** Channel 0 programs itself from the server's recently played, recently added, and highest rated movies and shows, measured across every user, and stays fresh with rotating picks.
* **A full guide.** Every program carries its description, genres, ratings, air dates, and episode numbers, plus every artwork shape the content has, with recently added items flagged as new.
* **Native streaming.** Channels are encoded on demand with Jellyfin's own hardware acceleration (fully GPU-resident on Intel), HDR is tone-mapped to SDR, pacing is automatic, and an encoder that dies mid-watch is replaced in place. Optional subtitle burn-in bakes a track into the picture, styled once for every channel.
* **Recording.** A program or a whole series can be recorded from the guide. Recordings are materialized from the library files into Jellyfin's Live TV recording folders, just like the built-in DVR.
* **Sessions dashboard.** Every active encoder is listed with its live speed and full ffmpeg log, and caps, time limits, and idle cleanup keep CPU and disk bounded on their own.
* **Import and export.** Every channel (filters, appearance, loop behaviour, and logos) moves between servers as one JSON file.

## Settings

Every option on the **Channels**, **Popular**, **Sessions**, and **Settings** tabs is documented in **[SETTINGS.md](SETTINGS.md)**, with defaults and valid ranges.

## Versioning

Releases use a four part version, `JJ.JJ.F.B`, that matches the supported Jellyfin version with the plugin's own feature and bug count:

```
10.11.1.0
JJ JJ F B
```

* `JJ.JJ` is the Jellyfin version this build was tested and released for.
* `F` is the plugin feature release.
* `B` is the plugin bug or patch release within that feature.

Targets **Jellyfin 10.11.x** (`net9.0`, ABI `10.11.10.0`). Requires ffmpeg, which Jellyfin already bundles and configures.

## Installation

### Step 1: Add Plugin Repository

* Open Jellyfin and navigate to Dashboard → Plugins → Repositories
* Click Add Repository
* Enter the following repository URL: `https://raw.githubusercontent.com/JPKribs/jellyfin-plugin-livechannels/master/manifest.json`
* Click Save

### Step 2: Install Plugin

* Go to the Catalog tab in the Plugins section
* Find Live Channels in the catalog
* Click Install
* Wait for installation to complete

### Step 3: Restart Jellyfin

* Restart your Jellyfin server completely
* Wait for Jellyfin to fully start up

### Verification Check

* After restart, navigate to Dashboard → Plugins → Live Channels, create a channel, run **Refresh Guide**, then open **Live TV** to confirm the channel appears.

---

## AI Disclaimer

Claude Code was utilized in the initial structure of this project and first drafts of documentation. All code has been manually reviewed, tested, and revised after its generation. This disclaimer exists in the interest of transparency.

**All code was written, or code reviewed and tested, by humans.**
