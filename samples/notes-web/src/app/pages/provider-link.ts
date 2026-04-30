import { Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ProviderName, isProviderName } from '../account-api';
import { DEFAULT_RETURN_URL } from '../auth/auth.guard';
import { AuthService } from '../auth/auth.service';
import { texts } from '../texts';
import { providerTitle } from './provider-buttons';

interface LinkState {
  ticket: string;
  email: string;
  emailVerified: boolean;
  provider: ProviderName | null;
}

/** What /provider-return handed on in the navigation state, or null (no navigation state, e.g. a typed address or a new tab): then the screen goes to /login. */
function linkState(router: Router): LinkState | null {
  const state = router.currentNavigation()?.extras.state;
  if (
    state === undefined ||
    typeof state['ticket'] !== 'string' ||
    state['ticket'] === '' ||
    typeof state['email'] !== 'string' ||
    typeof state['emailVerified'] !== 'boolean'
  ) {
    return null;
  }
  const provider: unknown = state['provider'];
  return { ticket: state['ticket'], email: state['email'], emailVerified: state['emailVerified'], provider: isProviderName(provider) ? provider : null };
}

/** Spec 0011: the password of an existing account, once, to connect the provider. */
@Component({
  selector: 'app-provider-link',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @if (link; as state) {
        @if (expired()) {
          <p class="error" role="alert">{{ returnTexts.invalidTicket }}</p>
          <p><a routerLink="/login">{{ common.backToSignIn }}</a></p>
        } @else {
          @if (state.emailVerified) {
            <p data-testid="link-intro">{{ t.intro(state.email, provider) }}</p>
          } @else {
            <p data-testid="link-intro">{{ t.introUnverified(state.email, provider) }}</p>
            <p><a routerLink="/signup" data-testid="link-signup">{{ t.createAccount }}</a></p>
          }
          <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
            <label for="email">{{ common.email }}</label>
            <input id="email" type="email" [value]="state.email" readonly autocomplete="username" />
            <label for="password">{{ common.password }}</label>
            <input id="password" type="password" formControlName="password" autocomplete="current-password" />
            <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
          </form>
          @if (error(); as text) {
            <p class="error" role="alert" data-testid="link-error">{{ text }}</p>
          }
          <p><a routerLink="/forgot">{{ login.forgot }}</a></p>
        }
      }
    </main>
  `,
})
export class ProviderLinkPage implements OnInit {
  protected readonly t = texts.providerLink;
  protected readonly returnTexts = texts.providerReturn;
  protected readonly login = texts.login;
  protected readonly common = texts.common;
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly link = linkState(this.router);
  protected readonly provider = providerTitle(this.link?.provider ?? null);

  protected readonly form = inject(NonNullableFormBuilder).group({ password: ['', Validators.required] });
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly expired = signal(false);

  ngOnInit(): void {
    if (this.link === null) {
      void this.router.navigateByUrl('/login', { replaceUrl: true }).catch(() => undefined);
    }
  }

  protected async submit(): Promise<void> {
    if (this.busy() || this.form.invalid || this.link === null) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    const result = await this.auth.linkProvider(this.link.ticket, this.form.controls.password.value);
    if (result.ok) {
      try {
        await this.router.navigateByUrl(DEFAULT_RETURN_URL, { replaceUrl: true });
      } catch {
        // signed in all the same
      }
    } else {
      switch (result.failure.kind) {
        case 'invalid_credentials':
          this.error.set(this.t.wrongPassword);
          break;
        case 'too_many_attempts':
          this.error.set(this.login.tooManyAttempts(result.failure.retryAfterSeconds));
          break;
        case 'too_many_requests':
          // The notice bar above the screen says when to try again: the interceptor put it there.
          break;
        case 'invalid_ticket':
          this.expired.set(true);
          break;
        case 'email_not_verified':
          this.error.set(this.login.notVerified);
          break;
        case 'no_membership':
          this.error.set(this.login.noMembership);
          break;
        case 'no_email':
          this.error.set(this.returnTexts.noEmail);
          break;
        default:
          this.error.set(this.common.somethingWrong);
      }
    }
    this.busy.set(false);
  }
}
