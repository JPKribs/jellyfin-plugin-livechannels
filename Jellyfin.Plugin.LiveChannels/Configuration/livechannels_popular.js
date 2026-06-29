export default function (view) {
    'use strict';

    var PLUGIN_ID = 'ac6940fb-aac6-4de8-b622-55a662e23658';
    var TABS = [
        { href: 'configurationpage?name=livechannels_channels', name: 'Channels' },
        { href: 'configurationpage?name=livechannels_popular', name: 'Popular' },
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

    // Populates the rating dropdowns from the server's parental ratings.
    function loadRatings() {
        return ApiClient.getJSON(ApiClient.getUrl('Localization/ParentalRatings')).then(function (list) {
            var seen = {};
            var ratings = [];
            (list || []).forEach(function (r) {
                var score = r.Value;
                if (score === undefined && r.RatingScore) score = r.RatingScore.Score;
                if (r && r.Name && !seen[r.Name]) { seen[r.Name] = true; ratings.push({ Name: r.Name, Value: score || 0 }); }
            });
            ratings.sort(function (a, b) { return a.Value - b.Value; });
            var opts = ratings.map(function (r) {
                return '<option value="' + Shared.escapeHtml(r.Name) + '">' + Shared.escapeHtml(r.Name) + '</option>';
            }).join('');
            el('popularMinRating').innerHTML = '<option value="">No minimum</option>' + opts;
            el('popularMaxRating').innerHTML = '<option value="">No limit</option>' + opts;
            el('popularKidsRating').innerHTML = '<option value="">None</option>' + opts;
        }).catch(function () {
            el('popularMinRating').innerHTML = '<option value="">No minimum</option>';
            el('popularMaxRating').innerHTML = '<option value="">No limit</option>';
            el('popularKidsRating').innerHTML = '<option value="">None</option>';
        });
    }

    function loadPopular(config) {
        var pc = config.PopularChannel || {};
        el('popularEnabled').checked = pc.Enabled !== false;
        el('popularName').value = pc.Name || 'Popular';
        el('popularIcon').value = pc.LogoSymbol || '';
        el('popularShowName').checked = pc.LogoShowName !== false;
        el('popularSubtitle').value = pc.SubtitleBurnIn || 'Never';
        el('popularMinRating').value = pc.MinOfficialRating || '';
        el('popularMaxRating').value = pc.MaxOfficialRating || '';
        el('popularIncludeUnrated').checked = pc.IncludeUnrated !== false;
        el('popularKidsRating').value = pc.KidsRatingThreshold || '';
        el('popularEpisodesPerBlock').value = pc.EpisodesPerBlock || 4;
        el('popularEpisodeOrder').value = pc.ShuffleEpisodes ? 'random' : 'air';
        el('popularKeepMultiPart').checked = pc.KeepMultiPartTogether !== false;
        el('popularIncludeSpecials').checked = !!pc.IncludeSpecials;
        el('popularShuffle').checked = pc.Shuffle !== false;
    }

    // Triggers Jellyfin's built-in guide refresh so a save propagates to Live TV right away.
    function refreshGuide() {
        return ApiClient.getScheduledTasks().then(function (tasks) {
            var task = (tasks || []).filter(function (t) { return t.Key === 'RefreshGuide'; })[0];
            if (task) return ApiClient.startScheduledTask(task.Id);
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
            pc.MinOfficialRating = el('popularMinRating').value;
            pc.MaxOfficialRating = el('popularMaxRating').value;
            pc.IncludeUnrated = el('popularIncludeUnrated').checked;
            pc.KidsRatingThreshold = el('popularKidsRating').value;
            pc.EpisodesPerBlock = Math.max(1, parseInt(el('popularEpisodesPerBlock').value, 10) || 1);
            pc.ShuffleEpisodes = el('popularEpisodeOrder').value === 'random';
            pc.KeepMultiPartTogether = el('popularKeepMultiPart').checked;
            pc.IncludeSpecials = el('popularIncludeSpecials').checked;
            pc.Shuffle = el('popularShuffle').checked;
            fresh.PopularChannel = pc;
            return Shared.saveConfig(fresh);
        }).then(function () {
            refreshGuide();
            Shared.setStatus('popularStatus', 'Saved. Refreshing Live TV…', false);
        }).catch(function () {
            Shared.setStatus('popularStatus', 'Save failed.', true);
        });
    }

    function bind() {
        el('btnSavePopular').addEventListener('click', savePopular);
    }

    view.addEventListener('viewshow', function () {
        _sharedPromise.then(function () {
            setTabs('livechannels', 1, TABS);
            if (!_bound) { bind(); _bound = true; }
            Shared.initCollapsibles();
            // Populate the rating options before applying the saved values to them.
            loadRatings().then(function () {
                Shared.getConfig().then(loadPopular);
            });
        });
    });
}
