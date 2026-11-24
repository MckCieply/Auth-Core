import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AccountApi, ProviderName, isProviderName, providerStartUrl } from '../account-api';
import { AuthService, Failure } from '../auth/auth.service';
import { texts } from '../texts';
import { ConfirmDialog } from './confirm-dialog';
import { DeleteAccountSection } from './delete-account';
import { acceptablePassword } from './password-input';
import {
  ASSIGN_LOCATION,
  armReauth,
  providerTitle,
  rememberPendingConnect,
  rememberPendingDisconnect,
  rememberProvider,
  takePendingConnect,
} from './provider-buttons';
import { PasswordRules } from './password-rules';
import { keepTicketOutOfHistory, reauthTicketOf } from './ticket-history';

/** What waits for the confirmation: a connect to start, a disconnect, or a connect ticket the return screen handed back. */
type Pending =
  | { kind: 'connect'; provider: ProviderName }
  | { kind: 'disconnect'; provider: ProviderName }
  | { kind: 'resend'; provider: ProviderName | null; ticket: string };

/** What /provider-return handed on in the navigation state (spec 0012): a sentence, or a connect ticket refused for a closed window. */
function handedOn(router: Router): { notice: string | null; pending: Pending | null } {
  const state = router.currentNavigation()?.extras.state ?? {};
  const notice: unknown = state['notice'];
  const ticket: unknown = state['connectTicket'];
  const provider: unknown = state['provider'];
  return {
    notice: typeof notice === 'string' ? notice : null,
    pending: typeof ticket === 'string' && ticket !== '' ? { kind: 'resend', provider: isProviderName(provider) ? provider : null, ticket } : null,
  };
}

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
  imports: [ReactiveFormsModule, RouterLink, PasswordRules, ConfirmDialog, DeleteAccountSection],
  template: `
    <main class="wide">
      <h1 tabindex="-1" data-testid="settings-title">{{ t.title }}</h1>
      @if (me(); as person) {
        @if (notice(); as text) {
          <p class="status" role="status" data-testid="settings-notice">{{ text }}</p>
        }
        @if (actionError(); as text) {
          <p class="error" role="alert" data-testid="settings-error">{{ text }}</p>
        }
        @if (pending() !== null) {
          <app-confirm-dialog [providers]="offered()" (confirmed)="confirmed()" (cancelled)="cancelPending()" (sendLink)="sendLink()" />
        }
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
                  <button
                    type="button"
                    [attr.data-testid]="'disconnect-' + row.name"
                    [disabled]="!row.canDisconnect || locked()"
                    (click)="disconnect(row.name)"
                  >
                    {{ t.disconnect }}
                  </button>
                  @if (!row.canDisconnect) {
                    <p class="status" data-testid="disconnect-blocked">{{ t.disconnectBlocked }}</p>
                  }
                } @else if (row.offered) {
                  <button type="button" [attr.data-testid]="'connect-' + row.name" [disabled]="locked()" (click)="connect(row.name)">
                    {{ t.connect }}
                  </button>
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
        <app-delete-account [ticket]="reauthTicket" />
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
  private readonly router = inject(Router);
  private readonly assign = inject(ASSIGN_LOCATION);
  private readonly fromReturn = handedOn(this.router);
  /** The ticket of "Confirm with {Provider}" for "Delete account", read now and handed to the section (it is created later). */
  protected readonly reauthTicket = reauthTicketOf(this.router);

  /** A sentence of the last action, or the one the return screen handed on. */
  protected readonly notice = signal<string | null>(this.fromReturn.notice);
  protected readonly actionError = signal<string | null>(null);
  /** Opens the dialog while it is set. */
  protected readonly pending = signal<Pending | null>(this.fromReturn.pending);

  protected readonly me = this.auth.me;
  /** The providers GET /auth/options offers, in its order; empty until it answers, and when it fails. */
  protected readonly offered = signal<ProviderName[]>([]);
  /** A connect or a disconnect in flight (spec 0012: each button is disabled while its request is). */
  protected readonly busy = signal(false);
  protected readonly passwordBusy = signal(false);
  /** The rows act on one thing at a time: not during a request, a password change or the open dialog. */
  protected readonly locked = computed(() => this.busy() || this.passwordBusy() || this.pending() !== null);
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
    if (this.fromReturn.pending !== null || this.reauthTicket !== null) {
      // A ticket lives in memory only: the history entry is replaced without it.
      void keepTicketOutOfHistory(this.router);
    }
    if (this.fromReturn.pending !== null) {
      if (this.fromReturn.pending.kind === 'resend' && this.fromReturn.pending.provider !== null) {
        // A confirmation with a provider from this dialog leaves the page, and the ticket with it (one auth_pt): the screen it comes back to
        // names the provider to connect again (spec 0012, "Confirmed. Connect {Provider} again.").
        rememberPendingConnect(this.fromReturn.pending.provider);
      }
    }
  }

  /** The window of this device's session, as the last GET /auth/me said, is still open. */
  private windowOpen(): boolean {
    const until = this.me()?.confirmed_until;
    return typeof until === 'string' && Date.parse(until) > Date.now();
  }

  protected async connect(name: ProviderName): Promise<void> {
    if (this.locked()) {
      return;
    }
    if (!(await this.readWindow())) {
      rememberPendingConnect(name);
      this.pending.set({ kind: 'connect', provider: name });
      return;
    }
    this.startConnect(name);
  }

  /** A connect starts its round trip only with the window open (spec 0012). */
  private startConnect(name: ProviderName): void {
    rememberProvider(name);
    armReauth('connect');
    this.assign(providerStartUrl(name, { connect: true }));
  }

  protected async disconnect(name: ProviderName): Promise<void> {
    if (this.locked()) {
      return;
    }
    if (!(await this.readWindow())) {
      rememberPendingDisconnect(name); // replaces a connect that was left open: the confirmation names this disconnect
      this.pending.set({ kind: 'disconnect', provider: name });
      return;
    }
    await this.unlink(name);
  }

  /** Reads the person again, with the buttons disabled meanwhile; true when the window is open. */
  private async readWindow(): Promise<boolean> {
    this.busy.set(true);
    this.notice.set(null);
    this.actionError.set(null);
    await this.auth.loadMe();
    this.busy.set(false);
    return this.windowOpen();
  }

  private async unlink(name: ProviderName): Promise<void> {
    this.busy.set(true);
    const result = await this.auth.unlink(name);
    this.busy.set(false);
    if (result.ok) {
      this.notice.set(this.t.disconnectedNow(providerTitle(name)));
    } else if (result.failure.kind === 'confirmation_required') {
      rememberPendingDisconnect(name);
      this.pending.set({ kind: 'disconnect', provider: name });
    } else {
      await this.refusedAction(result.failure, name);
    }
  }

  private async resend(ticket: string, provider: ProviderName | null): Promise<void> {
    this.busy.set(true);
    const result = await this.auth.connect(ticket);
    this.busy.set(false);
    if (result.ok) {
      this.notice.set(this.t.connectedNow(providerTitle(provider)));
    } else {
      await this.refusedAction(result.failure, provider);
    }
  }

  /** The dialog's password confirmation worked: what waited goes on at once. */
  protected async confirmed(): Promise<void> {
    const waiting = this.pending();
    this.pending.set(null);
    this.forgetPendingConnect(); // a password confirmation goes on here: no round trip comes back to name the connect
    if (waiting === null) {
      return;
    }
    if (waiting.kind === 'connect') {
      this.startConnect(waiting.provider);
    } else if (waiting.kind === 'disconnect') {
      await this.unlink(waiting.provider);
    } else {
      await this.resend(waiting.ticket, waiting.provider);
    }
  }

  protected cancelPending(): void {
    this.pending.set(null);
    this.forgetPendingConnect();
  }

  /** Whatever connect was waiting is over: it goes on here after a password, the person cancelled, or another action opened the dialog. */
  private forgetPendingConnect(): void {
    takePendingConnect();
  }

  /** The answers of a connect or a disconnect, one sentence each; the rows are read again, since they may have changed. */
  private async refusedAction(failure: Failure, provider: ProviderName | null): Promise<void> {
    const title = providerTitle(provider);
    const sentences: Partial<Record<Failure['kind'], string>> = {
      last_login: this.t.lastLogin,
      not_found: this.t.notFound,
      provider_in_use: this.t.providerInUse(title),
      provider_already_linked: this.t.providerAlreadyLinked(title),
      email_not_verified: this.t.emailNotVerified(title),
      no_email: this.t.noEmail(title),
      invalid_ticket: this.t.invalidTicket,
      no_membership: texts.login.noMembership,
      confirmation_required: this.t.reauthFailed,
    };
    if (failure.kind === 'session_required' || failure.kind === 'too_many_requests') {
      return; // signed out by the interceptor, or the bar says when to try again
    }
    if (failure.kind === 'address_mismatch') {
      this.actionError.set(this.t.addressMismatch(title, this.me()?.email ?? ''));
    } else {
      this.actionError.set(sentences[failure.kind] ?? this.common.somethingWrong);
    }
    await this.auth.loadMe();
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
