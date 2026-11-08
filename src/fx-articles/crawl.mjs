import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { ProxyAgent } from 'undici';

const API = 'https://client-api-global.fxpro.technology/api/v1/trading-signals';
const GAP = 10;
const DELAY_MS = 1000;
const here = path.dirname(fileURLToPath(import.meta.url));
const OUT = path.join(here, 'articles');
const STATE = path.join(here, 'last-id.txt');
const CONFIG = path.join(here, 'config.json');

function loadDispatcher() {
  if (!fs.existsSync(CONFIG)) return undefined;
  const proxy = JSON.parse(fs.readFileSync(CONFIG, 'utf8')).proxy;
  if (!proxy || !proxy.host) return undefined;
  const options = { uri: `http://${proxy.host}:${proxy.port}` };
  if (proxy.username) {
    options.token = 'Basic ' + Buffer.from(`${proxy.username}:${proxy.password || ''}`).toString('base64');
  }
  console.log(`proxy ${proxy.host}:${proxy.port}`);
  return new ProxyAgent(options);
}

const dispatcher = loadDispatcher();

function readLastId() {
  if (process.argv[2]) return Number(process.argv[2]);
  if (fs.existsSync(STATE)) return Number(fs.readFileSync(STATE, 'utf8').trim());
  console.error('No last-id.txt yet. Pass the start id: node crawl.mjs 15373057');
  process.exit(2);
}

function saveLastId(id) {
  const temp = STATE + '.tmp';
  fs.writeFileSync(temp, String(id));
  fs.renameSync(temp, STATE);
}

let firstRequest = true;

async function fetchArticle(id) {
  if (!firstRequest) await new Promise(resolve => setTimeout(resolve, DELAY_MS));
  firstRequest = false;
  const url = `${API}/${id}?lang=en`;
  console.log(`GET ${url}`);
  const started = Date.now();
  try {
    const response = await fetch(url, { signal: AbortSignal.timeout(20000), dispatcher });
    const text = await response.text();
    const elapsed = Date.now() - started;
    if (!response.ok) {
      console.error(`  ERROR HTTP ${response.status}, ${Buffer.byteLength(text)} bytes, ${elapsed} ms`);
      return null;
    }
    JSON.parse(text);
    console.log(`  OK HTTP ${response.status}, ${Buffer.byteLength(text)} bytes, ${elapsed} ms`);
    return text;
  } catch (error) {
    const reason = error.cause ? `${error.message} (${error.cause.code || error.cause.message})` : error.message;
    console.error(`  ERROR ${reason}, ${Date.now() - started} ms`);
    return null;
  }
}

fs.mkdirSync(OUT, { recursive: true });
let lastGood = readLastId();
let saved = 0;
let id = lastGood;

while (true) {
  let found = null;
  let foundId = 0;
  for (let step = 1; step <= GAP + 1 && found === null; step++) {
    foundId = id + step;
    found = await fetchArticle(foundId);
  }
  if (found === null) break;
  fs.writeFileSync(path.join(OUT, `${foundId}.json`), found);
  saveLastId(foundId);
  lastGood = foundId;
  id = foundId;
  saved++;
  console.log(`saved ${foundId}`);
}

saveLastId(lastGood);
console.log(`DONE saved ${saved}, last id ${lastGood}`);
