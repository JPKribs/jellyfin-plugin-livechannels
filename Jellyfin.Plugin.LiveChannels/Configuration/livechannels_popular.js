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
    var ratings = [];          // [{ Name, Value }]
    var ratingOptions = '';    // cached <option> html for the per-block rating selects
    var popularConfig = null;  // the PopularChannel object being edited (holds RatingBlocks)

    function el(id) { return view.querySelector('#' + id); }

    // Populates the kids-rating dropdown and caches the rating options for the block editor.
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
            ratingOptions = ratings.map(function (r) {
                return '<option value="' + Shared.escapeHtml(r.Name) + '">' + Shared.escapeHtml(r.Name) + '</option>';
            }).join('');
        }).catch(function () {
            ratingOptions = '';
        });
    }

    // MARK: Rating blocks (shared design with the channel editor)

    function pad2(n) { return (n < 10 ? '0' : '') + n; }
    function minutesToTime(m) { m = ((m % 1440) + 1440) % 1440; return pad2(Math.floor(m / 60)) + ':' + pad2(m % 60); }
    function timeToMinutes(text) { var p = (text || '').split(':'); var mins = ((parseInt(p[0], 10) || 0) * 60) + (parseInt(p[1], 10) || 0); return ((mins % 1440) + 1440) % 1440; }
    function newRatingBlock() { return { MinOfficialRating: '', MaxOfficialRating: '', IncludeUnrated: true, IsKids: false, Period: 'AllDay', StartMinutes: 0, EndMinutes: 0 }; }
    function ratingValue(name) { if (!name) return null; for (var i = 0; i < ratings.length; i++) { if (ratings[i].Name === name) return ratings[i].Value; } return null; }
    function coerceBand(minEl, maxEl, changed) { var mn = ratingValue(minEl.value), mx = ratingValue(maxEl.value); if (mn === null || mx === null || mn <= mx) return; if (changed === 'min') { maxEl.value = minEl.value; } else { minEl.value = maxEl.value; } }

    function migrateRatingBlocks(pc) {
        if (pc.RatingBlocks && pc.RatingBlocks.length) return pc.RatingBlocks;
        if (pc.MinOfficialRating || pc.MaxOfficialRating || pc.IncludeUnrated === false) {
            return [{ MinOfficialRating: pc.MinOfficialRating || '', MaxOfficialRating: pc.MaxOfficialRating || '', IncludeUnrated: pc.IncludeUnrated !== false, Period: 'AllDay', StartMinutes: 0, EndMinutes: 0 }];
        }
        return [];
    }

    function renderRatingBlocks() {
        var host = el('popularRatingBlocks');
        host.innerHTML = '';
        if (!popularConfig) return;
        popularConfig.RatingBlocks = popularConfig.RatingBlocks || [];
        if (!popularConfig.RatingBlocks.length) {
            host.innerHTML = '<div class="jpk-empty-section">No rating blocks. Any rating airs at any time. Add one to restrict by rating or time of day.</div>';
            return;
        }
        popularConfig.RatingBlocks.forEach(function (block, index) { host.appendChild(buildBlockCard(block, index)); });
    }

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
            '<div class="checkboxContainer"><label class="emby-checkbox-label">' +
                '<input type="checkbox" is="emby-checkbox" class="lc-block-kids" /><span class="checkboxLabel">Tag as kids</span></label></div>' +
            '<div class="selectContainer"><label class="selectLabel">Period</label>' +
                '<select class="lc-block-period jpk-selector-dropdown"><option value="AllDay">All day</option><option value="Custom">Custom</option></select></div>' +
            '<div class="lc-block-times">' +
                '<div class="inputContainer"><label class="inputLabel">Start time</label><input is="emby-input" type="time" class="lc-block-start" /></div>' +
                '<div class="inputContainer"><label class="inputLabel">End time</label><input is="emby-input" type="time" class="lc-block-end" /></div>' +
            '</div>';

        var min = card.querySelector('.lc-block-min');
        var max = card.querySelector('.lc-block-max');
        var unrated = card.querySelector('.lc-block-unrated');
        var kids = card.querySelector('.lc-block-kids');
        var period = card.querySelector('.lc-block-period');
        var times = card.querySelector('.lc-block-times');
        var start = card.querySelector('.lc-block-start');
        var end = card.querySelector('.lc-block-end');

        min.value = block.MinOfficialRating || '';
        max.value = block.MaxOfficialRating || '';
        unrated.checked = block.IncludeUnrated !== false;
        kids.checked = block.IsKids === true;
        period.value = block.Period || 'AllDay';
        start.value = minutesToTime(block.StartMinutes || 0);
        end.value = minutesToTime(block.EndMinutes || 0);

        function syncTimes() { times.classList.toggle('hidden', period.value !== 'Custom'); }
        syncTimes();

        min.addEventListener('change', function () { coerceBand(min, max, 'min'); block.MinOfficialRating = min.value; block.MaxOfficialRating = max.value; });
        max.addEventListener('change', function () { coerceBand(min, max, 'max'); block.MinOfficialRating = min.value; block.MaxOfficialRating = max.value; });
        unrated.addEventListener('change', function () { block.IncludeUnrated = unrated.checked; });
        kids.addEventListener('change', function () { block.IsKids = kids.checked; });
        period.addEventListener('change', function () { block.Period = period.value; syncTimes(); });
        start.addEventListener('change', function () { block.StartMinutes = timeToMinutes(start.value); });
        end.addEventListener('change', function () { block.EndMinutes = timeToMinutes(end.value); });

        card.querySelector('.lc-remove').addEventListener('click', function () {
            popularConfig.RatingBlocks.splice(index, 1);
            renderRatingBlocks();
        });

        return card;
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
        el('popularIncludeEpisodes').checked = pc.IncludeEpisodes !== false;
        el('popularIncludeMovies').checked = pc.IncludeMovies !== false;
        el('popularIncludeSpecials').checked = !!pc.IncludeSpecials;
        el('popularLoopMode').value = pc.LoopMode || (pc.Shuffle === false ? 'Alphabetical' : 'Shuffle');
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
            // Blocks are mutated live on the cards; the transition window is read here. The blocks are authoritative,
            // so neutralise the legacy single-band fields to keep them from double-applying.
            pc.RatingBlocks = (popularConfig && popularConfig.RatingBlocks) || [];
            pc.TransitionWindowMinutes = Math.max(0, parseInt(el('popularTransitionWindow').value, 10) || 0);
            pc.MinOfficialRating = '';
            pc.MaxOfficialRating = '';
            pc.IncludeUnrated = true;
            pc.Category = el('popularCategory').value;
            pc.EpisodesPerBlock = Math.max(1, parseInt(el('popularEpisodesPerBlock').value, 10) || 1);
            pc.ShuffleEpisodes = el('popularEpisodeOrder').value === 'random';
            pc.KeepMultiPartTogether = el('popularKeepMultiPart').checked;
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
        }).catch(function () {
            Shared.setStatus('popularStatus', 'Save failed.', true);
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
