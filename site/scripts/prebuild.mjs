// prebuild (spec 0018): the parser's self-test, versions.json, the changelog bodies, the version summaries and the page
// parity. Each failure prints its cause and exits 1. npm runs it before astro dev and astro build (site/package.json).
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { checkBodies, checkParity, checkSummaries, pickPython, SUMMARY_FILES } from './checks.mjs';

const site = join(dirname(fileURLToPath(import.meta.url)), '..');
const root = join(site, '..');
const parser = 'scripts/release-notes.py';

function run(command, args) {
  return spawnSync(command, args, { cwd: root, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
}

function fail(step, problems) {
  console.error('prebuild failed at ' + step + ':\n  ' + problems.join('\n  '));
  process.exit(1);
}

// 1. the parser's self-test, with the first Python that passes it
const { python, tried } = pickPython(run, parser);
if (!python) fail('the parser self-test (tried python3, python, py -3)', tried);

// 2. versions.json, from the same interpreter
const json = run(python[0], [...python.slice(1), parser, '--all-json']);
if (json.error || json.status !== 0) {
  fail('release-notes.py --all-json', [String(json.stderr || (json.error && json.error.message) || 'exit ' + json.status).trim()]);
}
let versions;
try {
  versions = JSON.parse(json.stdout);
} catch (error) {
  fail('release-notes.py --all-json', ['the output is not JSON: ' + error.message]);
}
const bodyProblems = checkBodies(versions);
if (bodyProblems.length) fail('the changelog bodies', bodyProblems);
mkdirSync(join(site, 'src', 'data'), { recursive: true });
writeFileSync(join(site, 'src', 'data', 'versions.json'), JSON.stringify(versions, null, 2) + '\n');

// 3. the version summaries
const files = {};
const readProblems = [];
for (const [lang, name] of Object.entries(SUMMARY_FILES)) {
  if (!existsSync(join(site, name))) {
    readProblems.push(name + ': the file is missing');
    continue;
  }
  try {
    files[lang] = JSON.parse(readFileSync(join(site, name), 'utf8'));
  } catch (error) {
    readProblems.push(name + ': not valid JSON (' + error.message + ')');
  }
}
if (readProblems.length) fail('the version summaries', readProblems);
const summaryProblems = checkSummaries(versions, files);
if (summaryProblems.length) fail('the version summaries', summaryProblems);

// 4. the page parity
const docs = join(site, 'src', 'content', 'docs');
const parityProblems = checkParity(readdirSync(docs, { recursive: true }).map((name) => String(name).split(sep).join('/')), ['404.md']);
if (parityProblems.length) fail('the page parity', parityProblems);

const server = versions.filter((entry) => entry.kind === 'server').length;
console.log('prebuild ok: ' + python.join(' ') + ', ' + versions.length + ' versions (' + server + ' server), summaries and page parity checked');
