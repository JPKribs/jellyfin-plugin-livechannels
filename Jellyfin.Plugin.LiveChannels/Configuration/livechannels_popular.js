import { createConfigPage } from './configurationpage?name=livechannels_jpkribs_shared.js';
import { loadRatings, migrateRatingBlocks, newPlaceholderBlock, newRatingBlock, renderBlocks } from './configurationpage?name=livechannels_placeholder.js';

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

    var ratings = { options: '', valueOf: function () { return null; } };
    var popularConfig = null;  // the PopularChannel object being edited (holds RatingBlocks)

    function el(id) { return view.querySelector('#' + id); }

    // renderRatingBlocks
    // Draws the Popular Channel's rating and placeholder blocks.
    function renderRatingBlocks() {
        if (!popularConfig) return;
        popularConfig.RatingBlocks = popularConfig.RatingBlocks || [];
        renderBlocks(el('popularRatingBlocks'), popularConfig.RatingBlocks, ratings, Shared);
    }

    function loadPopular(config) {
        var pc = config.PopularChannel || {};
        el('popularEnabled').checked = pc.Enabled !== false;
        el('popularName').value = pc.Name || 'Popular';
        el('popularIcon').value = pc.LogoSymbol || '';
        el('popularShowName').checked = pc.LogoShowName !== false;
        el('popularSubtitle').value = pc.SubtitleBurnIn || 'Never';
        popularConfig = pc;
        pc.RatingBlocks = migrateRatingBlocks(pc);
        el('popularTransitionWindow').value = pc.TransitionWindowMinutes || '';
        renderRatingBlocks();
        el('popularCategory').value = pc.Category || 'None';
        el('popularEpisodesPerBlock').value = pc.EpisodesPerBlock || 4;
        el('popularEpisodeOrder').value = pc.ShuffleEpisodes ? 'random' : 'air';
        el('popularKeepMultiPart').checked = pc.KeepMultiPartTogether !== false;
        el('popularShowBumpers').checked = pc.ShowBumpers !== false;
        el('popularIncludeEpisodes').checked = pc.IncludeEpisodes !== false;
        el('popularIncludeMovies').checked = pc.IncludeMovies !== false;
        el('popularIncludeSpecials').checked = !!pc.IncludeSpecials;
        el('popularLoopMode').value = pc.LoopMode || (pc.Shuffle === false ? 'Alphabetical' : 'Shuffle');
    }

    // Triggers Jellyfin's built-in guide refresh so a save propagates to Live TV right away, and the buffer cards
    // task when the channel has a placeholder block, so a changed placeholder card is rendered before its window.
    function refreshGuide() {
        return ApiClient.getScheduledTasks().then(function (tasks) {
            var task = (tasks || []).filter(function (t) { return t.Key === 'RefreshGuide'; })[0];
            var cards = (tasks || []).filter(function (t) { return t.Key === 'LiveChannelsGenerateBufferCards'; })[0];
            var started = [];
            if (task) started.push(ApiClient.startScheduledTask(task.Id));
            if (cards && popularConfig && (popularConfig.RatingBlocks || []).some(function (b) { return b.IsPlaceholder; })) started.push(ApiClient.startScheduledTask(cards.Id));
            return Promise.all(started);
        }).catch(function () { /* best effort */ });
    }

    function savePopular() {
        // Read the latest config so settings on the other tabs are preserved, then update just the popular channel.
        Shared.getConfig().then(function (fresh) {
            var pc = fresh.PopularChannel || {};
            pc.Enabled = el('popularEnabled').checked;
            pc.Name = (el('popularName').value || '').trim() || 'Popular';
            // The number and content are fixed; the logo always uses the symbol on this channel.
            pc.Number = 0;
            pc.LogoStyle = 'Symbol';
            pc.LogoSymbol = (el('popularIcon').value || '').trim();
            pc.LogoShowName = el('popularShowName').checked;
            pc.SubtitleBurnIn = el('popularSubtitle').value;
            // Blocks are mutated live on the cards; the transition window is read here. The blocks are authoritative,
            // so neutralise the legacy single-band fields to keep them from double-applying.
            pc.RatingBlocks = (popularConfig && popularConfig.RatingBlocks) || [];
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
            Shared.setStatus('popularStatus', 'Saved. Refreshing Live TV…', false);
        }).catch(function (err) {
            // Surface the server's validation message (like the settings page does) instead of a bare failure.
            var read = (err && err.responseText) ? Promise.resolve(err.responseText) : (err && err.text ? err.text() : Promise.resolve(''));
            read.then(function (t) {
                Shared.setStatus('popularStatus', t ? t.replace(/^"|"$/g, '') : 'Save failed.', true);
            }, function () {
                Shared.setStatus('popularStatus', 'Save failed.', true);
            });
        });
    }

    function bind() {
        el('btnSavePopular').addEventListener('click', savePopular);
        el('popularAddRatingBlock').addEventListener('click', function () {
            if (!popularConfig) return;
            popularConfig.RatingBlocks = popularConfig.RatingBlocks || [];
            popularConfig.RatingBlocks.push(newRatingBlock());
            renderRatingBlocks();
        });
        el('popularAddPlaceholderBlock').addEventListener('click', function () {
            if (!popularConfig) return;
            popularConfig.RatingBlocks = popularConfig.RatingBlocks || [];
            popularConfig.RatingBlocks.push(newPlaceholderBlock());
            renderRatingBlocks();
        });
    }

    function load() {
        Shared.initCollapsibles();
        // Populate the rating options before applying the saved values to them.
        loadRatings().then(function (info) {
            ratings = info;
            return Shared.getConfig().then(loadPopular);
        }).catch(function () {
            Shared.setStatus('popularStatus', 'Could not load the settings. Reload the page to try again.', true);
        });
    }
}
