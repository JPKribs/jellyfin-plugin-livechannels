# Translations

How this plugin shows its text in the viewer's language. Base builds the strings in and checks them on every build. The full reference is Base's [Translations](https://jpkribs.github.io/jellyfin-plugin-base/Translations/) guide.

## Where the Strings Live

| Strings | Where |
| :--- | :--- |
| Jellyfin's own | `<JellyfinString Include="Key" />` items in `Jellyfin.Plugin.LiveChannels/Jellyfin.Plugin.LiveChannels.csproj` |
| This plugin's own | `Jellyfin.Plugin.LiveChannels/Localization/en-us.json`, one file per language |
| The endpoint pages load them from | `GET /livechannels/Translations`, which answers with `BaseTranslations.Serve` |
| The tab names and the Save Bar's saved line every page shows | `tabList` and `saveBarText` in `Web/livechannels_placeholder.js` |

## Rules

* Every word a person reads comes from a key. No hard coded English in a page, a script, or a message the server shows.
* Use a Jellyfin string whenever Jellyfin has the words. Search the web client's `src/strings/en-us.json` and the server's `Localization/Core/en-US.json` before adding a key.
* Add a local key only when Jellyfin has no string for it. Put it in `en-us.json` with its US English.
* One string, one key. The build fails on a key said twice or English said twice.
* Keep placeholders as `{0}`, `{1}`. Never build a sentence from pieces, since word order differs by language.
* A key is PascalCase and says what the text is, such as `HeaderQueue`, `LabelApiKey`, `ButtonRetry`, `ToastSaved`, `ErrorServerUnreachable`.
* Log lines and exception text for admins in the log stay in English and need no key.
* Text the server writes for everyone, such as a refused save, an activity log entry, or a scheduled task name, is in the server's display language, from `BaseTranslations.ServerLanguage`.
* Text a `livechannels` route returns to a page, such as a stress test message, is in the page's language, from `BaseTranslations.RequestLanguage(Request)`. A page that calls a route through `ApiClient` adds `language: t.language` to the address.
* Text drawn into a channel's video, such as the Up Next and tune-in cards and the standby slate, stays in English. The bundled fonts cover Latin only, and the cards are shared by every viewer.
* A page loads its strings once in `load`, before it builds any Base component, so every component gets its text in the viewer's language. Until then the markup shows its English.
* Base's components show their own default text in the viewer's language. Pass a text option only when it says something different from Base's default, such as the Save Bar's saved line.

## Add a String

1. Search Jellyfin's strings for the words.
2. Found: add `<JellyfinString Include="Key" />` to the csproj.
3. Not found: add `"Key": "English"` to `Localization/en-us.json`.
4. Use it.
    * Markup, with the key on the element whose own text is the English: `data-str="Key"`, `data-str-title`, `data-str-label`, or `data-str-placeholder`, with the English as the element's own text.
    * Script: `t.tr('Key')` or `t.format('Key', value)` on the handle from `loadTranslations('livechannels/Translations')`.
    * C#: `BaseTranslations.Get(TranslationKeys.Key, language)` or `BaseTranslations.Format(...)`. Text for everyone, such as an activity log entry, uses `BaseTranslations.ServerLanguage(config)`. A summed activity entry is an `ActivityOutcome` built with a key for one and a key for many, each with `{0}` for the count.
5. Build. Fix every `JPKT` error and warning.
6. Run `TranslationKeysTests`, which checks every key the pages use has a string.

## Add a Language

Add `Localization/<code>.json`, such as `de.json` or `pt-br.json`, with only keys `en-us.json` has. A missing key falls back to the language's base and then to US English. Jellyfin's strings come in every language Jellyfin has, with no file.

## Checklist

* [ ] No English in a page, script, or server message outside `Localization/en-us.json`.
* [ ] Jellyfin's string used wherever it has the words.
* [ ] The build has no `JPKT` error or warning.
* [ ] The pages show the strings, checked once in another language such as `de`.
