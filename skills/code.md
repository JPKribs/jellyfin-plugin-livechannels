# Code

How to write, comment, and test code in this plugin. It covers C#, JavaScript, HTML, CSS, Python, shell, and YAML.

## Layout

| Path | Holds | Namespace |
| :--- | :--- | :--- |
| `Plugin.cs` | The plugin class and its pages. | root |
| `PluginServiceRegistrator.cs` | DI registration. | root |
| `Api/` | `LiveChannelsController`. | `.Api` |
| `Configuration/` | `PluginConfiguration` only. No web files. | `.Configuration` |
| `Web/` | Every page, script, and style served to the browser, named `livechannels_<page>`. | embedded |
| `Assets/` | The logo, the fonts in `Assets/Fonts/`, and the Material Symbols font and codepoints. | n/a |
| `Models/` | Channel settings, enums, and records. | `.Models` |
| `Services/` | The Live TV service, streams, sessions, cards, recording, and the stress test. | `.Services` |
| `Tasks/` | The `PluginScheduledTask` classes. | `.Tasks` |
| `Utilities/` | Pure helpers, such as ffmpeg arguments and schedule math. | `.Utilities` |

Development tools that never ship live in `tools/`. The tests sit flat in `Jellyfin.Plugin.LiveChannels.Tests/`.

## Base First

[JPKribs.Jellyfin.Base](https://github.com/JPKribs/jellyfin-plugin-base) holds everything two plugins could share. Its README lists every component and helper. Read it before you write anything new.

### Use What Base Has

* Use the Base type or component when one fits. Never write a local version, a wrapper that renames it, or a CSS override of a `base-` class.
* When Base almost fits, change Base. Never patch around it locally.
* Delete a local copy as soon as Base offers the same thing. Do it in the commit that moves to that Base version.
* Never shadow a Base name. A local `FormatUtilities` beside Base's `FormatUtilities` is ambiguous.
* Read Base from the package this plugin builds against. The C# source is in `~/.nuget/packages/jpkribs.jellyfin.base/<version>/src/`, and the JS typings are in `assets/jpkribs_shared.d.ts` beside it.
* Treat the local Base checkout as read only. Base has its own owner and its own release, so report a gap and wait for the version that closes it.
* When Base differs from the local code it replaces, keep the local behavior in a small step around the Base call, and cover it with a test. `LiveChannelsTvService.WriteFileAtomicAsync` writes through `AtomicFile` and lets a writer that loses a race stand down.

| Need | Use from Base |
| :--- | :--- |
| Plugin class and config lock | `PluginBase<TPlugin, TConfiguration>` |
| Scheduled task | `PluginScheduledTask` |
| Outbound HTTP | `BaseHttp` and `HttpResult` |
| HTTP handlers | `BaseRetryHandler` with `BaseRetryOptions`, and `RedirectRefusingHandler` |
| Addresses another server or a user supplied | `SsrfGuard`, with `requireHttpsForPublic: true` |
| Retry and backoff | `RetryPolicy`, `BackoffPolicy`, `CircuitBreaker` |
| Parallel work | `ConcurrentTaskRunner` |
| Secrets | `SecretProtector`, `AddPluginSecretProtector` |
| Comparing a presented secret | `SecretCompare.FixedTimeEquals` |
| Writing a file whole or not at all | `AtomicFile`, with `ownerOnly: true` for a key or secret |
| Paths inside a folder | `PathContainment` |
| Streams | `StreamUtilities.CopyWithIdleTimeoutAsync` and `CopyThrottledAsync`, `SpeedBucket`, `ThrottledReadStream`, `CappedReadStream`, `ProgressReportingStream`, `StreamLimits` |
| Hashes and sizes | `HashUtilities`, `ByteSize` |
| The clock | `TimeProvider`, and `UtcTime` for stored times |
| Activity log | `ActivityLogger`, `ActivitySummary`, `ActivityText` |
| Paged lists | `PagedResult<T>`, `PagedQuery` |
| SQLite versioning | `DatabaseSchema` |
| JSON state file | `JsonFileStore<T>` |
| Formatting and names | `FormatUtilities`, `ItemTitles`, `RateUtilities`, `FileNameSanitizer` |
| Config page lifecycle | `createConfigPage`, called by each page script |
| Page helpers | `createShared` |
| Tables | `createPaginatedTable` |
| Lists and row editors | `createCheckboxList`, `createRepeater`, `createSortableCardList` |
| Pickers | `createChipSelect`, `createMediaPicker`, `createUserSelector` |
| Logs | `createLogViewer` |
| Charts | `createPieChart`, `createUsageMeter`, `createBarChart` |
| Dialogs | `openDialog`, `confirmDialog`, `promptDialog`, `openEditorDialog`, `bindDialog` |

### Move Code Into Base

Code belongs in Base when another plugin could use it unchanged. A helper with no Live Channels types in its signature is a candidate.

1. Add it to `../Shared/jellyfin-plugin-base/src/`, one file per component, following Base's [code guide](https://github.com/JPKribs/jellyfin-plugin-base/blob/main/skills/code.md).
2. Add tests in `JPKribs.Jellyfin.Base.Tests/`, and JS tests in `JPKribs.Jellyfin.Base.Tests/js/`.
3. Document it in Base's docs site inventory.
4. Pack it locally with `scripts/pack.sh` and build this plugin against it with `-p:RestoreAdditionalProjectSources=<base>/nupkg`.
5. Commit and push to Base, then run Base's **Release** workflow.
6. Raise the `JPKribs.Jellyfin.Base` version in this plugin's csproj, delete the local copy, and add a `developer` changelog line naming the Base version.

Never commit this plugin against an unreleased Base package.

## Comments

### The Rules

* Every class, method, function, and component gets a header comment.
* The header is the name, then a summary of one or two sentences, leaning to one.
* List inputs and outputs when there are any. One line each, as `name [type]: What or why`.
* Write no inline comments, with one exception. A branch whose reason is not obvious gets one short line above it.
* A branch comment says why, not what. Keep it under 15 words.
* Never comment what the code already says. `// Check if values differ` is noise.
* Never leave commented out code, TODO, FIXME, or HACK. Open an issue instead.
* Never describe history. "Used to", "now", and "was changed to" belong in commit messages.
* Use no em dashes or semicolons in comments.

```text
<Name>
<What it does, in one sentence.>

Inputs
name [type]: What it is or why it is needed.

Output
[type]: What comes back.
```

Leave out **Inputs** when there are none, and **Output** when nothing comes back.

### C#

C# uses XML docs so the compiler and IntelliSense read them. The member name is the title. The type comes from the signature, so a `param` gives only the what or why.

```csharp
/// <summary>
/// Finds the program airing at a moment and how far into it the moment falls.
/// </summary>
/// <param name="programs">The loop, in airing order.</param>
/// <param name="now">The moment to place.</param>
/// <param name="anchor">When the loop first started.</param>
/// <returns>The index of the program and the offset into it.</returns>
public static (int Index, TimeSpan Offset) CurrentProgram(IReadOnlyList<ProgramEntry> programs, DateTime now, DateTime anchor)
{
    ArgumentNullException.ThrowIfNull(programs);

    if (programs.Count == 0)
    {
        return (0, TimeSpan.Zero);
    }

    // A moment before the anchor counts back from the end of the loop.
    ...
}
```

* Every type and every member that is not private gets `<summary>`, every `<param>`, and `<returns>` when it returns a value.
* Private members get a `<summary>` when the name does not say enough.
* Use `<inheritdoc/>` on an interface or override member.
* Write `true` and `false` as `<c>true</c>` and `<c>false</c>`.
* A constructor summary is "Initializes a new instance of the <see cref="Name"/> class."
* Skip tautologies. `<param name="item">The item.</param>` says nothing, so write what the item is for.
* Put one type in each file, named after the type. A small enum used by one class may share its file.
* Match the namespace to the folder.

### JavaScript

```js
// createBlockEditors
// Builds the Rating Blocks and Placeholder Blocks card lists for one channel. Both lists save into the one
// RatingBlocks array, rating blocks first.
//
// Inputs
// options [object]: { ratings, shared, onChange }, where ratings comes from loadRatings.
//
// Output
// [object]: { ratingElement, placeholderElement, setValue(blocks), getValue(), isValid() }.
export function createBlockEditors(options) {
```

* Comment every top level function, every function assigned to `shared`, and every exported function.
* Do not comment the methods inside a returned handle. List them in the Output line instead.
* Open each file with a header naming the file and saying what it holds in one sentence. Use no `// ====` banners.
* Each page script imports Base by the relative path `./configurationpage?name=livechannels_jpkribs_shared.js`. `livechannels_placeholder.js` holds the rating and placeholder block Repeaters the Channels and Popular pages share, each card field showing only for the values its Repeater `visible(item)` allows.
* A new script or page goes in `Web/`, is an `EmbeddedResource` in the csproj, and is a page in `Plugin.GetPages`.

### HTML

```html
<!-- Channel selector -->
<!-- Picks the channel the editor below works on. -->
<div class="verticalSection">
```

* One comment line naming each section or dialog where it starts, and an optional second line saying what it is for.
* Use no inline `style` attributes.

### CSS

```css
/* lc-guide-row */
/* A guide row with a fixed time column and the program filling the rest. */
/* Example: <div class="lc-guide-row"><span>8:00</span><span>Pilot</span></div> */
.lc-guide-row { display: grid; grid-template-columns: 5rem 1fr; gap: var(--base-gap-wide); }
```

* Three lines above each component, the name, what it looks like, and one example.
* Prefix every class with `lc-` in kebab case. `base-` is reserved for Base.
* Use Base tokens such as `--base-border` and `--base-fg` for colors. Never write a hex value for something Base already colors.

### Shell and YAML

* A shell script opens with a comment saying what it does and how to run it.
* A YAML file opens with a comment saying what it is for. Comment a step only when its reason is not obvious.

## C# Conventions

* Target `net10.0` with `Nullable` on, `TreatWarningsAsErrors` on, and `AnalysisMode` set to `AllEnabledByDefault`.
* Keep `jellyfin.ruleset` identical across plugins. Raise a rule change in every plugin at once.
* Suppress a rule only at the narrowest scope, with a reason. `#pragma warning disable CA2100 // SQL is internal and parameterized.`
* Use file scoped namespaces, `sealed` classes, and records for data.
* Name private fields `_camelCase`.
* End every async method name in `Async`, pass the `CancellationToken` through, and use `ConfigureAwait(false)` on every await.
* Guard public arguments with `ArgumentNullException.ThrowIfNull`.
* Log through `ILogger<T>` with static, named templates, starting with `Live Channels:`.
* Catch a specific exception, or filter with `when`. Rethrow `OperationCanceledException` when the token was cancelled.
* Read the clock once in a method and reuse the value. Two reads can straddle a second and disagree.
* Take a `TimeProvider` or a clock argument wherever time decides an outcome, so a test can move it.
* Register services in `PluginServiceRegistrator`.
* Close in Jellyfin every live stream the plugin releases itself, through `CloseInJellyfinAsync`. Jellyfin keeps an opened stream until `IMediaSourceManager.CloseLiveStream` is called for it.
* Bound anything that can wait on storage forever. A check on a hung mount never returns, so count the ones still waiting and stop starting more, as `StorageProbe` does.
* Build every ffmpeg argument list in a `Utilities` class, such as `StreamArguments` or `BumperArguments`, so a test can run it.

## JavaScript Conventions

* Each config page is an ES module whose default export calls Base's `createConfigPage` with the `TABS` list and its own `tabIndex`.
* Keep one handle for each Base component a page draws. Call `destroy` before drawing it again, and `setValue` when only its numbers change.
* Drop an answer that arrives after the page moved on. Remember the picked key before the request and compare it when the answer lands.
* Use `const` and `let`, never `var`. Arrow functions are fine for callbacks.
* Escape every value placed in HTML with `escapeHtml`.
* Report results and errors with `shared.setStatus`.
* Register every poller and observer with the page handle so `viewhide` stops it.

## Security

* Guard every route in `LiveChannelsController` with `[Authorize(Policy = "RequiresElevation")]`, which the controller carries.
* Never put a token in a URL. The session logo is fetched with `shared.fetchObjectUrl`, which sends the token in a header, since Jellyfin refuses an `api_key` query.
* Log text from a request, a channel, or a file through a named template. A new log line that holds such text passes it through `FormatUtilities.SanitizeForLog`.
* Escape every user value placed in an ffmpeg filter or `drawtext`, or write it to a file the filter reads, as the card text does.
* Keep files inside the folders `PluginPaths` names. Check a path built from user text with `PathContainment`, and a file name with `FileNameSanitizer`.
* Clean up only files the plugin wrote. `ConfigurationValidator` refuses a **Stream file location** that already holds other content.
* Air on the Popular Channel only titles every Live TV user may see, since its guide is shared.
* Write a file a reader may open at any moment with `AtomicFile`.
* Escape every value placed in HTML with `escapeHtml`.

## Tests

Every change has tests. See [testing.md](testing.md) for each layer, how to write tests, and the ffmpeg integration tests.

## Checklist

* [ ] Base's source and typings were searched, nothing here duplicates Base, and anything reusable went to Base first.
* [ ] Every class, method, function, and component has a header.
* [ ] Inline comments appear only above branches, and say why.
* [ ] Every change has tests that pass, per [testing.md](testing.md).
* [ ] The [security](#security) rules hold.
* [ ] `dotnet build` shows no warnings.
