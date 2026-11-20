import { createConfigPage, createSaveBar, createTranslations, loadTranslations } from './configurationpage?name=livechannels_jpkribs_shared.js';
import { TRANSLATIONS_URL, createBlockEditors, loadRatings, migrateRatingBlocks, readError, saveBarText, tabList, withText } from './configurationpage?name=livechannels_placeholder.js';

export default function (view) {
    'use strict';

    var PLUGIN_ID = 'ac6940fb-aac6-4de8-b622-55a662e23658';
    // The page's strings, empty until load reads them, so the markup keeps its English until then.
    var t = createTranslations({});

    var page = createConfigPage(view, {
        pluginId: PLUGIN_ID,
        tabsKey: 'livechannels',
        tabIndex: 1,
        tabs: function () { return tabList(t); },
        bind: bind,
        load: load
    });
    var Shared = page.shared;

    var blockEditors = null;   // the rating and placeholder block card lists
    var saveBar = null;        // the Save Bar

    function el(id) { return view.querySelector('#' + id); }

    // markDirty
    // Shows the Save Bar's unsaved line.
    function markDirty() {
        if (saveBar) saveBar.setDirty(true);
    }

    // ensureParts
    // Creates the Save Bar and the rating and placeholder block card lists once, with the server's ratings and the
    // page's strings.
    //
    // Inputs
    // ratings [object]: { choices, valueOf } from loadRatings.
    function ensureParts(ratings) {
        if (blockEditors) return;
        saveBar = createSaveBar(withText({ fill: true, onSave: savePopular }, saveBarText(t)));
        el('popularSaveBar').appendChild(saveBar.element);
        blockEditors = createBlockEditors({ ratings: ratings, t: t, onChange: markDirty });
        el('popularRatingBlocks').appendChild(blockEditors.ratingElement);
        el('popularPlaceholderBlocks').appendChild(blockEditors.placeholderElement);
    }

    function loadPopular(config) {
        var pc = config.PopularChannel || {};
        el('popularEnabled').checked = pc.Enabled !== false;
        el('popularName').value = pc.Name || t.tr('TabPopular');
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

    // savePopular
    // Saves the Popular Channel. The Save Bar runs it and shows its outcome, including the server's reason.
    //
    // Output
    // [Promise]: Settles once the save is done.
    function savePopular() {
        if (!blockEditors || !blockEditors.isValid()) return Promise.reject(new Error(t.tr('ErrorFixMarkedFields')));
        // Read the latest config so settings on the other tabs are preserved, then update just the popular channel.
        return Shared.getConfig().then(function (fresh) {
            var pc = fresh.PopularChannel || {};
            pc.Enabled = el('popularEnabled').checked;
            pc.Name = (el('popularName').value || '').trim() || t.tr('TabPopular');
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
            return readError(err, t.tr('ErrorCouldNotSave')).then(function (message) { throw new Error(message); });
        });
    }

    function bind() {
        // Any edit shows the Save Bar's unsaved line.
        el('LiveChannelsPopularForm').addEventListener('input', markDirty);
        el('LiveChannelsPopularForm').addEventListener('change', markDirty);
    }

    function load() {
        Shared.initCollapsibles();
        // Populate the rating options before applying the saved values to them.
        Promise.all([loadRatings(), loadTranslations(TRANSLATIONS_URL)]).then(function (results) {
            t = results[1];
            t.apply(view);
            ensureParts(results[0]);
            return Shared.getConfig().then(loadPopular);
        }).catch(function () {
            if (saveBar) saveBar.setState('failed', t.tr('ErrorCouldNotLoadSettings'));
        });
    }
}
