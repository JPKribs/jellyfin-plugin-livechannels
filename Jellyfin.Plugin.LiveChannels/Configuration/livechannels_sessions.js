import { createConfigPage, createLogViewer, createStatusDot, openDialog } from './configurationpage?name=livechannels_jpkribs_shared.js';

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
        tabIndex: 2,
        tabs: TABS,
        bind: bind,
        load: function () {
            // A fresh poller per show: the page stops it on viewhide.
            page.createPoller(loadSessions, 5000).start();
        },
        onHide: function () {
            // Close an open log so its Escape and focus handlers do not outlive the page.
            if (logDialog) { logDialog.dismiss(); }
        }
    });
    var Shared = page.shared;
    var logDialog = null;

    function el(id) { return view.querySelector('#' + id); }

    // Whole hours and minutes since the stream started, as H:MM.
    function formatElapsed(startedUtc) {
        var started = new Date(startedUtc).getTime();
        if (isNaN(started)) { return '-'; }
        var seconds = Math.max(0, Math.floor((Date.now() - started) / 1000));
        var h = Math.floor(seconds / 3600);
        var m = Math.floor((seconds % 3600) / 60);
        return h + ':' + (m < 10 ? '0' : '') + m;
    }

    function formatStarted(startedUtc) {
        var date = new Date(startedUtc);
        if (isNaN(date.getTime())) { return '-'; }
        return date.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
    }

    // speedCell
    // Shows the encode speed as its ratio and a Status Dot. Green keeps up at 1.0x and over, orange is slightly behind
    // from 0.95x, red falls behind under that, and gray waits for ffmpeg's first reading.
    //
    // Inputs
    // speed [number]: The encode speed ratio, or nothing before the first reading.
    //
    // Output
    // [string]: The cell markup.
    function speedCell(speed) {
        var color, label, text;
        if (typeof speed !== 'number' || speed <= 0) {
            color = 'gray'; label = 'Waiting for a Reading'; text = '-';
        } else if (speed >= 1.0) {
            color = 'green'; label = 'Keeping Up'; text = speed.toFixed(2) + 'x';
        } else if (speed >= 0.95) {
            color = 'orange'; label = 'Slightly Behind'; text = speed.toFixed(2) + 'x';
        } else {
            color = 'red'; label = 'Falling Behind'; text = speed.toFixed(2) + 'x';
        }
        return '<span class="lc-speed"><span class="base-mono">' + text + '</span>' + createStatusDot({ color: color, label: label }).element.outerHTML + '</span>';
    }

    function row(label, value) {
        return '<div class="base-record-row"><span class="base-record-label">' + label +
            '</span><span class="base-mono">' + value + '</span></div>';
    }

    // Session id to { url } (a loaded blob object URL), { pending: true }, or { failed: true }. The logo is
    // served by our controller, and from Jellyfin 10.11 a query-string api_key no longer authenticates, so an
    // <img src> URL cannot carry the token. Each logo is fetched once with the token in a header and shown as
    // a local object URL; the cache keeps the 5s poll from refetching, and pruneLogos revokes what is stale.
    var _logoCache = {};

    function ensureLogo(id) {
        if (_logoCache[id]) { return; }
        _logoCache[id] = { pending: true };
        Shared.fetchObjectUrl('livechannels/sessions/' + encodeURIComponent(id) + '/logo').then(function (url) {
            _logoCache[id] = { url: url };
            // The card may have re-rendered while the fetch ran; update whichever img is in the DOM now.
            var img = el('sessionList').querySelector('.lc-session-logo[data-session-id="' + id + '"]');
            if (img) { img.src = _logoCache[id].url; img.classList.remove('lc-pending'); }
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
            : '<img class="lc-session-logo lc-pending" data-session-id="' + Shared.escapeHtml(s.Id) + '" alt="" />';
        // A winding-down session (its viewer left; the encoder stays warm briefly so a returning viewer re-tunes
        // instantly) is dimmed and labelled with its countdown. Kill still stops it right away.
        var winding = typeof s.StopsInSeconds === 'number';
        var windingNote = winding
            ? '<div class="lc-session-stopping" title="The viewer left, so the encoder stays warm briefly for a quick return.">' +
                'No viewers. Stopping in ~' + s.StopsInSeconds + 's</div>'
            : '';
        return '<div class="base-record-card lc-session' + (winding ? ' lc-winding' : '') + '" data-id="' + Shared.escapeHtml(s.Id) + '" data-name="' + Shared.escapeHtml(s.Name || '') + '"' +
            ' title="Show the ffmpeg log" role="button" tabindex="0">' +
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
            imgs[i].addEventListener('error', function () { this.classList.add('lc-pending'); });
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

    // The ffmpeg log dialog. Selecting a session shows every command it ran with each process's exit summary and stderr
    // tail. The Log Viewer copies the whole log, and Kill stops the stream from here.

    var _logSessionId = null;
    var logViewer = null;

    // loadLog
    // Reads the open session's ffmpeg log into the Log Viewer.
    //
    // Output
    // [Promise]: Settles once the log is shown.
    function loadLog() {
        if (!_logSessionId || !logViewer) { return Promise.resolve(); }
        var viewer = logViewer;
        return ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('livechannels/sessions/' + encodeURIComponent(_logSessionId) + '/log') })
            .then(function (r) { return r && typeof r.text === 'function' ? r.text() : Promise.resolve(String(r || '')); })
            .then(function (text) {
                viewer.setValue(text ? text.split('\n') : ['No log yet. The session is still starting.']);
            }).catch(function () {
                viewer.setValue([{ text: 'Could not load the log. The session may have just closed.', level: 'error' }]);
            });
    }

    // openLog
    // Opens the log dialog for one session.
    //
    // Inputs
    // id [string]: The session id.
    // name [string]: The channel name, used as the title.
    function openLog(id, name) {
        _logSessionId = id;
        logViewer = createLogViewer({ maxLines: 20000, autoScroll: false, emptyText: 'Loading…', height: '55vh', label: 'ffmpeg log' });
        logDialog = openDialog({
            title: name || 'Session',
            body: logViewer.element,
            size: 'large',
            buttons: [
                { text: 'Kill', className: 'base-button destructive', action: function (close) {
                    var target = _logSessionId;
                    close();
                    if (target) { killSession(target); }
                } },
                { text: 'Refresh', className: 'base-button primary', action: function () { loadLog(); } }
            ],
            onClose: function () {
                _logSessionId = null;
                logDialog = null;
                if (logViewer) { logViewer.destroy(); logViewer = null; }
            }
        });
        loadLog();
    }

    function bind() {
        // Delegate so the cards keep working across re-renders: selecting a session opens its log.
        el('sessionList').addEventListener('click', function (e) {
            var card = e.target.closest('.lc-session');
            if (card) { openLog(card.getAttribute('data-id'), card.getAttribute('data-name')); }
        });
        el('sessionList').addEventListener('keydown', function (e) {
            var card = e.target.closest('.lc-session');
            if (card && (e.key === 'Enter' || e.key === ' ')) {
                e.preventDefault();
                openLog(card.getAttribute('data-id'), card.getAttribute('data-name'));
            }
        });
    }

    // Release every blob object URL when the page is torn down; nothing else revokes the last set.
    view.addEventListener('viewdestroy', function () { pruneLogos([]); });
}
