import { Component } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { has, pageText } from '../../testing/helpers';
import { LoginPage } from './login';

@Component({ template: '' })
class Blank {}

describe('LoginPage: after a deleted account (spec 0010)', () => {
  async function arrive(state?: object) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'start', component: Blank },
          { path: 'login', component: LoginPage },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    const harness = await RouterTestingHarness.create('/start');
    await TestBed.inject(Router).navigateByUrl('/login', state === undefined ? {} : { state });
    harness.detectChanges();
    return harness.fixture;
  }

  it('says "Your account was deleted." when the notes screen sent the person here after the deletion', async () => {
    const fixture = await arrive({ notice: 'deleted' });
    expect(pageText(fixture)).toContain('Your account was deleted.');
    expect(has(fixture, '[data-testid="login-notice"]')).toBe(true);
  });

  it('says nothing of the kind otherwise', async () => {
    const fixture = await arrive();
    expect(has(fixture, '[data-testid="login-notice"]')).toBe(false);
  });
});
