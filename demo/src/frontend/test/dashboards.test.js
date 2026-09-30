import test from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';

for (const stage of ['dev', 'staging', 'prod']) {
  test(`dashboard index describes gateway visibility in ${stage}`, async () => {
    const dom = new JSDOM('<ul data-dashboards></ul><p data-note></p>', {
      url: `${stage === 'dev' ? 'http' : 'https'}://micro-${stage}.localhost:${stage === 'dev' ? 8080 : 8443}/noelia`
    });
    globalThis.document = dom.window.document;
    globalThis.location = dom.window.location;
    try {
      await import(`../js/dashboards.js?stage=${stage}`);
      const links = [...document.querySelectorAll('a')].map(link => link.href);
      assert.equal(links.length, stage === 'dev' ? 4 : 3);
      assert.equal(links.some(link => link.includes('gateway-')), stage === 'dev');
      const note = document.querySelector('[data-note]').textContent;
      if (stage !== 'dev') assert.match(note, /only authenticated JSON reports/);
    } finally {
      delete globalThis.document;
      delete globalThis.location;
      dom.window.close();
    }
  });
}
