# ![Live Channels](Jellyfin.Plugin.LiveChannels/Assets/Logo.png)

**A Jellyfin plugin that builds looping virtual TV channels from your own library and presents them natively in Jellyfin's Live TV. No tuner URLs to paste and no exposed endpoints.**

## Why Does This Exist

There are a lot of psuedo TV channel programs out there but I've always found them to require a lot of a set up. I wanted a solution where channels were simple to set up and I didn't have to fuss with a whole separate application and Tuner set up in Jellyfin.

## How It Works

You define **channels** in the plugin configuration. Each channel resolves to an ordered list of items that loops forever on a fixed schedule. The plugin registers directly with Jellyfin's **Live TV** from inside the server, so the channels, their guide, and their streams are all served by Jellyfin itself. The channels appear in the Live TV guide and play like any other live channel. Saving a channel runs Jellyfin's **Refresh Guide** task so your edits show up right away.

### Channel content

A channel pulls from one or more **library cards**. Add a card per library and the channel plays the union of them all. Each card narrows its library one way, chosen from a **Selection** dropdown:

* **All content** includes everything in the library.
* **Genre** offers a genre list with a **match all** toggle for AND (for example Comedy and Animation) instead of the default OR (any).
* **Whitelist** and **Blacklist** are explicit include or exclude lists of shows and movies you pick from the library. Picking a show pulls in all of its episodes.

A channel always includes whatever its libraries and filters yield: movies, episodes, and music videos. Only items with a real media file and a known runtime can be scheduled. Anything else is skipped.

### Channel settings

Per channel you can also set:

* **Logo** uploads an image cropped to a square. With no upload the plugin generates a square in a colour from the channel name, showing either the channel number or a Material Symbols icon you name (such as `movie`), with the channel name along the bottom when you want it.
* **Minimum and maximum age rating** keep content within a rating band. Set both to make an adults only or a single band channel.
* **Include unrated** is on by default. Turn it off to drop items that carry no rating.
* **Kids rating** flags programs rated at or below it as Kids in the guide. Movies are flagged as movies automatically.
* **Episodes per block** plays a set number of consecutive episodes of a series before moving on.
* **Keep multipart episodes together** never splits a two part episode across a block boundary.
* **Include specials** opts season 0 in. Off by default.
* **Shuffle** is on by default and is fixed and repeatable, so the guide and the live stream always agree. Disable it to play everything alphabetically.
* **Episode order** plays a series in air order or at random.
* **Favor content type** weights a channel toward movies, shows, or music videos so that type plays more often, at a slight, moderate, or heavy strength. Shuffle must be on.
* **Subtitle burn in** bakes a subtitle track into the video for everyone. Choose **Never**, **Forced only**, or **Always**. Forced only also burns subtitles when the audio is in a known language other than English, preferring an English subtitle, so foreign content stays readable.

### Output

Resolution, video codec, audio codec, and bitrate are set on the **Settings** tab and apply to every channel. Decoding and encoding both follow Jellyfin's own hardware acceleration, with a switch to force software when you want it. HDR sources are tone mapped to SDR so they never play washed out. 1080p is the practical sweet spot for a round the clock channel.

## Versioning

Releases use a four part version, `JJ.JJ.F.B`, that matches the supported Jellyfin version with the plugin's own feature and bug count:

```
10.11.1.0
JJ JJ F B
```

* `10.11` is the Jellyfin version this build was tested and released for.
* `F` is the plugin feature release.
* `B` is the plugin bug or patch release within that feature.

Targets **Jellyfin 10.11.x** (`net9.0`, ABI `10.11.9.0`). Requires ffmpeg, which Jellyfin already bundles and configures.

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
