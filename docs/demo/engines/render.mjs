// Renders docs/demo/engines/index.html to a square MP4 (or stills) with headless Chrome and ffmpeg.
// No npm packages: Chrome is driven over the DevTools protocol with Node's built-in WebSocket (Node 22+).
//
//   node docs/demo/engines/render.mjs --lang en --out artifacts/demo/m2d-plus-engines-en.mp4
//   node docs/demo/engines/render.mjs --lang ja --cut short            (the short cut)
//   node docs/demo/engines/render.mjs --lang en --only pipes           (one engine scene; hld | pipes | raster)
//   node docs/demo/engines/render.mjs --lang ja --stills 3,12,20 --out artifacts/demo/stills
//
// Options: --fps 30  --scale 1 (2 = 2160 px)  --port 9377  --chrome <path>  --crf 16
import { spawn } from 'node:child_process';
import { mkdirSync, mkdtempSync, writeFileSync, existsSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const args = Object.fromEntries(process.argv.slice(2).join(' ').split(/\s*--/).filter(Boolean).map(s => { const [k, ...v] = s.split(' '); return [k, v.join(' ') || true]; }));
const lang = args.lang || 'en', fps = +args.fps || 30, scale = +args.scale || 1, port = +args.port || 9377, crf = +args.crf || 16;
const here = dirname(fileURLToPath(import.meta.url));
const cut = args.cut === 'short' ? 'short' : '', only = ['hld', 'pipes', 'raster'].includes(args.only) ? args.only : '';
const name = (only ? `m2d-plus-engine-${only}` : 'm2d-plus-engines') + (cut ? '-short' : '') + `-${lang}`;
const out = resolve(args.out || `artifacts/demo/${name}.mp4`);
const chrome = args.chrome || ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', '/usr/bin/google-chrome'].find(existsSync);
if (!chrome) throw new Error('Chrome not found; pass --chrome <path>');

const profile = mkdtempSync(join(tmpdir(), 'm2d-demo-'));
const browser = spawn(chrome, ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, '--no-first-run', '--hide-scrollbars',
  '--allow-file-access-from-files', '--window-size=1200,1300', 'about:blank'], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));

let ws, id = 0; const pending = new Map();
const send = (method, params = {}) => new Promise((res, rej) => { const i = ++id; pending.set(i, { res, rej }); ws.send(JSON.stringify({ id: i, method, params })); });
const evaluate = async expression => {
  const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);
  return r.result.value;
};
try {
  let target;
  for (let i = 0; i < 60 && !target; i++) {
    await sleep(250);
    try { target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(t => t.type === 'page'); } catch { /* not up yet */ }
  }
  if (!target) throw new Error('Chrome did not start');
  ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
  ws.onmessage = e => { const m = JSON.parse(e.data); const p = pending.get(m.id); if (!p) return; pending.delete(m.id); m.error ? p.rej(new Error(m.error.message)) : p.res(m.result); };
  await send('Page.enable');
  const url = pathToFileURL(join(here, 'index.html')).href + `?capture&lang=${lang}&scale=${scale}` + (cut ? '&cut=short' : '') + (only ? `&only=${only}` : '');
  await send('Page.navigate', { url });
  for (let i = 0; i < 240; i++) { await sleep(250); if (await evaluate('window.__ready === true').catch(() => false)) break; if (i === 239) throw new Error('page did not become ready'); }
  const duration = await evaluate('window.__duration');
  const frame = async t => Buffer.from((await evaluate(`window.__frame(${t})`)).split(',')[1], 'base64');

  if (args.stills) {
    mkdirSync(out, { recursive: true });
    for (const t of String(args.stills).split(',').map(Number)) { const f = join(out, `${lang}-${t.toFixed(1).padStart(5, '0')}.png`); writeFileSync(f, await frame(t)); console.log(f); }
  } else {
    mkdirSync(dirname(out), { recursive: true });
    const ff = spawn('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'image2pipe', '-framerate', String(fps), '-c:v', 'png', '-i', '-',
      '-c:v', 'libx264', '-preset', 'slow', '-crf', String(crf), '-pix_fmt', 'yuv420p', '-movflags', '+faststart', out], { stdio: ['pipe', 'inherit', 'inherit'] });
    const done = new Promise((res, rej) => ff.on('exit', c => c === 0 ? res() : rej(new Error('ffmpeg exited ' + c))));
    const n = Math.round(duration * fps);
    for (let i = 0; i < n; i++) {
      const png = await frame(i / fps);
      if (!ff.stdin.write(png)) await new Promise(r => ff.stdin.once('drain', r));
      if (i % 150 === 0) console.log(`${name}: frame ${i}/${n}`);
    }
    ff.stdin.end(); await done;
    console.log(out);
  }
} finally {
  try { ws?.close(); } catch { /* ignore */ }
  browser.kill();
  await sleep(400);
  try { rmSync(profile, { recursive: true, force: true }); } catch { /* profile may still be locked; it is in the temp folder */ }
}
