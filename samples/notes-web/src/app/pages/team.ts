import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, ElementRef, Injector, OnInit, afterNextRender, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService, errorCode, isRecord } from '../auth/auth.service';
import { texts } from '../texts';

interface Member {
  user_id: string;
  email: string;
  role: { id: string; name: string };
}

function isMember(value: unknown): value is Member {
  return (
    isRecord(value) &&
    typeof value['user_id'] === 'string' &&
    typeof value['email'] === 'string' &&
    isRecord(value['role']) &&
    typeof value['role']['name'] === 'string'
  );
}

/**
 * The company's members, for a member who holds members:manage (spec 0010, Settled 14): the address and the role of each, and "Remove" for
 * every other member, behind a confirmation that says what removing does: the person's account is deleted.
 */
@Component({
  selector: 'app-team',
  imports: [RouterLink],
  template: `
    <main class="wide">
      <h1 tabindex="-1" data-testid="team-title">{{ t.title }}</h1>
      @if (!ready()) {
        <p class="status" role="status" data-testid="team-loading">{{ t.loading }}</p>
      } @else if (me() === null) {
        <!-- The person could not be read (the network, a proxy): that is not the same as having no access. -->
        <p class="error" role="alert" data-testid="team-error">{{ somethingWrong }}</p>
        <button type="button" data-testid="try-again" (click)="start()">{{ common.tryAgain }}</button>
      } @else if (!canManage()) {
        <p class="error" role="alert" data-testid="team-forbidden">{{ t.notAllowed }}</p>
      } @else {
        @if (loadFailed()) {
          <p class="error" role="alert">{{ somethingWrong }}</p>
        }
        <ul class="plain" data-testid="members">
          @for (member of members(); track member.user_id) {
            <li data-testid="member">
              <strong data-testid="member-email">{{ member.email }}</strong>
              <span data-testid="member-role">{{ member.role.name }}</span>
              @if (member.user_id !== me()?.sub) {
                @if (confirming() === member.user_id) {
                  <span class="error" role="alert" data-testid="remove-confirmation">{{ t.removeConfirm }}</span>
                  <button
                    type="button"
                    data-testid="remove-confirm"
                    [attr.aria-label]="t.remove + ' ' + member.email"
                    [disabled]="busy()"
                    (click)="remove(member)"
                  >
                    {{ t.remove }}
                  </button>
                  <button type="button" data-testid="remove-cancel" [disabled]="busy()" (click)="cancel(member)">{{ t.cancel }}</button>
                } @else {
                  <button
                    type="button"
                    data-testid="remove"
                    [attr.data-user]="member.user_id"
                    [attr.aria-label]="t.remove + ' ' + member.email"
                    (click)="ask(member)"
                  >
                    {{ t.remove }}
                  </button>
                }
              }
            </li>
          }
        </ul>
        @if (error(); as text) {
          <p class="error" role="alert" data-testid="team-error">{{ text }}</p>
        }
      }
      <p><a routerLink="/notes">{{ t.back }}</a></p>
    </main>
  `,
})
export class TeamPage implements OnInit {
  protected readonly t = texts.team;
  protected readonly common = texts.common;
  protected readonly somethingWrong = texts.common.somethingWrong;
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly me = this.auth.me;
  protected readonly canManage = computed(() => this.me()?.permissions.includes('members:manage') ?? false);
  protected readonly members = signal<Member[]>([]);
  // False while the person and the list are being read: until then the page cannot tell "no access" from "not known yet".
  protected readonly ready = signal(false);
  protected readonly loadFailed = signal(false);
  protected readonly confirming = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): Promise<void> {
    return this.start();
  }

  protected async start(): Promise<void> {
    this.ready.set(false);
    if (this.me() === null) {
      await this.auth.loadMe();
    }
    if (this.canManage()) {
      await this.load();
    }
    this.ready.set(true);
  }

  protected ask(member: Member): void {
    this.error.set(null);
    this.confirming.set(member.user_id);
    // The safe choice: Enter pressed again cancels, it does not delete an account.
    this.focusAfterRender((host) => host.querySelector<HTMLElement>('[data-testid="remove-cancel"]'));
  }

  protected cancel(member: Member): void {
    this.confirming.set(null);
    this.focusAfterRender((host) => removeButtonOf(host, member.user_id));
  }

  protected async remove(member: Member): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      await firstValueFrom(this.http.delete(`/auth/org/members/${encodeURIComponent(member.user_id)}`));
      this.members.update((list) => list.filter((m) => m.user_id !== member.user_id));
    } catch (error) {
      const code = error instanceof HttpErrorResponse ? errorCode(error) : undefined;
      if (code === 'permission_not_held') {
        this.error.set(this.t.permissionNotHeld);
      } else if (code === 'last_manager') {
        this.error.set(this.t.lastManager);
      } else if (code === 'cannot_change_self') {
        this.error.set(this.t.cannotChangeSelf);
      } else if (code === 'not_found') {
        // Someone else removed this member meanwhile: the list shows how it is now.
        await this.load();
      } else if (code === 'permissions_changed' || code === 'forbidden') {
        // The interceptor has refreshed and tried again; what is left: the person's rights changed. Read them, and the list, again.
        await this.auth.loadMe();
        if (this.canManage()) {
          this.error.set(this.t.notAllowed);
          await this.load();
        }
      } else {
        this.error.set(this.somethingWrong);
      }
    } finally {
      this.confirming.set(null);
      this.busy.set(false);
      // The button that had the focus is gone: it goes to the member's button if the member is still listed, else to the title.
      this.focusAfterRender((host) => removeButtonOf(host, member.user_id) ?? host.querySelector<HTMLElement>('[data-testid="team-title"]'));
    }
  }

  private focusAfterRender(find: (host: HTMLElement) => HTMLElement | null): void {
    afterNextRender(() => find(this.host.nativeElement)?.focus(), { injector: this.injector });
  }

  private async load(): Promise<void> {
    try {
      const body = await firstValueFrom(this.http.get<unknown>('/auth/org/members'));
      const list = isRecord(body) && Array.isArray(body['members']) ? body['members'].filter(isMember) : null;
      if (list === null) {
        throw new Error('not a member list');
      }
      this.members.set(list);
      this.loadFailed.set(false);
    } catch {
      this.members.set([]);
      this.loadFailed.set(true);
    }
  }
}

function removeButtonOf(host: HTMLElement, userId: string): HTMLElement | null {
  return Array.from(host.querySelectorAll<HTMLElement>('[data-testid="remove"]')).find((button) => button.dataset['user'] === userId) ?? null;
}
