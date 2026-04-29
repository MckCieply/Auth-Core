import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, timeout } from 'rxjs';
import { Failure, failureOf, formFailureOf, isRecord } from './auth/auth.service';

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

/** Auth-Core's endpoints for the mail links and the account, which need no access token. */
@Injectable({ providedIn: 'root' })
export class AccountApi {
  private readonly http = inject(HttpClient);

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
      };
    } catch {
      return { signup: 'invite-only', private_signup: 'off', providers: [] };
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
