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

    function el(id) { return view.querySelector('#' + id); }

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
        el('bufferSeconds').value = config.BufferSeconds == null ? 3 : config.BufferSeconds;
        el('preRenderSeconds').value = config.PreRenderSeconds == null ? 3 : config.PreRenderSeconds;
        el('maxSessions').value = config.MaxConcurrentSessions == null ? 3 : config.MaxConcurrentSessions;
        el('sessionTimeout').value = config.SessionTimeoutMinutes == null ? 0 : config.SessionTimeoutMinutes;
        el('streamWindow').value = config.StreamWindowMinutes == null ? 5 : config.StreamWindowMinutes;
        el('producerRate').value = config.StreamReadRate == null ? 1 : config.StreamReadRate;
        el('streamDirectory').value = config.StreamDirectory || '';
        el('disableHwa').checked = !!config.DisableHardwareAcceleration;
        el('subtitleLanguage').value = config.DefaultSubtitleLanguage || 'eng';
        renderAcceleration();
    }

    // Populates the default-language dropdown from the server's known cultures (value is the three-letter ISO code).
    function loadLanguages() {
        return ApiClient.getJSON(ApiClient.getUrl('Localization/Cultures')).then(function (list) {
            var seen = {};
            var opts = (list || []).filter(function (c) {
                var code = c && c.ThreeLetterISOLanguageName;
                if (!code || seen[code]) return false;
                seen[code] = true; return true;
            }).sort(function (a, b) { return (a.DisplayName || a.Name || '').localeCompare(b.DisplayName || b.Name || ''); })
              .map(function (c) {
                return '<option value="' + Shared.escapeHtml(c.ThreeLetterISOLanguageName) + '">' + Shared.escapeHtml(c.DisplayName || c.Name) + '</option>';
            }).join('');
            el('subtitleLanguage').innerHTML = opts;
        }).catch(function () {
            el('subtitleLanguage').innerHTML = '<option value="eng">English</option>';
        });
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
            var buf = parseInt(el('bufferSeconds').value, 10);
            fresh.BufferSeconds = isNaN(buf) ? 3 : Math.min(30, Math.max(0, buf));
            var preRender = parseInt(el('preRenderSeconds').value, 10);
            fresh.PreRenderSeconds = isNaN(preRender) ? 3 : Math.min(30, Math.max(0, preRender));
            var maxSessions = parseInt(el('maxSessions').value, 10);
            fresh.MaxConcurrentSessions = isNaN(maxSessions) ? 3 : Math.max(0, maxSessions);
            var timeout = parseInt(el('sessionTimeout').value, 10);
            fresh.SessionTimeoutMinutes = isNaN(timeout) ? 0 : Math.max(0, timeout);
            var window = parseInt(el('streamWindow').value, 10);
            fresh.StreamWindowMinutes = isNaN(window) ? 5 : Math.max(1, window);
            var rate = parseFloat(el('producerRate').value);
            fresh.StreamReadRate = isNaN(rate) ? 1.0 : Math.min(2, Math.max(1, Math.round(rate * 1000) / 1000));
            fresh.StreamDirectory = (el('streamDirectory').value || '').trim();
            fresh.DisableHardwareAcceleration = el('disableHwa').checked;
            fresh.DefaultSubtitleLanguage = el('subtitleLanguage').value || 'eng';
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

    function bind() {
        el('btnSaveSettings').addEventListener('click', saveSettings);
        el('btnResetSchedule').addEventListener('click', resetSchedule);
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
        });
    });
}
