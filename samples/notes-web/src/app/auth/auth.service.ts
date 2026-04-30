import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom, timeout } from 'rxjs';
import { ProviderName, providerNamesOf } from '../account-api';

/** What GET /auth/me answers. The app never decodes the access token: it asks. */
export interface Me {
  sub: string;
  email: string;
  org_id: string;
  org_name: string;
  /** "personal" for a private account (spec 0010), whose space is named "private"; "organization" for a company member. */
  org_kind: 'organization' | 'personal';
  roles: string[];
  permissions: string[];
  /** Spec 0011: false for an account made through Google or Microsoft that has never set a password. */
  has_password: boolean;
  /** The providers that sign in to this account, sorted (spec 0011). */
  linked_providers: ProviderName[];
}

/** What went wrong with a call to an account endpoint, as far as a screen cares. */
export type Failure =
  | { kind: 'invalid_credentials' }
  | { kind: 'email_not_verified' }
  | { kind: 'no_membership' }
  | { kind: 'invalid_token' }
  | { kind: 'weak_password'; rules: string[] }
  | { kind: 'already_member' }
  | { kind: 'already_registered' }
  | { kind: 'signup_disabled' }
  | { kind: 'wrong_password' }
  | { kind: 'private_account_exists' }
  | { kind: 'invalid_request' }
  | { kind: 'invalid_ticket' }
  | { kind: 'no_email' }
  | { kind: 'no_account' }
  | { kind: 'address_mismatch' }
  | { kind: 'provider_already_linked' }
  | { kind: 'reauth_failed' }
  | { kind: 'password_required' }
  | { kind: 'reauth_required' }
  | { kind: 'too_many_attempts'; retryAfterSeconds: number }
  | { kind: 'too_many_requests'; retryAfterSeconds: number }
  | { kind: 'other' };

/**
 * 'unavailable': the refresh could not be done and the session stays. 'limited': the same, and the answer was a 429 too_many_requests,
 * whose "Try again in N s." the interceptor has already put in the bar (the caller must not replace it).
 */
export type RefreshResult = 'ok' | 'rejected' | 'unavailable' | 'limited';

/** The messages of the bar above the screens. */
export type AuthNotice = 'unreachable' | 'tryLater' | 'forbidden' | 'wait';

export type LoginResult = { ok: true } | { ok: false; failure: Failure };

/** The next screen of a provider round trip, from POST /auth/providers/exchange (spec 0011). */
export type ProviderNext =
  | { next: 'link'; email: string; emailVerified: boolean }
  | { next: 'finish_signup'; email: string; kinds: ('organization' | 'private')[] };

/** `next: null`: the exchange signed the person in. */
export type ExchangeResult = { ok: true; next: ProviderNext | null } | { ok: false; failure: Failure };

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

/** The `retry_after_seconds` of a 429 body, rounded up to a whole second, or a minute when it has none that is a positive number. */
export function retryAfterOf(error: HttpErrorResponse): number {
  const seconds = isRecord(error.error) ? error.error['retry_after_seconds'] : undefined;
  return typeof seconds === 'number' && Number.isFinite(seconds) && seconds > 0 ? Math.ceil(seconds) : DEFAULT_WAIT_SECONDS;
}

/** Reads the answers of Auth-Core's account endpoints. Anything it does not know is "other". */
export function failureOf(error: unknown): Failure {
  if (!(error instanceof HttpErrorResponse)) {
    return { kind: 'other' };
  }
  const code = errorCode(error);
  if (error.status === 429 && code === 'too_many_attempts') {
    return { kind: 'too_many_attempts', retryAfterSeconds: retryAfterOf(error) };
  }
  if (error.status === 429 && code === 'too_many_requests') {
    return { kind: 'too_many_requests', retryAfterSeconds: retryAfterOf(error) };
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
  if (error.status === 409 && code === 'already_registered') {
    return { kind: 'already_registered' };
  }
  if (error.status === 403 && code === 'signup_disabled') {
    return { kind: 'signup_disabled' };
  }
  if (error.status === 403 && code === 'wrong_password') {
    return { kind: 'wrong_password' };
  }
  if (error.status === 409 && code === 'private_account_exists') {
    return { kind: 'private_account_exists' };
  }
  return providerFailureOf(error.status, code);
}

/** The answers of spec 0011, each with the status of the contract; anything else is "other". */
function providerFailureOf(status: number, code: string | undefined): Failure {
  if (status === 400 && code === 'invalid_ticket') {
    return { kind: 'invalid_ticket' };
  }
  if (status === 409 && code === 'provider_already_linked') {
    return { kind: 'provider_already_linked' };
  }
  if (status !== 403) {
    return { kind: 'other' };
  }
  if (code === 'no_email') {
    return { kind: 'no_email' };
  }
  if (code === 'no_account') {
    return { kind: 'no_account' };
  }
  if (code === 'address_mismatch') {
    return { kind: 'address_mismatch' };
  }
  if (code === 'reauth_failed') {
    return { kind: 'reauth_failed' };
  }
  if (code === 'password_required') {
    return { kind: 'password_required' };
  }
  if (code === 'reauth_required') {
    return { kind: 'reauth_required' };
  }
  return { kind: 'other' };
}

/**
 * Like `failureOf`, for the sign-up forms: a `400 invalid_request` is a failure of its own there, because each of those screens has a text
 * for it (the sign-in and the other mail-link screens read it as "other").
 */
export function formFailureOf(error: unknown): Failure {
  return error instanceof HttpErrorResponse && error.status === 400 && errorCode(error) === 'invalid_request'
    ? { kind: 'invalid_request' }
    : failureOf(error);
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

/** A `next` answer of the exchange in the contract's shape, or null. `kinds` keeps the known kinds, organization first. */
function asNext(body: unknown): ProviderNext | null {
  if (!isRecord(body) || typeof body['email'] !== 'string') {
    return null;
  }
  if (body['next'] === 'link' && typeof body['email_verified'] === 'boolean') {
    return { next: 'link', email: body['email'], emailVerified: body['email_verified'] };
  }
  const kinds: unknown = body['kinds'];
  if (body['next'] === 'finish_signup' && Array.isArray(kinds)) {
    const known = (['organization', 'private'] as const).filter((kind) => kinds.includes(kind));
    return known.length === 0 ? null : { next: 'finish_signup', email: body['email'], kinds: known };
  }
  return null;
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
      org_kind: body['org_kind'] === 'personal' ? 'personal' : 'organization',
      roles: body['roles'],
      permissions: body['permissions'],
      // Anything but false keeps the password screens of spec 0010: an Auth-Core before spec 0011 does not send the member.
      has_password: body['has_password'] !== false,
      linked_providers: providerNamesOf(body['linked_providers']),
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
  private readonly noticeSecondsState = signal(0);
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
  /** The number of the 'wait' notice: the seconds the server asked the person to wait. */
  readonly noticeSeconds = this.noticeSecondsState.asReadonly();

  /** Run once before the first route is resolved (app.config.ts): is there a session to resume? */
  async start(): Promise<void> {
    const result = await this.refresh();
    // 'limited' sets nothing: the bar already says "Try again in N s." for the 429 of this very refresh.
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
    return this.startSession(body);
  }

  /**
   * Finishes a self-service sign-up with the token of the mail link. Auth-Core answers as it answers a login (spec 0009, Amendment 2): the
   * access token in the body and the refresh cookie, so the person is signed in at once and the screen opens the product. A refusal leaves
   * the session that exists alone, as a failed login does. A private sign-up (spec 0010) names no company: pass `null` and the member is
   * left out of the body.
   */
  async completeSignup(token: string, password: string, companyName: string | null): Promise<LoginResult> {
    let body: LoginBody | null;
    const request = companyName === null ? { token, password } : { token, password, company_name: companyName };
    try {
      body = await firstValueFrom(this.http.post<LoginBody | null>('/auth/signup/complete', request));
    } catch (error) {
      return { ok: false, failure: formFailureOf(error) };
    }
    return this.startSession(body);
  }

  /**
   * POST /auth/providers/exchange (spec 0011): the ticket of the return screen. Either the person is signed in, as at a login, or the answer
   * names the next screen. The auth_pt cookie goes with the request by itself (same origin); no access token is needed.
   */
  async exchangeTicket(ticket: string): Promise<ExchangeResult> {
    let body: unknown;
    try {
      body = await firstValueFrom(this.http.post<unknown>('/auth/providers/exchange', { ticket }));
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
    if (isRecord(body) && 'next' in body) {
      const next = asNext(body);
      return next === null ? { ok: false, failure: { kind: 'other' } } : { ok: true, next };
    }
    const signedIn = await this.startSession(isRecord(body) ? body : null);
    return signedIn.ok ? { ok: true, next: null } : signedIn;
  }

  /** POST /auth/providers/link: the password of the account once, then the provider alone signs in (spec 0011). */
  async linkProvider(ticket: string, password: string): Promise<LoginResult> {
    let body: LoginBody | null;
    try {
      body = await firstValueFrom(this.http.post<LoginBody | null>('/auth/providers/link', { ticket, password }));
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
    return this.startSession(body);
  }

  /** POST /auth/providers/finish: a new account with the provider's address and no password (spec 0011). */
  async finishWithProvider(ticket: string, kind: 'organization' | 'private', companyName: string | null): Promise<LoginResult> {
    let body: LoginBody | null;
    const request = companyName === null ? { ticket, kind } : { ticket, kind, company_name: companyName };
    try {
      body = await firstValueFrom(this.http.post<LoginBody | null>('/auth/providers/finish', request));
    } catch (error) {
      return { ok: false, failure: formFailureOf(error) };
    }
    return this.startSession(body);
  }

  /** What a sign-in that Auth-Core answered with `200` does: a session of its own, and who it is for. */
  private async startSession(body: LoginBody | null): Promise<LoginResult> {
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
   * Deletes the private account of the person who is signed in (spec 0010): DELETE /auth/account with the password. On `204` the session is
   * over here too (Auth-Core has cleared the cookie). The product deletes its own data before it calls this: Auth-Core does not tell it.
   */
  async deleteAccount(password: string): Promise<LoginResult> {
    try {
      await firstValueFrom(this.http.delete('/auth/account', { body: { password } }));
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
    this.dropSession();
    return { ok: true };
  }

  /**
   * Spec 0011: the same deletion for an account without a password, confirmed by a provider round trip with purpose=reauth. The ticket is
   * spent by this call; the auth_pt cookie goes with it by itself.
   */
  async deleteAccountWithTicket(ticket: string): Promise<LoginResult> {
    try {
      await firstValueFrom(this.http.delete('/auth/account', { body: { provider_ticket: ticket } }));
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
    this.dropSession();
    return { ok: true };
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

  showNotice(notice: AuthNotice, seconds = 0): void {
    this.noticeSecondsState.set(seconds);
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
      if (error instanceof HttpErrorResponse && error.status === 429 && errorCode(error) === 'too_many_requests') {
        return 'limited';
      }
      return 'unavailable';
    }
  }
}
