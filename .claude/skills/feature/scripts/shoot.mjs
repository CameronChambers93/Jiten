// Takes a list of screenshots on one Jiten site.
// Usage: node shoot.mjs <base-url> <state.json> <spec.json> <out-dir> <before|after>
//
// spec.json is an array of shots, taken in order on one page, so a shot can build on the last:
//   { "name": "1-mined",              // file name: <out-dir>/<name>-<tag>.png
//     "path": "/mined",               // optional: go here first
//     "fill": ["input[placeholder=\"Search words\"]", "運命"],  // optional
//     "click": "button:has(span.text-xl:text-is('運命'))",     // optional, a Playwright selector or a list of them
//     "wait": 1500,                   // optional: ms after each click
//     "waitFor": ".p-toast-message",  // optional: wait for this before shooting
//     "element": "...",               // optional: shoot this element only
//     "fullPage": true,               // optional
//     "only": "after" }               // optional: skip on the other site (e.g. a screen main lacks)
// A shot whose selector isn't found is reported and skipped, not fatal.
//
// AnkiConnect: the pages call 127.0.0.1:8765, which the container can't reach, so their calls go to the host's Anki
// ($ANKI_URL, else host.docker.internal:8765) for real. Reads always pass. Every other action is a write, and runs only
// while Anki's active profile is `test`; otherwise it's refused (the page sees an AnkiConnect error) and the script
// exits 1. Writes are listed at the end, with the ids of notes added, so they can be deleted with the seeded data.
import { createRequire } from 'module';
import { readFileSync, mkdirSync } from 'fs';
// The container points this at /opt/pw-browsers, which isn't writable; setup-tools.sh installs here instead.
// Playwright reads it on load, so set it before the require.
process.env.PLAYWRIGHT_BROWSERS_PATH = '/worktrees/.tools/pw/browsers';
const require = createRequire('/worktrees/.tools/pw/');
const { chromium } = require('playwright');

const [base, statePath, specPath, outDir, tag] = process.argv.slice(2);
if (!tag) {
  console.error('usage: node shoot.mjs <base-url> <state.json> <spec.json> <out-dir> <before|after>');
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

const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1400, height: Number(process.env.VH || 1000) }, storageState: statePath });
const page = await ctx.newPage();
await page.route('http://127.0.0.1:8765/**', async (route) => {
  const req = route.request();
  const headers = { 'access-control-allow-origin': '*', 'access-control-allow-headers': '*', 'content-type': 'application/json' };
  if (req.method() === 'OPTIONS') return route.fulfill({ status: 200, headers });
  const body = JSON.parse(req.postData() || '{}');
  if (!isRead(body)) {
    const profile = await activeProfile().catch(() => null);
    if (profile !== 'test') {
      refused.push(body.action);
      return route.fulfill({ status: 200, headers, body: JSON.stringify({ result: null, error: `refused: Anki's profile is ${profile}, not test` }) });
    }
    writes.push(body.action);
  }
  let text;
  try {
    text = await ankiFetch(req.postData());
  } catch (e) {
    return route.fulfill({ status: 200, headers, body: JSON.stringify({ result: null, error: `AnkiConnect unreachable: ${e.message}` }) });
  }
  if (body.action === 'addNote') {
    const id = JSON.parse(text).result;
    if (id) added.push(id);
  }
  return route.fulfill({ status: 200, headers, body: text });
});
console.log(`Anki profile: ${await activeProfile().catch(() => 'unreachable')}`);

for (const shot of shots) {
  if (shot.only && shot.only !== tag) continue;
  try {
    if (shot.path) {
      await page.goto(base + shot.path, { waitUntil: 'networkidle' });
      await page.waitForTimeout(1500);
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
    const file = `${outDir}/${shot.name}-${tag}.png`;
    if (shot.element) await page.locator(shot.element).first().screenshot({ path: file, timeout: 5000 });
    else await page.screenshot({ path: file, fullPage: !!shot.fullPage });
    console.log(`ok   ${file}`);
  } catch (e) {
    console.log(`skip ${shot.name}-${tag}: ${e.message.split('\n')[0]}`);
  }
}
console.log('AnkiConnect writes:', [...new Set(writes)].join(', ') || 'none');
if (added.length) console.log('notes added (delete them with the seeded data):', added.join(', '));
await browser.close();
if (refused.length) {
  console.error(`refused writes (Anki isn't on the test profile): ${[...new Set(refused)].join(', ')}`);
  process.exit(1);
}
