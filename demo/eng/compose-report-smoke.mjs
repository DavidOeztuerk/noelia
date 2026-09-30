// Real Compose/package integration, not an application E2E or TLS trust test.
// Creates only synthetic credentials and a uniquely named disposable stack.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { generateKeyPairSync, randomBytes, randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
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
// Caller-owned mode, used by release-gate.mjs. Everything is opt-in through the
// environment so the documented invocation above is unchanged:
//   NOELIA_SMOKE_PROJECT     a unique project name chosen by the caller
//   NOELIA_SMOKE_DIR         where the secret-free reports go (created if missing)
//   NOELIA_SMOKE_HTTP_PORT / NOELIA_SMOKE_HTTPS_PORT   fixed loopback host ports
//   NOELIA_SMOKE_KEEP_STATE  a directory that receives stack.json and stack.env
//                            (synthetic credentials, mode 0600). Setting it means the
//                            CALLER owns the stack: it stays up on success and on
//                            failure, and the caller must `down -v` its own project.
const ownedProject = process.env.NOELIA_SMOKE_PROJECT;
assert.ok(ownedProject === undefined || /^noelia-[a-z0-9-]{8,60}$/.test(ownedProject),
  'NOELIA_SMOKE_PROJECT must look like noelia-<unique>');
const fixedPort = name => {
  const value = process.env[name];
  if (value === undefined) return '0';
  assert.match(value, /^\d{4,5}$/, `${name} must be a port number`);
  return value;
};
const keepState = process.env.NOELIA_SMOKE_KEEP_STATE;
const project = ownedProject ?? `noelia-report-smoke-${randomUUID()}`;
const directory = process.env.NOELIA_SMOKE_DIR
  ? (await mkdir(process.env.NOELIA_SMOKE_DIR, { recursive: true, mode: 0o700 }), path.resolve(process.env.NOELIA_SMOKE_DIR))
  : await mkdtemp(path.join(tmpdir(), 'noelia-compose-reports-'));
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
  NOELIA_HTTP_PORT: fixedPort('NOELIA_SMOKE_HTTP_PORT'), NOELIA_HTTPS_PORT: fixedPort('NOELIA_SMOKE_HTTPS_PORT'),
  COMPOSE_PARALLEL_LIMIT: '2',
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
  if (keepState) {
    // Written as soon as the stack is up, before any assertion: a failed assertion
    // must not take the stack away from the caller's later steps.
    await mkdir(keepState, { recursive: true, mode: 0o700 });
    const names = ['NOELIA_JWT_KID', 'NOELIA_JWT_PRIVATE_KEY', 'NOELIA_JWT_PUBLIC_KEY', 'NOELIA_MASTER_KEY',
      'NOELIA_DASHBOARD_OPERATOR_SECRET', 'NOELIA_SOURCE', 'NOELIA_VERSION', 'NOELIA_HTTP_PORT', 'NOELIA_HTTPS_PORT',
      'COMPOSE_PARALLEL_LIMIT'];
    await writeFile(path.join(keepState, 'stack.env'), names.map(name => `${name}=${env[name]}`).join('\n') + '\n', { mode: 0o600 });
    await writeFile(path.join(keepState, 'stack.json'), JSON.stringify({ project, httpPort, httpsPort, version }), { mode: 0o600 });
  }
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
  if (started && !keepState) {
    await compose('down', '--volumes', '--timeout', '10');
    console.log(`Removed only synthetic containers/networks/volumes of ${project}; reports retained.`);
  }
}
