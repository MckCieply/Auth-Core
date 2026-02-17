// Checks the integration guide (docs/integration/angular.md) against acceptance criterion 9 of spec 0007, with no Docker and
// no browser:
//   1. the guide has exactly seven step headings, "## 1. ..." to "## 7. ...", in order, on the subjects of the spec;
//   2. every relative link of the guide ends at a file or directory that exists;
//   3. every path in a code span (`samples/...`, `src/app/auth/...`, `Caddyfile`, ...) exists, from the repository root or
//      from samples/notes-web;
//   4. the guide names no product (it is written for any product).
// Run it with `npm run check:docs` from samples/notes-web. Exit status 0 when every check passes, 1 otherwise.
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
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
console.log(`PASS the guide: ${headings.length} steps, ${links} links and ${spans.size} paths in code spans exist, no product named`);
