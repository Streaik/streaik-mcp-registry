import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test, { after, before } from 'node:test';

const sample = new URL('../samples/Streaik.Mcp.Registry.Sample/', import.meta.url);
const setting = 'McpRegistry__DefaultServersBaseUrl';
const entraSetting = 'McpRegistry__EntraServersBaseUrl';
const catalog = JSON.parse(await readFile(new URL('../../streaik-mcp-catalog/catalog.json', import.meta.url), 'utf8'));
let outputDirectory;
let listenArgs;

before(async () => {
  // Avoid overwriting DLLs held open by a running sample host on Windows.
  outputDirectory = await mkdtemp(join(tmpdir(), 'streaik-mcp-registry-'));
  listenArgs = [join(outputDirectory, 'Streaik.Mcp.Registry.Sample.dll'), '--urls', 'http://127.0.0.1:0'];
  const build = spawnSync('dotnet', ['build', '--output', outputDirectory], { cwd: fileURLToPath(sample), encoding: 'utf8', timeout: 120000 });
  assert.ifError(build.error);
  assert.equal(build.status, 0, build.stdout + build.stderr);
});

after(async () => { if (outputDirectory) await rm(outputDirectory, { recursive: true, force: true }); });

async function startRegistry(t, baseUrl = '', entraUrl = '') {
  const child = spawn('dotnet', listenArgs, {
    cwd: sample,
    env: { ...process.env, [setting]: baseUrl, [entraSetting]: entraUrl, Logging__LogLevel__Microsoft__Hosting__Lifetime: 'Information' },
  });
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

for (const byok of ['', 'https://mcp.example.test/byok']) {
  test(`publishes OAuth remotes without API-key headers ${byok ? 'alongside BYOK' : 'on their own'}`, async t => {
    const entra = 'https://mcp.example.test/team/entra';
    const origin = await startRegistry(t, byok, entra + '/');
    const response = await fetch(`${origin}/v0.1/servers`);
    assert.equal(response.status, 200);
    const listing = await response.json();
    assert.equal(listing.metadata.count, 3);
    for (const { server } of listing.servers) {
      const original = catalog.servers.find(item => item.name === server.name).remotes[0];
      const route = new URL(original.url).pathname;
      assert.deepEqual(server.remotes, [
        ...(byok ? [{ ...original, url: byok + route }] : []),
        { type: 'streamable-http', url: entra + route },
      ]);
    }
  });
}

for (const configured of ['http://localhost:5087', 'https://mcp.example.test:8443/team/tools/']) {
  test(`serves AIHappey's list envelope with catalog remotes rebased to ${configured}`, async t => {
    const origin = await startRegistry(t, configured);
    const response = await fetch(`${origin}/v0.1/servers`, { headers: { 'X-Forwarded-Host': 'different.example.test' } });
    assert.equal(response.status, 200);
    assert.match(response.headers.get('content-type'), /^application\/json\b/);
    const servers = ['Anthropic-Skills', 'Google-Images', 'OpenAI-Images'].map(name => {
      const server = structuredClone(catalog.servers.find(s => s.name === `io.github.streaik/${name}`));
      for (const remote of server.remotes) remote.url = configured.replace(/\/$/, '') + new URL(remote.url).pathname;
      return { server, _meta: null };
    });
    assert.deepEqual(await response.json(), { servers, metadata: { count: 3 } });
  });
}

test('serves an empty list when disabled and exposes no alternative discovery routes', async t => {
  const origin = await startRegistry(t);
  const response = await fetch(`${origin}/v0.1/servers`);
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { servers: [], metadata: { count: 0 } });
  for (const path of ['/v1/servers', '/catalog.json', '/openai-images']) {
    assert.equal((await fetch(`${origin}${path}`)).status, 404);
  }
  assert.equal((await fetch(`${origin}/v0.1/servers`, { method: 'POST' })).status, 405);
});

test('rejects invalid base URLs before listening', () => {
  for (const baseUrl of ['mcp.example.test', 'ftp://mcp.example.test', 'http://mcp.example.test',
    'https://user:pass@mcp.example.test', 'https://mcp.example.test/?query=1', 'https://mcp.example.test/#fragment']) {
    const result = spawnSync('dotnet', listenArgs, {
      cwd: sample, env: { ...process.env, [setting]: baseUrl, [entraSetting]: '' }, encoding: 'utf8', timeout: 5000,
    });
    assert.ifError(result.error);
    assert.ok(result.status !== null && result.status !== 0, baseUrl);
    assert.match(result.stderr, /McpRegistry:DefaultServersBaseUrl must be an absolute HTTPS URL/);
  }
});
