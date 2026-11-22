import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, timeout } from 'rxjs';
import { Failure, REFRESH_TIMEOUT_MS, failureOf, formFailureOf, isRecord } from './auth/auth.service';

/** How long the screens wait for GET /auth/options: a call that is slower counts as failed, as the refresh does (ten seconds). */
export const OPTIONS_TIMEOUT_MS = 10_000;

export type Outcome<T = void> = { ok: true; value: T } | { ok: false; failure: Failure };

/** What POST /auth/invites/preview answers: who invites, for whom, as what, and whether the address has a private account (spec 0010). */
export interface InvitePreview {
  org_name: string;
  email: string;
  role: string;
  has_private_account: boolean;
}

/** The sign-in providers this app has buttons for (spec 0011). */
export const PROVIDER_NAMES = ['google', 'microsoft'] as const;

export type ProviderName = (typeof PROVIDER_NAMES)[number];

export function isProviderName(value: unknown): value is ProviderName {
  return value === 'google' || value === 'microsoft';
}

/** The names of a JSON value that this app knows, in their order, each once: anything else is dropped. */
export function providerNamesOf(value: unknown): ProviderName[] {
  if (!Array.isArray(value)) {
    return [];
  }
  return value.filter(isProviderName).filter((name, index, all) => all.indexOf(name) === index);
}

/**
 * The address of a provider button: Auth-Core's start, plain, with the invitation token, or for the confirmation of a deletion (spec 0011).
 * A link, not a call: the browser leaves the app for the provider and comes back to /provider-return.
 */
export function providerStartUrl(name: ProviderName, how: { invite: string } | { reauth: true } | null = null): string {
  const start = `/auth/providers/${name}/start`;
  if (how === null) {
    return start;
  }
  return 'invite' in how ? `${start}?invite=${encodeURIComponent(how.invite)}` : `${start}?purpose=reauth`;
}

/**
 * What GET /auth/options answers, as far as the screens care: whether a visitor may start a company, or sign up alone (spec 0010), and the
 * providers that have a button, in Auth-Core's order (spec 0011).
 */
export interface SiteOptions {
  signup: 'self-service' | 'invite-only';
  private_signup: 'self-service' | 'off';
  providers: ProviderName[];
  /** Whether a signed-in account may create another company (spec 0016); `false` when the member is absent (an older server). */
  orgCreation: boolean;
}

/** A company of one's own, or a private account (spec 0010). */
export type SignupKind = 'organization' | 'private';

/**
 * What POST /auth/signup/preview answers: the kind, the address as it was typed and, for an organization, the company name of the
 * request.
 */
export interface SignupPreview {
  kind: SignupKind;
  email: string;
  company_name: string | null;
}

/** One space of the account, as GET /auth/orgs lists it (spec 0015): a company, or the personal space. */
export interface OrgEntry {
  id: string;
  name: string;
  kind: 'organization' | 'personal';
  role: { id: string; name: string };
  /** True for the space the access token is for. */
  current: boolean;
}

/**
 * What POST /auth/switch came to. 'gone': 404, the account is no longer a member there. 'rejected': 401, the session cannot go on.
 * 'failed': anything else (an outage, a limit, an answer that is not a token): nothing changed.
 */
export type SwitchOutcome = { result: 'ok'; token: string } | { result: 'gone' } | { result: 'rejected' } | { result: 'failed' };

/** An entry of GET /auth/orgs in the contract's shape, or null. */
function asOrgEntry(value: unknown): OrgEntry | null {
  if (!isRecord(value)) {
    return null;
  }
  const role = value['role'];
  if (
    typeof value['id'] === 'string' &&
    typeof value['name'] === 'string' &&
    typeof value['current'] === 'boolean' &&
    isRecord(role) &&
    typeof role['id'] === 'string' &&
    typeof role['name'] === 'string'
  ) {
    return {
      id: value['id'],
      name: value['name'],
      kind: value['kind'] === 'personal' ? 'personal' : 'organization',
      role: { id: role['id'], name: role['name'] },
      current: value['current'],
    };
  }
  return null;
}

/** Auth-Core's endpoints for the mail links and the account, which need no access token. */
@Injectable({ providedIn: 'root' })
export class AccountApi {
  private readonly http = inject(HttpClient);

  /**
   * GET /auth/orgs (spec 0015): every space of the account. The interceptor adds the token (the `/auth/org` prefix). An answer that is not
   * a list in the contract's shape is a failure, not a list with holes.
   */
  async listOrgs(): Promise<Outcome<OrgEntry[]>> {
    const outcome = await this.get<unknown>('/auth/orgs');
    if (!outcome.ok) {
      return outcome;
    }
    const body = outcome.value;
    const listed: unknown = isRecord(body) ? body['orgs'] : undefined;
    if (Array.isArray(listed)) {
      const orgs = listed.map(asOrgEntry);
      if (orgs.every((entry) => entry !== null)) {
        return { ok: true, value: orgs };
      }
    }
    return { ok: false, failure: { kind: 'other' } };
  }

  /**
   * POST /auth/switch (spec 0015): the refresh cookie goes with it by itself, as with `/auth/refresh`; no access token is sent (the path is
   * in neither interceptor list), so a `401` is the session's end and is never retried after a refresh. It spends the refresh cookie, so it
   * waits as long as a refresh does (a refresh that joins it must not wait longer); an answer that does not come is a failure.
   */
  async switchOrg(orgId: string): Promise<SwitchOutcome> {
    try {
      const body = await firstValueFrom(this.http.post<unknown>('/auth/switch', { org_id: orgId }).pipe(timeout(REFRESH_TIMEOUT_MS)));
      const token = isRecord(body) ? body['access_token'] : undefined;
      return typeof token === 'string' && token !== '' ? { result: 'ok', token } : { result: 'failed' };
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 404) {
        return { result: 'gone' };
      }
      return error instanceof HttpErrorResponse && error.status === 401 ? { result: 'rejected' } : { result: 'failed' };
    }
  }

  /**
   * POST /auth/orgs (spec 0016): another company for the signed-in account. The interceptor adds the token (the `/auth/org` prefix). The
   * `201` body must be an entry in the contract's shape; the company is not current yet, `switchTo` moves the session there. A `400
   * invalid_request` is a failure of its own (the name rule), as in the sign-up form.
   */
  async createOrg(name: string): Promise<Outcome<OrgEntry>> {
    const outcome = await this.postForm<unknown>('/auth/orgs', { name });
    if (!outcome.ok) {
      return outcome;
    }
    const entry = asOrgEntry(outcome.value);
    return entry === null ? { ok: false, failure: { kind: 'other' } } : { ok: true, value: entry };
  }

  forgotPassword(email: string): Promise<Outcome> {
    return this.post('/auth/password/forgot', { email });
  }

  requestVerification(email: string): Promise<Outcome> {
    return this.post('/auth/email/verify/request', { email });
  }

  resetPassword(token: string, newPassword: string): Promise<Outcome> {
    return this.post('/auth/password/reset', { token, new_password: newPassword });
  }

  verifyEmail(token: string): Promise<Outcome> {
    return this.post('/auth/email/verify', { token });
  }

  async previewInvite(token: string): Promise<Outcome<InvitePreview>> {
    const outcome = await this.post<unknown>('/auth/invites/preview', { token });
    if (!outcome.ok) {
      return outcome;
    }
    const body = outcome.value;
    if (
      isRecord(body) &&
      typeof body['org_name'] === 'string' &&
      typeof body['email'] === 'string' &&
      typeof body['role'] === 'string'
    ) {
      return {
        ok: true,
        // Anything but a true boolean means "no private account": the screen of spec 0005, which asks for a password.
        value: { org_name: body['org_name'], email: body['email'], role: body['role'], has_private_account: body['has_private_account'] === true },
      };
    }
    return { ok: false, failure: { kind: 'other' } };
  }

  /** With `leavePrivateAccount` (spec 0010) the private account moves into the company on purpose, and no password is sent. */
  acceptInvite(token: string, password: string | null, leavePrivateAccount = false): Promise<Outcome> {
    return this.post('/auth/invites/accept', leavePrivateAccount ? { token, leave_private_account: true } : { token, password });
  }

  /**
   * Whether the product's screens may offer sign-up. Each switch is read on its own (spec 0010). A call that fails, and an answer this
   * app does not know, and one that takes longer than ten seconds, mean "invite-only", "off" and no provider: the link "Create an account"
   * and the provider buttons are then not offered, which is the safe side.
   */
  async options(): Promise<SiteOptions> {
    try {
      const body = await firstValueFrom(this.http.get<unknown>('/auth/options').pipe(timeout(OPTIONS_TIMEOUT_MS)));
      return {
        signup: isRecord(body) && body['signup'] === 'self-service' ? 'self-service' : 'invite-only',
        private_signup: isRecord(body) && body['private_signup'] === 'self-service' ? 'self-service' : 'off',
        providers: isRecord(body) ? providerNamesOf(body['providers']) : [],
        orgCreation: isRecord(body) && body['org_creation'] === 'self-service',
      };
    } catch {
      return { signup: 'invite-only', private_signup: 'off', providers: [], orgCreation: false };
    }
  }

  /** A company of one's own (`companyName`), or a private account (`null`: the kind is sent and no company name). */
  signUp(email: string, companyName: string | null): Promise<Outcome> {
    return this.postForm('/auth/signup', companyName === null ? { email, kind: 'private' } : { email, company_name: companyName });
  }

  async previewSignup(token: string): Promise<Outcome<SignupPreview>> {
    const outcome = await this.post<unknown>('/auth/signup/preview', { token });
    if (!outcome.ok) {
      return outcome;
    }
    const body = outcome.value;
    if (isRecord(body) && typeof body['email'] === 'string') {
      if (body['kind'] === 'private') {
        return { ok: true, value: { kind: 'private', email: body['email'], company_name: null } };
      }
      if (typeof body['company_name'] === 'string') {
        return { ok: true, value: { kind: 'organization', email: body['email'], company_name: body['company_name'] } };
      }
    }
    return { ok: false, failure: { kind: 'other' } };
  }

  private async get<T>(url: string): Promise<Outcome<T>> {
    try {
      return { ok: true, value: await firstValueFrom(this.http.get<T>(url)) };
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
  }

  /** Like `post`, but a `400 invalid_request` is a failure of its own: the sign-up form has a text for it. */
  private async postForm<T = void>(url: string, body: unknown): Promise<Outcome<T>> {
    try {
      return { ok: true, value: await firstValueFrom(this.http.post<T>(url, body)) };
    } catch (error) {
      return { ok: false, failure: formFailureOf(error) };
    }
  }

  private async post<T = void>(url: string, body: unknown): Promise<Outcome<T>> {
    try {
      return { ok: true, value: await firstValueFrom(this.http.post<T>(url, body)) };
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
  }
}
