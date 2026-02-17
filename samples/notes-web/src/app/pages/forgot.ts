import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AccountApi } from '../account-api';
import { texts } from '../texts';
import { requiredText } from './required-text';

@Component({
  selector: 'app-forgot',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      <p>{{ t.intro }}</p>
      <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
        <label for="email">{{ common.email }}</label>
        <input id="email" type="email" formControlName="email" autocomplete="username" autocapitalize="none" />
        <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
      </form>
      @if (sent()) {
        <p class="status" role="status" data-testid="forgot-sent">{{ t.sent }}</p>
      }
      @if (error(); as text) {
        <p class="error" role="alert">{{ text }}</p>
      }
      <p><a routerLink="/login">{{ common.backToSignIn }}</a></p>
    </main>
  `,
})
export class ForgotPage {
  protected readonly t = texts.forgot;
  protected readonly common = texts.common;
  private readonly account = inject(AccountApi);

  protected readonly form = inject(NonNullableFormBuilder).group({ email: ['', requiredText] });
  protected readonly busy = signal(false);
  protected readonly sent = signal(false);
  protected readonly error = signal<string | null>(null);

  protected async submit(): Promise<void> {
    if (this.busy() || this.form.invalid) {
      return;
    }
    this.busy.set(true);
    this.sent.set(false);
    this.error.set(null);
    const result = await this.account.forgotPassword(this.form.getRawValue().email.trim());
    this.busy.set(false);
    if (result.ok) {
      this.sent.set(true);
    } else if (result.failure.kind === 'too_many_attempts') {
      this.error.set(this.common.waitSeconds(result.failure.retryAfterSeconds));
    } else {
      this.error.set(this.common.somethingWrong);
    }
  }
}
