"""Live Channels screenshot server: serves the plugin pages, Base and jellyfin-web the way Jellyfin does.

Usage: python3 -I server.py 8831
BASEVER picks the Base package (default 12.4.3), JELLYFIN_WEB the jellyfin-web folder.
"""
import http.server, json, os, re, sys, urllib.parse
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.join(HERE, '..', '..', 'Jellyfin.Plugin.LiveChannels')
JF = os.environ.get('JELLYFIN_WEB', '/Applications/Jellyfin.app/Contents/Resources/jellyfin-web')
PACKAGE = os.path.expanduser('~/.nuget/packages/jpkribs.jellyfin.base/%s' % os.environ.get('BASEVER', '12.4.3'))
BASE = os.path.join(PACKAGE, 'assets')
TYPES = {'.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.html': 'text/html', '.svg': 'image/svg+xml',
         '.woff2': 'font/woff2', '.woff': 'font/woff', '.ttf': 'font/ttf', '.png': 'image/png'}


def stylesheets():
    """jellyfin-web's stylesheets in load order: index.html's two first, the dark theme, then every chunk. A chunk that
    sets the root font to anything but the dashboard's 93% belongs to another layout, such as TV, and is left out."""
    first = re.findall(r'href="([^"?]+\.css)', open(os.path.join(JF, 'index.html')).read())
    rest = []
    for name in sorted(os.listdir(JF)):
        if not name.endswith('.css') or name in first:
            continue
        sizes = re.findall(r'html\{[^}]*?font-size:([^;}!]+)', open(os.path.join(JF, name), encoding='utf-8', errors='ignore').read())
        if any(size.strip() != '93%' for size in sizes):
            continue
        rest.append(name)
    return first + ['themes/dark/theme.css'] + rest


def sentence(text):
    """Jellyfin's English in sentence case, as Case="sentence" writes it."""
    return text[:1] + text[1:].lower()


def translations():
    """What the plugin's translations route serves in US English, as BaseTranslations.Serve answers: the strings, with
    Base's own and the Jellyfin strings Base and the project declare, and the local ones, and the casing list."""
    strings = json.load(open(os.path.join(PACKAGE, 'strings', 'en-us.json'), encoding='utf-8'))
    project = open(os.path.join(REPO, 'Jellyfin.Plugin.LiveChannels.csproj')).read()
    jellyfin = {}
    for source in ('web', 'server'):
        jellyfin[source] = json.load(open(os.path.join(PACKAGE, 'jellyfin', source, 'en-us.json'), encoding='utf-8'))
    for entry in json.load(open(os.path.join(PACKAGE, 'strings', 'jellyfin.json'), encoding='utf-8'))['strings']:
        text = jellyfin['web'].get(entry['key'], entry['key'])
        strings[entry['key']] = sentence(text) if entry.get('case') == 'sentence' else text
    for key, attrs in re.findall(r'<JellyfinString Include="([^"]+)"([^>]*)/>', project):
        source = 'server' if 'Source="server"' in attrs else 'web'
        text = jellyfin[source].get(key, key)
        strings[key] = sentence(text) if 'Case="sentence"' in attrs else text
    strings.update(json.load(open(os.path.join(REPO, 'Localization', 'en-us.json'), encoding='utf-8')))
    casing = ['Jellyfin', 'TV', 'ID', 'API', 'URL', 'HTTP', 'HTTPS', 'IP', 'DNS', 'HDR', 'SDR', '4K']
    plugin_casing = os.path.join(REPO, 'Localization', 'casing.json')
    if os.path.isfile(plugin_casing):
        casing += json.load(open(plugin_casing, encoding='utf-8'))
    return {'language': 'en-us', 'strings': strings, 'casing': casing}


class H(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def do_GET(self):
        u = urllib.parse.urlparse(self.path)
        q = urllib.parse.parse_qs(u.query)
        if u.path in ('/stylesheets.json', '/livechannels/Translations'):
            data = json.dumps(stylesheets() if u.path == '/stylesheets.json' else translations()).encode()
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(data)
            return
        if u.path.endswith('/configurationpage'):
            n = q['name'][0]
            if n == 'livechannels_jpkribs_shared.js':
                path = os.path.join(BASE, 'jpkribs_shared.js')
            elif n == 'livechannels_jpkribs_shared.css':
                path = os.path.join(BASE, 'jpkribs_shared.css')
            elif n == 'livechannels_symbols.ttf':
                path = os.path.join(REPO, 'Assets', 'MaterialSymbolsOutlined.ttf')
            elif os.path.splitext(n)[1] in ('.js', '.css'):
                path = os.path.join(REPO, 'Web', n)
            else:
                path = os.path.join(REPO, 'Web', n + '.html')
        elif u.path.startswith('/jf/'):
            path = os.path.join(JF, u.path[4:])
        else:
            path = os.path.join(HERE, u.path.lstrip('/'))
        if not os.path.isfile(path):
            self.send_response(404)
            self.end_headers()
            return
        data = open(path, 'rb').read()
        self.send_response(200)
        self.send_header('Content-Type', TYPES.get(os.path.splitext(path)[1], 'application/octet-stream'))
        self.send_header('Cache-Control', 'no-store')
        self.end_headers()
        self.wfile.write(data)


http.server.ThreadingHTTPServer(('127.0.0.1', int(sys.argv[1])), H).serve_forever()
