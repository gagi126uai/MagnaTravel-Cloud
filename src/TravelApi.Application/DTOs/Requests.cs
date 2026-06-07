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
    // OJO: en records los atributos de validacion van en el PARAMETRO del constructor primario
    // (sin "property:"). Con [property:] ASP.NET tira InvalidOperationException (500) al validar
    // el request ("validation metadata must be associated with the constructor parameter").
    // Bug real reportado por Gaston el 2026-06-06 al crear un hotel nuevo desde la ficha.
    [Required, MaxLength(200)] string Name,
    // Hotel: OBLIGATORIA (decision D6 — 400 si falta o viene en blanco). Otros tipos: destino/ruta,
    // opcional. La validacion "obligatoria para Hotel" se hace en el service (depende del tipo).
    [MaxLength(100)] string? City,
    // Operador con el que se crea el producto. Obligatorio: un producto del catalogo siempre nace con
    // un proveedor (su primera combinacion en RateSupplierSale).
    [Required] string SupplierPublicId);

/// <summary>
/// ADR-017 F1.3 (§2.8, D8c): body OPCIONAL del boton "Confirmar costo". Si trae montos, el confirmador
/// CORRIGE el costo; si viene vacio, CONFIRMA el costo resuelto tal cual (incluido 0, aserción humana
/// deliberada). Son montos TOTALES del servicio (la misma unidad que persiste el booking).
/// </summary>
public record ConfirmCostRequest(
    decimal? NetCost = null,
    decimal? Tax = null);

public record CreateFlightRequest(
    // ADR-018: AirlineCode/FlightNumber/Origin/Destination pasan a opcionales (string?). La ficha
    // "producto-primero" identifica el vuelo con ProductName (un solo texto) y puede omitirlos; el
    // modal viejo los sigue mandando. Siguen siendo posicionales (no llevan default).
    // ADR-018 Ronda 7 (guia UX, 2026-06-06): CabinClass tambien pasa a opcional. "Cabina" deja de ser
    // exigida ("Sin especificar" es una opcion real del desplegable); null/vacio se persiste como null
    // y YA NO se coalesce a "Economy" (supersede el default de negocio de ADR-018 §2).
    string SupplierId, string? AirlineCode, string? AirlineName, string? FlightNumber,
    string? Origin, string? OriginCity, string? Destination, string? DestinationCity,
    DateTime DepartureTime, DateTime ArrivalTime, string? CabinClass, string? Baggage, string? PNR,
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
    // ADR-018 (§4-bis): nombre del producto que VIO/tipeo el vendedor en la ficha. Fuente UNICA de
    // FlightSegment.ProductName (se copia al crear, no se re-deriva del Rate). Opcional al final.
    // (ADR-019: aca vivia TicketingDeadline; la fecha limite manual murio con el aviso automatico.)
    string? ProductName = null
);

public record UpdateFlightRequest(
    // ADR-018: estructurados opcionales. Ronda 7 (2026-06-06): CabinClass opcional, sin default de
    // negocio — null = "Sin especificar" (ver CreateFlightRequest).
    string SupplierId, string? AirlineCode, string? AirlineName, string? FlightNumber,
    string? Origin, string? OriginCity, string? Destination, string? DestinationCity,
    DateTime DepartureTime, DateTime ArrivalTime, string? CabinClass, string? Baggage,
    string? TicketNumber, string? PNR,
    decimal NetCost, decimal SalePrice, decimal Commission, decimal Tax, string Status, string? Notes,
    string? RateId = null,
    string WorkflowStatus = "Solicitado",
    string? ConfirmationNumber = null,
    // Cantidad de pasajeros de ESTE segmento. Opcional (nullable) para no romper
    // las llamadas existentes que no lo mandaban: si llega null, queda sin informar.
    int? PassengerCount = null,
    // ADR-018 (§4-bis): nombre del producto que VIO/tipeo el vendedor. La ficha inline lo reenvia en la
    // edicion (round-trip), pero el modal viejo NO lo manda (llega null): por eso el map lo IGNORA y el
    // service lo asigna a mano solo si viene con valor (anti-clobber, ver UpdateFlightAsync). Opcional al final.
    // (ADR-019: aca vivian TicketingDeadline + DeadlinesSpecified; murieron con el aviso automatico.)
    string? ProductName = null
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
    // (ADR-019: aca vivia OperatorPaymentDeadline; la fecha limite manual murio con el aviso automatico.)
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
    // (ADR-019: aca vivian OperatorPaymentDeadline + DeadlinesSpecified; murieron con el aviso automatico.)
    // Direccion del hotel (campo "Mas detalles" del form). Opcional con default null para no romper los
    // callers posicionales. AutoMapper la mapea por nombre a HotelBooking.Address, igual que los demas
    // strings del hotel (Country/Notes): el modal la reenvia en cada edicion, no usa discriminador.
    string? Address = null
);

public record CreateTransferRequest(
    // ADR-018: PickupLocation/DropoffLocation pasan a opcionales (string?). La ficha "producto-primero"
    // identifica el traslado con ProductName (un solo texto) y puede omitirlos; el modal viejo los manda.
    // ADR-018 Ronda 7 (guia UX, 2026-06-06): VehicleType tambien pasa a opcional. "Tipo de vehiculo"
    // deja de ser exigido; null/vacio se persiste como null y YA NO se coalesce a "Sedan" (supersede
    // el default de negocio de ADR-018 §2).
    string SupplierId, string? PickupLocation, string? DropoffLocation,
    DateTime PickupDateTime, string? FlightNumber, string? VehicleType, int Passengers,
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
    NewCatalogProductRequest? NewCatalogProduct = null,
    // Ficha F2: sentido del traslado ("in" = llegada / "out" = salida). Metadato operativo opcional,
    // no afecta costos. Opcional al final para no romper callers posicionales. Ver TransferBooking.Direction.
    string? Direction = null,
    // Ficha F2: modalidad ("private" = privado / "shared" = compartido). Ver TransferBooking.ServiceMode.
    string? ServiceMode = null,
    // ADR-018 (§4-bis): nombre del producto que VIO/tipeo el vendedor. Fuente UNICA de
    // TransferBooking.ProductName (se copia al crear, no se re-deriva del Rate). Opcional al final.
    string? ProductName = null
);

public record UpdateTransferRequest(
    // ADR-018: estructurados opcionales. Ronda 7 (2026-06-06): VehicleType opcional, sin default de
    // negocio — null = no informado (ver CreateTransferRequest).
    string SupplierId, string? PickupLocation, string? DropoffLocation,
    DateTime PickupDateTime, string? FlightNumber, string? VehicleType, int Passengers,
    bool IsRoundTrip, DateTime? ReturnDateTime,
    string? ConfirmationNumber,
    decimal NetCost, decimal SalePrice, decimal Commission, string Status, string? Notes,
    string? RateId = null,
    string WorkflowStatus = "Solicitado",
    // Impuesto INCLUIDO en el costo (no suma al precio del cliente). Opcional con default 0 para
    // no romper los callers posicionales existentes. Ver TransferBooking.Tax.
    decimal Tax = 0,
    // Ficha F2: sentido del traslado ("in"/"out"). El front siempre lo reenvia en la edicion (round-trip),
    // por eso se mapea por convencion (sin Ignore): null pisa, igual que Notes. Ver TransferBooking.Direction.
    string? Direction = null,
    // Ficha F2: modalidad ("private"/"shared"). Ver TransferBooking.ServiceMode.
    string? ServiceMode = null,
    // ADR-018 (§4-bis): nombre del producto que VIO/tipeo el vendedor. La ficha inline lo reenvia en la
    // edicion (round-trip), pero el modal viejo NO lo manda (llega null): por eso el map lo IGNORA y el
    // service lo asigna a mano solo si viene con valor (anti-clobber, ver UpdateTransferAsync). Opcional al final.
    string? ProductName = null
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
    // ADR-018: Destination pasa a opcional (string?) y EndDate a DateTime?. La ficha "producto-primero"
    // identifica el paquete con PackageName (sigue obligatorio) y puede omitir destino y fecha de fin.
    // EndDate sigue siendo posicional: un caller que pasa un DateTime convierte implicito a DateTime?.
    string SupplierId, string PackageName, string? Destination,
    DateTime StartDate, DateTime? EndDate,
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
    // (ADR-019: aca vivia OperatorPaymentDeadline; la fecha limite manual murio con el aviso automatico.)
    // Ficha F2: base de ocupacion ("double"/"triple"/etc). Metadato operativo opcional, no afecta costos.
    // Opcional al final para no romper callers posicionales. Ver PackageBooking.OccupancyBase.
    string? OccupancyBase = null
);

public record UpdatePackageRequest(
    // ADR-018: Destination opcional (string?) y EndDate a DateTime? (ver CreatePackageRequest).
    string SupplierId, string PackageName, string? Destination,
    DateTime StartDate, DateTime? EndDate,
    bool IncludesHotel, bool IncludesFlight, bool IncludesTransfer, bool IncludesExcursions, bool IncludesMeals,
    int Adults, int Children, string? Itinerary, string? ConfirmationNumber,
    decimal NetCost, decimal SalePrice, decimal Commission, string Status, string? Notes,
    string? RateId = null,
    string WorkflowStatus = "Solicitado",
    // Impuesto INCLUIDO en el costo (no suma al precio del cliente). Opcional con default 0 para
    // no romper los callers posicionales existentes. Ver PackageBooking.Tax.
    decimal Tax = 0,
    // (ADR-019: aca vivian OperatorPaymentDeadline + DeadlinesSpecified; murieron con el aviso automatico.)
    // Ficha F2: base de ocupacion ("double"/"triple"/etc). El front siempre lo reenvia en la edicion
    // (round-trip), por eso se mapea por convencion (sin Ignore): null pisa, igual que Notes.
    // Ver PackageBooking.OccupancyBase.
    string? OccupancyBase = null
);
