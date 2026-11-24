import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DEFAULT_RETURN_URL } from '../auth/auth.guard';
import { AuthService, Failure } from '../auth/auth.service';
import { texts } from '../texts';
import { ProviderName } from '../account-api';
import { providerTitle, rememberedProvider, takePending, takePendingConnect, takeReauth } from './provider-buttons';

/**
 * Spec 0011: Auth-Core sends the browser here after the provider, with `#ticket=...&purpose=...` or `#error=...&purpose=...`. The fragment is
 * read once, in the constructor; the address is replaced by /provider-return before anything else, and every way out of this screen is a
 * navigation that replaces the history entry: the ticket is never in the address bar while a request runs, never in the history, never in
 * storage. It lives in this component's memory and in the navigation state of the next screen. A `reauth` ticket is handed on only when this
 * tab started the confirmation (the one-shot flag `armReauth` sets, read and cleared here); otherwise the link was not asked for.
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
        <button type="button" data-testid="try-again" (click)="retry()">{{ common.tryAgain }}</button>
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
  // What "Try again" repeats after a rate limit: the ticket is still good.
  private action: 'exchange' | 'confirm' | 'connect' = 'exchange';
  // Kept for show(): the message after "Try again" depends on it too.
  private purpose: string | null = null;

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
    // Read and cleared on every return that gets this far, so that an abandoned confirmation cannot arm a later forged link.
    const armed = takeReauth();
    const error = this.fragment.get('error');
    const purpose = this.fragment.get('purpose');
    this.purpose = purpose;
    if (error !== null) {
      // An own member only: the fragment is the visitor's text, and `constructor` or `__proto__` must not reach Object.prototype.
      const message = Object.hasOwn(this.t.errors, error) ? this.t.errors[error] : this.t.errors['provider_error'];
      if ((armed === 'connect' && purpose === 'connect') || (armed === 'confirm' && purpose === 'reauth')) {
        // The settings screen started this round trip: the sentence goes there, and a connect that waited for it is over.
        takePendingConnect();
        await this.go('/settings', { notice: message });
      } else {
        this.message.set(message);
      }
    } else if (this.ticket === '') {
      await this.go('/login');
    } else if (purpose === 'reauth') {
      // Only for a confirmation this tab started, and to the screen it was started for (spec 0012: the flag's target).
      if (armed === 'delete_account') {
        // The settings screen opens "Delete account" ready to confirm: the ticket is spent there, not here.
        await this.go('/settings', { reauthTicket: this.ticket });
      } else if (armed === 'delete_company') {
        await this.go('/team', { reauthTicket: this.ticket });
      } else if (armed === 'confirm') {
        await this.confirm();
      } else {
        await this.go('/login');
      }
    } else if (purpose === 'connect') {
      await (armed === 'connect' ? this.connect() : this.go('/login'));
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

  /** "Try again" after a rate limit: the same action with the same ticket. */
  protected async retry(): Promise<void> {
    if (this.action === 'confirm') {
      await this.confirm();
    } else if (this.action === 'connect') {
      await this.connect();
    } else {
      await this.exchange();
    }
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

  /** Spec 0012: the confirmation of the settings screen. Then /settings, which says so when a connect waits for it. */
  private async confirm(): Promise<void> {
    if (this.asking) {
      return;
    }
    this.asking = true;
    this.limited.set(false);
    try {
      await this.confirmOnce();
    } finally {
      this.asking = false;
    }
  }

  private async confirmOnce(): Promise<void> {
    const result = await this.auth.confirmWithTicket(this.ticket);
    if (!result.ok && result.failure.kind === 'too_many_requests') {
      // The bar says when to try again; the ticket and the connect that waits for it are kept.
      this.action = 'confirm';
      this.limited.set(true);
      return;
    }
    const pending = takePending();
    if (result.ok) {
      const again = pending === null ? null : pending.kind === 'connect' ? texts.settings.connectAgain : texts.settings.disconnectAgain;
      await this.go('/settings', pending === null || again === null ? undefined : { notice: again(providerTitle(pending.provider)) });
    } else if (result.failure.kind !== 'session_required') {
      await this.go('/settings', { notice: await this.sentenceOf(result.failure) });
    }
  }

  /**
   * Spec 0012: the connect of the settings screen. A closed window hands the ticket back to /settings in the navigation state, which asks for
   * a confirmation and sends it again; every other answer is a sentence there.
   */
  private async connect(): Promise<void> {
    if (this.asking) {
      return;
    }
    this.asking = true;
    this.limited.set(false);
    try {
      await this.connectOnce();
    } finally {
      this.asking = false;
    }
  }

  private async connectOnce(): Promise<void> {
    const provider = rememberedProvider();
    const result = await this.auth.connect(this.ticket);
    if (!result.ok && result.failure.kind === 'too_many_requests') {
      this.action = 'connect';
      this.limited.set(true);
      return;
    }
    if (result.ok) {
      await this.go('/settings', { notice: texts.settings.connectedNow(providerTitle(provider)) });
    } else if (result.failure.kind === 'confirmation_required') {
      await this.go('/settings', { connectTicket: this.ticket, provider });
    } else if (result.failure.kind !== 'session_required') {
      await this.go('/settings', { notice: await this.sentenceOf(result.failure, provider) });
    }
  }

  private async sentenceOf(failure: Failure, provider: ProviderName | null = null): Promise<string> {
    const s = texts.settings;
    const title = providerTitle(provider);
    switch (failure.kind) {
      case 'provider_in_use':
        return s.providerInUse(title);
      case 'provider_already_linked':
        return s.providerAlreadyLinked(title);
      case 'address_mismatch': {
        if (this.auth.me() === null) {
          await this.auth.loadMe();
        }
        return s.addressMismatch(title, this.auth.me()?.email ?? '');
      }
      case 'email_not_verified':
        return s.emailNotVerified(title);
      case 'no_email':
        return s.noEmail(title);
      case 'invalid_ticket':
        return s.invalidTicket;
      case 'reauth_failed':
        return s.reauthFailed;
      case 'no_membership':
        return texts.login.noMembership;
      default:
        return this.common.somethingWrong;
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
    const invite = this.purpose === 'invite';
    const messages: Partial<Record<Failure['kind'], string>> = {
      invalid_ticket: this.t.invalidTicket,
      no_account: this.t.noAccount,
      no_email: this.t.noEmail,
      already_registered: invite ? this.t.alreadyRegisteredInvite : this.t.alreadyRegistered,
      address_mismatch: this.t.addressMismatch,
      already_member: invite ? texts.invite.alreadyMember : this.t.alreadyMember,
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
