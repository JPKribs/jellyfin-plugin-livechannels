export default function (view) {
    'use strict';

    var PLUGIN_ID = 'ac6940fb-aac6-4de8-b622-55a662e23658';
    var TABS = [
        { href: 'configurationpage?name=livechannels_channels', name: 'Channels' },
        { href: 'configurationpage?name=livechannels_popular', name: 'Popular' },
        { href: 'configurationpage?name=livechannels_sessions', name: 'Sessions' },
        { href: 'configurationpage?name=livechannels_settings', name: 'Settings' }
    ];

    var Shared = null;
    var setTabs = null;
    var _sharedPromise = import('/web/configurationpage?name=livechannels_jpkribs_shared.js').then(function (mod) {
        Shared = mod.createShared(view, PLUGIN_ID);
        setTabs = mod.setTabs;
    });

    var MAX_LOGO_BYTES = 2 * 1024 * 1024;

    var config = null;
    var channels = [];
    var currentIndex = -1;
    var currentEnabled = true;
    var logoData = '';
    var logoContentType = '';
    var libraries = [];        // [{ Id, Name }]
    var collections = [];      // [{ Id, Name }] box sets, for collection sources
    var ratings = [];          // [{ Name, Value }]
    var ratingOptions = '';    // cached <option> html for the per-block rating selects
    var studioPicker = null;   // channel-level studio typeahead picker (created once)
    var peoplePicker = null;   // channel-level person typeahead picker (created once)
    var audioLangPicker = null;// channel-level audio-language single-select typeahead (created once)
    var cultures = [];         // cached [{ key: ISO code, label: name }] for the language search
    var _bound = false;

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
        el('channelCounts').innerHTML =
            '<div class="jpk-card green"><span class="jpk-card-count">' + enabled + '</span><span class="jpk-card-label">Enabled</span></div>' +
            '<div class="jpk-card gray"><span class="jpk-card-count">' + disabled + '</span><span class="jpk-card-label">Disabled</span></div>';
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
                var label = (ch.Number ? ch.Number + '. ' : '') + (ch.Name || 'new channel') + (ch.Enabled === false ? ', disabled' : '');
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
        }
    }

    function setEnableVisual(enabled) {
        var btn = el('btnEnable');
        btn.classList.toggle('jpk-button-submit', enabled);
        btn.classList.toggle('jpk-secondary', !enabled);
        el('enableLabel').textContent = enabled ? 'Enabled' : 'Disabled';
        btn.querySelector('.material-icons').textContent = enabled ? 'visibility' : 'visibility_off';
    }

    // MARK: Chip select (genres)

    function createChipSelect(options) {
        options = options || {};
        var available = [];
        var selected = [];

        var wrap = document.createElement('div');
        wrap.className = 'jpk-chip-select';
        // Build the emby-select through innerHTML (exactly how the source cards do it) so the customized built-in
        // upgrades to the styled component. Do NOT use document.createElement('select', { is: 'emby-select' }) here:
        // that path throws "toLowerCase is not a function" inside Jellyfin's webcomponents polyfill and takes the
        // whole editor down with it.
        var pickerHost = document.createElement('div');
        pickerHost.innerHTML = '<select class="jpk-selector-dropdown"></select>';
        var picker = pickerHost.querySelector('select');
        var chips = document.createElement('div');
        chips.className = 'jpk-tags';
        // Selected chips sit above the picker (like UserManagement's tags).
        wrap.appendChild(chips);
        wrap.appendChild(picker);

        function emit() { if (options.onChange) options.onChange(selected.slice()); }

        function renderChips() {
            chips.innerHTML = '';
            selected.forEach(function (name, index) {
                var tag = document.createElement('span');
                tag.className = 'jpk-tag';
                var label = document.createElement('span');
                label.textContent = name;
                var remove = document.createElement('span');
                remove.className = 'jpk-tag-remove';
                remove.textContent = '×';
                remove.title = 'Remove';
                remove.addEventListener('click', function () { selected.splice(index, 1); renderChips(); renderPicker(); emit(); });
                tag.appendChild(label);
                tag.appendChild(remove);
                chips.appendChild(tag);
            });
        }

        function renderPicker() {
            var html = '<option value="">' + Shared.escapeHtml(options.placeholder || 'Add…') + '</option>';
            available.forEach(function (name) {
                if (selected.indexOf(name) < 0) html += '<option value="' + Shared.escapeHtml(name) + '">' + Shared.escapeHtml(name) + '</option>';
            });
            picker.innerHTML = html;
            picker.value = '';
        }

        picker.addEventListener('change', function () {
            var name = picker.value;
            if (name && selected.indexOf(name) < 0) { selected.push(name); renderChips(); emit(); }
            renderPicker();
        });

        renderChips();
        renderPicker();

        return {
            element: wrap,
            getValue: function () { return selected.slice(); },
            setValue: function (v) { selected = (v || []).slice(); renderChips(); renderPicker(); },
            setAvailable: function (v) { available = (v || []).slice(); renderPicker(); }
        };
    }

    // MARK: Search chip picker (studios, people)

    // A typeahead chip picker for sets that are awkward in a dropdown (studios run to the thousands, people more,
    // languages are a long fixed list): it searches as you type and stores the chosen { key, label } pairs as chips.
    // options.search(term) resolves to an array of { key, label }; options.placeholder labels the box; options.single
    // keeps at most one selection (for single-value fields like a language). The caller maps key/label to its own
    // storage shape (studios store the name as the key; people store the person id; languages store the ISO code).
    function createSearchChips(options) {
        options = options || {};
        var selected = []; // [{ key, label }]

        var wrap = document.createElement('div');
        wrap.className = 'jpk-chip-select';
        var chips = document.createElement('div');
        chips.className = 'jpk-tags';
        var searchHost = document.createElement('div');
        searchHost.innerHTML = '<input type="text" is="emby-input" class="emby-input" autocomplete="off" />';
        var search = searchHost.querySelector('input');
        search.placeholder = options.placeholder || 'Search…';
        var results = document.createElement('div');
        results.className = 'jpk-table';
        results.style.display = 'none';
        wrap.appendChild(chips);
        wrap.appendChild(search);
        wrap.appendChild(results);

        function renderChips() {
            chips.innerHTML = '';
            selected.forEach(function (item, index) {
                var tag = document.createElement('span');
                tag.className = 'jpk-tag';
                var label = document.createElement('span');
                label.textContent = item.label;
                var remove = document.createElement('span');
                remove.className = 'jpk-tag-remove';
                remove.textContent = '×';
                remove.title = 'Remove';
                remove.addEventListener('click', function () { selected.splice(index, 1); renderChips(); });
                tag.appendChild(label);
                tag.appendChild(remove);
                chips.appendChild(tag);
            });
        }

        function hideResults() { results.innerHTML = ''; results.style.display = 'none'; }

        function has(key) { return selected.some(function (s) { return s.key === key; }); }

        function add(item) {
            if (item.key != null && item.key !== '') {
                if (options.single) { selected = [{ key: item.key, label: item.label }]; renderChips(); }
                else if (!has(item.key)) { selected.push({ key: item.key, label: item.label }); renderChips(); }
            }
            search.value = '';
            hideResults();
        }

        function runSearch(term) {
            Promise.resolve(options.search(term)).then(function (rows) {
                results.innerHTML = '';
                rows = rows || [];
                if (!rows.length) { results.style.display = 'none'; return; }
                rows.forEach(function (row) {
                    var el = document.createElement('div');
                    el.className = 'jpk-table-row';
                    el.style.cursor = 'pointer';
                    el.textContent = row.label;
                    el.addEventListener('click', function () { add(row); });
                    results.appendChild(el);
                });
                results.style.display = '';
            }).catch(hideResults);
        }

        var timer = null;
        search.addEventListener('input', function () {
            if (timer) clearTimeout(timer);
            var term = search.value.trim();
            if (!term) { hideResults(); return; }
            timer = setTimeout(function () { runSearch(term); }, 300);
        });

        renderChips();

        return {
            element: wrap,
            getValue: function () { return selected.map(function (s) { return { key: s.key, label: s.label }; }); },
            setValue: function (v) {
                selected = (v || []).filter(function (s) { return s && s.key != null && s.key !== ''; })
                    .map(function (s) { return { key: s.key, label: s.label }; });
                renderChips();
                hideResults();
                if (search) search.value = '';
            }
        };
    }

    // Studio search: the server filters by name, so any studio is reachable (no truncated alphabetical list).
    function searchStudios(term) {
        return ApiClient.getJSON(ApiClient.getUrl('Studios', { searchTerm: term, Recursive: true, Limit: 25 }))
            .then(function (res) { return ((res && res.Items) || []).map(function (s) { return { key: s.Name, label: s.Name }; }); });
    }

    // People search: stores the person id as the key (matched by id at resolve time) and the name as the label.
    function searchPeople(term) {
        return ApiClient.getJSON(ApiClient.getUrl('Persons', { searchTerm: term, Limit: 25, userId: ApiClient.getCurrentUserId() }))
            .then(function (res) { return ((res && res.Items) || []).map(function (p) { return { key: p.Id, label: p.Name }; }); });
    }

    // Creates (once) the channel-level studio and people typeahead pickers and mounts them into the editor.
    function ensureFilterPickers() {
        if (!studioPicker) {
            studioPicker = createSearchChips({ placeholder: 'Search studios…', search: searchStudios });
            var studioMount = view.querySelector('.lc-studio-mount');
            if (studioMount) studioMount.appendChild(studioPicker.element);
        }

        if (!peoplePicker) {
            peoplePicker = createSearchChips({ placeholder: 'Search people…', search: searchPeople });
            var peopleMount = view.querySelector('.lc-people-mount');
            if (peopleMount) peopleMount.appendChild(peoplePicker.element);
        }

        if (!audioLangPicker) {
            audioLangPicker = createSearchChips({ placeholder: 'Any language', search: searchLanguages, single: true });
            var audioMount = view.querySelector('.lc-audiolang-mount');
            if (audioMount) audioMount.appendChild(audioLangPicker.element);
        }
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

    // A 1:1 pastel square (number centred, title on one or two bottom rows), drawn client-side so the box
    // updates live as the name/number change. Mirrors the server-generated logo.
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

    function renderLogoPreview() {
        var img = el('logoPreview');
        // The symbol field only applies to the generated 'Symbol' style.
        Shared.setVisible('logoSymbolRow', el('logoStyle').value === 'Symbol');
        if (logoData) {
            img.src = 'data:' + (logoContentType || 'image/png') + ';base64,' + logoData;
            Shared.setVisible('btnClearLogo', true);
        } else {
            // No upload: show the generated placeholder so the box is never empty.
            img.src = defaultLogoDataUrl(
                parseInt(el('channelNumber').value, 10) || 0,
                el('channelName').value || '',
                el('logoStyle').value,
                el('logoSymbol').value,
                el('logoShowName').checked);
            Shared.setVisible('btnClearLogo', false);
        }
        Shared.setVisible('logoPreview', true);
    }

    function onLogoFile(e) {
        var file = e.target.files && e.target.files[0];
        if (!file) return;
        if (file.size > MAX_LOGO_BYTES) { Shared.setStatus('channelStatus', 'Image is larger than 2 MB.', true); e.target.value = ''; return; }
        var reader = new FileReader();
        reader.onload = function () {
            var img = new Image();
            img.onload = function () {
                // Always store the logo as a centre-cropped 1:1 square (capped at 512px) so it renders
                // consistently in the guide regardless of the uploaded aspect ratio.
                var side = Math.min(img.naturalWidth, img.naturalHeight);
                var size = Math.min(side, 512);
                var canvas = document.createElement('canvas');
                canvas.width = size;
                canvas.height = size;
                var ctx = canvas.getContext('2d');
                ctx.drawImage(img, (img.naturalWidth - side) / 2, (img.naturalHeight - side) / 2, side, side, 0, 0, size, size);
                var dataUrl = canvas.toDataURL('image/png');
                var comma = dataUrl.indexOf(',');
                logoData = comma >= 0 ? dataUrl.slice(comma + 1) : '';
                logoContentType = 'image/png';
                renderLogoPreview();
            };
            img.onerror = function () { Shared.setStatus('channelStatus', 'Could not read that image.', true); };
            img.src = String(reader.result || '');
        };
        reader.readAsDataURL(file);
        e.target.value = '';
    }

    function clearLogo() { logoData = ''; logoContentType = ''; renderLogoPreview(); }

    // MARK: Library source cards

    function libraryName(id) {
        var lib = libraries.filter(function (l) { return l.Id === id; })[0];
        return lib ? lib.Name : 'Library';
    }

    function collectionName(id) {
        var col = collections.filter(function (c) { return c.Id === id; })[0];
        return col ? col.Name : '';
    }

    function newSource(libraryId) {
        return { Kind: 'Library', LibraryId: libraryId || '', LibraryName: libraryId ? libraryName(libraryId) : '', CollectionId: '', CollectionName: '', Genres: [], ExcludeGenres: [], MatchAllGenres: false, Selection: 'AllContent', ItemIds: [], __items: [] };
    }

    function renderSources() {
        var host = el('librarySources');
        host.innerHTML = '';
        var ch = channels[currentIndex];
        if (!ch) return;
        if (!ch.Sources.length) {
            host.innerHTML = '<div class="jpk-empty-section">No libraries yet. Add one below.</div>';
            return;
        }
        ch.Sources.forEach(function (source, index) { host.appendChild(buildCard(source, index)); });
    }

    // Builds one library source card, mirroring ServerSync's mapping card: a header (library + Remove), a
    // Genres section, then a Selection (all content / whitelist / blacklist) with an item picker. Uses
    // emby-input/emby-select via innerHTML so the fields render natively, then wires behaviour by query.
    function buildCard(source, index) {
        var card = document.createElement('div');
        card.className = 'lc-source';
        card.innerHTML =
            '<div class="lc-source-header">' +
                '<span class="material-icons lc-source-icon" aria-hidden="true">folder</span>' +
                '<span class="lc-source-title">Library source</span>' +
                '<button is="emby-button" type="button" class="lc-remove raised jpk-icon-btn jpk-button-destructive" title="Remove"><span class="material-icons" aria-hidden="true">delete</span><span>Remove</span></button>' +
            '</div>' +
            '<div class="selectContainer">' +
                '<label class="selectLabel">Source type</label>' +
                '<select class="lc-source-kind jpk-selector-dropdown">' +
                    '<option value="Library">Library</option>' +
                    '<option value="Collection">Collection</option>' +
                '</select>' +
            '</div>' +
            '<div class="lc-library-fields">' +
                '<div class="selectContainer">' +
                    '<label class="selectLabel">Library</label>' +
                    '<select class="lc-source-library jpk-selector-dropdown"></select>' +
                '</div>' +
                '<div class="inputContainer">' +
                    '<label class="inputLabel">Selection</label>' +
                    '<select class="lc-selection jpk-selector-dropdown">' +
                        '<option value="AllContent">All content</option>' +
                        '<option value="Genre">Genre</option>' +
                        '<option value="Whitelist">Whitelist</option>' +
                        '<option value="Blacklist">Blacklist</option>' +
                    '</select>' +
                '</div>' +
                '<div class="filterSection lc-genres">' +
                    '<h3 class="jpk-subsection-title">Genres</h3>' +
                    '<label class="inputLabel">Include</label>' +
                    '<div class="lc-genre-mount"></div>' +
                    '<label class="emby-checkbox-label lc-matchall-label"><input type="checkbox" is="emby-checkbox" class="lc-matchall" /><span class="checkboxLabel">Match all genres</span></label>' +
                    '<div class="fieldDescription">On, content must carry every included genre. Off, any one is enough.</div>' +
                    '<label class="inputLabel">Exclude</label>' +
                    '<div class="lc-exclude-mount"></div>' +
                    '<div class="fieldDescription">Content carrying any of these genres is never included, even if it matched above. Series level genres apply to their episodes.</div>' +
                '</div>' +
                '<div class="filterSection lc-items">' +
                    '<input is="emby-input" type="text" class="lc-search" placeholder="Search this library…" />' +
                    '<div class="jpk-table-item-count lc-count"></div>' +
                    '<div class="jpk-table-body lc-list"></div>' +
                '</div>' +
            '</div>' +
            '<div class="lc-collection-fields">' +
                '<div class="selectContainer">' +
                    '<label class="selectLabel">Collection</label>' +
                    '<select class="lc-source-collection jpk-selector-dropdown"></select>' +
                '</div>' +
                '<div class="fieldDescription">Every item in this collection, with series expanded to their episodes. The channel-wide filters still apply.</div>' +
            '</div>';

        // Library selector — the source's library is chosen here, inside the card.
        var libSelect = card.querySelector('.lc-source-library');
        libSelect.innerHTML = '<option value="">Select a library…</option>' + libraries.map(function (l) {
            return '<option value="' + Shared.escapeHtml(l.Id) + '">' + Shared.escapeHtml(l.Name) + '</option>';
        }).join('');
        libSelect.value = source.LibraryId || '';

        card.querySelector('.lc-remove').addEventListener('click', function () {
            channels[currentIndex].Sources.splice(index, 1);
            renderSources();
        });

        // Selection (all content / genre / whitelist / blacklist)
        var selection = card.querySelector('.lc-selection');
        selection.value = source.Selection || 'AllContent';

        // Genres (shown for the genre selection): an include list and an exclude (blacklist) list, both fed the
        // same available genres for the library.
        var genresSection = card.querySelector('.lc-genres');
        var chip = createChipSelect({ placeholder: 'Add a genre…', onChange: function (vals) { source.Genres = vals; } });
        chip.setValue(source.Genres || []);
        card.querySelector('.lc-genre-mount').appendChild(chip.element);
        var excludeChip = createChipSelect({ placeholder: 'Add a genre to exclude…', onChange: function (vals) { source.ExcludeGenres = vals; } });
        excludeChip.setValue(source.ExcludeGenres || []);
        card.querySelector('.lc-exclude-mount').appendChild(excludeChip.element);
        function reloadGenres() { loadCardGenres(source.LibraryId).then(function (names) { chip.setAvailable(names); excludeChip.setAvailable(names); }); }
        reloadGenres();
        var matchAll = card.querySelector('.lc-matchall');
        matchAll.checked = !!source.MatchAllGenres;
        matchAll.addEventListener('change', function () { source.MatchAllGenres = matchAll.checked; });

        // Items (shown for whitelist/blacklist)
        var itemsSection = card.querySelector('.lc-items');
        var search = card.querySelector('.lc-search');
        var count = card.querySelector('.lc-count');
        var list = card.querySelector('.lc-list');

        function updateCount() {
            var n = (source.ItemIds || []).length;
            count.textContent = n + ' selected' + (source.Selection === 'Blacklist' ? ' (excluded)' : '');
        }

        function thumb(id) {
            var img = document.createElement('img');
            img.className = 'jpk-table-row-thumb jpk-table-row-thumb-portrait';
            img.loading = 'lazy';
            img.src = ApiClient.getUrl('Items/' + id + '/Images/Primary', { maxHeight: 120 });
            img.addEventListener('error', function () {
                var ph = document.createElement('div');
                ph.className = 'jpk-table-row-thumb-placeholder';
                ph.innerHTML = '<span class="material-icons">movie</span>';
                if (img.parentNode) img.parentNode.replaceChild(ph, img);
            });
            return img;
        }

        // Builds a checkable row on the base table-row component. `indent` nests episode rows under a show.
        function makeRow(it, name, meta, indent) {
            var selected = (source.ItemIds || []).indexOf(it.Id) >= 0;
            var row = document.createElement('div');
            row.className = 'jpk-table-row' + (selected ? ' selected' : '');
            if (indent) { row.style.paddingLeft = '36px'; }

            row.appendChild(thumb(it.Id));

            var info = document.createElement('div');
            info.className = 'jpk-table-cell jpk-table-item-info';
            var nm = document.createElement('div');
            nm.className = 'jpk-table-item-title';
            nm.textContent = name;
            var mt = document.createElement('div');
            mt.className = 'jpk-table-item-sub';
            mt.textContent = meta;
            info.appendChild(nm);
            info.appendChild(mt);
            row.appendChild(info);

            var checkCell = document.createElement('div');
            checkCell.className = 'jpk-table-cell jpk-table-cell-checkbox';
            var cb = document.createElement('input');
            cb.type = 'checkbox';
            cb.className = 'jpk-table-row-checkbox';
            cb.checked = selected;
            checkCell.appendChild(cb);
            row.appendChild(checkCell);

            function set(on) {
                var arr = source.ItemIds || (source.ItemIds = []);
                var i = arr.indexOf(it.Id);
                if (on && i < 0) { arr.push(it.Id); }
                else if (!on && i >= 0) { arr.splice(i, 1); }
                cb.checked = on;
                row.classList.toggle('selected', on);
                updateCount();
            }
            cb.addEventListener('click', function (e) { e.stopPropagation(); set(cb.checked); });
            row.addEventListener('click', function () { set(!cb.checked); });

            return { row: row, checkCell: checkCell };
        }

        function showMeta(it) {
            var parts = [];
            if (it.ProductionYear) { parts.push(it.ProductionYear); }
            parts.push(it.Type === 'Series' ? 'Show' : (it.Type || 'Movie'));
            return parts.join(' • ');
        }

        function episodeMeta(ep) {
            var s = ep.ParentIndexNumber;
            var n = ep.IndexNumber;
            return (s != null ? 'S' + s : '') + (n != null ? 'E' + n : '') || 'Episode';
        }

        // Adds an expand control to a show row so its episodes can be picked individually.
        function addExpander(it, built) {
            var expand = document.createElement('button');
            expand.className = 'jpk-row-btn';
            expand.type = 'button';
            expand.title = 'Pick episodes';
            expand.innerHTML = '<span class="material-icons" aria-hidden="true">expand_more</span>';
            built.row.insertBefore(expand, built.checkCell);

            var host = null;
            expand.addEventListener('click', function (e) {
                e.stopPropagation();
                if (host) {
                    host.parentNode.removeChild(host);
                    host = null;
                    expand.querySelector('.material-icons').textContent = 'expand_more';
                    return;
                }
                expand.querySelector('.material-icons').textContent = 'expand_less';
                host = document.createElement('div');
                built.row.parentNode.insertBefore(host, built.row.nextSibling);
                host.innerHTML = '<div class="jpk-table-loading-more">Loading episodes…</div>';
                ApiClient.getItems(ApiClient.getCurrentUserId(), {
                    ParentId: it.Id, IncludeItemTypes: 'Episode', Recursive: true,
                    SortBy: 'ParentIndexNumber,IndexNumber', SortOrder: 'Ascending',
                    Fields: 'ParentIndexNumber,IndexNumber', Limit: 500, EnableTotalRecordCount: false
                }).then(function (res) {
                    var eps = (res && res.Items) || [];
                    host.innerHTML = '';
                    if (!eps.length) { host.innerHTML = '<div class="jpk-table-empty">No episodes.</div>'; return; }
                    eps.forEach(function (ep) { host.appendChild(makeRow(ep, ep.Name || '', episodeMeta(ep), true).row); });
                }).catch(function () { host.innerHTML = '<div class="jpk-table-empty">Failed to load episodes.</div>'; });
            });
        }

        function renderList(items) {
            if (!items.length) { list.innerHTML = '<div class="jpk-table-empty">No matches.</div>'; return; }
            list.innerHTML = '';
            items.forEach(function (it) {
                var built = makeRow(it, it.Name || '', showMeta(it), false);
                if (it.Type === 'Series') { addExpander(it, built); }
                list.appendChild(built.row);
            });
        }

        var loaded = false;
        function loadList(term) {
            if (!source.LibraryId) { list.innerHTML = '<div class="jpk-table-empty">Select a library first.</div>'; return; }
            list.innerHTML = '<div class="jpk-table-loading-more">Loading…</div>';
            ApiClient.getItems(ApiClient.getCurrentUserId(), {
                ParentId: source.LibraryId, IncludeItemTypes: 'Series,Movie', Recursive: true,
                searchTerm: term || undefined, SortBy: 'SortName', SortOrder: 'Ascending',
                Limit: 200, Fields: 'ProductionYear', EnableTotalRecordCount: false
            }).then(function (result) {
                loaded = true;
                renderList((result && result.Items) || []);
            }).catch(function () { list.innerHTML = '<div class="jpk-table-empty">Search failed.</div>'; });
        }

        var searchTimer = null;
        search.addEventListener('input', function () {
            if (searchTimer) clearTimeout(searchTimer);
            searchTimer = setTimeout(function () { loadList(search.value.trim()); }, 300);
        });

        updateCount();

        function applySelection() {
            source.Selection = selection.value;
            genresSection.style.display = source.Selection === 'Genre' ? '' : 'none';
            var showItems = source.Selection === 'Whitelist' || source.Selection === 'Blacklist';
            itemsSection.style.display = showItems ? '' : 'none';
            if (showItems && !loaded) loadList('');
        }
        selection.addEventListener('change', applySelection);
        applySelection();

        // Source type (library vs collection): toggles which fields show, plus the card's icon and title.
        var kindSelect = card.querySelector('.lc-source-kind');
        kindSelect.value = source.Kind || 'Library';
        var libraryFields = card.querySelector('.lc-library-fields');
        var collectionFields = card.querySelector('.lc-collection-fields');
        var titleEl = card.querySelector('.lc-source-title');
        var iconEl = card.querySelector('.lc-source-icon');

        var colSelect = card.querySelector('.lc-source-collection');
        colSelect.innerHTML = '<option value="">Select a collection…</option>' + collections.map(function (c) {
            return '<option value="' + Shared.escapeHtml(c.Id) + '">' + Shared.escapeHtml(c.Name) + '</option>';
        }).join('');
        colSelect.value = source.CollectionId || '';
        colSelect.addEventListener('change', function () {
            source.CollectionId = colSelect.value;
            source.CollectionName = collectionName(source.CollectionId);
        });

        function applyKind() {
            var isCollection = kindSelect.value === 'Collection';
            libraryFields.style.display = isCollection ? 'none' : '';
            collectionFields.style.display = isCollection ? '' : 'none';
            titleEl.textContent = isCollection ? 'Collection source' : 'Library source';
            iconEl.textContent = isCollection ? 'video_library' : 'folder';
            if (!isCollection) applySelection();
        }
        kindSelect.addEventListener('change', function () {
            source.Kind = kindSelect.value;
            applyKind();
        });
        applyKind();

        // Changing the library resets this source's library-specific selections and reloads its data.
        libSelect.addEventListener('change', function () {
            source.LibraryId = libSelect.value;
            source.LibraryName = libraryName(source.LibraryId);
            source.Genres = [];
            source.ItemIds = [];
            chip.setValue([]);
            reloadGenres();
            loaded = false;
            list.innerHTML = '';
            updateCount();
            if (selection.value === 'Whitelist' || selection.value === 'Blacklist') loadList('');
        });

        return card;
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

    function loadCardGenres(libraryId) {
        if (!libraryId) return Promise.resolve([]);
        return ApiClient.getJSON(ApiClient.getUrl('Genres', { ParentId: libraryId, IncludeItemTypes: 'Movie,Series,Episode', Recursive: true, Limit: 500, SortBy: 'SortName' }))
            .then(function (result) { return ((result && result.Items) || []).map(function (g) { return g.Name; }); })
            .catch(function () { return []; });
    }

    function loadRatings() {
        return ApiClient.getJSON(ApiClient.getUrl('Localization/ParentalRatings')).then(function (list) {
            var seen = {};
            ratings = [];
            (list || []).forEach(function (r) {
                var score = r.Value;
                if (score === undefined && r.RatingScore) score = r.RatingScore.Score;
                if (r && r.Name && !seen[r.Name]) { seen[r.Name] = true; ratings.push({ Name: r.Name, Value: score || 0 }); }
            });
            ratings.sort(function (a, b) { return a.Value - b.Value; });
            var opts = ratings.map(function (r) {
                return '<option value="' + Shared.escapeHtml(r.Name) + '">' + Shared.escapeHtml(r.Name) + '</option>';
            }).join('');
            ratingOptions = opts;
            el('kidsRating').innerHTML = '<option value="">None</option>' + opts;
        }).catch(function () {
            ratingOptions = '';
            el('kidsRating').innerHTML = '<option value="">None</option>';
        });
    }

    // Loads the server's known cultures once into a cache of { key: ISO code, label: display name }, so the audio
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
                return { key: c.ThreeLetterISOLanguageName, label: c.DisplayName || c.Name };
            }).sort(function (a, b) { return a.label.localeCompare(b.label); });
        }).catch(function () { cultures = []; });
    }

    // Filters the cached cultures by name for the language typeahead.
    function searchLanguages(term) {
        var lower = term.toLowerCase();
        return cultures.filter(function (c) { return c.label.toLowerCase().indexOf(lower) >= 0; }).slice(0, 25);
    }

    // The display name for a stored language code, falling back to the code itself when cultures are unavailable.
    function cultureLabel(code) {
        for (var i = 0; i < cultures.length; i++) { if (cultures[i].key === code) return cultures[i].label; }
        return code;
    }

    // Keep the rating band coherent: the minimum can never exceed the maximum. When the user picks one end past
    // the other, drag the other end to meet it so an impossible (empty) band can't be created.
    function ratingValue(name) {
        if (!name) return null;
        for (var i = 0; i < ratings.length; i++) { if (ratings[i].Name === name) return ratings[i].Value; }
        return null;
    }

    function coerceBand(minEl, maxEl, changed) {
        var mn = ratingValue(minEl.value), mx = ratingValue(maxEl.value);
        if (mn === null || mx === null || mn <= mx) return;
        if (changed === 'min') { maxEl.value = minEl.value; } else { minEl.value = maxEl.value; }
    }

    // MARK: Rating blocks (time-of-day rating limits)

    function pad2(n) { return (n < 10 ? '0' : '') + n; }

    function minutesToTime(m) {
        m = ((m % 1440) + 1440) % 1440;
        return pad2(Math.floor(m / 60)) + ':' + pad2(m % 60);
    }

    function timeToMinutes(text) {
        var parts = (text || '').split(':');
        var mins = ((parseInt(parts[0], 10) || 0) * 60) + (parseInt(parts[1], 10) || 0);
        return ((mins % 1440) + 1440) % 1440;
    }

    function newRatingBlock() {
        return { MinOfficialRating: '', MaxOfficialRating: '', IncludeUnrated: true, Period: 'AllDay', StartMinutes: 0, EndMinutes: 0 };
    }

    // Existing channels store a single band in the legacy fields; show it as one all-day block so it stays visible
    // and editable. A channel already using blocks keeps them.
    function migrateRatingBlocks(ch) {
        if (ch.RatingBlocks && ch.RatingBlocks.length) return ch.RatingBlocks;
        if (ch.MinOfficialRating || ch.MaxOfficialRating || ch.IncludeUnrated === false) {
            return [{ MinOfficialRating: ch.MinOfficialRating || '', MaxOfficialRating: ch.MaxOfficialRating || '', IncludeUnrated: ch.IncludeUnrated !== false, Period: 'AllDay', StartMinutes: 0, EndMinutes: 0 }];
        }
        return [];
    }

    function renderRatingBlocks() {
        var host = el('ratingBlocks');
        host.innerHTML = '';
        var ch = channels[currentIndex];
        if (!ch) return;
        ch.RatingBlocks = ch.RatingBlocks || [];
        if (!ch.RatingBlocks.length) {
            host.innerHTML = '<div class="jpk-empty-section">No rating blocks. Any rating airs at any time. Add one to restrict by rating or time of day.</div>';
            return;
        }
        ch.RatingBlocks.forEach(function (block, index) { host.appendChild(buildBlockCard(block, index)); });
    }

    // Builds one rating-block card: min/max rating, include-unrated, and an all-day/custom period whose start and
    // end times are revealed only for a custom window. Fields mutate the block object live, like the source cards.
    function buildBlockCard(block, index) {
        var card = document.createElement('div');
        card.className = 'lc-ratingblock';
        card.innerHTML =
            '<div class="lc-source-header">' +
                '<span class="material-icons lc-source-icon" aria-hidden="true">schedule</span>' +
                '<span class="lc-block-title">Rating block</span>' +
                '<button is="emby-button" type="button" class="lc-remove raised jpk-icon-btn jpk-button-destructive" title="Remove"><span class="material-icons" aria-hidden="true">delete</span><span>Remove</span></button>' +
            '</div>' +
            '<div class="selectContainer"><label class="selectLabel">Minimum age rating</label>' +
                '<select class="lc-block-min jpk-selector-dropdown"><option value="">No minimum</option>' + ratingOptions + '</select></div>' +
            '<div class="selectContainer"><label class="selectLabel">Maximum age rating</label>' +
                '<select class="lc-block-max jpk-selector-dropdown"><option value="">No limit</option>' + ratingOptions + '</select></div>' +
            '<div class="checkboxContainer"><label class="emby-checkbox-label">' +
                '<input type="checkbox" is="emby-checkbox" class="lc-block-unrated" /><span class="checkboxLabel">Include unrated</span></label></div>' +
            '<div class="selectContainer"><label class="selectLabel">Period</label>' +
                '<select class="lc-block-period jpk-selector-dropdown"><option value="AllDay">All day</option><option value="Custom">Custom</option></select></div>' +
            '<div class="lc-block-times">' +
                '<div class="inputContainer"><label class="inputLabel">Start time</label><input is="emby-input" type="time" class="lc-block-start" /></div>' +
                '<div class="inputContainer"><label class="inputLabel">End time</label><input is="emby-input" type="time" class="lc-block-end" /></div>' +
            '</div>';

        var min = card.querySelector('.lc-block-min');
        var max = card.querySelector('.lc-block-max');
        var unrated = card.querySelector('.lc-block-unrated');
        var period = card.querySelector('.lc-block-period');
        var times = card.querySelector('.lc-block-times');
        var start = card.querySelector('.lc-block-start');
        var end = card.querySelector('.lc-block-end');

        min.value = block.MinOfficialRating || '';
        max.value = block.MaxOfficialRating || '';
        unrated.checked = block.IncludeUnrated !== false;
        period.value = block.Period || 'AllDay';
        start.value = minutesToTime(block.StartMinutes || 0);
        end.value = minutesToTime(block.EndMinutes || 0);

        function syncTimes() { times.classList.toggle('hidden', period.value !== 'Custom'); }
        syncTimes();

        min.addEventListener('change', function () { coerceBand(min, max, 'min'); block.MinOfficialRating = min.value; block.MaxOfficialRating = max.value; });
        max.addEventListener('change', function () { coerceBand(min, max, 'max'); block.MinOfficialRating = min.value; block.MaxOfficialRating = max.value; });
        unrated.addEventListener('change', function () { block.IncludeUnrated = unrated.checked; });
        period.addEventListener('change', function () { block.Period = period.value; syncTimes(); });
        start.addEventListener('change', function () { block.StartMinutes = timeToMinutes(start.value); });
        end.addEventListener('change', function () { block.EndMinutes = timeToMinutes(end.value); });

        card.querySelector('.lc-remove').addEventListener('click', function () {
            channels[currentIndex].RatingBlocks.splice(index, 1);
            renderRatingBlocks();
        });

        return card;
    }

    // Favouring only applies to a shuffled loop, so the favor block is nested under Shuffle and revealed with it;
    // the strength is nested under the type and revealed once a type other than None is chosen.
    function updateFavorControls() {
        el('favorGroup').classList.toggle('hidden', el('loopMode').value !== 'Shuffle');
        el('favorStrengthGroup').classList.toggle('hidden', el('favorKind').value === 'None');
    }

    // MARK: Editor load / save

    function loadEditor() {
        var ch = channels[currentIndex];
        if (!ch) return;
        ensureFilterPickers();
        el('channelName').value = ch.Name || '';
        el('channelNumber').value = ch.Number || '';
        logoData = ch.LogoData || '';
        logoContentType = ch.LogoContentType || '';
        el('logoStyle').value = ch.LogoStyle || 'Number';
        el('logoSymbol').value = ch.LogoSymbol || '';
        el('logoShowName').checked = ch.LogoShowName !== false;
        renderLogoPreview();

        renderSources();

        audioLangPicker.setValue(ch.AudioLanguage ? [{ key: ch.AudioLanguage, label: cultureLabel(ch.AudioLanguage) }] : []);
        ch.RatingBlocks = migrateRatingBlocks(ch);
        el('transitionWindow').value = ch.TransitionWindowMinutes || '';
        renderRatingBlocks();
        el('kidsRating').value = ch.KidsRatingThreshold || '';
        el('years').value = yearsToText(ch.Years);
        el('minCommunityRating').value = ch.MinCommunityRating || '';
        el('minCriticRating').value = ch.MinCriticRating || '';
        studioPicker.setValue((ch.Studios || []).map(function (s) { return { key: s, label: s }; }));
        peoplePicker.setValue((ch.People || []).map(function (p) { return { key: p.Id, label: p.Name }; }));
        el('episodesPerBlock').value = ch.EpisodesPerBlock || 1;
        el('keepMultiPart').checked = ch.KeepMultiPartTogether !== false;
        el('includeSpecials').checked = !!ch.IncludeSpecials;
        el('includeHomeVideos').checked = !!ch.IncludeHomeVideos;
        el('loopMode').value = ch.LoopMode || (ch.Shuffle === false ? 'Alphabetical' : 'Shuffle');
        el('episodeOrder').value = ch.ShuffleEpisodes ? 'random' : 'air';
        el('favorKind').value = ch.FavorKind || 'None';
        el('favorStrength').value = ch.FavorStrength || 'Moderate';
        updateFavorControls();
        el('subtitleBurnIn').value = ch.SubtitleBurnIn || 'Never';

        currentEnabled = ch.Enabled !== false;
        setEnableVisual(currentEnabled);
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
        ch.AudioLanguage = audioLang.length ? audioLang[0].key : '';
        // Rating blocks are mutated live on the cards; the transition window is read here. The blocks are now
        // authoritative, so neutralise the legacy single-band fields to keep them from double-applying.
        ch.RatingBlocks = ch.RatingBlocks || [];
        ch.TransitionWindowMinutes = Math.max(0, parseInt(el('transitionWindow').value, 10) || 0);
        ch.MinOfficialRating = '';
        ch.MaxOfficialRating = '';
        ch.IncludeUnrated = true;
        ch.KidsRatingThreshold = el('kidsRating').value;
        ch.Years = parseYears(el('years').value);
        var minCommunity = parseFloat(el('minCommunityRating').value);
        ch.MinCommunityRating = isNaN(minCommunity) ? 0 : Math.min(10, Math.max(0, minCommunity));
        var minCritic = parseFloat(el('minCriticRating').value);
        ch.MinCriticRating = isNaN(minCritic) ? 0 : Math.min(100, Math.max(0, minCritic));
        ch.Studios = studioPicker ? studioPicker.getValue().map(function (s) { return s.key; }) : (ch.Studios || []);
        ch.People = peoplePicker ? peoplePicker.getValue().map(function (p) { return { Id: p.key, Name: p.label }; }) : (ch.People || []);
        ch.EpisodesPerBlock = Math.max(1, parseInt(el('episodesPerBlock').value, 10) || 1);
        ch.KeepMultiPartTogether = el('keepMultiPart').checked;
        ch.IncludeSpecials = el('includeSpecials').checked;
        ch.IncludeHomeVideos = el('includeHomeVideos').checked;
        ch.LoopMode = el('loopMode').value;
        ch.Shuffle = ch.LoopMode === 'Shuffle';
        ch.ShuffleEpisodes = el('episodeOrder').value === 'random';
        ch.FavorKind = el('favorKind').value;
        ch.FavorStrength = el('favorStrength').value;
        ch.SubtitleBurnIn = el('subtitleBurnIn').value;
        ch.Enabled = currentEnabled;
        // Sources are mutated live by the cards; keep the display names in sync.
        ch.Sources.forEach(function (s) {
            if (s.Kind === 'Collection') { s.CollectionName = collectionName(s.CollectionId); }
            else { s.LibraryName = libraryName(s.LibraryId); }
        });
    }

    // MARK: Persistence

    function saveChannel() {
        var ch = channels[currentIndex];
        if (!ch) return;
        readEditorInto(ch);
        if (!ch.Name) { Shared.setStatus('channelStatus', 'A name is required.', true); return; }
        if (!ch.Sources.length) { Shared.setStatus('channelStatus', 'Add at least one library source.', true); return; }
        if (ch.Sources.some(function (s) { return s.Kind === 'Collection' ? !s.CollectionId : !s.LibraryId; })) { Shared.setStatus('channelStatus', 'Pick a library or collection for each source.', true); return; }
        persist('channelStatus', 'Saved.', 'Save failed.');
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
    // channels and guide) without the user having to run it by hand.
    function refreshLiveTv() {
        return ApiClient.getScheduledTasks().then(function (tasks) {
            var task = (tasks || []).filter(function (t) { return t.Key === 'RefreshGuide'; })[0];
            if (task) return ApiClient.startScheduledTask(task.Id);
        }).catch(function () { /* best effort */ });
    }

    function persist(statusId, okMessage, errMessage) {
        return Shared.getConfig().then(function (fresh) {
            fresh.Channels = stripInternal(channels);
            config = fresh;
            return Shared.saveConfig(fresh);
        }).then(function () {
            renderSelect();
            refreshLiveTv();
            Shared.setStatus(statusId, okMessage + ' Refreshing Live TV…', false);
        }).catch(function () {
            Shared.setStatus(statusId, errMessage, true);
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
        var blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' });
        var url = URL.createObjectURL(blob);
        var a = document.createElement('a');
        a.href = url;
        a.download = name;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
        Shared.setStatus('ioStatus', 'Exported ' + channels.length + ' channel' + (channels.length === 1 ? '' : 's') + ' to ' + name + '.', false);
    }

    // Finds the index of the first channel with the given number, or -1.
    function indexByNumber(number) {
        for (var i = 0; i < channels.length; i++) { if ((channels[i].Number || 0) === (number || 0)) return i; }
        return -1;
    }

    function importChannelsFromText(text) {
        var parsed;
        try { parsed = JSON.parse(text); }
        catch (e) { Shared.setStatus('ioStatus', 'Import failed: the file is not valid JSON.', true); return; }

        var incoming = Array.isArray(parsed) ? parsed
            : (parsed && Array.isArray(parsed.channels) ? parsed.channels : null);
        if (!incoming) { Shared.setStatus('ioStatus', 'Import failed: no channels found in the file.', true); return; }

        // Validate the shape before touching the working set, so a bad file changes nothing.
        var clean = [];
        for (var i = 0; i < incoming.length; i++) {
            var ch = incoming[i];
            if (!ch || typeof ch !== 'object' || typeof ch.Name !== 'string' || !Array.isArray(ch.Sources)) {
                Shared.setStatus('ioStatus', 'Import failed: this does not look like a Live Channels export.', true);
                return;
            }
            clean.push(ch);
        }
        if (!clean.length) { Shared.setStatus('ioStatus', 'Nothing to import: the file contains no channels.', true); return; }

        var added = 0, replaced = 0;
        clean.forEach(function (ic) {
            delete ic.__items;
            ic.Sources = (ic.Sources || []).map(function (s) { delete s.__items; return s; });
            ic.Id = ic.Id || newId();

            var idx = indexByNumber(ic.Number);
            if (idx >= 0) {
                ic.Id = channels[idx].Id || ic.Id; // keep the slot's existing stable id
                channels[idx] = ic;
                replaced++;
            } else {
                // Avoid colliding with the id of a channel we are keeping.
                var collides = channels.some(function (c) { return c.Id === ic.Id; });
                if (collides) ic.Id = newId();
                channels.push(ic);
                added++;
            }
        });

        currentIndex = channels.length ? 0 : -1;
        // Resolve item names for any hand-picked sources so the editor reads cleanly, then save + refresh Live TV.
        hydrateItemNames().then(function () {
            persist('ioStatus', 'Imported ' + clean.length + ' channel' + (clean.length === 1 ? '' : 's') + ' (' + added + ' added, ' + replaced + ' replaced).', 'Import failed to save.');
        });
    }

    function onImportFile(e) {
        var file = e.target.files && e.target.files[0];
        e.target.value = ''; // let the same file be chosen again
        if (!file) return;
        var reader = new FileReader();
        reader.onload = function () { importChannelsFromText(String(reader.result || '')); };
        reader.onerror = function () { Shared.setStatus('ioStatus', 'Import failed: could not read the file.', true); };
        reader.readAsText(file);
    }

    function addChannel() {
        channels.push({
            Id: newId(), Name: '', Number: nextNumber(), LogoData: '', LogoContentType: '',
            LogoStyle: 'Number', LogoSymbol: '', LogoShowName: true,
            Sources: [], AudioLanguage: '', RatingBlocks: [], TransitionWindowMinutes: 0, MinOfficialRating: '', MaxOfficialRating: '', IncludeUnrated: true, KidsRatingThreshold: 'G',
            EpisodesPerBlock: 1, KeepMultiPartTogether: true,
            IncludeSpecials: false, IncludeHomeVideos: false, Shuffle: true, LoopMode: 'Shuffle', ShuffleEpisodes: false,
            FavorKind: 'None', FavorStrength: 'Moderate', SubtitleBurnIn: 'Never', Enabled: true
        });
        currentIndex = channels.length - 1;
        renderSelect();
        el('channelName').focus();
    }

    function deleteChannel() {
        var ch = channels[currentIndex];
        if (!ch) return;
        if (!window.confirm('Delete channel "' + (ch.Name || 'this channel') + '"? This cannot be undone.')) return;
        channels.splice(currentIndex, 1);
        currentIndex = -1;
        persist('channelStatus', 'Deleted.', 'Delete failed.');
    }

    function addLibrary() {
        var ch = channels[currentIndex];
        if (!ch) return;
        ch.Sources.push(newSource(''));
        renderSources();
    }

    // MARK: Hydration

    function hydrateItemNames() {
        var ids = [];
        channels.forEach(function (ch) {
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
                byId[it.Id] = it.Name + (it.ProductionYear ? ' (' + it.ProductionYear + ')' : '') + (it.Type === 'Series' ? ' (show)' : '');
            });
            channels.forEach(function (ch) {
                (ch.Sources || []).forEach(function (s) {
                    s.__items = (s.ItemIds || []).map(function (id) { return { Id: id, Name: byId[id] || id }; });
                });
            });
        }).catch(function () { /* names fall back to ids */ });
    }

    // MARK: Wiring

    function bind() {
        el('selectChannel').addEventListener('change', function () { currentIndex = parseInt(this.value, 10); loadEditor(); });
        el('btnUploadLogo').addEventListener('click', function () { el('logoFile').click(); });
        el('logoFile').addEventListener('change', onLogoFile);
        el('btnClearLogo').addEventListener('click', clearLogo);
        // Keep the placeholder in step with the name (colour) and number (label) while there's no upload.
        el('channelNumber').addEventListener('input', function () { if (!logoData) renderLogoPreview(); });
        el('channelName').addEventListener('input', function () { if (!logoData) renderLogoPreview(); });
        el('logoStyle').addEventListener('change', renderLogoPreview);
        el('logoSymbol').addEventListener('input', function () { if (!logoData) renderLogoPreview(); });
        el('logoShowName').addEventListener('change', function () { if (!logoData) renderLogoPreview(); });
        el('favorKind').addEventListener('change', updateFavorControls);
        el('loopMode').addEventListener('change', updateFavorControls);
        el('addRatingBlock').addEventListener('click', function () {
            var ch = channels[currentIndex];
            if (!ch) return;
            ch.RatingBlocks = ch.RatingBlocks || [];
            ch.RatingBlocks.push(newRatingBlock());
            renderRatingBlocks();
        });
        el('btnNewChannel').addEventListener('click', addChannel);
        el('btnDeleteChannel').addEventListener('click', deleteChannel);
        el('btnExportChannels').addEventListener('click', exportChannels);
        el('btnImportChannels').addEventListener('click', function () { el('importFile').click(); });
        el('importFile').addEventListener('change', onImportFile);
        el('btnSaveChannel').addEventListener('click', saveChannel);
        el('btnAddLibrary').addEventListener('click', addLibrary);
        el('btnEnable').addEventListener('click', function () { currentEnabled = !currentEnabled; setEnableVisual(currentEnabled); });

        // Warm the Material Symbols web font (the same glyphs the server draws), then refresh the preview so a
        // symbol logo renders once it has loaded rather than briefly falling back to the number.
        if (document.fonts && document.fonts.load) {
            document.fonts.load('40px "LiveChannelsSymbols"').then(function () { if (!logoData) renderLogoPreview(); }).catch(function () {});
        }
    }

    function load() {
        Promise.all([loadLibraries(), loadCollections(), loadRatings(), loadLanguages()]).then(function () {
            return Shared.getConfig();
        }).then(function (cfg) {
            config = cfg;
            channels = (config.Channels || []).map(function (ch) {
                ch.Sources = ch.Sources || [];
                return ch;
            });
            return hydrateItemNames();
        }).then(function () {
            currentIndex = channels.length ? 0 : -1;
            renderSelect();
        });
    }

    view.addEventListener('viewshow', function () {
        _sharedPromise.then(function () {
            setTabs('livechannels', 0, TABS);
            if (!_bound) { bind(); _bound = true; }
            Shared.initCollapsibles();
            load();
        });
    });
}
