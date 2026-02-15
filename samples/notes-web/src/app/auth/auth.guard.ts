import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

/** Where a person lands after signing in when there is no (safe) page to go back to. */
export const DEFAULT_RETURN_URL = '/notes';

const MAX_RETURN_URL_LENGTH = 2048;
const CONTROL_OR_BACKSLASH = /[\x00-\x1f\x7f\\]/;
// A slash or a backslash written as %2f or %5c at the start: a router or a server that decodes the path reads it as `//host`.
const ENCODED_SLASH_AT_START = /^\/%(2f|5c)/i;

/**
 * The page to go to after signing in: the value of `returnUrl` if it is a path of this app, else the default.
 * A path starts with one `/`; `//host`, `/\host`, `https://host` and `javascript:` are not paths of this app.
 * The result is for `router.navigateByUrl`; never give it to `location.href` or `window.open`.
 */
export function safeReturnUrl(value: string | null | undefined): string {
  if (typeof value !== 'string' || value.length === 0 || value.length > MAX_RETURN_URL_LENGTH) {
    return DEFAULT_RETURN_URL;
  }
  if (value[0] !== '/' || value[1] === '/') {
    return DEFAULT_RETURN_URL;
  }
  if (CONTROL_OR_BACKSLASH.test(value) || ENCODED_SLASH_AT_START.test(value)) {
    return DEFAULT_RETURN_URL;
  }
  return value;
}

/** Lets a route through only when a token is held. Otherwise: the login screen, which comes back here after. */
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return auth.token() !== null || router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};
