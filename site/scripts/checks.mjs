// The checks of the prebuild and postbuild steps. Pure functions (no file system, no process), tested in prebuild.test.mjs.
import { splitBody, findRawHtml, serverVersions } from '../src/lib/versions.mjs';

export const PYTHONS = [['python3'], ['python'], ['py', '-3']];
export const SUMMARY_LIMIT = 300;
export const SUMMARY_FILES = { pl: 'src/data/version-summaries.pl.json', en: 'src/data/version-summaries.en.json' };

// result: what spawnSync returned. 'ok', or the cause in a few words.
export function outcomeOf(result) {
  if (result.error) return result.error.code === 'ENOENT' ? 'not found' : 'did not start (' + result.error.message + ')';
  const out = String(result.stdout || '');
  if (result.status === 0 && out.split(/\r?\n/).includes('self-test ok')) return 'ok';
  const lines = (String(result.stderr || '') + '\n' + out).split(/\r?\n/).filter((line) => line.trim()).slice(0, 3);
  return 'exit ' + result.status + (lines.length ? ': ' + lines.join(' | ') : '');
}

// run(command, args) -> a spawnSync result. The first interpreter of PYTHONS whose run of script --self-test prints
// 'self-test ok'. Returns { python: ['py', '-3'] or null, tried: ['python3: not found', ...] }.
export function pickPython(run, script) {
  const tried = [];
  for (const candidate of PYTHONS) {
    const outcome = outcomeOf(run(candidate[0], [...candidate.slice(1), script, '--self-test']));
    if (outcome === 'ok') return { python: candidate, tried };
    tried.push(candidate.join(' ') + ': ' + outcome);
  }
  return { python: null, tried };
}

// versions: the entries of versions.json; files: { pl, en }, each the parsed JSON of its file.
export function checkSummaries(versions, files) {
  const server = serverVersions(versions).map((entry) => entry.version);
  const problems = [];
  for (const lang of ['pl', 'en']) {
    const name = SUMMARY_FILES[lang];
    const summaries = files[lang];
    if (summaries === null || typeof summaries !== 'object' || Array.isArray(summaries)
      || Object.values(summaries).some((text) => typeof text !== 'string')) {
      problems.push(name + ': not an object from version to text');
      continue;
    }
    for (const version of server) {
      if (!Object.hasOwn(summaries, version)) problems.push(name + ': no summary for ' + version);
    }
    for (const [version, text] of Object.entries(summaries)) {
      const length = [...text].length;
      if (!server.includes(version)) problems.push(name + ': ' + version + ' is not a server version of CHANGELOG.md');
      else if (!text.trim()) problems.push(name + ': the summary of ' + version + ' is empty');
      else if (length > SUMMARY_LIMIT) problems.push(name + ': the summary of ' + version + ' has ' + length + ' characters, more than ' + SUMMARY_LIMIT);
    }
  }
  return problems;
}

// paths: every entry under src/content/docs/, relative to it, with forward slashes ('fit.mdx', 'pl/fit.mdx', 'pl').
export function checkParity(paths) {
  const pages = paths.filter((path) => /\.mdx?$/.test(path));
  const english = pages.filter((path) => !path.startsWith('pl/'));
  const polish = pages.filter((path) => path.startsWith('pl/')).map((path) => path.slice(3));
  const problems = [];
  for (const path of english) {
    if (!polish.includes(path)) problems.push('src/content/docs/' + path + ' has no Polish page src/content/docs/pl/' + path);
  }
  for (const path of polish) {
    if (!english.includes(path)) problems.push('src/content/docs/pl/' + path + ' has no English page src/content/docs/' + path);
  }
  return problems.sort();
}

// Every server body has its Full changelog line and no raw HTML tag (the bodies are rendered unsanitised).
export function checkBodies(versions) {
  const problems = [];
  for (const entry of serverVersions(versions)) {
    try {
      splitBody(entry.body);
    } catch (error) {
      problems.push('versions.json: ' + entry.tag + ': ' + error.message);
      continue;
    }
    for (const tag of findRawHtml(entry.body)) problems.push('versions.json: ' + entry.tag + ': raw HTML ' + tag + ' in the changelog section');
  }
  return problems;
}

// pages: { 'fit/index.html': html, ... } relative to dist/; exists(path): dist/<path> is a file; base: '/Auth-Core/'.
// Every href and src that stays on the site must stay under the base path and lead to a file.
export function brokenLinks(pages, exists, base) {
  const problems = [];
  const root = base.replace(/\/$/, '');
  for (const [file, html] of Object.entries(pages)) {
    for (const found of html.matchAll(/\s(?:href|src)="([^"]*)"/g)) {
      const target = found[1].replaceAll('&amp;', '&');
      if (!target || target.startsWith('#') || target.startsWith('//') || /^[a-z][a-z0-9+.-]*:/i.test(target)) continue;
      let path = decodeURIComponent(new URL(target, 'https://site.invalid' + base + file).pathname);
      if (path === root) path = base;
      if (!path.startsWith(base)) {
        problems.push(file + ': the link ' + found[1] + ' is outside ' + base);
        continue;
      }
      const rest = path.slice(base.length);
      const candidates = rest === '' || rest.endsWith('/') ? [rest + 'index.html'] : [rest, rest + '/index.html', rest + '.html'];
      if (!candidates.some(exists)) problems.push(file + ': the link ' + found[1] + ' leads to no file');
    }
  }
  return problems;
}
