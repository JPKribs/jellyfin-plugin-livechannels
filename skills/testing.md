# Testing

How to test this plugin, from a single method to the real ffmpeg pipeline. A change is done when the tests for every layer it touches pass.

## The Rule

Every change has a test, and every test proves that given inputs produce the expected output. A bug fix starts with a test that fails without the fix.

## Layers

Test each behavior at the lowest layer that can prove it. Go up a layer only for what the lower one cannot reach.

| Layer | Proves | Runs | Time |
| :--- | :--- | :--- | :--- |
| Unit | One class or function gives the right output for each input | `dotnet test` | Seconds |
| Service | A service with Jellyfin's interfaces stood in, such as the guide, sessions, and timers | `dotnet test` | Seconds |
| ffmpeg integration | A real argument list run through jellyfin-ffmpeg gives the right picture, sound, and timing | `dotnet test`, with jellyfin-ffmpeg found | Minutes |
| Jellyfin image | The whole suite against the jellyfin-ffmpeg of the Jellyfin release the plugin targets | `.github/scripts/test-in-jellyfin.sh` | Minutes |
| Config pages | Each page loads, draws, and saves | By hand on a test server | Minutes |

| A change to | Needs |
| :--- | :--- |
| A utility, model, or schedule math | Unit |
| A service, a scheduled task, or the controller | Service |
| An ffmpeg argument list, a filter chain, a card, or the stream pipeline | ffmpeg integration |
| Anything that depends on the jellyfin-ffmpeg build | Jellyfin image |
| A config page script or its HTML | Config pages |

The repository has no JavaScript test runner. Check every page script with `node --check` on a copy named `.mjs`, and confirm every name it imports from Base exists in `assets/jpkribs_shared.d.ts`, as [review.md](review.md) says.

## Layout

Tests sit flat in `Jellyfin.Plugin.LiveChannels.Tests/`, one file per behavior or subject, named after it.

| Subject | Test |
| :--- | :--- |
| `Utilities/ScheduleCalculator.cs` | `ScheduleCalculatorTests.cs` |
| How Up Next cards render | `BumperRenderTests.cs` |
| Encoders that stall | `StallWatchdogTests.cs` |

* `FfmpegHarness.cs` finds jellyfin-ffmpeg and measures what a real run produces.
* A small fake that only one test file needs, such as `FakeGuideManager`, sits in that file.

## C# Tests

Tests use xUnit with plain `Assert`, and NSubstitute to stand in for Jellyfin's interfaces.

```csharp
[Fact]
public void CurrentProgram_PicksItemAndOffsetWithinCycle()
{
    var programs = ThreeHourLoop();
    var now = Epoch.AddMinutes(90);

    var (index, offset) = ScheduleCalculator.CurrentProgram(programs, now, Epoch);

    Assert.Equal(1, index);
    Assert.Equal(TimeSpan.FromMinutes(30), offset);
}
```

### Naming and Comments

* Name the class `<Subject>Tests` and each method `Method_Scenario_Expectation`, or a sentence of words joined by underscores when it reads better, such as `TornDownSession_RefusesANewConsumer`.
* Give the class a one sentence summary of what its subject must guarantee.
* Give a test a summary when its name does not say what failing would cause.

### Writing a Test

* Arrange, act, and assert, separated by blank lines.
* Assert one behavior per test. Several asserts are fine when they check one outcome.
* Use `[Theory]` with `[InlineData]` for one rule over several inputs.
* Test the edges. Null, empty, the largest value, a cancelled token, a missing file, and an ffmpeg that fails or stalls.
* Never sleep where a clock can be passed in. Pass the time or a `TimeProvider`, and move it from the test.
* A test that fails once in many runs has found a race. Fix the source or the test, and never run it again until it passes.
* A test never reaches the network.
* Write files only under a temp folder the test deletes in `Dispose`.

```bash
dotnet test
```

## ffmpeg Integration Tests

These tests run the plugin's real argument lists through jellyfin-ffmpeg and measure the output, such as its size, scan type, color, audio layout, and timestamps. They use `FfmpegHarness.Instance`.

* The harness looks for `LIVECHANNELS_TEST_FFMPEG`, then `/usr/lib/jellyfin-ffmpeg/ffmpeg`, `/usr/share/jellyfin-ffmpeg/ffmpeg`, and `/Applications/Jellyfin.app/Contents/MacOS/ffmpeg`, with `ffprobe` beside it.
* Only jellyfin-ffmpeg counts, since the production chains use its filters. A plain ffmpeg on the `PATH` is never used.
* A test returns early when no jellyfin-ffmpeg is found, so it passes without checking anything. Set `LIVECHANNELS_REQUIRE_FFMPEG=1` to make a missing ffmpeg fail every one of them instead.

```bash
LIVECHANNELS_REQUIRE_FFMPEG=1 dotnet test
```

```bash
LIVECHANNELS_TEST_FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg dotnet test --filter "FullyQualifiedName~AvSync"
```

* Report whether jellyfin-ffmpeg was found. A run without it proves nothing about the pipeline.
* Keep each clip short, a few seconds, so the suite stays in minutes.

## The Jellyfin Image

`.github/scripts/test-in-jellyfin.sh` runs the whole suite inside the Jellyfin server image, as an unprivileged user, with `LIVECHANNELS_REQUIRE_FFMPEG=1`. CI runs it on every push and pull request to `master`. Docker must be running.

```bash
.github/scripts/test-in-jellyfin.sh jellyfin/jellyfin:12.4
```

Run it before a release, and after any change to a filter chain, an encoder choice, or hardware acceleration.

## Config Pages

1. Build the zip with `./build.sh`.
2. Install it on a Jellyfin 12.4 test server, never on a server people use, and restart it.
3. Open each changed tab under **Dashboard > Plugins > Live Channels**, reload it, and use every control the change touched.
4. Check the page at 375 px wide.

## Checklist

* [ ] Every change has tests, and a bug fix has a test that fails without it.
* [ ] Edges are covered, including null, empty, cancelled, and a failing ffmpeg.
* [ ] `dotnet test` passes, and the report says whether jellyfin-ffmpeg was found.
* [ ] `.github/scripts/test-in-jellyfin.sh` passes for a change to the pipeline, and before a release.
* [ ] Every changed page script passes `node --check`, and its Base imports exist.
* [ ] Every changed page was opened on a test server.
