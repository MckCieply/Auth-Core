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
}

export function relabelBackLink(node, ctx) {
  const label = node.properties?.ariaLabel;
  if (typeof label === 'string' && label.startsWith(BACK)) {
    ctx.setProperty(node, 'ariaLabel', 'Powrót do odwołania ' + label.slice(BACK.length));
  }
}

export default function footnoteLabels(factoryContext) {
  const polish = isPolishPage(factoryContext?.fileURL);
  const element = [{ filter: ['h2'], visit: (node, ctx) => relabelHeading(node, ctx, polish) }];
  if (polish) element.push({ filter: ['a'], visit: relabelBackLink });
  return { name: 'footnote-labels', element };
}
