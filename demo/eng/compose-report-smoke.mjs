// Real Compose/package integration, not an application E2E or TLS trust test.
// Creates only synthetic credentials and a uniquely named disposable stack.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { generateKeyPairSync, randomBytes, randomUUID } from 'node:crypto';
import { mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import http from 'node:http';
import https from 'node:https';

const exec = promisify(execFile);
const cwd = fileURLToPath(new URL('../', import.meta.url));
const version = process.argv[2];
const source = process.argv[3] ?? 'https://api.nuget.org/v3/index.json';
assert.match(version ?? '', /^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$/, 'Pass an explicit package version');
assert.ok(source === 'https://api.nuget.org/v3/index.json' || source.startsWith('/src/.local-feed/'),
  'Use nuget.org or an explicit local feed inside the demo Docker context');
const project = `noelia-report-smoke-${randomUUID()}`;
const directory = await mkdtemp(path.join(tmpdir(), 'noelia-compose-reports-'));
const pair = generateKeyPairSync('ec', {
  namedCurve: 'prime256v1',
  privateKeyEncoding: { format: 'der', type: 'pkcs8' },
  publicKeyEncoding: { format: 'der', type: 'spki' },
});
// Do not inherit a developer's Noelia/Compose/application secrets or .env.
const env = Object.fromEntries(Object.entries(process.env).filter(([key]) =>
  ['PATH', 'HOME', 'TMPDIR', 'DOCKER_HOST', 'DOCKER_CONTEXT', 'DOCKER_CONFIG',
    'DOCKER_TLS_VERIFY', 'DOCKER_CERT_PATH'].includes(key)));
Object.assign(env, {
  NOELIA_JWT_KID: 'compose-smoke-key',
  NOELIA_JWT_PRIVATE_KEY: pair.privateKey.toString('base64'),
  NOELIA_JWT_PUBLIC_KEY: pair.publicKey.toString('base64'),
  NOELIA_MASTER_KEY: randomBytes(32).toString('base64'),
  NOELIA_DASHBOARD_OPERATOR_SECRET: randomBytes(32).toString('hex'),
  NOELIA_SOURCE: source, NOELIA_VERSION: version,
  NOELIA_HTTP_PORT: '0', NOELIA_HTTPS_PORT: '0', COMPOSE_PARALLEL_LIMIT: '2',
});
const secrets = [env.NOELIA_JWT_PRIVATE_KEY, env.NOELIA_MASTER_KEY, env.NOELIA_DASHBOARD_OPERATOR_SECRET];
const redact = value => secrets.reduce((text, secret) => text.replaceAll(secret, '[REDACTED]'), String(value));
async function docker(...args) {
  try {
    return (await exec('docker', args, { cwd, env, maxBuffer: 32 * 1024 * 1024 })).stdout.trim();
  } catch (error) {
    throw new Error(redact(`${error.message}\n${error.stdout ?? ''}\n${error.stderr ?? ''}`));
  }
}
const compose = (...args) => docker('compose', '--env-file', '/dev/null', '-f', 'docker-compose.yml',
  '-p', project, '--profile', 'all', ...args);
function request(stage, host, port, route, secret) {
  return new Promise((resolve, reject) => {
    const client = stage === 'dev' ? http : https;
    const req = client.request({ hostname: '127.0.0.1', port, path: route,
      // Only this synthetic demo request accepts its self-signed certificate.
      rejectUnauthorized: false, servername: host,
      headers: { Host: host, ...(secret ? { 'X-Noelia-Operator': secret } : {}) },
    }, response => {
      let body = '';
      response.setEncoding('utf8');
      response.on('data', chunk => {
        body += chunk;
        if (body.length > 4 * 1024 * 1024) req.destroy(new Error('Oversized smoke response'));
      });
      response.on('error', reject);
      response.on('end', () => resolve({ status: response.statusCode, body }));
    });
    req.setTimeout(15000, () => req.destroy(new Error('Smoke request timeout')));
    req.on('error', reject);
    req.end();
  });
}
const manifest = JSON.parse(await readFile(new URL('./composition-baselines.json', import.meta.url), 'utf8'));
assert.equal(manifest.SchemaVersion, 1);
const baselines = new Map(manifest.Roles.map(role => [role.Name, [...role.Modules].sort()]));
const hosts = [
  ['gateway', 'gateway', 'Gateway.Api'], ['user', 'issuer', 'UserService.Api'],
  ['todo', 'verifier', 'TodoService.Api'], ['mono', 'monolith', 'Demo.Monolith.Api'],
];
let started = false;
const heartbeat = setInterval(() => console.log(`Working: isolated stack ${project}`), 30000);
try {
  // Refuse to operate if even the randomly generated project identity exists.
  for (const kind of ['container', 'volume', 'network']) {
    assert.equal(await docker(kind, 'ls', '-q', '--filter', `label=com.docker.compose.project=${project}`), '');
  }
  console.log(`Building ${version}; project ${project}; no existing .env or credentials used`);
  await compose('config', '--quiet');
  await compose('build');
  console.log('Images built; starting disposable Compose services');
  started = true;
  await compose('up', '-d', '--no-build', '--wait', '--wait-timeout', '180');
  const httpPort = (await compose('port', 'edge', '8080')).split(':').at(-1);
  const httpsPort = (await compose('port', 'edge', '8443')).split(':').at(-1);
  const results = [];
  for (const stage of ['dev', 'staging', 'prod']) {
    const port = stage === 'dev' ? httpPort : httpsPort;
    for (const [host, role, dll] of hosts) {
      const service = `${host}-${stage}`;
      const hostname = `${service}.localhost`;
      const deps = JSON.parse(await compose('exec', '-T', service, 'cat', `/app/${dll}.deps.json`));
      const packages = Object.keys(deps.libraries).filter(name => name.startsWith('Noelia.'));
      assert.ok(packages.length > 0);
      assert.ok(packages.every(name => name.split('/')[1] === version), `${service}: unexpected Noelia package version`);
      if (stage !== 'dev') {
        for (const secret of [undefined, 'wrong-smoke-key']) {
          for (const route of ['/noelia/report.json', '/noelia/audit-chain.json']) {
            assert.equal((await request(stage, hostname, port, route, secret)).status, 404, `${service}: unauthorized ${route}`);
          }
        }
      }
      const response = await request(stage, hostname, port, '/noelia/report.json', env.NOELIA_DASHBOARD_OPERATOR_SECRET);
      assert.equal(response.status, 200, `${service}: report status`);
      for (const secret of secrets) assert.ok(!response.body.includes(secret), `${service}: secret leaked`);
      const report = JSON.parse(response.body);
      assert.equal(report.schemaVersion, 3, `${service}: schema`);
      const modules = report.composition.modules.filter(module => module.isRunning === true).map(module => module.name).sort();
      const baseline = `${role}-${stage === 'dev' ? 'memory' : 'redis'}`;
      assert.deepEqual(modules, baselines.get(baseline), `${service}: reviewed baseline ${baseline}`);
      const chain = await request(stage, hostname, port, '/noelia/audit-chain.json', env.NOELIA_DASHBOARD_OPERATOR_SECRET);
      assert.equal(chain.status, 200, `${service}: audit report status (not chain completeness)`);
      JSON.parse(chain.body);
      if (host === 'gateway' && stage !== 'dev') {
        for (const route of ['/noelia', '/noelia/composition', '/noelia/assets/dashboard.js', '/noelia/report.json/extra']) {
          assert.equal((await request(stage, hostname, port, route, env.NOELIA_DASHBOARD_OPERATOR_SECRET)).status, 404);
        }
      }
      await writeFile(path.join(directory, `${service}.json`), response.body, { mode: 0o600 });
      results.push({ service, baseline, modules: modules.length, version, schema: report.schemaVersion });
      console.log(`PASS: ${service}, ${baseline}, schema 3, exact package ${version}, report access policy`);
    }
  }
  await writeFile(path.join(directory, 'summary.json'), JSON.stringify(results, null, 2), { mode: 0o600 });
  console.log(`PASS: all 12 host reports through the real edge; reports: ${directory}`);
  if (process.env.NOELIA_CP_SMOKE_MODULE) {
    const { runDemoControlPlaneSmoke } = await import(pathToFileURL(path.resolve(process.env.NOELIA_CP_SMOKE_MODULE)).href);
    await runDemoControlPlaneSmoke({ directory, httpPort, httpsPort,
      operatorSecret: env.NOELIA_DASHBOARD_OPERATOR_SECRET, playwrightModule: process.env.PLAYWRIGHT_MODULE });
  } else console.log('CP live collection/browser smoke not requested.');
  console.log('Not covered: application browser journeys, restart durability, TLS trust, audit completeness.');
} catch (error) {
  if (started) {
    const logs = await compose('logs', '--no-color', '--tail', '80').catch(() => 'Log capture failed');
    await writeFile(path.join(directory, 'failure.log'), redact(logs), { mode: 0o600 });
    console.error(`Sanitized synthetic-stack diagnostic: ${directory}/failure.log`);
  }
  throw error;
} finally {
  clearInterval(heartbeat);
  // Only this fresh UUID-labelled stack and its newly created synthetic volumes.
  // Images/reports are retained; never runs prune or stops another project.
  if (started) {
    await compose('down', '--volumes', '--timeout', '10');
    console.log(`Removed only synthetic containers/networks/volumes of ${project}; reports retained.`);
  }
}
