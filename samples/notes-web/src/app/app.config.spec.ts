import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { ApplicationInitStatus } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { adminMe, settle } from '../testing/helpers';
import { appConfig } from './app.config';
import { AuthService } from './auth/auth.service';

describe('appConfig', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [...appConfig.providers, provideHttpClientTesting()] });
  });

  it('resumes a session before the app is ready: one refresh, then /auth/me', async () => {
    const status = TestBed.inject(ApplicationInitStatus);
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.expectOne('/auth/refresh').flush({ access_token: 'tok' });
    await settle();
    ctrl.expectOne('/auth/me').flush(adminMe);
    await status.donePromise;
    expect(TestBed.inject(AuthService).token()).toBe('tok');
    ctrl.verify();
  });

  it('starts anonymous when the cookie is refused', async () => {
    const status = TestBed.inject(ApplicationInitStatus);
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.expectOne('/auth/refresh').flush({ error: 'invalid_grant' }, { status: 401, statusText: 'Unauthorized' });
    await status.donePromise;
    expect(TestBed.inject(AuthService).token()).toBeNull();
    ctrl.verify();
  });
});
