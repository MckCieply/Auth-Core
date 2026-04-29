import { Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AccountApi, ProviderName, SignupKind } from '../account-api';
import { texts } from '../texts';
import { ProviderButtons } from './provider-buttons';
import { requiredText } from './required-text';

@Component({
  selector: 'app-signup',
  imports: [ReactiveFormsModule, RouterLink, ProviderButtons],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @if (closed()) {
        <p class="error" role="alert" data-testid="signup-closed">{{ t.closed }}</p>
      } @else if (ready()) {
        <app-provider-buttons [providers]="providers()" action="signup" />
        <p>{{ kind() === 'private' ? t.introPrivate : t.intro }}</p>
        <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
          @if (choice()) {
            <fieldset class="choice" data-testid="signup-kind">
              <legend>{{ t.kindLegend }}</legend>
              <label>
                <input
                  type="radio"
                  name="kind"
                  value="organization"
                  data-testid="kind-organization"
                  [checked]="kind() === 'organization'"
                  (change)="choose('organization')"
                />
                {{ t.kindOrganization }}
              </label>
              <label>
                <input
                  type="radio"
                  name="kind"
                  value="private"
                  data-testid="kind-private"
                  [checked]="kind() === 'private'"
                  (change)="choose('private')"
                />
                {{ t.kindPrivate }}
              </label>
            </fieldset>
          }
          <label for="email">{{ common.email }}</label>
          <input id="email" type="email" formControlName="email" autocomplete="username" autocapitalize="none" />
          @if (kind() === 'organization') {
            <label for="company">{{ t.companyName }}</label>
            <input id="company" type="text" formControlName="company" maxlength="100" autocomplete="organization" />
          }
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
export class SignupPage implements OnInit {
  protected readonly t = texts.signup;
  protected readonly common = texts.common;
  private readonly account = inject(AccountApi);

  // The password is not asked here: it is chosen at the link of the mail, so that only the reader of the mailbox ever sets it.
  protected readonly form = inject(NonNullableFormBuilder).group({ email: ['', requiredText], company: ['', requiredText] });
  protected readonly busy = signal(false);
  protected readonly sent = signal(false);
  protected readonly closed = signal(false);
  protected readonly error = signal<string | null>(null);
  /** An organization until the person, or the options, say otherwise. */
  protected readonly kind = signal<SignupKind>('organization');
  /** "Organization / Private person" is offered only while GET /auth/options says both are on (spec 0010). */
  protected readonly choice = signal(false);
  /** The form is drawn once GET /auth/options has answered, so that it never shows the wrong kind first. */
  protected readonly ready = signal(false);
  /** The offered providers (spec 0011): "Sign up with ..." comes before the form. */
  protected readonly providers = signal<ProviderName[]>([]);

  ngOnInit(): void {
    // With one kind on, the screen is that one. With both off, or when the call fails, it is the organization form: the request is then
    // answered by Auth-Core itself (403 signup_disabled), and the screen says that sign-up is closed.
    void this.account.options().then((options) => {
      const organization = options.signup === 'self-service';
      const privatePerson = options.private_signup === 'self-service';
      this.choice.set(organization && privatePerson);
      this.providers.set(options.providers);
      if (privatePerson && !organization) {
        this.choose('private');
      }
      this.ready.set(true);
    });
  }

  protected choose(kind: SignupKind): void {
    this.kind.set(kind);
    // A private person names no company: the field leaves the form and does not hold it invalid.
    if (kind === 'private') {
      this.form.controls.company.disable();
    } else {
      this.form.controls.company.enable();
    }
    this.sent.set(false);
    this.error.set(null);
  }

  protected async submit(): Promise<void> {
    if (this.busy() || this.form.invalid) {
      return;
    }
    this.busy.set(true);
    this.sent.set(false);
    this.error.set(null);
    const { email, company } = this.form.getRawValue();
    const isPrivate = this.kind() === 'private';
    // Trimmed: Auth-Core refuses a name with white space at either end.
    const result = await this.account.signUp(email.trim(), isPrivate ? null : company.trim());
    this.busy.set(false);
    if (result.ok) {
      this.sent.set(true);
    } else if (result.failure.kind === 'too_many_attempts') {
      this.error.set(this.common.waitSeconds(result.failure.retryAfterSeconds));
    } else if (result.failure.kind === 'signup_disabled') {
      this.closed.set(true);
    } else if (result.failure.kind === 'invalid_request') {
      this.error.set(isPrivate ? this.t.invalidRequestPrivate : this.t.invalidRequest);
    } else if (result.failure.kind === 'too_many_requests') {
      // The notice bar above the screen says when to try again: the interceptor put it there.
    } else {
      this.error.set(this.common.somethingWrong);
    }
  }
}
