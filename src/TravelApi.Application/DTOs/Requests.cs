using System;

namespace TravelApi.Application.DTOs;

public record CreateFlightRequest(
    string SupplierId, string AirlineCode, string? AirlineName, string FlightNumber,
    string Origin, string? OriginCity, string Destination, string? DestinationCity,
    DateTime DepartureTime, DateTime ArrivalTime, string CabinClass, string? Baggage, string? PNR,
    decimal NetCost, decimal SalePrice, decimal Commission, decimal Tax, string? Notes,
    string? RateId = null,
    string WorkflowStatus = "Solicitado",
    string? ConfirmationNumber = null,
    // Cantidad de pasajeros de ESTE segmento. Opcional (nullable) para no romper
    // las llamadas existentes que no lo mandaban: si llega null, queda sin informar.
    int? PassengerCount = null,
    // Numero de ticket emitido. Antes faltaba en el ALTA (solo estaba en UpdateFlightRequest),
    // asi que el ticket que mandaba el front al crear el vuelo se descartaba silenciosamente.
    // Opcional: en el alta muchas veces todavia no hay ticket emitido. AutoMapper lo mapea
    // por nombre contra FlightSegment.TicketNumber.
    string? TicketNumber = null
);

public record UpdateFlightRequest(
    string SupplierId, string AirlineCode, string? AirlineName, string FlightNumber,
    string Origin, string? OriginCity, string Destination, string? DestinationCity,
    DateTime DepartureTime, DateTime ArrivalTime, string CabinClass, string? Baggage,
    string? TicketNumber, string? PNR,
    decimal NetCost, decimal SalePrice, decimal Commission, decimal Tax, string Status, string? Notes,
    string? RateId = null,
    string WorkflowStatus = "Solicitado",
    string? ConfirmationNumber = null,
    // Cantidad de pasajeros de ESTE segmento. Opcional (nullable) para no romper
    // las llamadas existentes que no lo mandaban: si llega null, queda sin informar.
    int? PassengerCount = null
);

public record CreateHotelRequest(
    string SupplierId, string HotelName, int? StarRating, string City, string? Country,
    DateTime CheckIn, DateTime CheckOut, string RoomType, string MealPlan,
    int Adults, int Children, int Rooms, string? ConfirmationNumber,
    decimal NetCost, decimal SalePrice, decimal Commission, string? Notes,
    string? RoomingAssignments = null,
    string? RateId = null,
    string WorkflowStatus = "Solicitado"
);

public record UpdateHotelRequest(
    string SupplierId, string HotelName, int? StarRating, string City, string? Country,
    DateTime CheckIn, DateTime CheckOut, string RoomType, string MealPlan,
    int Adults, int Children, int Rooms, string? ConfirmationNumber,
    decimal NetCost, decimal SalePrice, decimal Commission, string Status, string? Notes,
    string? RoomingAssignments = null,
    string? RateId = null,
    string WorkflowStatus = "Solicitado"
);

public record CreateTransferRequest(
    string SupplierId, string PickupLocation, string DropoffLocation,
    DateTime PickupDateTime, string? FlightNumber, string VehicleType, int Passengers,
    bool IsRoundTrip, DateTime? ReturnDateTime,
    decimal NetCost, decimal SalePrice, decimal Commission, string? Notes,
    string? RateId = null,
    string WorkflowStatus = "Solicitado",
    string? ConfirmationNumber = null
);

public record UpdateTransferRequest(
    string SupplierId, string PickupLocation, string DropoffLocation,
    DateTime PickupDateTime, string? FlightNumber, string VehicleType, int Passengers,
    bool IsRoundTrip, DateTime? ReturnDateTime,
    string? ConfirmationNumber,
    decimal NetCost, decimal SalePrice, decimal Commission, string Status, string? Notes,
    string? RateId = null,
    string WorkflowStatus = "Solicitado"
);

public record CreatePackageRequest(
    string SupplierId, string PackageName, string Destination,
    DateTime StartDate, DateTime EndDate,
    bool IncludesHotel, bool IncludesFlight, bool IncludesTransfer, bool IncludesExcursions, bool IncludesMeals,
    int Adults, int Children, string? Itinerary,
    decimal NetCost, decimal SalePrice, decimal Commission, string? Notes,
    string? RateId = null,
    string WorkflowStatus = "Solicitado",
    string? ConfirmationNumber = null
);

public record UpdatePackageRequest(
    string SupplierId, string PackageName, string Destination,
    DateTime StartDate, DateTime EndDate,
    bool IncludesHotel, bool IncludesFlight, bool IncludesTransfer, bool IncludesExcursions, bool IncludesMeals,
    int Adults, int Children, string? Itinerary, string? ConfirmationNumber,
    decimal NetCost, decimal SalePrice, decimal Commission, string Status, string? Notes,
    string? RateId = null,
    string WorkflowStatus = "Solicitado"
);
