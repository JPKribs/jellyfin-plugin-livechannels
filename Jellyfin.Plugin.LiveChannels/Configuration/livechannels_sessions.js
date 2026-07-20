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
    var _timer = null;

    function el(id) { return view.querySelector('#' + id); }

    // Whole hours and minutes since the stream started, as H:MM.
    function formatElapsed(startedUtc) {
        var started = new Date(startedUtc).getTime();
        if (isNaN(started)) { return '—'; }
        var seconds = Math.max(0, Math.floor((Date.now() - started) / 1000));
        var h = Math.floor(seconds / 3600);
        var m = Math.floor((seconds % 3600) / 60);
        return h + ':' + (m < 10 ? '0' : '') + m;
    }

    function formatStarted(startedUtc) {
        var date = new Date(startedUtc);
        if (isNaN(date.getTime())) { return '—'; }
        return date.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
    }

    // The encode speed as its ratio followed by a status dot ("1.01x ●"): green keeping up (1.0x and over),
    // yellow slightly behind (0.95 to 1.0x), red falling behind (under 0.95x), and a muted dot with a dash
    // until ffmpeg reports a first reading.
    function speedCell(speed) {
        var color, label, text;
        if (typeof speed !== 'number' || speed <= 0) {
            color = 'rgba(255,255,255,0.25)'; label = 'Waiting for a reading'; text = '—';
        } else if (speed >= 1.0) {
            color = '#3fb950'; label = 'Keeping up'; text = speed.toFixed(2) + 'x';
        } else if (speed >= 0.95) {
            color = '#d29922'; label = 'Slightly behind'; text = speed.toFixed(2) + 'x';
        } else {
            color = '#f85149'; label = 'Falling behind'; text = speed.toFixed(2) + 'x';
        }
        return '<span class="jpk-mono">' + text + '</span> <span class="lc-speed-dot" style="color:' + color + '" title="' + label + '">●</span>';
    }

    function row(label, value) {
        return '<div class="jpk-record-row"><span class="jpk-record-label">' + label +
            '</span><span class="jpk-mono">' + value + '</span></div>';
    }

    // Session id to { url } (a loaded blob object URL), { pending: true }, or { failed: true }. The logo is
    // served by our controller, and from Jellyfin 10.11 a query-string api_key no longer authenticates, so an
    // <img src> URL cannot carry the token. Each logo is fetched once with the token in a header and shown as
    // a local object URL; the cache keeps the 5s poll from refetching, and pruneLogos revokes what is stale.
    var _logoCache = {};

    function ensureLogo(id) {
        if (_logoCache[id]) { return; }
        _logoCache[id] = { pending: true };
        fetch(ApiClient.getUrl('livechannels/sessions/' + encodeURIComponent(id) + '/logo'), {
            headers: { 'X-Emby-Token': ApiClient.accessToken() }
        }).then(function (r) {
            if (!r.ok) { throw new Error('logo ' + r.status); }
            return r.blob();
        }).then(function (blob) {
            _logoCache[id] = { url: URL.createObjectURL(blob) };
            // The card may have re-rendered while the fetch ran; update whichever img is in the DOM now.
            var img = el('sessionList').querySelector('.lc-session-logo[data-session-id="' + id + '"]');
            if (img) { img.src = _logoCache[id].url; img.style.visibility = ''; }
        }).catch(function () {
            _logoCache[id] = { failed: true };
        });
    }

    function pruneLogos(sessions) {
        var live = {};
        (sessions || []).forEach(function (s) { live[s.Id] = true; });
        Object.keys(_logoCache).forEach(function (id) {
            if (!live[id]) {
                if (_logoCache[id].url) { URL.revokeObjectURL(_logoCache[id].url); }
                delete _logoCache[id];
            }
        });
    }

    function sessionCard(s) {
        // Hidden until its blob URL is ready (or already cached), so no broken-image icon ever shows.
        var cached = _logoCache[s.Id];
        var logoImg = cached && cached.url
            ? '<img class="lc-session-logo" data-session-id="' + Shared.escapeHtml(s.Id) + '" src="' + cached.url + '" alt="" />'
            : '<img class="lc-session-logo" data-session-id="' + Shared.escapeHtml(s.Id) + '" alt="" style="visibility:hidden" />';
        // A winding-down session (its viewer left; the encoder stays warm briefly so a returning viewer re-tunes
        // instantly) is dimmed and labelled with its countdown. Kill still stops it right away.
        var winding = typeof s.StopsInSeconds === 'number';
        var windingNote = winding
            ? '<div class="lc-session-stopping" style="color:#d29922;font-size:0.82em;margin-top:2px;" title="The viewer left; the encoder stays warm briefly so tuning back in is instant. Kill stops it now.">' +
                'No viewers — stopping in ~' + s.StopsInSeconds + 's</div>'
            : '';
        return '<div class="jpk-record-card lc-session" data-id="' + Shared.escapeHtml(s.Id) + '" data-name="' + Shared.escapeHtml(s.Name || '') + '"' +
            (winding ? ' style="opacity:0.6"' : '') + ' title="Show this session\'s ffmpeg logs">' +
            '<div class="lc-session-head">' +
                logoImg +
                '<div class="lc-session-title">' +
                    '<div class="lc-session-name"><span class="lc-session-number">' + s.Number + '</span>' + Shared.escapeHtml(s.Name || '') + '</div>' +
                    windingNote +
                '</div>' +
            '</div>' +
            '<div class="lc-session-rows">' +
                row('Started', formatStarted(s.StartedUtc)) +
                row('Streaming for', formatElapsed(s.StartedUtc)) +
                row('Speed', speedCell(s.Speed)) +
            '</div>' +
        '</div>';
    }

    function render(sessions) {
        var list = sessions || [];
        Shared.setVisible('sessionsEmpty', list.length === 0);
        el('sessionList').innerHTML = list.map(sessionCard).join('');
        // Hide a logo that fails to load rather than showing a broken-image icon.
        var imgs = el('sessionList').querySelectorAll('.lc-session-logo');
        for (var i = 0; i < imgs.length; i++) {
            imgs[i].addEventListener('error', function () { this.style.visibility = 'hidden'; });
        }

        // Drop (and revoke) logos for sessions that have ended, then fetch any this render is missing.
        pruneLogos(list);
        list.forEach(function (s) { ensureLogo(s.Id); });
    }

    function loadSessions() {
        return ApiClient.getJSON(ApiClient.getUrl('livechannels/sessions')).then(render).catch(function () {
            Shared.setStatus('sessionsStatus', 'Could not load sessions.', true);
        });
    }

    function killSession(id) {
        Shared.setStatus('sessionsStatus', 'Stopping…', false);
        return ApiClient.ajax({ type: 'DELETE', url: ApiClient.getUrl('livechannels/sessions/' + encodeURIComponent(id)) }).then(function () {
            Shared.setStatus('sessionsStatus', '', false);
            return loadSessions();
        }).catch(function () {
            Shared.setStatus('sessionsStatus', 'Could not stop that stream.', true);
        });
    }

    // --- ffmpeg log modal: selecting a session shows every command it spawned plus each process's exit
    // summary and stderr tail. Copy puts the whole log on the clipboard; Kill stops the stream from here.

    var _logSessionId = null;

    function loadLog() {
        if (!_logSessionId) { return Promise.resolve(); }
        return ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('livechannels/sessions/' + encodeURIComponent(_logSessionId) + '/log') })
            .then(function (r) { return r && typeof r.text === 'function' ? r.text() : Promise.resolve(String(r || '')); })
            .then(function (text) {
                el('lcLogText').textContent = text || 'No log yet — the session is still starting.';
            }).catch(function () {
                el('lcLogText').textContent = 'Could not load the log (the session may have just closed).';
            });
    }

    function openLog(id, name) {
        _logSessionId = id;
        el('lcLogTitle').textContent = name || 'Session';
        el('lcLogText').textContent = 'Loading…';
        Shared.setVisible('lcLogDialog', true);
        loadLog();
    }

    function closeLog() {
        _logSessionId = null;
        Shared.setVisible('lcLogDialog', false);
    }

    function copyLog() {
        var text = el('lcLogText').textContent || '';
        var done = function () {
            var label = el('lcLogCopy').querySelector('span:last-child') || el('lcLogCopy');
            var original = label.textContent;
            label.textContent = 'Copied!';
            setTimeout(function () { label.textContent = original; }, 1500);
        };
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(text).then(done).catch(function () { legacyCopy(text); done(); });
        } else {
            legacyCopy(text);
            done();
        }
    }

    // Clipboard fallback for non-secure contexts (plain-HTTP dashboards), where navigator.clipboard is absent.
    function legacyCopy(text) {
        var area = document.createElement('textarea');
        area.value = text;
        area.style.position = 'fixed';
        area.style.opacity = '0';
        document.body.appendChild(area);
        area.select();
        try { document.execCommand('copy'); } catch (e) { /* best effort */ }
        document.body.removeChild(area);
    }

    function bind() {
        // Delegate so the cards keep working across re-renders: selecting a session opens its log modal.
        el('sessionList').addEventListener('click', function (e) {
            var card = e.target.closest('.lc-session');
            if (card) { openLog(card.getAttribute('data-id'), card.getAttribute('data-name')); }
        });

        el('lcLogClose').addEventListener('click', closeLog);
        el('lcLogRefresh').addEventListener('click', loadLog);
        el('lcLogCopy').addEventListener('click', copyLog);
        el('lcLogKill').addEventListener('click', function () {
            var id = _logSessionId;
            closeLog();
            if (id) { killSession(id); }
        });

        // A click on the dimmed backdrop (not the dialog content) also closes.
        el('lcLogDialog').addEventListener('click', function (e) {
            if (e.target === el('lcLogDialog')) { closeLog(); }
        });
    }

    function startPolling() {
        stopPolling();
        _timer = setInterval(loadSessions, 5000);
    }

    function stopPolling() {
        if (_timer) { clearInterval(_timer); _timer = null; }
    }

    view.addEventListener('viewshow', function () {
        _sharedPromise.then(function () {
            setTabs('livechannels', 2, TABS);
            if (!_bound) { bind(); _bound = true; }
            loadSessions();
            startPolling();
        });
    });

    view.addEventListener('viewhide', stopPolling);

    // Release every blob object URL when the page is torn down; nothing else revokes the last set.
    view.addEventListener('viewdestroy', function () { pruneLogos([]); });
}
