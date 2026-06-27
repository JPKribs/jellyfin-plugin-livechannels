export default function (view) {
    'use strict';

    var PLUGIN_ID = 'ac6940fb-aac6-4de8-b622-55a662e23658';
    var TABS = [
        { href: 'configurationpage?name=livechannels_channels', name: 'Channels' },
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
    var ratings = [];          // [{ Name, Value }]
    var _bound = false;

    function el(id) { return view.querySelector('#' + id); }

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
        var picker = document.createElement('select');
        picker.className = 'jpk-selector-dropdown';
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

        if (style === 'Symbol' && symbol) {
            // The Material Icons font (loaded by Jellyfin) renders the ligature name as its glyph.
            ctx.font = Math.round(size * 0.4) + 'px "Material Icons"';
            ctx.fillText(symbol, size / 2, size / 2);
        } else {
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

    function newSource(libraryId) {
        return { LibraryId: libraryId || '', LibraryName: libraryId ? libraryName(libraryId) : '', Genres: [], MatchAllGenres: false, Selection: 'AllContent', ItemIds: [], __items: [] };
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
                '<select is="emby-select" class="lc-source-library jpk-selector-dropdown"></select>' +
                '<button is="emby-button" type="button" class="lc-remove raised jpk-button-destructive jpk-button-small"><span>Remove</span></button>' +
            '</div>' +
            '<div class="inputContainer">' +
                '<label class="inputLabel">Selection</label>' +
                '<select is="emby-select" class="lc-selection">' +
                    '<option value="AllContent">All content</option>' +
                    '<option value="Genre">Genre</option>' +
                    '<option value="Whitelist">Whitelist</option>' +
                    '<option value="Blacklist">Blacklist</option>' +
                '</select>' +
            '</div>' +
            '<div class="filterSection lc-genres">' +
                '<h3 class="jpk-subsection-title">Genres</h3>' +
                '<div class="lc-genre-mount"></div>' +
                '<label class="emby-checkbox-label lc-matchall-label"><input type="checkbox" is="emby-checkbox" class="lc-matchall" /><span class="checkboxLabel">Match all genres</span></label>' +
                '<div class="fieldDescription">Content needs to contain all genres to be included in this channel.</div>' +
            '</div>' +
            '<div class="filterSection lc-items">' +
                '<input is="emby-input" type="text" class="lc-search" placeholder="Search this library…" />' +
                '<div class="jpk-table-item-count lc-count"></div>' +
                '<div class="jpk-table-body lc-list"></div>' +
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

        // Genres (shown for the genre selection)
        var genresSection = card.querySelector('.lc-genres');
        var chip = createChipSelect({ placeholder: 'Add a genre…', onChange: function (vals) { source.Genres = vals; } });
        chip.setValue(source.Genres || []);
        card.querySelector('.lc-genre-mount').appendChild(chip.element);
        function reloadGenres() { loadCardGenres(source.LibraryId).then(function (names) { chip.setAvailable(names); }); }
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

    function loadCardGenres(libraryId) {
        if (!libraryId) return Promise.resolve([]);
        return ApiClient.getJSON(ApiClient.getUrl('Genres', { ParentId: libraryId, IncludeItemTypes: 'Movie,Episode', Recursive: true, Limit: 500, SortBy: 'SortName' }))
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
            el('minRating').innerHTML = '<option value="">No minimum</option>' + opts;
            el('maxRating').innerHTML = '<option value="">No limit</option>' + opts;
            el('kidsRating').innerHTML = '<option value="">None</option>' + opts;
        }).catch(function () {
            el('minRating').innerHTML = '<option value="">No minimum</option>';
            el('maxRating').innerHTML = '<option value="">No limit</option>';
            el('kidsRating').innerHTML = '<option value="">None</option>';
        });
    }

    // MARK: Editor load / save

    function loadEditor() {
        var ch = channels[currentIndex];
        if (!ch) return;
        el('channelName').value = ch.Name || '';
        el('channelNumber').value = ch.Number || '';
        logoData = ch.LogoData || '';
        logoContentType = ch.LogoContentType || '';
        el('logoStyle').value = ch.LogoStyle || 'Number';
        el('logoSymbol').value = ch.LogoSymbol || '';
        el('logoShowName').checked = ch.LogoShowName !== false;
        renderLogoPreview();

        renderSources();

        el('minRating').value = ch.MinOfficialRating || '';
        el('maxRating').value = ch.MaxOfficialRating || '';
        el('includeUnrated').checked = ch.IncludeUnrated !== false;
        el('kidsRating').value = ch.KidsRatingThreshold || '';
        el('episodesPerBlock').value = ch.EpisodesPerBlock || 1;
        el('keepMultiPart').checked = ch.KeepMultiPartTogether !== false;
        el('includeSpecials').checked = !!ch.IncludeSpecials;
        el('shuffle').checked = ch.Shuffle !== false;
        el('episodeOrder').value = ch.ShuffleEpisodes ? 'random' : 'air';
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
        ch.MinOfficialRating = el('minRating').value;
        ch.MaxOfficialRating = el('maxRating').value;
        ch.IncludeUnrated = el('includeUnrated').checked;
        ch.KidsRatingThreshold = el('kidsRating').value;
        ch.EpisodesPerBlock = Math.max(1, parseInt(el('episodesPerBlock').value, 10) || 1);
        ch.KeepMultiPartTogether = el('keepMultiPart').checked;
        ch.IncludeSpecials = el('includeSpecials').checked;
        ch.Shuffle = el('shuffle').checked;
        ch.ShuffleEpisodes = el('episodeOrder').value === 'random';
        ch.SubtitleBurnIn = el('subtitleBurnIn').value;
        ch.Enabled = currentEnabled;
        // Sources are mutated live by the cards; keep LibraryName in sync.
        ch.Sources.forEach(function (s) { s.LibraryName = libraryName(s.LibraryId); });
    }

    // MARK: Persistence

    function saveChannel() {
        var ch = channels[currentIndex];
        if (!ch) return;
        readEditorInto(ch);
        if (!ch.Name) { Shared.setStatus('channelStatus', 'A name is required.', true); return; }
        if (!ch.Sources.length) { Shared.setStatus('channelStatus', 'Add at least one library source.', true); return; }
        if (ch.Sources.some(function (s) { return !s.LibraryId; })) { Shared.setStatus('channelStatus', 'Pick a library for each source.', true); return; }
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

    function addChannel() {
        channels.push({
            Id: newId(), Name: '', Number: nextNumber(), LogoData: '', LogoContentType: '',
            LogoStyle: 'Number', LogoSymbol: '', LogoShowName: true,
            Sources: [], MinOfficialRating: '', MaxOfficialRating: '', IncludeUnrated: true, KidsRatingThreshold: 'G',
            EpisodesPerBlock: 1, KeepMultiPartTogether: true,
            IncludeSpecials: false, Shuffle: true, ShuffleEpisodes: false, SubtitleBurnIn: 'Never', Enabled: true
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
        el('btnNewChannel').addEventListener('click', addChannel);
        el('btnDeleteChannel').addEventListener('click', deleteChannel);
        el('btnSaveChannel').addEventListener('click', saveChannel);
        el('btnAddLibrary').addEventListener('click', addLibrary);
        el('btnEnable').addEventListener('click', function () { currentEnabled = !currentEnabled; setEnableVisual(currentEnabled); });
    }

    function load() {
        Promise.all([loadLibraries(), loadRatings()]).then(function () {
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
