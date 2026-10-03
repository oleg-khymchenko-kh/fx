import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const API = 'https://client-api-global.fxpro.technology/api/v1/trading-signals';
const HEADERS = {
  accept: 'application/json',
  origin: 'https://direct.fxpro.group',
  referer: 'https://direct.fxpro.group/',
  'user-agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) '
    + 'Chrome/140.0.0.0 Safari/537.36',
};
const here = path.dirname(fileURLToPath(import.meta.url));
const defaultOut = path.resolve(here, '../../FXViewer/bin/Debug/net10.0-windows/data/trading-central/api-history');

const CURRENCIES = new Set(('USD EUR GBP JPY CHF CAD AUD NZD NOK SEK DKK PLN HUF CZK TRY ZAR MXN SGD HKD '
  + 'CNH CNY INR ILS RUB RON THB TWD PHP IDR KRW MYR BRL CLP').split(' '));

const IMPORT_PAIRS = ['EURUSD', 'GBPUSD', 'USDCHF', 'USDJPY', 'USDCAD', 'AUDUSD', 'NZDUSD', 'EURGBP'];

const args = parseArgs(process.argv.slice(2));
const outDir = path.resolve(args.out ?? defaultOut);
const batchSize = Number(args.batch ?? 100);
const concurrency = Number(args.concurrency ?? 4);
const pauseMs = Number(args.pause ?? 300);
const lang = args.lang ?? 'en';
const statePath = path.join(outDir, 'state.json');
const forexPath = path.join(outDir, 'forex.jsonl');
const indexPath = path.join(outDir, 'index.jsonl');

function parseArgs(list) {
  const result = {};
  for (let i = 0; i < list.length; i++) {
    const key = list[i];
    if (!key.startsWith('--')) continue;
    const next = list[i + 1];
    if (next === undefined || next.startsWith('--')) result[key.slice(2)] = true;
    else { result[key.slice(2)] = next; i++; }
  }
  return result;
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

function readState() {
  try { return JSON.parse(fs.readFileSync(statePath, 'utf8')); } catch { return null; }
}

function writeState(state) {
  const temp = statePath + '.tmp';
  fs.writeFileSync(temp, JSON.stringify(state, null, 2));
  fs.renameSync(temp, statePath);
}

function isForex(signal) {
  if (typeof signal.category === 'string') return signal.category.toLowerCase() === 'forex';
  const symbol = String(signal.symbol ?? '');
  return /^[A-Z]{6}$/.test(symbol) && CURRENCIES.has(symbol.slice(0, 3)) && CURRENCIES.has(symbol.slice(3));
}

function indexEntry(id, signal) {
  return {
    id,
    symbol: signal.symbol ?? '',
    category: signal.category ?? '',
    term: signal.term ?? '',
    language: signal.language ?? '',
    time: signal.time ?? 0,
    utc: signal.time ? new Date(signal.time * 1000).toISOString() : '',
    title: signal.title ?? '',
  };
}

async function fetchOne(id) {
  try {
    const response = await fetch(`${API}/${id}?lang=${lang}`, { headers: HEADERS, signal: AbortSignal.timeout(30000) });
    if (response.status === 200) return { id, status: 200, data: await response.json() };
    await response.arrayBuffer().catch(() => {});
    return { id, status: response.status };
  } catch (error) {
    return { id, status: 0, error: String(error) };
  }
}

async function fetchBatch(ids) {
  const results = new Array(ids.length);
  let next = 0;
  const worker = async () => {
    while (next < ids.length) {
      const at = next++;
      results[at] = await fetchOne(ids[at]);
    }
  };
  await Promise.all(Array.from({ length: concurrency }, worker));
  return results;
}

async function newestId() {
  let best = 0;
  for (const category of ['popular', 'forex', 'stocks', 'crypto', 'commodities', 'indices']) {
    try {
      const response = await fetch(`${API}?lang=${lang}&category=${category}&count=1&includeMedia=false`,
        { headers: HEADERS, signal: AbortSignal.timeout(30000) });
      if (response.status !== 200) continue;
      const list = await response.json();
      for (const signal of list.signals ?? []) best = Math.max(best, signal.articleId);
    } catch {
    }
  }
  return best;
}

async function scrapeBatch(ids) {
  let pending = ids;
  const done = new Map();
  let lastStatus = 0;
  for (let attempt = 0; attempt < 6 && pending.length > 0; attempt++) {
    if (attempt > 0) {
      const wait = 2000 * 2 ** attempt;
      console.log(`  retry ${pending.length} id(s) in ${wait / 1000}s (last status ${lastStatus})`);
      await sleep(wait);
    }
    for (const result of await fetchBatch(pending)) {
      if (result.status === 200 || result.status === 404) done.set(result.id, result);
      else lastStatus = result.status || result.error;
    }
    pending = pending.filter(id => !done.has(id));
  }
  return { done, failed: pending, lastStatus };
}

function knownIds() {
  const ids = new Set();
  if (!fs.existsSync(indexPath)) return ids;
  for (const line of fs.readFileSync(indexPath, 'utf8').split('\n')) {
    if (!line.trim()) continue;
    try { ids.add(JSON.parse(line).id); } catch {
    }
  }
  return ids;
}

function signalInput(signal) {
  const levels = signal.levels ?? {};
  const supports = [levels.resistance3, levels.resistance2, levels.resistance1,
    levels.pivot && `${levels.pivot} Pivot`, levels.lastPrice && `${levels.lastPrice} Last`,
    levels.support1, levels.support2, levels.support3].filter(Boolean);
  const text = [`Trading signal: ${signal.symbol}`, signal.term, signal.title, signal.summary,
    'Pivot', levels.pivot, 'Our preference', signal.ourPreference,
    'Alternative scenario', signal.alternativeScenario, 'Comment', signal.comment,
    'Supports and resistances', ...supports].filter(x => x != null && x !== '').join('\n');
  const media = (signal.media ?? []).map(m => m.path);
  const image = media.find(p => /_359x242\.gif$/.test(p)) ?? media[0] ?? '';
  const readAt = new Date(signal.time * 1000).toISOString();
  return { pair: signal.symbol, id: String(signal.articleId), text, image, readAt,
    stamp: readAt.slice(0, 16).replace(/[-:T]/g, '') };
}

function importSignals(signals) {
  const inputs = signals
    .filter(s => IMPORT_PAIRS.includes(s.symbol) && s.term === 'Intraday')
    .sort((a, b) => a.time - b.time || a.articleId - b.articleId)
    .map(signalInput);
  if (inputs.length === 0) {
    console.log('Import: no new Intraday signals of the chart pairs.');
    return 0;
  }
  const stamp = new Date().toISOString().slice(0, 16).replace(/[-:T]/g, '');
  const file = path.join(outDir, '..', 'mail', `incoming-signals-scrape-${stamp}.json`);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify(inputs, null, 1));
  console.log(`Import: ${inputs.length} signal(s) -> ${file}`);
  const run = spawnSync('dotnet', ['run', '--project', path.resolve(here, '../Fx.TradingCentral'), '-c', 'Release',
    '--', 'signal', '--file', file], { stdio: 'inherit' });
  return run.status ?? 1;
}

async function main() {
  fs.mkdirSync(outDir, { recursive: true });
  const state = readState() ?? {};
  const known = knownIds();
  const recheck = Number(args.recheck ?? 300);
  const head = Number(state.next ?? 15270000);
  const from = Number(args.from ?? Math.max(15270000, head - (state.next ? recheck : 0)));
  const newest = args.to ? 0 : await newestId();
  const to = Number(args.to ?? newest + 1);
  if (!args.to && newest === 0) {
    console.log('Cannot read the newest id from the API. Pass --to <id>.');
    process.exitCode = 1;
    return;
  }
  if (!(to > from)) {
    console.log(`Nothing to do: from ${from}, to ${to}`);
    return;
  }
  console.log(`Scanning ids ${from}..${to - 1}, ${known.size} id(s) already read are skipped, `
    + `batch ${batchSize}, concurrency ${concurrency}`);
  console.log(`Output: ${outDir}`);

  const started = Date.now();
  let found = state.found ?? 0;
  let forex = state.forex ?? 0;
  let newFound = 0;
  const newForex = [];
  for (let start = from; start < to; start += batchSize) {
    const ids = [];
    for (let id = start; id < Math.min(start + batchSize, to); id++) if (!known.has(id)) ids.push(id);
    if (ids.length === 0) continue;
    const { done, failed, lastStatus } = await scrapeBatch(ids);
    if (failed.length > 0) {
      writeState({ next: Math.max(head, start), to, found, forex, stoppedAt: new Date().toISOString() });
      console.log(`Stopped: ${failed.length} id(s) keep failing from ${failed[0]} (last status ${lastStatus}). `
        + 'Run again later to resume.');
      process.exitCode = 2;
      return;
    }
    const indexLines = [];
    const forexLines = [];
    for (const id of ids) {
      const result = done.get(id);
      if (result.status !== 200) continue;
      found++;
      newFound++;
      known.add(id);
      indexLines.push(JSON.stringify(indexEntry(id, result.data)));
      if (isForex(result.data)) {
        forex++;
        forexLines.push(JSON.stringify(result.data));
        newForex.push(result.data);
      }
    }
    if (indexLines.length) fs.appendFileSync(indexPath, indexLines.join('\n') + '\n');
    if (forexLines.length) fs.appendFileSync(forexPath, forexLines.join('\n') + '\n');
    const next = Math.max(head, Math.min(start + batchSize, to));
    writeState({ next, to, found, forex, updatedAt: new Date().toISOString() });

    const perId = (Date.now() - started) / (Math.min(start + batchSize, to) - from);
    const left = Math.round(((to - start - batchSize) * perId) / 60000);
    console.log(`${Math.min(start + batchSize, to)} / ${to}  new ${newFound}  new forex ${newForex.length}`
      + `  ~${Math.max(0, left)} min left`);
    if (pauseMs > 0) await sleep(pauseMs);
  }
  writeState({ next: to, to, found, forex, updatedAt: new Date().toISOString() });
  console.log(`Done. New signals ${newFound}, new forex ${newForex.length}. Total ${found}, forex ${forex}.`);
  if (args.import) {
    const status = importSignals(newForex);
    if (status !== 0) process.exitCode = status;
  }
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
