import { Component, InjectionToken, input } from '@angular/core';
import { ProviderName, isProviderName, providerStartUrl } from '../account-api';
import { texts } from '../texts';

const PROVIDER_KEY = 'notes-web.provider';
const REAUTH_KEY = 'notes-web.reauth';
const PENDING_CONNECT_KEY = 'notes-web.pending-connect';
/** As long as the provider round trip may last: the state Auth-Core keeps for it lives 10 minutes. */
const REAUTH_LIFETIME_MS = 10 * 60 * 1000;

/** What a confirmation round trip is for (spec 0012): the screen it comes back to, and what that screen does with the ticket. */
export type ReauthTarget = 'delete_account' | 'delete_company' | 'confirm' | 'connect';

const FLAG = /^(delete_account|delete_company|confirm|connect):(\d{1,15})$/;
const PENDING_CONNECT = /^([a-z]{1,20}):([a-z]{1,20}):(\d{1,15})$/;

/** Opens a page outside the app (a provider's start): a function of its own, so that a spec can see the address without leaving. */
export const ASSIGN_LOCATION = new InjectionToken<(url: string) => void>('ASSIGN_LOCATION', {
  providedIn: 'root',
  factory: () => (url: string) => window.location.assign(url),
});

/**
 * Keeps the provider the person chose, the name only, for the return screen ("connect Google"): the fragment that comes back does not
 * name it. Storage that is off or full changes nothing but that sentence.
 */
export function rememberProvider(name: ProviderName): void {
  try {
    sessionStorage.setItem(PROVIDER_KEY, name);
  } catch {
    // the return screen then says "Google, Microsoft or GitHub"
  }
}

/** The provider of the last button this tab followed, or null. */
export function rememberedProvider(): ProviderName | null {
  try {
    const name = sessionStorage.getItem(PROVIDER_KEY);
    return isProviderName(name) ? name : null;
  } catch {
    return null;
  }
}

/**
 * A one-shot flag, set when this tab starts a round trip whose ticket comes back to the app (spec 0011, As built D30; spec 0012: with its
 * target): `<target>:<the time it was set, in milliseconds>`, and nothing else, never a ticket, an address or a kind. Without a flag of the
 * fitting target that is at most 10 minutes old, /provider-return hands nothing on and sends nothing, so a forged link does nothing, and a
 * confirmation that was abandoned cannot arm a link later. Storage that is off changes nothing here but that the ticket is then not used.
 */
export function armReauth(target: ReauthTarget): void {
  try {
    sessionStorage.setItem(REAUTH_KEY, `${target}:${Date.now()}`);
  } catch {
    // the round trip then ends at the sign-in screen
  }
}

/**
 * Reads the flag and clears it, whether it is fresh or not: its target once per `armReauth`, and only within 10 minutes of it. A value of
 * another shape (the bare time of spec 0011 included), or a time that is not in the past, is no flag. Storage that throws counts as no flag.
 */
export function takeReauth(): ReauthTarget | null {
  try {
    const value = sessionStorage.getItem(REAUTH_KEY);
    sessionStorage.removeItem(REAUTH_KEY);
    const match = value === null ? null : FLAG.exec(value);
    if (match === null) {
      return null;
    }
    const age = Date.now() - Number(match[2]);
    return age >= 0 && age <= REAUTH_LIFETIME_MS ? (match[1] as ReauthTarget) : null;
  } catch {
    return null;
  }
}

/** What waits for a provider confirmation of the settings screen (spec 0012): a connect or a disconnect, and the provider it names. */
export interface PendingAction {
  kind: 'connect' | 'disconnect';
  provider: ProviderName;
}

function rememberPending(kind: PendingAction['kind'], name: ProviderName): void {
  try {
    sessionStorage.setItem(PENDING_CONNECT_KEY, `${kind}:${name}:${Date.now()}`);
  } catch {
    // the settings screen then says only that the confirmation worked
  }
}

/** The connect that waits for a confirmation: the name and the time, never a ticket. One value for the tab, the last one wins. */
export function rememberPendingConnect(name: ProviderName): void {
  rememberPending('connect', name);
}

/** The disconnect that waits for a confirmation. */
export function rememberPendingDisconnect(name: ProviderName): void {
  rememberPending('disconnect', name);
}

/** Reads and clears what waits, or null: no such value, one of another shape, a name this app does not know, or one older than 10 minutes. */
export function takePending(): PendingAction | null {
  try {
    const value = sessionStorage.getItem(PENDING_CONNECT_KEY);
    sessionStorage.removeItem(PENDING_CONNECT_KEY);
    const match = value === null ? null : PENDING_CONNECT.exec(value);
    if (match === null) {
      return null;
    }
    const [, kind, name] = match;
    const age = Date.now() - Number(match[3]);
    return (kind === 'connect' || kind === 'disconnect') && age >= 0 && age <= REAUTH_LIFETIME_MS && isProviderName(name)
      ? { kind, provider: name }
      : null;
  } catch {
    return null;
  }
}

/** Reads and clears what waits; the provider only when it was a connect. */
export function takePendingConnect(): ProviderName | null {
  const pending = takePending();
  return pending !== null && pending.kind === 'connect' ? pending.provider : null;
}

/** The provider's name for a sentence, or "Google, Microsoft or GitHub" when it is not known. */
export function providerTitle(name: ProviderName | null): string {
  return name === null ? texts.providers.either : (texts.providers.names[name] ?? texts.providers.either);
}

/**
 * The buttons of specs 0011 and 0012: one link per offered provider to Auth-Core's start, in the order of GET /auth/options. A link and not a call,
 * because the browser leaves for the provider. Nothing at all is drawn without a provider.
 */
@Component({
  selector: 'app-provider-buttons',
  template: `
    @if (providers().length > 0) {
      <ul class="plain providers" data-testid="provider-buttons">
        @for (name of providers(); track name) {
          <li>
            <a [href]="hrefOf(name)" [attr.data-testid]="'provider-' + name" (click)="chose(name)">{{ labelOf(name) }}</a>
          </li>
        }
      </ul>
      @if (separator()) {
        <p class="or" data-testid="provider-or">{{ t.or }}</p>
      }
    }
  `,
})
export class ProviderButtons {
  protected readonly t = texts.providers;
  readonly providers = input.required<readonly ProviderName[]>();
  readonly action = input<'continue' | 'signup' | 'accept' | 'confirm'>('continue');
  /** The invitation token of /invite: the start carries it, so that the round trip accepts the invitation. */
  readonly invite = input<string | null>(null);
  /** A confirmation: the start asks for purpose=reauth. */
  readonly reauth = input(false);
  /** Spec 0012: the screen a confirmation comes back to (the flag's target). */
  readonly target = input<ReauthTarget>('delete_account');
  /** "or" after the buttons, before a form. */
  readonly separator = input(true);

  protected hrefOf(name: ProviderName): string {
    const invite = this.invite();
    if (this.reauth()) {
      return providerStartUrl(name, { reauth: true });
    }
    return providerStartUrl(name, invite === null ? null : { invite });
  }

  protected labelOf(name: ProviderName): string {
    const title = providerTitle(name);
    switch (this.action()) {
      case 'signup':
        return this.t.signUpWith(title);
      case 'accept':
        return this.t.acceptWith(title);
      case 'confirm':
        return this.t.confirmWith(title);
      default:
        return this.t.continueWith(title);
    }
  }

  protected chose(name: ProviderName): void {
    rememberProvider(name);
    if (this.reauth()) {
      armReauth(this.target());
    }
  }
}
