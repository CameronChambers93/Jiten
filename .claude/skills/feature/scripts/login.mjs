// Logs in to a Jiten site and saves the browser state for shoot.mjs.
// Usage: node login.mjs <base-url> <state.json>
// Reads JITEN_USER and JITEN_PASSWORD from the environment or /worktrees/.tools/jiten-login.env.
import { createRequire } from 'module';
import { readFileSync } from 'fs';
// The container points this at /opt/pw-browsers, which isn't writable; setup-tools.sh installs here instead.
// Playwright reads it on load, so set it before the require.
process.env.PLAYWRIGHT_BROWSERS_PATH = '/worktrees/.tools/pw/browsers';
const require = createRequire('/worktrees/.tools/pw/');
const { chromium } = require('playwright');

// Credentials come from the environment, else from the local file setup-tools.sh documents (never from a repo).
const LOGIN_FILE = '/worktrees/.tools/jiten-login.env';
if (!process.env.JITEN_USER || !process.env.JITEN_PASSWORD) {
  try {
    for (const line of readFileSync(LOGIN_FILE, 'utf8').split('\n')) {
      const m = line.match(/^\s*(JITEN_USER|JITEN_PASSWORD)=(.*)$/);
      if (m) process.env[m[1]] ??= m[2].trim();
    }
  } catch {
    // No file: the usage message below says what's missing.
  }
}

const [base, statePath] = process.argv.slice(2);
const { JITEN_USER: user, JITEN_PASSWORD: password } = process.env;
if (!base || !statePath || !user || !password) {
  console.error(`usage: node login.mjs <base-url> <state.json>, with JITEN_USER and JITEN_PASSWORD set or in ${LOGIN_FILE}`);
  process.exit(1);
}

const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
await page.goto(`${base}/login`, { waitUntil: 'networkidle' });
await page.fill('#usernameOrEmail', user);
await page.fill('#password', password);
await page.click('button[type=submit]');
await page.waitForURL((u) => !u.pathname.startsWith('/login'), { timeout: 30000 });
await ctx.storageState({ path: statePath });
console.log(`logged in at ${base}`);
await browser.close();
