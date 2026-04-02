import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AccountApi } from '../account-api';
import { texts } from '../texts';
import { requiredText } from './required-text';

@Component({
  selector: 'app-signup',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @if (closed()) {
        <p class="error" role="alert" data-testid="signup-closed">{{ t.closed }}</p>
      } @else {
        <p>{{ t.intro }}</p>
        <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
          <label for="email">{{ common.email }}</label>
          <input id="email" type="email" formControlName="email" autocomplete="username" autocapitalize="none" />
          <label for="company">{{ t.companyName }}</label>
          <input id="company" type="text" formControlName="company" maxlength="100" autocomplete="organization" />
          <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
        </form>
        @if (sent()) {
          <p class="status" role="status" data-testid="signup-sent">{{ t.sent }}</p>
        }
        @if (error(); as text) {
          <p class="error" role="alert">{{ text }}</p>
        }
      }
      <p><a routerLink="/login">{{ common.backToSignIn }}</a></p>
    </main>
  `,
})
export class SignupPage {
  protected readonly t = texts.signup;
  protected readonly common = texts.common;
  private readonly account = inject(AccountApi);

  // The password is not asked here: it is chosen at the link of the mail, so that only the reader of the mailbox ever sets it.
  protected readonly form = inject(NonNullableFormBuilder).group({ email: ['', requiredText], company: ['', requiredText] });
  protected readonly busy = signal(false);
  protected readonly sent = signal(false);
  protected readonly closed = signal(false);
  protected readonly error = signal<string | null>(null);

  protected async submit(): Promise<void> {
    if (this.busy() || this.form.invalid) {
      return;
    }
    this.busy.set(true);
    this.sent.set(false);
    this.error.set(null);
    const { email, company } = this.form.getRawValue();
    // Trimmed: Auth-Core refuses a name with white space at either end.
    const result = await this.account.signUp(email.trim(), company.trim());
    this.busy.set(false);
    if (result.ok) {
      this.sent.set(true);
    } else if (result.failure.kind === 'too_many_attempts') {
      this.error.set(this.common.waitSeconds(result.failure.retryAfterSeconds));
    } else if (result.failure.kind === 'signup_disabled') {
      this.closed.set(true);
    } else if (result.failure.kind === 'invalid_request') {
      this.error.set(this.t.invalidRequest);
    } else if (result.failure.kind === 'too_many_requests') {
      // The notice bar above the screen says when to try again: the interceptor put it there.
    } else {
      this.error.set(this.common.somethingWrong);
    }
  }
}
