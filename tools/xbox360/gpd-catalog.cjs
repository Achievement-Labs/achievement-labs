#!/usr/bin/env node
// Maintains the public Xbox 360 GPD catalog, published as flat assets (index.json, TITLEID.gpd) of the
// gpd-catalog prerelease on Achievement-Labs/achievement-labs. Needs the GitHub CLI for pull/publish.
//   node tools/xbox360/gpd-catalog.cjs pull <catalog-dir>
//   node tools/xbox360/gpd-catalog.cjs add <catalog-dir> <TITLEID.gpd ...> [--title TITLEID]
//   node tools/xbox360/gpd-catalog.cjs verify <catalog-dir>
//   node tools/xbox360/gpd-catalog.cjs publish <catalog-dir>
// Every published GPD is the locked, stripped form from profile-core.js (Gpd.template), so the
// apps, the desktop C# port and this tool all produce identical bytes. Never touches Events/Data.json.
'use strict';
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto'), { execFileSync } = require('node:child_process');
const core = require('./shared/profile-core.js');
const TITLE = /^[0-9A-F]{8}$/, LIMIT = 8 * 1024 * 1024, REPO = 'Achievement-Labs/achievement-labs', TAG = 'gpd-catalog';
const sha256 = b => crypto.createHash('sha256').update(b).digest('hex');
const fail = message => { throw new Error(message); };

function readIndex(dir) {
  const file = path.join(dir, 'index.json');
  if (!fs.existsSync(file)) return { version: 1, generated: null, titles: {} };
  const index = JSON.parse(fs.readFileSync(file, 'utf8'));
  if (index.version !== 1 || typeof index.titles !== 'object') fail('Unsupported index.json.');
  return index;
}

function writeIndex(dir, index) {
  const titles = Object.fromEntries(Object.keys(index.titles).sort().map(id => [id, index.titles[id]]));
  const text = JSON.stringify({ version: 1, generated: new Date().toISOString(), titles }, null, 2) + '\n';
  const file = path.join(dir, 'index.json'), staged = file + '.partial';
  fs.writeFileSync(staged, text); fs.renameSync(staged, file);
}

function entry(bytes) {
  const info = core.Gpd.inspect(bytes);
  return { name: info.name, achievements: info.achievements, gamerscore: info.gamerscore, bytes: bytes.length, sha256: sha256(bytes) };
}

function add(dir, files, forcedTitle) {
  if (!files.length) fail('Give at least one GPD file.');
  if (forcedTitle && files.length !== 1) fail('--title applies to a single GPD.');
  const index = readIndex(dir), out = dir;
  fs.mkdirSync(out, { recursive: true });
  for (const file of files) {
    const id = (forcedTitle || path.basename(file, path.extname(file))).toUpperCase();
    if (!TITLE.test(id) || id === 'FFFE07D1') fail(`${file}: name it TITLEID.gpd or pass --title TITLEID.`);
    const input = fs.readFileSync(file);
    if (input.length > LIMIT) fail(`${file}: GPD exceeds 8 MB.`);
    const locked = core.Gpd.template(new Uint8Array(input));
    fs.writeFileSync(path.join(out, id + '.gpd'), locked);
    index.titles[id] = entry(locked);
    const t = index.titles[id];
    console.log(`${id}  ${t.name || '(no title string)'}  ${t.achievements} achievements  ${t.gamerscore} G${core.Gpd.inspect(input).unlocked ? '  (reset to locked)' : ''}`);
  }
  writeIndex(dir, index);
  verify(dir);
}

function verify(dir) {
  const index = readIndex(dir), gpdDir = dir;
  const files = fs.existsSync(gpdDir) ? fs.readdirSync(gpdDir).filter(f => f.toLowerCase().endsWith('.gpd')) : [];
  for (const file of files) if (!index.titles[path.basename(file, '.gpd')]) fail(`${file} is not listed in index.json.`);
  for (const [id, t] of Object.entries(index.titles)) {
    if (!TITLE.test(id) || id === 'FFFE07D1') fail(`Invalid title ID ${id}.`);
    const file = path.join(gpdDir, id + '.gpd');
    if (!fs.existsSync(file)) fail(`${id}.gpd is missing.`);
    const bytes = new Uint8Array(fs.readFileSync(file));
    if (bytes.length > LIMIT || bytes.length !== t.bytes || sha256(bytes) !== t.sha256) fail(`${id}.gpd does not match its index size/SHA-256.`);
    if (!Buffer.from(core.Gpd.template(bytes)).equals(Buffer.from(bytes))) fail(`${id}.gpd is not in locked catalog form.`);
    const expected = entry(bytes);
    for (const key of ['name', 'achievements', 'gamerscore']) if (t[key] !== expected[key]) fail(`${id} index ${key} is stale.`);
  }
  console.log(`PASS: ${Object.keys(index.titles).length} catalog GPDs locked, stripped and matching index.json.`);
}

const gh = (...args) => execFileSync('gh', [...args, '--repo', REPO], { stdio: 'inherit' });
const exists = () => { try { execFileSync('gh', ['release', 'view', TAG, '--repo', REPO], { stdio: 'ignore' }); return true; } catch { return false; } };

function pull(dir) {
  fs.mkdirSync(dir, { recursive: true });
  if (exists()) gh('release', 'download', TAG, '--dir', dir, '--clobber');
  verify(dir);
}

// GPDs go up before index.json, so the published index never names a missing asset.
function publish(dir) {
  verify(dir);
  const index = readIndex(dir);
  if (!exists()) gh('release', 'create', TAG, '--prerelease', '--latest=false', '--title', 'Xbox 360 GPD catalog',
    '--notes', 'Locked Xbox 360 GPDs for the add-game catalog. Not an app release; updaters ignore prereleases.');
  const gpds = Object.keys(index.titles).map(id => path.join(dir, id + '.gpd'));
  for (let i = 0; i < gpds.length; i += 50) gh('release', 'upload', TAG, ...gpds.slice(i, i + 50), '--clobber');
  gh('release', 'upload', TAG, path.join(dir, 'index.json'), '--clobber');
  console.log(`Published ${gpds.length} GPDs and index.json to the ${TAG} release.`);
}

try {
  const [command, dir, ...rest] = process.argv.slice(2);
  if (!dir) fail('Usage: gpd-catalog.cjs pull|add|verify|publish <catalog-dir> [files...] [--title TITLEID]');
  const at = rest.indexOf('--title'), title = at >= 0 ? rest.splice(at, 2)[1] : null;
  if (command === 'add') add(dir, rest, title);
  else if (command === 'verify') verify(dir);
  else if (command === 'pull') pull(dir);
  else if (command === 'publish') publish(dir);
  else fail('Unknown command ' + command);
} catch (error) { console.error('gpd-catalog: ' + error.message); process.exitCode = 1; }
