# Documentation

How to write the README, the docs site, screenshots, and release notes for this plugin. It applies to every page under `docs/`, to `README.md`, and to the `changelog` in `build.yaml`.

## The One Rule

Documentation describes the release it ships with. It reads as if this version were the only one that ever existed.

* Write what the plugin does. Never write what it used to do, what it no longer does, or what it now does.
* Write how a feature works. Never write that a bug was fixed, a problem is gone, or a limit was lifted.
* History belongs in the release notes. See [Release Notes](#release-notes).

| Do not write | Write |
| :--- | :--- |
| Channels now survive a restart. | Channels survive a restart. |
| The old rating fields were removed. | Leave it out. |
| The settings reference now lives in the docs. | The settings reference is in the docs. |
| Fixed an issue where a stalled share froze the picture. | A share that stops answering reaches standby within seconds. |
| New in 12.4.2.0, placeholder blocks take... | Placeholder blocks take... |
| This release carries no upgrade steps. | Version 12.4.2.4 carries no upgrade steps. |

"Now" and "no longer" are fine when they describe state rather than history. "The file is no longer on the storage" describes what the plugin sees, so it stays.

A version number belongs in docs only when the reader needs it to act, such as the minimum Jellyfin version or the oldest release that can upgrade directly. Keep those facts in [Installation](../docs/Installation.md) and [Versioning](../docs/Versioning.md), and name the version outright instead of "this release" or "an older release".

Remove compatibility notes for Jellyfin versions the plugin no longer supports. The plugin requires Jellyfin 12.4, so text about older servers describes servers that cannot run it.

## Writing Style

### Sentences

* Use present tense and plain declarative sentences.
* Describe the system in the third person. "Live Channels encodes the channel on demand."
* Address the reader in the second person only in steps. "Run **Refresh Guide**, then open **Live TV**."
* State one fact per sentence. Aim for 20 words or fewer and split anything over 30.
* Use no contractions. Write "cannot", not "can't".
* Use no em dashes, en dashes as dashes, or semicolons. Split the sentence, or join it with a comma and "and".
* Use colons only to introduce a list or a code block.
* Use parentheses rarely. Move the aside into its own sentence.
* End every sentence with a period.
* Write compounds as separate words the way the UI does, such as "one time setup" and "sign in".
* Write one paragraph per line. Never hard wrap prose.
* Use no marketing words such as "powerful", "seamless", "easily", or "simply".
* Write examples with generic names, such as a channel called `News` or a path such as `/media/cards/card.mp4`, never a real server, person, or address.

### Headings

* Use Title Case for every heading. "## Placeholder Blocks".
* Page titles are `#` and appear once per page.
* Name the thing, not the action, unless the section is a procedure. "## Settings" and "## Add the Plugin Repository" are both correct.
* Never rename a heading without fixing its anchors. `mkdocs build --strict` fails on a broken anchor.

### UI Names

* Write a label exactly as the page shows it, in bold. **Transition window (minutes)**.
* Write a path through the UI with `>` between the steps, in bold. **Dashboard > Plugins > Live Channels > Settings**.
* Write a tab as its label, in bold. **Channels**, **Popular**, **Sessions**, **Settings**.
* Quote activity log names and on-screen messages in double quotes. "No channels are streaming right now."
* Write routes, files, settings keys, and values in code. `POST livechannels/schedule/reset`.

A docs page and the UI must use the same word for the same thing. When a label changes, search `docs/` for the old one and change it everywhere in the same commit.

## Site Structure

The docs site is MkDocs with the Material theme, built from `docs/` by `mkdocs.yml` and published to `https://jpkribs.github.io/jellyfin-plugin-livechannels/` by the docs workflow.

### Navigation

Keep the nav in this order.

1. **Overview** (`index.md`). What the plugin is and why it exists.
2. **Getting Started**. [Installation](../docs/Installation.md) and [How It Works](../docs/HowItWorks.md).
3. **Reference**. [Settings](../docs/Settings.md) and [Versioning](../docs/Versioning.md).

Name page files in PascalCase with no spaces, such as `HowItWorks.md`. Add a page to the nav in `mkdocs.yml` in the same commit.

## How It Works

[docs/HowItWorks.md](../docs/HowItWorks.md) explains what the plugin does and how the parts fit, for users and for a new contributor. The summary on `PluginPaths` names where every file lives.

Review both after every change, and update them in the same commit when the change affects any of these.

* A feature is added, removed, or renamed.
* A scheduled task, hosted service, controller, or stored file is added or removed.
* Files move between Jellyfin's data folder and the **Stream file location**.

Name classes and files exactly as the code does, in code format. Never link to source files, since the docs build refuses links outside `docs/`.

### Page Templates

A settings section follows this shape.

```markdown
## Channels tab

One sentence that says what the tab is for.

### Section Name

| Setting | What it does | Default |
| --- | --- | --- |
| **Label** | What it does | Default |
```

A feature section opens with one sentence on what the feature is, then one paragraph per question a reader asks, then **Security** and **Limitations** where they apply.

### Settings References

List settings as bullets in the order the page shows them. Start each with the label in bold and follow it with a verb.

```markdown
* **Episodes per block** sets how many episodes of one series play in a row.
```

When a page has many settings with defaults, use a table instead.

| Setting | What it does | Default |
| :--- | :--- | :--- |
| **Episodes per block** | Consecutive episodes of one series before moving on | 1 |

Table cells have no closing period. Keep them to one sentence.

### Troubleshooting

Write trouble as a table of what the reader sees and what to do.

| Reason | What to do |
| :--- | :--- |
| Stream file location must be a new or empty directory | Pick a new folder, or empty the one named in *path* |

Put the exact on-screen text in the first column. Use italics for a placeholder inside it, such as *server*.

### Admonitions

Give every admonition a short title.

```markdown
!!! warning "Privacy and parental controls"
    One or two sentences.
```

* `note` for a detail the reader needs.
* `tip` for a better way to do something.
* `warning` for anything that can lose data or break a server.
* `info` for a requirement, such as jellyfin-ffmpeg.

### Links

* Link pages relatively with their `.md` name, such as `[Popular tab](Settings.md#popular-tab)`.
* End a Getting Started page with one "Next" line, such as `Next: [How It Works](HowItWorks.md).`
* Link to Base's README for a shared component instead of describing it again.

## README

The README is a landing page. The docs site holds the detail.

1. The icon, `<img src="docs/assets/icon.png" alt="Live Channels" width="96" align="left">`, then a one paragraph pitch and `<br clear="left">`.
2. `## Documentation`. One sentence on what the docs cover, a bold link to the site, then the repository URL for Jellyfin in a code block.
3. Any other section the owner keeps, unchanged.

Use `##` for README headings. Keep the README under 40 lines.

## Screenshots

### What to Capture

* Capture every page and dialog a docs page describes.
* Capture the dark theme only, which is Jellyfin's default.
* Capture from a test server with generic channel and library names.
* Capture with channels saved and a session running, so lists, counts, and the speed are filled.
* Hide nothing with editing. When a value is private, change the test data instead.

### Sizes

| Kind | Size | How |
| :--- | :--- | :--- |
| Dialog | 800 wide | The browser at 800 px, cropped to the dialog |
| Full page | 1920 wide | The browser at 1920 px, cropped to the content area |
| Phone | 375 wide at 2x | The browser's device mode at 375 px |

Save every capture as PNG. Crop out the browser frame and the Jellyfin header unless the header is the subject.

### Names and Placement

* Save to `docs/screenshots/`.
* Name the file in Title Case after what it shows, with spaces. `Channel Filters.png`, `Stress Test.png`.
* Link with `%20` for spaces and alt text that names the subject. `![Channel Filters](screenshots/Channel%20Filters.png)`.
* Place two related shots side by side in a table whose header row holds the captions.

```markdown
| Channels | Channels on a phone |
| :---: | :---: |
| ![Channels](screenshots/Channels.png) | ![Channels on a phone](screenshots/Channels%20Phone.png) |
```

### When to Retake

Retake a screenshot in the same commit as any change that alters what it shows, including a label, a tab, a layout, or the Jellyfin version in the header. A new Base version changes how every component looks, so retake every screenshot when it is raised. A screenshot that shows a tab or label the plugin no longer has is a bug.

## Installing a Build by Hand

```bash
./build.sh
```

The zip lands in `dist/` as `jellyfin-plugin-livechannels-<version>.zip`. Unzip it into `<config>/plugins/Live Channels_<version>/` on a Jellyfin 12.4 test server and restart the server.

## Building the Docs

```bash
pip install -r .github/docs/requirements.txt
```

```bash
mkdocs serve -a 127.0.0.1:8765
```

```bash
mkdocs build --strict
```

Run the strict build before every docs commit. CI runs the same build and refuses broken links, anchors, and nav entries.

## Release Notes

Release notes live in the `changelog` of `build.yaml`, under four keys. The publish workflow copies them into `manifest.json`, so every line reaches users' plugin catalogs.

| Key | Holds | Voice |
| :--- | :--- | :--- |
| `added` | A feature that did not exist | The feature name, then what it does |
| `changed` | Behavior a user will notice | The new behavior, in present tense |
| `fixed` | A bug | What went wrong in past tense, then the fix in present tense |
| `developer` | Routes, schema, tests, and tooling | Plain statements for other developers |

```yaml
changelog:
  added:
    - Placeholder blocks, which take a channel off air for a window of the day and loop a card with a message
  fixed:
    - The arrows on collapsible sections were too large. They are back to their usual size
```

* This is the one place that may say "now", "no longer", and "was".
* Write full sentences with no closing period.
* Write one entry per change a user would notice. Fold internal steps into the change they serve.
* Name UI labels exactly as the page shows them, without bold or backticks.
* Name the Base version in `developer`, with what it replaced locally.
* Give a new or changed `livechannels` route a `developer` line.

## Commit Messages

* Write an imperative sentence in sentence case with no closing period. "Keep multipart episodes in one block".
* Describe the behavior, not the files. Use no prefixes such as `fix:` or `feat:`.
* Add a body of plain paragraphs, wrapped at 72 characters, when the reason is not obvious.
* Commit docs and screenshots with the change they describe.

## Checklist

* [ ] Nothing describes an earlier version, a removed feature, or a fixed bug.
* [ ] Every UI label matches the page exactly and is bold.
* [ ] Every heading is Title Case.
* [ ] No em dashes, semicolons, or contractions.
* [ ] Every screenshot matches the current UI and uses generic test data.
* [ ] [How It Works](../docs/HowItWorks.md) was reviewed and matches the code.
* [ ] Every term matches [glossary.md](glossary.md).
* [ ] `mkdocs build --strict` passes.
* [ ] `build.yaml` has a changelog entry for any change a user would notice.
