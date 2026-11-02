import { CurrencyPipe, DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { ParticipantCardComponent } from '@app/components/group-orders/participant-card/participant-card.component';
import { GroupReservationStatus, GroupStatusResponse } from '@app/models/group-orders/group-status.model';
import { Participant } from '@app/models/group-orders/participant.model';
import { GroupSessionsResponse } from '@app/models/group-orders/group-sessions-response.model';
import { GuestInvite } from '@app/models/reservations/guest-invite.model';
import { GroupOrdersService } from '@app/services/group-orders/group-orders.service';
import { ToastService } from '@app/services/toast/toast.service';
import { ModalComponent } from '@app/shared/custom-components/modal/modal.component';
import { consolidatePreOrder } from '@app/shared/helpers/consolidate-pre-order';
import { forkJoin, of, switchMap } from 'rxjs';

@Component({
  selector: 'app-group-order-page',
  standalone: true,
  imports: [CurrencyPipe, DatePipe, ParticipantCardComponent, ModalComponent],
  templateUrl: './group-order-page.component.html',
  styleUrl: './group-order-page.component.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class GroupOrderPageComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly groupOrdersService = inject(GroupOrdersService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  readonly invite = signal<GuestInvite | null>(null);
  readonly sessions = signal<GroupSessionsResponse | null>(null);
  readonly ownerSessionId = signal<string | null>(null);
  readonly groupStatus = signal<GroupStatusResponse | null>(null);
  readonly isLoading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly isConfirmationOpen = signal(false);
  readonly isSubmitting = signal(false);

  readonly participants = computed<Participant[]>(() => this.sessions()?.participants ?? []);
  readonly consolidatedOrder = computed(() => consolidatePreOrder(this.participants()));
  readonly unsubmittedCount = computed(() => this.participants().filter((person) => !person.isSubmitted).length);
  readonly hasDishes = computed(() => this.participants().some((person) => person.items.length > 0));
  readonly hasSubmittedDishes = computed(() => this.consolidatedOrder().items.length > 0);
  readonly statusLabel = computed(() => {
    switch (this.groupStatus()?.status) {
      case 'Open':
        return 'Collecting pre-orders';
      case 'Submitted':
        return 'Sent to Restaurant';
      case 'Closed':
        return 'Closed';
      default:
        return '';
    }
  });
  readonly isSubmitDisabled = computed(
    () => !this.hasSubmittedDishes() || this.isSubmitting() || this.groupStatus()?.status !== 'Open'
  );

  ngOnInit(): void {
    this.loadGroupOrder();
  }

  loadGroupOrder(): void {
    const reservationId = this.route.snapshot.paramMap.get('reservationId');
    if (!reservationId) {
      this.isLoading.set(false);
      this.loadError.set('Group reservation not found');
      return;
    }

    this.isLoading.set(true);
    this.loadError.set(null);

    this.groupOrdersService
      .getGroupInvite$(reservationId)
      .pipe(
        switchMap((invite) =>
          forkJoin({
            invite: of(invite),
            sessions: this.groupOrdersService.getSessions$(invite.GroupReservationId),
            ownerSessionId: this.groupOrdersService.getOwnerSessionId$(reservationId),
            groupStatus: this.groupOrdersService.getStatus$(invite.GroupReservationId),
          })
        ),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe({
        next: ({ invite, sessions, ownerSessionId, groupStatus }) => {
          this.invite.set(invite);
          this.sessions.set(sessions);
          this.ownerSessionId.set(ownerSessionId);
          this.groupStatus.set(groupStatus);
          this.isLoading.set(false);
        },
        error: (error: unknown) => {
          this.loadError.set(
            error instanceof HttpErrorResponse && error.status === 404
              ? 'Group reservation not found'
              : 'Could not load the group reservation. Please try again.'
          );
          this.isLoading.set(false);
        },
      });
  }

  warningText(): string {
    const count = this.unsubmittedCount();
    if (count === 1) {
      return "1 participant hasn't submitted their cart yet — their dishes won't be included in the order";
    }

    return `${count} participants haven't submitted their cart yet — their dishes won't be included in the order`;
  }

  openConfirmation(): void {
    if (!this.isSubmitDisabled()) {
      this.isConfirmationOpen.set(true);
    }
  }

  closeConfirmation(): void {
    if (!this.isSubmitting()) {
      this.isConfirmationOpen.set(false);
    }
  }

  submitPreOrder(): void {
    const reservationId = this.route.snapshot.paramMap.get('reservationId');
    const groupReservationId = this.invite()?.GroupReservationId;
    if (!reservationId || !groupReservationId || this.isSubmitDisabled()) {
      return;
    }

    this.isSubmitting.set(true);
    this.groupOrdersService
      .submit$(groupReservationId, reservationId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.groupStatus.update((status) =>
            status ? { ...status, status: 'Submitted' as GroupReservationStatus } : status
          );
          this.isSubmitting.set(false);
          this.isConfirmationOpen.set(false);
          this.toast.success('Pre-order sent to the restaurant');
        },
        error: () => {
          this.isSubmitting.set(false);
        },
      });
  }
}