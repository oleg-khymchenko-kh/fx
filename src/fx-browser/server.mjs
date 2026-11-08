import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';
import { chromium } from 'playwright-core';
import { init } from 'fxpro';

const PORT = Number(process.env.FX_BROWSER_PORT || 9333);
const PROFILE = process.env.FX_BROWSER_PROFILE
  || path.join(process.env.LOCALAPPDATA || os.tmpdir(), 'fx-browser', 'profile');
const SHOTS = path.join(path.dirname(PROFILE), 'screenshots');
console.log(PROFILE);

try {
  const running = await fetch(`http://127.0.0.1:${PORT}/status`, { signal: AbortSignal.timeout(2000) });
  if (running.ok) {
    console.log(`fx-browser is already running on http://127.0.0.1:${PORT}`);
    console.log(`stop it first: curl -X POST http://127.0.0.1:${PORT}/close`);
    process.exit(0);
  }
} catch {
}

fs.mkdirSync(PROFILE, { recursive: true });
fs.mkdirSync(SHOTS, { recursive: true });

let context;
try {
  context = await chromium.launchPersistentContext(PROFILE, {
    channel: 'chrome',
    headless: false,
    viewport: null,
  });
  globalThis.ctx = context;
} catch (error) {
  if (/exitCode=21\b/.test(String(error))) {
    console.error(`Chrome profile is in use by another Chrome window: ${PROFILE}`);
    console.error('Close that window (or stop the other fx-browser) and start again.');
    process.exit(1);
  }
  throw error;
}

await init();

let mainPage = context.pages()[0] || null;

function page() {
  return mainPage && !mainPage.isClosed() ? mainPage : null;
}

async function currentPage() {
  if (!page()) mainPage = await context.newPage();
  return mainPage;
}

async function pageInfo(p) {
  return { url: p.url(), title: await p.title().catch(() => '') };
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on('data', c => chunks.push(c));
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

function send(res, status, data) {
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8' });
  res.end(JSON.stringify(data));
}

const routes = {
  'GET /status': async () => ({
    ok: true,
    ...(page() ? await pageInfo(page()) : { url: null, title: null }),
    tabs: context.pages().map(p => p.url()),
  }),

  'POST /open': async body => {
    const { url, waitMs = 3000 } = JSON.parse(body || '{}');
    if (!/^https?:\/\//.test(url || '')) throw new Error('url must start with http:// or https://');
    const p = await currentPage();
    await p.goto(url, { waitUntil: 'domcontentloaded', timeout: 60000 });
    await p.waitForTimeout(waitMs);
    return pageInfo(p);
  },

  'POST /eval': async body => {
    const p = await currentPage();
    const result = await p.evaluate(body);
    return { ...(await pageInfo(p)), result };
  },

  'GET /text': async () => {
    const p = await currentPage();
    return { ...(await pageInfo(p)), text: await p.evaluate(() => document.body ? document.body.innerText : '') };
  },

  'GET /screenshot': async () => {
    const p = await currentPage();
    const file = path.join(SHOTS, `shot-${Date.now()}.png`);
    await p.screenshot({ path: file });
    return { ...(await pageInfo(p)), file };
  },

  'POST /close': async () => {
    setTimeout(shutdown, 100);
    return { ok: true };
  },
};

const server = http.createServer(async (req, res) => {
  const route = routes[`${req.method} ${req.url.split('?')[0]}`];
  if (!route) return send(res, 404, { error: `unknown route ${req.method} ${req.url}` });
  try {
    send(res, 200, await route(await readBody(req)));
  } catch (error) {
    send(res, 500, { error: String(error && error.message || error) });
  }
});

let closing = false;
async function shutdown() {
  if (closing) return;
  closing = true;
  server.close();
  await context.close().catch(() => {});
  process.exit(0);
}

context.on('close', () => {
  if (!closing) {
    closing = true;
    server.close();
    process.exit(0);
  }
});
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);

server.listen(PORT, '127.0.0.1', () => {
  console.log(`fx-browser listening on http://127.0.0.1:${PORT}, profile ${PROFILE}`);
});
