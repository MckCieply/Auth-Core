// Paths of the site with the base path of astro.config.mjs ('/Auth-Core'). Pure functions, shared by the components
// and the tests. A component never writes a path by hand: it calls pagePath or withBase.
export const BASE = '/Auth-Core/';

export function withBase(path) {
  return BASE + String(path).replace(/^\/+/, '');
}

// lang: 'en' or 'pl'; slug: '' (Start), 'fit', 'compare', 'license' or 'versions'.
export function pagePath(lang, slug) {
  const parts = [lang === 'pl' ? 'pl' : '', slug].filter(Boolean);
  return parts.length ? withBase(parts.join('/') + '/') : BASE;
}

// Starlight's route id: '' or 'index' for Start, 'pl' or 'pl/index' for the Polish Start, 'fit', 'pl/fit', ...
export function slugOf(id) {
  const parts = String(id).split('/').filter(Boolean);
  if (parts[0] === 'pl') parts.shift();
  if (parts[parts.length - 1] === 'index') parts.pop();
  return parts.join('/');
}

export function langOf(id) {
  return String(id).split('/')[0] === 'pl' ? 'pl' : 'en';
}

// The same page in the other language: the footer's plain link, which works without JavaScript.
export function otherLanguagePath(id) {
  return pagePath(langOf(id) === 'pl' ? 'en' : 'pl', slugOf(id));
}
