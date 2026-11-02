using Amazon.DynamoDBv2.Model;
using APIHandler.Constants;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GroupReservation;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Orders;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.Auth;
using APIHandler.Services.GroupReservations;
using APIHandler.Services.Locations;
using AutoFixture.Xunit2;
using Moq;

namespace APIHandler.Tests.Services;

public class GroupReservationServiceTests
{
    private readonly Mock<IGroupReservationRepository> _groupReservationRepositoryMock = new();
    private readonly Mock<ITableRepository> _tableRepositoryMock = new();
    private readonly Mock<IReservationRepository> _reservationRepositoryMock = new();
    private readonly Mock<IGuestSessionRepository> _guestSessionRepositoryMock = new();
    private readonly Mock<IOrderRepository> _orderRepositoryMock = new();
    private readonly Mock<IUserService> _userServiceMock = new();
    private readonly Mock<ILocationService> _locationServiceMock = new();

    private readonly GroupReservationService _service;

    public GroupReservationServiceTests()
    {
        _service = new GroupReservationService(
            _groupReservationRepositoryMock.Object,
            _tableRepositoryMock.Object,
            _reservationRepositoryMock.Object,
            _guestSessionRepositoryMock.Object,
            _orderRepositoryMock.Object,
            _userServiceMock.Object,
            _locationServiceMock.Object);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationByTokenAsync_ValidInput_ReturnsGroupReservationInfo(
        GroupReservation groupReservation,
        Table table,
        Reservation reservation,
        Location location,
        string expectedUserName)
    {
        groupReservation.ReservationId = reservation.Id;
        groupReservation.Status = GroupReservationStatus.Open;
        reservation.TableId = table.Id;
        table.LocationId = location.Id;

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(groupReservation.ReservationToken))
            .ReturnsAsync(groupReservation);

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _guestSessionRepositoryMock
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(0);

        _tableRepositoryMock
            .Setup(r => r.GetTableAsync(table.Id))
            .ReturnsAsync(table);

        _locationServiceMock
            .Setup(s => s.GetLocationByIdAsync(table.LocationId))
            .ReturnsAsync(location);

        _userServiceMock
            .Setup(s => s.GetUserName(reservation.CustomerId!))
            .ReturnsAsync(expectedUserName);

        var result = await _service.GetGroupReservationByTokenAsync(groupReservation.ReservationToken);

        Assert.Equal(groupReservation.GroupReservationId, result.GroupReservationId);
        Assert.Equal(table.LocationId, result.LocationId);
        Assert.Equal(reservation.GuestCount, result.MaxGuestCount);
        Assert.Equal(reservation.StartTime, result.StartTime);
        Assert.Equal(reservation.EndTime, result.EndTime);
        Assert.Equal(table.TableNumber, result.TableNumber);
        Assert.Equal("Green & Tasty", result.RestaurantName);
        Assert.Equal(location.Address, result.LocationAddress);
        Assert.Equal(expectedUserName, result.InviterName);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationByTokenAsync_InvalidToken_ThrowsNotFound(string token)
    {
        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(token))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() => _service.GetGroupReservationByTokenAsync(token));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Closed)]
    [InlineAutoData(GroupReservationStatus.Submitted)]
    public async Task GetGroupReservationByTokenAsync_GroupReservationIsNotOpen_ThrowsException(
        GroupReservationStatus status,
        GroupReservation groupReservation)
    {
        groupReservation.Status = status;

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(groupReservation.ReservationToken))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _service.GetGroupReservationByTokenAsync(groupReservation.ReservationToken));
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationByTokenAsync_WhenGuestCountIsMax_ThrowsGuestLimitExceeded(GroupReservation groupReservation, Reservation reservation)
    {
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = reservation.Id;

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(groupReservation.ReservationToken))
            .ReturnsAsync(groupReservation);

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _guestSessionRepositoryMock
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(reservation.GuestCount);

        await Assert.ThrowsAsync<GuestLimitExceededException>(() => _service.GetGroupReservationByTokenAsync(groupReservation.ReservationToken));
    }

    [Theory]
    [AutoData]
    public async Task CloseGroupReservationAsync_ValidInput_UpdatesGroupReservationStatus(GroupReservation groupReservation)
    {
        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(groupReservation.ReservationId))
            .ReturnsAsync(groupReservation);

        GroupReservation? savedGroupReservation = null;
        _groupReservationRepositoryMock
            .Setup(r => r.SaveGroupReservationAsync(It.IsAny<GroupReservation>()))
            .Callback<GroupReservation>(r => savedGroupReservation = r);

        await _service.CloseGroupReservationAsync(groupReservation.ReservationId);

        Assert.NotNull(savedGroupReservation);
        Assert.Equal(GroupReservationStatus.Closed, savedGroupReservation.Status);
    }

    [Theory]
    [AutoData]
    public async Task CloseGroupReservationAsync_GroupReservationNotFound_DoesNotThrowException(string reservationId)
    {
        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(reservationId))
            .ReturnsAsync((GroupReservation?)null);

        await _service.CloseGroupReservationAsync(reservationId);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationInviteTokenAsync_ValidInput_ReturnsToken(Reservation reservation, GroupReservation groupReservation)
    {
        reservation.Status = ReservationStatus.Reserved;
        reservation.GuestCount = 3;
        groupReservation.ReservationId = reservation.Id;
        groupReservation.Status = GroupReservationStatus.Open;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(reservation.Id))
            .ReturnsAsync(groupReservation);

        GetGroupInviteTokenResponse result = await _service.GetGroupReservationInviteTokenAsync(reservation.Id);

        Assert.Equal(groupReservation.ReservationToken, result.Token);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationInviteTokenAsync_InvalidReservationId_ThrowsNotFound(string reservationId)
    {
        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservationId))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(() => _service.GetGroupReservationInviteTokenAsync(reservationId));
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationInviteTokenAsync_OneGuest_ThrowsValidationException(Reservation reservation)
    {
        reservation.Status = ReservationStatus.Reserved;
        reservation.GuestCount = 1;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ValidationException>(() => _service.GetGroupReservationInviteTokenAsync(reservation.Id));
    }

    [Theory]
    [InlineAutoData(ReservationStatus.Completed)]
    [InlineAutoData(ReservationStatus.Cancelled)]
    [InlineAutoData(ReservationStatus.InProgress)]
    public async Task GetGroupReservationInviteTokenAsync_StatusIsNotReserved_ThrowsValidationException(ReservationStatus reservationStatus, Reservation reservation)
    {
        reservation.Status = reservationStatus;
        reservation.GuestCount = 1;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ValidationException>(() => _service.GetGroupReservationInviteTokenAsync(reservation.Id));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    public async Task GetGroupReservationInviteTokenAsync_GroupStatusIsNotOpen_ThrowsException(GroupReservationStatus status, Type type, Reservation reservation, GroupReservation groupReservation)
    {
        reservation.Status = ReservationStatus.Reserved;
        reservation.GuestCount = 3;
        groupReservation.ReservationId = reservation.Id;
        groupReservation.Status = status;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(reservation.Id))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync(type, () => _service.GetGroupReservationInviteTokenAsync(reservation.Id));
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_ValidRequest_ReturnsMergedQuantitiesFromSubmittedSessionsOnly()
    {
        SetupOwnedGroup();
        SetupSessions(
            Session("s1", true, ("dish1", 1), ("dish2", 2)),
            Session("s2", true, ("dish1", 2)),
            Session("s3", false, ("dish1", 10), ("dish3", 5)));
        Order order = SetupOrder();

        var result = await _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId);

        Assert.Equal(2, result.Count);
        Assert.Equal(3, result.Single(i => i.DishId == "dish1").Quantity);
        Assert.Equal(2, result.Single(i => i.DishId == "dish2").Quantity);
        Assert.DoesNotContain(result, i => i.DishId == "dish3");
        Assert.Equal(2, order.Dishes.Count);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_ValidRequest_SavesOrderAtomicallyOnceAsSubmittedByCustomer()
    {
        SetupOwnedGroup();
        SetupSessions(Session("s1", true, ("dish1", 1)));
        Order order = SetupOrder();

        await _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId);

        _orderRepositoryMock.Verify(
            r => r.SaveOrderAndSubmitGroupAsync(
                It.Is<Order>(o => o == order && o.Status == OrderStatus.SubmittedByCustomer),
                GroupId),
            Times.Once);
        _orderRepositoryMock.Verify(r => r.SaveOrderAsync(It.IsAny<Order>()), Times.Never);
        _groupReservationRepositoryMock.Verify(r => r.SaveGroupReservationAsync(It.IsAny<GroupReservation>()), Times.Never);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_OrderHasNoDishes_AddsNewItemsAsPending()
    {
        SetupOwnedGroup();
        SetupSessions(Session("s1", true, ("dish1", 2)));
        Order order = SetupOrder();

        await _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId);

        OrderItem item = Assert.Single(order.Dishes);
        Assert.Equal("dish1", item.DishId);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(OrderItemState.Pending, item.State);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_OrderAlreadyHasDishes_MergesQuantitiesKeepsExistingStatesAndAddsNewPending()
    {
        SetupOwnedGroup();
        SetupSessions(
            Session("s1", true, ("dish1", 1), ("dish2", 2)),
            Session("s2", true, ("dish1", 2), ("dish3", 4)));
        Order order = SetupOrder(
            new OrderItem { DishId = "dish1", Quantity = 5, State = OrderItemState.Confirmed },
            new OrderItem { DishId = "dish2", Quantity = 1, State = OrderItemState.Canceled },
            new OrderItem { DishId = "dish9", Quantity = 7, State = OrderItemState.Confirmed });

        var result = await _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId);

        Assert.Equal(4, order.Dishes.Count);

        OrderItem dish1 = order.Dishes.Single(d => d.DishId == "dish1");
        Assert.Equal(8, dish1.Quantity);
        Assert.Equal(OrderItemState.Confirmed, dish1.State);

        OrderItem dish2 = order.Dishes.Single(d => d.DishId == "dish2");
        Assert.Equal(3, dish2.Quantity);
        Assert.Equal(OrderItemState.Canceled, dish2.State);

        OrderItem dish3 = order.Dishes.Single(d => d.DishId == "dish3");
        Assert.Equal(4, dish3.Quantity);
        Assert.Equal(OrderItemState.Pending, dish3.State);

        OrderItem dish9 = order.Dishes.Single(d => d.DishId == "dish9");
        Assert.Equal(7, dish9.Quantity);
        Assert.Equal(OrderItemState.Confirmed, dish9.State);

        // Response contains only the consolidated guest dishes, not the existing order contents.
        Assert.Equal(3, result.Count);
        Assert.Equal(3, result.Single(i => i.DishId == "dish1").Quantity);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_OwnerSessionIsSubmitted_IsIncludedInConsolidation()
    {
        SetupOwnedGroup();
        GuestSession owner = Session("owner-session", true, ("dish1", 2));
        owner.IsOwnerSession = true;
        SetupSessions(owner, Session("s1", true, ("dish1", 1)));
        SetupOrder();

        var result = await _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId);

        Assert.Equal(3, Assert.Single(result).Quantity);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_NoSubmittedSessions_ThrowsValidationExceptionAndSavesNothing()
    {
        SetupOwnedGroup();
        SetupSessions(Session("s1", false, ("dish1", 1)));
        SetupOrder();

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        VerifyNothingSaved();
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_NoSessionsAtAll_ThrowsValidationExceptionAndSavesNothing()
    {
        SetupOwnedGroup();
        SetupSessions();
        SetupOrder();

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        VerifyNothingSaved();
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_SubmittedSessionsWithoutDishes_ThrowsValidationExceptionAndSavesNothing()
    {
        SetupOwnedGroup();
        SetupSessions(Session("s1", true), Session("s2", true));
        SetupOrder();

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        VerifyNothingSaved();
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_OrderDoesNotExist_ThrowsValidationExceptionAndSavesNothing()
    {
        SetupOwnedGroup();
        SetupSessions(Session("s1", true, ("dish1", 1)));
        _orderRepositoryMock
            .Setup(r => r.GetOrderByReservationIdAsync(ReservationId))
            .ReturnsAsync((Order?)null);

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        VerifyNothingSaved();
    }

    [Theory]
    [InlineData("other-reservation")]
    [InlineData("")]
    [InlineData(null)]
    public async Task SubmitGroupPreOrderAsync_BodyReservationIdMismatch_ThrowsValidationExceptionAndSavesNothing(string? bodyReservationId)
    {
        SetupOwnedGroup();
        SetupSessions(Session("s1", true, ("dish1", 1)));
        SetupOrder();

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, bodyReservationId!));

        VerifyNothingSaved();
        _guestSessionRepositoryMock.Verify(r => r.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_GroupAlreadySubmitted_ThrowsSubmittedExceptionAndSavesNothing()
    {
        SetupOwnedGroup(GroupReservationStatus.Submitted);
        SetupSessions(Session("s1", true, ("dish1", 1)));
        SetupOrder();

        await Assert.ThrowsAsync<GroupReservationSubmittedException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        VerifyNothingSaved();
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_GroupClosed_ThrowsExpiredExceptionAndSavesNothing()
    {
        SetupOwnedGroup(GroupReservationStatus.Closed);
        SetupSessions(Session("s1", true, ("dish1", 1)));
        SetupOrder();

        await Assert.ThrowsAsync<GroupReservationExpiredException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        VerifyNothingSaved();
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_SecondCallAfterSuccess_IsRejectedAndQuantitiesAreNotDuplicated()
    {
        GroupReservation group = SetupOwnedGroup();
        SetupSessions(Session("s1", true, ("dish1", 2)));
        Order order = SetupOrder();

        // Simulates the atomic repository method flipping the group to Submitted.
        _orderRepositoryMock
            .Setup(r => r.SaveOrderAndSubmitGroupAsync(It.IsAny<Order>(), GroupId))
            .Callback(() => group.Status = GroupReservationStatus.Submitted)
            .Returns(Task.CompletedTask);

        await _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId);

        await Assert.ThrowsAsync<GroupReservationSubmittedException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        Assert.Equal(2, Assert.Single(order.Dishes).Quantity);
        _orderRepositoryMock.Verify(r => r.SaveOrderAndSubmitGroupAsync(It.IsAny<Order>(), It.IsAny<string>()), Times.Once);
    }

    [Theory]
    [InlineData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task SubmitGroupPreOrderAsync_GroupConditionFails_ReloadsAndMapsCurrentStatus(
        GroupReservationStatus currentStatus,
        Type expectedException)
    {
        GroupReservation initialGroup = SetupOwnedGroup();
        var currentGroup = new GroupReservation
        {
            GroupReservationId = GroupId,
            ReservationId = ReservationId,
            ReservationToken = "token",
            Status = currentStatus
        };
        _groupReservationRepositoryMock
            .SetupSequence(r => r.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync(initialGroup)
            .ReturnsAsync(currentGroup);
        SetupSubmissionTransactionFailure(TransactionCanceledWith(
            new CancellationReason { Code = "None" },
            new CancellationReason { Code = "ConditionalCheckFailed" }));

        await Assert.ThrowsAsync(expectedException,
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        VerifyTransactionFailureSideEffects(reloadCount: 2);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_GroupConditionFailsAndGroupIsMissing_ThrowsNotFoundAfterReload()
    {
        GroupReservation initialGroup = SetupOwnedGroup();
        _groupReservationRepositoryMock
            .SetupSequence(r => r.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync(initialGroup)
            .ReturnsAsync((GroupReservation?)null);
        SetupSubmissionTransactionFailure(TransactionCanceledWith(
            new CancellationReason { Code = "None" },
            new CancellationReason { Code = "ConditionalCheckFailed" }));

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        VerifyTransactionFailureSideEffects(reloadCount: 2);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_GroupConditionFailsButGroupRemainsOpen_ThrowsValidationExceptionAfterReload()
    {
        GroupReservation initialGroup = SetupOwnedGroup();
        var currentGroup = new GroupReservation
        {
            GroupReservationId = GroupId,
            ReservationId = ReservationId,
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        _groupReservationRepositoryMock
            .SetupSequence(r => r.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync(initialGroup)
            .ReturnsAsync(currentGroup);
        SetupSubmissionTransactionFailure(TransactionCanceledWith(
            new CancellationReason { Code = "None" },
            new CancellationReason { Code = "ConditionalCheckFailed" }));

        ValidationException exception = await Assert.ThrowsAsync<ValidationException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        Assert.Contains("Please retry", exception.Message);
        VerifyTransactionFailureSideEffects(reloadCount: 2);
    }

    [Theory]
    [InlineData("null-reasons")]
    [InlineData("index-1-absent")]
    [InlineData("other-code-at-index-1")]
    [InlineData("condition-failure-at-index-0-only")]
    public async Task SubmitGroupPreOrderAsync_TransactionCancellationIsNotGroupConditionFailure_RethrowsWithoutReload(string scenario)
    {
        SetupOwnedGroup();
        TransactionCanceledException transactionException = scenario switch
        {
            "null-reasons" => new TransactionCanceledException("transaction canceled") { CancellationReasons = null },
            "index-1-absent" => TransactionCanceledWith(),
            "other-code-at-index-1" => TransactionCanceledWith(
                new CancellationReason { Code = "None" },
                new CancellationReason { Code = "TransactionConflict" }),
            "condition-failure-at-index-0-only" => TransactionCanceledWith(
                new CancellationReason { Code = "ConditionalCheckFailed" }),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
        SetupSubmissionTransactionFailure(transactionException);

        TransactionCanceledException actualException = await Assert.ThrowsAsync<TransactionCanceledException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        Assert.Same(transactionException, actualException);
        VerifyTransactionFailureSideEffects(reloadCount: 1);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_GroupReservationNotFound_ThrowsGroupReservationNotFoundException()
    {
        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        VerifyNothingSaved();
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_ReservationNotFound_ThrowsReservationNotFoundException()
    {
        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync(new GroupReservation { GroupReservationId = GroupId, ReservationId = ReservationId, ReservationToken = "t" });
        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(
            () => _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        VerifyNothingSaved();
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_CallerIsNotOwner_ThrowsActionForbiddenExceptionAndSavesNothing()
    {
        SetupOwnedGroup();
        SetupSessions(Session("s1", true, ("dish1", 1)));
        SetupOrder();

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _service.SubmitGroupPreOrderAsync("another-user", GroupId, ReservationId));

        VerifyNothingSaved();
        _guestSessionRepositoryMock.Verify(r => r.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_NotOwnerAndGroupSubmitted_ThrowsForbiddenBeforeStatusCheck()
    {
        SetupOwnedGroup(GroupReservationStatus.Submitted);

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _service.SubmitGroupPreOrderAsync("another-user", GroupId, ReservationId));
    }

    private const string OwnerId = "owner-1";
    private const string GroupId = "group-1";
    private const string ReservationId = "res-1";

    private GroupReservation SetupOwnedGroup(GroupReservationStatus status = GroupReservationStatus.Open)
    {
        var group = new GroupReservation
        {
            GroupReservationId = GroupId,
            ReservationId = ReservationId,
            ReservationToken = "token",
            Status = status
        };

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync(group);

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(new Reservation { Id = ReservationId, TableId = "table-1", WaiterId = "waiter-1", CustomerId = OwnerId });

        return group;
    }

    private void SetupSessions(params GuestSession[] sessions) =>
        _guestSessionRepositoryMock
            .Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync(sessions.ToList());

    private Order SetupOrder(params OrderItem[] existingDishes)
    {
        var order = new Order
        {
            OrderId = "order-1",
            CustomerId = OwnerId,
            ReservationId = ReservationId,
            ReservationAddress = "Street 1, Table 1",
            ReservationTime = "10:00 - 11:00",
            Dishes = existingDishes.ToList(),
            Status = OrderStatus.Created
        };

        _orderRepositoryMock
            .Setup(r => r.GetOrderByReservationIdAsync(ReservationId))
            .ReturnsAsync(order);

        return order;
    }

    private void SetupSubmissionTransactionFailure(TransactionCanceledException exception)
    {
        SetupSessions(Session("s1", true, ("dish1", 1)));
        SetupOrder();
        _orderRepositoryMock
            .Setup(r => r.SaveOrderAndSubmitGroupAsync(It.IsAny<Order>(), GroupId))
            .ThrowsAsync(exception);
    }

    private void VerifyTransactionFailureSideEffects(int reloadCount)
    {
        _groupReservationRepositoryMock.Verify(
            r => r.GetGroupReservationByIdAsync(GroupId),
            Times.Exactly(reloadCount));
        _orderRepositoryMock.Verify(
            r => r.SaveOrderAndSubmitGroupAsync(It.IsAny<Order>(), GroupId),
            Times.Once);
        _orderRepositoryMock.Verify(r => r.SaveOrderAsync(It.IsAny<Order>()), Times.Never);
        _groupReservationRepositoryMock.Verify(r => r.SaveGroupReservationAsync(It.IsAny<GroupReservation>()), Times.Never);
    }

    private static TransactionCanceledException TransactionCanceledWith(params CancellationReason[] reasons) =>
        new("transaction canceled") { CancellationReasons = reasons.ToList() };

    private static GuestSession Session(string id, bool isSubmitted, params (string DishId, int Quantity)[] dishes) =>
        new()
        {
            Id = id,
            GuestName = id,
            GroupReservationId = GroupId,
            IsSubmitted = isSubmitted,
            Dishes = dishes.Select(d => new OrderItem { DishId = d.DishId, Quantity = d.Quantity, State = OrderItemState.Pending }).ToList()
        };

    private void VerifyNothingSaved()
    {
        _orderRepositoryMock.Verify(r => r.SaveOrderAndSubmitGroupAsync(It.IsAny<Order>(), It.IsAny<string>()), Times.Never);
        _orderRepositoryMock.Verify(r => r.SaveOrderAsync(It.IsAny<Order>()), Times.Never);
        _groupReservationRepositoryMock.Verify(r => r.SaveGroupReservationAsync(It.IsAny<GroupReservation>()), Times.Never);
    }
}
