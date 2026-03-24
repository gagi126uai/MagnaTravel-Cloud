using System;

namespace TravelApi.Application.DTOs;

public record CreateFlightRequest(
    string SupplierId, string AirlineCode, string? AirlineName, string FlightNumber,
    string Origin, string? OriginCity, string Destination, string? DestinationCity,
    DateTime DepartureTime, DateTime ArrivalTime, string CabinClass, string? Baggage, string? PNR,
    decimal NetCost, decimal SalePrice, decimal Commission, decimal Tax, string? Notes
);

public record UpdateFlightRequest(
    string SupplierId, string AirlineCode, string? AirlineName, string FlightNumber,
    string Origin, string? OriginCity, string Destination, string? DestinationCity,
    DateTime DepartureTime, DateTime ArrivalTime, string CabinClass, string? Baggage, 
    string? TicketNumber, string? PNR,
    decimal NetCost, decimal SalePrice, decimal Commission, decimal Tax, string Status, string? Notes
);

public record CreateHotelRequest(
    string SupplierId, string HotelName, int? StarRating, string City, string? Country,
    DateTime CheckIn, DateTime CheckOut, string RoomType, string MealPlan,
    int Adults, int Children, int Rooms, string? ConfirmationNumber,
    decimal NetCost, decimal SalePrice, decimal Commission, string? Notes
);

public record UpdateHotelRequest(
    string SupplierId, string HotelName, int? StarRating, string City, string? Country,
    DateTime CheckIn, DateTime CheckOut, string RoomType, string MealPlan,
    int Adults, int Children, int Rooms, string? ConfirmationNumber,
    decimal NetCost, decimal SalePrice, decimal Commission, string Status, string? Notes
);

public record CreateTransferRequest(
    string SupplierId, string PickupLocation, string DropoffLocation,
    DateTime PickupDateTime, string? FlightNumber, string VehicleType, int Passengers,
    bool IsRoundTrip, DateTime? ReturnDateTime,
    decimal NetCost, decimal SalePrice, decimal Commission, string? Notes
);

public record UpdateTransferRequest(
    string SupplierId, string PickupLocation, string DropoffLocation,
    DateTime PickupDateTime, string? FlightNumber, string VehicleType, int Passengers,
    bool IsRoundTrip, DateTime? ReturnDateTime,
    string? ConfirmationNumber,
    decimal NetCost, decimal SalePrice, decimal Commission, string Status, string? Notes
);

public record CreatePackageRequest(
    string SupplierId, string PackageName, string Destination,
    DateTime StartDate, DateTime EndDate,
    bool IncludesHotel, bool IncludesFlight, bool IncludesTransfer, bool IncludesExcursions, bool IncludesMeals,
    int Adults, int Children, string? Itinerary,
    decimal NetCost, decimal SalePrice, decimal Commission, string? Notes
);

public record UpdatePackageRequest(
    string SupplierId, string PackageName, string Destination,
    DateTime StartDate, DateTime EndDate,
    bool IncludesHotel, bool IncludesFlight, bool IncludesTransfer, bool IncludesExcursions, bool IncludesMeals,
    int Adults, int Children, string? Itinerary, string? ConfirmationNumber,
    decimal NetCost, decimal SalePrice, decimal Commission, string Status, string? Notes
);
