import { Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AccountApi } from '../account-api';
import { texts } from '../texts';
import { takeTokenFromUrl } from './token-from-url';

@Component({
  selector: 'app-verify',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @switch (phase()) {
        @case ('working') {
          <p class="status" role="status">{{ t.working }}</p>
        }
        @case ('done') {
          <p role="status" data-testid="verify-done">{{ t.done }} <a routerLink="/login">{{ common.signInLink }}</a></p>
        }
        @case ('invalid') {
          <p class="error" role="alert">{{ common.invalidLink }}</p>
          <form [formGroup]="form" (ngSubmit)="resend()" novalidate>
            <label for="email">{{ common.email }}</label>
            <input id="email" type="email" formControlName="email" autocomplete="username" autocapitalize="none" />
            <button type="submit" [disabled]="busy()">{{ common.sendVerificationAgain }}</button>
          </form>
          @if (info(); as text) {
            <p class="status" role="status">{{ text }}</p>
          }
          @if (error(); as text) {
            <p class="error" role="alert">{{ text }}</p>
          }
        }
        @case ('error') {
          <p class="error" role="alert">{{ common.somethingWrong }}</p>
        }
      }
    </main>
  `,
})
export class VerifyPage implements OnInit {
  protected readonly t = texts.verify;
  protected readonly common = texts.common;
  private readonly account = inject(AccountApi);
  // Read once, here, and gone from the address bar from now on.
  private readonly token = takeTokenFromUrl(inject(ActivatedRoute));

  protected readonly form = inject(NonNullableFormBuilder).group({ email: ['', Validators.required] });
  protected readonly phase = signal<'working' | 'done' | 'invalid' | 'error'>(this.token === null ? 'invalid' : 'working');
  protected readonly busy = signal(false);
  protected readonly info = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    if (this.token !== null) {
      void this.confirm(this.token);
    }
  }

  protected async resend(): Promise<void> {
    if (this.busy() || this.form.invalid) {
      return;
    }
    this.busy.set(true);
    this.info.set(null);
    this.error.set(null);
    const result = await this.account.requestVerification(this.form.getRawValue().email.trim());
    this.busy.set(false);
    if (result.ok) {
      this.info.set(this.common.verificationSent);
    } else if (result.failure.kind === 'too_many_attempts') {
      this.error.set(this.common.waitSeconds(result.failure.retryAfterSeconds));
    } else {
      this.error.set(this.common.somethingWrong);
    }
  }

  private async confirm(token: string): Promise<void> {
    const result = await this.account.verifyEmail(token);
    if (result.ok) {
      this.phase.set('done');
    } else if (result.failure.kind === 'invalid_token') {
      this.phase.set('invalid');
    } else {
      this.phase.set('error');
    }
  }
}
