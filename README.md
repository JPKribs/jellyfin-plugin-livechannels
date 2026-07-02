# ![Live Channels](Jellyfin.Plugin.LiveChannels/Assets/Logo.png)

**A Jellyfin plugin that builds looping virtual TV channels from your own library and presents them natively in Jellyfin's Live TV. It ships with a ready to watch Popular channel, fills out the guide with real metadata and landscape artwork, and has no tuner URLs to paste and no exposed endpoints.**

## Why Does This Exist

There are a lot of psuedo TV channel programs out there but I've always found them to require a lot of a set up. I wanted a solution where channels were simple to set up and I didn't have to fuss with a whole separate application and Tuner set up in Jellyfin.

## How It Works

You define **channels** in the plugin configuration. Each channel resolves to an ordered list of items that loops forever on a fixed schedule. The plugin registers directly with Jellyfin's **Live TV** from inside the server, so the channels, their guide, and their streams are all served by Jellyfin itself. The channels appear in the Live TV guide and play like any other live channel. Saving a channel runs Jellyfin's **Refresh Guide** task so your edits show up right away.

### Channel content

A channel pulls from one or more **library cards**. Add a card per library and the channel plays the union of them all. Each card narrows its library one way, chosen from a **Selection** dropdown:

* **All content** includes everything in the library.
* **Genre** offers a genre list with a **match all** toggle for AND (for example Comedy and Animation) instead of the default OR (any).
* **Whitelist** and **Blacklist** are explicit include or exclude lists of shows and movies you pick from the library. Picking a show pulls in all of its episodes.

A channel always includes whatever its libraries and filters yield: movies, episodes, and music videos. Turn on **Include home videos** to also pull in loose video files, such as those in a Home Videos library. Only items with a real media file and a known runtime can be scheduled. Anything else is skipped.

### Import and export

The **Channels** tab has **Export** and **Import** buttons. Export downloads all of your channels — every filter, the appearance, the loop behaviour, and the logo — as a single JSON file. Import reads that file on another server and merges it in: a channel whose number matches an existing one is updated in place, and any others are added, so you can copy a setup between servers. Channels built from libraries, genres, or ratings carry over as long as the target server has libraries with matching names; a channel that pins individual hand-picked items only keeps the items that exist on the target.

### The Popular channel

Out of the box the plugin serves a **Popular** channel on channel 0, with no setup at all. It loops a mix of your **recently played**, **recently added**, and **highest rated** movies and shows. Recently played is measured server wide by play date across every user, so whatever the server has been watching lately rises to the top; the popular shows are the series the most-recently-played episodes belong to (found by walking down the recently-played episodes until enough distinct series are collected). It aims for 25 movies (15 recently played, 5 recently added, 5 picked at random from your top community rated) and 10 shows (6 recently played, 2 whose episodes were just added, 2 picked at random from your top community rated), de-duplicated so a title that qualifies twice is counted once, and simply returns fewer when a source is thin. The random highly-rated picks are seeded and rotate over time, so the channel stays fresh. Each chosen show contributes a single block per loop (see **Shuffle** below), so you see all ten shows rather than one big series filling the channel. Its own **Popular** tab lets you rename it, change its icon, cap the rating, set a subtitle rule, and tune the loop, or turn it off entirely. Only its number (always 0) and its content are fixed.

### Filters

A **Filters** section narrows what a channel includes. Every filter is optional and they combine, so you can stack them: an HBO comedy from the 90s rated 7.5 and up is just a handful of filters on one channel. Leave a filter empty or zero to ignore it.

* **Minimum and maximum age rating** keep content within a rating band. Set both to make an adults only or a single band channel.
* **Include unrated** is on by default. Turn it off to drop items that carry no rating.
* **Years** limit the channel to certain production years, like a 90s channel. Enter years and ranges separated by commas, for example `1990-1999` or `1985, 1999, 2003`. For shows this uses each episode's own year, so a long running series contributes only its episodes from those years.
* **Minimum community rating** (a 0 to 10 audience score) and **minimum critic rating** (a 0 to 100 critic score) keep only higher rated content, for a best of channel. Content carrying no such rating is dropped.
* **Studios** limit the channel to one or more studios or networks, like an HBO channel. For shows this also matches the series' studio. Search by name to add.
* **People** limit the channel to content featuring chosen actors or directors. Search by name to add.
* **Audio language** includes only content whose default audio track is the chosen language.

### Channel settings

Per channel you can also set:

* **Logo** uploads an image cropped to a square. With no upload the plugin generates a square in a colour from the channel name, showing either the channel number or a Material Symbols icon you name (such as `movie`), with the channel name along the bottom when you want it.
* **Kids rating** flags programs rated at or below it as Kids in the guide.
* **Episodes per block** plays a set number of consecutive episodes of a series before moving on.
* **Keep multipart episodes together** holds a two-part episode in the same block, extending the block by at most one episode to do so. A three-parter keeps only its first pair together; the third part falls into a later block.
* **Include specials** opts season 0 in. Off by default.
* **Include home videos** enables Home and unassigned videos. Off by default, so existing channels are unchanged.
* **Shuffle** is on by default and is fixed and repeatable, so the guide and the live stream always agree. Each series contributes **one block per loop** and then steps aside until every other series has aired, so a giant series gets the same footing as a small one and nothing dominates. Which block a series contributes advances each guide refresh, so the channel works through the series over time. Disable shuffle to play everything alphabetically.
* **Episode order** plays a series in air order or at random.
* **Favor content type** weights a channel toward movies, shows, or music videos so that type plays more often, at a slight, moderate, or heavy strength. Shuffle must be on.
* **Subtitle burn in** bakes a subtitle track into the video for everyone. Choose **Never**, **Forced only**, or **Always**. Forced only burns only the forced track, but switches to Always behaviour when the audio is in a language other than your **Default language** (set on the Settings tab), so foreign content stays readable.

### Output

Resolution, video codec, audio codec, and bitrate are set on the **Settings** tab and apply to every channel. The same tab, organised into collapsible sections, also holds where stream files are written and your **Default language** (the language used to decide Forced subtitle burn in). Decoding and encoding both follow Jellyfin's own hardware acceleration, with a switch to force software when you want it. HDR sources are tone mapped to SDR so they never play washed out. 1080p is the practical sweet spot for a round the clock channel. Stream pacing is fully automatic: the encoder runs at exactly realtime, builds a head start at tune-in, and bursts to recover any time lost at item transitions, so there is nothing to tune and streams neither drift ahead nor fall behind. On Linux with Intel hardware (QSV or VA-API), the entire pipeline — decode, deinterlacing, scaling, HDR tone mapping, and encode — runs on the GPU, bound to the render node configured in Jellyfin's own transcoding settings, so 4K and HDR sources encode several times faster than realtime even on low-power hardware.

A **Sessions** section bounds how much a channel can cost the server. The **maximum concurrent streams** cap limits how many channels encode at once, closing the oldest stream when a new viewer would exceed it, so a client that quits without telling the server cannot leave encoders piling up. An optional **stream time limit** is a blunter backstop that closes any stream open longer than you allow, after which the client simply re-tunes. Each active channel keeps a fixed five-minute rolling window of segments on disk (roughly five minutes at the configured bitrate), so disk use stays bounded no matter how long a channel runs. The limits default to sensible values and can be turned off.

Not sure what to set the stream cap to? A **Stress test** section on the Settings tab measures it: pick a demanding movie or episode (4K or HDR gives the honest number) and the server encodes it with the real channel pipeline, adding one concurrent stream per round until one can no longer hold realtime. The result is the number to use. Nothing about the test is saved — run it once, ad hoc, while no channels are streaming.

### The Sessions tab

The **Sessions** tab lists every channel currently encoding, refreshed as you watch. Each one shows its logo, channel number and name, when it started, how long it has run in hours and minutes, and how fast it is transcoding, where 1.0x means the box keeps up with realtime and lower means it is falling behind. A **Kill** button stops a stream and frees its encoder right away, which is handy when a client has wandered off and left one running.

### Guide

Every program in the guide is filled out from your library: a description, genres, the official and community ratings, the production year and original air date, and season and episode numbers. Episodes show the series as the title with the episode name beside it, and recently added content is flagged as new. Artwork is always landscape so the guide stays tidy, with a movie showing its backdrop while episodes and music videos show their primary thumbnail.

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
