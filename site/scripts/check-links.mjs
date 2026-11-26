// postbuild (spec 0018): every href and src of the built pages that stays on the site leads to a file of dist/. This
// covers the links that components write, which starlight-links-validator does not see (it reads Markdown links only).
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { brokenLinks } from './checks.mjs';
import { BASE } from '../src/lib/paths.mjs';

const dist = join(dirname(fileURLToPath(import.meta.url)), '..', 'dist');
const names = readdirSync(dist, { recursive: true }).map((name) => String(name).split(sep).join('/'));
const pages = {};
for (const name of names.filter((item) => item.endsWith('.html'))) pages[name] = readFileSync(join(dist, name), 'utf8');
const exists = (path) => existsSync(join(dist, path)) && statSync(join(dist, path)).isFile();
const problems = brokenLinks(pages, exists, BASE);
if (problems.length) {
  console.error('check-links failed:\n  ' + problems.join('\n  '));
  process.exit(1);
}
const links = Object.values(pages).reduce((sum, html) => sum + [...html.matchAll(/\s(?:href|src)="/g)].length, 0);
console.log('check-links ok: ' + Object.keys(pages).length + ' pages, ' + links + ' links');
