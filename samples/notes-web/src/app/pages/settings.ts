import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AccountApi, ProviderName } from '../account-api';
import { AuthService, Failure } from '../auth/auth.service';
import { texts } from '../texts';
import { acceptablePassword } from './password-input';
import { providerTitle } from './provider-buttons';
import { PasswordRules } from './password-rules';

/** One row of "Sign-in methods": a provider the app offers, or one the account still has a login of. */
interface ProviderRow {
  name: ProviderName;
  title: string;
  connected: boolean;
  offered: boolean;
  /** False when the account would keep no way in after it: no password and no login of another offered provider. */
  canDisconnect: boolean;
}

/**
 * Spec 0012 -> Sample frontend: the person's own settings. "Sign-in methods" (the password and one row per provider), "Password" (change it
 * against the current one, or set a first one through the mail). Every answer is read from Auth-Core (GET /auth/me, GET /auth/options).
 */
@Component({
  selector: 'app-settings',
  imports: [ReactiveFormsModule, RouterLink, PasswordRules],
  template: `
    <main class="wide">
      <h1 tabindex="-1" data-testid="settings-title">{{ t.title }}</h1>
      @if (me(); as person) {
        <section data-testid="methods">
          <h2>{{ t.methods }}</h2>
          <ul class="plain">
            <li data-testid="row-password">
              {{ t.password }} <span>{{ person.has_password ? t.set : t.notSet }}</span>
            </li>
            @for (row of rows(); track row.name) {
              <li [attr.data-testid]="'row-' + row.name">
                {{ row.title }}
                @if (row.connected) {
                  <span>{{ t.connected }}</span>
                  <button type="button" [attr.data-testid]="'disconnect-' + row.name" [disabled]="!row.canDisconnect || busy()">
                    {{ t.disconnect }}
                  </button>
                  @if (!row.canDisconnect) {
                    <p class="status" data-testid="disconnect-blocked">{{ t.disconnectBlocked }}</p>
                  }
                } @else if (row.offered) {
                  <button type="button" [attr.data-testid]="'connect-' + row.name" [disabled]="busy()">{{ t.connect }}</button>
                }
              </li>
            }
          </ul>
        </section>
        <section data-testid="password-section">
          <h2>{{ t.password }}</h2>
          @if (person.has_password) {
            <form [formGroup]="passwordForm" (ngSubmit)="changePassword()" novalidate data-testid="password-form">
              <label for="current-password">{{ t.currentPassword }}</label>
              <input id="current-password" type="password" formControlName="current" autocomplete="current-password" />
              <label for="settings-new-password">{{ common.newPassword }}</label>
              <input id="settings-new-password" type="password" formControlName="next" autocomplete="new-password" />
              <label for="settings-repeat-password">{{ common.repeatPassword }}</label>
              <input id="settings-repeat-password" type="password" formControlName="repeat" autocomplete="new-password" />
              <button type="submit" data-testid="password-submit" [disabled]="passwordBusy()">{{ t.changePassword }}</button>
            </form>
            <app-password-rules [rules]="rules()" />
          } @else {
            <p>{{ t.setPassword }}</p>
            <button type="button" data-testid="send-link" [disabled]="linkBusy()" (click)="sendLink()">{{ t.sendLink }}</button>
            @if (linkSent()) {
              <p class="status" role="status" data-testid="link-sent">{{ t.linkSent }}</p>
            }
          }
          @if (passwordMessage(); as text) {
            <p class="status" role="status" data-testid="password-message">{{ text }}</p>
          }
          @if (passwordError(); as text) {
            <p class="error" role="alert" data-testid="password-error">{{ text }}</p>
          }
        </section>
      } @else {
        <p class="status" role="status">{{ t.loading }}</p>
      }
      <p><a routerLink="/notes">{{ t.back }}</a></p>
    </main>
  `,
})
export class SettingsPage implements OnInit {
  protected readonly t = texts.settings;
  protected readonly common = texts.common;
  protected readonly auth = inject(AuthService);
  private readonly account = inject(AccountApi);

  protected readonly me = this.auth.me;
  /** The providers GET /auth/options offers, in its order; empty until it answers, and when it fails. */
  protected readonly offered = signal<ProviderName[]>([]);
  /** A connect or a disconnect in flight (spec 0012: each button is disabled while its request is). */
  protected readonly busy = signal(false);
  protected readonly passwordBusy = signal(false);
  protected readonly linkBusy = signal(false);
  protected readonly linkSent = signal(false);
  protected readonly rules = signal<readonly string[]>([]);
  protected readonly passwordMessage = signal<string | null>(null);
  protected readonly passwordError = signal<string | null>(null);
  protected readonly passwordForm = inject(NonNullableFormBuilder).group({ current: [''], next: [''], repeat: [''] });

  protected readonly rows = computed<ProviderRow[]>(() => {
    const person = this.me();
    if (person === null) {
      return [];
    }
    const offered = this.offered();
    const linked = person.linked_providers;
    const names = [...offered, ...linked.filter((name) => !offered.includes(name))];
    return names.map((name) => ({
      name,
      title: providerTitle(name),
      connected: linked.includes(name),
      offered: offered.includes(name),
      canDisconnect: person.has_password || linked.some((other) => other !== name && offered.includes(other)),
    }));
  });

  ngOnInit(): void {
    void this.account.options().then((options) => this.offered.set(options.providers));
    // Always read afresh: the window and the logins are what the screen acts on.
    void this.auth.loadMe();
  }

  protected async changePassword(): Promise<void> {
    if (this.passwordBusy()) {
      return;
    }
    const { current, next, repeat } = this.passwordForm.getRawValue();
    this.passwordMessage.set(null);
    this.passwordError.set(null);
    this.rules.set([]);
    if (!acceptablePassword(current)) {
      this.passwordError.set(this.t.currentRequired);
      return;
    }
    if (next !== repeat) {
      this.passwordError.set(this.common.passwordsDiffer);
      return;
    }
    this.passwordBusy.set(true);
    const result = await this.auth.changePassword(current, next);
    this.passwordBusy.set(false);
    if (result.ok) {
      this.passwordForm.reset();
      this.passwordMessage.set(this.t.passwordChanged);
      return;
    }
    await this.passwordRefused(result.failure);
  }

  private async passwordRefused(failure: Failure): Promise<void> {
    switch (failure.kind) {
      case 'wrong_password':
        this.passwordError.set(this.t.wrongCurrent);
        break;
      case 'too_many_attempts':
        this.passwordError.set(texts.login.tooManyAttempts(failure.retryAfterSeconds));
        break;
      case 'weak_password':
        this.rules.set(failure.rules);
        break;
      case 'password_not_set':
        // The account is not what the screen thought: draw the section that fits it now.
        await this.auth.loadMe();
        break;
      case 'no_membership':
        this.passwordError.set(texts.login.noMembership);
        break;
      case 'too_many_requests':
      case 'session_required':
        // The bar says when to try again; or the interceptor has already signed the person out.
        break;
      default:
        this.passwordError.set(this.common.somethingWrong);
    }
  }

  protected async sendLink(): Promise<void> {
    const person = this.me();
    if (this.linkBusy() || person === null) {
      return;
    }
    this.linkBusy.set(true);
    this.passwordError.set(null);
    const outcome = await this.account.forgotPassword(person.email);
    this.linkBusy.set(false);
    if (outcome.ok) {
      this.linkSent.set(true);
    } else if (outcome.failure.kind !== 'too_many_requests') {
      this.passwordError.set(this.common.somethingWrong);
    }
  }
}
