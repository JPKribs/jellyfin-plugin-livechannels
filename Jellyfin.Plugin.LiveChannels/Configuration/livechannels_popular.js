import { createConfigPage, createSaveBar } from './configurationpage?name=livechannels_jpkribs_shared.js';
import { createBlockEditors, loadRatings, migrateRatingBlocks } from './configurationpage?name=livechannels_placeholder.js';

export default function (view) {
    'use strict';

    var PLUGIN_ID = 'ac6940fb-aac6-4de8-b622-55a662e23658';
    var TABS = [
        { href: 'configurationpage?name=livechannels_channels', name: 'Channels' },
        { href: 'configurationpage?name=livechannels_popular', name: 'Popular' },
        { href: 'configurationpage?name=livechannels_sessions', name: 'Sessions' },
        { href: 'configurationpage?name=livechannels_settings', name: 'Settings' }
    ];

    var page = createConfigPage(view, {
        pluginId: PLUGIN_ID,
        tabsKey: 'livechannels',
        tabIndex: 1,
        tabs: TABS,
        bind: bind,
        load: load
    });
    var Shared = page.shared;

    var blockEditors = null;   // the rating and placeholder block card lists
    var saveBar = null;        // the Save Bar

    function el(id) { return view.querySelector('#' + id); }

    // ensureBlockEditors
    // Creates the rating and placeholder block card lists once, with the server's ratings.
    //
    // Inputs
    // ratings [object]: { choices, valueOf } from loadRatings.
    function ensureBlockEditors(ratings) {
        if (blockEditors) return;
        blockEditors = createBlockEditors({ ratings: ratings, shared: Shared, onChange: function () { saveBar.setDirty(true); } });
        el('popularRatingBlocks').appendChild(blockEditors.ratingElement);
        el('popularPlaceholderBlocks').appendChild(blockEditors.placeholderElement);
    }

    function loadPopular(config) {
        var pc = config.PopularChannel || {};
        el('popularEnabled').checked = pc.Enabled !== false;
        el('popularName').value = pc.Name || 'Popular';
        el('popularIcon').value = pc.LogoSymbol || '';
        el('popularShowName').checked = pc.LogoShowName !== false;
        el('popularSubtitle').value = pc.SubtitleBurnIn || 'Never';
        el('popularTransitionWindow').value = pc.TransitionWindowMinutes || '';
        blockEditors.setValue(migrateRatingBlocks(pc));
        el('popularCategory').value = pc.Category || 'None';
        el('popularEpisodesPerBlock').value = pc.EpisodesPerBlock || 4;
        el('popularEpisodeOrder').value = pc.ShuffleEpisodes ? 'random' : 'air';
        el('popularKeepMultiPart').checked = pc.KeepMultiPartTogether !== false;
        el('popularShowBumpers').checked = pc.ShowBumpers !== false;
        el('popularIncludeEpisodes').checked = pc.IncludeEpisodes !== false;
        el('popularIncludeMovies').checked = pc.IncludeMovies !== false;
        el('popularIncludeSpecials').checked = !!pc.IncludeSpecials;
        el('popularLoopMode').value = pc.LoopMode || (pc.Shuffle === false ? 'Alphabetical' : 'Shuffle');
        saveBar.setDirty(false);
    }

    // Triggers Jellyfin's built-in guide refresh so a save propagates to Live TV right away, and the buffer cards
    // task when the channel has a placeholder block, so a changed placeholder card is rendered before its window.
    function refreshGuide() {
        return ApiClient.getScheduledTasks().then(function (tasks) {
            var task = (tasks || []).filter(function (t) { return t.Key === 'RefreshGuide'; })[0];
            var cards = (tasks || []).filter(function (t) { return t.Key === 'LiveChannelsGenerateBufferCards'; })[0];
            var started = [];
            if (task) started.push(ApiClient.startScheduledTask(task.Id));
            if (cards && blockEditors && blockEditors.getValue().some(function (b) { return b.IsPlaceholder; })) started.push(ApiClient.startScheduledTask(cards.Id));
            return Promise.all(started);
        }).catch(function () { /* best effort */ });
    }

    // failSave
    // Shows why a save failed on the Save Bar's failed line, after the bar draws its own.
    function failSave(message) {
        setTimeout(function () { saveBar.setState('failed', message); }, 0);
        return Promise.reject(new Error(message));
    }

    // savePopular
    // Saves the Popular Channel. The Save Bar runs it and shows its outcome.
    //
    // Output
    // [Promise]: Settles once the save is done.
    function savePopular() {
        if (!blockEditors || !blockEditors.isValid()) return failSave('Fix the fields marked in red.');
        // Read the latest config so settings on the other tabs are preserved, then update just the popular channel.
        return Shared.getConfig().then(function (fresh) {
            var pc = fresh.PopularChannel || {};
            pc.Enabled = el('popularEnabled').checked;
            pc.Name = (el('popularName').value || '').trim() || 'Popular';
            // The number and content are fixed; the logo always uses the symbol on this channel.
            pc.Number = 0;
            pc.LogoStyle = 'Symbol';
            pc.LogoSymbol = (el('popularIcon').value || '').trim();
            pc.LogoShowName = el('popularShowName').checked;
            pc.SubtitleBurnIn = el('popularSubtitle').value;
            // The blocks are authoritative, so neutralise the legacy single-band fields to keep them from double-applying.
            pc.RatingBlocks = blockEditors.getValue();
            pc.TransitionWindowMinutes = Math.min(1440, Math.max(0, parseInt(el('popularTransitionWindow').value, 10) || 0));
            pc.MinOfficialRating = '';
            pc.MaxOfficialRating = '';
            pc.IncludeUnrated = true;
            pc.Category = el('popularCategory').value;
            pc.EpisodesPerBlock = Math.max(1, parseInt(el('popularEpisodesPerBlock').value, 10) || 1);
            pc.ShuffleEpisodes = el('popularEpisodeOrder').value === 'random';
            pc.KeepMultiPartTogether = el('popularKeepMultiPart').checked;
            pc.ShowBumpers = el('popularShowBumpers').checked;
            pc.IncludeEpisodes = el('popularIncludeEpisodes').checked;
            pc.IncludeMovies = el('popularIncludeMovies').checked;
            pc.IncludeSpecials = el('popularIncludeSpecials').checked;
            pc.LoopMode = el('popularLoopMode').value;
            pc.Shuffle = pc.LoopMode === 'Shuffle';
            fresh.PopularChannel = pc;
            return Shared.saveConfig(fresh);
        }).then(function () {
            refreshGuide();
        }, function (err) {
            // Surface the server's validation message (like the settings page does) instead of a bare failure.
            var read = (err && err.responseText) ? Promise.resolve(err.responseText) : (err && err.text ? err.text() : Promise.resolve(''));
            return read.then(function (t) { return t ? t.replace(/^"|"$/g, '') : 'Could not save.'; }, function () { return 'Could not save.'; })
                .then(failSave);
        });
    }

    function bind() {
        saveBar = createSaveBar({ fill: true, savedText: 'Saved. Refreshing Live TV…', onSave: savePopular });
        el('popularSaveBar').appendChild(saveBar.element);
        // Any edit shows the Save Bar's unsaved line.
        el('LiveChannelsPopularForm').addEventListener('input', function () { saveBar.setDirty(true); });
        el('LiveChannelsPopularForm').addEventListener('change', function () { saveBar.setDirty(true); });
    }

    function load() {
        Shared.initCollapsibles();
        // Populate the rating options before applying the saved values to them.
        loadRatings().then(function (info) {
            ensureBlockEditors(info);
            return Shared.getConfig().then(loadPopular);
        }).catch(function () {
            Shared.setStatus('popularStatus', 'Could not load the settings. Reload the page to try again.', true);
        });
    }
}
