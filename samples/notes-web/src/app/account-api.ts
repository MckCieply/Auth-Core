import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Failure, failureOf, formFailureOf, isRecord } from './auth/auth.service';

export type Outcome<T = void> = { ok: true; value: T } | { ok: false; failure: Failure };

/** What POST /auth/invites/preview answers: who invites, for whom, as what. */
export interface InvitePreview {
  org_name: string;
  email: string;
  role: string;
}

/** What GET /auth/options answers, as far as the screens care: whether a visitor may start a company of their own. */
export interface SiteOptions {
  signup: 'self-service' | 'invite-only';
}

/**
 * What POST /auth/signup/preview answers: the address as it was typed, the company name of the request, and whether an account without a
 * company has the address (spec 0009, Amendment 3): then the completion asks for no password.
 */
export interface SignupPreview {
  email: string;
  company_name: string;
  has_account: boolean;
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
      return { ok: true, value: { org_name: body['org_name'], email: body['email'], role: body['role'] } };
    }
    return { ok: false, failure: { kind: 'other' } };
  }

  acceptInvite(token: string, password: string): Promise<Outcome> {
    return this.post('/auth/invites/accept', { token, password });
  }

  /**
   * Whether the product's screens may offer sign-up. A call that fails, and an answer this app does not know, mean "invite-only":
   * the link "Create an account" is then not offered, which is the safe side.
   */
  async options(): Promise<SiteOptions> {
    try {
      const body = await firstValueFrom(this.http.get<unknown>('/auth/options'));
      return { signup: isRecord(body) && body['signup'] === 'self-service' ? 'self-service' : 'invite-only' };
    } catch {
      return { signup: 'invite-only' };
    }
  }

  signUp(email: string, companyName: string): Promise<Outcome> {
    return this.postForm('/auth/signup', { email, company_name: companyName });
  }

  async previewSignup(token: string): Promise<Outcome<SignupPreview>> {
    const outcome = await this.post<unknown>('/auth/signup/preview', { token });
    if (!outcome.ok) {
      return outcome;
    }
    const body = outcome.value;
    if (isRecord(body) && typeof body['email'] === 'string' && typeof body['company_name'] === 'string') {
      // Anything but a true boolean means "no account": the screen then asks for a password, which is the safe side.
      return { ok: true, value: { email: body['email'], company_name: body['company_name'], has_account: body['has_account'] === true } };
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
