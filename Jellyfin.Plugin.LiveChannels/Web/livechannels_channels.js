import { createChipSelect, createConfigPage, createImagePicker, createMediaPicker, createRepeater, createSaveBar, createSliderField, createTranslations, confirmDialog, downloadJson, loadTranslations, pickJsonFile } from './configurationpage?name=livechannels_jpkribs_shared.js';
import { TRANSLATIONS_URL, createBlockEditors, loadRatings, migrateRatingBlocks, ratingInfo, readError, saveBarText, tabList, withText } from './configurationpage?name=livechannels_placeholder.js';

export default function (view) {
    'use strict';

    var PLUGIN_ID = 'ac6940fb-aac6-4de8-b622-55a662e23658';
    // The page's strings, empty until load reads them, so the markup keeps its English until then.
    var t = createTranslations({});

    var page = createConfigPage(view, {
        pluginId: PLUGIN_ID,
        tabsKey: 'livechannels',
        tabIndex: 0,
        tabs: function () { return tabList(t); },
        bind: bind,
        load: load
    });
    var Shared = page.shared;

    var MAX_LOGO_BYTES = 2 * 1024 * 1024;

    var channels = [];
    var currentIndex = -1;
    // The channel being edited: a deep COPY of channels[currentIndex]. Every editor control (fields, source
    // cards, rating blocks) works on this copy, and only Save writes it back, so saving channel B
    // can never persist half-made edits to channel A, and switching away discards consistently (with a
    // confirm when something would be lost).
    var editing = null;
    // The server's channel list as of the last load or successful save, serialised. A save first re-reads the
    // server copy and refuses when it differs, so a second tab (or admin) editing channels is not silently
    // overwritten with this page's older list.
    var serverChannels = '';
    // Compares the editor against its just-loaded state, for the unsaved-changes check.
    var editorTracker = Shared.createChangeTracker({ serialize: editorSnapshot });
    var currentEnabled = true;
    var logoData = '';
    var logoContentType = '';
    var libraries = [];        // [{ Id, Name }]
    var collections = [];      // [{ Id, Name }] box sets, for collection sources
    var ratings = ratingInfo([]);
    var logoPicker = null;     // the logo Image Picker
    var communitySlider = null;// Minimum community rating Slider
    var criticSlider = null;   // Minimum critic rating Slider
    var sourceList = null;     // the source card Repeater
    var sourceParts = new Map(); // a source card's custom control element to { key, handle, library }
    var genreCache = {};       // library id to a promise of its genre names
    var blockEditors = null;   // the rating and placeholder block card lists
    var saveBar = null;        // the channel Save Bar
    var studioPicker = null;   // channel-level studio Chip Select (created once)
    var peoplePicker = null;   // channel-level person Chip Select (created once)
    var audioLangPicker = null;// channel-level audio language Chip Select, single (created once)
    var cultures = [];         // cached [{ value: ISO code, label: name }] for the language choices

    function el(id) { return view.querySelector('#' + id); }

    // Parses the Years field into a sorted, de-duplicated list of production years. Accepts individual years and
    // ranges (e.g. "1990-1999, 2005" or "1990 1991 1992"), so a decade is two keystrokes rather than ten entries.
    // Anything outside a sane 1850-2200 band is dropped.
    function parseYears(text) {
        var set = {};
        function addRange(a, b) { if (a > b) { var t = a; a = b; b = t; } for (var y = a; y <= b; y++) set[y] = true; }
        (text || '').split(/[,\n]/).forEach(function (token) {
            token = token.trim();
            if (!token) return;
            var range = token.match(/^(\d{3,4})\s*-\s*(\d{3,4})$/);
            if (range) { addRange(parseInt(range[1], 10), parseInt(range[2], 10)); return; }
            token.split(/\s+/).forEach(function (part) {
                if (/^\d{3,4}$/.test(part)) set[parseInt(part, 10)] = true;
            });
        });
        return Object.keys(set).map(Number)
            .filter(function (n) { return n >= 1850 && n <= 2200; })
            .sort(function (a, b) { return a - b; });
    }

    // Renders a year list back into the compact field text, collapsing consecutive runs to ranges (e.g.
    // [1990..1999, 2005] -> "1990-1999, 2005"), so what was typed round-trips cleanly.
    function yearsToText(years) {
        if (!years || !years.length) return '';
        var sorted = years.slice().sort(function (a, b) { return a - b; });
        var parts = [], start = sorted[0], prev = sorted[0];
        for (var i = 1; i <= sorted.length; i++) {
            var cur = sorted[i];
            if (cur === prev + 1) { prev = cur; continue; }
            if (cur === prev) { continue; }
            parts.push(start === prev ? String(start) : (start + '-' + prev));
            start = cur; prev = cur;
        }
        return parts.join(', ');
    }

    function newId() {
        if (window.crypto && typeof window.crypto.randomUUID === 'function') return window.crypto.randomUUID();
        return 'ch-' + Date.now().toString(16) + '-' + Math.floor(Math.random() * 1e9).toString(16);
    }

    // MARK: Header

    function renderCounts() {
        var enabled = 0, disabled = 0;
        channels.forEach(function (ch) { if (ch.Enabled === false) disabled++; else enabled++; });
        Shared.renderCards('channelCounts', [
            { label: t.tr('Enabled'), count: enabled, color: 'green' },
            { label: t.tr('Disabled'), count: disabled, color: 'gray' }
        ]);
    }

    function renderSelect() {
        renderCounts();
        var select = el('selectChannel');
        // Display ordered by channel number (name as a tie-breaker), but each option's value stays the real
        // index into `channels` so selection still maps back correctly.
        select.innerHTML = channels
            .map(function (ch, i) { return { ch: ch, i: i }; })
            .sort(function (a, b) {
                var diff = (a.ch.Number || 0) - (b.ch.Number || 0);
                return diff !== 0 ? diff : (a.ch.Name || '').localeCompare(b.ch.Name || '');
            })
            .map(function (entry) {
                var ch = entry.ch;
                var name = ch.Name || t.tr('OptionNewChannel');
                var label = ch.Enabled === false ? t.format('OptionChannelDisabled', name) : name;
                if (ch.Number) label = ch.Enabled === false ? t.format('OptionChannelNumberedDisabled', ch.Number, name) : t.format('OptionChannelNumbered', ch.Number, name);
                return '<option value="' + entry.i + '">' + Shared.escapeHtml(label) + '</option>';
            }).join('');

        var has = channels.length > 0;
        Shared.setVisible('emptyState', !has);
        Shared.setVisible('channelEditor', has);
        Shared.setVisible('btnDeleteChannel', has);
        Shared.setVisible('btnEnable', has);

        if (has) {
            if (currentIndex < 0 || currentIndex >= channels.length) currentIndex = 0;
            select.value = String(currentIndex);
            loadEditor();
        } else {
            editing = null;
            editorTracker.markClean();
        }
    }

    function setEnableVisual(enabled) {
        var btn = el('btnEnable');
        btn.classList.toggle('primary', enabled);
        btn.classList.toggle('secondary', !enabled);
        el('enableLabel').textContent = enabled ? t.tr('Enabled') : t.tr('Disabled');
        btn.querySelector('.material-icons').textContent = enabled ? 'visibility' : 'visibility_off';
    }

    // searchStudios
    // Asks the server for studios whose name holds the text, so any studio is reachable.
    //
    // Inputs
    // term [string]: The typed text.
    // cb [function]: Receives the { value, label } choices.
    function searchStudios(term, cb) {
        ApiClient.getJSON(ApiClient.getUrl('Studios', { searchTerm: term, Recursive: true, Limit: 25 }))
            .then(function (res) { cb(((res && res.Items) || []).map(function (s) { return { value: s.Name, label: s.Name }; })); })
            .catch(function () { cb([]); });
    }

    // searchPeople
    // Asks the server for people whose name holds the text. The person id is the value and the name the label.
    //
    // Inputs
    // term [string]: The typed text.
    // cb [function]: Receives the { value, label } choices.
    function searchPeople(term, cb) {
        ApiClient.getJSON(ApiClient.getUrl('Persons', { searchTerm: term, Limit: 25, userId: ApiClient.getCurrentUserId() }))
            .then(function (res) { cb(((res && res.Items) || []).map(function (p) { return { value: p.Id, label: p.Name }; })); })
            .catch(function () { cb([]); });
    }

    // MARK: Logo

    // FNV-1a over the name's low bytes — matches the server (DefaultLogoService) so the preview's colour is
    // the same one the guide will show when there's no upload or poster.
    function logoHue(name) {
        var hash = 2166136261;
        for (var i = 0; i < (name || '').length; i++) {
            hash = Math.imul(hash ^ (name.charCodeAt(i) & 0xff), 16777619) >>> 0;
        }
        return hash % 360;
    }

    // Fits the title for the bottom strip, matching the server (DefaultLogoService.FitTitleLines): one line if
    // it fits, else two wrapped rows, else initials, else nothing.
    function fitTitleLines(name) {
        var MAX = 15;
        var title = (name || '').trim().toUpperCase();
        if (!title) { return []; }
        if (title.length <= MAX) { return [title]; }
        var words = title.split(/[\s\-_./:]+/).filter(Boolean);
        if (words.length >= 2) {
            var line1 = '', i = 0;
            while (i < words.length) {
                var cand = line1 ? line1 + ' ' + words[i] : words[i];
                if (cand.length <= MAX) { line1 = cand; i++; } else { break; }
            }
            if (line1 && i < words.length) {
                var line2 = words.slice(i).join(' ');
                if (line2.length <= MAX) { return [line1, line2]; }
            }
            var acr = '';
            for (var j = 0; j < words.length; j++) { acr += words[j].charAt(0).toUpperCase(); }
            if (acr.length >= 2 && acr.length <= MAX) { return [acr]; }
        }
        return [];
    }

    // Whether the Material Symbols font actually has a glyph for this ligature name. Draws it on a scratch
    // canvas and checks for any ink, so an unknown name falls back to the number in the preview like the server.
    function symbolRenders(symbol) {
        try {
            var px = 48;
            var c = document.createElement('canvas');
            c.width = px;
            c.height = px;
            var x = c.getContext('2d');
            x.font = Math.round(px * 0.7) + 'px "LiveChannelsSymbols"';
            x.textAlign = 'center';
            x.textBaseline = 'middle';
            x.fillStyle = '#000';
            x.fillText(symbol, px / 2, px / 2);
            var data = x.getImageData(0, 0, px, px).data;
            for (var i = 3; i < data.length; i += 4) { if (data[i] !== 0) { return true; } }
            return false;
        } catch (e) {
            return true; // If the check fails, assume valid and let the server decide.
        }
    }

    // A 1:1 pastel square (number centred, title on one or two bottom rows), drawn client-side so the box
    // updates live as the name/number change. Mirrors the server-generated logo.
    function defaultLogoDataUrl(number, name, style, symbol, showName) {
        var hue = logoHue(name);
        var size = 256;
        var fam = ' -apple-system, Segoe UI, Roboto, sans-serif';
        var canvas = document.createElement('canvas');
        canvas.width = size;
        canvas.height = size;
        var ctx = canvas.getContext('2d');
        ctx.fillStyle = 'hsl(' + hue + ', 65%, 82%)';
        ctx.fillRect(0, 0, size, size);
        ctx.fillStyle = 'hsl(' + hue + ', 50%, 32%)';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';

        if (style === 'Symbol' && symbol && symbolRenders(symbol)) {
            // The Material Symbols font renders the ligature name as its glyph. Symbols read a touch small at the
            // number's size, so draw them about 10% larger.
            ctx.font = Math.round(size * 0.44) + 'px "LiveChannelsSymbols"';
            ctx.fillText(symbol, size / 2, size / 2);
        } else {
            // Number style, or a symbol the font has no glyph for: show the number, matching the server.
            ctx.font = '700 ' + Math.round(size * 0.4) + 'px' + fam;
            ctx.fillText(String(number || 0), size / 2, size / 2);
        }

        if (showName) {
            var lines = fitTitleLines(name);
            if (lines.length) {
                ctx.textBaseline = 'alphabetic';
                ctx.font = '600 ' + Math.round(size / 13) + 'px' + fam;
                var bottom = size / 11;
                var lineH = size * 7 / 90;
                for (var k = 0; k < lines.length; k++) {
                    ctx.fillText(lines[k], size / 2, size - (bottom + (lines.length - 1 - k) * lineH));
                }
            }
        }
        return canvas.toDataURL('image/png');
    }

    // renderLogoPreview
    // Shows the uploaded logo in the Image Picker, with the generated logo as its fallback while nothing is
    // uploaded.
    function renderLogoPreview() {
        // The symbol field only applies to the generated 'Symbol' style.
        Shared.setVisible('logoSymbolRow', el('logoStyle').value === 'Symbol');
        if (!logoPicker) return;
        logoPicker.setFallback(defaultLogoDataUrl(
            parseInt(el('channelNumber').value, 10) || 0,
            el('channelName').value || '',
            el('logoStyle').value,
            el('logoSymbol').value,
            el('logoShowName').checked));
        logoPicker.setValue(logoData ? 'data:' + (logoContentType || 'image/png') + ';base64,' + logoData : null);
    }

    // onLogoPicked
    // Keeps the picked logo, which the Image Picker has already cropped to a square of at most 512 pixels.
    //
    // Inputs
    // value [object]: { type, base64 } from the Image Picker, or null after Remove.
    function onLogoPicked(value) {
        logoData = value ? value.base64 : '';
        logoContentType = value ? value.type : '';
        renderLogoPreview();
        markDirty();
    }

    // MARK: Sources

    function libraryName(id) {
        var lib = libraries.filter(function (l) { return l.Id === id; })[0];
        return lib ? lib.Name : t.tr('LabelLibrary');
    }

    function collectionName(id) {
        var col = collections.filter(function (c) { return c.Id === id; })[0];
        return col ? col.Name : '';
    }

    // episodeLabel
    // Names an item for the item picker, with an episode's series and number before its own name.
    //
    // Inputs
    // it [object]: The item from the server.
    //
    // Output
    // [string]: The name.
    function episodeLabel(it) {
        if (it.Type !== 'Episode') return it.Name || '';
        var s = it.ParentIndexNumber, n = it.IndexNumber;
        var code = (s != null ? 'S' + s : '') + (n != null ? 'E' + n : '');
        return [it.SeriesName, code, it.Name].filter(Boolean).join(' ');
    }

    // pickerItem
    // Shapes a server item as a Media Picker item.
    function pickerItem(it) {
        var out = { Id: it.Id, Name: episodeLabel(it), Type: it.Type, ProductionYear: it.ProductionYear };
        if (it.ImageTags) out.ImageTags = it.ImageTags;
        return out;
    }

    // searchSourceItems
    // Searches one library's shows, movies, and episodes for the item picker.
    //
    // Inputs
    // libraryId [string]: The source's library.
    // query [string]: The typed text.
    //
    // Output
    // [Promise<object[]>]: The Media Picker items.
    function searchSourceItems(libraryId, query) {
        if (!libraryId) return Promise.resolve([]);
        return ApiClient.getItems(ApiClient.getCurrentUserId(), {
            ParentId: libraryId, IncludeItemTypes: 'Series,Movie,Episode,MusicVideo,Video', Recursive: true,
            searchTerm: query, SortBy: 'SortName', SortOrder: 'Ascending',
            Limit: 20, Fields: 'ProductionYear', EnableTotalRecordCount: false
        }).then(function (res) { return ((res && res.Items) || []).map(pickerItem); });
    }

    // sourcePart
    // Wraps a Base control as a custom Repeater field, and remembers it so the card it sits in can reach it.
    //
    // Inputs
    // key [string]: The field key.
    // handle [object]: The Base control handle.
    // read [function]: Returns the field value.
    // write [function]: Sets the field value.
    //
    // Output
    // [object]: The custom field control.
    function sourcePart(key, handle, read, write) {
        var entry = { key: key, handle: handle, library: undefined };
        sourceParts.set(handle.element, entry);
        return {
            entry: entry,
            element: handle.element,
            input: handle.input || handle.element.querySelector('input'),
            getValue: read,
            setValue: write,
            destroy: function () { sourceParts.delete(handle.element); handle.destroy(); }
        };
    }

    // genreField
    // Renders a genre Chip Select for a source card.
    function genreField(key, placeholder) {
        return function (value, change) {
            var chip = createChipSelect({ placeholder: placeholder, value: value || [], noResultsText: t.tr('MessageNoGenresMatch'), onChange: function () { change(); } });
            return sourcePart(key, chip,
                function () { return chip.getValue().map(String); },
                function (v) { chip.setValue(v || []); });
        };
    }

    // itemsField
    // Renders the Media Picker a Whitelist or Blacklist source picks its items with, searching the card's library.
    function itemsField(value, change) {
        var part = null;
        var picker = createMediaPicker({
            value: value || [],
            itemTypes: ['Series', 'Movie', 'Episode', 'MusicVideo', 'Video'],
            placeholder: t.tr('PlaceholderSearchLibrary'),
            emptyText: t.tr('EmptyPickedItems'),
            typeLabels: { Series: t.tr('OptionShow'), Movie: t.tr('Movie'), Episode: t.tr('Episode'), MusicVideo: t.tr('MusicVideo'), Video: t.tr('OptionHomeVideo') },
            maxHeight: 360,
            searchFn: function (query) { return searchSourceItems(part ? part.entry.library : '', query); },
            onChange: function () { change(); }
        });
        part = sourcePart('Items', picker,
            function () { return picker.getValue(); },
            function (v) { picker.setValue(v || []); });
        return part;
    }

    // partsIn
    // Finds the custom controls of one source card by field key.
    function partsIn(card) {
        var out = {};
        sourceParts.forEach(function (entry, element) { if (card.contains(element)) out[entry.key] = entry; });
        return out;
    }

    // loadCardGenres
    // Reads a library's genres once, then answers from the cache.
    function loadCardGenres(libraryId) {
        if (!libraryId) return Promise.resolve([]);
        if (!genreCache[libraryId]) {
            genreCache[libraryId] = ApiClient.getJSON(ApiClient.getUrl('Genres', { ParentId: libraryId, IncludeItemTypes: 'Movie,Series,Episode,MusicVideo,Video', Recursive: true, Limit: 500, SortBy: 'SortName' }))
                .then(function (result) { return ((result && result.Items) || []).map(function (g) { return g.Name; }); })
                .catch(function () { delete genreCache[libraryId]; return []; });
        }
        return genreCache[libraryId];
    }

    // followLibrary
    // Points a card's genre and item pickers at its library. A changed library clears the included genres and the
    // picked items, which belong to the old one.
    function followLibrary(card, libraryId) {
        var parts = partsIn(card);
        var first = parts.Genres && parts.Genres.library === undefined;
        var moved = !first && parts.Genres && parts.Genres.library !== libraryId;
        if (!first && !moved) return;
        ['Genres', 'ExcludeGenres', 'Items'].forEach(function (key) { if (parts[key]) parts[key].library = libraryId; });
        if (moved) {
            if (parts.Genres) parts.Genres.handle.setValue([]);
            if (parts.Items) parts.Items.handle.setValue([]);
        }
        loadCardGenres(libraryId).then(function (names) {
            if (parts.Genres) parts.Genres.handle.setOptions(names);
            if (parts.ExcludeGenres) parts.ExcludeGenres.handle.setOptions(names);
        });
    }

    // isLibrary, isGenre, isPicked
    // Decide which fields a source card shows for its type and selection.
    function isLibrary(item) { return item.Kind !== 'Collection'; }
    function isGenre(item) { return isLibrary(item) && item.Selection === 'Genre'; }
    function isPicked(item) { return isLibrary(item) && (item.Selection === 'Whitelist' || item.Selection === 'Blacklist'); }

    // syncSources
    // Keeps each source card's pickers on its library.
    function syncSources() {
        var cards = sourceList.element.querySelectorAll('.base-repeater-card');
        var values = sourceList.getValue();
        if (cards.length !== values.length) return;
        for (var i = 0; i < cards.length; i++) followLibrary(cards[i], values[i].LibraryId || '');
    }

    // toSourceItem
    // Turns a stored source into the values of its card.
    function toSourceItem(s) {
        var ids = s.ItemIds || [];
        var known = (s.__items && s.__items.length === ids.length) ? s.__items : ids.map(function (id) { return { Id: id, Name: id }; });
        return {
            Kind: s.Kind || 'Library',
            LibraryId: s.LibraryId || '',
            CollectionId: s.CollectionId || '',
            Selection: s.Selection || 'AllContent',
            Genres: s.Genres || [],
            MatchAllGenres: !!s.MatchAllGenres,
            ExcludeGenres: s.ExcludeGenres || [],
            Items: known
        };
    }

    // fromSourceItem
    // Turns a card's values back into a stored source.
    function fromSourceItem(i) {
        var items = i.Items || [];
        return {
            Kind: i.Kind || 'Library',
            LibraryId: i.LibraryId || '',
            LibraryName: i.LibraryId ? libraryName(i.LibraryId) : '',
            CollectionId: i.CollectionId || '',
            CollectionName: i.CollectionId ? collectionName(i.CollectionId) : '',
            Genres: i.Genres || [],
            ExcludeGenres: i.ExcludeGenres || [],
            MatchAllGenres: !!i.MatchAllGenres,
            Selection: i.Selection || 'AllContent',
            ItemIds: items.map(function (it) { return it.Id; }),
            __items: items
        };
    }

    // ensureEditorParts
    // Creates the editor's Base controls once and mounts them: the filter Chip Selects, the logo Image Picker, the
    // rating Sliders, the source cards, the block cards, and the Save Bar.
    function ensureEditorParts() {
        if (!studioPicker) {
            studioPicker = createChipSelect({ placeholder: t.tr('PlaceholderSearchStudios'), searchFn: searchStudios, onChange: markDirty });
            el('studioHost').appendChild(studioPicker.element);
            peoplePicker = createChipSelect({ placeholder: t.tr('PlaceholderSearchPeople'), searchFn: searchPeople, onChange: markDirty });
            el('peopleHost').appendChild(peoplePicker.element);
            audioLangPicker = createChipSelect({ placeholder: t.tr('AnyLanguage'), options: cultures, single: true, onChange: markDirty });
            el('audioLanguageHost').appendChild(audioLangPicker.element);
        } else {
            audioLangPicker.setOptions(cultures);
        }
        if (logoPicker) return;

        logoPicker = createImagePicker({
            label: t.tr('Logo'),
            description: t.tr('HelpLogoUpload'),
            maxBytes: MAX_LOGO_BYTES,
            crop: true,
            maxSize: 512,
            chooseText: t.tr('Upload'),
            onChange: onLogoPicked
        });
        el('logoPickerHost').appendChild(logoPicker.element);

        communitySlider = createSliderField({ min: 0, max: 10, step: 0.1, label: t.tr('LabelMinCommunityRating'), onChange: markDirty });
        el('minCommunityRatingHost').appendChild(communitySlider.element);
        criticSlider = createSliderField({ min: 0, max: 100, step: 1, label: t.tr('LabelMinCriticRating'), onChange: markDirty });
        el('minCriticRatingHost').appendChild(criticSlider.element);

        sourceList = createRepeater({
            layout: 'card',
            titleText: t.tr('TitleKindSource'),
            addText: t.tr('ButtonAddSource'),
            emptyText: t.tr('EmptySources'),
            fields: [
                { key: 'Kind', label: t.tr('LabelSourceType'), type: 'select', options: [{ value: 'Library', label: t.tr('LabelLibrary') }, { value: 'Collection', label: t.tr('LabelCollection') }] },
                {
                    key: 'LibraryId', label: t.tr('LabelLibrary'), type: 'select', placeholder: t.tr('PlaceholderSelectLibrary'), visible: isLibrary,
                    options: libraries.map(function (l) { return { value: l.Id, label: l.Name }; }),
                    validate: function (value, item) { return item.Kind !== 'Collection' && !value ? t.tr('ErrorSelectLibrary') : ''; }
                },
                {
                    key: 'CollectionId', label: t.tr('LabelCollection'), type: 'select', placeholder: t.tr('PlaceholderSelectCollection'), visible: function (item) { return !isLibrary(item); },
                    options: collections.map(function (c) { return { value: c.Id, label: c.Name }; }),
                    validate: function (value, item) { return item.Kind === 'Collection' && !value ? t.tr('ErrorSelectCollection') : ''; }
                },
                {
                    key: 'Selection', label: t.tr('LabelSelection'), type: 'select', visible: isLibrary, options: [
                        { value: 'AllContent', label: t.tr('OptionAllContent') }, { value: 'Genre', label: t.tr('Genre') },
                        { value: 'Whitelist', label: t.tr('Whitelist') }, { value: 'Blacklist', label: t.tr('Blacklist') }]
                },
                { key: 'Genres', label: t.tr('LabelIncludeGenres'), type: 'custom', visible: isGenre, render: genreField('Genres', t.tr('PlaceholderAddGenre')) },
                { key: 'MatchAllGenres', label: t.tr('LabelMatchAllGenres'), type: 'toggle', visible: isGenre },
                { key: 'ExcludeGenres', label: t.tr('LabelExcludeGenres'), type: 'custom', visible: isGenre, render: genreField('ExcludeGenres', t.tr('PlaceholderExcludeGenre')) },
                {
                    key: 'Items', label: t.tr('LabelIncludedItems'), type: 'custom', visible: isPicked, render: itemsField,
                    labelFor: function (item) { return item.Selection === 'Blacklist' ? t.tr('LabelExcludedItems') : t.tr('LabelIncludedItems'); }
                }
            ],
            onChange: function () { syncSources(); markDirty(); }
        });
        el('librarySources').appendChild(sourceList.element);

        blockEditors = createBlockEditors({ ratings: ratings, t: t, onChange: markDirty });
        el('ratingBlocks').appendChild(blockEditors.ratingElement);
        el('placeholderBlocks').appendChild(blockEditors.placeholderElement);

        saveBar = createSaveBar(withText({
            fill: true,
            onSave: saveChannel
        }, saveBarText(t)));
        el('channelSaveBar').appendChild(saveBar.element);
    }

    // markDirty
    // Shows the Save Bar's unsaved line while the editor differs from what was loaded.
    function markDirty() {
        if (saveBar && editing) saveBar.setDirty(editorDirty());
    }

    // MARK: Reference data

    function loadLibraries() {
        return ApiClient.getJSON(ApiClient.getUrl('Library/VirtualFolders')).then(function (folders) {
            libraries = (folders || []).map(function (f) { return { Id: f.ItemId, Name: f.Name }; })
                .filter(function (l) { return l.Id; });
        }).catch(function () { /* leave empty */ });
    }

    function loadCollections() {
        return ApiClient.getItems(ApiClient.getCurrentUserId(), {
            IncludeItemTypes: 'BoxSet', Recursive: true, SortBy: 'SortName', SortOrder: 'Ascending', EnableTotalRecordCount: false
        }).then(function (res) {
            collections = ((res && res.Items) || []).map(function (c) { return { Id: c.Id, Name: c.Name }; })
                .filter(function (c) { return c.Id; });
        }).catch(function () { collections = []; });
    }

    // Loads the server's known cultures once into a cache of { value: ISO code, label: display name }, so the audio
    // language search can filter them locally (the value stored is the three-letter ISO code, matched against each
    // item's default audio track on the server).
    function loadLanguages() {
        return ApiClient.getJSON(ApiClient.getUrl('Localization/Cultures')).then(function (list) {
            var seen = {};
            cultures = (list || []).filter(function (c) {
                var code = c && c.ThreeLetterISOLanguageName;
                if (!code || seen[code]) return false;
                seen[code] = true; return true;
            }).map(function (c) {
                return { value: c.ThreeLetterISOLanguageName, label: c.DisplayName || c.Name };
            }).sort(function (a, b) { return a.label.localeCompare(b.label); });
        }).catch(function () { cultures = []; });
    }

    // The display name for a stored language code, falling back to the code itself when cultures are unavailable.
    function cultureLabel(code) {
        for (var i = 0; i < cultures.length; i++) { if (cultures[i].value === code) return cultures[i].label; }
        return code;
    }

    // Favouring only applies to a shuffled loop, so the favor block is nested under Shuffle and revealed with it;
    // the strength is nested under the type and revealed once a type other than None is chosen.
    function updateFavorControls() {
        el('favorGroup').classList.toggle('hidden', el('loopMode').value !== 'Shuffle');
        el('favorStrengthGroup').classList.toggle('hidden', el('favorKind').value === 'None');
    }

    // MARK: Editor load / save

    // Reads the editor back into a copy of the working channel and serialises it, so "what would be saved
    // right now" can be compared against what was loaded (the unsaved-changes check).
    function editorSnapshot() {
        if (!editing) return '';
        var probe = JSON.parse(JSON.stringify(editing));
        readEditorInto(probe);
        return JSON.stringify(probe);
    }

    function editorDirty() {
        return editing !== null && editorTracker.isDirty();
    }

    // Confirms discarding unsaved editor changes before an action that would lose them. Resolves to true to proceed.
    function confirmDiscard() {
        if (!editorDirty()) return Promise.resolve(true);
        var name = (el('channelName').value || '').trim();
        return confirmDialog(name ? t.format('ConfirmDiscardChannel', name) : t.tr('ConfirmDiscardNewChannel'), { title: t.tr('HeaderDiscardChanges'), confirmText: t.tr('ButtonDiscard'), destructive: true });
    }

    function loadEditor() {
        var stored = channels[currentIndex];
        if (!stored) { editing = null; editorTracker.markClean(); return; }

        // Deep-copy the stored channel, so nothing the editor does leaks into the saved working set until Save.
        editing = JSON.parse(JSON.stringify(stored));
        editing.Sources = editing.Sources || [];
        var ch = editing;
        ensureEditorParts();
        el('channelName').value = ch.Name || '';
        el('channelNumber').value = ch.Number || '';
        logoData = ch.LogoData || '';
        logoContentType = ch.LogoContentType || '';
        el('logoStyle').value = ch.LogoStyle || 'Number';
        el('logoSymbol').value = ch.LogoSymbol || '';
        el('logoShowName').checked = ch.LogoShowName !== false;
        renderLogoPreview();

        sourceList.setValue(ch.Sources.map(toSourceItem));
        syncSources();

        audioLangPicker.setValue(ch.AudioLanguage ? [{ value: ch.AudioLanguage, label: cultureLabel(ch.AudioLanguage) }] : []);
        ch.RatingBlocks = migrateRatingBlocks(ch);
        el('transitionWindow').value = ch.TransitionWindowMinutes || '';
        blockEditors.setValue(ch.RatingBlocks);
        el('category').value = ch.Category || 'None';
        el('years').value = yearsToText(ch.Years);
        communitySlider.setValue(ch.MinCommunityRating || 0);
        criticSlider.setValue(ch.MinCriticRating || 0);
        studioPicker.setValue((ch.Studios || []).map(function (s) { return { value: s, label: s }; }));
        peoplePicker.setValue((ch.People || []).map(function (p) { return { value: p.Id, label: p.Name }; }));
        el('episodesPerBlock').value = ch.EpisodesPerBlock || 1;
        el('keepMultiPart').checked = ch.KeepMultiPartTogether !== false;
        el('showBumpers').checked = ch.ShowBumpers !== false;
        el('includeEpisodes').checked = ch.IncludeEpisodes !== false;
        el('includeMovies').checked = ch.IncludeMovies !== false;
        el('includeSpecials').checked = !!ch.IncludeSpecials;
        el('includeMusicVideos').checked = ch.IncludeMusicVideos !== false;
        el('includeHomeVideos').checked = !!ch.IncludeHomeVideos;
        el('loopMode').value = ch.LoopMode || (ch.Shuffle === false ? 'Alphabetical' : 'Shuffle');
        el('episodeOrder').value = ch.ShuffleEpisodes ? 'random' : 'air';
        el('favorKind').value = ch.FavorKind || 'None';
        el('favorStrength').value = ch.FavorStrength || 'Moderate';
        updateFavorControls();
        el('subtitleBurnIn').value = ch.SubtitleBurnIn || 'Never';

        currentEnabled = ch.Enabled !== false;
        setEnableVisual(currentEnabled);

        // Everything above (including the pickers) is now in its just-loaded state; snapshot it as the
        // baseline the unsaved-changes check compares against.
        editorTracker.markClean();
        saveBar.setDirty(false);
    }

    function readEditorInto(ch) {
        ch.Name = el('channelName').value.trim();
        ch.Number = parseInt(el('channelNumber').value, 10) || 0;
        ch.LogoData = logoData;
        ch.LogoContentType = logoContentType;
        ch.LogoStyle = el('logoStyle').value;
        ch.LogoSymbol = el('logoSymbol').value.trim();
        ch.LogoShowName = el('logoShowName').checked;
        var audioLang = audioLangPicker ? audioLangPicker.getValue() : [];
        ch.AudioLanguage = audioLang.length ? String(audioLang[0]) : '';
        // The blocks are authoritative, so neutralise the legacy single-band fields to keep them from double-applying.
        ch.RatingBlocks = blockEditors ? blockEditors.getValue() : (ch.RatingBlocks || []);
        ch.TransitionWindowMinutes = Math.min(1440, Math.max(0, parseInt(el('transitionWindow').value, 10) || 0));
        ch.MinOfficialRating = '';
        ch.MaxOfficialRating = '';
        ch.IncludeUnrated = true;
        ch.Category = el('category').value;
        ch.Years = parseYears(el('years').value);
        var minCommunity = communitySlider ? Number(communitySlider.getValue()) : (ch.MinCommunityRating || 0);
        ch.MinCommunityRating = isNaN(minCommunity) ? 0 : Math.min(10, Math.max(0, Math.round(minCommunity * 10) / 10));
        var minCritic = criticSlider ? Number(criticSlider.getValue()) : (ch.MinCriticRating || 0);
        ch.MinCriticRating = isNaN(minCritic) ? 0 : Math.min(100, Math.max(0, minCritic));
        ch.Studios = studioPicker ? studioPicker.getValue().map(String) : (ch.Studios || []);
        ch.People = peoplePicker ? peoplePicker.getItems().map(function (p) { return { Id: String(p.value), Name: p.label }; }) : (ch.People || []);
        ch.EpisodesPerBlock = Math.max(1, parseInt(el('episodesPerBlock').value, 10) || 1);
        ch.KeepMultiPartTogether = el('keepMultiPart').checked;
        ch.ShowBumpers = el('showBumpers').checked;
        ch.IncludeEpisodes = el('includeEpisodes').checked;
        ch.IncludeMovies = el('includeMovies').checked;
        ch.IncludeSpecials = el('includeSpecials').checked;
        ch.IncludeMusicVideos = el('includeMusicVideos').checked;
        ch.IncludeHomeVideos = el('includeHomeVideos').checked;
        ch.LoopMode = el('loopMode').value;
        ch.Shuffle = ch.LoopMode === 'Shuffle';
        ch.ShuffleEpisodes = el('episodeOrder').value === 'random';
        ch.FavorKind = el('favorKind').value;
        ch.FavorStrength = el('favorStrength').value;
        ch.SubtitleBurnIn = el('subtitleBurnIn').value;
        ch.Enabled = currentEnabled;
        ch.Sources = sourceList ? sourceList.getValue().map(fromSourceItem) : (ch.Sources || []);
    }

    // MARK: Persistence

    // saveChannel
    // Checks the editor and saves the channel. The Save Bar runs it and shows its outcome, including the reason
    // a rejected save gives.
    //
    // Output
    // [Promise]: Settles once the save is done.
    function saveChannel() {
        if (currentIndex < 0 || !editing) return Promise.resolve();
        var ch = editing;
        readEditorInto(ch);
        if (!ch.Name) return Promise.reject(new Error(t.tr('ErrorNameRequired')));
        if (!ch.Sources.length) return Promise.reject(new Error(t.tr('ErrorAddSource')));
        if (!sourceList.isValid() || !blockEditors.isValid()) return Promise.reject(new Error(t.tr('ErrorFixMarkedFields')));

        // The copy is complete and valid: persist a working set with it in place, and adopt that set only once
        // the server accepts it, so a rejected save leaves the stored channels (and the editor) as they were.
        var candidate = channels.slice();
        candidate[currentIndex] = JSON.parse(JSON.stringify(ch));
        return persist(candidate, t.tr('ErrorCouldNotSaveChannel'));
    }

    function stripInternal(list) {
        return list.map(function (ch) {
            var copy = {};
            Object.keys(ch).forEach(function (k) { if (k !== '__items') copy[k] = ch[k]; });
            copy.Sources = (ch.Sources || []).map(function (s) {
                var sc = {};
                Object.keys(s).forEach(function (k) { if (k !== '__items') sc[k] = s[k]; });
                return sc;
            });
            return copy;
        });
    }

    // Trigger Jellyfin's built-in guide refresh so channel changes propagate to Live TV (re-pull the
    // channels and guide) without the user having to run it by hand, and, when any channel has a placeholder
    // block, the buffer cards task so a changed placeholder card is rendered before its window.
    function refreshLiveTv() {
        return ApiClient.getScheduledTasks().then(function (tasks) {
            var task = (tasks || []).filter(function (t) { return t.Key === 'RefreshGuide'; })[0];
            var cards = (tasks || []).filter(function (t) { return t.Key === 'LiveChannelsGenerateBufferCards'; })[0];
            var started = [];
            if (task) started.push(ApiClient.startScheduledTask(task.Id));
            if (cards && (channels || []).some(function (c) { return (c.RatingBlocks || []).some(function (b) { return b.IsPlaceholder; }); })) started.push(ApiClient.startScheduledTask(cards.Id));
            return Promise.all(started);
        }).catch(function () { /* best effort */ });
    }

    // Saves `candidate` as the channel list. `channels` is replaced with it only after the server accepts the
    // save; on failure nothing local changes, so the next save does not resend a rejected list. The promise
    // rejects with an Error whose message says why.
    function persist(candidate, errMessage, newIndex) {
        return Shared.getConfig().then(function (fresh) {
            if (JSON.stringify(fresh.Channels || []) !== serverChannels) {
                var conflict = new Error(t.tr('ErrorChannelsChangedElsewhere'));
                conflict.conflict = true;
                throw conflict;
            }
            fresh.Channels = stripInternal(candidate);
            return Shared.saveConfig(fresh);
        }).then(function () {
            channels = candidate;
            if (newIndex !== undefined) currentIndex = newIndex;
            renderSelect();
            refreshLiveTv();
            // Re-read the stored list as the new baseline (the server may normalise what was sent).
            return Shared.getConfig().then(function (cfg) { serverChannels = JSON.stringify(cfg.Channels || []); }).catch(function () {});
        }, function (err) {
            if (err && err.conflict) throw err;
            // Surface the server's validation message (like the settings page does) instead of a bare failure.
            return readError(err, errMessage).then(function (message) { throw new Error(message); });
        });
    }

    function nextNumber() {
        var max = 0;
        channels.forEach(function (ch) { if (ch.Number > max) max = ch.Number; });
        return max + 1;
    }

    // MARK: Import / export
    //
    // Move channels between servers (e.g. a low-power box and a fast one) without re-creating them by hand.
    // Export writes every channel -- filters, appearance, loop behaviour, and the Base64 logo -- to a self-contained
    // JSON file. Import merges that file back: a channel whose Number matches an existing one replaces it (so the
    // same export re-imported updates in place instead of duplicating), and any other channel is added. Library
    // *content* still has to exist on the target server; the IDs of hand-picked items only resolve if those items
    // are present, but library/genre/rating filters carry over as-is.

    function exportChannels() {
        var payload = {
            type: 'livechannels-export',
            version: 1,
            exportedAt: new Date().toISOString(),
            channels: stripInternal(channels)
        };
        var name = 'live-channels-' + new Date().toISOString().slice(0, 10) + '.json';
        downloadJson(name, payload);
        Shared.setStatus('ioStatus', channels.length === 1 ? t.format('MessageExportedOne', name) : t.format('MessageExportedMany', channels.length, name), false);
    }

    // Finds the index of the first channel in `list` with the given number, or -1.
    function indexByNumber(list, number) {
        for (var i = 0; i < list.length; i++) { if ((list[i].Number || 0) === (number || 0)) return i; }
        return -1;
    }

    function importChannels(parsed) {
        var incoming = Array.isArray(parsed) ? parsed
            : (parsed && Array.isArray(parsed.channels) ? parsed.channels : null);
        if (!incoming) { Shared.setStatus('ioStatus', t.tr('ErrorImportNoChannels'), true); return; }

        // Validate the shape before touching the working set, so a bad file changes nothing.
        var clean = [];
        for (var i = 0; i < incoming.length; i++) {
            var ch = incoming[i];
            if (!ch || typeof ch !== 'object' || typeof ch.Name !== 'string' || !Array.isArray(ch.Sources)) {
                Shared.setStatus('ioStatus', t.tr('ErrorImportNotExport'), true);
                return;
            }
            clean.push(ch);
        }
        if (!clean.length) { Shared.setStatus('ioStatus', t.tr('MessageNoChannelsToImport'), true); return; }

        // Merge into a copy of the working set; it replaces `channels` only once the save succeeds.
        var candidate = channels.slice();
        var added = 0, replaced = 0;
        clean.forEach(function (ic) {
            delete ic.__items;
            ic.Sources = (ic.Sources || []).map(function (s) { delete s.__items; return s; });
            ic.Id = ic.Id || newId();

            var idx = indexByNumber(candidate, ic.Number);
            if (idx >= 0) {
                ic.Id = candidate[idx].Id || ic.Id; // keep the slot's existing stable id
                candidate[idx] = ic;
                replaced++;
            } else {
                // Avoid colliding with the id of a channel we are keeping.
                var collides = candidate.some(function (c) { return c.Id === ic.Id; });
                if (collides) ic.Id = newId();
                candidate.push(ic);
                added++;
            }
        });

        // Resolve item names for any hand-picked sources so the editor reads cleanly, then save + refresh Live TV.
        hydrateItemNames(candidate).then(function () {
            var message = clean.length === 1 ? t.format('MessageImportedOne', added, replaced) : t.format('MessageImportedMany', clean.length, added, replaced);
            persist(candidate, t.tr('ErrorCouldNotSaveImport'), candidate.length ? 0 : -1).then(function () {
                Shared.setStatus('ioStatus', message, false);
            }, function (err) { Shared.setStatus('ioStatus', err.message, true); });
        });
    }

    function onImportClick() {
        pickJsonFile().then(function (parsed) {
            if (parsed === null) return; // picker cancelled
            return confirmDiscard().then(function (ok) { if (ok) importChannels(parsed); });
        }, function (err) {
            var unreadable = err && err.message === 'The file could not be read.';
            Shared.setStatus('ioStatus', unreadable ? t.tr('ErrorCouldNotReadFile') : t.tr('ErrorImportNotJson'), true);
        });
    }

    function addChannel() {
        confirmDiscard().then(function (ok) {
            if (ok) addBlankChannel();
        });
    }

    function addBlankChannel() {
        channels.push({
            Id: newId(), Name: '', Number: nextNumber(), LogoData: '', LogoContentType: '',
            LogoStyle: 'Number', LogoSymbol: '', LogoShowName: true,
            Sources: [], AudioLanguage: '', RatingBlocks: [], TransitionWindowMinutes: 0, MinOfficialRating: '', MaxOfficialRating: '', IncludeUnrated: true, Category: 'None',
            EpisodesPerBlock: 1, KeepMultiPartTogether: true, ShowBumpers: true,
            IncludeEpisodes: true, IncludeMovies: true, IncludeSpecials: false, IncludeMusicVideos: true, IncludeHomeVideos: false, Shuffle: true, LoopMode: 'Shuffle', ShuffleEpisodes: false,
            FavorKind: 'None', FavorStrength: 'Moderate', SubtitleBurnIn: 'Never', Enabled: true
        });
        currentIndex = channels.length - 1;
        renderSelect();
        el('channelName').focus();
    }

    function deleteChannel() {
        var ch = channels[currentIndex];
        if (!ch) return;
        var question = ch.Name ? t.format('ConfirmDeleteChannel', ch.Name) : t.tr('ConfirmDeleteNewChannel');
        confirmDialog(question, { title: t.tr('HeaderDeleteChannel'), confirmText: t.tr('Delete'), destructive: true }).then(function (ok) {
            // Delete the channel that was asked about, even if the list shifted while the dialog was open.
            var index = channels.indexOf(ch);
            if (!ok || index < 0) return;
            var candidate = channels.slice();
            candidate.splice(index, 1);
            persist(candidate, t.tr('ErrorCouldNotDeleteChannel'), -1).then(function () {
                Shared.setStatus('ioStatus', t.tr('MessageDeletedRefreshing'), false);
            }, function (err) { Shared.setStatus('ioStatus', err.message, true); });
        });
    }

    // MARK: Hydration

    function hydrateItemNames(list) {
        var ids = [];
        list.forEach(function (ch) {
            (ch.Sources || []).forEach(function (s) {
                s.__items = [];
                (s.ItemIds || []).forEach(function (id) { ids.push(id); });
            });
        });
        if (!ids.length) return Promise.resolve();
        return ApiClient.getItems(ApiClient.getCurrentUserId(), {
            Ids: ids.join(','), Fields: 'ProductionYear', Limit: ids.length, EnableTotalRecordCount: false
        }).then(function (result) {
            var byId = {};
            ((result && result.Items) || []).forEach(function (it) {
                byId[it.Id] = pickerItem(it);
            });
            list.forEach(function (ch) {
                (ch.Sources || []).forEach(function (s) {
                    s.__items = (s.ItemIds || []).map(function (id) { return byId[id] || { Id: id, Name: id }; });
                });
            });
        }).catch(function () { /* names fall back to ids */ });
    }

    // MARK: Wiring

    function bind() {
        el('selectChannel').addEventListener('change', function () {
            var select = this;
            var next = parseInt(select.value, 10);
            if (next === currentIndex) return;
            confirmDiscard().then(function (ok) {
                if (!ok) { select.value = String(currentIndex); return; }
                currentIndex = next;
                loadEditor();
            });
        });
        // Keep the placeholder in step with the name (colour) and number (label) while there's no upload.
        el('channelNumber').addEventListener('input', function () { if (!logoData) renderLogoPreview(); });
        el('channelName').addEventListener('input', function () { if (!logoData) renderLogoPreview(); });
        el('logoStyle').addEventListener('change', renderLogoPreview);
        el('logoSymbol').addEventListener('input', function () { if (!logoData) renderLogoPreview(); });
        el('logoShowName').addEventListener('change', function () { if (!logoData) renderLogoPreview(); });
        el('favorKind').addEventListener('change', updateFavorControls);
        el('loopMode').addEventListener('change', updateFavorControls);
        el('btnNewChannel').addEventListener('click', addChannel);
        el('btnDeleteChannel').addEventListener('click', deleteChannel);
        el('btnExportChannels').addEventListener('click', exportChannels);
        el('btnImportChannels').addEventListener('click', onImportClick);
        el('btnEnable').addEventListener('click', function () { currentEnabled = !currentEnabled; setEnableVisual(currentEnabled); markDirty(); });
        // Any edit in the editor shows the Save Bar's unsaved line until it matches what was loaded again.
        el('channelEditor').addEventListener('input', markDirty);
        el('channelEditor').addEventListener('change', markDirty);

        // Warm the Material Symbols web font (the same glyphs the server draws), then refresh the preview so a
        // symbol logo renders once it has loaded rather than briefly falling back to the number.
        if (document.fonts && document.fonts.load) {
            document.fonts.load('40px "LiveChannelsSymbols"').then(function () { if (!logoData) renderLogoPreview(); }).catch(function () {});
        }
    }

    function load() {
        Shared.initCollapsibles();
        Promise.all([loadLibraries(), loadCollections(), loadRatings(), loadLanguages(), loadTranslations(TRANSLATIONS_URL)]).then(function (results) {
            ratings = results[2];
            t = results[4];
            t.apply(view);
            return Shared.getConfig();
        }).then(function (cfg) {
            serverChannels = JSON.stringify(cfg.Channels || []);
            channels = (cfg.Channels || []).map(function (ch) {
                ch.Sources = ch.Sources || [];
                return ch;
            });
            return hydrateItemNames(channels);
        }).then(function () {
            currentIndex = channels.length ? 0 : -1;
            renderSelect();
        }).catch(function () {
            Shared.setStatus('ioStatus', t.tr('ErrorCouldNotLoadChannels'), true);
        });
    }
}
