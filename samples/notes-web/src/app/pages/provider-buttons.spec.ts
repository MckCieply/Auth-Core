import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { ProviderName } from '../account-api';
import { has, spacedText } from '../../testing/helpers';
import {
  ProviderButtons,
  ReauthTarget,
  armReauth,
  rememberPendingConnect,
  rememberPendingDisconnect,
  rememberProvider,
  rememberedProvider,
  takePending,
  takePendingConnect,
  takeReauth,
} from './provider-buttons';

@Component({
  imports: [ProviderButtons],
  template: `<app-provider-buttons
    [providers]="providers()"
    [action]="action()"
    [invite]="invite()"
    [reauth]="reauth()"
    [target]="target()"
    [separator]="separator()"
  />`,
})
class Host {
  readonly providers = signal<ProviderName[]>(['google', 'microsoft']);
  readonly action = signal<'continue' | 'signup' | 'accept' | 'confirm'>('continue');
  readonly invite = signal<string | null>(null);
  readonly reauth = signal(false);
  readonly target = signal<ReauthTarget>('delete_account');
  readonly separator = signal(true);
}

function draw(change: (host: Host) => void = () => undefined) {
  const fixture = TestBed.createComponent(Host);
  change(fixture.componentInstance);
  fixture.detectChanges();
  return fixture;
}

function hrefs(fixture: ReturnType<typeof draw>): (string | null)[] {
  return [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')].map((a) => a.getAttribute('href'));
}

describe('ProviderButtons (spec 0011)', () => {
  afterEach(() => {
    vi.useRealTimers();
    sessionStorage.clear();
  });

  it('draws one link per provider, in the given order, to the start, with "or" after them', () => {
    const fixture = draw((host) => host.providers.set(['microsoft', 'google']));
    expect(hrefs(fixture)).toEqual(['/auth/providers/microsoft/start', '/auth/providers/google/start']);
    expect(spacedText(fixture)).toBe('Continue with Microsoft Continue with Google or');
    expect(has(fixture, 'button')).toBe(false);
  });

  it('draws nothing at all without a provider', () => {
    const fixture = draw((host) => host.providers.set([]));
    expect(spacedText(fixture)).toBe('');
    expect(has(fixture, '[data-testid="provider-buttons"]')).toBe(false);
  });

  it.each([
    ['signup', 'Sign up with Google Sign up with Microsoft or'],
    ['accept', 'Accept with Google Accept with Microsoft or'],
  ] as const)('the words of %s', (action, text) => {
    expect(spacedText(draw((host) => host.action.set(action)))).toBe(text);
  });

  it('an invitation goes with the start, and a confirmation asks for reauth, without the "or"', () => {
    expect(hrefs(draw((host) => host.invite.set('tok-i')))).toEqual([
      '/auth/providers/google/start?invite=tok-i',
      '/auth/providers/microsoft/start?invite=tok-i',
    ]);
    const confirm = draw((host) => {
      host.providers.set(['google']);
      host.action.set('confirm');
      host.reauth.set(true);
      host.separator.set(false);
    });
    expect(hrefs(confirm)).toEqual(['/auth/providers/google/start?purpose=reauth']);
    expect(spacedText(confirm)).toBe('Confirm with Google');
  });

  it('remembers the provider the person chose, the name only', () => {
    const fixture = draw();
    const link = (fixture.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>('[data-testid="provider-microsoft"]')!;
    link.addEventListener('click', (event) => event.preventDefault()); // jsdom does not navigate
    link.click();
    expect(rememberedProvider()).toBe('microsoft');
    expect(Object.keys(sessionStorage)).toEqual(['notes-web.provider']);
  });

  it('a confirmation sets the one-shot reauth flag (the time it was set, never a ticket) besides the name; a plain button does not', () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-05-02T12:00:00Z'));
    const plain = draw();
    const plainLink = (plain.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>('[data-testid="provider-google"]')!;
    plainLink.addEventListener('click', (event) => event.preventDefault());
    plainLink.click();
    expect(takeReauth()).toBeNull();
    sessionStorage.clear();

    const confirm = draw((host) => {
      host.providers.set(['google']);
      host.action.set('confirm');
      host.reauth.set(true);
    });
    const link = (confirm.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>('[data-testid="provider-google"]')!;
    link.addEventListener('click', (event) => event.preventDefault());
    link.click();
    expect(Object.fromEntries(Object.entries(sessionStorage))).toEqual({
      'notes-web.provider': 'google',
      'notes-web.reauth': 'delete_account:' + String(Date.parse('2026-05-02T12:00:00Z')),
    });
    expect(localStorage.length).toBe(0);
    expect(takeReauth()).toBe('delete_account');
    expect(takeReauth()).toBeNull();
  });

  describe('the reauth flag lives 10 minutes', () => {
    const MINUTE = 60 * 1000;

    beforeEach(() => {
      vi.useFakeTimers({ toFake: ['Date'] });
      vi.setSystemTime(new Date('2026-05-02T12:00:00Z'));
    });

    it('a fresh flag works, once', () => {
      armReauth('delete_account');
      vi.setSystemTime(Date.now() + 9 * MINUTE + 59_000);
      expect(takeReauth()).toBe('delete_account');
      expect(takeReauth()).toBeNull();
    });

    it('a flag older than 10 minutes is no flag, and reading it clears it', () => {
      armReauth('delete_account');
      vi.setSystemTime(Date.now() + 10 * MINUTE + 1);
      expect(takeReauth()).toBeNull();
      expect(sessionStorage.getItem('notes-web.reauth')).toBeNull();
    });

    it('a time ahead of this clock, a text and an old-style 1 are no flag', () => {
      for (const value of [
        `delete_account:${Date.now() + MINUTE}`,
        String(Date.now()), // the shape of spec 0011: no target
        `other:${Date.now()}`,
        'confirm:',
        'yes',
        '1',
        '',
        '12:00',
        '-5',
      ]) {
        sessionStorage.setItem('notes-web.reauth', value);
        expect(takeReauth(), value).toBeNull();
        expect(sessionStorage.getItem('notes-web.reauth')).toBeNull();
      }
    });
  });

  it('a confirmation for the settings screen arms its target; each target is read back once (spec 0012)', () => {
    for (const target of ['delete_account', 'delete_company', 'confirm', 'connect'] as const) {
      armReauth(target);
      expect(sessionStorage.getItem('notes-web.reauth')).toMatch(new RegExp(`^${target}:\\d+$`));
      expect(takeReauth()).toBe(target);
      expect(takeReauth()).toBeNull();
    }
  });

  it('the target input of the buttons arms that target', () => {
    const fixture = draw((host) => {
      host.providers.set(['github']);
      host.action.set('confirm');
      host.reauth.set(true);
      host.target.set('confirm');
    });
    const link = (fixture.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>('[data-testid="provider-github"]')!;
    link.addEventListener('click', (event) => event.preventDefault());
    link.click();
    expect(takeReauth()).toBe('confirm');
    expect(rememberedProvider()).toBe('github');
  });

  it('keeps the provider of a pending connect, the name and the time only, once, and for ten minutes', () => {
    rememberPendingConnect('github');
    expect(sessionStorage.getItem('notes-web.pending-connect')).toMatch(/^connect:github:\d+$/);
    expect(takePendingConnect()).toBe('github');
    expect(takePendingConnect()).toBeNull();
    for (const value of [
      `connect:gitlab:${Date.now()}`,
      `connect:github`, // no time
      'github:' + Date.now(), // the old shape: no kind
      `fetch:github:${Date.now()}`,
      `connect:github:${Date.now() + 60_000}`,
      `connect:github:${Date.now() - 10 * 60_000 - 1000}`,
      `disconnect:github:${Date.now() + 60_000}`,
    ]) {
      sessionStorage.setItem('notes-web.pending-connect', value);
      expect(takePending(), value).toBeNull();
    }
  });

  it('keeps the provider of a pending disconnect with its kind: read once, and not a connect', () => {
    rememberPendingDisconnect('google');
    expect(sessionStorage.getItem('notes-web.pending-connect')).toMatch(/^disconnect:google:\d+$/);
    expect(takePending()).toEqual({ kind: 'disconnect', provider: 'google' });
    expect(takePending()).toBeNull();
    rememberPendingDisconnect('google');
    expect(takePendingConnect()).toBeNull(); // a disconnect is not named as a connect, and the value is gone
    expect(takePending()).toBeNull();
  });

  it('a confirmation survives storage that throws on write: the link is still followed, and nothing is armed', () => {
    const writing = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied');
    });
    const confirm = draw((host) => {
      host.providers.set(['google']);
      host.reauth.set(true);
    });
    const link = (confirm.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>('[data-testid="provider-google"]')!;
    let prevented: boolean | null = null;
    link.addEventListener('click', (event) => {
      prevented = event.defaultPrevented;
      event.preventDefault();
    });
    expect(() => link.click()).not.toThrow();
    writing.mockRestore();
    expect(prevented).toBe(false);
    expect(takeReauth()).toBeNull();
  });

  it('keeps the name and nothing else: not the invitation token, not a ticket, and nothing in localStorage', () => {
    const fixture = draw((host) => host.invite.set('tok-secret'));
    const link = (fixture.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>('[data-testid="provider-google"]')!;
    link.addEventListener('click', (event) => event.preventDefault());
    link.click();
    const kept = Object.entries(sessionStorage);
    expect(kept).toEqual([['notes-web.provider', 'google']]);
    expect(JSON.stringify([kept, Object.entries(localStorage)])).not.toContain('tok-secret');
    expect(localStorage.length).toBe(0);
  });

  it('a click survives storage that throws on write: the link is still followed', () => {
    const writing = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied');
    });
    const fixture = draw();
    const link = (fixture.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>('[data-testid="provider-google"]')!;
    let prevented: boolean | null = null;
    link.addEventListener('click', (event) => {
      prevented = event.defaultPrevented; // read before the test's own stop of the navigation
      event.preventDefault();
    });
    expect(() => link.click()).not.toThrow();
    writing.mockRestore();
    expect(prevented).toBe(false);
  });

  it('an empty storage gives no provider', () => {
    sessionStorage.clear();
    expect(rememberedProvider()).toBeNull();
  });

  it('an invitation token is encoded into the start address: it cannot add a member or end the address', () => {
    const fixture = draw((host) => {
      host.providers.set(['google']);
      host.invite.set('x&purpose=reauth#frag?y=1');
    });
    expect(hrefs(fixture)).toEqual(['/auth/providers/google/start?invite=x%26purpose%3Dreauth%23frag%3Fy%3D1']);
  });

  it('reads nothing it does not know, and survives storage that throws', () => {
    sessionStorage.setItem('notes-web.provider', 'gitlab');
    expect(rememberedProvider()).toBeNull();
    const failing = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied');
    });
    expect(rememberedProvider()).toBeNull();
    failing.mockRestore();
    const writing = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied');
    });
    expect(() => rememberProvider('google')).not.toThrow();
    writing.mockRestore();
  });
});
