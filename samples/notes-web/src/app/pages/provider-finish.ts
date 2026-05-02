import { Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { SignupKind } from '../account-api';
import { DEFAULT_RETURN_URL } from '../auth/auth.guard';
import { AuthService } from '../auth/auth.service';
import { texts } from '../texts';
import { requiredText } from './required-text';
import { keepTicketOutOfHistory } from './ticket-history';

interface FinishState {
  ticket: string;
  email: string;
  kinds: SignupKind[];
}

/** What /provider-return handed on in the navigation state, or null (a typed address, a new tab): then the screen goes to /login. */
function finishState(router: Router): FinishState | null {
  const state = router.currentNavigation()?.extras.state;
  const kinds: unknown = state?.['kinds'];
  if (state === undefined || typeof state['ticket'] !== 'string' || state['ticket'] === '' || typeof state['email'] !== 'string' || !Array.isArray(kinds)) {
    return null;
  }
  const known = (['organization', 'private'] as const).filter((kind) => kinds.includes(kind));
  return known.length === 0 ? null : { ticket: state['ticket'], email: state['email'], kinds: known };
}

/** Spec 0011: a new account with the provider's verified address and no password. */
@Component({
  selector: 'app-provider-finish',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @if (finish; as state) {
        @switch (phase()) {
          @case ('form') {
            <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
              <label for="email">{{ common.email }}</label>
              <input id="email" type="email" [value]="state.email" readonly autocomplete="username" />
              @if (state.kinds.length > 1) {
                <fieldset class="choice" data-testid="finish-kind">
                  <legend>{{ signup.kindLegend }}</legend>
                  <label>
                    <input type="radio" name="kind" data-testid="finish-organization" [checked]="kind() === 'organization'" (change)="choose('organization')" />
                    {{ signup.kindOrganization }}
                  </label>
                  <label>
                    <input type="radio" name="kind" data-testid="finish-private" [checked]="kind() === 'private'" (change)="choose('private')" />
                    {{ signup.kindPrivate }}
                  </label>
                </fieldset>
              }
              @if (kind() === 'organization') {
                <label for="company">{{ signup.companyName }}</label>
                <input id="company" type="text" formControlName="company" maxlength="100" autocomplete="organization" />
              }
              <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
            </form>
            @if (error(); as text) {
              <p class="error" role="alert" data-testid="finish-error">{{ text }}</p>
            }
          }
          @case ('registered') {
            <p class="error" role="alert">{{ t.alreadyRegistered }}</p>
            <p><a routerLink="/login">{{ common.signInLink }}</a></p>
          }
          @case ('closed') {
            <p class="error" role="alert">{{ signup.closed }}</p>
            <p><a routerLink="/login">{{ common.backToSignIn }}</a></p>
          }
          @case ('expired') {
            <p class="error" role="alert">{{ returnTexts.invalidTicket }}</p>
            <p><a routerLink="/login">{{ common.backToSignIn }}</a></p>
          }
        }
      }
    </main>
  `,
})
export class ProviderFinishPage implements OnInit {
  protected readonly t = texts.providerFinish;
  protected readonly signup = texts.signup;
  protected readonly returnTexts = texts.providerReturn;
  protected readonly common = texts.common;
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly finish = finishState(this.router);

  protected readonly form = inject(NonNullableFormBuilder).group({ company: ['', requiredText] });
  protected readonly kind = signal<SignupKind>(this.finish?.kinds[0] ?? 'organization');
  protected readonly phase = signal<'form' | 'registered' | 'closed' | 'expired'>('form');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    if (this.finish === null) {
      void this.router.navigateByUrl('/login', { replaceUrl: true }).catch(() => undefined);
    } else {
      void keepTicketOutOfHistory(this.router); // the ticket stays in this component's memory only
    }
  }

  protected choose(kind: SignupKind): void {
    this.kind.set(kind);
    this.error.set(null);
  }

  protected async submit(): Promise<void> {
    if (this.busy() || this.finish === null) {
      return;
    }
    const isPrivate = this.kind() === 'private';
    if (!isPrivate && this.form.invalid) {
      this.error.set(texts.completeSignup.companyRequired);
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    const result = await this.auth.finishWithProvider(this.finish.ticket, this.kind(), isPrivate ? null : this.form.controls.company.value.trim());
    if (result.ok) {
      try {
        await this.router.navigateByUrl(DEFAULT_RETURN_URL, { replaceUrl: true });
      } catch {
        // signed in all the same
      }
    } else if (result.failure.kind === 'already_registered') {
      this.phase.set('registered');
    } else if (result.failure.kind === 'signup_disabled') {
      this.phase.set('closed');
    } else if (result.failure.kind === 'invalid_ticket') {
      this.phase.set('expired');
    } else if (result.failure.kind === 'invalid_request') {
      this.error.set(texts.completeSignup.invalidRequest);
    } else if (result.failure.kind !== 'too_many_requests') {
      this.error.set(this.common.somethingWrong);
    }
    this.busy.set(false);
  }
}
