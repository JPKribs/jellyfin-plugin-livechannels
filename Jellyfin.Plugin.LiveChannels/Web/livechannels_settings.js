import { createBanner, createChipSelect, createColorPicker, createConfigPage, createDefinitionList, createInheritField, createMediaPicker, createSaveBar, createSliderField, createTranslations, loadTranslations } from './configurationpage?name=livechannels_jpkribs_shared.js';
import { TRANSLATIONS_URL, pickerText, readError, saveBarText, tabList, withText } from './configurationpage?name=livechannels_placeholder.js';

export default function (view) {
    'use strict';

    var PLUGIN_ID = 'ac6940fb-aac6-4de8-b622-55a662e23658';
    // The page's strings, empty until load reads them, so the markup keeps its English until then.
    var t = createTranslations({});

    var page = createConfigPage(view, {
        pluginId: PLUGIN_ID,
        tabsKey: 'livechannels',
        tabIndex: 3,
        tabs: function () { return tabList(t); },
        bind: bind,
        load: load
    });
    var Shared = page.shared;

    var cultures = [];         // cached [{ value: ISO code, label: name }] for the language choices
    var langSelect = null;     // Chip Select, single, for the default subtitle language
    var accelList = null;      // the Hardware Acceleration Definition List
    var scaleSlider = null;    // the subtitle Size Slider
    var textColor = null;      // the subtitle Text Color field
    var outlineColor = null;   // the subtitle Outline Color field
    var saveBar = null;        // the Save Bar
    var stressRounds = null;   // the stress test rounds Definition List
    var stressSummary = null;

    function el(id) { return view.querySelector('#' + id); }

    // The display name for a stored language code, falling back to the code itself when cultures are unavailable.
    function cultureLabel(code) {
        for (var i = 0; i < cultures.length; i++) { if (cultures[i].value === code) return cultures[i].label; }
        return code;
    }

    // colorField
    // Builds a subtitle Color Picker inside an Inherit Field, where As Authored keeps each subtitle's own color.
    //
    // Inputs
    // fallback [string]: The color an override starts from.
    // label [string]: The field's name.
    //
    // Output
    // [object]: { element, read(), write(stored) }, where a blank stored value means As Authored.
    function colorField(fallback, label) {
        var picker = createColorPicker({ value: fallback, label: label, pickLabel: t.tr('LabelPickColor'), invalidText: t.tr('ErrorInvalidColor'), onChange: markDirty });
        var inherit = createInheritField({
            field: picker,
            inheritedValue: fallback,
            inheritText: t.tr('OptionAsAuthored'),
            overrideText: t.tr('Custom'),
            label: label,
            onChange: markDirty
        });
        return {
            element: inherit.element,
            read: function () { var v = inherit.getValue(); return v.inherit ? '' : normalizeColor(v.value); },
            write: function (stored) { var hex = normalizeColor(stored); inherit.setValue({ inherit: !hex, value: hex || fallback }); }
        };
    }

    // markDirty
    // Shows the Save Bar's unsaved line.
    function markDirty() {
        if (saveBar) saveBar.setDirty(true);
    }

    // ensureParts
    // Creates the page's Base components once, with the page's strings, and mounts them.
    function ensureParts() {
        if (saveBar) return;
        accelList = createDefinitionList({ items: [{ label: t.tr('TitleHardwareAcceleration'), value: t.tr('MessageChecking'), mono: true }] });
        scaleSlider = createSliderField({ min: 50, max: 300, step: 5, value: 100, suffix: '%', label: t.tr('LabelSize') });
        textColor = colorField('#FFFFFF', t.tr('LabelTextColorTitle'));
        outlineColor = colorField('#000000', t.tr('LabelOutlineColor'));
        saveBar = createSaveBar(withText({ fill: true, onSave: saveSettings }, saveBarText(t)));
        stressRounds = createDefinitionList({ items: [] });
        el('accelHost').appendChild(accelList.element);
        el('subScaleHost').appendChild(scaleSlider.element);
        el('subTextColorHost').appendChild(textColor.element);
        el('subOutlineColorHost').appendChild(outlineColor.element);
        el('settingsSaveBar').appendChild(saveBar.element);
        el('stressRounds').appendChild(stressRounds.element);
        Shared.setVisible(stressRounds.element, false);
    }

    // ensureLangSelect
    // Creates the language Chip Select once and mounts it in the Playback section, or refreshes its choices.
    function ensureLangSelect() {
        if (!langSelect) {
            langSelect = createChipSelect(withText({ placeholder: t.tr('PlaceholderSearchLanguages'), options: cultures, single: true, onChange: markDirty }, pickerText(t)));
            el('subLanguageHost').appendChild(langSelect.element);
        } else {
            langSelect.setOptions(cultures);
        }
    }

    // Accepts a colour with or without the leading hash and stores it in the one form the server parses. Anything
    // that is not six hex digits is stored blank, which means "leave the subtitle's own colour alone".
    function normalizeColor(value) {
        var hex = (value || '').trim().replace(/^#/, '');
        return /^[0-9a-fA-F]{6}$/.test(hex) ? '#' + hex.toUpperCase() : '';
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
            accelList.setItems([{ label: t.tr('TitleHardwareAcceleration'), value: (info && info.acceleration) || t.tr('Unknown'), mono: true }]);
        }).catch(function () { accelList.setItems([{ label: t.tr('TitleHardwareAcceleration'), value: t.tr('Unknown'), mono: true }]); });
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
        el('copyVideo').checked = !!config.CopyMatchingVideo;
        el('subFont').value = config.SubtitleFont || '';
        scaleSlider.setValue(config.SubtitleFontScalePercent ? config.SubtitleFontScalePercent : 100);
        textColor.write(config.SubtitleTextColor);
        outlineColor.write(config.SubtitleOutlineColor);
        el('subBorder').value = config.SubtitleBorder || 'Default';
        el('subBold').checked = !!config.SubtitleBold;
        ensureLangSelect();
        var lang = config.DefaultSubtitleLanguage || 'eng';
        langSelect.setValue([{ value: lang, label: cultureLabel(lang) }]);
        renderAcceleration();
        saveBar.setDirty(false);
    }

    // Loads the server's known cultures once into a cache of { value: ISO code, label: name } for the language choices.
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

    // Triggers Jellyfin's built-in guide refresh so a save propagates to Live TV right away.
    function refreshGuide() {
        return ApiClient.getScheduledTasks().then(function (tasks) {
            var task = (tasks || []).filter(function (t) { return t.Key === 'RefreshGuide'; })[0];
            if (task) return ApiClient.startScheduledTask(task.Id);
        }).catch(function () { /* best effort */ });
    }

    // saveSettings
    // Saves the settings. The Save Bar runs it and shows its outcome, including the server's reason.
    //
    // Output
    // [Promise]: Settles once the save is done.
    function saveSettings() {
        // Read the latest config so channel edits made on the other tab are preserved.
        return Shared.getConfig().then(function (fresh) {
            fresh.TranscodeWidth = parseInt(el('resolution').value, 10) || 1280;
            fresh.VideoCodec = el('videoCodec').value;
            fresh.AudioCodec = el('audioCodec').value;
            fresh.TranscodeVideoBitrateKbps = Math.min(200000, Math.max(100, parseInt(el('videoBitrate').value, 10) || 4000));
            var maxSessions = parseInt(el('maxSessions').value, 10);
            fresh.MaxConcurrentSessions = isNaN(maxSessions) ? 3 : Math.max(0, maxSessions);
            var timeout = parseInt(el('sessionTimeout').value, 10);
            fresh.SessionTimeoutMinutes = isNaN(timeout) ? 0 : Math.max(0, timeout);
            fresh.StreamDirectory = (el('streamDirectory').value || '').trim();
            fresh.DisableHardwareAcceleration = el('disableHwa').checked;
            fresh.CopyMatchingVideo = el('copyVideo').checked;
            var picked = langSelect ? langSelect.getValue() : [];
            fresh.DefaultSubtitleLanguage = (picked.length ? String(picked[0]) : '') || 'eng';
            fresh.SubtitleFont = (el('subFont').value || '').trim();
            var scale = parseInt(scaleSlider.getValue(), 10);
            fresh.SubtitleFontScalePercent = isNaN(scale) ? 100 : Math.min(300, Math.max(50, scale));
            fresh.SubtitleTextColor = textColor.read();
            fresh.SubtitleOutlineColor = outlineColor.read();
            fresh.SubtitleBorder = el('subBorder').value || 'Default';
            fresh.SubtitleBold = el('subBold').checked;
            return Shared.saveConfig(fresh);
        }).then(function () {
            renderAcceleration();
            refreshGuide();
        }, function (err) {
            // The server rejects a color it cannot parse, so show what it said rather than a bare failure.
            return readError(err, t.tr('ErrorCouldNotSaveSettings')).then(function (message) { throw new Error(message); });
        });
    }

    // Resets every channel from scratch: the plugin clears the cached schedules and tune-in cards, then starts
    // Jellyfin's Refresh Guide task and its own Generate Buffer Cards task, so a stale schedule (for example one
    // still showing a now-excluded genre) and stale cards are both recreated fresh.
    function resetSchedule() {
        Shared.setStatus('resetStatus', t.tr('MessageRebuilding'), false);
        ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('livechannels/schedule/reset') }).then(function () {
            Shared.setStatus('resetStatus', t.tr('MessageRebuildStarted'), false);
        }).catch(function () {
            Shared.setStatus('resetStatus', t.tr('ErrorCouldNotRebuild'), true);
        });
    }

    // --- Stress test: measures how many concurrent streams the encoder sustains, using the production pipeline.

    var _stressPoller = null;

    // The stress test item picker, a single Media Picker over movies and episodes. Nothing here is saved.
    var stressPicker = null;

    // ensureStressPicker
    // Creates the stress test Media Picker once and mounts it in the Stress Test section.
    function ensureStressPicker() {
        if (stressPicker) { return; }
        stressPicker = createMediaPicker(withText({
            single: true,
            itemTypes: ['Movie', 'Episode'],
            typeLabels: { Movie: t.tr('Movie'), Episode: t.tr('Episode') },
            placeholder: t.tr('PlaceholderSearchMoviesEpisodes'),
            emptyText: t.tr('EmptyTestItem'),
            removeText: t.format('LabelRemoveValue', '{value}')
        }, pickerText(t)));
        el('stressItemHost').appendChild(stressPicker.element);
    }

    // renderStress
    // Shows each stress test round as a definition with a pass or fail badge, and the recommendation as a Banner.
    //
    // Inputs
    // s [object]: The stress test status from the server.
    function renderStress(s) {
        var items = (s.Rounds || []).map(function (r) {
            return {
                label: streamCount(r.Streams),
                value: { text: t.format('ValueSlowestFps', r.MinFps), badge: r.Passed ? { label: t.tr('StatusPassed'), color: 'green' } : { label: t.tr('LabelFailed'), color: 'red' } }
            };
        });
        if (s.Running && s.CurrentStreams > 0) {
            items.push({ label: streamCount(s.CurrentStreams), value: { text: t.tr('MessageTesting'), badge: { label: t.tr('StatusRunning'), color: 'blue' } } });
        }
        stressRounds.setItems(items);
        Shared.setVisible(stressRounds.element, items.length > 0);

        if (stressSummary) { stressSummary.destroy(); stressSummary = null; }
        el('stressSummary').innerHTML = '';
        var finished = !s.Running && typeof s.Recommended === 'number' && (s.Rounds || []).length;
        if (finished || s.Error) {
            var good = finished && s.Recommended > 0;
            stressSummary = createBanner({
                kind: s.Error ? 'error' : (good ? 'success' : 'warn'),
                title: finished ? (good ? t.format('MessageRecommendedStreams', s.Recommended) : t.tr('MessageNoStreamKeepsUp')) : '',
                text: s.Error ? s.Error : (good ? '' : t.tr('MessageLowerResolution')),
                dismissible: false,
                actions: good ? [{
                    text: t.tr('ButtonApply'),
                    onClick: function () {
                        el('maxSessions').value = s.Recommended;
                        markDirty();
                        Shared.setStatus('stressStatus', t.format('MessageSetToSave', s.Recommended), false);
                    }
                }] : null
            });
            el('stressSummary').appendChild(stressSummary.element);
        }
        Shared.setVisible('stressCancel', s.Running);
        el('stressRun').disabled = !!s.Running;
    }

    // streamCount
    // Names a stress test round by its number of streams.
    //
    // Inputs
    // count [number]: How many streams the round runs.
    //
    // Output
    // [string]: The label, such as 3 Streams.
    function streamCount(count) {
        return count === 1 ? t.tr('LabelOneStream') : t.format('LabelStreams', count);
    }

    function pollStress() {
        return ApiClient.getJSON(ApiClient.getUrl('livechannels/stresstest')).then(function (s) {
            renderStress(s);
            if (!s.Running) { stopStressPolling(); }
        }).catch(function () { /* transient; keep polling */ });
    }

    // The page stops the poller on viewhide.
    function startStressPolling() {
        stopStressPolling();
        _stressPoller = page.createPoller(pollStress, 3000);
        _stressPoller.start();
    }

    function stopStressPolling() {
        if (_stressPoller) { _stressPoller.stop(); _stressPoller = null; }
    }

    function stressRun() {
        var picked = stressPicker ? stressPicker.getValue() : [];
        var id = picked.length ? picked[0].Id : '';
        if (!id) { Shared.setStatus('stressStatus', t.tr('ErrorSelectTestItem'), true); return; }
        Shared.setStatus('stressStatus', t.tr('MessageStarting'), false);
        stressRounds.setItems([]);
        Shared.setVisible(stressRounds.element, false);
        el('stressSummary').innerHTML = '';
        ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('livechannels/stresstest/' + encodeURIComponent(id)) }).then(function () {
            Shared.setStatus('stressStatus', '', false);
            startStressPolling();
        }).catch(function (err) {
            readError(err, t.tr('ErrorCouldNotStartTest')).then(function (message) {
                Shared.setStatus('stressStatus', message, true);
            });
        });
    }

    function stressCancel() {
        ApiClient.ajax({ type: 'DELETE', url: ApiClient.getUrl('livechannels/stresstest') }).then(pollStress).catch(function () {
            Shared.setStatus('stressStatus', t.tr('ErrorCouldNotCancelTest'), true);
        });
    }

    function bind() {
        // Any edit shows the Save Bar's unsaved line.
        el('LiveChannelsSettingsForm').addEventListener('input', function (e) { if (!e.target.closest('#sectionStress')) markDirty(); });
        el('LiveChannelsSettingsForm').addEventListener('change', function (e) { if (!e.target.closest('#sectionStress')) markDirty(); });
        el('btnResetSchedule').addEventListener('click', resetSchedule);
        el('stressRun').addEventListener('click', stressRun);
        el('stressCancel').addEventListener('click', stressCancel);
    }

    function load() {
        Shared.initCollapsibles();
        // Populate the language options before applying the saved value to the dropdown.
        Promise.all([loadLanguages(), loadTranslations(TRANSLATIONS_URL)]).then(function (results) {
            t = results[1];
            t.apply(view);
            ensureParts();
            // A stress test may already be running (started earlier, page revisited): resume its display. The
            // poll stops itself on the first response when no test is running.
            ensureStressPicker();
            startStressPolling();
            return Shared.getConfig().then(loadSettings);
        }).catch(function () {
            if (saveBar) saveBar.setState('failed', t.tr('ErrorCouldNotLoadSettings'));
        });
    }
}
