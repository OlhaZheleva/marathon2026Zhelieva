using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GuestSessions;
using APIHandler.Handlers.GroupReservations;
using APIHandler.Models.GroupReservation;
using APIHandler.Models.GuestSessions;
using APIHandler.Models.Orders;
using APIHandler.Models.Dishes;
using APIHandler.Services.GroupReservations;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers;

public class GetGroupSessionsHandlerTests
{
    private const string UserId = "owner-1";
    private const string GroupId = "group-1";

    private readonly Mock<IGuestSessionService> _mockGuestSessionService = new();
    private readonly Mock<ILambdaContext> _mockContext = new();
    private readonly Mock<ILambdaLogger> _mockLogger = new();
    private readonly GetGroupSessionsHandler _handler;

    public GetGroupSessionsHandlerTests()
    {
        _mockContext.SetupGet(c => c.Logger).Returns(_mockLogger.Object);
        _handler = new GetGroupSessionsHandler(_mockGuestSessionService.Object);
    }

    [Fact]
    public async Task HandleRequest_MissingPathValue_Returns400()
    {
        var request = CreateRequest(groupId: null, userId: UserId);

        var response = await _handler.HandleRequest(request, _mockContext.Object);

        Assert.Equal(400, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Fact]
    public async Task HandleRequest_NoClaims_Returns401()
    {
        var request = CreateRequest(GroupId, userId: null);

        var response = await _handler.HandleRequest(request, _mockContext.Object);

        Assert.Equal(401, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Fact]
    public async Task HandleRequest_NoRequestContext_Returns401()
    {
        var request = CreateRequest(GroupId, userId: null);
        request.RequestContext = null;

        var response = await _handler.HandleRequest(request, _mockContext.Object);

        Assert.Equal(401, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Fact]
    public async Task HandleRequest_ClaimsWithoutSub_Returns401()
    {
        var request = CreateRequest(GroupId, userId: null);
        request.RequestContext.Authorizer = new APIGatewayCustomAuthorizerContext
        {
            Claims = new Dictionary<string, string> { ["email"] = "user@example.com" }
        };

        var response = await _handler.HandleRequest(request, _mockContext.Object);

        Assert.Equal(401, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Fact]
    public async Task HandleRequest_UserIsNotOwner_Returns403()
    {
        _mockGuestSessionService
            .Setup(s => s.GetGroupSessionsAsync(UserId, GroupId))
            .ThrowsAsync(new ActionForbiddenException("Only the reservation owner can perform this action."));

        var response = await _handler.HandleRequest(CreateRequest(GroupId, UserId), _mockContext.Object);

        Assert.Equal(403, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_GroupReservationNotFound_Returns404()
    {
        _mockGuestSessionService
            .Setup(s => s.GetGroupSessionsAsync(UserId, GroupId))
            .ThrowsAsync(new GroupReservationNotFoundException());

        var response = await _handler.HandleRequest(CreateRequest(GroupId, UserId), _mockContext.Object);

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_ParentReservationNotFound_Returns404()
    {
        _mockGuestSessionService
            .Setup(s => s.GetGroupSessionsAsync(UserId, GroupId))
            .ThrowsAsync(new ReservationNotFoundException("Parent reservation not found."));

        var response = await _handler.HandleRequest(CreateRequest(GroupId, UserId), _mockContext.Object);

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_ValidRequest_Returns200WithCamelCaseBody()
    {
        var participant = new Participant(
            "s-1",
            ParticipantType.Guest,
            "Alice",
            [new OrderItemResponse { Dish = new DishResponse
            {
                Id = "d-1",
                Name = "Soup",
                Description = "d",
                DishType = "Appetizer",
                Price = 4m,
                Weight = "1",
                Calories = "1",
                Proteins = "1",
                Fats = "1",
                Carbohydrates = "1",
                Vitamins = "1",
                ImageUrl = null,
                State = "Available"
            }, Quantity = 2 }],
            8m,
            true);
        _mockGuestSessionService
            .Setup(s => s.GetGroupSessionsAsync(UserId, GroupId))
            .ReturnsAsync(new GetAllSessionsResponse([participant], 8m));

        var response = await _handler.HandleRequest(CreateRequest(GroupId, UserId), _mockContext.Object);

        Assert.Equal(200, response.StatusCode);
        using var doc = JsonDocument.Parse(response.Body);
        var root = doc.RootElement;
        Assert.Equal(8m, root.GetProperty("total").GetDecimal());
        var participants = root.GetProperty("participants");
        Assert.Equal(1, participants.GetArrayLength());
        var first = participants[0];
        Assert.Equal("Guest", first.GetProperty("type").GetString());
        Assert.Equal("Alice", first.GetProperty("name").GetString());
        Assert.True(first.GetProperty("isSubmitted").GetBoolean());
        Assert.Equal(8m, first.GetProperty("total").GetDecimal());
        Assert.Equal("s-1", first.GetProperty("id").GetString());
        var firstItem = first.GetProperty("items")[0];
        Assert.Equal(2, firstItem.GetProperty("quantity").GetInt32());
        Assert.Equal("d-1", firstItem.GetProperty("dish").GetProperty("id").GetString());
    }

    [Fact]
    public async Task HandleRequest_EmptyGroup_Returns200WithEmptyParticipants()
    {
        _mockGuestSessionService
            .Setup(s => s.GetGroupSessionsAsync(UserId, GroupId))
            .ReturnsAsync(new GetAllSessionsResponse([], 0m));

        var response = await _handler.HandleRequest(CreateRequest(GroupId, UserId), _mockContext.Object);

        Assert.Equal(200, response.StatusCode);
        using var doc = JsonDocument.Parse(response.Body);
        Assert.Equal(0, doc.RootElement.GetProperty("participants").GetArrayLength());
        Assert.Equal(0m, doc.RootElement.GetProperty("total").GetDecimal());
    }

    private void VerifyServiceNotCalled() =>
        _mockGuestSessionService.Verify(s => s.GetGroupSessionsAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

    private static APIGatewayProxyRequest CreateRequest(string? groupId, string? userId)
    {
        var request = new APIGatewayProxyRequest
        {
            PathParameters = groupId is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["groupReservationId"] = groupId },
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = userId is null
                    ? null
                    : new APIGatewayCustomAuthorizerContext
                    {
                        Claims = new Dictionary<string, string> { ["sub"] = userId }
                    }
            }
        };

        return request;
    }
}

public class GetOwnerSessionIdHandlerTests
{
    private const string UserId = "owner-1";
    private const string ReservationId = "res-1";

    private readonly Mock<IGuestSessionService> _mockGuestSessionService = new();
    private readonly Mock<ILambdaContext> _mockContext = new();
    private readonly Mock<ILambdaLogger> _mockLogger = new();
    private readonly GetOwnerSessionIdHandler _handler;

    public GetOwnerSessionIdHandlerTests()
    {
        _mockContext.SetupGet(c => c.Logger).Returns(_mockLogger.Object);
        _handler = new GetOwnerSessionIdHandler(_mockGuestSessionService.Object);
    }

    [Fact]
    public async Task HandleRequest_ValidRequest_Returns200WithCamelCaseBody()
    {
        _mockGuestSessionService
            .Setup(s => s.GetOwnerSessionIdAsync(UserId, ReservationId))
            .ReturnsAsync("owner-session-123");

        var response = await _handler.HandleRequest(CreateRequest(ReservationId, UserId), _mockContext.Object);

        Assert.Equal(200, response.StatusCode);
        using var doc = JsonDocument.Parse(response.Body);
        Assert.Equal("owner-session-123", doc.RootElement.GetProperty("guestSessionId").GetString());
    }

    [Fact]
    public async Task HandleRequest_MissingPathValue_Returns400()
    {
        var response = await _handler.HandleRequest(CreateRequest(null, UserId), _mockContext.Object);

        Assert.Equal(400, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Fact]
    public async Task HandleRequest_NoClaims_Returns401()
    {
        var response = await _handler.HandleRequest(CreateRequest(ReservationId, null), _mockContext.Object);

        Assert.Equal(401, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Fact]
    public async Task HandleRequest_ClaimsWithoutSub_Returns401()
    {
        var request = CreateRequest(ReservationId, null);
        request.RequestContext.Authorizer = new APIGatewayCustomAuthorizerContext
        {
            Claims = new Dictionary<string, string> { ["email"] = "user@example.com" }
        };

        var response = await _handler.HandleRequest(request, _mockContext.Object);

        Assert.Equal(401, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Fact]
    public async Task HandleRequest_UserIsNotOwner_Returns403()
    {
        _mockGuestSessionService
            .Setup(s => s.GetOwnerSessionIdAsync(UserId, ReservationId))
            .ThrowsAsync(new ActionForbiddenException("Only the reservation owner can perform this action."));

        var response = await _handler.HandleRequest(CreateRequest(ReservationId, UserId), _mockContext.Object);

        Assert.Equal(403, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_ReservationNotFound_Returns404()
    {
        _mockGuestSessionService
            .Setup(s => s.GetOwnerSessionIdAsync(UserId, ReservationId))
            .ThrowsAsync(new ReservationNotFoundException("Reservation res-1 was not found."));

        var response = await _handler.HandleRequest(CreateRequest(ReservationId, UserId), _mockContext.Object);

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_GroupReservationNotFound_Returns404()
    {
        _mockGuestSessionService
            .Setup(s => s.GetOwnerSessionIdAsync(UserId, ReservationId))
            .ThrowsAsync(new GroupReservationNotFoundException());

        var response = await _handler.HandleRequest(CreateRequest(ReservationId, UserId), _mockContext.Object);

        Assert.Equal(404, response.StatusCode);
    }

    private void VerifyServiceNotCalled() =>
        _mockGuestSessionService.Verify(s => s.GetOwnerSessionIdAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

    private static APIGatewayProxyRequest CreateRequest(string? reservationId, string? userId) => new()
    {
        PathParameters = reservationId is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["reservationId"] = reservationId },
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = userId is null
                ? null
                : new APIGatewayCustomAuthorizerContext
                {
                    Claims = new Dictionary<string, string> { ["sub"] = userId }
                }
        }
    };
}

public class SubmitGroupPreOrderHandlerTests
{
    private const string UserId = "owner-1";
    private const string GroupId = "group-1";
    private const string ReservationId = "res-1";

    private readonly Mock<IGroupReservationService> _mockService = new();
    private readonly Mock<ILambdaContext> _mockContext = new();
    private readonly Mock<ILambdaLogger> _mockLogger = new();
    private readonly SubmitGroupPreOrderHandler _handler;

    public SubmitGroupPreOrderHandlerTests()
    {
        _mockContext.SetupGet(c => c.Logger).Returns(_mockLogger.Object);
        _handler = new SubmitGroupPreOrderHandler(_mockService.Object);
    }

    [Fact]
    public async Task HandleRequest_ValidRequest_Returns200WithConsolidatedDishes()
    {
        _mockService
            .Setup(s => s.SubmitGroupPreOrderAsync(UserId, GroupId, ReservationId))
            .ReturnsAsync([new SubmitGroupPreOrderItem("dish1", 3), new SubmitGroupPreOrderItem("dish2", 2)]);

        var response = await _handler.HandleRequest(CreateRequest(GroupId, UserId, ValidBody), _mockContext.Object);

        Assert.Equal(200, response.StatusCode);
        using var doc = JsonDocument.Parse(response.Body);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal("dish1", doc.RootElement[0].GetProperty("dishId").GetString());
        Assert.Equal(3, doc.RootElement[0].GetProperty("quantity").GetInt32());
        Assert.Equal("dish2", doc.RootElement[1].GetProperty("dishId").GetString());
        Assert.Equal(2, doc.RootElement[1].GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task HandleRequest_MissingPathValue_Returns400()
    {
        var response = await _handler.HandleRequest(CreateRequest(null, UserId, ValidBody), _mockContext.Object);

        Assert.Equal(400, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Fact]
    public async Task HandleRequest_NoClaims_Returns401()
    {
        var response = await _handler.HandleRequest(CreateRequest(GroupId, null, ValidBody), _mockContext.Object);

        Assert.Equal(401, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"reservationId\":\"\"}")]
    public async Task HandleRequest_InvalidBody_Returns400(string? body)
    {
        var response = await _handler.HandleRequest(CreateRequest(GroupId, UserId, body), _mockContext.Object);

        Assert.Equal(400, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Fact]
    public async Task HandleRequest_UserIsNotOwner_Returns403()
    {
        _mockService
            .Setup(s => s.SubmitGroupPreOrderAsync(UserId, GroupId, ReservationId))
            .ThrowsAsync(new ActionForbiddenException("Only the reservation owner can perform this action."));

        var response = await _handler.HandleRequest(CreateRequest(GroupId, UserId, ValidBody), _mockContext.Object);

        Assert.Equal(403, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_GroupReservationNotFound_Returns404()
    {
        _mockService
            .Setup(s => s.SubmitGroupPreOrderAsync(UserId, GroupId, ReservationId))
            .ThrowsAsync(new GroupReservationNotFoundException());

        var response = await _handler.HandleRequest(CreateRequest(GroupId, UserId, ValidBody), _mockContext.Object);

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_ReservationNotFound_Returns404()
    {
        _mockService
            .Setup(s => s.SubmitGroupPreOrderAsync(UserId, GroupId, ReservationId))
            .ThrowsAsync(new ReservationNotFoundException("Reservation res-1 was not found."));

        var response = await _handler.HandleRequest(CreateRequest(GroupId, UserId, ValidBody), _mockContext.Object);

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_ValidationFailure_Returns400()
    {
        _mockService
            .Setup(s => s.SubmitGroupPreOrderAsync(UserId, GroupId, ReservationId))
            .ThrowsAsync(new ValidationException("There are no submitted guest sessions in this group reservation."));

        var response = await _handler.HandleRequest(CreateRequest(GroupId, UserId, ValidBody), _mockContext.Object);

        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_GroupAlreadySubmitted_Returns400WithSubmittedErrorCode()
    {
        _mockService
            .Setup(s => s.SubmitGroupPreOrderAsync(UserId, GroupId, ReservationId))
            .ThrowsAsync(new GroupReservationSubmittedException());

        var response = await _handler.HandleRequest(CreateRequest(GroupId, UserId, ValidBody), _mockContext.Object);

        Assert.Equal(400, response.StatusCode);
        using var doc = JsonDocument.Parse(response.Body);
        Assert.Equal("GroupReservationSubmitted", doc.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task HandleRequest_GroupClosed_Returns400()
    {
        _mockService
            .Setup(s => s.SubmitGroupPreOrderAsync(UserId, GroupId, ReservationId))
            .ThrowsAsync(new GroupReservationExpiredException());

        var response = await _handler.HandleRequest(CreateRequest(GroupId, UserId, ValidBody), _mockContext.Object);

        Assert.Equal(400, response.StatusCode);
    }

    private static string ValidBody => $"{{\"reservationId\":\"{ReservationId}\"}}";

    private void VerifyServiceNotCalled() =>
        _mockService.Verify(
            s => s.SubmitGroupPreOrderAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);

    private static APIGatewayProxyRequest CreateRequest(string? groupId, string? userId, string? body) => new()
    {
        Body = body,
        PathParameters = groupId is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["groupReservationId"] = groupId },
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = userId is null
                ? null
                : new APIGatewayCustomAuthorizerContext
                {
                    Claims = new Dictionary<string, string> { ["sub"] = userId }
                }
        }
    };
}
