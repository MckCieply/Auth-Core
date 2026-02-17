import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Failure, failureOf, isRecord } from './auth/auth.service';

export type Outcome<T = void> = { ok: true; value: T } | { ok: false; failure: Failure };

/** What POST /auth/invites/preview answers: who invites, for whom, as what. */
export interface InvitePreview {
  org_name: string;
  email: string;
  role: string;
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

  private async post<T = void>(url: string, body: unknown): Promise<Outcome<T>> {
    try {
      return { ok: true, value: await firstValueFrom(this.http.post<T>(url, body)) };
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
  }
}
