using System;
using System.ComponentModel.DataAnnotations;

namespace TravelApi.Application.DTOs;

/// <summary>
/// ADR-017 F1.3 (§2.3.b): sub-objeto OPCIONAL para crear un producto del catalogo "en linea", en la
/// misma operacion que el servicio. Mutuamente excluyente con <c>RateId</c> (400 si vienen ambos).
/// Solo tiene efecto con el flag <c>EnableCatalogFindOrCreate</c> ON; con OFF se ignora (o 400 segun
/// el create). Campos minimos: el resto del producto se enriquece despues desde el back-office.
/// </summary>
public record NewCatalogProductRequest(
    // Nombre del producto tal como lo escribio el vendedor (hotel, ruta, plan...). De aca sale el
    // SearchName normalizado para el anti-duplicados.
    [property: Required, MaxLength(200)] string Name,
    // Hotel: OBLIGATORIA (decision D6 — 400 si falta o viene en blanco). Otros tipos: destino/ruta,
    // opcional. La validacion "obligatoria para Hotel" se hace en el service (depende del tipo).
    [property: MaxLength(100)] string? City,
    // Operador con el que se crea el producto. Obligatorio: un producto del catalogo siempre nace con
    // un proveedor (su primera combinacion en RateSupplierSale).
    [property: Required] string SupplierPublicId);

/// <summary>
/// ADR-017 F1.3 (§2.8, D8c): body OPCIONAL del boton "Confirmar costo". Si trae montos, el confirmador
/// CORRIGE el costo; si viene vacio, CONFIRMA el costo resuelto tal cual (incluido 0, aserción humana
/// deliberada). Son montos TOTALES del servicio (la misma unidad que persiste el booking).
/// </summary>
public record ConfirmCostRequest(
    decimal? NetCost = null,
    decimal? Tax = null);

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
    string? TicketNumber = null,
    // ADR-017 F1.3 (§2.1, D5): moneda REAL de esta venta (ARS / USD). Con flag ON es OBLIGATORIA
    // (la ficha siempre la manda); con flag OFF se ignora si viene null (byte-identico). El map la
    // ignora a proposito: la asigna el service segun el flag (ver MappingProfile).
    string? Currency = null,
    // ADR-017 F1.3 (§2.3.b): crear producto del catalogo en linea. Mutuamente excluyente con RateId.
    NewCatalogProductRequest? NewCatalogProduct = null,
    // ADR-017 F1.4 (§2.2/§2.6): fecha limite de EMISION del ticket, opcional. La API la acepta y persiste
    // aun con el flag OFF (precedente Currency traceability); sin UI que la cargue no cambia nada observable.
    // El map la IGNORA (ver MappingProfile) y la asigna el service normalizada a medianoche Kind=Utc.
    DateTime? TicketingDeadline = null
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
    int? PassengerCount = null,
    // ADR-017 F1.1 (2026-06-05): fecha limite de EMISION del ticket. Mismo criterio que
    // UpdateHotelRequest: Ignore() en el map + handler de persistencia gobernado por
    // DeadlinesSpecified, que llega en F1.4. En F1.1 solo estructura.
    DateTime? TicketingDeadline = null,
    bool DeadlinesSpecified = false
);

public record CreateHotelRequest(
    string SupplierId, string HotelName, int? StarRating, string City, string? Country,
    DateTime CheckIn, DateTime CheckOut, string RoomType, string MealPlan,
    int Adults, int Children, int Rooms, string? ConfirmationNumber,
    decimal NetCost, decimal SalePrice, decimal Commission, string? Notes,
    string? RoomingAssignments = null,
    string? RateId = null,
    string WorkflowStatus = "Solicitado",
    // Impuesto INCLUIDO en el costo (no suma al precio del cliente). El front lo manda ya restado
    // en la Commission (SalePrice - NetCost - Tax). Opcional con default 0 para no romper los
    // callers posicionales existentes que no lo enviaban. Ver HotelBooking.Tax.
    decimal Tax = 0,
    // ADR-017 F1.3 (§2.1, D5): moneda REAL de esta venta. Obligatoria con flag ON, ignorada con OFF.
    string? Currency = null,
    // ADR-017 F1.3 (§2.3.b): crear producto del catalogo en linea. Mutuamente excluyente con RateId.
    // Para Hotel exige City (D6).
    NewCatalogProductRequest? NewCatalogProduct = null,
    // ADR-017 F1.4 (§2.2/§2.6): fecha limite de seña/pago al operador, opcional. La API la acepta y
    // persiste aun con el flag OFF; el map la IGNORA y la asigna el service normalizada a medianoche Kind=Utc.
    DateTime? OperatorPaymentDeadline = null,
    // Direccion del hotel (campo "Mas detalles" del form). Antes el front la mandaba y se descartaba
    // porque el request no la tenia. Opcional con default null para no romper los callers posicionales.
    // AutoMapper la mapea por nombre contra HotelBooking.Address (no es campo de costo, es inocuo).
    string? Address = null
);

public record UpdateHotelRequest(
    string SupplierId, string HotelName, int? StarRating, string City, string? Country,
    DateTime CheckIn, DateTime CheckOut, string RoomType, string MealPlan,
    int Adults, int Children, int Rooms, string? ConfirmationNumber,
    decimal NetCost, decimal SalePrice, decimal Commission, string Status, string? Notes,
    string? RoomingAssignments = null,
    string? RateId = null,
    string WorkflowStatus = "Solicitado",
    // Impuesto INCLUIDO en el costo (no suma al precio del cliente). Opcional con default 0 para
    // no romper los callers posicionales existentes. Ver HotelBooking.Tax.
    decimal Tax = 0,
    // ADR-017 F1.1 (2026-06-05): fecha limite de seña/pago al operador. El map de update hace
    // Ignore() de este campo (ver MappingProfile) — en F1.1 el handler NO lo persiste todavia
    // (eso es F1.4). DeadlinesSpecified distingue "no lo mande" (modal viejo) de "borralo": con
    // false (default) se preserva el valor persistido; con true se asigna lo que vino, null = borrar.
    DateTime? OperatorPaymentDeadline = null,
    bool DeadlinesSpecified = false,
    // Direccion del hotel (campo "Mas detalles" del form). Opcional con default null para no romper los
    // callers posicionales. AutoMapper la mapea por nombre a HotelBooking.Address, igual que los demas
    // strings del hotel (Country/Notes): el modal la reenvia en cada edicion, no usa discriminador.
    string? Address = null
);

public record CreateTransferRequest(
    string SupplierId, string PickupLocation, string DropoffLocation,
    DateTime PickupDateTime, string? FlightNumber, string VehicleType, int Passengers,
    bool IsRoundTrip, DateTime? ReturnDateTime,
    decimal NetCost, decimal SalePrice, decimal Commission, string? Notes,
    string? RateId = null,
    string WorkflowStatus = "Solicitado",
    string? ConfirmationNumber = null,
    // Impuesto INCLUIDO en el costo (no suma al precio del cliente). Opcional con default 0 para
    // no romper los callers posicionales existentes. Ver TransferBooking.Tax.
    decimal Tax = 0,
    // ADR-017 F1.3 (§2.1, D5): moneda REAL de esta venta. Obligatoria con flag ON, ignorada con OFF.
    string? Currency = null,
    // ADR-017 F1.3 (§2.3.b): crear producto del catalogo en linea. Mutuamente excluyente con RateId.
    NewCatalogProductRequest? NewCatalogProduct = null
);

public record UpdateTransferRequest(
    string SupplierId, string PickupLocation, string DropoffLocation,
    DateTime PickupDateTime, string? FlightNumber, string VehicleType, int Passengers,
    bool IsRoundTrip, DateTime? ReturnDateTime,
    string? ConfirmationNumber,
    decimal NetCost, decimal SalePrice, decimal Commission, string Status, string? Notes,
    string? RateId = null,
    string WorkflowStatus = "Solicitado",
    // Impuesto INCLUIDO en el costo (no suma al precio del cliente). Opcional con default 0 para
    // no romper los callers posicionales existentes. Ver TransferBooking.Tax.
    decimal Tax = 0
);

// Bloque 3: Asistencia al viajero (seguro). Espeja a CreateHotelRequest/UpdateHotelRequest:
// SupplierId = aseguradora, vigencia date-only (ValidFrom/ValidTo, como CheckIn/CheckOut),
// y campos financieros NetCost/SalePrice/Commission (Commission entra al request pero NO se
// expone en el DTO de salida). Campos de negocio del seguro al final, todos opcionales.
public record CreateAssistanceRequest(
    string SupplierId,
    DateTime ValidFrom, DateTime ValidTo,
    int Adults, int Children,
    decimal NetCost, decimal SalePrice, decimal Commission,
    string? PolicyNumber = null, string? PlanType = null, string? CoverageLimit = null,
    string? CoverageZone = null, string? ConfirmationNumber = null, string? Notes = null,
    string? RateId = null,
    string WorkflowStatus = "Solicitado",
    // Impuesto INCLUIDO en el costo (no suma al precio del cliente). Opcional con default 0 para
    // no romper los callers posicionales existentes. Ver AssistanceBooking.Tax.
    decimal Tax = 0,
    // ADR-017 F1.3 (§2.1, D5): moneda REAL de esta venta. Obligatoria con flag ON, ignorada con OFF.
    string? Currency = null,
    // ADR-017 F1.3 (§2.3.b): crear producto del catalogo en linea. Mutuamente excluyente con RateId.
    NewCatalogProductRequest? NewCatalogProduct = null
);

public record UpdateAssistanceRequest(
    string SupplierId,
    DateTime ValidFrom, DateTime ValidTo,
    int Adults, int Children,
    decimal NetCost, decimal SalePrice, decimal Commission,
    string Status,
    string? PolicyNumber = null, string? PlanType = null, string? CoverageLimit = null,
    string? CoverageZone = null, string? ConfirmationNumber = null, string? Notes = null,
    string? RateId = null,
    string WorkflowStatus = "Solicitado",
    // Impuesto INCLUIDO en el costo (no suma al precio del cliente). Opcional con default 0 para
    // no romper los callers posicionales existentes. Ver AssistanceBooking.Tax.
    decimal Tax = 0
);

public record CreatePackageRequest(
    string SupplierId, string PackageName, string Destination,
    DateTime StartDate, DateTime EndDate,
    bool IncludesHotel, bool IncludesFlight, bool IncludesTransfer, bool IncludesExcursions, bool IncludesMeals,
    int Adults, int Children, string? Itinerary,
    decimal NetCost, decimal SalePrice, decimal Commission, string? Notes,
    string? RateId = null,
    string WorkflowStatus = "Solicitado",
    string? ConfirmationNumber = null,
    // Impuesto INCLUIDO en el costo (no suma al precio del cliente). Opcional con default 0 para
    // no romper los callers posicionales existentes. Ver PackageBooking.Tax.
    decimal Tax = 0,
    // ADR-017 F1.3 (§2.1, D5): moneda REAL de esta venta. Obligatoria con flag ON, ignorada con OFF.
    string? Currency = null,
    // ADR-017 F1.3 (§2.3.b): crear producto del catalogo en linea. Mutuamente excluyente con RateId.
    NewCatalogProductRequest? NewCatalogProduct = null,
    // ADR-017 F1.4 (§2.2/§2.6): fecha limite de seña/pago al operador, opcional. La API la acepta y
    // persiste aun con el flag OFF; el map la IGNORA y la asigna el service normalizada a medianoche Kind=Utc.
    DateTime? OperatorPaymentDeadline = null
);

public record UpdatePackageRequest(
    string SupplierId, string PackageName, string Destination,
    DateTime StartDate, DateTime EndDate,
    bool IncludesHotel, bool IncludesFlight, bool IncludesTransfer, bool IncludesExcursions, bool IncludesMeals,
    int Adults, int Children, string? Itinerary, string? ConfirmationNumber,
    decimal NetCost, decimal SalePrice, decimal Commission, string Status, string? Notes,
    string? RateId = null,
    string WorkflowStatus = "Solicitado",
    // Impuesto INCLUIDO en el costo (no suma al precio del cliente). Opcional con default 0 para
    // no romper los callers posicionales existentes. Ver PackageBooking.Tax.
    decimal Tax = 0,
    // ADR-017 F1.1 (2026-06-05): fecha limite de seña/pago al operador. Mismo criterio que
    // UpdateHotelRequest: Ignore() en el map + handler de persistencia gobernado por
    // DeadlinesSpecified, que llega en F1.4. En F1.1 solo estructura.
    DateTime? OperatorPaymentDeadline = null,
    bool DeadlinesSpecified = false
);
