import { createConfigPage, createList, createLogViewer, createMediaCard, openDialog } from './configurationpage?name=livechannels_jpkribs_shared.js';

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
    var sessionList = createList({ layout: 'rows', render: function (s) { return sessionCard(s); }, emptyText: 'No channels are streaming right now.', label: 'Sessions' });

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

    // speedStatus
    // Shows the encode speed as a status badge. Green keeps up at 1.0x and over, orange is slightly behind from
    // 0.95x, red falls behind under that, and gray waits for ffmpeg's first reading.
    //
    // Inputs
    // speed [number]: The encode speed ratio, or nothing before the first reading.
    //
    // Output
    // [object]: { status, detail } for the Media Card.
    function speedStatus(speed) {
        if (typeof speed !== 'number' || speed <= 0) return { status: { label: 'Waiting', color: 'gray' }, detail: 'Waiting for a reading' };
        var text = speed.toFixed(2) + 'x';
        if (speed >= 1.0) return { status: { label: text, color: 'green' }, detail: 'Keeping up' };
        if (speed >= 0.95) return { status: { label: text, color: 'orange' }, detail: 'Slightly behind' };
        return { status: { label: text, color: 'red' }, detail: 'Falling behind' };
    }

    // Session id to a promise of its logo's object URL. The logo is served by our controller, and from Jellyfin
    // 10.11 a query-string api_key no longer authenticates, so an image address cannot carry the token. Each
    // logo is fetched once with the token in a header and shown as a local object URL. The cache keeps the 5s
    // poll from refetching, and pruneLogos revokes what is stale.
    var _logoCache = {};

    // logoUrl
    // Fetches a session's logo once and answers with its object URL.
    function logoUrl(id) {
        if (!_logoCache[id]) {
            _logoCache[id] = Shared.fetchObjectUrl('livechannels/sessions/' + encodeURIComponent(id) + '/logo');
            _logoCache[id].then(function (url) { _logoCache[id].url = url; }, function () { /* the card keeps its icon */ });
        }
        return _logoCache[id];
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

    // sessionCard
    // Builds one session as a Media Card that opens its ffmpeg log. A winding-down session (its viewer left, and
    // the encoder stays warm briefly so a returning viewer re-tunes instantly) shows its countdown.
    function sessionCard(s) {
        var speed = speedStatus(s.Speed);
        var winding = typeof s.StopsInSeconds === 'number';
        return createMediaCard({
            shape: 'square',
            icon: 'live_tv',
            image: function () { return logoUrl(s.Id); },
            title: s.Name || '',
            titleSuffix: 'Channel ' + s.Number,
            subtitle: 'Started ' + formatStarted(s.StartedUtc) + ', streaming for ' + formatElapsed(s.StartedUtc),
            status: speed.status,
            detail: speed.detail,
            badges: winding ? [{ label: 'No viewers. Stopping in ~' + s.StopsInSeconds + 's', color: 'orange' }] : [],
            onClick: function () { openLog(s.Id, s.Name); }
        });
    }

    function render(sessions) {
        var list = sessions || [];
        sessionList.setItems(list);
        // Drop (and revoke) logos for sessions that have ended.
        pruneLogos(list);
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
        el('sessionList').appendChild(sessionList.element);
    }

    // Release every blob object URL when the page is torn down; nothing else revokes the last set.
    view.addEventListener('viewdestroy', function () { pruneLogos([]); });
}
