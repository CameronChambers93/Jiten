// Takes a list of screenshots on one Jiten site.
// Usage: node shoot.mjs [--phone] <base-url> <state.json> <spec.json> <out-dir> <before|after>
//
// It logs in first (login.mjs, with the credentials in /worktrees/.tools/jiten-login.env) and saves the state to
// state.json, so each run starts with a fresh token.
//
// --phone takes them in Chrome on the host's Android emulator instead of headless Chromium (see phone.mjs), with the
// same spec and login state; files are named <name>-<tag>-phone.png. The emulator's viewport is fixed (about 432x832),
// fullPage isn't supported there, and only "path" may load a new document (clicks that navigate within the app are fine).
//
// spec.json is an array of shots, taken in order on one page, so a shot can build on the last:
//   { "name": "1-mined",              // file name: <out-dir>/<name>-<tag>.png
//     "path": "/mined",               // optional: go here first
//     "ready": "text=Mined words",    // optional: after "path", wait for this before filling or clicking
//     "fill": ["input[placeholder=\"Search words\"]", "運命"],  // optional
//     "click": "button:has(span.text-xl:text-is('運命'))",     // optional, a Playwright selector or a list of them
//     "wait": 1500,                   // optional: ms after each click
//     "waitFor": ".p-toast-message",  // optional: wait for this before shooting
//     "element": "...",               // optional: shoot this element only
//     "fullPage": true,               // optional
//     "only": "after",                // optional: skip on the other site (e.g. a screen main lacks)
//     "device": "phone" }             // optional: only with (phone) or without (desktop) --phone
// A shot whose selector isn't found is reported and skipped, not fatal.
//
// AnkiConnect: the pages call 127.0.0.1:8765, which the container can't reach, so their calls go to the host's Anki
// ($ANKI_URL, else host.docker.internal:8765) for real. Reads always pass. Every other action is a write, and runs only
// while Anki's active profile is `test`; otherwise it's refused (the page sees an AnkiConnect error) and the script
// exits 1. Writes are listed at the end, with the ids of notes added, so they can be deleted with the seeded data.
// With --phone the same rules apply, through a fetch wrapper instead of a route (see phone.mjs).
import { createRequire } from 'module';
import { readFileSync, mkdirSync } from 'fs';
// The container points this at /opt/pw-browsers, which isn't writable; setup-tools.sh installs here instead.
// Playwright reads it on load, so set it before the require.
process.env.PLAYWRIGHT_BROWSERS_PATH = '/worktrees/.tools/pw/browsers';
const require = createRequire('/worktrees/.tools/pw/');
const { chromium } = require('playwright');
import { connectPhone, shoot } from './phone.mjs';
import { login } from './login.mjs';

const args = process.argv.slice(2);
const phone = args.includes('--phone');
const [base, statePath, specPath, outDir, tag] = args.filter((a) => a !== '--phone');
if (!tag) {
  console.error('usage: node shoot.mjs [--phone] <base-url> <state.json> <spec.json> <out-dir> <before|after>');
  process.exit(1);
}
const shots = JSON.parse(readFileSync(specPath, 'utf8'));
mkdirSync(outDir, { recursive: true });

const ANKI = process.env.ANKI_URL || 'http://host.docker.internal:8765';
const READS = new Set(['version', 'getActiveProfile', 'getProfiles', 'deckNames', 'deckNamesAndIds', 'modelNames', 'modelFieldNames',
  'modelTemplates', 'modelStyling', 'findNotes', 'findCards', 'notesInfo', 'cardsInfo', 'getDecks', 'getReviewsOfCards', 'retrieveMediaFile',
  'getTags', 'multi']);
const activeProfile = async () =>
  (await (await fetch(ANKI, { method: 'POST', body: JSON.stringify({ action: 'getActiveProfile', version: 6 }) })).json()).result;
/** A `multi` call is a read only when every action in it is. */
const isRead = (body) => READS.has(body.action) && (body.action !== 'multi' || (body.params?.actions ?? []).every(isRead));
const writes = [];
const refused = [];
const added = [];

// AnkiConnect refuses connections under a burst (the settings page counts every note type at once), so calls go one at
// a time, and a refused connection is retried.
let ankiQueue = Promise.resolve();
function ankiFetch(body) {
  const run = async () => {
    for (let attempt = 0; ; attempt++) {
      try {
        const res = await fetch(ANKI, { method: 'POST', body });
        return await res.text();
      } catch (e) {
        if (attempt >= 5) throw e;
        await new Promise((r) => setTimeout(r, 300 * (attempt + 1)));
      }
    }
  };
  const next = ankiQueue.then(run, run);
  ankiQueue = next.catch(() => {});
  return next;
}

/** Answers one AnkiConnect request from the page (its JSON body) with the response text, applying the rules above. */
async function anki(postData) {
  const body = JSON.parse(postData || '{}');
  if (!isRead(body)) {
    const profile = await activeProfile().catch(() => null);
    if (profile !== 'test') {
      refused.push(body.action);
      return JSON.stringify({ result: null, error: `refused: Anki's profile is ${profile}, not test` });
    }
    writes.push(body.action);
  }
  let text;
  try {
    text = await ankiFetch(postData);
  } catch (e) {
    return JSON.stringify({ result: null, error: `AnkiConnect unreachable: ${e.message}` });
  }
  if (body.action === 'addNote') {
    const id = JSON.parse(text).result;
    if (id) added.push(id);
  }
  return text;
}

await login(base, statePath);
console.log(`logged in at ${base}`);

let page, close, device;
if (phone) {
  device = await connectPhone(chromium, base, statePath, anki);
  page = await device.open(`${base}${shots.find((s) => s.path)?.path ?? '/'}`);
  close = device.close;
} else {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1400, height: Number(process.env.VH || 1000) }, storageState: statePath });
  page = await ctx.newPage();
  close = () => browser.close();
  await page.route('http://127.0.0.1:8765/**', async (route) => {
    const req = route.request();
    const headers = { 'access-control-allow-origin': '*', 'access-control-allow-headers': '*', 'content-type': 'application/json' };
    if (req.method() === 'OPTIONS') return route.fulfill({ status: 200, headers });
    return route.fulfill({ status: 200, headers, body: await anki(req.postData()) });
  });
}
console.log(`Anki profile: ${await activeProfile().catch(() => 'unreachable')}`);

for (const shot of shots) {
  if (shot.only && shot.only !== tag) continue;
  if (shot.device && (shot.device === 'phone') !== phone) continue;
  try {
    if (shot.path) {
      // On the phone this only waits for the document to parse, so give such shots a waitFor.
      if (phone) page = page.url() === base + shot.path ? page : await device.open(base + shot.path);
      else await page.goto(base + shot.path, { waitUntil: 'networkidle' });
      if (shot.ready) await page.locator(shot.ready).first().waitFor({ timeout: 120000 });
      await page.waitForTimeout(phone ? 4000 : 1500); // the phone hydrates a dev page more slowly
    }
    if (shot.fill) {
      await page.fill(shot.fill[0], shot.fill[1], { timeout: 5000 });
      await page.waitForTimeout(1500);
    }
    for (const c of [].concat(shot.click ?? [])) {
      await page.locator(c).first().click({ timeout: 5000 });
      await page.waitForTimeout(shot.wait ?? 1500);
    }
    if (shot.waitFor) await page.locator(shot.waitFor).first().waitFor({ timeout: 120000 });
    const file = `${outDir}/${shot.name}-${tag}${phone ? '-phone' : ''}.png`;
    if (phone) await shoot(page, file, shot);
    else if (shot.element) await page.locator(shot.element).first().screenshot({ path: file, timeout: 5000 });
    else await page.screenshot({ path: file, fullPage: !!shot.fullPage });
    console.log(`ok   ${file}`);
  } catch (e) {
    console.log(`skip ${shot.name}-${tag}: ${e.message.split('\n')[0]}`);
  }
}
console.log('AnkiConnect writes:', [...new Set(writes)].join(', ') || 'none');
if (added.length) console.log('notes added (delete them with the seeded data):', added.join(', '));
await close();
if (refused.length) {
  console.error(`refused writes (Anki isn't on the test profile): ${[...new Set(refused)].join(', ')}`);
  process.exit(1);
}
