// Isolated frontend acceptance only, not a full gateway/backend integration test.
// Uses an existing Playwright installation; never reads demo credentials or volumes.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { randomUUID } from 'node:crypto';
import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const exec = promisify(execFile);
const docker = async (...args) => (await exec('docker', args)).stdout.trim();
const image = process.argv[2];
assert.ok(image, 'Pass the already built demo frontend image tag');
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE
  ? pathToFileURL(path.resolve(process.env.PLAYWRIGHT_MODULE)).href : 'playwright');
const name = `noelia-frontend-smoke-${randomUUID()}`;
const directory = await mkdtemp(path.join(tmpdir(), 'noelia-frontend-smoke-'));
let container;
let browser;
try {
  container = await docker('run', '-d', '--name', name,
    '-p', '127.0.0.1::8080', '-p', '127.0.0.1::8443', image);
  await docker('exec', container, 'nginx', '-t');
  const httpPort = (await docker('port', container, '8080/tcp')).split(':').at(-1);
  const httpsPort = (await docker('port', container, '8443/tcp')).split(':').at(-1);
  browser = await chromium.launch({ headless: true });
  // The demo certificate is explicitly self-signed; this is not a TLS trust test.
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  for (const stage of ['dev', 'staging', 'prod']) {
    const protocol = stage === 'dev' ? 'http' : 'https';
    const port = stage === 'dev' ? httpPort : httpsPort;
    const response = await page.goto(`${protocol}://micro-${stage}.localhost:${port}/noelia`);
    assert.equal(response.status(), 200);
    await page.waitForLoadState('networkidle');
    const links = await page.locator('[data-dashboards] a').evaluateAll(items => items.map(a => a.href));
    assert.equal(links.length, stage === 'dev' ? 4 : 3);
    assert.equal(links.some(link => link.includes('gateway-')), stage === 'dev');
    if (stage !== 'dev') {
      assert.match(await page.locator('[data-note]').innerText(), /only authenticated JSON reports/);
      for (const route of ['/noelia', '/noelia/composition', '/noelia/assets/dashboard.js', '/noelia/report.json/extra']) {
        const blocked = await context.request.get(`https://gateway-${stage}.localhost:${port}${route}`,
          { headers: { 'X-Noelia-Operator': 'synthetic-canary-not-a-real-secret' } });
        assert.equal(blocked.status(), 404);
      }
    }
    await page.screenshot({ path: path.join(directory, `${stage}.png`), fullPage: true });
  }
  assert.deepEqual(errors, []);
  console.log('PASS: stage-specific dashboard index, gateway HTML/assets denied at nginx, no browser errors');
  console.log(`Screenshots: ${directory}`);
} finally {
  if (browser) await browser.close();
  // Only the exact container created by this run, never a stack or named user volume.
  if (container) await docker('rm', '-f', container);
}
