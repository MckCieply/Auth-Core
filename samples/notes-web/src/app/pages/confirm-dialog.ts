import { Component, computed, inject, input, output, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { ProviderName } from '../account-api';
import { AuthService } from '../auth/auth.service';
import { texts } from '../texts';
import { acceptablePassword } from './password-input';
import { ProviderButtons } from './provider-buttons';

/**
 * Spec 0012 -> Confirming a connect or a disconnect: "Confirm it's you". The password and "Confirm" for an account with a password; one
 * "Confirm with {Provider}" per linked offered provider otherwise (the browser leaves, and comes back to /settings with the window open);
 * with neither, the sentence and "Send me a link".
 */
@Component({
  selector: 'app-confirm-dialog',
  imports: [ReactiveFormsModule, ProviderButtons],
  template: `
    <section role="dialog" aria-modal="true" aria-labelledby="confirm-title" data-testid="confirm-dialog">
      <h2 id="confirm-title" tabindex="-1">{{ t.confirmTitle }}</h2>
      @if (me()?.has_password) {
        <form [formGroup]="form" (ngSubmit)="confirm()" novalidate data-testid="confirm-form">
          <label for="confirm-password">{{ password }}</label>
          <input id="confirm-password" type="password" formControlName="password" autocomplete="current-password" />
          <button type="submit" data-testid="confirm-submit" [disabled]="busy()">{{ t.confirm }}</button>
        </form>
      } @else if (usable().length > 0) {
        <app-provider-buttons [providers]="usable()" action="confirm" [reauth]="true" target="confirm" [separator]="false" />
      } @else {
        <p data-testid="confirm-none">{{ t.noMethod }}</p>
        <button type="button" data-testid="send-link" (click)="sendLink.emit()">{{ t.sendLink }}</button>
      }
      @if (error(); as text) {
        <p class="error" role="alert" data-testid="confirm-error">{{ text }}</p>
      }
      <button type="button" data-testid="confirm-cancel" [disabled]="busy()" (click)="cancelled.emit()">{{ cancel }}</button>
    </section>
  `,
})
export class ConfirmDialog {
  protected readonly t = texts.settings;
  protected readonly password = texts.common.password;
  protected readonly cancel = texts.common.cancel;
  private readonly auth = inject(AuthService);

  /** The providers the app offers now: a login of another one cannot confirm here. */
  readonly providers = input.required<readonly ProviderName[]>();
  readonly confirmed = output<void>();
  readonly cancelled = output<void>();
  readonly sendLink = output<void>();

  protected readonly me = this.auth.me;
  protected readonly usable = computed(() => {
    const offered = this.providers();
    return (this.me()?.linked_providers ?? []).filter((name) => offered.includes(name)).sort((a, b) => offered.indexOf(a) - offered.indexOf(b));
  });
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(NonNullableFormBuilder).group({ password: [''] });

  protected async confirm(): Promise<void> {
    const password = this.form.controls.password.value;
    if (this.busy()) {
      return;
    }
    if (!acceptablePassword(password)) {
      this.error.set(texts.common.passwordRequired);
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    const result = await this.auth.confirmPassword(password);
    this.busy.set(false);
    if (result.ok) {
      this.form.reset();
      this.confirmed.emit();
      return;
    }
    switch (result.failure.kind) {
      case 'wrong_password':
        this.error.set(this.t.wrongPassword);
        break;
      case 'too_many_attempts':
        this.error.set(texts.login.tooManyAttempts(result.failure.retryAfterSeconds));
        break;
      case 'reauth_required':
      case 'password_required':
        await this.auth.loadMe(); // the account is not what the dialog thought: draw what fits it now
        break;
      case 'no_membership':
        this.error.set(texts.login.noMembership);
        break;
      case 'too_many_requests':
      case 'session_required': // the interceptor signs the person out; no sentence
        break;
      default:
        this.error.set(texts.common.somethingWrong);
    }
  }
}
