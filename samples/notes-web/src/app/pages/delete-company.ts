import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService, Failure, errorCode } from '../auth/auth.service';
import { texts } from '../texts';
import { acceptablePassword } from './password-input';
import { ProviderButtons } from './provider-buttons';

/**
 * Spec 0012: "Delete company" on /team, for a holder of org:delete. The name is typed again and compared with the company's name first; then
 * the password, which is confirmed with POST /auth/account/confirm before any data goes, or "Confirm with {Provider}" for an admin without one.
 * Then the product's data (DELETE /api/org), and only after its 204 the company (DELETE /auth/org); then the person is signed out.
 */
@Component({
  selector: 'app-delete-company',
  imports: [ReactiveFormsModule, ProviderButtons],
  template: `
    <section data-testid="delete-company-section">
      <h2>{{ t.deleteCompany }}</h2>
      @if (!open()) {
        <button type="button" data-testid="delete-company" (click)="start()">{{ t.deleteCompany }}</button>
      } @else {
        <p class="error">{{ t.deleteCompanyWarning }}</p>
        <form [formGroup]="form" (ngSubmit)="submit()" novalidate data-testid="delete-company-form">
          <label for="company-name">{{ t.companyName }}</label>
          <input id="company-name" type="text" formControlName="name" autocomplete="off" />
          @if (me()?.has_password !== false) {
            <label for="company-password">{{ password }}</label>
            <input id="company-password" type="password" formControlName="password" autocomplete="current-password" />
            <button type="submit" data-testid="delete-company-confirm" [disabled]="busy()">{{ t.deleteCompany }}</button>
          } @else if (ticket() !== null) {
            <button type="submit" data-testid="delete-company-confirm" [disabled]="busy()">{{ t.deleteCompany }}</button>
          }
        </form>
        @if (me()?.has_password === false && ticket() === null) {
          <app-provider-buttons
            [providers]="me()?.linked_providers ?? []"
            action="confirm"
            [reauth]="true"
            target="delete_company"
            [separator]="false"
          />
        }
        <button type="button" data-testid="delete-company-cancel" [disabled]="busy()" (click)="cancel()">{{ cancelText }}</button>
        @if (error(); as text) {
          <p class="error" role="alert" data-testid="delete-company-error">{{ text }}</p>
        }
      }
    </section>
  `,
})
export class DeleteCompanySection implements OnInit {
  protected readonly t = texts.team;
  protected readonly password = texts.common.password;
  protected readonly cancelText = texts.common.cancel;
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly me = this.auth.me;
  /**
   * The ticket of "Confirm with {Provider}", handed back by /provider-return in the navigation state: read by the routed page (`TeamPage`) while
   * that navigation is current and passed down, because this section is created later (after the page has read the members), when
   * `currentNavigation()` is null. Memory only; the page also takes it out of the history entry.
   */
  readonly handedTicket = input<string | null>(null, { alias: 'ticket' });
  protected readonly ticket = signal<string | null>(null);
  protected readonly open = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(NonNullableFormBuilder).group({ name: [''], password: [''] });
  protected readonly companyName = computed(() => this.me()?.org_name ?? null);

  ngOnInit(): void {
    const handed = this.handedTicket();
    if (handed !== null) {
      this.ticket.set(handed);
      this.open.set(true);
    }
  }

  protected start(): void {
    this.form.reset();
    this.error.set(null);
    this.open.set(true);
  }

  protected cancel(): void {
    this.open.set(false);
    this.ticket.set(null);
    this.error.set(null);
  }

  protected async submit(): Promise<void> {
    if (this.busy()) {
      return;
    }
    const { name, password } = this.form.getRawValue();
    this.error.set(null);
    // The name first, exactly: nothing is sent for a mismatch.
    if (name !== this.companyName()) {
      this.error.set(this.t.nameMismatch);
      return;
    }
    const ticket = this.ticket();
    const withPassword = this.me()?.has_password !== false;
    if (withPassword && !acceptablePassword(password)) {
      this.error.set(texts.common.passwordRequired);
      return;
    }
    this.busy.set(true);
    try {
      if (withPassword) {
        // A wrong password is caught before any data goes (spec 0012, risk 51).
        const confirmed = await this.auth.confirmPassword(password);
        if (!confirmed.ok) {
          await this.refused(confirmed.failure, false);
          return;
        }
      } else if (ticket === null) {
        return;
      }
      if (!(await this.deleteData())) {
        return;
      }
      const result = await this.auth.deleteCompany(name, withPassword ? { password } : { provider_ticket: ticket! });
      if (result.ok) {
        await this.router.navigateByUrl('/login', { state: { notice: 'company_deleted' } }).catch(() => false);
        return;
      }
      await this.refused(result.failure, true);
    } finally {
      this.busy.set(false);
    }
  }

  /** DELETE /api/org. True when the company's notes are gone. */
  private async deleteData(): Promise<boolean> {
    try {
      await firstValueFrom(this.http.delete('/api/org'));
      return true;
    } catch (error) {
      const unavailable = error instanceof HttpErrorResponse && error.status === 503 && errorCode(error) === 'auth_unavailable';
      this.error.set(unavailable ? null : texts.common.somethingWrong);
      return false;
    }
  }

  /** The answers of the confirmation and of DELETE /auth/org; `gone` once the company's notes are deleted. */
  private async refused(failure: Failure, gone: boolean): Promise<void> {
    const after = gone ? ` ${this.t.notesGone}` : '';
    switch (failure.kind) {
      case 'invalid_request':
        this.error.set(this.t.nameMismatch + after);
        break;
      case 'permission_not_held':
        this.error.set(this.t.permissionNotHeldDelete + after);
        break;
      case 'permissions_changed':
        // The company is not the one this screen thought, after the interceptor's refresh and second try: sign out.
        await this.auth.logout();
        await this.router.navigateByUrl('/login').catch(() => false);
        break;
      case 'wrong_password':
        this.error.set(this.t.wrongPassword + after);
        break;
      case 'too_many_attempts':
        this.error.set(texts.login.tooManyAttempts(failure.retryAfterSeconds) + after);
        break;
      case 'reauth_failed':
        this.ticket.set(null);
        this.error.set(this.t.reauthFailed);
        break;
      case 'password_required':
      case 'reauth_required':
        this.ticket.set(null);
        await this.auth.loadMe();
        this.error.set(gone ? this.t.notesGone : null);
        break;
      case 'too_many_requests':
      case 'session_required':
        if (gone) {
          this.error.set(this.t.notesGone);
        }
        break;
      default:
        this.error.set(texts.common.somethingWrong + after);
    }
  }
}
