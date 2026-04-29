import { Component, input } from '@angular/core';
import { ProviderName, isProviderName, providerStartUrl } from '../account-api';
import { texts } from '../texts';

const PROVIDER_KEY = 'notes-web.provider';

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
  }
}
