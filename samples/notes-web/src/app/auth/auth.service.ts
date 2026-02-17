import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom, timeout } from 'rxjs';

/** What GET /auth/me answers. The app never decodes the access token: it asks. */
export interface Me {
  sub: string;
  email: string;
  org_id: string;
  org_name: string;
  roles: string[];
  permissions: string[];
}

/** What went wrong with a call to an account endpoint, as far as a screen cares. */
export type Failure =
  | { kind: 'invalid_credentials' }
  | { kind: 'email_not_verified' }
  | { kind: 'no_membership' }
  | { kind: 'invalid_token' }
  | { kind: 'weak_password'; rules: string[] }
  | { kind: 'already_member' }
  | { kind: 'too_many_attempts'; retryAfterSeconds: number }
  | { kind: 'other' };

export type RefreshResult = 'ok' | 'rejected' | 'unavailable';

/** The messages of the bar above the screens. */
export type AuthNotice = 'unreachable' | 'tryLater' | 'forbidden';

export type LoginResult = { ok: true } | { ok: false; failure: Failure };

const REFRESH_TIMEOUT_MS = 10_000;
const DEFAULT_WAIT_SECONDS = 60;

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** The "error" of a JSON error body (`{"error":"forbidden"}`), or undefined when the body is anything else. */
export function errorCode(error: HttpErrorResponse): string | undefined {
  const body: unknown = error.error;
  return isRecord(body) && typeof body['error'] === 'string' ? body['error'] : undefined;
}

/** Reads the answers of Auth-Core's account endpoints. Anything it does not know is "other". */
export function failureOf(error: unknown): Failure {
  if (!(error instanceof HttpErrorResponse)) {
    return { kind: 'other' };
  }
  const code = errorCode(error);
  if (error.status === 429 && code === 'too_many_attempts') {
    const seconds = isRecord(error.error) ? error.error['retry_after_seconds'] : undefined;
    return {
      kind: 'too_many_attempts',
      retryAfterSeconds: typeof seconds === 'number' && seconds > 0 ? seconds : DEFAULT_WAIT_SECONDS,
    };
  }
  if (error.status === 401 && code === 'invalid_credentials') {
    return { kind: 'invalid_credentials' };
  }
  if (error.status === 403 && code === 'email_not_verified') {
    return { kind: 'email_not_verified' };
  }
  if (error.status === 403 && code === 'no_membership') {
    return { kind: 'no_membership' };
  }
  if (error.status === 400 && code === 'invalid_token') {
    return { kind: 'invalid_token' };
  }
  if (error.status === 400 && code === 'weak_password') {
    const rules = isRecord(error.error) && Array.isArray(error.error['rules']) ? error.error['rules'] : [];
    return { kind: 'weak_password', rules: rules.filter((rule): rule is string => typeof rule === 'string') };
  }
  if (error.status === 409 && code === 'already_member') {
    return { kind: 'already_member' };
  }
  return { kind: 'other' };
}

interface LoginBody {
  status?: unknown;
  access_token?: unknown;
}

interface RefreshBody {
  access_token?: unknown;
}

function isStrings(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === 'string');
}

/** The answer of GET /auth/me if it has the shape of the contract, else null: a page of HTML from a proxy is not a person. */
function asMe(body: unknown): Me | null {
  if (
    isRecord(body) &&
    typeof body['sub'] === 'string' &&
    typeof body['email'] === 'string' &&
    typeof body['org_id'] === 'string' &&
    typeof body['org_name'] === 'string' &&
    isStrings(body['roles']) &&
    isStrings(body['permissions'])
  ) {
    return {
      sub: body['sub'],
      email: body['email'],
      org_id: body['org_id'],
      org_name: body['org_name'],
      roles: body['roles'],
      permissions: body['permissions'],
    };
  }
  return null;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly accessToken = signal<string | null>(null);
  private readonly meState = signal<Me | null>(null);
  private readonly noticeState = signal<AuthNotice | null>(null);
  private refreshing: Promise<RefreshResult> | null = null;
  // The /auth/me that is on its way, and the session and token it was asked for.
  private asking: { generation: number; token: string | null; answer: Promise<boolean> } | null = null;
  // Counts the sessions that ended in this tab: an answer that was on its way when the person signed out is never used.
  private generation = 0;

  /** The access token, or null when nobody is signed in. Held here, in memory, and nowhere else. */
  readonly token = this.accessToken.asReadonly();
  /** Who is signed in, as GET /auth/me says. */
  readonly me = this.meState.asReadonly();
  /** The message of the bar, if there is one. */
  readonly notice = this.noticeState.asReadonly();

  /** Run once before the first route is resolved (app.config.ts): is there a session to resume? */
  async start(): Promise<void> {
    const result = await this.refresh();
    if (result === 'unavailable') {
      this.noticeState.set('unreachable');
    } else if (result === 'ok' && !(await this.loadMe(REFRESH_TIMEOUT_MS)) && this.noticeState() === null) {
      // The same ten seconds as the refresh: the app must not sit blank behind the initializer. Not when the interceptor has
      // already set a bar of its own for this /auth/me (503 auth_unavailable: "Try again shortly."): that one is the better one.
      this.noticeState.set('unreachable');
    }
  }

  async login(email: string, password: string): Promise<LoginResult> {
    let body: LoginBody | null;
    try {
      body = await firstValueFrom(this.http.post<LoginBody | null>('/auth/login', { email, password }));
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
    const token = body?.access_token;
    if (body?.status !== 'authenticated' || typeof token !== 'string' || token === '') {
      return { ok: false, failure: { kind: 'other' } };
    }
    // A session that exists ends first (/login has no guard, so a signed-in person can sign in as someone else): what the old
    // person asked for and is still on its way, an /auth/me or a request that gets a 401, is never used for the new one.
    this.dropSession();
    this.accessToken.set(token);
    this.noticeState.set(null);
    // Signed in once there is a token, even when this answer cannot be read: the notes screen asks for the person again.
    // Ten seconds, like the start: a sign-in must not hang behind an /auth/me that never answers.
    await this.loadMe(REFRESH_TIMEOUT_MS);
    return { ok: true };
  }

  /** Ends the session: whatever the server answers, the token is dropped. */
  async logout(): Promise<void> {
    try {
      await firstValueFrom(this.http.post('/auth/logout', null));
    } catch {
      // The answer does not matter: this tab is signed out either way.
    }
    this.dropSession();
  }

  /**
   * Exchanges the refresh cookie for a new access token. Callers that ask while a refresh runs get that refresh:
   * there is never more than one request at a time. A refresh that started before the person signed out hands its callers
   * 'rejected' when it ends, whatever the cookie said: the session it was for is gone (the interceptor then leaves the page alone).
   */
  refresh(): Promise<RefreshResult> {
    this.refreshing ??= this.exchange().finally(() => {
      this.refreshing = null;
    });
    return this.refreshing;
  }

  /**
   * Asks GET /auth/me. True when an answer of the contract's shape was read. `giveUpAfterMs` ends the wait for an answer that
   * does not come (the start uses it); an answer that arrives after the person signed out is ignored.
   */
  loadMe(giveUpAfterMs?: number): Promise<boolean> {
    // Several callers at once (a page of requests that all got 403 permissions_changed) share one request, as long as it is for
    // the same session and the same token: the answer would be the same.
    const token = this.accessToken();
    if (this.asking === null || this.asking.generation !== this.generation || this.asking.token !== token) {
      const answer = this.askMe(giveUpAfterMs).finally(() => {
        if (this.asking?.answer === answer) {
          this.asking = null;
        }
      });
      this.asking = { generation: this.generation, token, answer };
    }
    return this.asking.answer;
  }

  private async askMe(giveUpAfterMs?: number): Promise<boolean> {
    const started = this.generation;
    try {
      const request = this.http.get<unknown>('/auth/me');
      const body = await firstValueFrom(giveUpAfterMs === undefined ? request : request.pipe(timeout(giveUpAfterMs)));
      const me = asMe(body);
      if (me === null || this.generation !== started) {
        return false;
      }
      this.meState.set(me);
      return true;
    } catch {
      return false;
    }
  }

  /** Counts the sessions that ended in this tab. A request that was sent in an older one is never sent again with a newer token. */
  get session(): number {
    return this.generation;
  }

  dropSession(): void {
    this.generation += 1;
    this.accessToken.set(null);
    this.meState.set(null);
  }

  showNotice(notice: AuthNotice): void {
    this.noticeState.set(notice);
  }

  clearNotice(): void {
    this.noticeState.set(null);
  }

  private async exchange(): Promise<RefreshResult> {
    const started = this.generation;
    try {
      const body = await firstValueFrom(
        this.http.post<RefreshBody | null>('/auth/refresh', null).pipe(timeout(REFRESH_TIMEOUT_MS)),
      );
      if (this.generation !== started) {
        // The person signed out while this was on its way: the session it would renew is gone. (The interceptor then goes
        // to /login, where the person already is or is on the way.)
        return 'rejected';
      }
      const token = body?.access_token;
      if (typeof token !== 'string' || token === '') {
        return 'unavailable';
      }
      this.accessToken.set(token);
      return 'ok';
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 401) {
        if (this.generation === started) {
          // Only the session this refresh was for is dropped: a person who signed out and in again meanwhile keeps the new one.
          this.dropSession();
        }
        return 'rejected';
      }
      return 'unavailable';
    }
  }
}
