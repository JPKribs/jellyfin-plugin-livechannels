// A stand-in ApiClient with generic sample data for the Live Channels documentation screenshots.
window.__errors = [];
window.__posts = [];
window.addEventListener('error', function (e) { window.__errors.push(String(e.message)); });
window.addEventListener('unhandledrejection', function (e) { window.__errors.push('rejection: ' + (e.reason && e.reason.message || e.reason)); });

// MARK: Sample images

// Draws a canvas and returns it as a PNG data address.
function drawPng(w, h, paint) {
    var c = document.createElement('canvas');
    c.width = w; c.height = h;
    paint(c.getContext('2d'), w, h);
    return c.toDataURL('image/png');
}

// A soft two color poster for an item, keyed by its id.
var POSTER_COLORS = [['#2b4c7e', '#567ebb'], ['#7a2e3b', '#c25a4a'], ['#2f5d50', '#5fa38c'], ['#5a3d7a', '#9b72c4'], ['#7a5a20', '#c99a3e'], ['#244a5a', '#3f8fa8']];
var posterCache = {};
function poster(id) {
    if (posterCache[id]) return posterCache[id];
    var n = 0;
    for (var i = 0; i < id.length; i++) n = (n * 31 + id.charCodeAt(i)) >>> 0;
    var pair = POSTER_COLORS[n % POSTER_COLORS.length];
    posterCache[id] = drawPng(120, 180, function (g, w, h) {
        var grad = g.createLinearGradient(0, 0, w, h);
        grad.addColorStop(0, pair[0]); grad.addColorStop(1, pair[1]);
        g.fillStyle = grad; g.fillRect(0, 0, w, h);
        g.fillStyle = 'rgba(255,255,255,0.18)';
        g.beginPath(); g.arc(w * 0.7, h * 0.32, w * 0.28, 0, Math.PI * 2); g.fill();
        g.fillStyle = 'rgba(0,0,0,0.22)';
        g.fillRect(0, h * 0.72, w, h * 0.28);
    });
    return posterCache[id];
}

// The plugin's generated logo, drawn the way the channel editor previews it.
function logoHue(name) {
    var hash = 2166136261;
    for (var i = 0; i < (name || '').length; i++) hash = Math.imul(hash ^ (name.charCodeAt(i) & 0xff), 16777619) >>> 0;
    return hash % 360;
}
// The name on one line, else two, as the plugin fits it.
function fitTitleLines(name) {
    var title = name.toUpperCase();
    if (title.length <= 15) return [title];
    var words = title.split(' '), line1 = '', i = 0;
    while (i < words.length && (line1 ? line1 + ' ' + words[i] : words[i]).length <= 15) { line1 = line1 ? line1 + ' ' + words[i] : words[i]; i++; }
    return [line1, words.slice(i).join(' ')];
}
function generatedLogo(number, name, symbol) {
    return drawPng(256, 256, function (g, size) {
        var hue = logoHue(name);
        var fam = ' -apple-system, Segoe UI, Roboto, sans-serif';
        g.fillStyle = 'hsl(' + hue + ', 65%, 82%)'; g.fillRect(0, 0, size, size);
        g.fillStyle = 'hsl(' + hue + ', 50%, 32%)';
        g.textAlign = 'center'; g.textBaseline = 'middle';
        if (symbol) { g.font = Math.round(size * 0.44) + 'px "LiveChannelsSymbols"'; g.fillText(symbol, size / 2, size / 2); }
        else { g.font = '700 ' + Math.round(size * 0.4) + 'px' + fam; g.fillText(String(number), size / 2, size / 2); }
        g.textBaseline = 'alphabetic';
        g.font = '600 ' + Math.round(size / 13) + 'px' + fam;
        var lines = fitTitleLines(name);
        for (var k = 0; k < lines.length; k++) g.fillText(lines[k], size / 2, size - (size / 11 + (lines.length - 1 - k) * size * 7 / 90));
    });
}
window.__generatedLogo = generatedLogo;

// MARK: Library

var LIBRARIES = [{ ItemId: 'lib-shows', Name: 'Shows' }, { ItemId: 'lib-movies', Name: 'Movies' }, { ItemId: 'lib-mv', Name: 'Music Videos' }];
var GENRES = {
    'lib-shows': ['Animation', 'Comedy', 'Drama', 'Family', 'Mystery', 'Reality', 'Science Fiction', 'Sitcom'],
    'lib-movies': ['Action', 'Adventure', 'Animation', 'Comedy', 'Drama', 'Family', 'Horror', 'Science Fiction', 'Thriller'],
    'lib-mv': ['Electronic', 'Hip-Hop', 'Pop', 'Rock']
};
var ITEMS = [
    { Id: 'm1', Name: 'The Lighthouse Keeper', Type: 'Movie', ProductionYear: 2004, lib: 'lib-movies' },
    { Id: 'm2', Name: 'Northbound Express', Type: 'Movie', ProductionYear: 1997, lib: 'lib-movies' },
    { Id: 'm3', Name: 'Paper Kites', Type: 'Movie', ProductionYear: 2011, lib: 'lib-movies' },
    { Id: 'm4', Name: 'Midnight Harbor', Type: 'Movie', ProductionYear: 1999, lib: 'lib-movies' },
    { Id: 'm5', Name: 'Starfall Ridge', Type: 'Movie', ProductionYear: 2016, lib: 'lib-movies' },
    { Id: 's1', Name: 'Corner Diner', Type: 'Series', ProductionYear: 1991, lib: 'lib-shows' },
    { Id: 's2', Name: 'Space Pups', Type: 'Series', ProductionYear: 1988, lib: 'lib-shows' },
    { Id: 'e1', Name: 'Pilot', Type: 'Episode', SeriesName: 'Corner Diner', ParentIndexNumber: 1, IndexNumber: 1, ProductionYear: 1991, lib: 'lib-shows' },
    { Id: 'mv1', Name: 'Summer Static', Type: 'MusicVideo', ProductionYear: 2019, lib: 'lib-mv' },
    { Id: 'mv2', Name: 'Neon Highway', Type: 'MusicVideo', ProductionYear: 2021, lib: 'lib-mv' },
    { Id: 'hv1', Name: 'Backyard Talent Show', Type: 'Video', ProductionYear: 2018, lib: 'lib-mv' }
];
var BYID = {};
ITEMS.forEach(function (i) { i.ImageTags = { Primary: 'p' }; BYID[i.Id] = i; });
var COLLECTIONS = [{ Id: 'b1', Name: 'Holiday Specials' }, { Id: 'b2', Name: 'Summer Blockbusters' }];
var RATINGS = [['TV-Y', 0], ['TV-Y7', 7], ['G', 1], ['TV-G', 2], ['PG', 10], ['TV-PG', 10], ['PG-13', 13], ['TV-14', 14], ['R', 17], ['TV-MA', 17], ['NC-17', 18]]
    .map(function (r) { return { Name: r[0], Value: r[1] }; });
var CULTURES = [['eng', 'English'], ['fre', 'French'], ['ger', 'German'], ['ita', 'Italian'], ['jpn', 'Japanese'], ['kor', 'Korean'], ['spa', 'Spanish']]
    .map(function (c) { return { ThreeLetterISOLanguageName: c[0], DisplayName: c[1], Name: c[1] }; });
var STUDIOS = ['Bluebird Studios', 'Harbor Light Television', 'Northwind Pictures', 'Sunset Animation'];
var PEOPLE = [{ Id: 'p1', Name: 'Dana Whitfield' }, { Id: 'p2', Name: 'Rowan Ellis' }];

// MARK: Configuration

function block(min, max, kids, start, end) {
    return { MinOfficialRating: min, MaxOfficialRating: max, IncludeUnrated: true, IsKids: kids, Period: start === null ? 'AllDay' : 'Custom', StartMinutes: start || 0, EndMinutes: end || 0 };
}
function placeholder(start, end, name, style) {
    return { IsPlaceholder: true, MinOfficialRating: '', MaxOfficialRating: '', IncludeUnrated: true, IsKids: false, Period: 'Custom', StartMinutes: start, EndMinutes: end,
        PlaceholderName: name, PlaceholderStyle: style, PlaceholderTitle: 'Thank you for watching!', PlaceholderBody: 'We will resume at {time} with {contentName}.', PlaceholderMediaPath: '' };
}
function source(kind, lib, selection, genres, exclude, ids, collection) {
    var l = LIBRARIES.filter(function (x) { return x.ItemId === lib; })[0];
    var c = COLLECTIONS.filter(function (x) { return x.Id === collection; })[0];
    return { Kind: kind, LibraryId: lib || '', LibraryName: l ? l.Name : '', CollectionId: collection || '', CollectionName: c ? c.Name : '',
        Genres: genres || [], ExcludeGenres: exclude || [], MatchAllGenres: false, Selection: selection || 'AllContent', ItemIds: ids || [] };
}
function channel(o) {
    var base = { LogoData: '', LogoContentType: '', LogoStyle: 'Number', LogoSymbol: '', LogoShowName: true, AudioLanguage: '', RatingBlocks: [],
        TransitionWindowMinutes: 0, MinOfficialRating: '', MaxOfficialRating: '', IncludeUnrated: true, Category: 'None', Years: [], MinCommunityRating: 0,
        MinCriticRating: 0, Studios: [], People: [], EpisodesPerBlock: 1, KeepMultiPartTogether: true, ShowBumpers: true, IncludeEpisodes: true,
        IncludeMovies: true, IncludeSpecials: false, IncludeMusicVideos: true, IncludeHomeVideos: false, Shuffle: true, LoopMode: 'Shuffle',
        ShuffleEpisodes: false, FavorKind: 'None', FavorStrength: 'Moderate', SubtitleBurnIn: 'Never', Enabled: true };
    Object.keys(o).forEach(function (k) { base[k] = o[k]; });
    return base;
}

window.__config = {
    TranscodeWidth: 1920, VideoCodec: 'H264', AudioCodec: 'Aac', TranscodeVideoBitrateKbps: 6000, MaxConcurrentSessions: 4, SessionTimeoutMinutes: 240,
    StreamDirectory: '', DisableHardwareAcceleration: false, SubtitleFont: '', SubtitleFontScalePercent: 100, SubtitleTextColor: '#FFE14D',
    SubtitleOutlineColor: '', SubtitleBorder: 'Default', SubtitleBold: false, DefaultSubtitleLanguage: 'eng',
    PopularChannel: channel({ Id: 'popular', Name: 'Popular', Number: 0, LogoStyle: 'Symbol', LogoSymbol: 'diversity_1', EpisodesPerBlock: 4,
        RatingBlocks: [block('', 'TV-14', false, null)], IncludeMusicVideos: false }),
    Channels: [
        channel({ Id: 'c1', Name: 'Saturday Cartoons', Number: 1, LogoStyle: 'Symbol', LogoSymbol: 'cruelty_free',
            Sources: [
                source('Library', 'lib-shows', 'Genre', ['Animation', 'Family'], ['Reality']),
                source('Library', 'lib-mv', 'Whitelist', [], [], ['mv1', 'mv2', 'hv1']),
                source('Collection', '', 'AllContent', [], [], [], 'b1')
            ],
            RatingBlocks: [block('', 'TV-Y7', true, 360, 1260), block('', 'PG', false, 1260, 360), placeholder(120, 360, 'Off Air', 'NightSky')],
            TransitionWindowMinutes: 30, MinCommunityRating: 6.5, MinCriticRating: 60, Years: [1985, 1986, 1987, 1988, 1989, 1990, 1991, 1992, 1993, 1994, 1995, 1996, 1997, 1998, 1999],
            Studios: ['Sunset Animation'], EpisodesPerBlock: 2, IncludeMovies: true, IncludeMusicVideos: false, FavorKind: 'Shows', FavorStrength: 'Moderate', AudioLanguage: 'eng' }),
        channel({ Id: 'c2', Name: 'Movie Night', Number: 2, LogoStyle: 'Symbol', LogoSymbol: 'movie',
            Sources: [source('Library', 'lib-movies', 'Genre', ['Action', 'Adventure', 'Science Fiction'], ['Horror']), source('Collection', '', 'AllContent', [], [], [], 'b2')],
            AudioLanguage: 'eng', Category: 'None', IncludeEpisodes: false, IncludeMusicVideos: false }),
        channel({ Id: 'c3', Name: 'Sitcom Classics', Number: 3,
            Sources: [source('Library', 'lib-shows', 'Genre', ['Sitcom'], [])], EpisodesPerBlock: 3, IncludeMovies: false, IncludeMusicVideos: false }),
        channel({ Id: 'c4', Name: 'Music Video Mix', Number: 4, LogoStyle: 'Symbol', LogoSymbol: 'music_note',
            Sources: [source('Library', 'lib-mv', 'AllContent')], IncludeEpisodes: false, IncludeMovies: false }),
        channel({ Id: 'c5', Name: 'Late Night Horror', Number: 5, Enabled: false,
            Sources: [source('Library', 'lib-movies', 'Genre', ['Horror'], [])], IncludeEpisodes: false, IncludeMusicVideos: false })
    ]
};

// MARK: Sessions, log and stress test

var now = Date.now();
var SESSIONS = [
    { Id: 'x1', Name: 'Movie Night', Number: 2, StartedUtc: new Date(now - 83 * 60000).toISOString(), Speed: 1.42 },
    { Id: 'x2', Name: 'Saturday Cartoons', Number: 1, StartedUtc: new Date(now - 26 * 60000).toISOString(), Speed: 0.97 },
    { Id: 'x3', Name: 'Sitcom Classics', Number: 3, StartedUtc: new Date(now - 9 * 60000).toISOString(), Speed: 0.81 },
    { Id: 'x4', Name: 'Music Video Mix', Number: 4, StartedUtc: new Date(now - 41 * 60000).toISOString(), Speed: 1.18, StopsInSeconds: 25 }
];
var SESSION_LOGOS = { x1: ['Movie Night', 2, 'movie'], x2: ['Saturday Cartoons', 1, 'cruelty_free'], x3: ['Sitcom Classics', 3, ''], x4: ['Music Video Mix', 4, 'music_note'] };
var LOG = [
    '=== 19:02:11 producer started: Midnight Harbor (1999) at 00:41:27 ===',
    'ffmpeg -hide_banner -loglevel info -ss 2487.0 -re -i "/media/movies/Midnight Harbor (1999)/Midnight Harbor (1999).mkv" -map 0:v:0 -map 0:a:0 -vf "scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:-1:-1,setsar=1,fps=30,format=yuv420p" -c:v libx264 -preset veryfast -b:v 6000k -maxrate 6000k -bufsize 12000k -g 60 -c:a aac -b:a 192k -ac 2 -ar 48000 -f mpegts pipe:1',
    'Input #0, matroska,webm, from \'/media/movies/Midnight Harbor (1999)/Midnight Harbor (1999).mkv\':',
    '  Duration: 01:52:08.38, start: 0.000000, bitrate: 9874 kb/s',
    '  Stream #0:0(eng): Video: h264 (High), yuv420p(progressive), 1920x800, 23.98 fps',
    '  Stream #0:1(eng): Audio: ac3, 48000 Hz, 5.1(side), fltp, 640 kb/s (default)',
    'Output #0, mpegts, to \'pipe:1\':',
    '  Stream #0:0(eng): Video: h264, yuv420p(progressive), 1920x1080 [SAR 1:1 DAR 16:9], q=2-31, 6000 kb/s, 30 fps',
    '  Stream #0:1(eng): Audio: aac (LC), 48000 Hz, stereo, fltp, 192 kb/s',
    'frame=  900 fps= 43 q=21.0 size=   21504KiB time=00:00:30.00 bitrate=5872.0kbits/s speed=1.43x',
    'frame= 1800 fps= 43 q=22.0 size=   43008KiB time=00:01:00.00 bitrate=5871.4kbits/s speed=1.42x',
    'frame= 2700 fps= 42 q=21.0 size=   64768KiB time=00:01:30.00 bitrate=5895.1kbits/s speed=1.42x',
    '=== 19:04:02 item ended, next: Northbound Express (1997) ==='
].join('\n');
var STRESS = { Running: false, Rounds: [
    { Streams: 1, MinFps: 118.4, Passed: true }, { Streams: 2, MinFps: 64.2, Passed: true }, { Streams: 3, MinFps: 41.7, Passed: true },
    { Streams: 4, MinFps: 31.3, Passed: true }, { Streams: 5, MinFps: 24.6, Passed: false }], Recommended: 4 };
window.__stress = { Running: false, Rounds: [] };

function json(v) { return Promise.resolve(JSON.parse(JSON.stringify(v))); }
function clone(o) { return JSON.parse(JSON.stringify(o)); }
function dataBlob(url) { return fetch(url).then(function (r) { return r.blob(); }); }

function route(o) {
    var url = o.url, type = o.type || 'GET';
    window.__posts.push(type + ' ' + url);
    var m = /livechannels\/sessions\/([^/]+)\/logo/.exec(url);
    if (m) { var s = SESSION_LOGOS[m[1]]; return dataBlob(generatedLogo(s[1], s[0], s[2])).then(function (b) { return new Response(b); }); }
    if (/livechannels\/sessions\/[^/]+\/log/.test(url)) return Promise.resolve(new Response(LOG));
    if (/livechannels\/sessions/.test(url) && type === 'GET') return json(SESSIONS);
    if (/livechannels\/encoders/.test(url)) return json({ acceleration: 'Intel QuickSync (QSV)' });
    if (/livechannels\/stresstest/.test(url)) return json(window.__stress);
    if (/Library\/VirtualFolders/.test(url)) return json(LIBRARIES);
    if (/ParentalRatings/.test(url)) return json(RATINGS);
    if (/Cultures/.test(url)) return json(CULTURES);
    var g = /\/Genres\?/.test(url) && new URL(url, location.origin).searchParams.get('ParentId');
    if (g) return json({ Items: (GENRES[g] || []).map(function (n) { return { Name: n }; }) });
    if (/\/Studios\?/.test(url)) { var t = (new URL(url, location.origin).searchParams.get('searchTerm') || '').toLowerCase(); return json({ Items: STUDIOS.filter(function (s) { return s.toLowerCase().indexOf(t) !== -1; }).map(function (s) { return { Name: s }; }) }); }
    if (/\/Persons\?/.test(url)) return json({ Items: PEOPLE });
    if (/^\/Items\?/.test(url)) {
        var sp = new URL(url, location.origin).searchParams, kinds = (sp.get('IncludeItemTypes') || '').split(','), term2 = (sp.get('searchTerm') || '').toLowerCase();
        return json({ Items: ITEMS.filter(function (i) { return kinds.indexOf(i.Type) !== -1 && i.Name.toLowerCase().indexOf(term2) !== -1; }) });
    }
    return json({});
}

window.ApiClient = {
    serverAddress: function () { return 'http://jellyfin.example.com:8096'; },
    serverId: function () { return 's1'; },
    accessToken: function () { return 't'; },
    getCurrentUserId: function () { return 'u1'; },
    getUrl: function (p, q) { return '/' + p + (q ? '?' + new URLSearchParams(q) : ''); },
    getJSON: function (u) { return route({ url: u }); },
    ajax: function (o) { return route(o); },
    fetch: function (o) { return route(o); },
    getPluginConfiguration: function () { return json(window.__config); },
    updatePluginConfiguration: function (id, c) { window.__config = clone(c); return Promise.resolve({}); },
    getScheduledTasks: function () { return Promise.resolve([]); },
    startScheduledTask: function () { return Promise.resolve(); },
    getUsers: function () { return Promise.resolve([]); },
    getUserImageUrl: function () { return ''; },
    sendMessage: function () {}, sendWebSocketMessage: function () {},
    getImageUrl: function (id) { return poster(String(id)); },
    getScaledImageUrl: function (id) { return poster(String(id)); },
    getItems: function (u, q) {
        if (q.Ids) return json({ Items: q.Ids.split(',').map(function (id) { return BYID[id]; }).filter(Boolean) });
        if (q.IncludeItemTypes === 'BoxSet') return json({ Items: COLLECTIONS });
        var types = (q.IncludeItemTypes || '').split(',');
        var term = (q.searchTerm || '').toLowerCase();
        return json({ Items: ITEMS.filter(function (i) {
            return (!q.ParentId || i.lib === q.ParentId) && types.indexOf(i.Type) !== -1
                && ((i.Name + ' ' + (i.SeriesName || '')).toLowerCase().indexOf(term) !== -1);
        }) });
    }
};
window.Dashboard = { alert: function (m) { window.__errors.push('alert: ' + m); }, showLoadingMsg: function () {}, hideLoadingMsg: function () {} };
window.LibraryMenu = { setTabs: function () {} };
window.__STRESS_DONE = STRESS;
