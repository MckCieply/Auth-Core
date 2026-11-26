// Pure functions over versions.json (the output of release-notes.py --all-json). Shared by the components and the tests.
const MONTHS = {
  en: ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'],
  pl: ['sty', 'lut', 'mar', 'kwi', 'maj', 'cze', 'lip', 'sie', 'wrz', 'paź', 'lis', 'gru'],
};
const FENCE = String.fromCharCode(96).repeat(3);
const BT = String.fromCharCode(96);
const CODE_SPAN = new RegExp(BT + BT + '.*?' + BT + BT + '|' + BT + '[^' + BT + ']*' + BT, 'g');

export const FULL_CHANGELOG = 'Full changelog: ';

// '2026-11-24' -> '24 Nov 2026' (en) or '24 lis 2026' (pl): from the string, never through Date or Intl.
export function formatDate(date, lang) {
  const found = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(date));
  const months = MONTHS[lang];
  if (!found || !months) throw new Error('formatDate: expected YYYY-MM-DD and en or pl, got ' + JSON.stringify(date) + ' and ' + JSON.stringify(lang));
  const month = Number(found[2]);
  const day = Number(found[3]);
  if (month < 1 || month > 12 || day < 1 || day > 31) throw new Error('formatDate: no such date ' + date);
  return day + ' ' + months[month - 1] + ' ' + found[1];
}

// The server versions, in the order of the file (newest first).
export function serverVersions(entries) {
  return entries.filter((entry) => entry.kind === 'server');
}

function trimBlank(lines) {
  let start = 0;
  let end = lines.length;
  while (start < end && !lines[start].trim()) start++;
  while (end > start && !lines[end - 1].trim()) end--;
  return lines.slice(start, end);
}

// body: the text release-notes.py prints for a tag. Returns
//   upgrading: the lines after '### Upgrading' up to the next '### ' heading or the Full changelog line ('' when none);
//   rest: the body without the Upgrading subsection and without the Full changelog line;
//   fullChangelog: the Full changelog line.
export function splitBody(body) {
  const lines = String(body).split('\n');
  const last = lines.findLastIndex((line) => line.startsWith(FULL_CHANGELOG));
  if (last < 0) throw new Error('splitBody: the body has no line starting with "' + FULL_CHANGELOG + '"');
  const fullChangelog = lines[last];
  const content = lines.slice(0, last);
  const start = content.findIndex((line) => line.trim() === '### Upgrading');
  if (start < 0) return { upgrading: '', rest: trimBlank(content).join('\n'), fullChangelog };
  let end = content.findIndex((line, index) => index > start && line.startsWith('### '));
  if (end < 0) end = content.length;
  return {
    upgrading: trimBlank(content.slice(start + 1, end)).join('\n'),
    rest: trimBlank([...content.slice(0, start), ...content.slice(end)]).join('\n'),
    fullChangelog,
  };
}

// The first sentence: up to the first '.', '!' or '?' that a space or the end follows, so '0.4.0' does not end it.
export function firstSentence(text) {
  const trimmed = String(text).trim();
  const found = /^.*?(?:\.|!|\?)(?=\s|$)/s.exec(trimmed);
  return found ? found[0] : trimmed;
}

// The raw HTML tags of a changelog body outside fenced blocks, code spans and autolinks; [] when none.
export function findRawHtml(markdown) {
  const found = [];
  let inFence = false;
  for (const line of String(markdown).split('\n')) {
    if (line.trim().startsWith(FENCE)) {
      inFence = !inFence;
      continue;
    }
    if (inFence) continue;
    const prose = line.replace(CODE_SPAN, '').replace(/<(?:https?:|mailto:)[^>\s]*>/g, '');
    for (const tag of prose.matchAll(/<\/?[A-Za-z][A-Za-z0-9-]*(?:\s[^<>]*)?\/?>/g)) found.push(tag[0]);
  }
  return found;
}
