import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, readdir, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test, { after, before } from 'node:test';

const samples = Object.fromEntries(['NoAuth', 'EntraAuth'].map(kind => {
  const name = `Streaik.Mcp.Registry.${kind}`;
  return [kind, { name, cwd: new URL(`../samples/${name}/`, import.meta.url) }];
}));
const setting = 'McpRegistry__DefaultServersBaseUrl';
const entraSetting = 'McpRegistry__EntraServersBaseUrl';
const protectedRemotes = 'net.streaik/entra-protected-remotes';
const tenant = 'ffffffff-ffff-4fff-8fff-ffffffffffff';
const client = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const catalog = new URL('../../streaik-mcp-catalog/servers/', import.meta.url);
const entries = await Promise.all((await readdir(catalog)).map(async file => JSON.parse(await readFile(new URL(file, catalog), 'utf8'))));
let outputDirectory;

before(async () => {
  // Avoid overwriting DLLs held open by a running sample host on Windows.
  outputDirectory = await mkdtemp(join(tmpdir(), 'streaik-mcp-registry-'));
  for (const sample of Object.values(samples)) {
    const output = join(outputDirectory, sample.name);
    sample.listenArgs = [join(output, `${sample.name}.dll`), '--urls', 'http://127.0.0.1:0'];
    const build = spawnSync('dotnet', ['build', '-c', 'Release', '--output', output], {
      cwd: fileURLToPath(sample.cwd), encoding: 'utf8', timeout: 120000,
    });
    assert.ifError(build.error);
    assert.equal(build.status, 0, build.stdout + build.stderr);
  }
});

after(async () => { if (outputDirectory) await rm(outputDirectory, { recursive: true, force: true }); });

function registryEnvironment(baseUrl = '', entraUrl = '', tenantId = tenant, clientId = client) {
  return { ...process.env, DOTNET_ENVIRONMENT: 'Testing', ASPNETCORE_ENVIRONMENT: 'Testing',
    [setting]: baseUrl, [entraSetting]: entraUrl, AzureAd__TenantId: tenantId, AzureAd__ClientId: clientId,
    Logging__LogLevel__Microsoft__Hosting__Lifetime: 'Information' };
}

async function startRegistry(t, kind, baseUrl, entraUrl) {
  const { cwd, listenArgs } = samples[kind];
  const env = kind === 'EntraAuth' ? registryEnvironment(baseUrl, entraUrl) : registryEnvironment(baseUrl, entraUrl, '', '');
  const child = spawn('dotnet', listenArgs, { cwd, env });
  const closed = once(child, 'close');
  t.after(async () => { child.kill(); await closed; });

  return new Promise((resolve, reject) => {
    let output = '';
    const timer = setTimeout(() => reject(new Error(`Registry did not start:\n${output}`)), 15000);
    const onData = chunk => {
      output += chunk;
      const match = output.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)\s/);
      if (match) { clearTimeout(timer); resolve(match[1]); }
    };
    child.stdout.on('data', onData);
    child.stderr.on('data', onData);
    closed.then(([code]) => { clearTimeout(timer); reject(new Error(`Registry exited (${code}):\n${output}`)); }, reject);
  });
}

function expectedDocuments(prefix, byok, entra) {
  return entries.filter(({ obo }) => entra || byok && !obo).map(({ server, obo }) => {
    const [original] = server.remotes;
    const route = new URL(original.url).pathname;
    const name = `${prefix}/${server.name.split('/')[1]}`;
    const remotes = byok && !obo ? [{ ...original, url: byok + route }] : [];
    const expected = { ...server, name, remotes };
    if (entra) {
      const { headers, ...remote } = original;
      const url = entra + route;
      remotes.push({ ...remote, url });
      if (obo || headers?.length) expected._meta = { [protectedRemotes]: [url] };
    }
    return { server: expected, _meta: null };
  });
}

async function assertListing(origin, expected) {
  const response = await fetch(`${origin}/v0.1/servers`, { headers: {
    Origin: 'https://web.example.test', Forwarded: 'host=registry.example.test', 'X-Forwarded-Host': 'other.example.test',
  } });
  assert.equal(response.status, 200);
  assert.equal(response.headers.get('access-control-allow-origin'), '*');
  assert.match(response.headers.get('content-type'), /^application\/json\b/);
  const listing = await response.json();
  const byName = (a, b) => a.server.name.localeCompare(b.server.name);
  assert.deepEqual(listing, { servers: listing.servers, metadata: { count: expected.length } });
  assert.deepEqual(listing.servers.toSorted(byName), expected.toSorted(byName));
  for (const { server } of listing.servers) {
    assert.match(server.name, /^[a-zA-Z0-9.-]+\/[a-zA-Z0-9._-]+$/);
    assert.ok(server.name.length <= 200);
  }
}

for (const kind of Object.keys(samples)) {
  test(`${kind} serves the same public discovery contract with both runtimes configured`, async t => {
    const byok = 'https://mcp.example.test/team/byok';
    const entra = 'https://entra.example.test:8443/team/entra';
    const origin = await startRegistry(t, kind, byok + '/', entra + '/');
    await assertListing(origin, [
      ...expectedDocuments('test.example.mcp', byok),
      ...expectedDocuments('test.example.entra-8443', '', entra),
    ]);
    for (const path of ['/v1/servers', '/v0.1/entra/servers', '/v0.1/servers/mcp', '/catalog.json', '/openai-images']) {
      assert.equal((await fetch(`${origin}${path}`)).status, 404, path);
    }
    for (const method of ['POST', 'PUT', 'PATCH', 'DELETE']) {
      assert.equal((await fetch(`${origin}/v0.1/servers`, { method })).status, 405, method);
    }
  });

  test(`${kind} serves an empty list with no runtimes configured`, async t => {
    const origin = await startRegistry(t, kind);
    await assertListing(origin, []);
  });
}

test('groups transports on the same authority but keeps path prefixes', async t => {
  const byok = 'https://mcp.example.test/byok';
  const entra = 'https://mcp.example.test/team/entra';
  const origin = await startRegistry(t, 'NoAuth', byok, entra);
  await assertListing(origin, expectedDocuments('test.example.mcp', byok, entra));
});

test('can list only the Entra runtime, independently of registry authentication', async t => {
  const entra = 'https://mcp.example.test/team/entra';
  const origin = await startRegistry(t, 'NoAuth', '', entra);
  await assertListing(origin, expectedDocuments('test.example.mcp', '', entra));
});

for (const [configured, prefix] of [
  ['http://localhost:5087', 'localhost-5087'],
  ['http://127.0.0.1:5087', '127.0.0.1-5087'],
  ['http://[::1]:5087', 'ipv6-00000000000000000000000000000001-5087'],
  ['https://mcp.streaik.net:443/team/tools', 'net.streaik.mcp'],
]) {
  test(`uses a schema-compatible name for ${configured}`, async t => {
    const origin = await startRegistry(t, 'NoAuth', configured + '/');
    await assertListing(origin, expectedDocuments(prefix, configured.replace(':443', '')));
  });
}

function assertStartupFailure(kind, env, message) {
  const { cwd, listenArgs } = samples[kind];
  const result = spawnSync('dotnet', listenArgs, {
    cwd, env, encoding: 'utf8', timeout: 10000,
  });
  assert.ifError(result.error);
  assert.notEqual(result.status, 0, result.stdout);
  assert.match(result.stderr, message);
}

test('rejects invalid configured runtime URLs before listening', () => {
  for (const baseUrl of ['mcp.example.test', 'ftp://mcp.example.test', 'http://mcp.example.test',
    'https://user:pass@mcp.example.test', 'https://mcp.example.test/?query=1', 'https://mcp.example.test/#fragment']) {
    assertStartupFailure('NoAuth', registryEnvironment(baseUrl), /McpRegistry:DefaultServersBaseUrl must be an absolute HTTPS URL/);
  }
  assertStartupFailure('EntraAuth', registryEnvironment('', 'http://mcp.example.test'), /McpRegistry:EntraServersBaseUrl must be an absolute HTTPS URL/);
});

test('rejects duplicate transport URLs and names that collide across authorities', () => {
  assertStartupFailure('NoAuth', registryEnvironment('https://mcp.example.test/mcp', 'https://mcp.example.test/mcp/'), /must not publish the same transport URLs/);
  assertStartupFailure('NoAuth', registryEnvironment('https://mcp.example.test:8443', 'https://mcp-8443.example.test'), /must not map different authorities/);
});

test('requires tenant and API application IDs only in the EntraAuth sample', () => {
  for (const [tenantId, clientId] of [['', client], [tenant, 'not-a-guid'], [tenant, '00000000-0000-0000-0000-000000000000']]) {
    assertStartupFailure('EntraAuth', registryEnvironment('', '', tenantId, clientId), /Set AzureAd:TenantId and AzureAd:ClientId/);
  }
});
