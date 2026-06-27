# ![Live Channels](Jellyfin.Plugin.LiveChannels/Assets/Logo.png)

**A Jellyfin plugin that builds looping virtual TV channels from your own library and presents them natively in Jellyfin's Live TV. No tuner URLs to paste and no exposed endpoints.**

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

* **Logo** uploads an image cropped to a square. With no upload the plugin generates a square in a colour from the channel name, showing either the channel number or a Material Icons symbol you name (such as `movie`), with the channel name along the bottom when you want it.
* **Minimum and maximum age rating** keep content within a rating band. Set both to make an adults only or a single band channel.
* **Include unrated** is on by default. Turn it off to drop items that carry no rating.
* **Kids rating** flags programs rated at or below it as Kids in the guide. Movies are flagged as movies automatically.
* **Episodes per block** plays a set number of consecutive episodes of a series before moving on.
* **Keep multipart episodes together** never splits a two part episode across a block boundary.
* **Include specials** opts season 0 in. Off by default.
* **Shuffle** is on by default and is fixed and repeatable, so the guide and the live stream always agree. Disable it to play everything alphabetically.
* **Episode order** plays a series in air order or at random.
* **Subtitle burn in** bakes a subtitle track into the video for everyone. Choose **Never**, **Forced only**, or **Always**.

Programs are tagged in the guide automatically: movies as movies, and anything rated at or below the channel's **Kids rating** as Kids, so Live TV's category filters surface the channel.

### Output

Resolution, video codec, audio codec, and bitrate are set on the **Settings** tab and apply to every channel. Encoding follows Jellyfin's own hardware acceleration, with a switch to force software if a channel will not play. 1080p is the practical sweet spot for a round the clock channel.

## Security

* **No exposed endpoints.** Channels, guide, and streams are served entirely inside Jellyfin's own Live TV. The plugin opens no public HTTP endpoint, so clients reach the streams only through Jellyfin's normal authenticated Live TV playback.
* **Administrators only.** Channels are authored only by administrators through the plugin configuration.
* **Idle costs nothing.** A channel transcodes on demand only while someone is watching. An unused channel uses no CPU.

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
