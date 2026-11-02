using Amazon.CognitoIdentityProvider.Model;
using APIHandler.Constants.Dishes;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GuestSessions;
using APIHandler.Repositories.Auth;
using APIHandler.Repositories.Dishes;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.GuestSessions;
using AutoFixture.Xunit2;
using Moq;
using NPOI.POIFS.Storage;
using System.Security.Cryptography;
using System.Text;

namespace APIHandler.Tests.Services;

public class GuestSessionServiceTests
{
    private readonly Mock<IGuestSessionRepository> _mockGuestSessionRepository = new();
    private readonly Mock<IGroupReservationRepository> _mockGroupReservationRepository = new();
    private readonly Mock<IDishRepository> _mockDishRepository = new();
    private readonly Mock<IReservationRepository> _mockReservationRepository = new();
    private readonly Mock<IUserRepository> _mockUserRepository = new();
    private readonly Mock<ITableRepository> _mockTableRepository = new();
    private readonly GuestSessionService _guestSessionService;

    public GuestSessionServiceTests()
    {
        _guestSessionService = new GuestSessionService(
            _mockGroupReservationRepository.Object,
            _mockGuestSessionRepository.Object,
            _mockDishRepository.Object,
            _mockReservationRepository.Object,
            _mockUserRepository.Object,
            _mockTableRepository.Object);
    }
    [Theory]
    [InlineAutoData(0)]
    [InlineAutoData(3)]
    public async Task CreateGuestSessionAsync_CorrectData_ReturnsSessionToken(
        int guestCount,
        CreateGuestSessionRequest request,
        GroupReservation groupReservation,
        Reservation parentReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;

        parentReservation.GuestCount = 5;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(parentReservation.Id))
            .ReturnsAsync(parentReservation);

        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync([]);

        GuestSession? session = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => session = s);

        _mockGuestSessionRepository
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(guestCount);

        var response = await _guestSessionService.CreateGuestSessionAsync(request);

        Assert.NotEmpty(response.SessionId);
        Assert.NotEmpty(response.SessionToken);
        Assert.NotNull(session);
        Assert.Equal(groupReservation.GroupReservationId, session.GroupReservationId);
        Assert.Equal(request.GuestName, session.GuestName);
        Assert.NotNull(session.SessionTokenHash);
        Assert.NotEmpty(session.SessionTokenHash);
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_InvalidGroupReservationId_ThrowsGroupReservationNotFoundException(CreateGuestSessionRequest request)
    {
        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(request.GroupReservationId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_GroupReservationExpired_ThrowsGroupReservationExpiredException(CreateGuestSessionRequest request, GroupReservation groupReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(request.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldAddNewDish_WhenDishDoesNotExistInSession(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;
        dish.State = DishState.Available;
        int quantityToAdd = 2;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        await _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantityToAdd);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(dish.Id, guestSession.Dishes[0].DishId);
        Assert.Equal(quantityToAdd, guestSession.Dishes[0].Quantity);
        Assert.Equal(OrderItemState.Pending, guestSession.Dishes[0].State);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldIncreaseQuantity_WhenDishAlreadyExists(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        int initialQuantity = 3;
        int quantityToAdd = 2;
        guestSession.Dishes = [new OrderItem { DishId = dish.Id, Quantity = initialQuantity, State = OrderItemState.Pending }];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;
        dish.State = DishState.Available;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        await _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantityToAdd);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(initialQuantity + quantityToAdd, guestSession.Dishes[0].Quantity);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [InlineAutoData(0)]
    [InlineAutoData(-1)]
    public async Task AddDishAsync_ShouldThrowValidationException_WhenQuantityIsZeroOrLess(int quantity, string guestSessionId, Dish dish)
    {
        dish.State = DishState.Available;

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.AddDishAsync(guestSessionId, dish.Id, quantity));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowGuestSessionNotFoundException_WhenSessionNotFound(string guestSessionId, Dish dish)
    {
        dish.State = DishState.Available;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        var exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.AddDishAsync(guestSessionId, dish.Id, 2));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowDishNotFound_WhenDishNotFound(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, string dishId)
    {
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);

        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([]);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.AddDishAsync(guestSession.Id, dishId, quantity: 2));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowDishNotAvailable_WhenDishIsNotAvailable(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        dish.State = DishState.OutOfStock;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        var exception = await Assert.ThrowsAsync<DishIsNotAvailableException>(() =>
            _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantity: 2));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task AddDishAsync_ShouldThrowGroupReservationExpired_WhenGroupStatusIsNotOpen(GroupReservationStatus status, Type exceptionType, GuestSession guestSession, GroupReservation groupReservation, Dish dish)
    {
        dish.State = DishState.Available;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = status;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        var exception = await Assert.ThrowsAsync(exceptionType, () =>
            _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantity: 2));
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldDecreaseQuantity_WhenQuantityIsGreaterThanAmountToRemove(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        int initialQuantity = 5;
        int quantityToRemove = 2;
        guestSession.Dishes =
        [
            new OrderItem { DishId = dishId, Quantity = initialQuantity, State = OrderItemState.Pending }
        ];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);


        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, quantityToRemove);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(initialQuantity - quantityToRemove, guestSession.Dishes[0].Quantity);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldRemoveDishFromList_WhenQuantityToRemoveIsEqualOrGreater(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        int initialQuantity = 2;
        int quantityToRemove = 3;
        guestSession.Dishes =
        [
            new OrderItem { DishId = dishId, Quantity = initialQuantity, State = OrderItemState.Pending }
        ];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, quantityToRemove);

        Assert.Empty(guestSession.Dishes);
        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowDishNotFoundException_WhenDishDoesNotExistInSession(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
           .ReturnsAsync(groupReservation);

        var exception = await Assert.ThrowsAsync<DishNotFoundException>(() =>
            _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, 2));

        Assert.Contains("not found in guest session", exception.Message);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowValidationException_WhenQuantityIsZeroOrLess(string guestSessionId, string dishId)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, 0));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, -5));
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowGuestSessionNotFoundException_WhenSessionNotFound(string guestSessionId, string dishId)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        var exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, 2));
    }
    [Theory]
    [InlineAutoData(1, 0)]
    [InlineAutoData(1, 1)]
    [InlineAutoData(2, 1)]
    [InlineAutoData(2, 2)]
    public async Task CreateGuestSessionAsync_WhenCapacityReached_ThrowsGuestLimitExceededException(
        int reservationCapacity,
        int guestCount,
        CreateGuestSessionRequest request,
        GroupReservation groupReservation,
        Reservation parentReservation)
    {
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.ReservationId = parentReservation.Id;

        parentReservation.GuestCount = reservationCapacity;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(parentReservation.Id))
            .ReturnsAsync(parentReservation);

        _mockGuestSessionRepository
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(guestCount);

        var exception = await Assert.ThrowsAsync<GuestLimitExceededException>(() => _guestSessionService.CreateGuestSessionAsync(request));

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_MissingParentReservation_ThrowsGroupReservationExpiredException(
        CreateGuestSessionRequest request,
        GroupReservation groupReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(groupReservation.ReservationId))
            .ReturnsAsync((Reservation?)null);

        var exception = await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_ValidTokenAndId_ReturnsTrue(GuestSession guestSession, byte[] tokenBytes)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));
        guestSession.SessionTokenHash = hashString;

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync(guestSession);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSession.Id);

        Assert.True(result);
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_InvalidToken_ReturnsFalse(byte[] tokenBytes, string guestSessionId)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync((GuestSession?)null);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSessionId);

        Assert.False(result);
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_InvalidGuestSessionId_ReturnsFalse(GuestSession guestSession, byte[] tokenBytes, string guestSessionId)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));
        guestSession.SessionTokenHash = hashString;

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync(guestSession);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSessionId);

        Assert.False(result);
    }
    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_Valid_SubmitsSuccessfuilly(GuestSession guestSession, GroupReservation groupReservation, Dish[] dishes)
    {
        foreach (var dish in dishes)
            dish.State = DishState.Available;

        guestSession.IsSubmitted = false;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
           .ReturnsAsync(groupReservation);

        _mockDishRepository
            .Setup(d => d.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(dishes);

        GuestSession? session = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => session = s);

        await _guestSessionService.SubmitCartAsync(guestSession.Id);

        Assert.NotNull(session);
        Assert.True(session.IsSubmitted);
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_SessionNotFound_ThrowsGuestSessionNotFound(string guestSessionId)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(() => _guestSessionService.SubmitCartAsync(guestSessionId));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task SubmitCartAsync_GroupReservationNotOpen_ThrowsException(GroupReservationStatus status, Type exceptionType, GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = status;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync(exceptionType, () => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_GuestSessionHasNoDishes_ThrowsCartIsEmptyException(GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<CartIsEmptyException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_GuestSessionSubmitted_ThrowsAlreadySubmited(GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.IsSubmitted = true;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GuestSessionSubmittedException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [InlineAutoData(DishState.OutOfStock)]
    [InlineAutoData(DishState.OnStop)]
    public async Task SubmitCartAsync_DishNotAvailable_ThrowsDishNotAvailable(DishState state, Dish dish, GuestSession guestSession, GroupReservation groupReservation)
    {
        dish.State = state;
        guestSession.IsSubmitted = false;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        guestSession.Dishes = 
        [
            new OrderItem()
            {
                DishId = dish.Id,
                Quantity = 1,
            }
        ];

        _mockDishRepository
            .Setup(d => d.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([dish]);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
           .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<DishIsNotAvailableException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }
    [Theory]
    [MemberAutoData(nameof(PepareDishesForGuestSession))]
    public async Task GetGuestSessionInfoAsync_Guest_ReturnsAsync(
        IEnumerable<string> dishIds,
        IEnumerable<Dish> dishes,
        List<OrderItem> orderItems,
        decimal expectedTotal,
        GuestSession session)
    {
        session.Dishes = orderItems;
        session.IsOwnerSession = false;

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(dishIds))
            .ReturnsAsync(dishes);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(session.Id))
            .ReturnsAsync(session);

        GetGuestSessionResponse result = await _guestSessionService.GetGuestSessionInfoAsync(session.Id);

        Assert.Equal(session.GuestName, result.Name);
        Assert.Equal(dishes.Count(), result.Items.Count());
        Assert.Equal(expectedTotal, result.Total);
    }

    [Theory]
    [MemberAutoData(nameof(PepareDishesForGuestSession))]
    public async Task GetGuestSessionInfoAsync_GroupOwner_ReturnsAsync(
        IEnumerable<string> dishIds,
        IEnumerable<Dish> dishes,
        List<OrderItem> orderItems,
        decimal expectedTotal,
        GuestSession session,
        GroupReservation groupReservation,
        Reservation reservation,
        string customerId,
        string ownerName,
        UserType userType)
    {
        session.Dishes = orderItems;
        session.IsOwnerSession = true;
        session.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.ReservationId = reservation.Id;
        reservation.CustomerId = customerId;
        userType.Attributes.Add(new AttributeType()
        {
            Name = "given_name",
            Value = ownerName,
        });

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(dishIds))
            .ReturnsAsync(dishes);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(session.Id))
            .ReturnsAsync(session);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _mockUserRepository
            .Setup(r => r.GetUserByIdAsync(customerId))
            .ReturnsAsync(userType);

        GetGuestSessionResponse result = await _guestSessionService.GetGuestSessionInfoAsync(session.Id);

        Assert.Equal(ownerName, result.Name);
        Assert.Equal(dishes.Count(), result.Items.Count());
        Assert.Equal(expectedTotal, result.Total);
    }

    public static TheoryData<IEnumerable<string>, IEnumerable<Dish>, List<OrderItem>, decimal> PepareDishesForGuestSession()
    {
        var dishMap = new Dictionary<string, Dish>()
        {
            ["1"] = new()
            {
                Id = "1",
                Price = 1
            },
            ["2"] = new()
            {
                Id = "2",
                Price = 2
            },
            ["3"] = new()
            {
                Id = "3",
                Price = 3
            }
        };

        var orderItems = new List<OrderItem>
        {
            new OrderItem()
                    {
                        DishId = "1",
                        Quantity = 2
                    },
                    new OrderItem()
                    {
                        DishId = "2",
                        Quantity = 3
                    },
                    new OrderItem()
                    {
                        DishId = "3",
                        Quantity = 1
                    }
        };

        var expectedTotal = orderItems.Sum(o => dishMap[o.DishId].Price * o.Quantity);

        return new()
        {
            { dishMap.Keys, dishMap.Values, orderItems, expectedTotal }
        };
    }

    [Fact]
    public async Task GetGroupSessionsAsync_MultipleParticipants_ReturnsTotalsTypesNamesAndSubmittedFlags()
    {
        const string userId = "owner-1";
        const string groupId = "group-1";
        SetupOwnedGroup(groupId, userId);

        var dishA = new Dish { Id = "dish-a", Price = 10m, Name = "A", LocationId = "loc" };
        var dishB = new Dish { Id = "dish-b", Price = 2.5m, Name = "B", LocationId = "loc" };

        var ownerSession = new GuestSession
        {
            Id = "s-owner",
            GuestName = "Owner",
            GroupReservationId = groupId,
            IsOwnerSession = true,
            IsSubmitted = true,
            Dishes = [new OrderItem { DishId = dishA.Id, Quantity = 2 }]
        };
        var guestSession1 = new GuestSession
        {
            Id = "s-guest-1",
            GuestName = "Alice",
            GroupReservationId = groupId,
            IsSubmitted = false,
            Dishes =
            [
                new OrderItem { DishId = dishA.Id, Quantity = 1 },
                new OrderItem { DishId = dishB.Id, Quantity = 4 }
            ]
        };
        var guestSession2 = new GuestSession
        {
            Id = "s-guest-2",
            GuestName = "Bob",
            GroupReservationId = groupId,
            IsSubmitted = true,
            Dishes = [new OrderItem { DishId = dishB.Id, Quantity = 2 }]
        };

        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupId))
            .ReturnsAsync([ownerSession, guestSession1, guestSession2]);

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([dishA, dishB]);

        var result = await _guestSessionService.GetGroupSessionsAsync(userId, groupId);

        var participants = result.Participants.ToList();
        Assert.Equal(3, participants.Count);

        var owner = participants.Single(p => p.Id == "s-owner");
        Assert.Equal(ParticipantType.Owner, owner.Type);
        Assert.Equal("Owner", owner.Name);
        Assert.Equal(20m, owner.Total);
        Assert.True(owner.IsSubmitted);
        Assert.Single(owner.Items);

        var alice = participants.Single(p => p.Id == "s-guest-1");
        Assert.Equal(ParticipantType.Guest, alice.Type);
        Assert.Equal("Alice", alice.Name);
        Assert.Equal(20m, alice.Total);
        Assert.False(alice.IsSubmitted);
        Assert.Equal(2, alice.Items.Count());
        Assert.Equal(4, alice.Items.Single(i => i.Dish.Id == dishB.Id).Quantity);

        var bob = participants.Single(p => p.Id == "s-guest-2");
        Assert.Equal(ParticipantType.Guest, bob.Type);
        Assert.Equal("Bob", bob.Name);
        Assert.Equal(5m, bob.Total);
        Assert.True(bob.IsSubmitted);

        Assert.Equal(45m, result.Total);
        Assert.Equal(participants.Sum(p => p.Total), result.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_NoOwnerSession_ReturnsOnlyGuestParticipants()
    {
        const string userId = "owner-1";
        const string groupId = "group-1";
        SetupOwnedGroup(groupId, userId);

        var dish = new Dish { Id = "dish-a", Price = 3m, Name = "A", LocationId = "loc" };
        var guestSession = new GuestSession
        {
            Id = "s-guest",
            GuestName = "Alice",
            GroupReservationId = groupId,
            Dishes = [new OrderItem { DishId = dish.Id, Quantity = 3 }]
        };

        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupId))
            .ReturnsAsync([guestSession]);

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([dish]);

        var result = await _guestSessionService.GetGroupSessionsAsync(userId, groupId);

        var participant = Assert.Single(result.Participants);
        Assert.Equal(ParticipantType.Guest, participant.Type);
        Assert.Equal(9m, participant.Total);
        Assert.Equal(9m, result.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_EmptyGroup_ReturnsNoParticipantsAndZeroTotal()
    {
        const string userId = "owner-1";
        const string groupId = "group-1";
        SetupOwnedGroup(groupId, userId);

        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupId))
            .ReturnsAsync([]);

        var result = await _guestSessionService.GetGroupSessionsAsync(userId, groupId);

        Assert.Empty(result.Participants);
        Assert.Equal(0m, result.Total);
        _mockDishRepository.Verify(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_ItemReferencesDeletedDish_SkipsItemAndExcludesItFromTotals()
    {
        const string userId = "owner-1";
        const string groupId = "group-1";
        SetupOwnedGroup(groupId, userId);

        var existingDish = new Dish { Id = "dish-a", Price = 10m, Name = "A", LocationId = "loc" };
        var session = new GuestSession
        {
            Id = "s-guest",
            GuestName = "Alice",
            GroupReservationId = groupId,
            Dishes =
            [
                new OrderItem { DishId = "deleted-dish", Quantity = 5 },
                new OrderItem { DishId = existingDish.Id, Quantity = 2 }
            ]
        };

        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupId))
            .ReturnsAsync([session]);

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([existingDish]);

        var result = await _guestSessionService.GetGroupSessionsAsync(userId, groupId);

        var participant = Assert.Single(result.Participants);
        var item = Assert.Single(participant.Items);
        Assert.Equal(existingDish.Id, item.Dish.Id);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(20m, participant.Total);
        Assert.Equal(20m, result.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_OnlyItemReferencesDeletedDish_ReturnsParticipantWithEmptyItemsAndZeroTotal()
    {
        const string userId = "owner-1";
        const string groupId = "group-1";
        SetupOwnedGroup(groupId, userId);

        var existingDish = new Dish { Id = "dish-a", Price = 4m, Name = "A", LocationId = "loc" };
        var sessionWithDeletedDish = new GuestSession
        {
            Id = "s-deleted",
            GuestName = "Alice",
            GroupReservationId = groupId,
            Dishes = [new OrderItem { DishId = "deleted-dish", Quantity = 3 }]
        };
        var otherSession = new GuestSession
        {
            Id = "s-other",
            GuestName = "Bob",
            GroupReservationId = groupId,
            Dishes = [new OrderItem { DishId = existingDish.Id, Quantity = 2 }]
        };

        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupId))
            .ReturnsAsync([sessionWithDeletedDish, otherSession]);

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([existingDish]);

        var result = await _guestSessionService.GetGroupSessionsAsync(userId, groupId);

        var participants = result.Participants.ToList();
        Assert.Equal(2, participants.Count);

        var alice = participants.Single(p => p.Id == "s-deleted");
        Assert.Empty(alice.Items);
        Assert.Equal(0m, alice.Total);

        var bob = participants.Single(p => p.Id == "s-other");
        Assert.Single(bob.Items);
        Assert.Equal(8m, bob.Total);

        Assert.Equal(8m, result.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_GroupReservationNotFound_ThrowsGroupReservationNotFoundException()
    {
        const string groupId = "missing-group";

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _guestSessionService.GetGroupSessionsAsync("owner-1", groupId));

        _mockGuestSessionRepository.Verify(r => r.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_ParentReservationNotFound_ThrowsReservationNotFoundException()
    {
        const string groupId = "group-1";

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupId))
            .ReturnsAsync(new GroupReservation { GroupReservationId = groupId, ReservationId = "res-1", ReservationToken = "token" });

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync("res-1"))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(
            () => _guestSessionService.GetGroupSessionsAsync("owner-1", groupId));

        _mockGuestSessionRepository.Verify(r => r.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_UserIsNotOwner_ThrowsActionForbiddenException()
    {
        const string groupId = "group-1";
        SetupOwnedGroup(groupId, "owner-1");

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _guestSessionService.GetGroupSessionsAsync("another-user", groupId));

        _mockGuestSessionRepository.Verify(r => r.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
        _mockDishRepository.Verify(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_ValidGuest_ReturnsUpdatedSnapshotAndDeletesOnlyTarget()
    {
        const string userId = "owner-1";
        const string groupId = "group-1";
        SetupOwnedGroup(groupId, userId);

        var target = new GuestSession
        {
            Id = "target",
            GuestName = "Removed guest",
            GroupReservationId = groupId,
            Dishes = [new OrderItem { DishId = "removed-dish", Quantity = 10 }]
        };
        var owner = new GuestSession
        {
            Id = "owner-session",
            GuestName = "Owner",
            GroupReservationId = groupId,
            IsOwnerSession = true,
            IsSubmitted = true,
            Dishes = [new OrderItem { DishId = "dish-a", Quantity = 2 }]
        };
        var remainingGuest = new GuestSession
        {
            Id = "guest-session",
            GuestName = "Alice",
            GroupReservationId = groupId,
            IsSubmitted = false,
            Dishes =
            [
                new OrderItem { DishId = "dish-a", Quantity = 1 },
                new OrderItem { DishId = "dish-b", Quantity = 4 }
            ]
        };
        var dishA = new Dish { Id = "dish-a", Name = "Soup", Price = 10m, LocationId = "loc" };
        var dishB = new Dish { Id = "dish-b", Name = "Tea", Price = 2.5m, LocationId = "loc" };

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(target.Id))
            .ReturnsAsync(target);
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupId))
            .ReturnsAsync([owner, remainingGuest]);
        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([dishA, dishB]);

        var result = await _guestSessionService.DeleteGuestSessionAsync(userId, groupId, target.Id);

        var participants = result.Participants.ToList();
        Assert.Equal(2, participants.Count);
        Assert.DoesNotContain(participants, participant => participant.Id == target.Id);

        var ownerParticipant = Assert.Single(participants, participant => participant.Id == owner.Id);
        Assert.Equal(ParticipantType.Owner, ownerParticipant.Type);
        Assert.Equal("Owner", ownerParticipant.Name);
        Assert.True(ownerParticipant.IsSubmitted);
        Assert.Equal(20m, ownerParticipant.Total);
        var ownerItem = Assert.Single(ownerParticipant.Items);
        Assert.Equal("dish-a", ownerItem.Dish.Id);
        Assert.Equal(2, ownerItem.Quantity);

        var guestParticipant = Assert.Single(participants, participant => participant.Id == remainingGuest.Id);
        Assert.Equal(ParticipantType.Guest, guestParticipant.Type);
        Assert.Equal("Alice", guestParticipant.Name);
        Assert.False(guestParticipant.IsSubmitted);
        Assert.Equal(20m, guestParticipant.Total);
        Assert.Equal(2, guestParticipant.Items.Count());
        Assert.Equal(4, guestParticipant.Items.Single(item => item.Dish.Id == dishB.Id).Quantity);
        Assert.Equal(40m, result.Total);
        Assert.Equal(participants.Sum(participant => participant.Total), result.Total);

        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(target), Times.Once);
        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.Is<GuestSession>(session => session.Id != target.Id)), Times.Never);
        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_LastGuest_ReturnsEmptySnapshot()
    {
        const string groupId = "group-1";
        SetupOwnedGroup(groupId, "owner-1");
        var target = new GuestSession
        {
            Id = "target",
            GuestName = "Alice",
            GroupReservationId = groupId,
            Dishes = []
        };
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(target.Id))
            .ReturnsAsync(target);
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupId))
            .ReturnsAsync([]);

        var result = await _guestSessionService.DeleteGuestSessionAsync("owner-1", groupId, target.Id);

        Assert.Empty(result.Participants);
        Assert.Equal(0m, result.Total);
        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(target), Times.Once);
        _mockDishRepository.Verify(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_RemainingSessionReferencesDeletedDish_SkipsMissingDish()
    {
        const string groupId = "group-1";
        SetupOwnedGroup(groupId, "owner-1");
        var target = new GuestSession
        {
            Id = "target",
            GuestName = "Removed",
            GroupReservationId = groupId,
            Dishes = []
        };
        var remaining = new GuestSession
        {
            Id = "remaining",
            GuestName = "Alice",
            GroupReservationId = groupId,
            Dishes =
            [
                new OrderItem { DishId = "missing-dish", Quantity = 8 },
                new OrderItem { DishId = "dish-a", Quantity = 2 }
            ]
        };
        var existingDish = new Dish { Id = "dish-a", Name = "Soup", Price = 4m, LocationId = "loc" };
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(target.Id))
            .ReturnsAsync(target);
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupId))
            .ReturnsAsync([remaining]);
        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([existingDish]);

        var result = await _guestSessionService.DeleteGuestSessionAsync("owner-1", groupId, target.Id);

        var participant = Assert.Single(result.Participants);
        var item = Assert.Single(participant.Items);
        Assert.Equal(existingDish.Id, item.Dish.Id);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(8m, participant.Total);
        Assert.Equal(8m, result.Total);
        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(target), Times.Once);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_TargetNotFound_ThrowsAndDoesNotDeleteOrBuildSnapshot()
    {
        const string groupId = "group-1";
        SetupOwnedGroup(groupId, "owner-1");
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId("missing"))
            .ReturnsAsync((GuestSession?)null);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(
            () => _guestSessionService.DeleteGuestSessionAsync("owner-1", groupId, "missing"));

        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
        _mockGuestSessionRepository.Verify(r => r.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_TargetBelongsToAnotherGroup_ThrowsAndDoesNotDelete()
    {
        const string groupId = "group-1";
        SetupOwnedGroup(groupId, "owner-1");
        var target = new GuestSession
        {
            Id = "target",
            GuestName = "Alice",
            GroupReservationId = "other-group",
            Dishes = []
        };
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(target.Id))
            .ReturnsAsync(target);

        await Assert.ThrowsAsync<ValidationException>(
            () => _guestSessionService.DeleteGuestSessionAsync("owner-1", groupId, target.Id));

        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
        _mockGuestSessionRepository.Verify(r => r.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_OwnerSessionTarget_ThrowsAndDoesNotDelete()
    {
        const string groupId = "group-1";
        SetupOwnedGroup(groupId, "owner-1");
        var target = new GuestSession
        {
            Id = "owner-session",
            GuestName = "Owner",
            GroupReservationId = groupId,
            IsOwnerSession = true,
            Dishes = []
        };
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(target.Id))
            .ReturnsAsync(target);

        await Assert.ThrowsAsync<ValidationException>(
            () => _guestSessionService.DeleteGuestSessionAsync("owner-1", groupId, target.Id));

        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
        _mockGuestSessionRepository.Verify(r => r.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_GroupNotFound_ThrowsAndDoesNotDelete()
    {
        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync("missing-group"))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _guestSessionService.DeleteGuestSessionAsync("owner-1", "missing-group", "target"));

        _mockGuestSessionRepository.Verify(r => r.GetByGuestSessionId(It.IsAny<string>()), Times.Never);
        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_ParentReservationNotFound_ThrowsAndDoesNotDelete()
    {
        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync("group-1"))
            .ReturnsAsync(new GroupReservation { GroupReservationId = "group-1", ReservationId = "missing-reservation", ReservationToken = "token" });
        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync("missing-reservation"))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(
            () => _guestSessionService.DeleteGuestSessionAsync("owner-1", "group-1", "target"));

        _mockGuestSessionRepository.Verify(r => r.GetByGuestSessionId(It.IsAny<string>()), Times.Never);
        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_UserIsNotOwner_ThrowsAndDoesNotDelete()
    {
        SetupOwnedGroup("group-1", "owner-1");

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _guestSessionService.DeleteGuestSessionAsync("another-user", "group-1", "target"));

        _mockGuestSessionRepository.Verify(r => r.GetByGuestSessionId(It.IsAny<string>()), Times.Never);
        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Theory]
    [InlineData(GroupReservationStatus.Open)]
    [InlineData(GroupReservationStatus.Submitted)]
    [InlineData(GroupReservationStatus.Closed)]
    public async Task DeleteGuestSessionAsync_SubmittedSessionAndGroupStatus_RemainsDeletable(GroupReservationStatus status)
    {
        const string groupId = "group-1";
        SetupOwnedGroup(groupId, "owner-1");
        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupId))
            .ReturnsAsync(new GroupReservation { GroupReservationId = groupId, ReservationId = "res-1", ReservationToken = "token", Status = status });
        var target = new GuestSession
        {
            Id = "submitted-guest",
            GuestName = "Alice",
            GroupReservationId = groupId,
            IsSubmitted = true,
            Dishes = []
        };
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(target.Id))
            .ReturnsAsync(target);
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupId))
            .ReturnsAsync([]);

        var result = await _guestSessionService.DeleteGuestSessionAsync("owner-1", groupId, target.Id);

        Assert.Empty(result.Participants);
        Assert.Equal(0m, result.Total);
        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(target), Times.Once);
        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerSessionIdAsync_OwnerSessionMissing_CreatesAndReturnsNewSessionId()
    {
        SetupOwnedReservation("res-1", "owner-1");
        SetupGroupByReservation("res-1", "group-1");
        SetupSessions("group-1", new GuestSession { Id = "guest-1", GuestName = "Alice", Dishes = [] });

        GuestSession? saved = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => saved = s)
            .Returns(Task.CompletedTask);

        var result = await _guestSessionService.GetOwnerSessionIdAsync("owner-1", "res-1");

        Assert.NotNull(saved);
        Assert.Equal(saved.Id, result);
        Assert.True(saved.IsOwnerSession);
        Assert.Equal("owner-1", saved.OwnerUserId);
        Assert.Equal("group-1", saved.GroupReservationId);
        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Once);
    }

    [Fact]
    public async Task GetOwnerSessionIdAsync_NoSessionsAtAll_CreatesOwnerSession()
    {
        SetupOwnedReservation("res-1", "owner-1");
        SetupGroupByReservation("res-1", "group-1");
        SetupSessions("group-1");

        var result = await _guestSessionService.GetOwnerSessionIdAsync("owner-1", "res-1");

        Assert.False(string.IsNullOrEmpty(result));
        _mockGuestSessionRepository.Verify(
            r => r.SaveAsync(It.Is<GuestSession>(s => s.Id == result && s.IsOwnerSession)), Times.Once);
    }

    [Fact]
    public async Task GetOwnerSessionIdAsync_OwnerSessionExists_ReturnsExistingIdWithoutCreating()
    {
        SetupOwnedReservation("res-1", "owner-1");
        SetupGroupByReservation("res-1", "group-1");
        SetupSessions(
            "group-1",
            new GuestSession { Id = "guest-1", GuestName = "Alice", Dishes = [] },
            new GuestSession { Id = "owner-session-123", GuestName = "Owner", Dishes = [], IsOwnerSession = true });

        var result = await _guestSessionService.GetOwnerSessionIdAsync("owner-1", "res-1");

        Assert.Equal("owner-session-123", result);
        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerSessionIdAsync_NoGroupReservation_ThrowsAndCreatesNothing()
    {
        SetupOwnedReservation("res-1", "owner-1");
        _mockGroupReservationRepository
            .Setup(r => r.GetByReservationIdAsync("res-1"))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _guestSessionService.GetOwnerSessionIdAsync("owner-1", "res-1"));

        _mockGroupReservationRepository.Verify(r => r.SaveGroupReservationAsync(It.IsAny<GroupReservation>()), Times.Never);
        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerSessionIdAsync_ReservationNotFound_ThrowsReservationNotFoundException()
    {
        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync("res-1"))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(
            () => _guestSessionService.GetOwnerSessionIdAsync("owner-1", "res-1"));

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
        _mockGroupReservationRepository.Verify(r => r.SaveGroupReservationAsync(It.IsAny<GroupReservation>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerSessionIdAsync_UserIsNotOwner_ThrowsActionForbiddenException()
    {
        SetupOwnedReservation("res-1", "owner-1");
        SetupGroupByReservation("res-1", "group-1");

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _guestSessionService.GetOwnerSessionIdAsync("another-user", "res-1"));

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task CreateGroupOwnerSessionAsync_ValidInput_SavesOwnerSessionAndReturnsItsId()
    {
        GuestSession? saved = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => saved = s)
            .Returns(Task.CompletedTask);

        var result = await _guestSessionService.CreateGroupOwnerSessionAsync("owner-1", "group-1");

        Assert.NotNull(saved);
        Assert.Equal(saved.Id, result);
        Assert.True(saved.IsOwnerSession);
        Assert.Equal("owner-1", saved.OwnerUserId);
        Assert.Equal("group-1", saved.GroupReservationId);
        Assert.Empty(saved.Dishes);
    }

    private void SetupOwnedReservation(string reservationId, string ownerId) =>
        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(new Reservation { Id = reservationId, TableId = "table-1", WaiterId = "waiter-1", CustomerId = ownerId });

    private void SetupGroupByReservation(string reservationId, string groupId) =>
        _mockGroupReservationRepository
            .Setup(r => r.GetByReservationIdAsync(reservationId))
            .ReturnsAsync(new GroupReservation { GroupReservationId = groupId, ReservationId = reservationId, ReservationToken = "token" });

    private void SetupSessions(string groupId, params GuestSession[] sessions) =>
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupId))
            .ReturnsAsync(sessions.ToList());

    private void SetupOwnedGroup(string groupId, string ownerId)
    {
        var group = new GroupReservation
        {
            GroupReservationId = groupId,
            ReservationId = "res-1",
            ReservationToken = "token"
        };
        var reservation = new Reservation
        {
            Id = "res-1",
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = ownerId
        };

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupId))
            .ReturnsAsync(group);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);
    }
}





