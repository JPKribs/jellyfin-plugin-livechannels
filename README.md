# ![Live Channels](Jellyfin.Plugin.LiveChannels/Assets/Logo.png)

**A Jellyfin plugin that builds looping virtual TV channels from your own library and presents them natively in Jellyfin's Live TV — no tuner URLs to paste and no exposed endpoints.**

## How It Works

You define **channels** in the plugin's configuration. Each channel resolves to an ordered list of items that loops forever on a fixed wall-clock schedule. The plugin registers directly with Jellyfin's **Live TV** as an in-process provider, so the channels, their guide, and their streams are all served inside the server — there is nothing to add and no URL to expose. The channels simply appear in the Live TV guide and play like any other live channel.

### Channel content

A channel pulls from one or more **library cards** — add a card per library, and the channel plays the union of them all. Each card narrows its library one way, chosen from a **Selection** dropdown:

* **All content** — everything in the library.
* **Genre** — a genre multi-select, with a **"match all"** toggle for AND (e.g. Comedy *and* Animation) versus the default OR (any).
* **Whitelist** / **Blacklist** — an explicit include or exclude list of shows and movies you pick from the library. Picking a show pulls in all of its episodes.

### Channel settings

Per channel you can also set:

* **Logo** — upload an image (it is cropped to a square). With no upload, the plugin generates a 1080×1080 square automatically: a colour derived from the channel name, the channel number centred, and the channel title along the bottom.
* **Maximum age rating** — a ceiling (e.g. `TV-14`); unrated content is always allowed.
* **Episodes per block** — play *N* consecutive episodes of a series before moving on.
* **Keep multipart episodes together** — never split a two part episode (e.g. "… (1)" then "… (2)") across a block boundary.
* **Include specials** — opt season 0 in (off by default).
* **Shuffle** — on by default; the shuffle is fixed and repeatable, so the guide and the live stream always agree. Disable to play everything alphabetically.
* **Episode order** — air order or random within a series.
* **Subtitle burn in** — bake a subtitle track into the video for every viewer: **Never**, **Forced only** (the forced track when present), or **Always** (the forced track, or the default/first track when none is forced). When you join an item already in progress, the subtitle is taken from Jellyfin's cached subtitle extraction so it stays aligned without scanning the whole file.
* **Guide categories** — tag the channel with any of **Movies**, **Sports**, **Kids**, **News** (none by default). The channel's guide entries carry those tags so Jellyfin's Live TV category filters surface it.

A channel always includes whatever its libraries and filters yield — both movies and episodes. Only items with a real media file and a known runtime can be scheduled; anything without is skipped.

### The schedule

A channel's loop is anchored to a fixed epoch, so "what is on now" is a pure function of the clock and the item list. The guide and the live stream compute it independently and arrive at the same answer, with no shared state to drift — and it survives server restarts. Tune into a channel and you join it already in progress, exactly where the guide says it should be.

### Streaming

When a client tunes a channel, the plugin works out which item is airing and how far into it the clock is, seeks there with **ffmpeg** (the one Jellyfin already provides), and re-encodes the channel to one uniform stream that loops until you change the channel — a single continuous feed a player can follow like real Live TV. Jellyfin reads that stream in-process and re-exposes it through its own authenticated Live TV (no endpoint of our own).

Because a linear stream cannot change format mid-play, the channel has **one fixed output resolution** and every item is scaled to fit it (smaller sources upscaled, larger ones downscaled). In **Settings** you choose:

* **Resolution** — 720p / 1080p / 1440p / 4K. Pick your display resolution; higher resolutions cost much more CPU per stream.
* **Video codec** — H.264 (most compatible) or HEVC / H.265 (smaller, fewer clients).
* **Audio codec** — AAC, AC3, or E-AC3.
* **Video bitrate** — the target kbps.
* **Disable hardware acceleration** — force software encoding and decoding. Slower, but works on any system, codec, and media type; turn it on if a channel fails to play with hardware acceleration.

Under the hood the plugin picks one of two pipelines automatically per channel: a **seamless continuous encode** for standard channels (item boundaries have no seam), and a **per-item encode** for channels with 4K sources or subtitle burn-in, which lets each item be hardware-decoded at its own resolution.

Both the **encoder and decoder follow Jellyfin's own hardware acceleration** (Dashboard → Playback): VideoToolbox, NVENC, QSV, VAAPI, or AMF are used for encoding, with hardware **decoding** on the same accelerator where it is available (AMF is encode-only, so it decodes in software). It falls back to software (libx264 / libx265) when hardware isn't configured or can't handle a given source, and the **Disable hardware acceleration** switch forces software everywhere. Real-time transcoding for a 24/7 channel is demanding — **1080p is the practical sweet spot**, and 4K needs capable hardware.

## Setting up Live TV

1. Open **Live Channels** in the plugin settings and create one or more channels.
2. Make sure **Live TV** is enabled in Jellyfin (it appears in the sidebar once a Live TV provider is present — this plugin is one).
3. On the **Settings** tab, click **Refresh guide now** (or wait for Jellyfin's scheduled guide refresh) to pull your channels and their guide.

That's it — there is no tuner to add and no URL to paste. The channels appear under **Live TV** automatically.

### Applying channel changes

Saving a channel automatically runs Jellyfin's built-in **Refresh Guide** task (under *Dashboard → Scheduled Tasks → Live TV*), which re-pulls the channel list and guide so your edits show up in Live TV right away. You can also run it from the Settings tab, or let Jellyfin's own schedule keep things current.

## Security

* **No exposed endpoints** — Channels, guide, and streams are served entirely in-process through Jellyfin's own Live TV. The plugin opens no public HTTP endpoint of its own; clients reach the streams only through Jellyfin's normal, authenticated Live TV playback.
* **Administrators only** — Channels are authored only by administrators, through the plugin configuration.
* **Idle costs nothing** — A channel transcodes on demand only while someone is watching; an unused channel uses no CPU.

## Versioning

Releases use a four-part version, `JJ.JJ.F.B`, that matches the supported Jellyfin version with the plugin's own feature/bug count:

```
10.11.1.0
└───┘ └┬┘
  │    └── 1 = Plugin feature release
  │        0 = Plugin bug/patch release within that feature
  │
  └─── 10.11 = Jellyfin version this build was tested/released for
```

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
