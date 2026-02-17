import { Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AccountApi, InvitePreview } from '../account-api';
import { texts } from '../texts';
import { PasswordRules } from './password-rules';
import { takeTokenFromUrl } from './token-from-url';

@Component({
  selector: 'app-invite',
  imports: [ReactiveFormsModule, PasswordRules],
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
  // Read once, here, and gone from the address bar from now on.
  private readonly token = takeTokenFromUrl(inject(ActivatedRoute));

  protected readonly form = inject(NonNullableFormBuilder).group({
    password: ['', Validators.required],
    repeat: ['', Validators.required],
  });
  protected readonly phase = signal<'loading' | 'form' | 'member' | 'invalid' | 'error'>(
    this.token === null ? 'invalid' : 'loading',
  );
  protected readonly invite = signal<InvitePreview | null>(null);
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
    const result = await this.account.acceptInvite(this.token, password);
    if (result.ok) {
      // The email goes in the navigation state: it is never in the URL. The button stays off until the login is open: the
      // token is used up, and a second Enter would only post it again.
      await this.router.navigate(['/login'], { state: { email: invitation.email } });
    } else if (result.failure.kind === 'weak_password') {
      this.rules.set(result.failure.rules);
    } else if (result.failure.kind === 'invalid_token') {
      this.phase.set('invalid');
    } else if (result.failure.kind === 'already_member') {
      this.phase.set('member');
    } else {
      this.error.set(this.common.somethingWrong);
    }
    this.busy.set(false);
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
    } else {
      this.phase.set('error');
    }
  }
}
