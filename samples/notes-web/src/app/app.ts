import { Component, computed, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { AuthService } from './auth/auth.service';
import { texts } from './texts';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  template: `
    @if (notice(); as text) {
      <div class="bar" role="alert" data-testid="notice">
        <span data-testid="notice-text">{{ text }}</span>
        <button type="button" (click)="dismiss()">{{ dismissLabel }}</button>
      </div>
    }
    <router-outlet />
  `,
  styles: [
    `
      .bar {
        display: flex;
        gap: 1rem;
        align-items: center;
        justify-content: space-between;
        padding: 0.5rem 1rem;
        color: #3b2f00;
        background: #fef3c7;
      }
      .bar button {
        min-height: 32px;
        color: inherit;
        text-decoration: underline;
        background: none;
      }
    `,
  ],
})
export class App {
  private readonly auth = inject(AuthService);
  protected readonly dismissLabel = texts.notices.dismiss;
  protected readonly notice = computed(() => {
    const kind = this.auth.notice();
    if (kind === null) {
      return null;
    }
    return kind === 'wait' ? texts.notices.tryAgainIn(this.auth.noticeSeconds()) : texts.notices[kind];
  });

  protected dismiss(): void {
    this.auth.clearNotice();
  }
}
