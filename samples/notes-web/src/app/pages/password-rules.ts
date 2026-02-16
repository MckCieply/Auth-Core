import { Component, input } from '@angular/core';
import { texts } from '../texts';

/**
 * The line for a rule Auth-Core named in `weak_password`. Only the rules texts.ts names have a text of their own: a name like
 * "constructor" is not a rule with a text, so it gets the fallback line.
 */
export function ruleText(rule: string): string {
  return Object.hasOwn(texts.reset.rules, rule) ? texts.reset.rules[rule] : texts.reset.ruleOther;
}

/** The rules a password did not meet, one line each. Shared by the reset and the invite screens: the same policy. */
@Component({
  selector: 'app-password-rules',
  template: `
    @if (rules().length > 0) {
      <ul class="plain error" data-testid="rules">
        @for (rule of rules(); track $index) {
          <li>{{ ruleText(rule) }}</li>
        }
      </ul>
    }
  `,
})
export class PasswordRules {
  readonly rules = input.required<readonly string[]>();
  protected readonly ruleText = ruleText;
}
