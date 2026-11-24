import { HttpClient, HttpContext, HttpContextToken, HttpErrorResponse } from '@angular/common/http';
import { Injectable, Injector, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom, timeout } from 'rxjs';
import { AccountApi, OrgEntry, ProviderName, providerNamesOf } from '../account-api';

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
  /** Spec 0011: false for an account made through a provider that has never set a password. */
  has_password: boolean;
  /** The providers that sign in to this account, sorted (spec 0011). */
  linked_providers: ProviderName[];
  /** Spec 0012: the end of the confirmation window of this device's session, UTC with seconds, or null when it is closed. */
  confirmed_until: string | null;
}

/** Marks the request of an unlink or a password change: it is the one request to /auth/ that is not held while it runs (spec 0012). */
export const RENEWAL = new HttpContextToken<boolean>(() => false);

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
  | { kind: 'private_account_exists' } // spec 0017: the server no longer sends it; kept for an older server
  | { kind: 'invalid_request' }
  | { kind: 'invalid_ticket' }
  | { kind: 'no_email' }
  | { kind: 'no_account' }
  | { kind: 'address_mismatch' }
  | { kind: 'provider_already_linked' }
  | { kind: 'reauth_failed' }
  | { kind: 'password_required' }
  | { kind: 'reauth_required' }
  | { kind: 'session_required' }
  | { kind: 'confirmation_required' }
  | { kind: 'provider_in_use' }
  | { kind: 'last_login' }
  | { kind: 'password_not_set' }
  | { kind: 'not_found' }
  | { kind: 'org_creation_disabled' }
  | { kind: 'company_limit_reached' }
  | { kind: 'company_member' }
  | { kind: 'permissions_changed' }
  | { kind: 'too_many_attempts'; retryAfterSeconds: number }
  | { kind: 'too_many_requests'; retryAfterSeconds: number }
  | { kind: 'other' };

/**
 * 'unavailable': the refresh could not be done and the session stays. 'limited': the same, and the answer was a 429 too_many_requests,
 * whose "Try again in N s." the interceptor has already put in the bar (the caller must not replace it).
 */
export type RefreshResult = 'ok' | 'rejected' | 'unavailable' | 'limited';

/** The messages of the bar above the screens. */
export type AuthNotice = 'unreachable' | 'tryLater' | 'forbidden' | 'wait' | 'switched';

export type LoginResult = { ok: true } | { ok: false; failure: Failure };

/** What `switchTo` came to: the session is in the other space; the account is not a member there any more; the session is over; nothing changed. */
export type SwitchResult = 'switched' | 'gone' | 'signed-out' | 'failed';

/**
 * What `createCompany` came to (spec 0016); 'created' means the session is now in the new company. 'changed': the POST answered
 * `permissions_changed` (the caller asks for the list again). 'gone': the switch found the new company gone (the list is already asked again).
 * 'switch-failed': the company was created but the switch did not go through (the caller asks for the list again). 'failed': anything else.
 */
export type CreateCompanyResult = 'created' | 'limit' | 'disabled' | 'invalid' | 'changed' | 'gone' | 'switch-failed' | 'failed';

/** The next screen of a provider round trip, from POST /auth/providers/exchange (spec 0011). */
export type ProviderNext =
  | { next: 'link'; email: string; emailVerified: boolean }
  | { next: 'finish_signup'; email: string; kinds: ('organization' | 'private')[] };

/** `next: null`: the exchange signed the person in. */
export type ExchangeResult = { ok: true; next: ProviderNext | null } | { ok: false; failure: Failure };

export const REFRESH_TIMEOUT_MS = 10_000;
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
  if (error.status === 403 && (code === 'org_creation_disabled' || code === 'company_member' || code === 'permissions_changed')) {
    return { kind: code };
  }
  if (error.status === 409 && code === 'company_limit_reached') {
    return { kind: 'company_limit_reached' };
  }
  return providerFailureOf(error.status, code);
}

/** The answers of specs 0011 and 0012, each with the status of the contract; anything else is "other". */
function providerFailureOf(status: number, code: string | undefined): Failure {
  if (status === 400 && code === 'invalid_ticket') {
    return { kind: 'invalid_ticket' };
  }
  if (status === 409 && code === 'provider_already_linked') {
    return { kind: 'provider_already_linked' };
  }
  // Spec 0012: the settings screen.
  if (status === 409 && (code === 'provider_in_use' || code === 'last_login')) {
    return { kind: code };
  }
  if (status === 401 && code === 'session_required') {
    return { kind: 'session_required' };
  }
  if (status === 404 && code === 'not_found') {
    return { kind: 'not_found' };
  }
  if (status !== 403) {
    return { kind: 'other' };
  }
  if (code === 'confirmation_required' || code === 'password_not_set') {
    return { kind: code };
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
      // Anything but a string is a closed window: an Auth-Core before spec 0012 does not send the member.
      confirmed_until: typeof body['confirmed_until'] === 'string' ? body['confirmed_until'] : null,
    };
  }
  return null;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly api = inject(AccountApi);
  private readonly injector = inject(Injector);
  private readonly accessToken = signal<string | null>(null);
  private readonly meState = signal<Me | null>(null);
  private readonly noticeState = signal<AuthNotice | null>(null);
  private readonly noticeSecondsState = signal(0);
  private readonly noticeNameState = signal<string | null>(null);
  private readonly orgsState = signal<OrgEntry[]>([]);
  private readonly orgChangesState = signal(0);
  private refreshing: Promise<RefreshResult> | null = null;
  // The refresh that the 401 of a renewal's own request starts (spec 0012): never shared with a plain refresh.
  private renewalRefreshing: Promise<RefreshResult> | null = null;
  // Spec 0012: an unlink or a password change in flight. Its answer replaces the refresh cookie, so nothing else that carries it may leave.
  private renewing: Promise<void> | null = null;
  // The call of /auth/switch that is on its way. It spends the same refresh cookie as a refresh, so the two never run together: a refresh
  // asked meanwhile waits for it (and takes its token), a switch asked while a refresh runs waits for that.
  private switching: Promise<SwitchResult> | null = null;
  // The /auth/me that is on its way, and the session and token it was asked for.
  private asking: { generation: number; token: string | null; answer: Promise<boolean> } | null = null;
  // Counts the sessions that ended in this tab: an answer that was on its way when the person signed out is never used.
  private generation = 0;
  // Counts the switches this tab made: a refresh that finds another company while one of them runs is not news from another tab.
  private switches = 0;

  /** The access token, or null when nobody is signed in. Held here, in memory, and nowhere else. */
  readonly token = this.accessToken.asReadonly();
  /** Who is signed in, as GET /auth/me says. */
  readonly me = this.meState.asReadonly();
  /** The message of the bar, if there is one. */
  readonly notice = this.noticeState.asReadonly();
  /** The number of the 'wait' notice: the seconds the server asked the person to wait. */
  readonly noticeSeconds = this.noticeSecondsState.asReadonly();
  /** The spaces of the account, as the last GET /auth/orgs listed them (empty before the first answer). */
  readonly orgs = this.orgsState.asReadonly();
  /** Goes up by one whenever the company of the session changed, here or in another tab: the notes page reloads on it. */
  readonly orgChanges = this.orgChangesState.asReadonly();
  /** The space the 'switched' notice names; null for the personal space. */
  readonly noticeName = this.noticeNameState.asReadonly();

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
    if (result === 'ok' && this.meState() !== null) {
      void this.loadOrgs();
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
    // The picker of the account bar: not waited for, a sign-in does not hang behind it.
    void this.loadOrgs();
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

  /** The unlink or the password change in flight, or null: the interceptor holds every other request to /auth/ until it settles. */
  get renewal(): Promise<void> | null {
    return this.renewing;
  }

  /** POST /auth/account/confirm with the password (spec 0012): the window of this device's session opens for five minutes. */
  confirmPassword(password: string): Promise<LoginResult> {
    return this.settingsCall('/auth/account/confirm', { password });
  }

  /** The same with the ticket of a `reauth` round trip; the auth_pt cookie goes with it by itself. */
  confirmWithTicket(ticket: string): Promise<LoginResult> {
    return this.settingsCall('/auth/account/confirm', { provider_ticket: ticket });
  }

  /** POST /auth/providers/connect with the ticket of a `connect` round trip. */
  connect(ticket: string): Promise<LoginResult> {
    return this.settingsCall('/auth/providers/connect', { ticket });
  }

  /** POST /auth/account/logins/unlink: every other device is signed out, and this one gets a new session in the answer. */
  unlink(provider: string): Promise<LoginResult> {
    return this.renewSession('/auth/account/logins/unlink', { provider });
  }

  /** POST /auth/account/password, against the current password: as the unlink, a new session for this device. */
  changePassword(current: string, next: string): Promise<LoginResult> {
    return this.renewSession('/auth/account/password', { current_password: current, new_password: next });
  }

  /** A settings call that answers 204: then the person is read again (the window, the logins). */
  private async settingsCall(url: string, body: unknown): Promise<LoginResult> {
    try {
      await firstValueFrom(this.http.post(url, body));
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
    await this.loadMe(REFRESH_TIMEOUT_MS);
    return { ok: true };
  }

  /**
   * A call whose answer is a login's (spec 0012 -> Sessions): the new access token is stored in the same session of this tab (the refresh cookie
   * is already the new one). Until it is stored, every other request to /auth/ waits, so that none carries the cookie the answer replaces.
   * It starts only when nothing else that spends the refresh cookie is on its way: a switch (its token must be the one the call is sent with),
   * a refresh, or another renewal.
   */
  private async renewSession(url: string, body: unknown): Promise<LoginResult> {
    // The wait and the claim of `renewing` are one step with no await between them, so two waiters never both go through.
    while (this.switching !== null || this.refreshing !== null || this.renewing !== null) {
      await (this.switching ?? this.refreshing ?? this.renewing)?.catch(() => undefined);
    }
    const started = this.generation;
    let release = (): void => undefined;
    this.renewing = new Promise<void>((resolve) => (release = resolve));
    let result: LoginResult;
    try {
      const answer = await firstValueFrom(
        this.http.post<LoginBody | null>(url, body, { context: new HttpContext().set(RENEWAL, true) }),
      );
      const token = answer?.access_token;
      if (answer?.status !== 'authenticated' || typeof token !== 'string' || token === '' || this.generation !== started) {
        result = { ok: false, failure: { kind: 'other' } };
      } else {
        this.accessToken.set(token);
        result = { ok: true };
      }
    } catch (error) {
      result = { ok: false, failure: failureOf(error) };
    } finally {
      this.renewing = null;
      release();
    }
    if (result.ok) {
      await this.loadMe(REFRESH_TIMEOUT_MS);
    }
    return result;
  }

  /**
   * Exchanges the refresh cookie for a new access token. Callers that ask while a refresh runs get that refresh:
   * there is never more than one request at a time. A refresh that started before the person signed out hands its callers
   * 'rejected' when it ends, whatever the cookie said: the session it was for is gone (the interceptor then leaves the page alone).
   * A switch that is on its way (spec 0015) spends the same refresh cookie: the refresh waits for it and takes its token ('ok'), or its end
   * of the session ('rejected'); when the switch renewed nothing (404, 503) the refresh goes on to its own request.
   */
  refresh(forRenewal = false): Promise<RefreshResult> {
    if (forRenewal) {
      // Never shared with a plain refresh: one started while the renewal runs has its request held behind the renewal, and the renewal's
      // own refresh would wait for it (until its 10 seconds) if it joined it. Its own promise, its own request, marked as part of the renewal.
      this.renewalRefreshing ??= this.renew(true).finally(() => {
        this.renewalRefreshing = null;
      });
      return this.renewalRefreshing;
    }
    this.refreshing ??= this.renew(false).finally(() => {
      this.refreshing = null;
    });
    return this.refreshing;
  }

  private async renew(forRenewal: boolean): Promise<RefreshResult> {
    const asked = this.generation;
    // The refresh of a renewal's own 401 does not wait for a switch: the switch's request is held by the interceptor until the renewal is over,
    // so waiting for it would wait for the renewal itself (spec 0012). The gate already keeps the cookie safe.
    while (!forRenewal && this.switching !== null) {
      const switched = await this.switching;
      if (switched === 'switched') {
        return 'ok';
      }
      // The session this refresh was asked in is over. A switch of an older session that ends 'signed-out' leaves a newer one alone.
      if (switched === 'signed-out' && this.generation !== asked) {
        return 'rejected';
      }
    }
    return this.exchange(forRenewal);
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

  /** Asks GET /auth/orgs and keeps the list; an answer that cannot be read, or that comes after the session ended, leaves the last list. */
  async loadOrgs(): Promise<void> {
    const started = this.generation;
    const outcome = await this.api.listOrgs();
    if (outcome.ok && this.generation === started) {
      this.orgsState.set(outcome.value);
    }
  }

  /**
   * Moves the session to another space of the account (POST /auth/switch, spec 0015): the answer is a renewed token for the chosen space, so
   * it is kept as the same session (the generation does not change) and the person, the list and the page follow it. `404`: the account is
   * not a member there any more, the list is asked again. `401`: the session cannot go on; /login opens, and there is no refresh and no
   * second try, because this call spends the refresh token itself.
   */
  async switchTo(orgId: string): Promise<SwitchResult> {
    const started = this.generation;
    // A refresh or another switch that is on its way spends the same refresh cookie, and its token would replace the switched one when it
    // lands: wait for it. Only one of them holds the cookie at a time.
    while (this.refreshing !== null || this.switching !== null) {
      await (this.switching ?? this.refreshing);
    }
    if (this.generation !== started) {
      return 'signed-out'; // the refresh found the session over
    }
    // The slot is held for the call and the keeping of its token only: /auth/me and the list below may need a refresh of their own.
    const call = this.callSwitch(orgId, started);
    // The slot's promise is only ever awaited by a waiter; `call` is awaited below, so a rejection reaches the caller of `switchTo` once.
    // The catch keeps the copy kept in the slot from being a second, unobserved rejection when no waiter joins.
    this.switching = call
      .finally(() => {
        this.switching = null;
      })
      .catch(() => 'failed');
    const result = await call;
    if (result === 'gone') {
      await this.loadOrgs();
    } else if (result === 'switched') {
      await this.loadMe();
      await this.loadOrgs();
      this.orgChangesState.update((count) => count + 1);
    }
    return result;
  }

  /**
   * Creates another company for the account (POST /auth/orgs, spec 0016) and moves the session into it with `switchTo`, as the picker does.
   * 'limit', 'disabled', 'invalid' and 'changed' (`permissions_changed`: the caller refreshes) mean nothing was created and nothing was
   * switched. After the `201` a `switchTo` that does not end in the company is 'gone' (a `404`), 'switch-failed' (any other failed answer) or
   * 'failed' (the session ended: `switchTo` has opened /login itself); the company exists then and the next list shows it.
   */
  async createCompany(name: string): Promise<CreateCompanyResult> {
    const outcome = await this.api.createOrg(name);
    if (!outcome.ok) {
      switch (outcome.failure.kind) {
        case 'company_limit_reached':
          return 'limit';
        case 'org_creation_disabled':
          return 'disabled';
        case 'invalid_request':
          return 'invalid';
        case 'permissions_changed':
          return 'changed';
        default:
          return 'failed';
      }
    }
    const result = await this.switchTo(outcome.value.id);
    if (result === 'switched') {
      return 'created';
    }
    if (result === 'gone') {
      return 'gone';
    }
    return result === 'failed' ? 'switch-failed' : 'failed';
  }

  /** The request of `switchTo` and what the session does with its answer; 'switched' means the new token is kept. */
  private async callSwitch(orgId: string, started: number): Promise<SwitchResult> {
    const outcome = await this.api.switchOrg(orgId);
    if (this.generation !== started) {
      return 'signed-out'; // the session this was for ended while the call was away: its token is never kept
    }
    if (outcome.result === 'rejected') {
      this.dropSession();
      void this.injector.get(Router).navigateByUrl('/login');
      return 'signed-out';
    }
    if (outcome.result === 'gone') {
      return 'gone';
    }
    if (outcome.result === 'failed') {
      return 'failed';
    }
    this.switches += 1;
    this.accessToken.set(outcome.token);
    this.noticeState.set(null);
    return 'switched';
  }

  /** Counts the sessions that ended in this tab. A request that was sent in an older one is never sent again with a newer token. */
  get session(): number {
    return this.generation;
  }

  dropSession(): void {
    this.generation += 1;
    this.accessToken.set(null);
    this.meState.set(null);
    this.orgsState.set([]);
  }

  showNotice(notice: AuthNotice, seconds = 0): void {
    this.noticeSecondsState.set(seconds);
    this.noticeState.set(notice);
  }

  /** The bar says that another tab moved the session to a space (`null`: the personal space). */
  showSwitchedNotice(name: string | null): void {
    this.noticeNameState.set(name);
    this.noticeState.set('switched');
  }

  clearNotice(): void {
    this.noticeState.set(null);
  }

  private async exchange(forRenewal: boolean): Promise<RefreshResult> {
    const started = this.generation;
    try {
      // The refresh that the 401 of a renewal's own request starts is marked as part of the renewal, so that the interceptor does not hold it
      // behind that request (spec 0012). No other caller passes `forRenewal`: theirs wait for the renewal as before.
      const options = forRenewal ? { context: new HttpContext().set(RENEWAL, true) } : {};
      const body = await firstValueFrom(
        this.http.post<RefreshBody | null>('/auth/refresh', null, options).pipe(timeout(REFRESH_TIMEOUT_MS)),
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
      const before = this.meState()?.org_id ?? null;
      if (before !== null) {
        // Not waited for: the refresh is done, and a caller that is waiting for a token must not wait for a second request.
        void this.noticeAnotherTab(before, started);
      }
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

  /**
   * The refresh cookie is shared by every tab of the browser, so another tab that switched company moves this session too, at its next
   * refresh. After a refresh, `/auth/me` says which company the token is for: when it is not the one the page showed, the notes are read again,
   * the list is asked again and the bar says so. A switch made in this tab is not news.
   */
  private async noticeAnotherTab(before: string, started: number): Promise<void> {
    const switches = this.switches;
    if (!(await this.loadMe()) || this.generation !== started || this.switches !== switches) {
      return;
    }
    const person = this.meState();
    if (person === null || person.org_id === before) {
      return;
    }
    await this.loadOrgs();
    if (this.generation !== started) {
      return;
    }
    this.orgChangesState.update((count) => count + 1);
    this.showSwitchedNotice(person.org_kind === 'personal' ? null : person.org_name);
  }
}
