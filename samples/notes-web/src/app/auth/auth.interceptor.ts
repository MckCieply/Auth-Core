import {
  HttpErrorResponse,
  HttpEvent,
  HttpHandlerFn,
  HttpInterceptorFn,
  HttpRequest,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { from, lastValueFrom } from 'rxjs';
import { AuthService, errorCode } from './auth.service';

/** Paths that start like this get the access token. A product edits these two lists (guide, step 3). */
export const TOKEN_PATH_PREFIXES: readonly string[] = ['/api/', '/auth/org'];
/** Paths that are exactly this get it too. */
export const TOKEN_PATHS: readonly string[] = ['/auth/me'];

/**
 * True for a request to the app's own origin whose path is one of the lists above. Anything else (another origin, the
 * other /auth endpoints, a path that only looks like one after `..` is resolved) never gets the token.
 */
export function wantsToken(url: string, origin: string = window.location.origin): boolean {
  let parsed: URL;
  try {
    parsed = new URL(url, origin);
  } catch {
    return false;
  }
  if (parsed.origin !== origin) {
    return false;
  }
  const path = parsed.pathname;
  return TOKEN_PATHS.includes(path) || TOKEN_PATH_PREFIXES.some((prefix) => path.startsWith(prefix));
}

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!wantsToken(req.url)) {
    return next(req);
  }
  // `from(promise)` loses cancellation: a caller that unsubscribes does not stop the request in flight. Acceptable for a sample
  // whose calls are short JSON requests; a product that needs cancellation writes this with RxJS operators instead.
  return from(send(req, next, inject(AuthService), inject(Router)));
};

async function send(
  req: HttpRequest<unknown>,
  next: HttpHandlerFn,
  auth: AuthService,
  router: Router,
): Promise<HttpEvent<unknown>> {
  const sentWith = auth.token();
  try {
    return await lastValueFrom(next(withToken(req, sentWith)));
  } catch (error) {
    if (!(error instanceof HttpErrorResponse)) {
      throw error;
    }
    const permissionsChanged = error.status === 403 && errorCode(error) === 'permissions_changed';
    if (error.status !== 401 && !permissionsChanged) {
      announce(error, auth);
      throw error;
    }
    if (!(await tokenIsRenewed(auth, router, sentWith))) {
      throw error;
    }
    if (permissionsChanged && !isMe(req)) {
      // The role changed: the header and the forms follow it.
      void auth.loadMe();
    }
    return await sendAgain(req, next, auth);
  }
}

/** True when the request may be sent again with the token the service holds now. */
async function tokenIsRenewed(auth: AuthService, router: Router, sentWith: string | null): Promise<boolean> {
  const current = auth.token();
  if (current === null) {
    return false; // the session ended while this request was away
  }
  if (current !== sentWith) {
    return true; // another request renewed the token meanwhile
  }
  const result = await auth.refresh(); // one refresh at a time: the service shares a running one
  if (result === 'ok') {
    return true;
  }
  if (result === 'rejected') {
    if (auth.token() === null) {
      goToLogin(router); // not when a new sign-in happened while the refresh was away: that session is fine
    }
  } else {
    auth.showNotice('unreachable');
  }
  return false;
}

/** The second and last try: whatever it answers is the answer. */
async function sendAgain(req: HttpRequest<unknown>, next: HttpHandlerFn, auth: AuthService): Promise<HttpEvent<unknown>> {
  try {
    return await lastValueFrom(next(withToken(req, auth.token())));
  } catch (error) {
    if (error instanceof HttpErrorResponse) {
      announce(error, auth);
    }
    throw error;
  }
}

function withToken(req: HttpRequest<unknown>, token: string | null): HttpRequest<unknown> {
  return token === null ? req : req.clone({ setHeaders: { Authorization: `Bearer ${token}` } });
}

/** The answers that have a message of their own. */
function announce(error: HttpErrorResponse, auth: AuthService): void {
  const code = errorCode(error);
  if (error.status === 403 && code === 'forbidden') {
    auth.showNotice('forbidden');
  } else if (error.status === 503 && code === 'auth_unavailable') {
    auth.showNotice('try_later');
  }
}

function isMe(req: HttpRequest<unknown>): boolean {
  return new URL(req.url, window.location.origin).pathname === '/auth/me';
}

function goToLogin(router: Router): void {
  if (router.url.startsWith('/login')) {
    return;
  }
  void router.navigate(['/login'], { queryParams: { returnUrl: router.url } });
}
