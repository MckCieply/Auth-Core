import { Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AccountApi, SignupPreview } from '../account-api';
import { DEFAULT_RETURN_URL } from '../auth/auth.guard';
import { AuthService } from '../auth/auth.service';
import { texts } from '../texts';
import { PasswordRules } from './password-rules';
import { requiredText } from './required-text';
import { takeTokenFromUrl } from './token-from-url';

@Component({
  selector: 'app-complete-signup',
  imports: [ReactiveFormsModule, RouterLink, PasswordRules],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @switch (phase()) {
        @case ('loading') {
          <p class="status" role="status">{{ t.loading }}</p>
        }
        @case ('form') {
          @if (pending(); as signup) {
            <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
              <label for="email">{{ common.email }}</label>
              <input id="email" type="email" [value]="signup.email" readonly autocomplete="username" />
              <label for="company">{{ shared.companyName }}</label>
              <input id="company" type="text" formControlName="company" maxlength="100" autocomplete="organization" />
              <p class="status">{{ t.setsPassword(signup.email) }}</p>
              <label for="password">{{ common.newPassword }}</label>
              <input id="password" type="password" formControlName="password" autocomplete="new-password" />
              <label for="repeat">{{ common.repeatPassword }}</label>
              <input id="repeat" type="password" formControlName="repeat" autocomplete="new-password" />
              <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
            </form>
          }
          <app-password-rules [rules]="rules()" />
          @if (error(); as text) {
            <p class="error" role="alert">{{ text }}</p>
          }
        }
        @case ('invalid') {
          <p class="error" role="alert" data-testid="signup-invalid">{{ common.invalidLink }}</p>
          <p><a routerLink="/signup" data-testid="signup-again">{{ t.signUpAgain }}</a></p>
        }
        @case ('registered') {
          <p class="error" role="alert" data-testid="signup-registered">{{ t.alreadyRegistered }}</p>
          <p><a routerLink="/login">{{ common.signInLink }}</a></p>
        }
        @case ('closed') {
          <p class="error" role="alert" data-testid="signup-closed">{{ shared.closed }}</p>
          <p><a routerLink="/login">{{ common.backToSignIn }}</a></p>
        }
        @case ('error') {
          <p class="error" role="alert">{{ common.somethingWrong }}</p>
          <button type="button" data-testid="try-again" (click)="retry()">{{ common.tryAgain }}</button>
        }
      }
    </main>
  `,
})
export class CompleteSignupPage implements OnInit {
  protected readonly t = texts.completeSignup;
  /** The words this screen shares with the sign-up form: the label of the company name and the sentence for a closed sign-up. */
  protected readonly shared = texts.signup;
  protected readonly common = texts.common;
  private readonly account = inject(AccountApi);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  // Read once, here, and gone from the address bar from now on.
  private readonly token = takeTokenFromUrl(inject(ActivatedRoute), inject(Router));

  protected readonly form = inject(NonNullableFormBuilder).group({
    company: ['', requiredText],
    password: ['', Validators.required],
    repeat: ['', Validators.required],
  });
  protected readonly phase = signal<'loading' | 'form' | 'invalid' | 'registered' | 'closed' | 'error'>(
    this.token === null ? 'invalid' : 'loading',
  );
  protected readonly pending = signal<SignupPreview | null>(null);
  protected readonly busy = signal(false);
  protected readonly rules = signal<string[]>([]);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    if (this.token !== null) {
      void this.preview(this.token);
    }
  }

  /** The token is gone from the address bar but still held here: ask again with it. */
  protected retry(): void {
    if (this.token !== null) {
      this.phase.set('loading');
      void this.preview(this.token);
    }
  }

  protected async submit(): Promise<void> {
    const signup = this.pending();
    if (this.busy() || this.token === null || signup === null) {
      return;
    }
    const { company, password, repeat } = this.form.getRawValue();
    this.rules.set([]);
    this.error.set(null);
    if (company.trim() === '') {
      this.error.set(this.t.companyRequired);
      return;
    }
    if (this.form.invalid) {
      return;
    }
    if (password !== repeat) {
      this.error.set(this.common.passwordsDiffer);
      return;
    }
    this.busy.set(true);
    // The company name typed here is the one that counts (the name of the first request was only a suggestion); trimmed, because
    // Auth-Core refuses white space at either end.
    const result = await this.auth.completeSignup(this.token, password, company.trim());
    if (result.ok) {
      // Signed in (Auth-Core answered as it answers a login): the product opens at once, in place of this screen, so that Back does
      // not land on a link that is used up. The address is in no URL. The button stays off until the notes screen is open: the token
      // is used up, and a second Enter would only post it again. When the router cannot open it (it answers false or fails) the person
      // stays here, signed in, and the button is on again.
      try {
        await this.router.navigateByUrl(DEFAULT_RETURN_URL, { replaceUrl: true });
      } catch {
        // nothing to show: the company was made and the person is signed in
      }
    } else if (result.failure.kind === 'weak_password') {
      this.rules.set(result.failure.rules);
    } else if (result.failure.kind === 'invalid_token') {
      this.phase.set('invalid');
    } else if (result.failure.kind === 'already_registered') {
      this.phase.set('registered');
    } else if (result.failure.kind === 'signup_disabled') {
      this.phase.set('closed');
    } else if (result.failure.kind === 'invalid_request') {
      this.error.set(this.t.invalidRequest);
    } else if (result.failure.kind === 'too_many_requests') {
      // The notice bar above the screen says when to try again: the interceptor put it there.
    } else {
      this.error.set(this.common.somethingWrong);
    }
    this.busy.set(false);
  }

  private async preview(token: string): Promise<void> {
    const result = await this.account.previewSignup(token);
    if (result.ok) {
      this.pending.set(result.value);
      this.form.controls.company.setValue(result.value.company_name);
      this.phase.set('form');
    } else if (result.failure.kind === 'invalid_token') {
      this.phase.set('invalid');
    } else if (result.failure.kind === 'signup_disabled') {
      this.phase.set('closed');
    } else {
      this.phase.set('error');
    }
  }
}
