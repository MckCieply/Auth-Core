import { Component, input } from '@angular/core';
import { ProviderName, isProviderName, providerStartUrl } from '../account-api';
import { texts } from '../texts';

const PROVIDER_KEY = 'notes-web.provider';
const REAUTH_KEY = 'notes-web.reauth';
/** As long as the provider round trip may last: the state Auth-Core keeps for it lives 10 minutes. */
const REAUTH_LIFETIME_MS = 10 * 60 * 1000;

/**
 * Keeps the provider the person chose, the name only, for the return screen ("connect Google"): the fragment that comes back does not
 * name it. Storage that is off or full changes nothing but that sentence.
 */
export function rememberProvider(name: ProviderName): void {
  try {
    sessionStorage.setItem(PROVIDER_KEY, name);
  } catch {
    // the return screen then says "Google or Microsoft"
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
 * A one-shot flag, set when the person follows "Confirm with ..." in the delete section: this tab started a re-authentication. It holds the
 * time it was set (milliseconds, digits only) and nothing else, never a ticket, an address or a kind. Without a flag that is at most 10
 * minutes old /provider-return hands no `reauth` ticket on, so a forged link cannot open the delete section, and a confirmation that was
 * abandoned cannot arm a link later. Storage that is off changes nothing here but that a confirmation is then not handed on.
 */
export function armReauth(): void {
  try {
    sessionStorage.setItem(REAUTH_KEY, String(Date.now()));
  } catch {
    // the confirmation then ends at the sign-in screen
  }
}

/**
 * Reads the flag and clears it, whether it is fresh or not: true once per `armReauth`, and only within 10 minutes of it. A value that is not a
 * time in the past (another text, a time ahead of this clock) is no flag. Storage that throws counts as no flag.
 */
export function takeReauth(): boolean {
  try {
    const value = sessionStorage.getItem(REAUTH_KEY);
    sessionStorage.removeItem(REAUTH_KEY);
    if (value === null || !/^\d{1,15}$/.test(value)) {
      return false;
    }
    const age = Date.now() - Number(value);
    return age >= 0 && age <= REAUTH_LIFETIME_MS;
  } catch {
    return false;
  }
}

/** The provider's name for a sentence, or "Google or Microsoft" when it is not known. */
export function providerTitle(name: ProviderName | null): string {
  return name === null ? texts.providers.either : (texts.providers.names[name] ?? texts.providers.either);
}

/**
 * The buttons of spec 0011: one link per offered provider to Auth-Core's start, in the order of GET /auth/options. A link and not a call,
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
  /** The confirmation of a deletion: the start asks for purpose=reauth. */
  readonly reauth = input(false);
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
      armReauth();
    }
  }
}
