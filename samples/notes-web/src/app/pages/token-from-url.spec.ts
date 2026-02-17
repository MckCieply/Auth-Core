import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { realLocation, settle } from '../../testing/helpers';
import { takeTokenFromUrl } from './token-from-url';

let built = 0;
let read: (string | null)[] = [];

@Component({ template: '' })
class Probe {
  constructor() {
    built += 1;
    read.push(takeTokenFromUrl(TestBed.inject(ActivatedRoute), TestBed.inject(Router)));
  }
}

async function open(url: string) {
  TestBed.configureTestingModule({ providers: [provideRouter([{ path: 'reset', component: Probe }]), realLocation] });
  history.replaceState(null, '', url);
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(url, Probe);
  await settle();
  await harness.fixture.whenStable();
  return { harness, router: TestBed.inject(Router) };
}

describe('takeTokenFromUrl', () => {
  beforeEach(() => {
    built = 0;
    read = [];
  });
  afterEach(() => history.replaceState(null, '', '/'));

  it('returns the token and takes it out of the address bar and out of the router', async () => {
    const { router } = await open('/reset?token=abc123');
    expect(read).toEqual(['abc123']);
    expect(window.location.search).toBe('');
    expect(window.location.pathname).toBe('/reset');
    expect(window.location.href).not.toContain('abc123');
    expect(router.url).toBe('/reset');
  });

  it('replaces the history entry instead of adding one', async () => {
    const push = vi.spyOn(history, 'pushState');
    await open('/reset?token=abc123');
    expect(push).not.toHaveBeenCalled();
    push.mockRestore();
  });

  it('keeps the screen: it is not built again and reads no token twice', async () => {
    await open('/reset?token=abc123');
    expect(built).toBe(1);
    expect(read).toEqual(['abc123']);
  });

  it('keeps the other parts of the query', async () => {
    const { router } = await open('/reset?token=abc123&x=1');
    expect(router.url).toBe('/reset?x=1');
    expect(window.location.href).not.toContain('abc123');
  });

  it.each([['/reset'], ['/reset?token=']])('is null for %s, and the screen stays', async (url) => {
    const { router } = await open(url);
    expect(read).toEqual([null]);
    expect(built).toBe(1);
    expect(router.url).toBe('/reset');
    expect(window.location.search).toBe('');
  });

  it('a failed removal navigation is no unhandled rejection: the token was read all the same', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), realLocation] });
    const router = TestBed.inject(Router);
    const failing = vi.spyOn(router, 'navigate').mockRejectedValue(new Error('navigation failed'));
    const route = { snapshot: { queryParamMap: convertToParamMap({ token: 'abc123' }) } } as unknown as ActivatedRoute;
    expect(takeTokenFromUrl(route, router)).toBe('abc123');
    await settle();
    expect(failing).toHaveBeenCalledTimes(1);
  });

  it.each([['/reset'], ['/reset?other=1']])('%s has no token parameter: no navigation is queued', async (url) => {
    history.replaceState(null, '', url);
    TestBed.configureTestingModule({ providers: [provideRouter([{ path: 'reset', component: Probe }]), realLocation] });
    const harness = await RouterTestingHarness.create();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate');
    await harness.navigateByUrl(url, Probe);
    await settle();
    expect(navigate).not.toHaveBeenCalled();
    expect(read).toEqual([null]);
  });

  it('uses the first of two token parameters', async () => {
    await open('/reset?token=first&token=second');
    expect(read).toEqual(['first']);
    expect(window.location.href).not.toContain('first');
  });
});
