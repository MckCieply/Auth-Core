import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AccountApi } from '../account-api';
import { texts } from '../texts';
import { PasswordRules } from './password-rules';
import { takeTokenFromUrl } from './token-from-url';

@Component({
  selector: 'app-reset',
  imports: [ReactiveFormsModule, RouterLink, PasswordRules],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @switch (phase()) {
        @case ('form') {
          <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
            <label for="password">{{ common.newPassword }}</label>
            <input id="password" type="password" formControlName="password" autocomplete="new-password" />
            <label for="repeat">{{ common.repeatPassword }}</label>
            <input id="repeat" type="password" formControlName="repeat" autocomplete="new-password" />
            <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
          </form>
          <app-password-rules [rules]="rules()" />
          @if (error(); as text) {
            <p class="error" role="alert">{{ text }}</p>
          }
        }
        @case ('done') {
          <p role="status" data-testid="reset-done">{{ t.done }} <a routerLink="/login">{{ common.signInLink }}</a></p>
        }
        @case ('invalid') {
          <p class="error" role="alert">{{ common.invalidLink }}</p>
          <p><a routerLink="/forgot">{{ common.askForNewLink }}</a></p>
        }
      }
    </main>
  `,
})
export class ResetPage {
  protected readonly t = texts.reset;
  protected readonly common = texts.common;
  private readonly account = inject(AccountApi);
  // Read once, here, and gone from the address bar from now on.
  private readonly token = takeTokenFromUrl(inject(ActivatedRoute), inject(Router));

  protected readonly form = inject(NonNullableFormBuilder).group({
    password: ['', Validators.required],
    repeat: ['', Validators.required],
  });
  protected readonly phase = signal<'form' | 'done' | 'invalid'>(this.token === null ? 'invalid' : 'form');
  protected readonly busy = signal(false);
  protected readonly rules = signal<string[]>([]);
  protected readonly error = signal<string | null>(null);

  protected async submit(): Promise<void> {
    if (this.busy() || this.form.invalid || this.token === null) {
      return;
    }
    const { password, repeat } = this.form.getRawValue();
    this.rules.set([]);
    this.error.set(null);
    if (password !== repeat) {
      this.error.set(this.common.passwordsDiffer);
      return;
    }
    this.busy.set(true);
    const result = await this.account.resetPassword(this.token, password);
    this.busy.set(false);
    if (result.ok) {
      this.phase.set('done');
    } else if (result.failure.kind === 'invalid_token') {
      this.phase.set('invalid');
    } else if (result.failure.kind === 'weak_password') {
      this.rules.set(result.failure.rules);
    } else {
      this.error.set(this.common.somethingWrong);
    }
  }
}
