#!/usr/bin/env node
// Maintains the public Xbox 360 GPD catalog on Achievement-Labs/achievement-labs. index.json is an asset of
// the gpd-catalog prerelease. GitHub allows 1000 assets per release, so each TITLEID.gpd lives in the
// gpd-catalog-N prerelease named by its index entry. Needs the GitHub CLI for pull/publish.
//   node tools/xbox360/gpd-catalog.cjs pull <catalog-dir>
//   node tools/xbox360/gpd-catalog.cjs add <catalog-dir> <TITLEID.gpd or folder ...> [--title TITLEID]
//   node tools/xbox360/gpd-catalog.cjs verify <catalog-dir>
//   node tools/xbox360/gpd-catalog.cjs publish <catalog-dir>
// Every published GPD is the locked, stripped form from profile-core.js (Gpd.template), so the
// apps, the desktop C# port and this tool all produce identical bytes. Never touches Events/Data.json.
// add searches folders recursively; files that fail to parse are skipped and listed.
'use strict';
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto'), { execFileSync } = require('node:child_process');
const core = require('./shared/profile-core.js');
const TITLE = /^[0-9A-F]{8}$/, LIMIT = 8 * 1024 * 1024, REPO = 'Achievement-Labs/achievement-labs', TAG = 'gpd-catalog';
const SHARD = 900, RELEASE = /^gpd-catalog-[1-9][0-9]*$/;
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

function entry(bytes, release) {
  const info = core.Gpd.inspect(bytes);
  return { name: info.name, achievements: info.achievements, gamerscore: info.gamerscore, bytes: bytes.length, sha256: sha256(bytes), release };
}

// A title keeps its release; new titles fill the lowest release with room.
function shard(index) {
  const counts = {};
  for (const t of Object.values(index.titles)) counts[t.release] = (counts[t.release] || 0) + 1;
  for (let n = 1; ; n++) if ((counts[`${TAG}-${n}`] || 0) < SHARD) return `${TAG}-${n}`;
}

const expand = file => fs.statSync(file).isDirectory()
  ? fs.readdirSync(file).sort().flatMap(name => expand(path.join(file, name)))
  : file.toLowerCase().endsWith('.gpd') ? [file] : [];

function add(dir, files, forcedTitle) {
  files = files.flatMap(expand);
  if (!files.length) fail('Give at least one GPD file or folder.');
  if (forcedTitle && files.length !== 1) fail('--title applies to a single GPD.');
  const index = readIndex(dir), skipped = [];
  let reset = 0;
  fs.mkdirSync(dir, { recursive: true });
  for (const file of files) {
    try {
      const id = (forcedTitle || path.basename(file, path.extname(file))).toUpperCase();
      if (!TITLE.test(id) || id === 'FFFE07D1') fail('name it TITLEID.gpd or pass --title TITLEID');
      const input = fs.readFileSync(file);
      if (input.length > LIMIT) fail('GPD exceeds 8 MB');
      const locked = core.Gpd.template(new Uint8Array(input));
      fs.writeFileSync(path.join(dir, id + '.gpd'), locked);
      index.titles[id] = entry(locked, index.titles[id]?.release || shard(index));
      if (core.Gpd.inspect(input).unlocked) reset++;
    } catch (error) { skipped.push(`${file}: ${error.message}`); }
  }
  writeIndex(dir, index);
  console.log(`Added ${files.length - skipped.length} GPDs (${reset} reset to locked).`);
  if (skipped.length) console.log(`Skipped ${skipped.length}:\n  ` + skipped.join('\n  '));
  verify(dir);
}

function verify(dir) {
  const index = readIndex(dir);
  const files = fs.existsSync(dir) ? fs.readdirSync(dir).filter(f => f.toLowerCase().endsWith('.gpd')) : [];
  for (const file of files) if (!index.titles[path.basename(file, '.gpd')]) fail(`${file} is not listed in index.json.`);
  const counts = {};
  for (const [id, t] of Object.entries(index.titles)) {
    if (!TITLE.test(id) || id === 'FFFE07D1') fail(`Invalid title ID ${id}.`);
    if (!RELEASE.test(t.release || '')) fail(`${id} has no gpd-catalog-N release.`);
    if ((counts[t.release] = (counts[t.release] || 0) + 1) > SHARD) fail(`${t.release} holds more than ${SHARD} GPDs.`);
    const file = path.join(dir, id + '.gpd');
    if (!fs.existsSync(file)) fail(`${id}.gpd is missing.`);
    const bytes = new Uint8Array(fs.readFileSync(file));
    if (bytes.length > LIMIT || bytes.length !== t.bytes || sha256(bytes) !== t.sha256) fail(`${id}.gpd does not match its index size/SHA-256.`);
    if (!Buffer.from(core.Gpd.template(bytes)).equals(Buffer.from(bytes))) fail(`${id}.gpd is not in locked catalog form.`);
    const expected = entry(bytes, t.release);
    for (const key of ['name', 'achievements', 'gamerscore']) if (t[key] !== expected[key]) fail(`${id} index ${key} is stale.`);
  }
  console.log(`PASS: ${Object.keys(index.titles).length} catalog GPDs locked, stripped and matching index.json.`);
}

const gh = (...args) => execFileSync('gh', [...args, '--repo', REPO], { stdio: 'inherit' });
const exists = tag => { try { execFileSync('gh', ['release', 'view', tag, '--repo', REPO], { stdio: 'ignore' }); return true; } catch { return false; } };
const ensure = (tag, title) => exists(tag) || gh('release', 'create', tag, '--prerelease', '--latest=false', '--title', title,
  '--notes', 'Locked Xbox 360 GPDs for the add-game catalog. Not an app release; updaters ignore prereleases.');

function pull(dir) {
  fs.mkdirSync(dir, { recursive: true });
  if (!exists(TAG)) return verify(dir);
  gh('release', 'download', TAG, '--dir', dir, '--clobber');
  for (const tag of new Set(Object.values(readIndex(dir).titles).map(t => t.release))) gh('release', 'download', tag, '--dir', dir, '--clobber');
  verify(dir);
}

// GPDs go up before index.json, so the published index never names a missing asset.
function publish(dir) {
  verify(dir);
  const index = readIndex(dir), groups = {};
  for (const [id, t] of Object.entries(index.titles)) (groups[t.release] ||= []).push(path.join(dir, id + '.gpd'));
  for (const [tag, gpds] of Object.entries(groups)) {
    ensure(tag, `Xbox 360 GPD catalog ${tag.slice(TAG.length + 1)}`);
    for (let i = 0; i < gpds.length; i += 50) gh('release', 'upload', tag, ...gpds.slice(i, i + 50), '--clobber');
  }
  ensure(TAG, 'Xbox 360 GPD catalog index');
  gh('release', 'upload', TAG, path.join(dir, 'index.json'), '--clobber');
  console.log(`Published ${Object.keys(index.titles).length} GPDs across ${Object.keys(groups).length} releases, then index.json.`);
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
