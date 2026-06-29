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

    function formatSpeed(speed) {
        return (typeof speed === 'number' && speed > 0) ? speed.toFixed(1) + 'x' : '—';
    }

    function row(label, value) {
        return '<div class="jpk-record-row"><span class="jpk-record-label">' + label +
            '</span><span class="jpk-mono">' + value + '</span></div>';
    }

    function sessionCard(s) {
        // The logo is served by our controller; pass the token in the URL since an <img> sends no auth header.
        var logoUrl = ApiClient.getUrl('livechannels/sessions/' + encodeURIComponent(s.Id) + '/logo', { api_key: ApiClient.accessToken() });
        return '<div class="jpk-record-card lc-session">' +
            '<div class="lc-session-head">' +
                '<img class="lc-session-logo" src="' + logoUrl + '" alt="" />' +
                '<div class="lc-session-title">' +
                    '<div class="lc-session-name"><span class="lc-session-number">' + s.Number + '</span>' + Shared.escapeHtml(s.Name || '') + '</div>' +
                '</div>' +
                '<button is="emby-button" type="button" class="raised jpk-icon-btn jpk-button-destructive lc-kill" data-id="' + Shared.escapeHtml(s.Id) + '" title="Stop this stream">' +
                    '<span class="material-icons" aria-hidden="true">stop</span><span>Kill</span>' +
                '</button>' +
            '</div>' +
            '<div class="lc-session-rows">' +
                row('Started', formatStarted(s.StartedUtc)) +
                row('Streaming for', formatElapsed(s.StartedUtc)) +
                row('Speed', formatSpeed(s.Speed)) +
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

    function bind() {
        // Delegate so the buttons keep working across re-renders.
        el('sessionList').addEventListener('click', function (e) {
            var btn = e.target.closest('.lc-kill');
            if (btn) { killSession(btn.getAttribute('data-id')); }
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
}
