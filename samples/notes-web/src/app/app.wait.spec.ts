import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { settle } from '../testing/helpers';
import { App } from './app';
import { AuthService } from './auth/auth.service';

describe('App, the notice of a request limit', () => {
  function open() {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    return { fixture, auth: TestBed.inject(AuthService), root: fixture.nativeElement as HTMLElement };
  }

  const text = (root: HTMLElement) => root.querySelector('[data-testid="notice-text"]')?.textContent;

  it('shows "Try again in N s." with the number the server gave', async () => {
    const { fixture, auth, root } = open();
    auth.showNotice('wait', 12);
    await settle(fixture);
    expect(text(root)).toBe('Try again in 12 s.');
  });

  it('follows a newer notice and can be dismissed', async () => {
    const { fixture, auth, root } = open();
    auth.showNotice('wait', 12);
    await settle(fixture);
    auth.showNotice('wait', 3);
    await settle(fixture);
    expect(text(root)).toBe('Try again in 3 s.');
    auth.showNotice('tryLater');
    await settle(fixture);
    expect(text(root)).toBe('Try again shortly.');
    root.querySelector<HTMLButtonElement>('[data-testid="notice"] button')?.click();
    await settle(fixture);
    expect(root.querySelector('[data-testid="notice"]')).toBeNull();
  });
});
