# ![Live Channels](Jellyfin.Plugin.LiveChannels/Assets/Logo.png)

**Looping virtual TV channels built from your own library and served natively in Jellyfin's Live TV. No separate app, no tuner setup, no URLs to paste.**

## Why Does This Exist

Most pseudo-TV programs run as a separate application that you then wire into Jellyfin as a tuner. Live Channels lives inside the server instead: define a channel, and it appears in Live TV with a full guide, ready to watch.

## How It Works

You define **channels** in the plugin configuration. Each channel resolves to an ordered list of items that loops forever on a fixed schedule. The plugin registers directly with Jellyfin's **Live TV**, so the channels, their guide, and their streams are all served by Jellyfin itself. Saving a channel refreshes the guide, so edits show up right away.

### Channel content

A channel pulls from one or more **sources** and plays the union of them all. A source is either a library or a collection:

* A **library** source narrows a library by **All content**, **Genre** (with a **match all** toggle for AND instead of the default OR), or an explicit **Whitelist** or **Blacklist** of shows and movies. Picking a show pulls in all of its episodes.
* A **collection** source uses every item in a collection, expanding a series to its episodes.

Which item types air is set under **Content Types**: episodes, movies, specials, music videos, and home videos. Every other filter still applies on top of a source, so a collection is narrowed by the channel's ratings, years, studios, and the rest just like a library. Only items with a real media file and a known runtime can be scheduled.

### Import and export

The **Channels** tab exports all of your channels (filters, appearance, loop behaviour, and logos) as a single JSON file, and imports that file on another server. A channel whose number matches an existing one is updated in place, and others are added. Library and genre filters carry over when the target server has libraries with matching names. Hand-picked items keep only what exists on the target.

### The Popular channel

Channel 0 is a built-in **Popular** channel that needs no setup: a de-duplicated mix of the server's recently played, recently added, and highest-rated movies and shows, measured across every user, with rotating seeded picks so it stays fresh. Its **Popular** tab lets you rename it, change its icon, apply rating blocks, choose its content types, set a subtitle rule, tune the loop, or turn it off. Rating limits select the population, so it keeps the top titles that are valid for the cap. Only its number and content sources are fixed.

### Filters

Every filter is optional and they combine. Leave a filter empty or zero to ignore it.

* **Rating blocks** limit which ratings air, optionally by time of day. Each block sets a minimum rating, a maximum rating, and whether unrated content is allowed, either all day or during a custom window that may wrap past midnight. With no blocks, every rating airs. Where two windows overlap, the lowest minimum and lowest maximum win. A block can also tag the channel as **Kids** while it is active.
* **Transition window** keeps content compliant across a change of window: an item that starts within this many minutes of a boundary must satisfy both the current and upcoming windows. Set it to your longest content.
* **Years** limit the channel to production years, for example `1990-1999` or `1985, 1999, 2003`. Episodes use their own year.
* **Minimum community rating** (0 to 10) and **minimum critic rating** (0 to 100) keep only higher rated content.
* **Studios** match one or more studios or networks. For shows this also matches the series' studio.
* **People** match content featuring chosen actors or directors.
* **Audio language** includes only content whose default audio track is the chosen language.

### Channel settings

* **Logo** uploads an image cropped to a square, or the plugin generates one from the channel name, number, or a Material Symbols icon.
* **Category** tags the whole channel in the guide as **News** or **Sports**. Kids is set per rating block, and a program is tagged as a movie automatically while a movie is playing, so a channel can carry up to three tags at once.
* **Content Types** choose what airs: episodes, movies, specials, music videos, and home videos.
* **Episodes per block** plays a run of consecutive episodes before moving on.
* **Keep multipart episodes together** holds a two part episode in the same block.
* **Loop order** arranges the channel. **Shuffle** is fixed and repeatable, so the guide and the stream always agree, and each series contributes one block per loop before waiting for every other series so nothing dominates. **Alphabetical** plays by name, and **Chronological** plays oldest to newest by release date.
* **Episode order** plays a series in air order or at random.
* **Favor content type** weights a shuffled channel toward movies, shows, or music videos.
* **Subtitle burn in** bakes a subtitle track into the video: **Never**, **Forced only**, or **Always**. Forced only switches to Always behaviour when the audio is not your **Default language**, so foreign content stays readable.

### Output

Resolution, codecs, and bitrate are set on the **Settings** tab and apply to every channel. Encoding and decoding follow Jellyfin's own hardware-acceleration settings (with a switch to force software), and HDR is tone-mapped to SDR using Jellyfin's VPP brightness gain where available. On Linux with Intel hardware (QSV or VA-API), the whole pipeline (decode, deinterlacing, scaling, tone mapping, subtitle burn-in, and encode) runs on the GPU, bound to the render node from Jellyfin's transcoding settings. Stream pacing is automatic: the encoder runs at realtime, builds a head start at tune-in, and bursts to recover time lost at item transitions, so there is nothing to tune.

A **Sessions** section bounds cost: a **maximum concurrent streams** cap closes the oldest stream when a new viewer would exceed it, and an optional **stream time limit** closes anything open too long (the client simply re-tunes). Each active channel keeps a short rolling window of segments on disk, so disk use stays bounded regardless of watch time. A **Stress test** on the same tab measures the right cap: it encodes a demanding item with the real channel pipeline, adding one concurrent stream per round until one drops below realtime. Nothing about the test is saved.

### The Sessions tab

Lists every channel currently encoding: logo, number and name, start time, runtime, and live encode speed (1.0x means the server is keeping up). Selecting a session opens its ffmpeg log with **Refresh**, **Copy**, and **Kill**. Kill stops the stream and frees its encoder immediately.

### Guide

Every program is filled out from your library: description, genres, ratings, year, air date, and season or episode numbers, with recently added content flagged as new and programs tagged by their category (kids, movie, news, or sports). Artwork is always landscape, so the guide stays tidy.

## Versioning

Releases use a four part version, `JJ.JJ.F.B`, that matches the supported Jellyfin version with the plugin's own feature and bug count:

```
10.11.1.0
JJ JJ F B
```

* `JJ.JJ` is the Jellyfin version this build was tested and released for.
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
