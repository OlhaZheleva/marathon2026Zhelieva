namespace APIHandler.Models.GroupReservation;

public record GetGroupInviteTokenResponse(string Token);

public record SubmitGroupPreOrderRequest([property: System.ComponentModel.DataAnnotations.Required] string ReservationId);

public record SubmitGroupPreOrderItem(string DishId, int Quantity);