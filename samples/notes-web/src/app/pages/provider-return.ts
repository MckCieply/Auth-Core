import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DEFAULT_RETURN_URL } from '../auth/auth.guard';
import { AuthService, Failure } from '../auth/auth.service';
import { texts } from '../texts';
import { rememberedProvider } from './provider-buttons';

/**
 * Spec 0011: Auth-Core sends the browser here after the provider, with `#ticket=...&purpose=...` or `#error=...&purpose=...`. The fragment is
 * read once, in the constructor; the address is replaced by /provider-return before anything else, and every way out of this screen is a
 * navigation that replaces the history entry: the ticket is never in the address bar while a request runs, never in the history, never in
 * storage. It lives in this component's memory and in the navigation state of the next screen.
 */
@Component({
  selector: 'app-provider-return',
  imports: [RouterLink],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @if (message(); as text) {
        <p class="error" role="alert" data-testid="provider-message">{{ text }}</p>
        <p><a routerLink="/login" data-testid="provider-back">{{ common.backToSignIn }}</a></p>
      } @else if (limited()) {
        <button type="button" data-testid="try-again" (click)="exchange()">{{ common.tryAgain }}</button>
      } @else {
        <p class="status" role="status">{{ t.working }}</p>
      }
    </main>
  `,
})
export class ProviderReturnPage implements OnInit {
  protected readonly t = texts.providerReturn;
  protected readonly common = texts.common;
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  // Read once, here: the first value of each parameter, the others (and any extra ones) are never looked at.
  private readonly fragment = new URLSearchParams(this.route.snapshot.fragment ?? '');
  private readonly ticket = this.fragment.get('ticket') ?? '';
  private asking = false;

  protected readonly message = signal<string | null>(null);
  protected readonly limited = signal(false);

  ngOnInit(): void {
    void this.handle();
  }

  private async handle(): Promise<void> {
    // After the navigation that is building this screen: the same route, no fragment, the history entry replaced. The screen is kept.
    await Promise.resolve();
    if (!(await this.removeFragment())) {
      // The ticket may still be in the address bar: nothing is sent and nothing is handed on while it is.
      this.message.set(this.t.errors['provider_error']);
      return;
    }
    const error = this.fragment.get('error');
    const purpose = this.fragment.get('purpose');
    if (error !== null) {
      // An own member only: the fragment is the visitor's text, and `constructor` or `__proto__` must not reach Object.prototype.
      this.message.set(Object.hasOwn(this.t.errors, error) ? this.t.errors[error] : this.t.errors['provider_error']);
    } else if (this.ticket === '') {
      await this.go('/login');
    } else if (purpose === 'reauth') {
      // The notes screen opens "Delete account" ready to confirm: the ticket is spent there, not here.
      await this.go(DEFAULT_RETURN_URL, { reauthTicket: this.ticket });
    } else if (purpose === 'sign_in' || purpose === 'invite') {
      await this.exchange();
    } else {
      await this.go('/login');
    }
  }

  /** True when the address no longer holds the fragment (or anything else after the path). A navigation that is refused, cancelled or fails says false. */
  private async removeFragment(): Promise<boolean> {
    try {
      await this.router.navigate([], { relativeTo: this.route, replaceUrl: true });
    } catch {
      return false;
    }
    // No navigation is needed (and `false` is the answer) when there was nothing to remove.
    return !/[#?]/.test(this.router.url);
  }

  protected async exchange(): Promise<void> {
    if (this.asking) {
      return;
    }
    this.asking = true;
    this.limited.set(false);
    try {
      const result = await this.auth.exchangeTicket(this.ticket);
      if (!result.ok) {
        this.show(result.failure);
      } else if (result.next === null) {
        await this.go(DEFAULT_RETURN_URL);
      } else if (result.next.next === 'link') {
        const { email, emailVerified } = result.next;
        await this.go('/provider-link', { ticket: this.ticket, email, emailVerified, provider: rememberedProvider() });
      } else {
        const { email, kinds } = result.next;
        await this.go('/provider-finish', { ticket: this.ticket, email, kinds });
      }
    } finally {
      this.asking = false;
    }
  }

  private async go(url: string, state?: Record<string, unknown>): Promise<void> {
    try {
      await this.router.navigateByUrl(url, { replaceUrl: true, state });
    } catch {
      // the person stays on this screen
    }
  }

  private show(failure: Failure): void {
    const messages: Partial<Record<Failure['kind'], string>> = {
      invalid_ticket: this.t.invalidTicket,
      no_account: this.t.noAccount,
      no_email: this.t.noEmail,
      already_registered: this.t.alreadyRegistered,
      address_mismatch: this.t.addressMismatch,
      already_member: this.t.alreadyMember,
      private_account_exists: this.t.privateAccountExists,
      provider_already_linked: this.t.providerAlreadyLinked,
      invalid_token: this.t.invalidToken,
      no_membership: texts.login.noMembership,
    };
    if (failure.kind === 'too_many_requests') {
      // The bar above the screen says when to try again; the ticket is still good for a few minutes.
      this.limited.set(true);
    } else {
      this.message.set(messages[failure.kind] ?? this.common.somethingWrong);
    }
  }
}
