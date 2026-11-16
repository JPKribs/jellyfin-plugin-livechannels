// Draws the dashboard's drawer and header around a plugin page, for the full window shots.
var TABS = [['livechannels_channels', 'Channels'], ['livechannels_popular', 'Popular'], ['livechannels_sessions', 'Sessions'], ['livechannels_settings', 'Settings']];
var NAV = [
    ['Server'], ['dashboard', 'Dashboard'], ['settings', 'General'], ['palette', 'Branding'], ['people', 'Users'], ['library_add', 'Libraries', 1], ['play_circle', 'Playback', 1],
    ['Devices'], ['devices', 'Devices'], ['analytics', 'Activity'],
    ['Live TV'], ['live_tv', 'Live TV'], ['dvr', 'DVR'],
    ['Plugins'], ['extension', 'Plugins', 0, 1],
    ['Advanced'], ['schedule', 'Scheduled Tasks'], ['notes', 'Logs']
];
export function drawChrome(page) {
    var nav = NAV.map(function (n) {
        if (n.length === 1) return '<div class="lcGroup">' + n[0] + '</div>';
        return '<div class="lcItem' + (n[3] ? ' lcActive' : '') + '"><span class="material-icons">' + n[0] + '</span><span>' + n[1] + '</span>'
            + (n[2] ? '<span class="material-icons lcMore">expand_more</span>' : '') + '</div>';
    }).join('');
    var tabs = TABS.map(function (t) { return '<div class="lcTab' + (t[0] === page ? ' lcActive' : '') + '">' + t[1] + '</div>'; }).join('');
    document.getElementById('chrome').innerHTML =
        '<div class="lcDrawer"><div class="lcBrand"><div class="lcBrandIcon"></div><div><div class="lcBrandName">Jellyfin</div><div class="lcBrandVer">12.4.0</div></div></div>' + nav + '</div>'
        + '<div class="lcHeader"><span class="material-icons lcBack">arrow_back</span><div class="lcAvatar"><span class="material-icons">person</span></div><div class="lcTabs">' + tabs + '</div></div>';
}
