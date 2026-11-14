// Drives Chrome on the host's Android emulator (AVD lumix_s23plus) for shoot.mjs --phone.
//
// The container reaches the host's adb server (host.docker.internal:5037). Chrome's DevTools socket on the device is
// tunnelled through it to a local port, and Playwright attaches with connectOverCDP. `adb reverse` maps the site's web
// and API ports on the emulator to the same ports on the host, so https://localhost:<port> works there as it does here,
// and the login state from login.mjs (same origin) applies. Certificate errors are ignored browser-wide, as on the desktop.
//
// Android Chrome quirks this works around:
// - It can't open a tab over CDP, so the script uses Chrome's current tab (it starts Chrome if needed).
// - Playwright follows only the first navigation of a connection there: after a second one, page.url() and locators
//   stay on the old document. So open() reconnects for every page it opens.
// - page.screenshot comes back blank, and nothing beyond the viewport can be captured; use shoot() below.
// - Request interception (page.route) over the tunnel stalls a dev page's hundreds of Vite requests, and a paused
//   request outlives a killed script. A script on every document wraps fetch instead (see hooks below).
import net from 'net';
import { readFileSync, writeFileSync } from 'fs';
import { execFileSync } from 'child_process';

const ADB_HOST = 'host.docker.internal';
const ADB_PORT = 5037;
const adbEnv = { ...process.env, ADB_SERVER_SOCKET: `tcp:${ADB_HOST}:${ADB_PORT}` };
const adb = (...args) => execFileSync('adb', args, { env: adbEnv, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();

/** Opens a stream to a service on the device (e.g. localabstract:...) through the host's adb server. */
function adbStream(serial, service) {
  return new Promise((resolve, reject) => {
    const s = net.connect(ADB_PORT, ADB_HOST);
    const steps = [`host:transport:${serial}`, service];
    let buf = Buffer.alloc(0);
    const send = () => s.write(steps[0].length.toString(16).padStart(4, '0') + steps[0]);
    const onData = (d) => {
      buf = Buffer.concat([buf, d]);
      while (buf.length >= 4) {
        if (buf.subarray(0, 4).toString() !== 'OKAY') return reject(new Error(`adb ${steps[0]}: ${buf.toString()}`));
        buf = buf.subarray(4);
        steps.shift();
        if (!steps.length) {
          s.off('data', onData);
          if (buf.length) s.unshift(buf);
          return resolve(s);
        }
        send();
      }
    };
    s.on('error', reject);
    s.on('data', onData);
    s.on('connect', send);
  });
}

/** The API port that goes with a web port: main 3000 -> 7299, session 300N -> 730N. */
const apiPort = (webPort) => (webPort === 3000 ? 7299 : webPort + 4300);

/**
 * Prepares the emulator's Chrome for `base` with the login state in statePath. The pages' AnkiConnect calls go to
 * onAnki(requestBody), which returns the response text. Returns { open(url), page(), close() }: open() loads a page on a
 * fresh connection, and page() is the Playwright page for it.
 */
export async function connectPhone(chromium, base, statePath, onAnki) {
  const serial = adb('devices').split('\n').slice(1).map((l) => l.split('\t')).find(([, st]) => st === 'device')?.[0];
  if (!serial) throw new Error('no emulator: start AVD lumix_s23plus on the host (adb devices is empty)');
  const webPort = Number(new URL(base).port);
  const api = `https://localhost:${apiPort(webPort)}`;
  for (const port of [webPort, apiPort(webPort)]) adb('-s', serial, 'reverse', `tcp:${port}`, `tcp:${port}`);
  // Chrome only listens once it has run; start it (a no-op when it's already open).
  adb('-s', serial, 'shell', 'am', 'start', '-n', 'com.android.chrome/com.google.android.apps.chrome.Main');

  const server = net.createServer(async (client) => {
    try {
      const dev = await adbStream(serial, 'localabstract:chrome_devtools_remote');
      client.pipe(dev).pipe(client);
      dev.on('close', () => client.destroy());
      client.on('close', () => dev.destroy());
    } catch (e) {
      console.error(e.message);
      client.destroy();
    }
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const endpoint = `http://127.0.0.1:${server.address().port}`;

  const state = JSON.parse(readFileSync(statePath, 'utf8'));
  const storage = state.origins.find((o) => o.origin === new URL(base).origin)?.localStorage ?? [];
  let browser = null;
  let page = null;
  let first = true;

  /** A fresh connection to the tab as it is now, with the hooks for this and later documents. */
  async function attach() {
    if (browser) await browser.close(); // disconnects; Chrome keeps running
    browser = await chromium.connectOverCDP(endpoint);
    await (await browser.newBrowserCDPSession()).send('Security.setIgnoreCertificateErrors', { ignore: true });
    const ctx = browser.contexts()[0];
    page = ctx.pages()[0];
    if (!page) throw new Error('Chrome on the emulator has no tab open');
    if (first) {
      // Main and the sessions all live on localhost, where cookies ignore the port: replace them with this site's.
      await ctx.clearCookies();
      await ctx.addCookies(state.cookies);
      first = false;
    }
    // hooks: main's pages call the API at https://jiten.local (JITEN_HOST), which the emulator can't resolve without
    // jiten_dns.py; Caddy passes /api on unchanged, so those calls go straight to the API port. AnkiConnect calls
    // (127.0.0.1:8765 would be AnkiConnect Android on the emulator) go to onAnki in Node. The login state's
    // localStorage is restored on the site's origin.
    await page.exposeFunction('__phoneAnki', onAnki);
    await page.addInitScript(({ api, origin, storage }) => {
      if (location.origin === origin) for (const { name, value } of storage) localStorage.getItem(name) ?? localStorage.setItem(name, value);
      const real = window.fetch.bind(window);
      window.fetch = async (input, init) => {
        const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
        if (/^https?:\/\/(127\.0\.0\.1|localhost):8765\b/.test(url)) {
          const body = init?.body ?? (input instanceof Request ? await input.text() : '');
          return new Response(await window.__phoneAnki(String(body)), { headers: { 'content-type': 'application/json' } });
        }
        if (url.startsWith('https://jiten.local/api/')) return real(url.replace('https://jiten.local', api), init ?? (input instanceof Request ? input : undefined));
        return real(input, init);
      };
    }, { api, origin: new URL(base).origin, storage });
  }

  /**
   * Loads url in the tab. Playwright loses track of the page after some navigations there (page.url() and locators
   * stay on the old document; main-world evaluate still works), so navigate from the page, wait for the new document
   * in the main world, then reconnect so Playwright attaches to it. The hooks were in place for its start.
   */
  async function open(url) {
    if (!browser) await attach();
    const href = () => page.evaluate(() => [location.href, document.readyState]).catch(() => ['', 'loading']);
    await page.evaluate((u) => { location.href = u; }, url).catch(() => {}); // the context goes away mid-call
    await page.waitForTimeout(1000);
    for (const start = Date.now(); ; await page.waitForTimeout(500)) {
      const [at, ready] = await href();
      if (at.split('#')[0] === url.split('#')[0] && ready !== 'loading') break;
      if (Date.now() - start > 120000) throw new Error(`${url} didn't load (the tab is at ${at || '?'})`);
    }
    await attach();
    return page;
  }

  async function close() {
    if (browser) await browser.close();
    server.close();
  }

  return { open, page: () => page, close };
}

/**
 * A screenshot of what the phone shows: the viewport, or one element cut from it. Android Chrome can't capture beyond
 * the viewport (it repeats the visible part), so fullPage isn't supported and an element taller than the screen is cut
 * at its bottom edge; scroll and take several shots instead.
 */
export async function shoot(page, file, { element, fullPage } = {}) {
  if (fullPage) console.log(`note: ${file}: fullPage isn't supported on the phone; took the viewport`);
  const cdp = await page.context().newCDPSession(page);
  await page.evaluate(() => document.activeElement?.blur()); // drops the soft keyboard
  // The surface is taller than the page area (it includes the space under Chrome's toolbar), so always clip.
  let [x, y, width, height] = await page.evaluate(() => [scrollX, scrollY, innerWidth, innerHeight]);
  if (element) {
    const target = page.locator(element).first();
    await target.scrollIntoViewIfNeeded({ timeout: 5000 });
    const box = await target.boundingBox();
    if (!box) throw new Error(`${element} isn't visible`);
    [x, y] = await page.evaluate(() => [scrollX, scrollY]);
    const top = Math.max(box.y, 0);
    [x, y, width, height] = [x + box.x, y + top, box.width, Math.min(box.y + box.height, height) - top];
  }
  const { data } = await cdp.send('Page.captureScreenshot', { format: 'png', fromSurface: true, clip: { x, y, width, height, scale: 1 } });
  writeFileSync(file, Buffer.from(data, 'base64'));
  await cdp.detach();
}
