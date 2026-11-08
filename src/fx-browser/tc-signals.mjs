import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const PORT = Number(process.env.FX_BROWSER_PORT || 9333);
const BASE = `http://127.0.0.1:${PORT}`;
const PAGE = 'https://direct.fxpro.group/en/discover';
const here = path.dirname(fileURLToPath(import.meta.url));
const INBOX = path.resolve(here, '../../FXViewer/bin/Debug/net10.0-windows/data/trading-central/direct-inbox');

const EXIT_NOT_RUNNING = 4;
const EXIT_CHALLENGE = 5;
const EXIT_ERROR = 1;

const READ_SIGNALS = `(async () => { const pairs = ['EURUSD','GBPUSD','USDCHF','USDJPY','USDCAD','AUDUSD','NZDUSD','EURGBP']; const api = 'https://client-api-global.fxpro.technology/api/v1/trading-signals'; const list = await (await fetch(api + '?lang=en&category=forex&count=100&includeMedia=false', {credentials: 'omit'})).json(); const picked = list.signals.filter(s => pairs.includes(s.symbol)); const out = []; for (const s of picked) { const d = await (await fetch(api + '/' + s.articleId + '?lang=en', {credentials: 'omit'})).json(); const L = d.levels || {}; const sr = [L.resistance3, L.resistance2, L.resistance1, L.pivot && L.pivot + ' Pivot', L.lastPrice && L.lastPrice + ' Last', L.support1, L.support2, L.support3].filter(Boolean); const text = ['Trading signal: ' + d.symbol, d.term, d.title, d.summary, 'Pivot', L.pivot, 'Our preference', d.ourPreference, 'Alternative scenario', d.alternativeScenario, 'Comment', d.comment, 'Supports and resistances', ...sr].filter(x => x != null && x !== '').join('\\n'); const image = (d.media || []).map(m => m.path).find(p => /_359x242\\.gif$/.test(p)) || ((d.media || []).find(m => m.theme === 'light') || {}).path || ''; const readAt = new Date().toISOString(); out.push({pair: d.symbol, id: String(d.articleId), text, image, readAt, stamp: readAt.slice(0, 16).replace(/[-:T]/g, ''), published: new Date(d.time * 1000).toISOString()}); } return {count: out.length, out}; })()`;

async function call(method, route, body, timeoutMs = 120000) {
  const response = await fetch(BASE + route, {
    method,
    body,
    headers: body ? { 'content-type': 'application/json' } : undefined,
    signal: AbortSignal.timeout(timeoutMs),
  });
  const data = await response.json();
  if (!response.ok) throw new Error(data.error || `HTTP ${response.status}`);
  return data;
}

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

try {
  await call('GET', '/status', undefined, 3000);
} catch {
  console.log(`NOT RUNNING: fx-browser does not answer on ${BASE}. Start it: cd src/fx-browser && node server.mjs`);
  process.exit(EXIT_NOT_RUNNING);
}

try {
  let info = await call('POST', '/open', JSON.stringify({ url: PAGE, waitMs: 3000 }));
  for (let i = 0; i < 10 && /just a moment/i.test(info.title); i++) {
    await sleep(2000);
    info = await call('GET', '/status');
  }
  console.log(`PAGE ${info.url} "${info.title}"`);
  if (/just a moment/i.test(info.title)) {
    console.log('CHALLENGE: Cloudflare check is shown in the fx-browser window. Pass it there by hand.');
    process.exit(EXIT_CHALLENGE);
  }

  let read;
  try {
    read = await call('POST', '/eval', READ_SIGNALS);
  } catch (error) {
    console.log(`RETRY after error: ${error.message}`);
    await sleep(5000);
    read = await call('POST', '/eval', READ_SIGNALS);
  }

  const signals = read.result && read.result.out || [];
  console.log(`SIGNALS ${signals.length}`);
  for (const s of signals) console.log(`  ${s.pair} ${s.published} ${s.id}`);
  if (!signals.length) process.exit(0);

  fs.mkdirSync(INBOX, { recursive: true });
  const file = path.join(INBOX, `signals-${signals[0].stamp}.json`);
  fs.writeFileSync(file + '.tmp', JSON.stringify(signals, null, 1), 'utf8');
  fs.renameSync(file + '.tmp', file);
  console.log(`WROTE ${file}`);
} catch (error) {
  console.log(`ERROR: ${error.message}`);
  process.exit(EXIT_ERROR);
}
