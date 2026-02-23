import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { has, settle, submitForm, typeInto } from '../../testing/helpers';
import { authInterceptor } from '../auth/auth.interceptor';
import { AuthService } from '../auth/auth.service';
import { LoginPage } from './login';

describe('LoginPage when the address has made too many requests', () => {
  it('leaves the words to the notice bar, says nothing of its own and can be tried again', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'login', component: LoginPage }]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/login', LoginPage);
    vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const ctrl = TestBed.inject(HttpTestingController);
    const auth = TestBed.inject(AuthService);

    typeInto(harness.fixture, '#email', 'admin@example.test');
    typeInto(harness.fixture, '#password', 'pw');
    submitForm(harness.fixture);
    ctrl
      .expectOne('/auth/login')
      .flush({ error: 'too_many_requests', retry_after_seconds: 17 }, { status: 429, statusText: '429' });
    await settle(harness.fixture);

    expect(auth.notice()).toBe('wait');
    expect(auth.noticeSeconds()).toBe(17);
    expect(has(harness.fixture, '[data-testid="login-message"]')).toBe(false);
    expect(harness.fixture.nativeElement.querySelector('button[type="submit"]').disabled).toBe(false);
    expect(auth.token()).toBeNull();
    ctrl.verify();
  });
});
