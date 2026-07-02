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

    var _bound = false;
    var cultures = [];         // cached [{ key: ISO code, label: name }] for the language search
    var langSelect = null;     // searchable single-select for the default subtitle language

    function el(id) { return view.querySelector('#' + id); }

    // The display name for a stored language code, falling back to the code itself when cultures are unavailable.
    function cultureLabel(code) {
        for (var i = 0; i < cultures.length; i++) { if (cultures[i].key === code) return cultures[i].label; }
        return code;
    }

    // A searchable single-select for a language: type to filter the (fixed, long) culture list and pick one, instead
    // of scrolling a ~180-entry dropdown. Stores and returns the three-letter ISO code.
    function createLanguageSelect() {
        var selected = null; // { key, label }

        var wrap = document.createElement('div');
        wrap.className = 'jpk-chip-select';
        var chips = document.createElement('div');
        chips.className = 'jpk-tags';
        var searchHost = document.createElement('div');
        searchHost.innerHTML = '<input type="text" is="emby-input" class="emby-input" placeholder="Search language…" autocomplete="off" />';
        var search = searchHost.querySelector('input');
        var results = document.createElement('div');
        results.className = 'jpk-table';
        results.style.display = 'none';
        wrap.appendChild(chips);
        wrap.appendChild(search);
        wrap.appendChild(results);

        function hideResults() { results.innerHTML = ''; results.style.display = 'none'; }

        function renderChip() {
            chips.innerHTML = '';
            if (!selected) { return; }
            var tag = document.createElement('span');
            tag.className = 'jpk-tag';
            var label = document.createElement('span');
            label.textContent = selected.label;
            var remove = document.createElement('span');
            remove.className = 'jpk-tag-remove';
            remove.textContent = '×';
            remove.title = 'Remove';
            remove.addEventListener('click', function () { selected = null; renderChip(); });
            tag.appendChild(label);
            tag.appendChild(remove);
            chips.appendChild(tag);
        }

        var timer = null;
        search.addEventListener('input', function () {
            if (timer) clearTimeout(timer);
            var term = search.value.trim();
            if (!term) { hideResults(); return; }
            timer = setTimeout(function () {
                var lower = term.toLowerCase();
                var rows = cultures.filter(function (c) { return c.label.toLowerCase().indexOf(lower) >= 0; }).slice(0, 25);
                results.innerHTML = '';
                if (!rows.length) { results.style.display = 'none'; return; }
                rows.forEach(function (c) {
                    var row = document.createElement('div');
                    row.className = 'jpk-table-row';
                    row.style.cursor = 'pointer';
                    row.textContent = c.label;
                    row.addEventListener('click', function () { selected = { key: c.key, label: c.label }; renderChip(); search.value = ''; hideResults(); });
                    results.appendChild(row);
                });
                results.style.display = '';
            }, 300);
        });

        renderChip();

        return {
            element: wrap,
            getValue: function () { return selected ? selected.key : ''; },
            setValue: function (code) { selected = code ? { key: code, label: cultureLabel(code) } : null; renderChip(); hideResults(); search.value = ''; }
        };
    }

    // Creates (once) the language select and mounts it into the playback section.
    function ensureLangSelect() {
        if (!langSelect) {
            langSelect = createLanguageSelect();
            var mount = view.querySelector('.lc-sublang-mount');
            if (mount) mount.appendChild(langSelect.element);
        }
    }

    // Snap a stored width to the nearest preset value in the resolution dropdown.
    function setResolution(width) {
        var opts = el('resolution').options;
        var best = opts[0].value, bestDiff = Infinity;
        for (var i = 0; i < opts.length; i++) {
            var diff = Math.abs(parseInt(opts[i].value, 10) - (width || 1280));
            if (diff < bestDiff) { bestDiff = diff; best = opts[i].value; }
        }
        el('resolution').value = best;
    }

    function renderAcceleration() {
        ApiClient.getJSON(ApiClient.getUrl('livechannels/encoders')).then(function (info) {
            el('accelValue').textContent = (info && info.acceleration) || 'Unknown';
        }).catch(function () { el('accelValue').textContent = 'Unknown'; });
    }

    function loadSettings(config) {
        setResolution(config.TranscodeWidth || 1280);
        el('videoCodec').value = config.VideoCodec || 'H264';
        el('audioCodec').value = config.AudioCodec || 'Aac';
        el('videoBitrate').value = config.TranscodeVideoBitrateKbps || 4000;
        el('maxSessions').value = config.MaxConcurrentSessions == null ? 3 : config.MaxConcurrentSessions;
        el('sessionTimeout').value = config.SessionTimeoutMinutes == null ? 0 : config.SessionTimeoutMinutes;
        el('streamDirectory').value = config.StreamDirectory || '';
        el('disableHwa').checked = !!config.DisableHardwareAcceleration;
        ensureLangSelect();
        langSelect.setValue(config.DefaultSubtitleLanguage || 'eng');
        renderAcceleration();
    }

    // Loads the server's known cultures once into a cache of { key: ISO code, label: name } for the language search.
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

    // Triggers Jellyfin's built-in guide refresh so a save propagates to Live TV right away.
    function refreshGuide() {
        return ApiClient.getScheduledTasks().then(function (tasks) {
            var task = (tasks || []).filter(function (t) { return t.Key === 'RefreshGuide'; })[0];
            if (task) return ApiClient.startScheduledTask(task.Id);
        }).catch(function () { /* best effort */ });
    }

    function saveSettings() {
        // Read the latest config so channel edits made on the other tab are preserved.
        Shared.getConfig().then(function (fresh) {
            fresh.TranscodeWidth = parseInt(el('resolution').value, 10) || 1280;
            fresh.VideoCodec = el('videoCodec').value;
            fresh.AudioCodec = el('audioCodec').value;
            fresh.TranscodeVideoBitrateKbps = Math.max(500, parseInt(el('videoBitrate').value, 10) || 4000);
            var maxSessions = parseInt(el('maxSessions').value, 10);
            fresh.MaxConcurrentSessions = isNaN(maxSessions) ? 3 : Math.max(0, maxSessions);
            var timeout = parseInt(el('sessionTimeout').value, 10);
            fresh.SessionTimeoutMinutes = isNaN(timeout) ? 0 : Math.max(0, timeout);
            fresh.StreamDirectory = (el('streamDirectory').value || '').trim();
            fresh.DisableHardwareAcceleration = el('disableHwa').checked;
            fresh.DefaultSubtitleLanguage = (langSelect ? langSelect.getValue() : '') || 'eng';
            return Shared.saveConfig(fresh);
        }).then(function () {
            renderAcceleration();
            refreshGuide();
            Shared.setStatus('settingsStatus', 'Saved. Refreshing Live TV…', false);
        }).catch(function () {
            Shared.setStatus('settingsStatus', 'Save failed.', true);
        });
    }

    // Forces Jellyfin to rebuild the Live TV guide for every channel from the current saved configuration.
    // The schedule is a pure projection of the channels, so a guide rebuild is a full reset: a stale schedule
    // (for example one still showing a now-excluded genre) is recreated fresh.
    function resetSchedule() {
        Shared.setStatus('resetStatus', 'Rebuilding schedule and guide…', false);
        ApiClient.getScheduledTasks().then(function (tasks) {
            var task = (tasks || []).filter(function (t) { return t.Key === 'RefreshGuide'; })[0];
            if (!task) { throw new Error('Refresh Guide task not found'); }
            return ApiClient.startScheduledTask(task.Id);
        }).then(function () {
            Shared.setStatus('resetStatus', 'Done. The guide is rebuilding in the background.', false);
        }).catch(function () {
            Shared.setStatus('resetStatus', 'Could not start the rebuild. Try again in a moment.', true);
        });
    }

    // --- Stress test: measures how many concurrent streams the encoder sustains, using the production pipeline.

    var _stressTimer = null;

    // The stress-test item picker: the same single-select typeahead chip the channel editor uses for its
    // pickers (search as you type, click a row, the choice becomes a removable chip), so choosing a test item
    // feels identical to choosing channel content. Searches recursively across all libraries and returns only
    // individual playable files (movies and episodes). Nothing here is saved — the test is ad hoc.
    var stressPicker = null;

    function ensureStressPicker() {
        if (stressPicker) { return; }
        var selected = null;
        var wrap = document.createElement('div');
        wrap.className = 'jpk-chip-select';
        var chips = document.createElement('div');
        chips.className = 'jpk-tags';
        var searchHost = document.createElement('div');
        searchHost.innerHTML = '<input type="text" is="emby-input" class="emby-input" autocomplete="off" />';
        var search = searchHost.querySelector('input');
        search.placeholder = 'Search movies and episodes…';
        var results = document.createElement('div');
        results.className = 'jpk-table';
        results.style.display = 'none';
        wrap.appendChild(chips);
        wrap.appendChild(search);
        wrap.appendChild(results);

        function hideResults() { results.innerHTML = ''; results.style.display = 'none'; }

        function renderChip() {
            chips.innerHTML = '';
            if (!selected) { return; }
            var tag = document.createElement('span');
            tag.className = 'jpk-tag';
            var label = document.createElement('span');
            label.textContent = selected.label;
            var remove = document.createElement('span');
            remove.className = 'jpk-tag-remove';
            remove.textContent = '×';
            remove.title = 'Remove';
            remove.addEventListener('click', function () { selected = null; renderChip(); });
            tag.appendChild(label);
            tag.appendChild(remove);
            chips.appendChild(tag);
        }

        function runSearch(term) {
            ApiClient.getJSON(ApiClient.getUrl('Items', {
                searchTerm: term, recursive: true, includeItemTypes: 'Movie,Episode', limit: 25, fields: 'ProductionYear'
            })).then(function (r) {
                results.innerHTML = '';
                var items = (r && r.Items) || [];
                if (!items.length) { results.style.display = 'none'; return; }
                items.forEach(function (i) {
                    var row = document.createElement('div');
                    row.className = 'jpk-table-row';
                    row.style.cursor = 'pointer';
                    row.textContent = i.Name + (i.ProductionYear ? ' (' + i.ProductionYear + ')' : '') + (i.SeriesName ? ' — ' + i.SeriesName : '');
                    row.addEventListener('click', function () {
                        selected = { key: i.Id, label: row.textContent };
                        renderChip();
                        search.value = '';
                        hideResults();
                    });
                    results.appendChild(row);
                });
                results.style.display = '';
            }).catch(hideResults);
        }

        var timer = null;
        search.addEventListener('input', function () {
            if (timer) { clearTimeout(timer); }
            var term = search.value.trim();
            if (!term) { hideResults(); return; }
            timer = setTimeout(function () { runSearch(term); }, 300);
        });

        var mount = view.querySelector('.lc-stressitem-mount');
        if (mount) { mount.appendChild(wrap); }
        stressPicker = { getId: function () { return selected ? selected.key : ''; } };
    }

    function renderStress(s) {
        var lines = (s.Rounds || []).map(function (r) {
            return r.Streams + ' stream' + (r.Streams === 1 ? '' : 's') + ': slowest ' + r.MinFps + ' fps ' + (r.Passed ? '✓' : '✗');
        });
        if (s.Running && s.CurrentStreams > 0) {
            lines.push('Testing ' + s.CurrentStreams + ' concurrent stream' + (s.CurrentStreams === 1 ? '' : 's') + '…');
        }
        var html = lines.join('<br>');
        if (!s.Running && typeof s.Recommended === 'number' && (s.Rounds || []).length) {
            html += '<br><b>' + (s.Recommended > 0
                ? 'Recommended Maximum concurrent streams: ' + s.Recommended
                : 'Even one stream could not hold realtime with this item; consider lowering resolution or bitrate.') + '</b>';
            if (s.Recommended > 0) {
                html += ' <a is="emby-linkbutton" href="#" id="stressApply" class="button-link">Apply</a>';
            }
        }
        if (s.Error) { html += '<br>' + Shared.escapeHtml(s.Error); }
        el('stressResult').innerHTML = html;
        var apply = el('stressApply');
        if (apply) {
            apply.addEventListener('click', function (e) {
                e.preventDefault();
                el('maxSessions').value = s.Recommended;
                Shared.setStatus('stressStatus', 'Set to ' + s.Recommended + ' — remember to Save.', false);
            });
        }
        Shared.setVisible('stressCancel', s.Running);
        el('stressRun').disabled = !!s.Running;
    }

    function pollStress() {
        return ApiClient.getJSON(ApiClient.getUrl('livechannels/stresstest')).then(function (s) {
            renderStress(s);
            if (!s.Running) { stopStressPolling(); }
        }).catch(function () { /* transient; keep polling */ });
    }

    function startStressPolling() {
        stopStressPolling();
        _stressTimer = setInterval(pollStress, 3000);
        pollStress();
    }

    function stopStressPolling() {
        if (_stressTimer) { clearInterval(_stressTimer); _stressTimer = null; }
    }

    function stressRun() {
        var id = stressPicker ? stressPicker.getId() : '';
        if (!id) { Shared.setStatus('stressStatus', 'Search for and pick a test item first.', true); return; }
        Shared.setStatus('stressStatus', 'Starting…', false);
        el('stressResult').innerHTML = '';
        ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('livechannels/stresstest/' + encodeURIComponent(id)) }).then(function () {
            Shared.setStatus('stressStatus', '', false);
            startStressPolling();
        }).catch(function (err) {
            var read = (err && err.responseText) ? Promise.resolve(err.responseText) : (err && err.text ? err.text() : Promise.resolve(''));
            read.then(function (t) {
                Shared.setStatus('stressStatus', t ? t.replace(/^"|"$/g, '') : 'Could not start the test (are streams still running?).', true);
            });
        });
    }

    function stressCancel() {
        ApiClient.ajax({ type: 'DELETE', url: ApiClient.getUrl('livechannels/stresstest') }).then(pollStress);
    }

    function bind() {
        el('btnSaveSettings').addEventListener('click', saveSettings);
        el('btnResetSchedule').addEventListener('click', resetSchedule);
        el('stressRun').addEventListener('click', stressRun);
        el('stressCancel').addEventListener('click', stressCancel);
    }

    view.addEventListener('viewshow', function () {
        _sharedPromise.then(function () {
            setTabs('livechannels', 3, TABS);
            if (!_bound) { bind(); _bound = true; }
            Shared.initCollapsibles();
            // Populate the language options before applying the saved value to the dropdown.
            loadLanguages().then(function () {
                Shared.getConfig().then(loadSettings);
            });
            // A stress test may already be running (started earlier, page revisited): resume its display. The
            // poll stops itself on the first response when no test is running.
            ensureStressPicker();
            startStressPolling();
        });
    });

    view.addEventListener('viewhide', stopStressPolling);
}
