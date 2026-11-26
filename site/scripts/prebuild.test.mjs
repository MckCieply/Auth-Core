// Tests of the build checks and the pure functions of the site (spec 0018, "Prebuild"). Run: node --test scripts/*.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { checkSummaries, checkParity, checkBodies, pickPython, outcomeOf, brokenLinks, SUMMARY_LIMIT } from './checks.mjs';
import { formatDate, splitBody, firstSentence, findRawHtml, serverVersions } from '../src/lib/versions.mjs';
import { otherLanguagePath, pagePath, withBase, slugOf, BASE } from '../src/lib/paths.mjs';

const PL = 'src/data/version-summaries.pl.json';
const EN = 'src/data/version-summaries.en.json';
const FULL = 'Full changelog: [CHANGELOG.md](https://github.com/MckCieply/Auth-Core/blob/v0.2.0/CHANGELOG.md)';
const BT = String.fromCharCode(96);
const versions = [
  { tag: 'v0.2.0', kind: 'server', version: '0.2.0', date: '2026-01-02', title: 'Auth-Core 0.2.0', body: 'Two.\n\n' + FULL + '\n' },
  { tag: 'v0.1.0', kind: 'server', version: '0.1.0', date: '2026-01-01', title: 'Auth-Core 0.1.0', body: 'One.\n\n' + FULL + '\n' },
  { tag: 'python-v0.1.1', kind: 'python', version: '0.1.1', date: '2026-01-03', title: 'auth-core-fastapi 0.1.1', body: 'Py.\n\n' + FULL + '\n' },
];
const goodPl = { '0.2.0': 'Dwa.', '0.1.0': 'Jeden.' };
const goodEn = { '0.2.0': 'Two.', '0.1.0': 'One.' };
const both = (pl, en) => ({ pl, en });

test('versions: only server versions, in file order', () => {
  assert.deepEqual(serverVersions(versions).map((entry) => entry.version), ['0.2.0', '0.1.0']);
});

test('summaries: complete files pass', () => {
  assert.deepEqual(checkSummaries(versions, both(goodPl, goodEn)), []);
});

test('summaries: a version missing from the Polish file is named', () => {
  assert.deepEqual(checkSummaries(versions, both({ '0.2.0': 'Dwa.' }, goodEn)), [PL + ': no summary for 0.1.0']);
});

test('summaries: a version missing from the English file is named', () => {
  assert.deepEqual(checkSummaries(versions, both(goodPl, { '0.1.0': 'One.' })), [EN + ': no summary for 0.2.0']);
});

test('summaries: a key that is not a server version is named', () => {
  assert.deepEqual(checkSummaries(versions, both({ ...goodPl, '9.9.9': 'X.', '0.1.1': 'Py.' }, goodEn)), [
    PL + ': 9.9.9 is not a server version of CHANGELOG.md',
    PL + ': 0.1.1 is not a server version of CHANGELOG.md',
  ]);
});

test('summaries: 300 characters pass, 301 fail', () => {
  assert.equal(SUMMARY_LIMIT, 300);
  assert.deepEqual(checkSummaries(versions, both({ ...goodPl, '0.1.0': 'a'.repeat(300) }, goodEn)), []);
  assert.deepEqual(checkSummaries(versions, both(goodPl, { ...goodEn, '0.2.0': 'a'.repeat(301) })), [
    EN + ': the summary of 0.2.0 has 301 characters, more than 300',
  ]);
});

test('summaries: 300 Polish letters count as 300 characters', () => {
  assert.deepEqual(checkSummaries(versions, both({ ...goodPl, '0.2.0': 'ą'.repeat(300) }, goodEn)), []);
});

test('summaries: an empty summary is named', () => {
  assert.deepEqual(checkSummaries(versions, both({ ...goodPl, '0.1.0': '  ' }, goodEn)), [PL + ': the summary of 0.1.0 is empty']);
});

test('summaries: a file that is not an object from version to text is named', () => {
  assert.deepEqual(checkSummaries(versions, both(['Dwa.'], { ...goodEn, '0.1.0': 1 })), [
    PL + ': not an object from version to text',
    EN + ': not an object from version to text',
  ]);
});

const pages = ['index.mdx', 'fit.mdx', 'compare.md', 'pl/index.mdx', 'pl/fit.mdx', 'pl/compare.md'];

test('parity: complete pages pass', () => {
  assert.deepEqual(checkParity(pages), []);
});

test('parity: a missing Polish page is named', () => {
  assert.deepEqual(checkParity(pages.filter((path) => path !== 'pl/compare.md')), [
    'src/content/docs/compare.md has no Polish page src/content/docs/pl/compare.md',
  ]);
});

test('parity: a missing English page is named', () => {
  assert.deepEqual(checkParity(pages.filter((path) => path !== 'fit.mdx')), [
    'src/content/docs/pl/fit.mdx has no English page src/content/docs/fit.mdx',
  ]);
});

test('parity: the same page with another extension counts as missing', () => {
  assert.deepEqual(checkParity(['fit.mdx', 'pl/fit.md']), [
    'src/content/docs/fit.mdx has no Polish page src/content/docs/pl/fit.mdx',
    'src/content/docs/pl/fit.md has no English page src/content/docs/fit.md',
  ]);
});

test('parity: files that are not pages are ignored', () => {
  assert.deepEqual(checkParity([...pages, 'pl', 'notes.txt', 'pl/image.webp']), []);
});

test('parity: a skipped name with no Polish twin passes, and without the skip it fails', () => {
  assert.deepEqual(checkParity([...pages, '404.md'], ['404.md']), []);
  assert.deepEqual(checkParity([...pages, '404.md']), ['src/content/docs/404.md has no Polish page src/content/docs/pl/404.md']);
});

test('parity: a skip does not hide any other missing page', () => {
  assert.deepEqual(checkParity([...pages.filter((path) => path !== 'pl/compare.md'), '404.md'], ['404.md']), [
    'src/content/docs/compare.md has no Polish page src/content/docs/pl/compare.md',
  ]);
});

test('dates: every month in both languages, the day without a leading zero', () => {
  const en = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  const pl = ['sty', 'lut', 'mar', 'kwi', 'maj', 'cze', 'lip', 'sie', 'wrz', 'paź', 'lis', 'gru'];
  for (let month = 1; month <= 12; month++) {
    const date = '2026-' + String(month).padStart(2, '0') + '-05';
    assert.equal(formatDate(date, 'en'), '5 ' + en[month - 1] + ' 2026');
    assert.equal(formatDate(date, 'pl'), '5 ' + pl[month - 1] + ' 2026');
  }
  assert.equal(formatDate('2026-11-24', 'en'), '24 Nov 2026');
  assert.equal(formatDate('2026-11-24', 'pl'), '24 lis 2026');
  assert.equal(formatDate('2026-10-01', 'pl'), '1 paź 2026');
});

test('dates: a malformed date or language throws', () => {
  assert.throws(() => formatDate('2026-1-5', 'en'));
  assert.throws(() => formatDate('2026-13-01', 'en'));
  assert.throws(() => formatDate('2026-11-24', 'de'));
});

test('upgrading: cut in the middle ends at the next ### heading', () => {
  const body = ['Intro.', '', '### Added', '- a', '', '### Upgrading', '', '- run the migration', '- set X', '', '### Fixed', '- b', '', FULL, ''].join('\n');
  assert.deepEqual(splitBody(body), {
    upgrading: '- run the migration\n- set X',
    rest: 'Intro.\n\n### Added\n- a\n\n### Fixed\n- b',
    fullChangelog: FULL,
  });
});

test('upgrading: cut at the end ends before the Full changelog line', () => {
  const body = ['Intro.', '', '### Upgrading', '', 'Back up first.', '', FULL, ''].join('\n');
  assert.deepEqual(splitBody(body), { upgrading: 'Back up first.', rest: 'Intro.', fullChangelog: FULL });
});

test('upgrading: a body without Upgrading keeps everything', () => {
  assert.deepEqual(splitBody('Intro.\n\n### Added\n- a\n\n' + FULL + '\n'), {
    upgrading: '',
    rest: 'Intro.\n\n### Added\n- a',
    fullChangelog: FULL,
  });
});

test('upgrading: a body without the Full changelog line throws', () => {
  assert.throws(() => splitBody('Intro.\n'), /Full changelog/);
});

test('first sentence: a version number does not end it', () => {
  assert.equal(firstSentence('Moves from 0.3.0 to 0.4.0 without a migration. Then more.'), 'Moves from 0.3.0 to 0.4.0 without a migration.');
  assert.equal(firstSentence('Sign-in with GitHub (spec 0012). A second one.'), 'Sign-in with GitHub (spec 0012).');
  assert.equal(firstSentence('Ends on a version 0.4.0.'), 'Ends on a version 0.4.0.');
  assert.equal(firstSentence('No full stop at all'), 'No full stop at all');
  assert.equal(firstSentence('Poprawka: mail nie podaje roli. Druga.'), 'Poprawka: mail nie podaje roli.');
});

test('raw html: a tag in prose is found', () => {
  assert.deepEqual(findRawHtml('Text <b>bold</b> and <br/>.'), ['<b>', '</b>', '<br/>']);
  assert.deepEqual(findRawHtml('<details open>'), ['<details open>']);
});

test('raw html: code spans, fences, autolinks and a lone < are ignored', () => {
  const fence = BT.repeat(3);
  const text = ['Use ' + BT + '<b>' + BT + ' here, see <https://x.y/z>, 1 < 2 and a -> b.', fence + 'html', '<div>', fence, ''].join('\n');
  assert.deepEqual(findRawHtml(text), []);
});

test('bodies: a raw HTML tag and a missing Full changelog line are named', () => {
  const bad = [{ ...versions[0], body: 'Two <b>x</b>.\n\n' + FULL + '\n' }, { ...versions[1], body: 'One.\n' }, versions[2]];
  assert.deepEqual(checkBodies(bad), [
    'versions.json: v0.2.0: raw HTML <b> in the changelog section',
    'versions.json: v0.2.0: raw HTML </b> in the changelog section',
    'versions.json: v0.1.0: splitBody: the body has no line starting with "Full changelog: "',
  ]);
  assert.deepEqual(checkBodies(versions), []);
});

const STORE = 'Python was not found; run without arguments to install from the Microsoft Store, or disable this shortcut from Settings > Manage App Execution Aliases.';
const missing = { error: Object.assign(new Error('spawnSync ENOENT'), { code: 'ENOENT' }), status: null, stdout: null, stderr: null };
const passing = { status: 0, stdout: 'self-test ok\n', stderr: '' };
function fake(results) {
  const calls = [];
  const run = (command, args) => {
    calls.push([command, ...args].join(' '));
    return results[command];
  };
  return { run, calls };
}

test('python: the Windows stub of python3 and a missing python move on to py -3', () => {
  const { run, calls } = fake({ python3: { status: 9009, stdout: '', stderr: STORE + '\r\n' }, python: missing, py: passing });
  const picked = pickPython(run, 'scripts/release-notes.py');
  assert.deepEqual(picked.python, ['py', '-3']);
  assert.deepEqual(calls, [
    'python3 scripts/release-notes.py --self-test',
    'python scripts/release-notes.py --self-test',
    'py -3 scripts/release-notes.py --self-test',
  ]);
  assert.deepEqual(picked.tried, ['python3: exit 9009: ' + STORE, 'python: not found']);
});

test('python: when none passes, all three are named with their cause', () => {
  const failing = { status: 1, stdout: '', stderr: 'self-test FAILED: all-json body\n' };
  const { run } = fake({ python3: missing, python: failing, py: missing });
  const picked = pickPython(run, 'scripts/release-notes.py');
  assert.equal(picked.python, null);
  assert.deepEqual(picked.tried, ['python3: not found', 'python: exit 1: self-test FAILED: all-json body', 'py -3: not found']);
});

test('python: exit 0 without the self-test ok line does not pass', () => {
  assert.equal(outcomeOf({ status: 0, stdout: 'self-test okay\n', stderr: '' }), 'exit 0: self-test okay');
  assert.equal(outcomeOf({ status: 0, stdout: 'self-test ok\r\n', stderr: '' }), 'ok');
  assert.equal(outcomeOf(passing), 'ok');
});

test('language link: Start and inner pages, both ways', () => {
  assert.equal(otherLanguagePath(''), '/Auth-Core/pl/');
  assert.equal(otherLanguagePath('index'), '/Auth-Core/pl/');
  assert.equal(otherLanguagePath('pl'), '/Auth-Core/');
  assert.equal(otherLanguagePath('pl/index'), '/Auth-Core/');
  assert.equal(otherLanguagePath('fit'), '/Auth-Core/pl/fit/');
  assert.equal(otherLanguagePath('pl/fit'), '/Auth-Core/fit/');
  assert.equal(otherLanguagePath('pl/versions/'), '/Auth-Core/versions/');
});

test('paths: pagePath and withBase add the base path', () => {
  assert.equal(BASE, '/Auth-Core/');
  assert.equal(pagePath('en', ''), '/Auth-Core/');
  assert.equal(pagePath('pl', ''), '/Auth-Core/pl/');
  assert.equal(pagePath('pl', 'license'), '/Auth-Core/pl/license/');
  assert.equal(withBase('/favicon.svg'), '/Auth-Core/favicon.svg');
  assert.equal(slugOf('pl/compare'), 'compare');
});

const built = new Set(['index.html', 'fit/index.html', 'pl/index.html', 'pl/fit/index.html', '_astro/a.css', 'favicon.svg']);
const exists = (path) => built.has(path);

test('links: good links, anchors, other hosts and assets pass', () => {
  const html = '<a href="/Auth-Core/fit/">x</a><a href="/Auth-Core/">s</a><a href="/Auth-Core">s</a><a href="#_top">t</a>'
    + '<a href="https://github.com/MckCieply/Auth-Core">g</a><a href="mailto:kontakt@toviq.pl">m</a>'
    + '<link rel="stylesheet" href="/Auth-Core/_astro/a.css"><img src="/Auth-Core/favicon.svg" alt=""><a href="pl/?x=1&amp;y=2">p</a>';
  assert.deepEqual(brokenLinks({ 'index.html': html }, exists, '/Auth-Core/'), []);
});

test('links: a component link without the base path is reported', () => {
  assert.deepEqual(brokenLinks({ 'pl/fit/index.html': '<a href="/fit/">x</a>' }, exists, '/Auth-Core/'), [
    'pl/fit/index.html: the link /fit/ is outside /Auth-Core/',
  ]);
});

test('links: a link to a missing page is reported', () => {
  assert.deepEqual(brokenLinks({ 'index.html': '<a href="/Auth-Core/nope/">x</a><a href="fit">y</a>' }, exists, '/Auth-Core/'), [
    'index.html: the link /Auth-Core/nope/ leads to no file',
  ]);
});

test('links: a malformed percent escape is reported, not thrown', () => {
  assert.deepEqual(brokenLinks({ 'index.html': '<a href="/Auth-Core/a%zz">x</a>' }, exists, '/Auth-Core/'), [
    'index.html: the link /Auth-Core/a%zz is not a valid URL',
  ]);
});
