using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GuestSessions;
using APIHandler.Models.Dishes;
using APIHandler.Models.GuestSessions;
using APIHandler.Models.Orders;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers;

public class DeleteGuestSessionHandlerTests
{
    private const string UserId = "owner-1";
    private const string GroupId = "group-1";
    private const string SessionId = "session-1";

    private readonly Mock<IGuestSessionService> _mockGuestSessionService = new();
    private readonly Mock<ILambdaContext> _mockContext = new();
    private readonly Mock<ILambdaLogger> _mockLogger = new();
    private readonly DeleteGuestSessionHandler _handler;

    public DeleteGuestSessionHandlerTests()
    {
        _mockContext.SetupGet(context => context.Logger).Returns(_mockLogger.Object);
        _handler = new DeleteGuestSessionHandler(_mockGuestSessionService.Object);
    }

    [Fact]
    public async Task HandleRequest_MissingGroupPathValue_Returns400()
    {
        var response = await _handler.HandleRequest(CreateRequest(groupId: null, SessionId, UserId), _mockContext.Object);

        Assert.Equal(400, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Fact]
    public async Task HandleRequest_MissingSessionPathValue_Returns400()
    {
        var response = await _handler.HandleRequest(CreateRequest(GroupId, guestSessionId: null, UserId), _mockContext.Object);

        Assert.Equal(400, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Fact]
    public async Task HandleRequest_NoSubClaim_Returns401()
    {
        var response = await _handler.HandleRequest(CreateRequest(GroupId, SessionId, userId: null), _mockContext.Object);

        Assert.Equal(401, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Fact]
    public async Task HandleRequest_UserIsNotOwner_Returns403()
    {
        _mockGuestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(UserId, GroupId, SessionId))
            .ThrowsAsync(new ActionForbiddenException("Only the reservation owner can perform this action."));

        var response = await _handler.HandleRequest(CreateRequest(GroupId, SessionId, UserId), _mockContext.Object);

        Assert.Equal(403, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_GroupNotFound_Returns404()
    {
        _mockGuestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(UserId, GroupId, SessionId))
            .ThrowsAsync(new GroupReservationNotFoundException());

        var response = await _handler.HandleRequest(CreateRequest(GroupId, SessionId, UserId), _mockContext.Object);

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_SessionNotFound_Returns404()
    {
        _mockGuestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(UserId, GroupId, SessionId))
            .ThrowsAsync(new GuestSessionNotFoundException("Guest session was not found."));

        var response = await _handler.HandleRequest(CreateRequest(GroupId, SessionId, UserId), _mockContext.Object);

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_ParentReservationNotFound_Returns404()
    {
        _mockGuestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(UserId, GroupId, SessionId))
            .ThrowsAsync(new ReservationNotFoundException("Parent reservation was not found."));

        var response = await _handler.HandleRequest(CreateRequest(GroupId, SessionId, UserId), _mockContext.Object);

        Assert.Equal(404, response.StatusCode);
    }

    [Theory]
    [InlineData("Guest session does not belong to this group reservation.")]
    [InlineData("The reservation owner's session cannot be deleted.")]
    public async Task HandleRequest_ValidationFailure_Returns400(string message)
    {
        _mockGuestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(UserId, GroupId, SessionId))
            .ThrowsAsync(new ValidationException(message));

        var response = await _handler.HandleRequest(CreateRequest(GroupId, SessionId, UserId), _mockContext.Object);

        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_ValidRequest_Returns200WithCamelCaseSnapshot()
    {
        var dish = new DishResponse
        {
            Id = "dish-1",
            Name = "Soup",
            Description = "Hot soup",
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
        };
        var participant = new Participant(
            "remaining-session",
            ParticipantType.Guest,
            "Alice",
            [new OrderItemResponse { Dish = dish, Quantity = 2 }],
            8m,
            true);
        _mockGuestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(UserId, GroupId, SessionId))
            .ReturnsAsync(new GetAllSessionsResponse([participant], 8m));

        var response = await _handler.HandleRequest(CreateRequest(GroupId, SessionId, UserId), _mockContext.Object);

        Assert.Equal(200, response.StatusCode);
        using var document = JsonDocument.Parse(response.Body);
        var root = document.RootElement;
        Assert.Equal(8m, root.GetProperty("total").GetDecimal());
        var serializedParticipant = Assert.Single(root.GetProperty("participants").EnumerateArray());
        Assert.Equal("remaining-session", serializedParticipant.GetProperty("id").GetString());
        Assert.Equal("Guest", serializedParticipant.GetProperty("type").GetString());
        Assert.Equal("Alice", serializedParticipant.GetProperty("name").GetString());
        Assert.True(serializedParticipant.GetProperty("isSubmitted").GetBoolean());
        Assert.Equal(8m, serializedParticipant.GetProperty("total").GetDecimal());
        var item = Assert.Single(serializedParticipant.GetProperty("items").EnumerateArray());
        Assert.Equal(2, item.GetProperty("quantity").GetInt32());
        Assert.Equal("dish-1", item.GetProperty("dish").GetProperty("id").GetString());
        _mockGuestSessionService.Verify(
            service => service.DeleteGuestSessionAsync(UserId, GroupId, SessionId), Times.Once);
    }

    private void VerifyServiceNotCalled() =>
        _mockGuestSessionService.Verify(
            service => service.DeleteGuestSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);

    private static APIGatewayProxyRequest CreateRequest(string? groupId, string? guestSessionId, string? userId)
    {
        var pathParameters = new Dictionary<string, string>();
        if (groupId is not null)
        {
            pathParameters["groupReservationId"] = groupId;
        }

        if (guestSessionId is not null)
        {
            pathParameters["guestSessionId"] = guestSessionId;
        }

        return new APIGatewayProxyRequest
        {
            PathParameters = pathParameters,
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
}