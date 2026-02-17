import { ActivatedRoute, Router } from '@angular/router';

/**
 * The `token` of a mail link, read once. It is taken out of the address bar at once with a router navigation that replaces the
 * history entry (`replaceUrl`), so it is in neither the history, nor what a person copies, nor the router's own copy of the
 * address (`router.url`). A missing or empty token is `null`.
 *
 * The navigation lands on the same route, so the router keeps this component and does not build it again: the token is read
 * here, once, and the screen makes no second call. A page that is reused when only `?token=` changes would keep the first token;
 * a mail link is a full page load and the address bar never holds a token after the screen opens, so a second one cannot arrive.
 */
export function takeTokenFromUrl(route: ActivatedRoute, router: Router): string | null {
  const token = route.snapshot.queryParamMap.get('token');
  // After the navigation that is running (this component is being built by it); an error of the second one changes nothing here.
  void Promise.resolve().then(() =>
    router.navigate([], { relativeTo: route, queryParams: { token: null }, queryParamsHandling: 'merge', replaceUrl: true }),
  );
  return token === null || token === '' ? null : token;
}
