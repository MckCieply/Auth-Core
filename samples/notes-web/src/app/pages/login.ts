import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AccountApi } from '../account-api';
import { safeReturnUrl } from '../auth/auth.guard';
import { AuthService, Failure } from '../auth/auth.service';
import { texts } from '../texts';
import { requiredText } from './required-text';

/** The email an invitation screen passed on in the navigation state (it is never in the URL). */
function startEmail(router: Router): string {
  const email: unknown = router.currentNavigation()?.extras.state?.['email'];
  return typeof email === 'string' ? email : '';
}

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
        <label for="email">{{ common.email }}</label>
        <input id="email" type="email" formControlName="email" autocomplete="username" autocapitalize="none" />
        <label for="password">{{ common.password }}</label>
        <input id="password" type="password" formControlName="password" autocomplete="current-password" />
        <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
      </form>
      @if (message(); as text) {
        <p class="error" role="alert" data-testid="login-message">{{ text }}</p>
      }
      @if (canResend()) {
        <button type="button" class="link" data-testid="resend" (click)="resend()">{{ common.sendVerificationAgain }}</button>
      }
      @if (info(); as text) {
        <p class="status" role="status">{{ text }}</p>
      }
      <p><a routerLink="/forgot">{{ t.forgot }}</a></p>
    </main>
  `,
})
export class LoginPage {
  protected readonly t = texts.login;
  protected readonly common = texts.common;
  private readonly auth = inject(AuthService);
  private readonly account = inject(AccountApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly form = inject(NonNullableFormBuilder).group({
    email: [startEmail(this.router), requiredText],
    password: ['', Validators.required],
  });
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly info = signal<string | null>(null);
  protected readonly canResend = signal(false);
  protected readonly resending = signal(false);

  protected async submit(): Promise<void> {
    if (this.busy() || this.form.invalid) {
      return;
    }
    this.busy.set(true);
    this.message.set(null);
    this.info.set(null);
    this.canResend.set(false);
    const { email, password } = this.form.getRawValue();
    const result = await this.auth.login(email.trim(), password);
    if (result.ok) {
      // The button stays off until the next page is open: a second Enter must not sign in twice. When the router cannot open it
      // (it answers false or fails) the person stays here and the button is on again.
      try {
        await this.router.navigateByUrl(safeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl')));
      } catch {
        // nothing to show: the sign-in itself worked
      }
    } else {
      this.show(result.failure);
    }
    this.busy.set(false);
  }

  protected async resend(): Promise<void> {
    const email = this.form.controls.email.value.trim();
    if (email === '' || this.resending()) {
      return;
    }
    this.resending.set(true);
    this.message.set(null);
    this.info.set(null);
    const result = await this.account.requestVerification(email);
    this.resending.set(false);
    if (result.ok) {
      this.info.set(this.common.verificationSent);
    } else if (result.failure.kind === 'too_many_attempts') {
      this.info.set(this.common.waitSeconds(result.failure.retryAfterSeconds));
    } else {
      this.message.set(this.common.somethingWrong);
    }
  }

  private show(failure: Failure): void {
    switch (failure.kind) {
      case 'invalid_credentials':
        this.message.set(this.t.wrongCredentials);
        break;
      case 'email_not_verified':
        this.message.set(this.t.notVerified);
        this.canResend.set(true);
        break;
      case 'no_membership':
        this.message.set(this.t.noMembership);
        break;
      case 'too_many_attempts':
        this.message.set(this.t.tooManyAttempts(failure.retryAfterSeconds));
        break;
      default:
        this.message.set(this.common.somethingWrong);
    }
  }
}
