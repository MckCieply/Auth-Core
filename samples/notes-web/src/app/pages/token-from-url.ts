import { ParamMap } from '@angular/router';

/**
 * The `token` of a mail link, read once. It is taken out of the address bar at once (`history.replaceState`), together with
 * everything else after the path, so it stays out of the history and of what a person copies. `history.state` is passed on
 * unchanged: the router keeps its own bookkeeping there. A missing or empty token is `null`.
 */
export function takeTokenFromUrl(route: { snapshot: { queryParamMap: ParamMap } }): string | null {
  const token = route.snapshot.queryParamMap.get('token');
  history.replaceState(history.state, '', window.location.pathname);
  return token === null || token === '' ? null : token;
}
