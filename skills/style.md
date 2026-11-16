# Style

How every page, dialog, label, and message in this plugin looks and reads. It is drawn from Base and from all the JPKribs plugins, so the same rules carry to each of them.

Base is [JPKribs.Jellyfin.Base](https://github.com/JPKribs/jellyfin-plugin-base). Its README documents each component's options. This guide says which component to use, when, and what to write in it.

## Principles

1. **One component per concept.** When Base has it, use it. Look before you build, per [Find the Component](#find-the-component). When Base lacks it and a second plugin needs it, add it to Base. Never build a second version.
2. **Short beats complete.** A label is a few words. A description is one sentence. A message is one sentence.
3. **The page explains itself.** A reader understands a setting from its label and its one line description, without the docs.
4. **The same thing has the same name everywhere.** The label, the docs, the message, and the activity log use one word for one concept.
5. **Follow Jellyfin.** Use Jellyfin's own markup and theme, so the plugin looks like part of the dashboard.

## Writing

### The Rules

These rules apply to every string a user can see. That includes HTML, JS, C# messages the API returns, and activity log entries.

* **No em dashes or en dashes as punctuation.** Split the sentence, or join it with a comma and "and". An en dash in a numeric range, such as `22–6`, is fine.
* **No semicolons.** Split the sentence.
* **Avoid colons.** Never end a label with one. In a sentence, rewrite it. "How fast files move: downloads and uploads." becomes "Sets how fast files move in each direction."
* **One sentence.** Two only when the second gives the fix or the consequence.
* **End every sentence with a period.** Labels, buttons, titles, tabs, and table cells are not sentences and take no period.
* **No "please", "successfully", "simply", or "just".**
* **No exclamation marks.**
* **No ALL CAPS for emphasis.** Say why it matters instead.
* **No "(s)".** Write the right plural for the count.
* **No contractions.** Write "cannot", not "can't".
* **No "e.g." or "i.e.".** Write "such as".
* **American spelling.** Color, behavior, favorite, center.
* **Use the reader's words.** "Encoder" and "loop", not "producer" and "program loop builder", unless the text is for developers.

### Casing

| Text | Case | Example |
| :--- | :--- | :--- |
| Tab | Title Case, one word | Channels |
| Section and collapsible title | Title Case | Channel Settings |
| Field label | Title Case | Episodes per Block |
| Checkbox label | Title Case | Show Up Next Cards |
| Button | Title Case, verb first | Add Source |
| Dialog title | Title Case | Delete Channel |
| Badge and status chip | Title Case | Enabled |
| Column header | Title Case | Started |
| Activity log name | Title Case | Channel Stopped for News |
| Description, message, placeholder sentence | Sentence case | Plays a 10 second card before each item. |

Title Case capitalizes every word except short articles, conjunctions, and prepositions in the middle, such as "a", "and", "of", "to", and "in". Keep acronyms as written, such as "ffmpeg", "GIF", "kbps", and "MB".

Many labels on the pages are still in sentence case, such as **Channel number**. Put a label in Title Case when you change it, and change the docs in the same commit.

### Words to Use

Pick the word on the left every time.

| Use | Not | When |
| :--- | :--- | :--- |
| Remove | Delete | Takes an entry out of a list or the config, and nothing on disk is lost |
| Delete | Remove | Destroys data, files, or history that cannot come back |
| Clear | Empty, Reset | Removes every entry in a list or log |
| Reset | Restore | Puts settings back to their defaults |
| Save | Apply, Update | Writes changes to the config |
| Done | OK, Close | Closes an editor and keeps its changes |
| Cancel | Close, No | Closes without keeping changes |
| Add | New, Create | Puts a new entry into a list on the page |
| New | Add, Create | Starts a new record in a record picker |
| Run Now | Start, Trigger | Starts a scheduled task right away |
| Kill | Stop, End | Stops a session at once, even with viewers |
| Select | Pick, Choose, Tick, Click | Any instruction to make a choice |
| Turn on, Turn off | Enable, Disable, Tick, Untick | An instruction about a checkbox or toggle |
| Leave blank | Leave empty | An empty field means the default |
| Could not | Failed to, Unable to | An error message |

The control names its own result. **Remove** on a source takes it out of the channel and leaves the library alone, so it is Remove. **Delete** on a channel destroys its settings, so it is Delete.

### Labels

* Two to four words. Name the thing the field holds, not the action.
* Put a unit at the end in lowercase parentheses, such as **Transition Window (minutes)** or **Video Bitrate (kbps)**. Use symbols for standard units, such as `MB` and `kbps`.
* No colons, periods, or question marks.
* A checkbox label names the feature it turns on, never "Enable X". Write **Show Up Next Cards**, not **Enable Up Next Cards**. The one exception is the master switch at the top of a page, which reads **Enable** followed by its name, such as **Enable the Popular Channel**.

### Descriptions

A `fieldDescription` sits under every field that is not obvious from its label.

* One sentence that says what the setting does, starting with a verb. "Keeps two part episodes together across a block boundary."
* Add a second sentence only for the default or a consequence. "0 turns it off."
* Describe the current behavior only. Never "now" or "no longer".
* Never repeat the label.
* Put units, ranges, and what blank means here, never in the label or the placeholder.

| Bad | Good |
| :--- | :--- |
| Bakes subtitles in; Forced only burns the forced track. | Bakes subtitles into the picture for everyone. |
| Even one stream could not hold realtime; consider lowering resolution. | Even one stream could not hold realtime. Lower the resolution or bitrate. |
| Set to 3 — remember to Save. | Set to 3. Select Save to keep it. |
| This setting enables the generation of Up Next cards. | Plays a 10 second card before each item. |

A section intro is one `fieldDescription` right under the section title. It says what the section is for in one sentence.

### Placeholders

A placeholder shows an example of a valid value. It is never the only place an instruction lives, since it disappears once the reader types.

* Show a realistic example with no prefix. `diversity_1`, not `e.g. diversity_1`.
* Use `example.com` addresses and generic names only.
* For an override that falls back to a default, show the value it falls back to. When that value is not known, write `Default`.
* For a search box, write "Search" and the noun, with no ellipsis. `Search studios`.
* Never put a sentence in a placeholder. Move it to the description.

### Buttons

* A verb, or a verb and a noun. **Save**, **Add Source**, **Run Now**.
* Name the object when the page has more than one of that action. **Add Rating Block** and **Add Placeholder Block** share the Filters section.
* A Save is always **Save**, with no noun.
* An icon only button carries a `title` of one to three words, such as `Remove`, `Move Up`, or `Retry`. Never a sentence.
* A button that runs for a while shows progress in place. See [Progress](#progress).

### Messages

A message reports what happened in one sentence that ends with a period.

| Kind | Pattern | Example |
| :--- | :--- | :--- |
| Saved | `<Thing> saved.` or `Saved.` | Saved. |
| Done | `<Thing> <past verb>.` | Deleted. |
| Copied | `<Thing> copied.` | Copied. |
| Working | `<Verb>ing…` | Stopping… |
| Could not | `Could not <verb> the <thing>.` | Could not save the channel. |
| Could not, with a fix | Add one sentence with the fix | Could not load the channels. Reload the page to try again. |
| Missing input | `Enter <the thing>.` or `Select <the thing>.` | Select a test item. |
| Nothing to do | `No <things> to <verb>.` | No channels to import. |

* Use the ellipsis character `…`, never three dots, and only for work in progress.
* When the server gave a reason, show it. A config save shows the `ConfigurationValidator` sentence the server returns.
* Name the count with a correct plural. "Exported 3 channels." and "Exported 1 channel."
* Never say "error occurred". Say what could not happen.

### Confirm Dialogs

A confirm asks one question and states the consequence.

```js
confirmDialog('Delete channel "News"? This cannot be undone.', {
    title: 'Delete Channel',
    confirmText: 'Delete',
    destructive: true
});
```

* The message is a question, then one sentence on the consequence. "Delete 3 items? This cannot be undone."
* The title is the action in Title Case, such as **Delete Channel**. Never leave the default **Confirm**.
* The confirm button repeats the action verb, such as **Remove** or **Delete**. Never **OK** or **Yes**.
* Set `destructive: true` for anything that loses data.
* Never "Are you sure you want to". Ask the question directly.
* Never use `window.confirm` or `Dashboard.confirm`.

### Empty States

| Where | Pattern | Example |
| :--- | :--- | :--- |
| Record picker | `No <things> yet. Select New to add one.` | No channels yet. Select New to add one. |
| List in a form | `No <things> yet. Add one below.` | No sources yet. Add one below. |
| Live list | `No <things> right now.` | No channels are streaming right now. |
| Filtered list | `No <things> match.` | No items match. |
| Feature off | `<Feature> is off. Turn it on above to <result>.` | The Popular Channel is off. Turn it on above to serve it. |

Use `shared.emptySection(text)` for a region and `shared.emptySection(text, { inline: true })` for a list.

### Activity Log

Write entries through `LiveChannelsActivity`, which uses `ActivityLogger`, `ActivitySummary`, and `ActivityText` from Base.

* The name is short and Title Case.
* The overview is one sentence with no closing period, as Base writes it.
* Use the same verb the UI uses for the action.

### Messages From C#

The API returns text that the page shows as is, so it follows the same rules.

* A full sentence that starts with a capital and ends with a period, as `ConfigurationValidator` writes them.
* Say what is wrong and, when it helps, what to do.
* Never return an exception message, a stack trace, or a type name.

## Color

### Tokens

Use Base tokens. They follow the dashboard's theme, light themes included. Never write a hex value for something these cover.

| Token | Use |
| :--- | :--- |
| `--base-accent` | Links, focus rings, primary buttons, progress |
| `--base-accent-hover` | Hover on accent elements |
| `--base-fg` | Washes on elements that fade their own text |
| `--base-surface` | Panel and dialog backgrounds |
| `--base-border` | Every border and divider |
| `--base-gap-wide` | Space between components, such as one field row and the next |
| `--base-gap-narrow` | Space inside a component, such as between a label and its field |
| `--base-mono` | Code, ids, paths, and raw errors |
| `--base-success`, `--base-warning`, `--base-error` | Solid status colors |
| `--base-<color>-bg`, `--base-<color>-fg` | Tinted badge and card backgrounds and text, for green, red, orange, purple, blue, and gray |

### Meaning

A color means the same thing on every page and in every plugin.

| Color | Means | States |
| :--- | :--- | :--- |
| Green | Done and healthy | Enabled, Keeping Up |
| Red | Failed, or about to lose data | Falling Behind, Failed |
| Orange | Waiting, or needs attention soon | Slightly Behind, Stopping |
| Purple | Working right now | Running |
| Blue | Informational | Info |
| Gray | Off or not applicable | Disabled, Waiting for a Reading |

* Map a status to a color through `BADGE_STATES`. Never keep a second map in the plugin.
* Disabled is gray when it means off. Red is only for failure.
* Use "orange" for the warning color in class names, never "amber", "yellow", or "warning".
* Never use color alone. A badge always has a word, and a dot always has a `title`.

## Page Structure

### Shell

Every config page uses Jellyfin's page shell.

```html
<div data-role="page" class="page type-interior pluginConfigurationPage withTabs"
     data-require="emby-input,emby-button,emby-select,emby-checkbox">
    <div>
        <div class="content-primary">
            <div class="verticalSection">
                <div class="sectionTitleContainer flex align-items-center">
                    <h2 class="sectionTitle">Sessions</h2>
                </div>
                <div class="fieldDescription">Every channel encoding on the server.</div>
            </div>
        </div>
    </div>
    <link rel="stylesheet" href="configurationpage?name=livechannels_jpkribs_shared.css">
</div>
```

* List `data-require` in the order `emby-input,emby-button,emby-select,emby-checkbox`, dropping any the page does not use.
* Use no inline `style` attributes. Add a class to the plugin CSS, or to Base when it is reusable.

### Tabs

* One registered page per tab, wired with `createConfigPage` and `setTabs`.
* Tab names are one Title Case word.
* Each page script holds the same `TABS` list and its own `tabIndex`. Change all four together, or move the list to one shared module first.
* Order tabs from what is used most to what is set once. Live Channels is **Channels**, **Popular**, **Sessions**, **Settings**.

### Sections

| Need | Component |
| :--- | :--- |
| A page with several independent groups of settings | `base-collapsible-section`, one per group |
| A group that is always open | `base-collapsible-section fixed`, whose header is a `div` with no icon, so it matches the collapsible sections around it |
| A heading inside a section | `base-subsection-title` |
| An ordered setup | `base-collapsible-section numbered`, which Base numbers in order |
| Settings that only matter when a checkbox is on | `base-indent` under the checkbox, shown with `shared.bindReveal` |

* A collapsible header is a `button.base-collapsible-header` holding `base-collapsible-title` and `base-collapsible-icon`. Base replaces the icon with `expand_more`.
* Start collapsibles closed, except the first one on a page someone opens to act on.
* Give every top level group on one page the same header. A chart or a summary takes the `fixed` section, never a bare `base-subsection-title`.
* Hide a whole section while its feature is off or it has nothing to show. Never show an empty section.
* Put anything that depends on a picker under that picker, and redraw it when the pick changes.

### Saving

Every save looks the same.

* Every save is Base's `createSaveBar` with `fill: true`, and its button reads **Save**.
* One Save Bar per thing that saves. A page with one save has one bar at the bottom of what it saves, and a section that saves on its own has one bar at the bottom of its content.
* The Save Bar is the last element of what it saves. Nothing sits beside it or below it in that section.
* Other actions that belong with the save, such as Reset to Defaults, Clear, Test, Run Now, or Refresh, go in one `base-button-row` directly above the Save Bar. A select that belongs with them stays a normal select in that row.
* The Save Bar shows the outcome. Never add a status span or a hand placed Save button beside it, and never position a Save Bar with plugin CSS.
* Wire `setDirty` from the page's change tracking, and have `onSave` return the save promise. The channel editor tracks changes with `createChangeTracker` and asks before discarding edits.
* An editor dialog closes with **Done** in `openEditorDialog`.
* Read the config fresh before a save, change only what the page owns, and save it, so another tab's settings survive. The Channels page also refuses a save when the stored channels changed since it loaded.

## Components

Every component below is in Base. Use it as Base ships it. When it does not fit, change Base.

### Find the Component

Before writing markup for a list, a row editor, a chart, or a picker, look for it in Base.

```bash
grep -o "export function create[A-Za-z]*" ~/.nuget/packages/jpkribs.jellyfin.base/<version>/assets/jpkribs_shared.d.ts
```

* The typings in that file document every option of every component. Read the component's block before using it.
* Rows of fields with Add and Remove are a `createRepeater`. A list of checkboxes is a `createCheckboxList`. Values picked from a long list are a `createChipSelect`. Library items are a `createMediaPicker`. A log is a `createLogViewer`.
* A hand built version of any of these is a defect, even when it looks the same.
* When a Base component lacks one thing the page needs, use the component and report the gap to Base. Never fall back to building it by hand.

### Fields

| Need | Use |
| :--- | :--- |
| Text, number, or URL | `inputContainer` with `<label for>`, `<input is="emby-input">`, and a `fieldDescription` |
| Choice from a short list | `selectContainer` with `label.selectLabel` and `<select is="emby-select">` |
| On or off | `checkboxContainer checkboxContainer-withDescription` with `emby-checkbox` and `fieldDescription checkboxFieldDescription` |
| A mode that needs a sentence per option | `createChoiceGroup` with `base-choice` cards |
| Several on or off options in a row | `base-toggle-group` with `base-toggle` pills |
| Many checkboxes | `base-check-grid` |
| Many items from a long or searchable list | `createCheckboxList`, with `renderRow` giving each row a `label`, a `sublabel`, and an `image` loader |
| Rows of several fields that are added and removed | `createRepeater` |
| Pairs of a name and a value | `createKeyValueEditor` |
| A list of plain text values | `createStringList` or `createTagInput` |
| Typeahead with removable chips | `createChipSelect`, with `single: true` for one choice |
| One user | `createUserSelector` |
| Several users | `createUserMultiSelector` |
| Several fields side by side | `base-field-row` with `base-field` |
| A secret | `bindSecretField`, `readSecretField`, and `clearSecretField` |
| A template with `{name}` tokens | `placeholderHint` and `bindPlaceholders` |
| Code or JSON, read only | `base-code-block` |

* Use `emby-select`, never `base-selector-dropdown`, for a field. `base-selector-dropdown` is only for the record picker.
* Every input has a `<label for>`.

### Buttons

| Need | Class |
| :--- | :--- |
| Primary action | `raised base-button primary` |
| Destructive action | `raised base-button destructive` |
| Secondary action | `raised base-button secondary` |
| Save | `createSaveBar` with `fill: true`. See [Saving](#saving) |
| Action in the row above a Save Bar | `raised base-button secondary fill`, or `raised base-button destructive fill` for one that deletes or resets data, in one `base-button-row` |
| Compact action in a section | add `small`, never in the row above a Save Bar |
| Icon and label in a toolbar | `raised base-button` holding a `material-icons` span and a label |
| Icon only in a row | `base-row-btn`, with `.dirty` for unsaved and `.success` after saving |

* Every button is `<button is="emby-button" type="button">`.
* Never build a Save button by hand.
* Icons are `<span class="material-icons" aria-hidden="true">name</span>`.
* Close a dialog with `base-dialog-close` and `&times;`. Never "✕" or another glyph.

| Action | Icon |
| :--- | :--- |
| Add | `add` |
| Edit | `edit` |
| Remove or delete | `delete` |
| Copy | `content_copy` |
| Move up and down | `keyboard_arrow_up`, `keyboard_arrow_down` |
| Drag | `drag_indicator` |
| Expand | `expand_more` |
| Retry | `refresh` |
| Open | `chevron_right` |
| Import and export | `upload`, `download` |
| Show | `visibility` |

### Records

| Need | Use |
| :--- | :--- |
| One record at a time from a list | `base-selector` with New, Rename, and Remove |
| Records in priority order | `createSortableCardList` |
| A plain list of records | `base-card-list` with `base-card-item-*` |
| Edit one record in a dialog | `openEditorDialog` |
| A large searchable list | `createPaginatedTable` |
| Edit table rows in place | `base-bulk-edit-bar` and `base-edit-row` |
| Uploaded files | `base-asset-list` |
| Records of grouped fields, such as sources or rating blocks | `createRepeater` with `layout: 'card'`, and `custom` fields for any other Base control |
| A thumbnail in a table row | A column `image(item)` loader with `imageShape`, one shape for each column |
| A mark on a table row, such as picked | The table's `rowClass(item, index)` |

* A record picker's actions read **New**, **Rename**, **Remove**, then **Import** and **Export**, in that order.
* Use `createPaginatedTable` for any list that can grow past one screen. Rows open with `actions.onRowClick`.

### Status

| Need | Use |
| :--- | :--- |
| The state of one item | `shared.statusBadge(text, state)` |
| A label on an item | `shared.badge(color, label)` |
| Counts at the top of a page | `shared.renderCards` with `base-cards`, and `compact` for many counts on a phone |
| Label and value pairs | `createDefinitionList` |
| A notice in the page | `base-info`, `base-success`, `base-warn`, `base-error`, `base-neutral` |
| Raw failure text | `base-error-message` |
| The result of an action | `shared.setStatus` |
| A row of reset actions | `base-reset-group` with `base-reset-row` |

* Never use `Dashboard.alert` for a result. It blocks the page. Use `setStatus`.
* A notice holds one sentence. Use a callout for something the reader must know before acting, not for a result.

### Charts

| Need | Use |
| :--- | :--- |
| Parts of a whole | `createPieChart`, with `donut` and the total in `centerText` |
| How much of a limit is used | `createUsageMeter`, with `bytes: true` for sizes |
| Values compared across names | `createBarChart` |
| A value over time | `createLineChart` or `createSparkline` |
| One number with a label | `createStatCard` |

* Pick the chart by the question it answers. A single used of total bar is a Usage Meter, never a Bar Chart.
* Name each slice with a glossary term in Title Case.
* Take colors from Base's series colors. Set a color only when it carries a [meaning](#meaning).
* Give every chart a `label`, and an `emptyText` sentence for when it has nothing to draw.
* Scope a chart to what the page shows. A chart beside the channel picker counts that channel only.

### Progress

* A task started from a button shows progress in that button with `shared.pollTaskProgress`.
* Progress inside a row uses `base-progress` and `base-progress-bar`.
* Never block the page with `Dashboard.showLoadingMsg`.

### Dialogs

| Need | Use |
| :--- | :--- |
| A yes or no question | `confirmDialog` |
| A few values | `promptDialog` |
| A record editor | `openEditorDialog`, with `wide: true` for a large one |
| A custom dialog | `openDialog`, with `size: 'large'` for a large one such as the ffmpeg log |
| A dialog already in the page markup | `bindDialog` |

* Build dialogs in JS with `openDialog` and its wrappers. Use `bindDialog` only when the markup is large and static.
* A dialog title is Title Case and names what the dialog does.
* Buttons read, from left to right, the destructive action, then **Cancel**, then the primary action.

## Layout

* Use Base's breakpoints, 768 px for tablet and 600 px for phone. Use 700 px only where Base does, for dialogs.
* Test every page at 375 px wide. Nothing scrolls sideways except a table.
* Look at every changed page on a test server before calling it done. Reload the page after installing a build, since the dashboard caches scripts.
* Spacing comes from `--base-gap-wide` and `--base-gap-narrow`. Never add margins between fields.
* Never set a width in pixels on a field. Let it fill its container.

## Accessibility

* Every input has a label, and every icon only button has a `title` and an `aria-label`.
* Every icon span has `aria-hidden="true"`.
* Rows that open on click also open with Enter and Space. Base's table and card list do this already.
* Never rely on color alone.

## CSS in This Plugin

* Plugin classes use the `lc-` prefix in kebab case. `base-` is reserved for Base.
* Plugin CSS lives in a `<style>` block at the end of each page's HTML, with the three line comment from [code.md](code.md#css) above each component.
* Keep plugin CSS for layout that only this plugin has. A component another plugin could use goes to Base.
* Never override a `base-` class. Change Base instead.
* When Base has a defect that breaks a page, report it and add one marked stopgap rule under a comment that names the defect. Delete the rule in the commit that moves to the Base version with the fix.
* Size a column in `rem` or `fr`. An `em` width changes with the font size of each row, so a smaller heading row falls out of line with its columns.
* Never define a color token. Use Base's.

## Hand Built Pieces

Every control on the pages is a Base component, with nothing built around one. When a page needs something Base lacks, report the gap to Base and wait for the option. Never step around a component in the plugin.

* A card field that shows only for some values uses the Repeater field's `visible(item)`, and a label that follows the card uses `labelFor(item)`.
* A new card's starting values come from the Repeater's `newItem()`.
* The generated logo is the Image Picker's `fallback`, swapped with `setFallback`.
* A save rejects with an Error, and the Save Bar shows its message.
* A button that belongs to a Banner is one of its `actions`.

## Checklist

Run every new or changed string through this list.

* [ ] No em dashes, en dashes as punctuation, or semicolons.
* [ ] No colon at the end of a label, and none in a sentence unless it introduces a list.
* [ ] One sentence, or two when the second is the fix or the consequence.
* [ ] Sentences end with a period. Labels, buttons, and titles do not.
* [ ] Title Case for labels, buttons, titles, tabs, and badges. Sentence case for sentences.
* [ ] No "please", "successfully", exclamation marks, ALL CAPS, "(s)", or contractions.
* [ ] The word matches [Words to Use](#words-to-use).
* [ ] Base's typings were searched before any markup was built by hand.
* [ ] The component comes from Base, unchanged.
* [ ] Colors come from Base tokens and mean what [Meaning](#meaning) says.
* [ ] The docs use the same label.
