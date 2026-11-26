// Satteri hast plugin: Polish pages get Polish footnote labels (the footnote markup is written in English).
const PL_PATH = '/content/docs/pl/';
const BACK = 'Back to reference ';

export function isPolishPage(fileURL) {
  const path = decodeURIComponent(String(fileURL?.pathname ?? '')).split(String.fromCharCode(92)).join('/');
  return path.includes(PL_PATH);
}

export function relabelHeading(node, ctx) {
  if (node.properties?.id !== 'footnote-label') return;
  const count = node.children?.length ?? 0;
  for (let index = count - 1; index >= 0; index--) ctx.removeChildAt(node, index);
  ctx.appendChild(node, { type: 'text', value: 'Przypisy' });
}

export function relabelBackLink(node, ctx) {
  const label = node.properties?.ariaLabel;
  if (typeof label === 'string' && label.startsWith(BACK)) {
    ctx.setProperty(node, 'ariaLabel', 'Powrót do odwołania ' + label.slice(BACK.length));
  }
}

export default function footnoteLabels(factoryContext) {
  if (!isPolishPage(factoryContext?.fileURL)) return false;
  return {
    name: 'footnote-labels',
    element: [
      { filter: ['h2'], visit: relabelHeading },
      { filter: ['a'], visit: relabelBackLink },
    ],
  };
}
