import { ParamMap } from '@angular/router';

/**
 * The `token` of a mail link, read once. It is taken out of the address bar at once (`history.replaceState`), together with
 * everything else after the path, so it stays out of the history and of what a person copies. `history.state` is passed on
 * unchanged: the router keeps its own bookkeeping there. A missing or empty token is `null`.
 *
 * Two things this does not do, on purpose (the contract names `history.replaceState` as the way):
 * - The router is not told, so `router.url` still holds the original address, token included, while the page is open. Nothing
 *   on the three mail-link screens reads it: they make no call that the interceptor would send to /login with `router.url`.
 *   A page of your own that reads `router.url` on one of these routes must not log or send it.
 * - A page that is reused by the router when only `?token=` changes would keep the first token. A mail link is a full page load
 *   and the address bar never holds a token after the screen opens, so a second token cannot arrive by navigation.
 */
export function takeTokenFromUrl(route: { snapshot: { queryParamMap: ParamMap } }): string | null {
  const token = route.snapshot.queryParamMap.get('token');
  history.replaceState(history.state, '', window.location.pathname);
  return token === null || token === '' ? null : token;
}
