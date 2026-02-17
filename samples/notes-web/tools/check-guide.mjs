// Checks the integration guide (docs/integration/angular.md) against acceptance criterion 9 of spec 0007, with no Docker and
// no browser:
//   1. the guide has exactly seven step headings, "## 1. ..." to "## 7. ...", in order, on the subjects of the spec;
//   2. every relative link of the guide ends at a file or directory that exists;
//   3. every path in a code span (`samples/...`, `src/app/auth/...`, `Caddyfile`, ...) exists, from the repository root or
//      from samples/notes-web;
//   3b. every bare file name in a code span (`auth.guard.ts`, `pages/login.ts`, `texts.ts`) is the path of a real file relative to
//      src/app/auth, src/app, src or samples/notes-web (build output and packages left out) or a file of docs/integration, or
//      the end of the path (after a `/`) of exactly one file; a name that fits several files and no base is ambiguous: a failure;
//   4. the guide names no product (it is written for any product).
// Run it with `npm run check:docs` from samples/notes-web. Exit status 0 when every check passes, 1 otherwise.
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const sample = resolve(here, '..');
const repo = resolve(sample, '..', '..');
const guidePath = resolve(repo, 'docs', 'integration', 'angular.md');

// What each of the seven steps is about (spec 0007, "Integration guide"): a word of the heading that must be there.
const STEPS = ['one origin', 'src/app/auth', 'which paths', 'mail links', "interceptor's table", 'headers', 'real phone'];
// Names of products that must not appear in the guide (the sample is "notes"; the guide is for any product).
const PRODUCTS = [/speech[- ]to[- ]mail/i, /\bspeech\b/i, /stereo/i, /investing/i];
// A code span is a path to check when it starts like one of these.
const PATH_START = /^(samples\/|scripts\/|docs\/|src\/|e2e\/|tools\/|Caddyfile|compose\.yml|Dockerfile|proxy\.conf\.json|angular\.json|playwright\.config\.ts)/;

// A bare file name in a code span: no folder of the repository in front, ends in one of these extensions, so `pages/login.ts` and
// `texts.ts` are checked, while `.js`, `/missing.js` and `ng serve` are not.
const BARE_FILE = /^[A-Za-z0-9_][A-Za-z0-9_./-]*\.(ts|json|yml|md|mjs|html)$/;
// Folders that hold no source of the sample.
const SKIP_DIRS = new Set(['node_modules', 'dist', '.angular', 'test-results', '.git']);

/** The paths (with /) of every file under a folder, relative to it. */
function filesUnder(folder, base = folder) {
  const found = [];
  for (const entry of readdirSync(folder, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (!SKIP_DIRS.has(entry.name)) {
        found.push(...filesUnder(resolve(folder, entry.name), base));
      }
    } else {
      found.push(relative(base, resolve(folder, entry.name)).split(sep).join('/'));
    }
  }
  return found;
}

const problems = [];
const problem = (text) => problems.push(text);

if (!existsSync(guidePath)) {
  console.error(`FAIL the guide is missing: ${guidePath}`);
  process.exit(1);
}
const guide = readFileSync(guidePath, 'utf8').replace(/\r\n/g, '\n');
// Fenced blocks hold code, not prose: their backticks are not code spans, and their text is not checked for links.
const prose = guide.replace(/^```[^\n]*\n[\s\S]*?^```[^\n]*$/gm, '');

// 1. The seven steps.
const headings = [...prose.matchAll(/^## (\d+)\. (.+)$/gm)].map((match) => ({ number: Number(match[1]), title: match[2] }));
if (headings.length !== STEPS.length) {
  problem(`the guide has ${headings.length} numbered step headings, not ${STEPS.length}`);
}
STEPS.forEach((subject, index) => {
  const heading = headings[index];
  if (heading === undefined) {
    problem(`step ${index + 1} is missing`);
  } else if (heading.number !== index + 1) {
    problem(`heading ${index + 1} is numbered ${heading.number}: "${heading.title}"`);
  } else if (!heading.title.toLowerCase().includes(subject.toLowerCase())) {
    problem(`step ${index + 1} ("${heading.title}") is not about "${subject}"`);
  }
});

// 2. Relative links.
let links = 0;
for (const match of prose.matchAll(/\]\(([^)\s]+)\)/g)) {
  const target = match[1].split('#')[0];
  if (target === '' || /^[a-z][a-z0-9+.-]*:/i.test(target)) {
    continue; // an anchor in the same page, or an absolute URL
  }
  links += 1;
  if (!existsSync(resolve(dirname(guidePath), target))) {
    problem(`the link ${match[1]} points at nothing`);
  }
}

// 3. Paths in code spans.
const spans = new Set();
for (const match of prose.matchAll(/`([^`\n]+)`/g)) {
  const text = match[1].trim();
  if (PATH_START.test(text) && !/[\s*<>{}=]/.test(text)) {
    spans.add(text);
  }
}
// 3b. Bare file names. A name is a file when it is the path of a file relative to one of the folders the guide talks about (BASES,
// the closest first: `auth.guard.ts` is in src/app/auth, `pages/login.ts` in src/app, `index.html` in src, a file of
// docs/integration by its own name), or, failing that, the end of the path on a `/` boundary of exactly one file. A name that
// ends the paths of several files and is none of them relative to a base is ambiguous: it fails, so a renamed file cannot hide
// behind another of the same name.
const BASES = ['src/app/auth', 'src/app', 'src', ''];
const sampleFiles = filesUnder(sample);
const docFiles = filesUnder(resolve(repo, 'docs', 'integration'));
const everyFile = [...sampleFiles.map((path) => `samples/notes-web/${path}`), ...docFiles.map((path) => `docs/integration/${path}`)];
const bare = new Set();
for (const match of prose.matchAll(/`([^`\n]+)`/g)) {
  const text = match[1].trim();
  if (!PATH_START.test(text) && BARE_FILE.test(text)) {
    bare.add(text);
  }
}
for (const name of bare) {
  const exact = BASES.some((base) => sampleFiles.includes(base === '' ? name : `${base}/${name}`)) || docFiles.includes(name);
  if (exact) {
    continue;
  }
  const ends = everyFile.filter((path) => path.endsWith(`/${name}`));
  if (ends.length === 0) {
    problem(`the file name \`${name}\` is no file of samples/notes-web or docs/integration`);
  } else if (ends.length > 1) {
    problem(`the file name \`${name}\` is ambiguous (${ends.join(', ')}): write its path from src/app or from samples/notes-web`);
  }
}
for (const path of spans) {
  if (!existsSync(resolve(repo, path)) && !existsSync(resolve(sample, path))) {
    problem(`the path \`${path}\` exists neither from the repository root nor from samples/notes-web`);
  }
}

// 4. No product.
for (const name of PRODUCTS) {
  const found = name.exec(guide);
  if (found !== null) {
    problem(`the guide names a product: "${found[0]}"`);
  }
}

if (problems.length > 0) {
  for (const text of problems) {
    console.error(`FAIL ${text}`);
  }
  process.exit(1);
}
console.log(`PASS the guide: ${headings.length} steps, ${links} links and ${spans.size} paths and ${bare.size} file names in code spans exist, no product named`);
