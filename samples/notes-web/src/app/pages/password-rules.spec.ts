import { TestBed } from '@angular/core/testing';
import { PasswordRules, ruleText } from './password-rules';

describe('ruleText', () => {
  it.each([
    ['too_short', 'The password is too short.'],
    ['requires_upper', 'The password needs an uppercase letter.'],
    ['requires_lower', 'The password needs a lowercase letter.'],
    ['requires_digit', 'The password needs a digit.'],
  ])('%s has its own line', (rule, text) => {
    expect(ruleText(rule)).toBe(text);
  });

  it.each(['requires_symbol', 'constructor', 'toString', '__proto__', 'hasOwnProperty'])(
    '%s has no text of its own: the fallback line, never a function',
    (rule) => {
      expect(ruleText(rule)).toBe('The password does not meet a rule.');
    },
  );
});

describe('PasswordRules', () => {
  function show(rules: string[]) {
    const fixture = TestBed.createComponent(PasswordRules);
    fixture.componentRef.setInput('rules', rules);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('draws one line for each rule, in the order given', () => {
    const lines = Array.from(show(['too_short', 'requires_digit']).querySelectorAll('[data-testid="rules"] li'), (li) =>
      li.textContent?.trim(),
    );
    expect(lines).toEqual(['The password is too short.', 'The password needs a digit.']);
  });

  it('draws nothing for no rules', () => {
    expect(show([]).querySelector('ul')).toBeNull();
  });

  it('draws a line for each of two equal rules', () => {
    expect(show(['too_short', 'too_short']).querySelectorAll('li').length).toBe(2);
  });
});
