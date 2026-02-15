import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { settle } from '../testing/helpers';
import { App } from './app';
import { AuthService } from './auth/auth.service';

describe('App', () => {
  function open() {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    return { fixture, auth: TestBed.inject(AuthService), root: fixture.nativeElement as HTMLElement };
  }

  it('is a shell with a router outlet and no bar while there is nothing to say', () => {
    const { root } = open();
    expect(root.querySelector('router-outlet')).not.toBeNull();
    expect(root.querySelector('[data-testid="notice"]')).toBeNull();
  });

  it.each([
    ['unreachable', "Can't reach the server. Try again shortly."],
    ['try_later', 'Try again shortly.'],
    ['forbidden', "You don't have access to this."],
  ] as const)('shows the bar for the notice %s', async (notice, text) => {
    const { fixture, auth, root } = open();
    auth.showNotice(notice);
    await settle(fixture);
    expect(root.querySelector('[data-testid="notice-text"]')?.textContent).toBe(text);
  });

  it('lets the person dismiss the bar', async () => {
    const { fixture, auth, root } = open();
    auth.showNotice('try_later');
    await settle(fixture);
    root.querySelector<HTMLButtonElement>('[data-testid="notice"] button')?.click();
    await settle(fixture);
    expect(root.querySelector('[data-testid="notice"]')).toBeNull();
    expect(auth.notice()).toBeNull();
  });
});
