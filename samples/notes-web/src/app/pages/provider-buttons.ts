import { Component, input } from '@angular/core';
import { ProviderName, isProviderName, providerStartUrl } from '../account-api';
import { texts } from '../texts';

const PROVIDER_KEY = 'notes-web.provider';
const REAUTH_KEY = 'notes-web.reauth';

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
 * A one-shot flag, set when the person follows "Confirm with ..." in the delete section: this tab started a re-authentication. It is the
 * flag only (the value `1`), never a ticket. Without it /provider-return hands no `reauth` ticket on, so a forged link cannot open the
 * delete section. Storage that is off changes nothing here but that a confirmation is then not handed on.
 */
export function armReauth(): void {
  try {
    sessionStorage.setItem(REAUTH_KEY, '1');
  } catch {
    // the confirmation then ends at the sign-in screen
  }
}

/** Reads the flag and clears it: true once per `armReauth`. Storage that throws counts as no flag. */
export function takeReauth(): boolean {
  try {
    const armed = sessionStorage.getItem(REAUTH_KEY) === '1';
    sessionStorage.removeItem(REAUTH_KEY);
    return armed;
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
