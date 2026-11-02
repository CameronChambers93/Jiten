// Takes a list of screenshots on one Jiten site.
// Usage: node shoot.mjs <base-url> <state.json> <spec.json> <out-dir> <before|after>
//
// spec.json is an array of shots, taken in order on one page, so a shot can build on the last:
//   { "name": "1-mined",              // file name: <out-dir>/<name>-<tag>.png
//     "path": "/mined",               // optional: go here first
//     "fill": ["input[placeholder=\"Search words\"]", "運命"],  // optional
//     "click": "button:has(span.text-xl:text-is('運命'))",     // optional, a Playwright selector
//     "element": "...",               // optional: shoot this element only
//     "fullPage": true,               // optional
//     "only": "after" }               // optional: skip on the other site (e.g. a screen main lacks)
// A shot whose selector isn't found is reported and skipped, not fatal.
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

const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1400, height: 1000 }, storageState: statePath });
const page = await ctx.newPage();

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
    if (shot.click) {
      await page.locator(shot.click).first().click({ timeout: 5000 });
      await page.waitForTimeout(1500);
    }
    const file = `${outDir}/${shot.name}-${tag}.png`;
    if (shot.element) await page.locator(shot.element).first().screenshot({ path: file, timeout: 5000 });
    else await page.screenshot({ path: file, fullPage: !!shot.fullPage });
    console.log(`ok   ${file}`);
  } catch (e) {
    console.log(`skip ${shot.name}-${tag}: ${e.message.split('\n')[0]}`);
  }
}
await browser.close();
