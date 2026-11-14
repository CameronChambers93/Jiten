// Logs in to a Jiten site and saves the browser state for shoot.mjs (which also calls login() itself before each run).
// Usage: node login.mjs <base-url> <state.json>
// Reads JITEN_USER and JITEN_PASSWORD from the environment or /worktrees/.tools/jiten-login.env.
import { createRequire } from 'module';
import { readFileSync } from 'fs';
import { fileURLToPath } from 'url';
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
    // No file: login() says what's missing.
  }
}

/** Logs in at base with the credentials above and writes the browser state to statePath. */
export async function login(base, statePath) {
  const { JITEN_USER: user, JITEN_PASSWORD: password } = process.env;
  if (!user || !password) throw new Error(`no credentials: set JITEN_USER and JITEN_PASSWORD or put them in ${LOGIN_FILE}`);
  const browser = await chromium.launch();
  try {
    const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
    const page = await ctx.newPage();
    await page.goto(`${base}/login`, { waitUntil: 'networkidle' });
    await page.fill('#usernameOrEmail', user);
    await page.fill('#password', password);
    await page.click('button[type=submit]');
    await page.waitForURL((u) => !u.pathname.startsWith('/login'), { timeout: 30000 });
    await ctx.storageState({ path: statePath });
  } finally {
    await browser.close();
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [base, statePath] = process.argv.slice(2);
  if (!base || !statePath) {
    console.error('usage: node login.mjs <base-url> <state.json>');
    process.exit(1);
  }
  await login(base, statePath);
  console.log(`logged in at ${base}`);
}
