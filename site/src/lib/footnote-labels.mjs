// Satteri hast plugin: the footnote heading reads Sources (Polish: Źródła) on every page; Polish pages also get Polish back-link labels.
const PL_PATH = '/content/docs/pl/';
const BACK = 'Back to reference ';

export function isPolishPage(fileURL) {
  const path = decodeURIComponent(String(fileURL?.pathname ?? '')).split(String.fromCharCode(92)).join('/');
  return path.includes(PL_PATH);
}

export function relabelHeading(node, ctx, polish = false) {
  if (node.properties?.id !== 'footnote-label') return;
  const count = node.children?.length ?? 0;
  for (let index = count - 1; index >= 0; index--) ctx.removeChildAt(node, index);
  ctx.appendChild(node, { type: 'text', value: polish ? 'Źródła' : 'Sources' });
  const cls = node.properties?.className;
  const names = Array.isArray(cls) ? cls : typeof cls === 'string' ? cls.split(' ') : [];
  if (names.includes('sr-only')) ctx.setProperty(node, 'className', names.filter((name) => name !== 'sr-only'));
}

const TEXT_STYLE = String.fromCharCode(0xfe0e);

export function relabelBackLink(node, ctx, polish = false) {
  const label = node.properties?.ariaLabel;
  if (typeof label !== 'string' || !label.startsWith(BACK)) return;
  if (polish) ctx.setProperty(node, 'ariaLabel', 'Powrót do odwołania ' + label.slice(BACK.length));
  // U+21A9 renders as a blue emoji box on Windows; the text-style selector keeps it a plain arrow.
  const arrow = node.children?.find((child) => child.type === 'text' && child.value.length > 0 && !child.value.endsWith(TEXT_STYLE));
  if (arrow) {
    const index = node.children.indexOf(arrow);
    const value = arrow.value + TEXT_STYLE;
    ctx.removeChildAt(node, index);
    ctx.appendChild(node, { type: 'text', value });
  }
}

export default function footnoteLabels(factoryContext) {
  const polish = isPolishPage(factoryContext?.fileURL);
  const element = [
    { filter: ['h2'], visit: (node, ctx) => relabelHeading(node, ctx, polish) },
    { filter: ['a'], visit: (node, ctx) => relabelBackLink(node, ctx, polish) },
  ];
  return { name: 'footnote-labels', element };
}
