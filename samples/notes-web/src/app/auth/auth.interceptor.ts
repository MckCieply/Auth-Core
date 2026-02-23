import {
  HttpErrorResponse,
  HttpEvent,
  HttpHandlerFn,
  HttpInterceptorFn,
  HttpRequest,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, defer, from, last, of, switchMap, tap, throwError, timeout } from 'rxjs';
import { AuthService, errorCode, retryAfterOf } from './auth.service';

/** Paths that start like this get the access token. A product edits these two lists (guide, step 3). */
export const TOKEN_PATH_PREFIXES: readonly string[] = ['/api/', '/auth/org'];
/** Paths that are exactly this get it too. */
export const TOKEN_PATHS: readonly string[] = ['/auth/me'];
/**
 * A call to the app's own origin that has not been answered after this long fails, so no screen waits for ever. A product with
 * calls that take longer (an upload) raises it, or ends the call itself.
 */
export const REQUEST_TIMEOUT_MS = 30_000;

/** The URL of a request when it is for the app's own origin; null for another origin or for text that is no URL. */
function ownUrl(url: string, origin: string = window.location.origin): URL | null {
  try {
    const parsed = new URL(url, origin);
    return parsed.origin === origin ? parsed : null;
  } catch {
    return null;
  }
}

/**
 * True for a request to the app's own origin whose path is one of the lists above. Anything else (another origin, the
 * other /auth endpoints, a path that only looks like one after `..` is resolved) never gets the token.
 */
export function wantsToken(url: string, origin: string = window.location.origin): boolean {
  const path = ownUrl(url, origin)?.pathname;
  return path !== undefined && (TOKEN_PATHS.includes(path) || TOKEN_PATH_PREFIXES.some((prefix) => path.startsWith(prefix)));
}

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const answer = wantsToken(req.url) ? send(req, next, auth, inject(Router)) : next(req);
  // Built from observables, never promises: when the time is up the timeout unsubscribes, and that cancels the request in flight
  // and ends the chain (no refresh, no second try, no /login) for a caller that has already been told it failed. A 429 from any endpoint
  // of the app's own origin is announced on the way out, whatever the endpoint is.
  return ownUrl(req.url) === null
    ? answer
    : answer.pipe(
        timeout(REQUEST_TIMEOUT_MS),
        tap({ error: (error: unknown) => announceTooManyRequests(error, auth) }),
      );
};

/**
 * A 429 too_many_requests (the limit per address, spec 0008): the notice bar says when to try again. It signs nobody out and
 * ends no session: only a 401 does.
 */
function announceTooManyRequests(error: unknown, auth: AuthService): void {
  if (error instanceof HttpErrorResponse && error.status === 429 && errorCode(error) === 'too_many_requests') {
    auth.showNotice('wait', retryAfterOf(error));
  }
}

function send(
  req: HttpRequest<unknown>,
  next: HttpHandlerFn,
  auth: AuthService,
  router: Router,
): Observable<HttpEvent<unknown>> {
  return defer(() => {
    const sentWith = auth.token();
    const sentIn = auth.session;
    // Only the last event: a caller that asks for `observe: 'events'` (upload progress) gets the final response alone from the
    // requests this interceptor handles. A product that needs the progress events keeps them with an operator of its own.
    return next(withToken(req, sentWith)).pipe(
      last(),
      catchError((error: unknown) => {
        if (!(error instanceof HttpErrorResponse)) {
          return throwError(() => error);
        }
        const permissionsChanged = error.status === 403 && errorCode(error) === 'permissions_changed';
        if (error.status !== 401 && !permissionsChanged) {
          announce(error, auth);
          return throwError(() => error);
        }
        return tokenIsRenewed(auth, router, sentWith, sentIn).pipe(
          switchMap((renewed) => {
            if (!renewed) {
              return throwError(() => error);
            }
            if (permissionsChanged && !isMe(req)) {
              // The role changed: the header and the forms follow it.
              void auth.loadMe();
            }
            return sendAgain(req, next, auth);
          }),
        );
      }),
    );
  });
}

/** True when the request may be sent again with the token the service holds now. */
function tokenIsRenewed(auth: AuthService, router: Router, sentWith: string | null, sentIn: number): Observable<boolean> {
  return defer(() => {
    const current = auth.token();
    if (current === null) {
      // The session ended while this request was away (a refresh that said 401 has dropped it, or the person signed out): the
      // person goes to /login, which does nothing when they are there already.
      goToLogin(router);
      return of(false);
    }
    if (auth.session !== sentIn) {
      // The session this request was sent in has ended and someone else has signed in since: the request is the old person's and
      // is never sent again with the new person's token. (A refresh keeps the session, so a renewed token is still the same one.)
      return of(false);
    }
    if (current !== sentWith) {
      return of(true); // another request renewed the token meanwhile
    }
    // One refresh at a time: the service shares a running one. What the answer means for the session (the bar, the way to /login)
    // is done here, in the promise, not in an operator: it must happen even when this caller has given up (timeout) and has
    // unsubscribed, or a session that ended would leave the person on a page that no longer works.
    const renewed = auth.refresh().then((result) => {
      if (result === 'ok') {
        return true;
      }
      if (result === 'rejected') {
        if (auth.token() === null) {
          goToLogin(router); // not when a new sign-in happened while the refresh was away: that session is fine
        }
      } else {
        if (auth.notice() !== 'wait') {
          auth.showNotice('unreachable'); // not over the "try again in N s." that a 429 of this refresh has put there
        }
      }
      return false;
    });
    return from(renewed);
  });
}

/** The second and last try: whatever it answers is the answer. */
function sendAgain(req: HttpRequest<unknown>, next: HttpHandlerFn, auth: AuthService): Observable<HttpEvent<unknown>> {
  return defer(() => next(withToken(req, auth.token()))).pipe(
    last(),
    tap({
      error: (error: unknown) => {
        if (error instanceof HttpErrorResponse) {
          announce(error, auth);
        }
      },
    }),
  );
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
    auth.showNotice('tryLater');
  }
}

function isMe(req: HttpRequest<unknown>): boolean {
  return ownUrl(req.url)?.pathname === '/auth/me';
}

function goToLogin(router: Router): void {
  // While a navigation runs, router.url is still the page the person is leaving: the page they are on the way to is the one
  // to come back to. A target that carries a mail token (/reset, /verify, /invite) is never put in the address of the login
  // screen: the person comes back to the page they are on, or to /notes. (Either way the guard and safeReturnUrl check what the
  // login screen follows.)
  const pending = router.currentNavigation();
  const candidates = [pending === null ? null : router.serializeUrl(pending.finalUrl ?? pending.extractedUrl), router.url];
  const here = candidates.find((url) => url !== null && !router.parseUrl(url).queryParamMap.has('token')) ?? '/notes';
  if (here.startsWith('/login')) {
    return;
  }
  void router.navigate(['/login'], { queryParams: { returnUrl: here } });
}
