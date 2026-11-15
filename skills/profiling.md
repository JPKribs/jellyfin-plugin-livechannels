# Profiling

How to find and fix slow C# in this plugin. It covers CPU, memory, allocations, locks, library queries, and the ffmpeg processes the plugin starts. Every other language is out of scope.

## When to Profile

Profile only when there is a reason. Never profile to look for something to improve.

Start when one of these is true.

* A user or a test reports that something is slow, uses too much memory, or starves playback.
* A change adds or alters a loop or a library query on a path that runs for every item or every channel, such as building a loop, filtering a source, a guide refresh, or **Generate Buffer Cards**.
* A change touches the stream path, such as `SegmentConcatStream`, the encoder watchdogs, or session start, where a delay is a stall a viewer sees.
* A number in a test run or a task run grew from one release to the next.

Without one of these, do not profile. Code that has no symptom and no change is not a problem.

## The Tiers

Profiling has three tiers. Start at Tier 1. Go up a tier only when the one below could not answer the question. Most issues end at Tier 1 or Tier 2.

| Tier | Answers | Budget | Needs a test server |
| :--- | :--- | :--- | :--- |
| 1. Time One Method | Is this code slow by itself | 10 minutes | No |
| 2. Trace One Task | Which code dominates one slow task, route, or tune-in | 30 minutes | Yes |
| 3. Go Deeper | Why memory grows, or what a second path costs | Ask first | Yes |

* **Tier 1** is a temporary timing test in the test project. It needs no server and no library.
* **Tier 2** is one CPU trace and counters of one task or route on a Jellyfin 12.4 test server. Pick the one path the issue points at, with a test library built once by the [recipe](#the-test-library).
* **Tier 3** is a heap dump, a larger library, or a second path. Stop and ask before you start it.

### The Budget

* Each tier has a time budget. When it runs out, stop and report what you have, even when it is inconclusive.
* Profile one path per issue. Never sweep every path "to be sure".
* Stop as soon as the goal is met or the cause is proven.
* Write the tier, the path, and the stop reason in the report.

## The Rule

Measure first, change second, measure again. A change made for speed is not done until a number shows it is faster and the tests still pass.

* Never optimize from a guess. A hot path you cannot show is not a hot path.
* Never trade correctness, a safety limit, or a [security](code.md#security) rule for speed.
* Keep the simplest code that meets the goal. Stop when the goal is met.

## Goals

State the goal before you measure. A goal names the operation, the data size, and the limit.

| Good | Bad |
| :--- | :--- |
| A guide refresh of 20 channels over 10,000 items finishes in under 30 seconds | Make the guide faster |
| A tune-in shows its first frame in under 3 seconds with 3 streams running | Make streams start faster |

The plugin runs inside Jellyfin on the user's server. Treat its CPU, memory, and disk as shared. A task that is fast but starves playback has failed.

## Check These First

These causes are common. Check each against the code before you measure anything.

| Check | Usual cause |
| :--- | :--- |
| A loop that asks Jellyfin the same question for every item or every channel | The answer does not change inside a run, so ask once per run |
| A library query that asks for fields nothing reads | The result is larger than the work needs |
| A schedule rebuilt when nothing it depends on changed | The schedule cache key misses an input, or a save clears it |
| An ffmpeg run started again for a card or logo already on disk | The file name does not follow its content, or the check runs after the render |
| A log string built in a hot loop | The string is built even when the level is off |

If the code shows the cause, a Tier 1 test is enough to prove it.

## The Test Library

Tier 2 needs data that is big enough to show the problem and no bigger.

* Use a test server, never a server people use, with one library of a few hundred short clips, then scan it once.
* Build only the channels the path needs, such as one channel per Selection when the issue is in the filters.
* Write down the number of items and channels in the report.
* Never build a bigger library to prove a scaling claim. Ask first, at Tier 3.

## Tools

Use only the tools Microsoft ships for .NET. They need the .NET 10 SDK, the same as the repo, so check it first.

```bash
dotnet --list-sdks
```

Install the tools once. Update them with `dotnet tool update --global` and the same name.

```bash
dotnet tool install --global dotnet-trace
```

```bash
dotnet tool install --global dotnet-counters
```

```bash
dotnet tool install --global dotnet-gcdump
```

The tools install to `~/.dotnet/tools`. On macOS that folder can be missing from `PATH`, because the dotnet installer writes the literal text `~/.dotnet/tools` to `/etc/paths.d`, and a shell does not expand it. Add this line to `~/.zshrc` and open a new shell.

```bash
export PATH="$PATH:$HOME/.dotnet/tools"
```

Check that each tool runs.

```bash
dotnet-trace --version
```

| Question | Tool |
| :--- | :--- |
| Which methods use the CPU | `dotnet-trace` |
| Is the process healthy over time | `dotnet-counters` |
| What holds the memory | `dotnet-gcdump` |
| How fast is this one method | A temporary test with `Stopwatch` and `GC.GetAllocatedBytesForCurrentThread()` |
| How long did this run take | A `Stopwatch` in a temporary test, or `dotnet test` timing |
| Which ffmpeg run is slow | The command and timing in the session's ffmpeg log, or a `FfmpegHarness` test |

A trace is a `.nettrace` file, which `dotnet-trace` writes by default. Open it in PerfView on Windows, or turn it into a speedscope file and open that at [speedscope.app](https://www.speedscope.app). Pass `--format Speedscope` to `collect` to get both files in one step, or convert a trace later.

```bash
dotnet-trace convert run.nettrace --format Speedscope -o run
```

This writes `run.speedscope.json`. Keep every trace, dump, and test output outside the repository.

## Tier 1: Time One Method

A method with no I/O is best measured alone.

* Write a temporary xUnit test in the test project that times the method with `Stopwatch` and reads `GC.GetAllocatedBytesForCurrentThread()` before and after. Take the difference on the same thread, since the counter covers only the current thread. Write the numbers with `ITestOutputHelper`, and read them with `--logger "console;verbosity=detailed"`. Never commit the test.
* Build and run in `Release`, with `dotnet test -c Release`, and without a debugger.
* Warm up first. Call the method a few times before you measure, so the JIT is not in the number.
* Run it many times and take the median. One run proves nothing.
* Run it before and after the change, on the same machine, with nothing else busy.
* Give it realistic input. A loop of three items hides what a loop of 10,000 shows.
* Never commit the test, its output, or any trace.

To see where the time goes inside the method, trace the test host. Make the test loop for at least 10 seconds, so a trace has time to attach. Do not use `dotnet-trace collect -- dotnet test`. It traces the `dotnet test` command and not the test code, and it can hang. Run the tests in one shell and attach from another.

```bash
dotnet test -c Release --filter FullyQualifiedName~MyTimingTest
```

Find the test host process id, which is the newest `testhost` process.

```bash
pgrep -f testhost
```

```bash
dotnet-trace collect --process-id <pid> --duration 00:00:08 --format Speedscope -o ~/Desktop/method.nettrace
```

For what holds the memory, take a heap dump of the same process, and read it.

```bash
dotnet-gcdump collect --process-id <pid> -o ~/Desktop/method.gcdump
```

```bash
dotnet-gcdump report ~/Desktop/method.gcdump
```

## Tier 2: Trace One Task

The plugin runs in Jellyfin's process, so attach to that process on the test server. Trace one task, one route, or one tune-in, the one the issue points at.

1. Install the build from `./build.sh` on the test server, restart it, and build the test library and channels.
2. Find Jellyfin's process id with `dotnet-trace ps`. When the server runs in a container, run the Linux build of the tool inside the container, from the same `/tmp` as Jellyfin, since a tool on the host cannot reach the process.
3. Record a CPU trace while the slow run goes. Use the `dotnet-sampled-thread-time` profile, since the default one adds JIT and loader events and grows large.

    ```bash
    dotnet-trace collect --process-id <pid> --profile dotnet-sampled-thread-time --duration 00:01:00 -o ~/Desktop/run.nettrace
    ```

4. Start the slow work while the trace runs, such as **Refresh Guide**, **Generate Buffer Cards**, a channel save, or tuning in to a channel.
5. Convert the trace and read it in speedscope. Every thread is sampled, so an idle thread shows as `UNMANAGED_CODE_TIME`. Time spent waiting on ffmpeg also shows there, so credit it to the nearest plugin frame above it.

    ```bash
    dotnet-trace convert ~/Desktop/run.nettrace --format Speedscope -o ~/Desktop/run
    ```

6. For counters, record while the run goes, and read allocation, collections, thread pool queue length, and lock contentions.

    ```bash
    dotnet-counters collect --process-id <pid> --counters System.Runtime --format json --duration 00:01:00 -o ~/Desktop/counters
    ```

7. For what holds the memory, take a heap dump in the middle of the run and again after it, and read both.

    ```bash
    dotnet-gcdump collect --process-id <pid> -o ~/Desktop/mid.gcdump
    ```

8. For an ffmpeg run, read its command and its exit summary in the session's log on the **Sessions** tab, or in the failure report in Jellyfin's log folder. Time the same argument list in a `FfmpegHarness` test to compare a change.

## Tier 3: Go Deeper

Stop and ask before you start Tier 3. Say what Tier 2 left unanswered and what you will do.

* Take a heap dump in the middle of the run and after it, per the memory step of Tier 2.
* Grow the test library to the size the issue needs.
* Trace a second path only when the first trace points at it.

## Reading a Trace

* **Inclusive time** is a method plus everything it calls. **Exclusive time** is the method alone. Start from the largest inclusive plugin frame and walk down to the largest exclusive one.
* A frame in `System.Threading` or a wait is time spent blocked. Find what it waits on, since more CPU will not help.
* Many small frames in the allocator or the GC point to allocation pressure. Read the allocation counters, then find the allocating call.
* Time spent in a `Task` that never shows CPU is waiting on I/O. Count the calls before you tune one call.

## Common Fixes

Try these in order. Each is cheaper and safer than the next.

1. **Do less.** Skip a schedule or a card whose inputs have not changed. Stop a pass at the first answer.
2. **Do it once.** Hoist work out of a loop. Cache a result for the length of one run, never across runs unless it is invalidated.
3. **Do it in bulk.** One query for a set, not one per row. One transaction for a batch of writes. One page, not the whole table.
4. **Do it together.** Run independent work with `ConcurrentTaskRunner` from Base, with a limit that leaves the server room to play.
5. **Allocate less.** Use `Span<T>`, `StringBuilder`, pooled buffers, and `static` lambdas where a trace shows allocation pressure. Never do this without a number.
6. **Stream, never buffer.** Read a file or a response as a stream. Never load a large file into memory.

### Library Queries

* Ask Jellyfin's library once per run for a set, not once per item.
* Ask only for the fields and item types the work reads.
* Keep a cached answer for the length of one run, and never across runs unless a change clears it.

### Async and Threads

* Never block on a task with `.Result` or `.Wait()`. It starves the thread pool.
* Pass the `CancellationToken` through, so a stopped run stops working.
* Hold a lock for the shortest time, and never across an `await`.
* Bound every queue and every degree of parallelism.

### ffmpeg

* Render a card once, named after its content, and reuse it. Never render one while a viewer waits.
* Run background renders at a lower priority than a stream a viewer is watching.
* Never loosen a stall watchdog or a restart budget for speed.

### Logging

* Use the `ILogger` templates in [code.md](code.md#c-conventions). Never build the message with string interpolation.
* A log in a hot loop costs even when the level is off. Move it out of the loop or guard it with `IsEnabled`.

## Proving the Result

1. Measure the same scenario before and after, with the same data and the same machine. Record both numbers.
2. Run the tests for every layer the change touches, per [testing.md](testing.md#layers).
3. For a change to the stream path or an ffmpeg argument list, run the ffmpeg integration tests with jellyfin-ffmpeg found. A faster wrong answer is a bug.
4. Check that memory and the thread count stay flat across a long run.
5. Report the numbers in the commit message or the report, as before and after for the same operation.

## What Not to Do

* Never ship a profiler, a timing test, a `Stopwatch`, an allocation counter, or a timing log in the plugin.
* Never commit trace files, gcdumps, or timing output.
* Never raise a timeout or lower a retry count to hide a slow call.
* Never remove a watchdog, a check, or a guard to gain speed.
* Never profile a Debug build. Use `Release`.
* Never change a [stop and ask](../starthere.md#stop-and-ask) item without approval, even when it is the cause.

## Checklist

* [ ] The reason to profile is a symptom, a change to a hot path, or a number that grew.
* [ ] The tier, the path, and the stop reason are in the report.
* [ ] The goal names the operation, the data size, and the limit.
* [ ] A trace, a counter, or a timed test shows where the time or memory goes.
* [ ] The fix is the simplest one that meets the goal.
* [ ] Before and after numbers exist for the same operation and data.
* [ ] A `Release` build was measured.
* [ ] The tests for every touched layer pass, including the ffmpeg integration tests for a stream path change.
* [ ] No profiler code, trace file, timing test, or output is in the diff.
* [ ] Anything on the stop and ask list, such as a change to stored schedules, was approved first.
