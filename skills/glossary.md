# Glossary

The agreed name for every concept in this plugin. The UI, the docs, messages, and code use these words and no others.

* Use the **Term** in the UI, the docs, and messages.
* The **In Code** column names the type or member that holds it, when the code name differs.
* Never use a word from the **Not** column for the concept.
* Add a term here before it appears anywhere else. Rename it here first when it changes, then everywhere in the same commit.

## Channels

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Channel | A looping virtual TV channel built from the library and served in Live TV | `Channel` | Station, stream, feed |
| Channel number | A channel's place in the guide. 0 is the Popular Channel | `Channel.Number` | Position, slot |
| Enabled, Disabled | Whether a channel is served in Live TV. A disabled channel can be a draft | `Channel.Enabled` | Active, hidden |
| Popular Channel | The built-in channel 0 that programs itself from what the server watches and adds | `PluginConfiguration.PopularChannel`, `ChannelService.Popular` | Trending channel, default channel |
| Logo | The channel's image in the guide, uploaded or generated | `LogoData`, `DefaultLogoService` | Icon, artwork |
| Generated Logo Style | How a channel with no upload draws its logo, as **Number** or **Symbol** | `LogoStyle` | Logo type |
| Symbol | A Material Symbols name drawn as a generated logo | `LogoSymbol` | Glyph, emoji |
| Import, Export | Moving every channel between servers as one JSON file | `livechannels-export` | Backup, restore |

## Content

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Source | One place a channel takes items from, a **Library** or a **Collection** | `LibrarySource`, `SourceKind` | Feed, input |
| Library source | A source that narrows one library with a Selection | `SourceKind.Library` | Library mapping |
| Collection source | A source that takes every item in one Jellyfin collection | `SourceKind.Collection` | Box set source, playlist |
| Selection | How a library source narrows its library | `SelectionMode` | Mode, filter type |
| All content | Takes everything in the library | `SelectionMode.AllContent` | Everything |
| Genre | Takes items carrying the included genres, less the excluded ones | `SelectionMode.Genre` | Tag |
| Whitelist | Takes only the picked shows, movies, and episodes | `SelectionMode.Whitelist` | Allow list, include list |
| Blacklist | Takes everything except the picked items | `SelectionMode.Blacklist` | Block list, exclude list |
| Filters | The channel wide limits on ratings, years, scores, studios, and people | `ChannelService.Filters` | Rules, criteria |
| Content types | Which kinds of item air, such as **Episodes** and **Movies** | `Include*` | Media types |
| Audio language | The audio track a channel requires and plays | `Channel.AudioLanguage` | Dub, track language |
| Default language | The server wide language for audio and subtitles | `DefaultSubtitleLanguage` | Native language |

## Ratings and Placeholders

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Rating block | A limit on which age ratings air, all day or in a window of the day | `RatingBlock` | Rating rule, daypart |
| Age Ratings | The list of rating blocks on a channel, beside its Placeholder Blocks | `Channel.RatingBlocks` | Parental controls |
| Period | When a block applies, **All day** or **Custom** | `RatingBlockPeriod` | Schedule, window type |
| Transition window | How close to a window change an item must fit both windows | `TransitionWindowMinutes` | Grace period, buffer |
| Placeholder block | A window of the day when the channel is off air and loops a card | `RatingBlock.IsPlaceholder` | Off air block, blackout |
| Placeholder card | The 10 second card a placeholder block loops, with its title and message | `PlaceholderService` | Slate, filler |
| Style | A placeholder card's look, such as **Ocean** or **Custom** | `PlaceholderStyle` | Theme, background |
| Guide name | What the guide calls a placeholder block's window | `PlaceholderName` | Program name |

## The Loop and the Guide

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Loop | The ordered list of items a channel plays forever | `ProgramLoop`, `ProgramLoopBuilder` | Playlist, queue |
| Loop order | How the loop is arranged, **Shuffle**, **Alphabetical**, or **Chronological** | `LoopMode` | Sort, order mode |
| Episodes per block | How many episodes of one series play in a row | `EpisodesPerBlock` | Run length, chunk |
| Favor content type | Weights one kind of item more in a shuffled loop | `FavorKind` | Bias, boost |
| Favor strength | How strong that weighting is | `FavorStrength` | Weight |
| Schedule | When each loop item airs, kept so the guide and the stream agree | `StreamSchedule`, `ChannelService.Schedule` | Timeline, EPG data |
| Splice anchor | The point a rebuilt schedule continues from, so the program on air keeps its slot. Used in code, not the UI | Splice anchor | Resume point |
| Guide | Jellyfin's Live TV program guide | `ChannelService.Guide` | EPG, listings |
| Reset Schedule | Rebuilds every schedule, the guide, and the buffer cards | `POST livechannels/schedule/reset` | Clear cache, wipe |

## Cards

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Up Next card | The 10 second card before each item, with its artwork, title, and what airs after | `BumperService` | Bumper, interstitial |
| Tune-in card | The countdown card shown while a stream starts | `IntroService` | Intro, splash |
| Standby card | The card a channel that cannot play shows, saying why | Standby | Error screen, offline card |
| Buffer cards | The tune-in card and what every Up Next card shares, rendered ahead of time | `GenerateBufferCardsTask` | Pre-renders, assets |
| Generate Buffer Cards | The scheduled task that renders buffer cards | `GenerateBufferCardsTask` | Card task |

## Streams and Sessions

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Session | One channel encoding on the server, shared by all its viewers | `ActiveSession`, `StreamSessionService` | Viewer, connection |
| Encoder | The ffmpeg process that makes a session's stream | Producer | Transcoder, worker |
| Speed | How fast a session encodes. 1.0x keeps up with realtime | `ActiveSession.Speed` | FPS, rate |
| Kill | Stops a session at once, even with viewers | `DELETE livechannels/sessions/{id}` | End, terminate |
| Stream file location | Where streams and rebuildable files are written | `StreamDirectory`, `PluginPaths` | Cache folder, temp folder |
| Maximum concurrent streams | The most channels that may encode at once | `MaxConcurrentSessions` | Session cap, max sessions |
| Stream time limit | Closes a stream open longer than this once nobody watches | `SessionTimeoutMinutes` | Timeout, idle limit |
| Stress test | Measures how many streams the server can encode at once | `StressTestService` | Benchmark, load test |
| Failure report | The log file written when an encoder fails | `FailureReports` | Crash log, dump |
| Subtitle burn in | Bakes a subtitle track into the picture, **Never**, **Forced only**, or **Always** | `SubtitleBurnInMode` | Hardcode, embed |
| Hardware acceleration | The accelerator from Jellyfin's Playback settings that encoding follows | `EncoderResolver` | GPU mode, HWA |

## Recording

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Recording | A program's library file placed in Jellyfin's recording folders when its air window ends | `RecordingService` | DVR file, capture |
| Timer | A program or series set to record from the guide | `TimerService` | Schedule, reminder |
