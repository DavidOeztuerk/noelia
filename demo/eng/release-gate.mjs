#!/usr/bin/env node
// The local release gate: proves the exact package set that would be published.
//
//   node demo/eng/release-gate.mjs [VERSION] [--cp-repo PATH] [--report-dir DIR] [--require-clean]
//
// One candidate is packed once (step 1) and every later step consumes THAT feed:
// the demo's .NET tests, the Control Plane's tests, the Compose stack, the security
// gate, six browser combinations and the CP live smoke. Nothing here is skipped
// quietly: a missing prerequisite, a step that cannot run because an earlier one
// failed, a skipped test and a leftover container all end in a non-zero exit. A
// skipped step is never a pass.
//
// Safety: it only ever touches what it created. A unique Compose project
// (`noelia-gate-*`), free loopback ports, an isolated NuGet restore path and
// artifacts directory per consumer, synthetic secrets in a 0700 temp directory.
// Teardown is `down -v` on that one project (also on failure and on SIGINT/SIGTERM);
// there is no prune, no FLUSHALL and no touching of ~/.nuget/packages.
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createServer } from 'node:net';
import { randomBytes } from 'node:crypto';
import { existsSync, createWriteStream, readdirSync, statSync } from 'node:fs';
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const execFileAsync = promisify(execFile);
const demoRoot = fileURLToPath(new URL('../', import.meta.url));
const repoRoot = path.resolve(demoRoot, '..');
const frontend = path.join(demoRoot, 'src/frontend');

// --- arguments ---------------------------------------------------------------

const args = process.argv.slice(2);
const options = { version: undefined, cpRepo: path.resolve(repoRoot, '../NoeliaControlPlane'), reportDir: undefined, requireClean: false };
for (let i = 0; i < args.length; i += 1) {
  const arg = args[i];
  if (arg === '--cp-repo') options.cpRepo = path.resolve(args[++i] ?? '');
  else if (arg === '--report-dir') options.reportDir = path.resolve(args[++i] ?? '');
  else if (arg === '--require-clean') options.requireClean = true;
  else if (!arg.startsWith('-') && options.version === undefined) options.version = arg;
  else { console.error(`Unknown argument: ${arg}`); process.exit(64); }
}

const stamp = new Date().toISOString().replace(/[-:T]/g, '').slice(0, 14);
const versionPrefix = (await readFile(path.join(repoRoot, 'Directory.Build.props'), 'utf8'))
  .match(/<VersionPrefix>\s*([0-9]+\.[0-9]+\.[0-9]+)\s*<\/VersionPrefix>/)?.[1];
if (!versionPrefix) { console.error('No <VersionPrefix> in Directory.Build.props'); process.exit(64); }
const version = options.version ?? `${versionPrefix}-gate.${stamp}`;
if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(version)) { console.error(`Invalid version: ${version}`); process.exit(64); }

const reportDir = options.reportDir ?? await mkdtemp(path.join(tmpdir(), 'noelia-release-gate-'));
await mkdir(reportDir, { recursive: true, mode: 0o700 });
const secretDir = await mkdtemp(path.join(tmpdir(), 'noelia-release-gate-state-'));
const feedName = `gate-${stamp}-${randomBytes(3).toString('hex')}`;
const feedDir = path.join(demoRoot, '.local-feed', feedName);   // new, uniquely named, never an existing feed
const project = `noelia-gate-${stamp}-${randomBytes(3).toString('hex')}`;
const cpCache = path.join(reportDir, 'cp-cache');
const cpArtifacts = path.join(reportDir, 'cp-artifacts');

// --- plumbing ------------------------------------------------------------------

const results = [];
const meta = { version, versionPrefix, project, feed: feedDir, reportDir, started: new Date().toISOString() };
let currentStep = '';
const heartbeat = setInterval(() => console.log(`  ... still running: ${currentStep}`), 60000);

const log = message => console.log(message);
const secretsToHide = [];
const redact = text => secretsToHide.reduce((t, s) => t.replaceAll(s, '[REDACTED]'), text);

/** Runs a command, tees its output to a log file, resolves with the whole text. */
function run(command, argv, { cwd = repoRoot, env = {}, logName, timeoutMs = 45 * 60 * 1000, allowFail = false } = {}) {
  return new Promise((resolve, reject) => {
    const file = path.join(reportDir, `${logName}.log`);
    const stream = createWriteStream(file, { flags: 'a', mode: 0o600 });
    stream.write(`$ ${command} ${argv.join(' ')}\n`);
    const child = spawn(command, argv, { cwd, env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1',
      // The result lines below are parsed; a German or French SDK prints "Bestanden!"/"Fehler:".
      DOTNET_CLI_UI_LANGUAGE: 'en', VSLANG: '1033', PreferredUILang: 'en-US', ...env } });
    let out = '';
    const take = chunk => { const text = redact(chunk.toString()); out += text; stream.write(text); };
    child.stdout.on('data', take);
    child.stderr.on('data', take);
    const timer = setTimeout(() => { child.kill('SIGTERM'); setTimeout(() => child.kill('SIGKILL'), 5000); }, timeoutMs);
    child.on('error', error => { clearTimeout(timer); stream.end(); reject(error); });
    child.on('close', code => {
      clearTimeout(timer);
      stream.end();
      const result = { code, out, file };
      if (code !== 0 && !allowFail) {
        const error = new Error(`${command} ${argv.slice(0, 3).join(' ')} exited ${code}; see ${file}`);
        error.result = result;
        reject(error);
      } else resolve(result);
    });
  });
}
const capture = async (command, argv, cwd = repoRoot) => {
  try { return (await execFileAsync(command, argv, { cwd, maxBuffer: 64 * 1024 * 1024 })).stdout.trim(); }
  catch (error) { return { failed: true, message: error.message }; }
};

class Blocked extends Error {}

/** One gate step. Returns nothing; records PASS / FAIL / BLOCKED. */
async function step(id, name, body, { needs = [] } = {}) {
  currentStep = `${id} ${name}`;
  const started = Date.now();
  const unmet = needs.filter(need => results.find(r => r.id === need)?.status !== 'PASS');
  let entry;
  if (unmet.length) {
    entry = { id, name, status: 'BLOCKED', detail: `not run: ${unmet.join(', ')} did not pass` };
  } else {
    log(`\n== ${id} ${name}`);
    try {
      const detail = await body();
      entry = { id, name, status: 'PASS', detail: detail ?? '' };
    } catch (error) {
      entry = { id, name, status: error instanceof Blocked ? 'BLOCKED' : 'FAIL', detail: String(error.message ?? error).split('\n')[0] };
      if (error.result) {
        const tail = error.result.out.trim().split('\n').slice(-25).join('\n');
        log(`-- last output of the failing command (full log: ${error.result.file})\n${tail}`);
      } else if (!(error instanceof Blocked)) log(String(error.stack ?? error));
    }
  }
  entry.ms = Date.now() - started;
  results.push(entry);
  log(`${entry.status}: ${id} ${name} - ${entry.detail} (${(entry.ms / 1000).toFixed(0)} s)`);
}

const sum = (text, regex, group) => [...text.matchAll(regex)].reduce((total, match) => total + Number(match[group]), 0);
/** Totals over every "Passed!/Failed!  - Failed: n, Passed: n, Skipped: n, Total: n" line dotnet test prints. */
function dotnetCounts(text) {
  const line = /(?:Passed|Failed)!\s+-\s+Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+),\s+Total:\s+(\d+)/g;
  const assemblies = [...text.matchAll(line)].length;
  return { assemblies, failed: sum(text, line, 1), passed: sum(text, line, 2), skipped: sum(text, line, 3), total: sum(text, line, 4) };
}
function requireCleanCounts(label, counts, { min = 1 } = {}) {
  if (counts.assemblies < 1 || counts.total < min) throw new Error(`${label}: no test result lines found (${counts.total} tests)`);
  if (counts.failed > 0) throw new Error(`${label}: ${counts.failed} failed`);
  if (counts.skipped > 0) throw new Error(`${label}: ${counts.skipped} skipped - a skipped test is not a pass`);
  return `${counts.passed} passed in ${counts.assemblies} assemblies`;
}

async function freePort() {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => { const { port } = server.address(); server.close(() => resolve(port)); });
  });
}

/** Every `Noelia.*` entry in every project.assets.json below `root` must be exactly `expected`. */
async function assertAssetsVersion(root, expected, { extraFilter = () => true } = {}) {
  const files = [];
  const walk = async dir => {
    for (const entry of await readdir(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) await walk(full);
      else if (entry.name === 'project.assets.json' && extraFilter(full)) files.push(full);
    }
  };
  await walk(root);
  let libraries = 0;
  for (const file of files) {
    const assets = JSON.parse(await readFile(file, 'utf8'));
    // Only packages: a ProjectReference (Noelia.ControlPlane/1.0.0) is not a restored candidate.
    for (const key of Object.entries(assets.libraries ?? {}).filter(([name, lib]) => name.startsWith('Noelia.') && lib.type === 'package').map(([name]) => name)) {
      libraries += 1;
      if (key.split('/')[1] !== expected) throw new Error(`${file}: restored ${key}, not ${expected}`);
    }
  }
  if (libraries === 0) throw new Error(`no Noelia package was restored under ${root}; the candidate was not tested`);
  return `${libraries} Noelia package entries in ${files.length} project.assets.json, all ${expected}`;
}

// --- state for teardown ----------------------------------------------------------

let stackStarted = false;
let tornDown = false;
let teardownEntry;

async function teardown() {
  if (!stackStarted || tornDown) return;
  tornDown = true;
  const started = Date.now();
  const compose = ['compose', '-f', path.join(demoRoot, 'docker-compose.yml'), '-p', project];
  const envFile = path.join(secretDir, 'state', 'stack.env');
  try {
    // The project name is unique and ours; that is the only thing this ever addresses.
    if (existsSync(envFile))
      await run('docker', [...compose, '--env-file', envFile, '--profile', 'all', 'down', '--volumes', '--rmi', 'local', '--timeout', '10'],
        { cwd: demoRoot, logName: 'teardown', allowFail: true, timeoutMs: 5 * 60 * 1000 });
    // Whatever a failed start left behind, matched by OUR project label only.
    const label = `label=com.docker.compose.project=${project}`;
    const leftovers = {};
    for (const [kind, listArgs, removeArgs] of [
      ['containers', ['ps', '-aq', '--filter', label], ['rm', '-f', '-v']],
      ['networks', ['network', 'ls', '-q', '--filter', label], ['network', 'rm']],
      ['volumes', ['volume', 'ls', '-q', '--filter', label], ['volume', 'rm', '-f']],
    ]) {
      let ids = (await capture('docker', listArgs));
      ids = typeof ids === 'string' && ids ? ids.split('\n') : [];
      if (ids.length) { await run('docker', [...removeArgs, ...ids], { logName: 'teardown', allowFail: true }); }
      const after = await capture('docker', listArgs);
      leftovers[kind] = typeof after === 'string' && after ? after.split('\n').length : 0;
      if (typeof after !== 'string') leftovers[kind] = 'unknown';
    }
    const left = Object.entries(leftovers).filter(([, n]) => n !== 0);
    teardownEntry = left.length
      ? { status: 'FAIL', detail: `leftovers of ${project}: ${left.map(([k, n]) => `${k}=${n}`).join(', ')}` }
      : { status: 'PASS', detail: `down -v of ${project}; no container, network or volume left` };
  } catch (error) {
    teardownEntry = { status: 'FAIL', detail: `teardown error: ${error.message}` };
  }
  teardownEntry = { id: 'S9', name: 'Teardown of this gate\'s own project', ms: Date.now() - started, ...teardownEntry };
  log(`${teardownEntry.status}: S9 ${teardownEntry.name} - ${teardownEntry.detail}`);
}

let interrupted = false;
for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, async () => {
    if (interrupted) return;
    interrupted = true;
    log(`\n${signal}: tearing down ${project} before exit`);
    await teardown();
    process.exit(130);
  });
}

// --- the gate --------------------------------------------------------------------

log(`Noelia release gate\n  candidate version : ${version}\n  compose project   : ${project}\n  new local feed    : ${feedDir}\n  report directory  : ${reportDir}`);

const gitInfo = async cwd => {
  const sha = await capture('git', ['rev-parse', 'HEAD'], cwd);
  const branch = await capture('git', ['rev-parse', '--abbrev-ref', 'HEAD'], cwd);
  const status = await capture('git', ['status', '--porcelain'], cwd);
  return { sha: typeof sha === 'string' ? sha : 'unknown', branch: typeof branch === 'string' ? branch : 'unknown',
    dirty: typeof status === 'string' ? status.length > 0 : 'unknown', changedPaths: typeof status === 'string' && status ? status.split('\n').length : 0 };
};
meta.noelia = await gitInfo(repoRoot);
meta.controlPlane = existsSync(options.cpRepo) ? await gitInfo(options.cpRepo) : { sha: 'missing' };
log(`  Noelia git        : ${meta.noelia.sha.slice(0, 12)} (${meta.noelia.branch}) ${meta.noelia.dirty ? `DIRTY, ${meta.noelia.changedPaths} changed path(s)` : 'clean'}`);
log(`  ControlPlane git  : ${meta.controlPlane.sha.slice(0, 12)} ${meta.controlPlane.dirty ? 'DIRTY' : 'clean'}`);

const playwrightModule = path.join(frontend, 'node_modules/playwright/index.mjs');
const cpProject = path.join(options.cpRepo, 'tests/Noelia.ControlPlane.Tests');
const cpSmokeModule = path.join(options.cpRepo, 'eng/demo-live-smoke.mjs');
let cpDll;
let stackState;

try {
  await step('S0', 'Prerequisites', async () => {
    const missing = [];
    const seen = [];
    const probe = async (label, command, argv, ok = () => true) => {
      const out = await capture(command, argv);
      if (typeof out !== 'string' || !ok(out)) missing.push(label); else seen.push(`${label} ${out.split('\n')[0].slice(0, 40)}`);
    };
    await probe('docker daemon', 'docker', ['info', '--format', '{{.ServerVersion}}']);
    await probe('docker compose', 'docker', ['compose', 'version', '--short']);
    await probe('dotnet 10 SDK', 'dotnet', ['--version'], out => /^10\./.test(out));
    await probe('node >= 20', 'node', ['--version'], out => Number(out.replace('v', '').split('.')[0]) >= 20);
    await probe('npm', 'npm', ['--version']);
    await probe('python3', 'python3', ['--version']);
    await probe('git', 'git', ['--version']);
    await probe('jq (test-package-version.sh)', 'jq', ['--version']);
    await probe('rg (test-package-version.sh)', 'rg', ['--version']);
    await probe('rsync (test-package-version.sh)', 'rsync', ['--version']);
    if (!existsSync(options.cpRepo) || !existsSync(cpProject) || !existsSync(cpSmokeModule))
      missing.push(`NoeliaControlPlane repository at ${options.cpRepo} (tests/Noelia.ControlPlane.Tests, eng/demo-live-smoke.mjs)`);
    if (options.requireClean && (meta.noelia.dirty || meta.controlPlane.dirty)) missing.push('a clean working tree (--require-clean)');
    if (missing.length) throw new Error(`missing prerequisite(s): ${missing.join('; ')}`);

    // A clean checkout has no node_modules; the frontend tests and the browser both need it.
    if (!existsSync(path.join(frontend, 'node_modules/playwright')))
      await run('npm', ['--prefix', frontend, 'ci'], { logName: 'S0-npm-ci', timeoutMs: 10 * 60 * 1000 });
    // The browser has to actually launch: an installed package without its browser is not a prerequisite met.
    try {
      const { chromium } = await import(pathToFileURL(playwrightModule).href);
      const browser = await chromium.launch({ headless: true });
      await browser.close();
    } catch (error) {
      throw new Error(`Playwright Chromium cannot launch (run: npx --prefix demo/src/frontend playwright install chromium): ${error.message.split('\n')[0]}`);
    }
    return seen.join('; ') + '; chromium launches';
  });
  const prereq = ['S0'];

  await step('S1', 'Candidate pack', async () => {
    await run('dotnet', ['pack', 'Noelia.slnx', '-c', 'Release', `-p:Version=${version}`, '-o', feedDir], { logName: 'S1-pack' });
    const expected = readdirSync(path.join(repoRoot, 'src')).filter(name => existsSync(path.join(repoRoot, 'src', name, `${name}.csproj`))).length;
    const packages = readdirSync(feedDir).filter(name => name.endsWith('.nupkg'));
    const wrong = packages.filter(name => !name.endsWith(`.${version}.nupkg`));
    if (packages.length !== expected || wrong.length) throw new Error(`expected ${expected} packages of ${version}, feed has ${packages.length} (unexpected: ${wrong.join(', ') || 'none'})`);
    return `${packages.length} packages of ${version} in ${path.relative(repoRoot, feedDir)}`;
  }, { needs: prereq });

  await step('S2', 'Library tests (dotnet test Noelia.slnx -c Release)', async () => {
    const { out } = await run('dotnet', ['test', 'Noelia.slnx', '-c', 'Release'], { logName: 'S2-library-tests' });
    return requireCleanCounts('library', dotnetCounts(out), { min: 100 });
  }, { needs: prereq });

  await step('S3', 'Demo .NET tests against the candidate (isolated restore)', async () => {
    const { out } = await run('bash', [path.join(demoRoot, 'eng/test-package-version.sh'), version, feedDir], { logName: 'S3-demo-tests', cwd: demoRoot });
    if (!out.includes(`Noelia ${version} passed the Demo package gate.`)) throw new Error('the package gate script did not report success');
    const counts = dotnetCounts(out);
    return `${requireCleanCounts('demo', counts)}; restored Noelia.* packages verified == ${version} in every project.assets.json`;
  }, { needs: ['S1'] });

  await step('S4', 'Control Plane tests against the candidate (isolated restore)', async () => {
    if (!existsSync(options.cpRepo)) throw new Error(`Control Plane repository not found: ${options.cpRepo}`);
    const { out } = await run('dotnet', ['test', 'tests/Noelia.ControlPlane.Tests', '-c', 'Release',
      `-p:NoeliaPackageVersion=${version}`, `-p:RestorePackagesPath=${cpCache}`,
      `-p:RestoreSources=${feedDir}%3Bhttps://api.nuget.org/v3/index.json`, '--artifacts-path', cpArtifacts],
    { cwd: options.cpRepo, logName: 'S4-cp-tests' });
    const summary = requireCleanCounts('control plane', dotnetCounts(out));
    const assets = await assertAssetsVersion(cpArtifacts, version);
    cpDll = path.join(cpArtifacts, 'bin/Noelia.ControlPlane/release/Noelia.ControlPlane.dll');
    if (!existsSync(cpDll)) throw new Error(`candidate-built Control Plane not found at ${cpDll}`);
    return `${summary}; ${assets}`;
  }, { needs: ['S1'] });

  await step('S10', 'Frontend unit tests (npm test)', async () => {
    const { out } = await run('npm', ['--prefix', frontend, 'test'], { logName: 'S10-frontend-unit' });
    const pass = Number(out.match(/^(?:#|ℹ) pass (\d+)/m)?.[1] ?? NaN);
    const fail = Number(out.match(/^(?:#|ℹ) fail (\d+)/m)?.[1] ?? NaN);
    const skipped = Number(out.match(/^(?:#|ℹ) skipped (\d+)/m)?.[1] ?? NaN);
    if (!(pass > 0) || fail !== 0 || skipped !== 0) throw new Error(`frontend: pass=${pass} fail=${fail} skipped=${skipped}`);
    return `${pass} passed`;
  }, { needs: prereq });

  // Steps 5 and 8 share one process: compose-report-smoke builds and starts the stack,
  // proves every host's .deps.json carries the candidate, compares the live reports with
  // the reviewed baselines and then hands the running stack to the CP smoke module.
  // In caller-owned mode it leaves the stack up (this script tears it down), so a failure
  // in the CP part cannot take the security and browser steps away.
  let smoke;
  const httpPort = await freePort();
  let httpsPort = await freePort();
  while (httpsPort === httpPort) httpsPort = await freePort();
  meta.ports = { http: httpPort, https: httpsPort };
  await step('S5', 'Compose stack from the candidate; package versions in every host', async () => {
    stackStarted = true;                       // from here teardown addresses this project
    const stateDir = path.join(secretDir, 'state');
    smoke = await run('node', [path.join(demoRoot, 'eng/compose-report-smoke.mjs'), version, `/src/.local-feed/${feedName}`], {
      cwd: demoRoot, logName: 'S5-compose-smoke', allowFail: true,
      env: {
        NOELIA_SMOKE_PROJECT: project, NOELIA_SMOKE_DIR: path.join(reportDir, 'compose-smoke'),
        NOELIA_SMOKE_HTTP_PORT: String(httpPort), NOELIA_SMOKE_HTTPS_PORT: String(httpsPort),
        NOELIA_SMOKE_KEEP_STATE: stateDir,
        // Without the candidate-built CP (S4 failed) the stack is still proven; S8 then reports BLOCKED.
        ...(cpDll ? { NOELIA_CP_SMOKE_MODULE: cpSmokeModule, NOELIA_CP_DLL: cpDll, PLAYWRIGHT_MODULE: playwrightModule } : {}),
      },
    });
    const stateFile = path.join(stateDir, 'stack.json');
    if (existsSync(stateFile)) stackState = JSON.parse(await readFile(stateFile, 'utf8'));
    const hostPasses = [...smoke.out.matchAll(/^PASS: (gateway|user|todo|mono)-(dev|staging|prod), .*exact package/gm)].length;
    if (smoke.code !== 0 && hostPasses < 12) throw Object.assign(new Error(`compose smoke exited ${smoke.code} after ${hostPasses}/12 host checks; see ${smoke.file}`), { result: smoke });
    if (hostPasses !== 12 || !smoke.out.includes('PASS: all 12 host reports')) throw new Error(`only ${hostPasses} of 12 hosts verified`);
    if (!cpDll) throw new Error('12/12 hosts verified, but the CP live smoke could not run (S4 did not produce a candidate-built Control Plane)');
    return `12/12 hosts carry exactly ${version} (.deps.json), reviewed baselines match, report access policy holds; ports http ${httpPort} https ${httpsPort}`;
  }, { needs: ['S1'] });

  await step('S6', 'Security gate (eng/security-checks.py, --fail-on-warning) and its unit tests', async () => {
    if (!stackState) throw new Blocked('no running stack (S5 did not bring it up)');
    const gate = await run('python3', ['eng/security-checks.py', '-p', project, '--env-file', path.join(secretDir, 'state/stack.env'),
      '--profile', 'all', '--fail-on-warning'], { cwd: demoRoot, logName: 'S6-security-checks', allowFail: true });
    if (gate.code !== 0) throw Object.assign(new Error(`security-checks.py exited ${gate.code}; see ${gate.file}`), { result: gate });
    const summaryLine = gate.out.trim().split('\n').at(-1);
    const units = await run('python3', ['-m', 'unittest', 'discover', '-s', 'eng', '-p', 'test_*.py'], { cwd: demoRoot, logName: 'S6-security-unit' });
    const ran = Number(units.out.match(/Ran (\d+) tests?/)?.[1] ?? 0);
    if (!ran || !/^OK\b/m.test(units.out)) throw new Error('security-gate unit tests did not report OK');
    return `${summaryLine} ${ran} unit tests OK`;
  });

  await step('S7', 'Browser: demo e2e in six combinations (+ stage hint, logout gating)', async () => {
    if (!stackState) throw new Blocked('no running stack (S5 did not bring it up)');
    const combos = [];
    for (const stage of ['dev', 'staging', 'prod'])
      for (const kind of ['micro', 'mono'])
        combos.push({ name: `${kind}-${stage}`, url: stage === 'dev' ? `http://${kind}-${stage}.localhost:${httpPort}` : `https://${kind}-${stage}.localhost:${httpsPort}` });
    let total = 0;
    const failures = [];
    const required = ['the sign-out button is disabled until the session is ready', 'a failed sign-out says so instead of pretending', 'the stage hint on /login', 'the stage hint on the task page'];
    for (const combo of combos) {
      const jsonFile = path.join(reportDir, `e2e-${combo.name}.json`);
      const result = await run(path.join(frontend, 'node_modules/.bin/playwright'), ['test', '--reporter=json'], {
        cwd: frontend, logName: `S7-e2e-${combo.name}`, allowFail: true, timeoutMs: 15 * 60 * 1000,
        env: { DEMO_BASE_URL: combo.url, PLAYWRIGHT_JSON_OUTPUT_NAME: jsonFile, CI: '1' },
      });
      let report;
      try { report = JSON.parse(await readFile(jsonFile, 'utf8')); } catch { failures.push(`${combo.name}: no JSON report (exit ${result.code})`); continue; }
      const titles = [];
      const collect = suite => { for (const spec of suite.specs ?? []) titles.push({ title: spec.title, ok: spec.tests.every(t => t.status === 'expected') }); (suite.suites ?? []).forEach(collect); };
      (report.suites ?? []).forEach(collect);
      const { expected, unexpected, skipped, flaky } = report.stats;
      const missing = required.filter(part => !titles.some(t => t.title.includes(part) && t.ok));
      if (result.code !== 0 || unexpected || skipped || flaky || !expected || missing.length)
        failures.push(`${combo.name}: passed ${expected}, failed ${unexpected}, skipped ${skipped}, flaky ${flaky}${missing.length ? `, required test not passed: ${missing.join(' | ')}` : ''}`);
      total += expected;
      log(`  e2e ${combo.name.padEnd(13)} passed ${expected}, failed ${unexpected}, skipped ${skipped}, flaky ${flaky}`);
    }
    if (failures.length) throw new Error(failures.join('; '));
    return `6/6 combinations, ${total} browser tests passed (0 failed/skipped/flaky); stage hint and logout gating included`;
  });

  await step('S8', 'Control Plane live smoke against the running demo', async () => {
    if (!smoke) throw new Blocked('the compose smoke did not run');
    if (!cpDll) throw new Blocked('no candidate-built Control Plane (S4 did not pass)');
    const passes = [...smoke.out.matchAll(/^PASS: CP live (\S+),/gm)].map(m => m[1]);
    if (smoke.code !== 0 || passes.length !== 6) throw Object.assign(new Error(`CP live smoke: ${passes.length}/6 fleets passed (smoke exit ${smoke.code}); see ${smoke.file}`), { result: smoke });
    return `6/6 fleets (${passes.join(', ')}): Complete collection, Match roles, explicit v3, Chromium pages`;
  });
} finally {
  clearInterval(heartbeat);
  await teardown();
  await rm(secretDir, { recursive: true, force: true });
}

// --- report ----------------------------------------------------------------------------

if (teardownEntry) results.push(teardownEntry);
else results.push({ id: 'S9', name: 'Teardown of this gate\'s own project', status: 'PASS', detail: 'no stack was started; nothing to remove', ms: 0 });
// The stack lives at most until here. S9 is only a pass if it removed everything.
const order = id => Number(id.slice(1));
results.sort((a, b) => order(a.id) - order(b.id));
meta.finished = new Date().toISOString();
const failed = results.filter(r => r.status !== 'PASS');
meta.result = failed.length ? 'FAIL' : 'PASS';

const lines = [
  `Noelia release gate - ${meta.result}`,
  `Candidate version : ${version}`,
  `Noelia            : ${meta.noelia.sha} (${meta.noelia.branch}) ${meta.noelia.dirty ? `WORKING TREE DIRTY (${meta.noelia.changedPaths} changed paths): the result applies to the tree, not to the commit` : 'clean'}`,
  `Control Plane     : ${meta.controlPlane.sha} ${meta.controlPlane.dirty ? 'WORKING TREE DIRTY' : 'clean'}`,
  `Feed              : ${feedDir}`,
  `Started/finished  : ${meta.started} / ${meta.finished}`,
  '',
  ...results.map(r => `${r.status.padEnd(7)} ${r.id.padEnd(4)} ${r.name} [${(r.ms / 1000).toFixed(0)} s]\n          ${r.detail}`),
  '',
  failed.length ? `${failed.length} step(s) not passed: ${failed.map(r => r.id).join(', ')}. The candidate is NOT proven.` : 'All steps passed for exactly this candidate.',
  'Not proven: production hardening, real TLS trust, restart durability, audit completeness, load, and anything outside these steps (see demo/README.md).',
  `Logs and reports: ${reportDir}`,
];
const text = lines.join('\n');
await writeFile(path.join(reportDir, 'summary.txt'), text + '\n', { mode: 0o600 });
await writeFile(path.join(reportDir, 'summary.json'), JSON.stringify({ meta, results }, null, 2) + '\n', { mode: 0o600 });
console.log(`\n${'='.repeat(78)}\n${text}`);
process.exit(failed.length ? 1 : 0);
