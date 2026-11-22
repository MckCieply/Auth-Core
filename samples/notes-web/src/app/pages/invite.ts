import { Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AccountApi, InvitePreview, Outcome, ProviderName } from '../account-api';
import { AuthService } from '../auth/auth.service';
import { texts } from '../texts';
import { PasswordRules } from './password-rules';
import { ProviderButtons } from './provider-buttons';
import { takeTokenFromUrl } from './token-from-url';

/** Folds ASCII capitals only, as the server compares addresses; `toLowerCase()` would fold other letters too. */
function asciiLower(text: string): string {
  return text.replace(/[A-Z]/g, (letter) => String.fromCharCode(letter.charCodeAt(0) + 32));
}

@Component({
  selector: 'app-invite',
  imports: [ReactiveFormsModule, PasswordRules, ProviderButtons],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @switch (phase()) {
        @case ('loading') {
          <p class="status" role="status">{{ t.loading }}</p>
        }
        @case ('form') {
          @if (invite(); as invitation) {
            <p data-testid="invite-join">
              {{ t.joinPrefix }} <strong>{{ invitation.org_name }}</strong> {{ t.joinMiddle }}
              <strong>{{ invitation.role }}</strong>
            </p>
            @if (invitation.has_account) {
              <!-- Spec 0017: the address has an account. Accepting adds the company to it, with no password and nothing lost. -->
              <p data-testid="invite-existing">{{ t.existingAccount(invitation.email, invitation.org_name) }}</p>
              <button type="button" data-testid="accept" [disabled]="busy()" (click)="acceptExisting()">{{ t.accept }}</button>
              <app-provider-buttons [providers]="providers()" action="accept" [invite]="token" />
            } @else {
              <!-- Spec 0011: the invitation can be accepted with a provider; the token rides on the start, never in this page's URL. -->
              <app-provider-buttons [providers]="providers()" action="accept" [invite]="token" />
              <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
                <label for="email">{{ common.email }}</label>
                <input id="email" type="email" [value]="invitation.email" readonly autocomplete="username" />
                <p class="status">{{ t.setsPassword(invitation.email) }}</p>
                <label for="password">{{ common.newPassword }}</label>
                <input id="password" type="password" formControlName="password" autocomplete="new-password" />
                <label for="repeat">{{ common.repeatPassword }}</label>
                <input id="repeat" type="password" formControlName="repeat" autocomplete="new-password" />
                <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
              </form>
            }
          }
          <app-password-rules [rules]="rules()" />
          @if (error(); as text) {
            <p class="error" role="alert">{{ text }}</p>
          }
        }
        @case ('member') {
          <p class="error" role="alert">{{ t.alreadyMember }}</p>
        }
        @case ('invalid') {
          <p class="error" role="alert" data-testid="invite-invalid">{{ t.invalid }}</p>
        }
        @case ('limited') {
          <button type="button" data-testid="try-again" (click)="retry()">{{ common.tryAgain }}</button>
        }
        @case ('error') {
          <p class="error" role="alert">{{ common.somethingWrong }}</p>
          <button type="button" data-testid="try-again" (click)="retry()">{{ common.tryAgain }}</button>
        }
      }
    </main>
  `,
})
export class InvitePage implements OnInit {
  protected readonly t = texts.invite;
  protected readonly common = texts.common;
  private readonly account = inject(AccountApi);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  // Read once, here, and gone from the address bar from now on.
  protected readonly token = takeTokenFromUrl(inject(ActivatedRoute), inject(Router));

  protected readonly form = inject(NonNullableFormBuilder).group({
    password: ['', Validators.required],
    repeat: ['', Validators.required],
  });
  protected readonly phase = signal<'loading' | 'form' | 'member' | 'invalid' | 'limited' | 'error'>(
    this.token === null ? 'invalid' : 'loading',
  );
  protected readonly invite = signal<InvitePreview | null>(null);
  protected readonly busy = signal(false);
  protected readonly rules = signal<string[]>([]);
  protected readonly error = signal<string | null>(null);
  /** The offered providers (spec 0011): "Accept with ..." for every address (spec 0017: an existing account has the buttons too). */
  protected readonly providers = signal<ProviderName[]>([]);

  ngOnInit(): void {
    if (this.token !== null) {
      void this.account.options().then((options) => this.providers.set(options.providers));
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
    const invitation = this.invite();
    if (this.busy() || this.form.invalid || this.token === null || invitation === null) {
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
    await this.answer(await this.account.acceptInvite(this.token, password), invitation);
    this.busy.set(false);
  }

  /** The one button of an address that has an account (spec 0017): the token goes alone, no password. */
  protected async acceptExisting(): Promise<void> {
    const invitation = this.invite();
    if (this.busy() || this.token === null || invitation === null) {
      return;
    }
    this.error.set(null);
    this.busy.set(true);
    await this.answer(await this.account.acceptInvite(this.token, null), invitation);
    this.busy.set(false);
  }

  private async answer(result: Outcome, invitation: InvitePreview): Promise<void> {
    if (result.ok) {
      // The email goes in the navigation state: it is never in the URL. The button stays off until the next screen is open: the
      // token is used up, and a second Enter would only post it again.
      // When the router cannot open it (it answers false or fails) the person stays here and the button is on again.
      try {
        if (invitation.has_account && (await this.switchIntoCompany(invitation))) {
          await this.router.navigate(['/notes']);
        } else {
          await this.router.navigate(['/login'], { state: { email: invitation.email } });
        }
      } catch {
        // nothing to show: the invitation is accepted
      }
    } else if (result.failure.kind === 'weak_password') {
      this.rules.set(result.failure.rules);
    } else if (result.failure.kind === 'invalid_token') {
      this.phase.set('invalid');
    } else if (result.failure.kind === 'already_member') {
      this.phase.set('member');
    } else if (result.failure.kind === 'too_many_requests') {
      // The notice bar above the screen says when to try again: the interceptor put it there.
    } else if (result.failure.kind === 'private_account_exists') {
      // An older server (spec 0010) still sends this. Nothing here can answer it: the generic error, and no second preview.
      this.phase.set('error');
    } else {
      this.error.set(this.common.somethingWrong);
    }
  }

  /**
   * After the acceptance of an existing account: when the person signed in here is the invited one (the address up to ASCII case) and
   * the preview named the company, move the session into it. False: the page opens /login instead (also after a switch that did not
   * end in the company, `switchTo` has then done what it does).
   */
  private async switchIntoCompany(invitation: InvitePreview): Promise<boolean> {
    const me = this.auth.me();
    if (me === null || invitation.org_id === null || asciiLower(me.email) !== asciiLower(invitation.email)) {
      return false;
    }
    try {
      return (await this.auth.switchTo(invitation.org_id)) === 'switched';
    } catch {
      // The token is used up by now: a throw is a switch that did not happen, and the person signs in again.
      return false;
    }
  }

  private async preview(token: string): Promise<void> {
    const result = await this.account.previewInvite(token);
    if (result.ok) {
      this.invite.set(result.value);
      this.phase.set('form');
    } else if (result.failure.kind === 'invalid_token') {
      this.phase.set('invalid');
    } else if (result.failure.kind === 'already_member') {
      this.phase.set('member');
    } else if (result.failure.kind === 'too_many_requests') {
      // The notice bar above the screen says when to try again: the interceptor put it there. Only the button is shown.
      this.phase.set('limited');
    } else {
      this.phase.set('error');
    }
  }
}
