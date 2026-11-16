// Captures every Live Channels documentation screenshot from the harness at 2x, dark theme, into docs/screenshots.
// Run python3 -I server.py 8831 in the background, then node shoot.mjs [file name ...] to take all or only the named ones.
// PLAYWRIGHT_CORE points at playwright-core's index.mjs and CHROMIUM at the Chromium binary.
import { fileURLToPath } from 'node:url';
const { chromium } = await import(process.env.PLAYWRIGHT_CORE || 'playwright-core');
const b = await chromium.launch({ executablePath: process.env.CHROMIUM || `${process.env.HOME}/Library/Caches/ms-playwright/chromium-1148/chrome-mac/Chromium.app/Contents/MacOS/Chromium` });
const OUT = fileURLToPath(new URL('../../docs/screenshots/', import.meta.url));
const ROOT = 'http://127.0.0.1:8831/';
const PAD = 12;
const section = (id) => ({ open: [id], top: `[data-target="${id}"]`, bottom: `#${id}` });
const shots = [
  { file: 'Channels', page: 'livechannels_channels', chrome: true, w: 960, h: 944 },
  { file: 'Channel Identity', page: 'livechannels_channels', idx: 1, ...section('sectionChannel') },
  { file: 'Channel Content', page: 'livechannels_channels', h: 2400, idx: 0, focus: '#librarySources .base-repeater-card input', ...section('sectionContent') },
  { file: 'Channel Filters', page: 'livechannels_channels', idx: 0, ...section('sectionFilters') },
  { file: 'Channel Settings', page: 'livechannels_channels', idx: 0, ...section('sectionSettings') },
  { file: 'Popular', page: 'livechannels_popular', open: ['popularSectionChannel'], top: 'form', bottom: '#popularSaveBar' },
  { file: 'Sessions', page: 'livechannels_sessions', top: 'form', bottom: '#sessionList' },
  { file: 'Session Log', page: 'livechannels_sessions', w: 960, h: 720, click: '#sessionList .base-media-card' },
  { file: 'Settings', page: 'livechannels_settings', chrome: true, w: 960, h: 967 },
  { file: 'Encoding', page: 'livechannels_settings', ...section('sectionEncoding') },
  { file: 'Hardware Acceleration', page: 'livechannels_settings', ...section('sectionHardware') },
  { file: 'Playback', page: 'livechannels_settings', ...section('sectionPlayback') },
  { file: 'Subtitle Appearance', page: 'livechannels_settings', ...section('sectionSubtitles') },
  { file: 'Session Limits', page: 'livechannels_settings', ...section('sectionSessions') },
  { file: 'Stress Test', page: 'livechannels_settings', stress: true, pick: 'Midnight', ...section('sectionStress') },
  { file: 'Live TV Channels', url: 'livetv.html', w: 960, h: 360 }
];
const only = process.argv.slice(2);
for (const s of shots) {
  if (only.length && !only.includes(s.file)) continue;
  const p = await b.newPage({ viewport: { width: s.w || 720, height: s.h || 900 }, deviceScaleFactor: 2, colorScheme: 'dark' });
  await p.goto(ROOT + (s.url || `harness.html?page=${s.page}${s.chrome ? '&chrome=1' : ''}${s.stress ? '&stress=1' : ''}`));
  await p.waitForFunction(() => window.__ready); await p.waitForTimeout(900);
  if (s.idx !== undefined) await p.evaluate((i) => { const e = document.querySelector('#selectChannel'); e.value = String(i); e.dispatchEvent(new Event('change', { bubbles: true })); }, s.idx);
  for (const id of s.open || []) await p.click(`[data-target="${id}"]`);
  if (s.pick) {
    await p.click('#stressItemHost input');
    await p.keyboard.type(s.pick, { delay: 40 });
    await p.waitForTimeout(700);
    await p.keyboard.press('ArrowDown');
    await p.keyboard.press('Enter');
  }
  if (s.click) await p.click(s.click);
  await p.evaluate(() => document.activeElement && document.activeElement.blur && document.activeElement.blur());
  await p.mouse.move(0, 0);
  if (s.focus) await p.click(s.focus);
  await p.waitForTimeout(900);
  const errs = await p.evaluate(() => window.__errors.filter((e) => !/ResizeObserver/.test(e)));
  if (!s.top) {
    await p.screenshot({ path: `${OUT}${s.file}.png` });
    console.log(s.file, (s.w * 2) + 'x' + (s.h * 2), JSON.stringify(errs));
    await p.close();
    continue;
  }
  const r = await p.evaluate((s) => {
    const t = document.querySelector(s.top), bt = document.querySelector(s.bottom);
    if (!t || !bt) return null;
    return { top: t.getBoundingClientRect().top + scrollY, bottom: bt.getBoundingClientRect().bottom + scrollY };
  }, s);
  if (!r) { console.log('missing', s.file); await p.close(); continue; }
  const top = Math.max(0, Math.round(r.top - PAD));
  const clip = { x: 3, y: top, width: 714, height: Math.round(r.bottom + PAD) - top };
  await p.screenshot({ path: `${OUT}${s.file}.png`, clip, fullPage: true });
  console.log(s.file, clip.width * 2 + 'x' + clip.height * 2, JSON.stringify(errs));
  await p.close();
}
await b.close();
