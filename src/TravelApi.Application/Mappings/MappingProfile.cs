using AutoMapper;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.Mappings;

public class MappingProfile : Profile
{
    public MappingProfile()
    {
        // Suppliers
        CreateMap<Supplier, SupplierDto>();

        // Passengers
        CreateMap<Passenger, PassengerDto>();
        
        // Financials
        CreateMap<Payment, PaymentDto>()
            .ForMember(dest => dest.PublicId, opt => opt.MapFrom(src => src.PublicId))
            .ForMember(dest => dest.ReservaPublicId, opt => opt.MapFrom(src => src.Reserva != null ? src.Reserva.PublicId : (Guid?)null))
            .ForMember(dest => dest.NumeroReserva, opt => opt.MapFrom(src => src.Reserva != null ? src.Reserva.NumeroReserva : null))
            .ForMember(dest => dest.RelatedInvoicePublicId, opt => opt.MapFrom(src => src.RelatedInvoice != null ? src.RelatedInvoice.PublicId : (Guid?)null))
            .ForMember(dest => dest.OriginalPaymentPublicId, opt => opt.MapFrom(src => src.OriginalPayment != null ? src.OriginalPayment.PublicId : (Guid?)null))
            // ADR-021 Capa 7: moneda/cruce. Currency se normaliza (vacio/legacy -> ARS). ExchangeRateSource
            // es enum en la entidad y se expone como int? (el front lo manda/lee como int). El resto
            // (ImputedCurrency/ExchangeRate/ExchangeRateAt/ImputedAmount) matchea por nombre.
            .ForMember(dest => dest.Currency, opt => opt.MapFrom(src => TravelApi.Domain.Entities.Monedas.Normalizar(src.Currency)))
            .ForMember(dest => dest.ExchangeRateSource, opt => opt.MapFrom(src => src.ExchangeRateSource != null ? (int?)src.ExchangeRateSource : null))
            .ForMember(dest => dest.Receipt, opt => opt.MapFrom(src => src.Receipt));

        CreateMap<PaymentReceipt, PaymentReceiptDto>()
            .ForMember(dest => dest.PublicId, opt => opt.MapFrom(src => src.PublicId))
            .ForMember(dest => dest.PaymentPublicId, opt => opt.MapFrom(src => src.Payment.PublicId))
            .ForMember(dest => dest.ReservaPublicId, opt => opt.MapFrom(src => src.Reserva.PublicId));
            
        CreateMap<Invoice, InvoiceDto>()
            .ForMember(dest => dest.PublicId, opt => opt.MapFrom(src => src.PublicId))
            .ForMember(dest => dest.ReservaPublicId, opt => opt.MapFrom(src => src.Reserva != null ? (Guid?)src.Reserva.PublicId : null))
            .ForMember(dest => dest.Reserva, opt => opt.MapFrom(src => src.Reserva))
            .ForMember(dest => dest.InvoiceType, opt => opt.MapFrom(src =>
                src.TipoComprobante == 1 || src.TipoComprobante == 2 || src.TipoComprobante == 3 ? "A" :
                src.TipoComprobante == 6 || src.TipoComprobante == 7 || src.TipoComprobante == 8 ? "B" :
                src.TipoComprobante == 11 || src.TipoComprobante == 12 || src.TipoComprobante == 13 ? "C" :
                src.TipoComprobante == 51 ? "M" :
                "UNK"))
            // B1.15 (2026-05-11): expose anulación al frontend para que distinga
            // Factura (None/Pending/Succeeded/Failed) y muestre relación factura↔NC.
            .ForMember(dest => dest.AnnulmentStatus, opt => opt.MapFrom(src => src.AnnulmentStatus.ToString()))
            .ForMember(dest => dest.OriginalInvoicePublicId, opt => opt.MapFrom(src => src.OriginalInvoice != null ? (Guid?)src.OriginalInvoice.PublicId : null))
            .ForMember(dest => dest.OriginalInvoiceNumeroComprobante, opt => opt.MapFrom(src => src.OriginalInvoice != null ? (long?)src.OriginalInvoice.NumeroComprobante : null))
            .ForMember(dest => dest.OriginalInvoiceTipoComprobante, opt => opt.MapFrom(src => src.OriginalInvoice != null ? (int?)src.OriginalInvoice.TipoComprobante : null))
            .ForMember(dest => dest.OriginalInvoicePuntoDeVenta, opt => opt.MapFrom(src => src.OriginalInvoice != null ? (int?)src.OriginalInvoice.PuntoDeVenta : null));

        // === BOOKING SERVICES (con RatePublicId) ===

        CreateMap<FlightSegment, FlightSegmentDto>()
            .ForMember(dest => dest.PublicId, opt => opt.MapFrom(src => src.PublicId))
            .ForMember(dest => dest.SupplierPublicId, opt => opt.MapFrom(src => src.Supplier != null ? src.Supplier.PublicId : Guid.Empty))
            .ForMember(dest => dest.SupplierName, opt => opt.MapFrom(src => src.Supplier != null ? src.Supplier.Name : string.Empty))
            .ForMember(dest => dest.RatePublicId, opt => opt.MapFrom(src => src.Rate != null ? (Guid?)src.Rate.PublicId : null))
            .ForMember(dest => dest.WorkflowStatus, opt => opt.MapFrom(src => TravelApi.Domain.Entities.WorkflowStatusHelper.MapFlightStatus(src.Status)))
            // ADR-017 (pill "creado en esta venta"): derivado de la nav Rate. SOLO es correcto cuando la nav
            // viene cargada (los listados con ProjectTo la joinean solos en SQL). En los paths de entidad
            // suelta (byId/create/update/status, cargados con FindAsync SIN nav) el BookingService lo
            // re-resuelve con ResolveProductCreatedInSaleAsync. CostToConfirm mapea por convencion (mismo nombre).
            .ForMember(dest => dest.ProductCreatedInSale, opt => opt.MapFrom(src => src.Rate != null && src.Rate.CreatedInSale))
            .ForMember(dest => dest.IsPriceSynced, opt => opt.MapFrom(src => src.Rate == null || (src.Rate.SalePrice == src.SalePrice && src.Rate.NetCost == src.NetCost)));
            
        CreateMap<CreateFlightRequest, FlightSegment>()
            .ForMember(dest => dest.SupplierId, opt => opt.Ignore())
            .ForMember(dest => dest.RateId, opt => opt.Ignore())
            // ADR-017 F1.3: Currency NO se mapea por convencion. Con flag OFF la asigna el snapshot
            // del tarifario (byte-identico a hoy); con flag ON la asigna el service desde el request
            // (regla "request manda"). Sin este Ignore, el campo nuevo del request cambiaria el OFF.
            .ForMember(dest => dest.Currency, opt => opt.Ignore())
            .ForMember(dest => dest.Status, opt => opt.MapFrom(src => src.WorkflowStatus == "Confirmado" ? "HK" : src.WorkflowStatus == "Cancelado" ? "UN" : "HL"));
            
        // Fuga 3 (ADR-017 §2.7, F1b): NetCost/Tax/Commission NO se mapean automaticamente
        // en los UPDATE. Un caller sin cobranzas.see_cost recibe el costo enmascarado a 0
        // en el GET, el form se puebla con ese 0 y el submit lo mandaba de vuelta ->
        // _mapper.Map destruia el costo real persistido. La asignacion ahora es manual
        // en BookingService, condicionada por el permiso del caller (ver ApplyUpdateCostFields).
        // Los CREATE no se tocan: ahi no hay valor persistido que destruir.
        CreateMap<UpdateFlightRequest, FlightSegment>()
            .ForMember(dest => dest.SupplierId, opt => opt.Ignore())
            .ForMember(dest => dest.RateId, opt => opt.Ignore())
            .ForMember(dest => dest.NetCost, opt => opt.Ignore())
            .ForMember(dest => dest.Tax, opt => opt.Ignore())
            .ForMember(dest => dest.Commission, opt => opt.Ignore())
            // ADR-018 (anti-clobber): ProductName NO se mapea por convencion en el UPDATE. El modal viejo
            // edita estos vuelos pero NO manda ProductName (llega null) -> el map lo borraria y la identidad
            // del servicio revertiria a "Vuelo "/ruta. La ficha inline (producto-primero) SIEMPRE reenvia el
            // texto, asi que el service lo asigna a mano solo cuando viene con valor (ver UpdateFlightAsync).
            .ForMember(dest => dest.ProductName, opt => opt.Ignore())
            // Auditoria ERP item 5 (anti-clobber): los deadlines NO se mapean por convencion en el UPDATE.
            // Un modal viejo que no los manda llega null y borraria una fecha cargada por el operador; el
            // service los asigna a mano solo cuando vienen con valor (ver UpdateFlightAsync).
            .ForMember(dest => dest.TicketingDeadline, opt => opt.Ignore())
            .ForMember(dest => dest.OperatorPaymentDeadline, opt => opt.Ignore())
            // Tanda 6 (anti-clobber de estado, 2026-07-05): SOLO pisamos el estado si el request trae
            // WorkflowStatus con valor. Si viene null/vacio, PreCondition corta y NO se toca dest.Status: se
            // CONSERVA el estado actual del vuelo. Esto evita que editar un vuelo emitido sin mandar estado lo
            // revierta a "Solicitado" (HL) y desaparezca de la facturacion. Un valor explicito se aplica igual
            // que antes (el guard de downgrade con pagos al proveedor sigue vigente en BookingService).
            .ForMember(dest => dest.Status, opt =>
            {
                opt.PreCondition(src => !string.IsNullOrWhiteSpace(src.WorkflowStatus));
                opt.MapFrom(src => src.WorkflowStatus == "Confirmado" ? "HK" : src.WorkflowStatus == "Cancelado" ? "UN" : "HL");
            });

        CreateMap<HotelBooking, HotelBookingDto>()
            .ForMember(dest => dest.PublicId, opt => opt.MapFrom(src => src.PublicId))
            .ForMember(dest => dest.SupplierPublicId, opt => opt.MapFrom(src => src.Supplier != null ? src.Supplier.PublicId : Guid.Empty))
            .ForMember(dest => dest.SupplierName, opt => opt.MapFrom(src => src.Supplier != null ? src.Supplier.Name : string.Empty))
            .ForMember(dest => dest.RatePublicId, opt => opt.MapFrom(src => src.Rate != null ? (Guid?)src.Rate.PublicId : null))
            .ForMember(dest => dest.WorkflowStatus, opt => opt.MapFrom(src => TravelApi.Domain.Entities.WorkflowStatusHelper.MapGenericStatus(src.Status)))
            .ForMember(dest => dest.SnapshotSource, opt => opt.MapFrom(src => src.RateId.HasValue ? "TariffAtBookingTime" : "Manual"))
            // ADR-017 (pill "creado en esta venta"): ver nota en el map de FlightSegment de arriba.
            .ForMember(dest => dest.ProductCreatedInSale, opt => opt.MapFrom(src => src.Rate != null && src.Rate.CreatedInSale))
            .ForMember(dest => dest.RoomingAssignments, opt => opt.MapFrom(src => src.RoomingAssignmentsJson));


        CreateMap<CreateHotelRequest, HotelBooking>()
            .ForMember(dest => dest.SupplierId, opt => opt.Ignore())
            .ForMember(dest => dest.RateId, opt => opt.Ignore())
            // ADR-017 F1.3: Currency la asigna el service (snapshot con OFF / request con ON). Ver Flight.
            .ForMember(dest => dest.Currency, opt => opt.Ignore())
            .ForMember(dest => dest.Status, opt => opt.MapFrom(src => src.WorkflowStatus))
            .ForMember(dest => dest.Nights, opt => opt.MapFrom(src => (src.CheckOut - src.CheckIn).Days))
            .ForMember(dest => dest.RoomingAssignmentsJson, opt => opt.MapFrom(src => src.RoomingAssignments));

        CreateMap<UpdateHotelRequest, HotelBooking>()
            .ForMember(dest => dest.SupplierId, opt => opt.Ignore())
            .ForMember(dest => dest.RateId, opt => opt.Ignore())
            // Fuga 3 (F1b): costos asignados a mano segun permiso (ver UpdateFlightRequest arriba).
            .ForMember(dest => dest.NetCost, opt => opt.Ignore())
            .ForMember(dest => dest.Tax, opt => opt.Ignore())
            .ForMember(dest => dest.Commission, opt => opt.Ignore())
            // Auditoria ERP item 5 (anti-clobber): el deadline NO se mapea por convencion en el UPDATE
            // (ver Flight). El service lo asigna a mano solo cuando viene con valor (ver UpdateHotelAsync).
            .ForMember(dest => dest.OperatorPaymentDeadline, opt => opt.Ignore())
            // Tanda 6 (anti-clobber de estado): si el request no trae WorkflowStatus, se CONSERVA el estado
            // actual del hotel (ver map de UpdateFlightRequest arriba). Un valor explicito se aplica como antes.
            .ForMember(dest => dest.Status, opt =>
            {
                opt.PreCondition(src => !string.IsNullOrWhiteSpace(src.WorkflowStatus));
                opt.MapFrom(src => src.WorkflowStatus);
            })
            .ForMember(dest => dest.Nights, opt => opt.MapFrom(src => (src.CheckOut - src.CheckIn).Days))
            .ForMember(dest => dest.RoomingAssignmentsJson, opt => opt.MapFrom(src => src.RoomingAssignments));

        CreateMap<TransferBooking, TransferBookingDto>()
            .ForMember(dest => dest.PublicId, opt => opt.MapFrom(src => src.PublicId))
            .ForMember(dest => dest.SupplierPublicId, opt => opt.MapFrom(src => src.Supplier != null ? src.Supplier.PublicId : Guid.Empty))
            .ForMember(dest => dest.SupplierName, opt => opt.MapFrom(src => src.Supplier != null ? src.Supplier.Name : string.Empty))
            .ForMember(dest => dest.RatePublicId, opt => opt.MapFrom(src => src.Rate != null ? (Guid?)src.Rate.PublicId : null))
            .ForMember(dest => dest.WorkflowStatus, opt => opt.MapFrom(src => TravelApi.Domain.Entities.WorkflowStatusHelper.MapGenericStatus(src.Status)))
            // ADR-017 (pill "creado en esta venta"): ver nota en el map de FlightSegment de arriba.
            .ForMember(dest => dest.ProductCreatedInSale, opt => opt.MapFrom(src => src.Rate != null && src.Rate.CreatedInSale))
            .ForMember(dest => dest.IsPriceSynced, opt => opt.MapFrom(src => src.Rate == null || (src.Rate.SalePrice == src.SalePrice && src.Rate.NetCost == src.NetCost)));

        CreateMap<CreateTransferRequest, TransferBooking>()
            .ForMember(dest => dest.SupplierId, opt => opt.Ignore())
            .ForMember(dest => dest.Status, opt => opt.MapFrom(src => src.WorkflowStatus))
            .ForMember(dest => dest.RateId, opt => opt.Ignore())
            // ADR-017 F1.3: Currency la asigna el service (snapshot con OFF / request con ON). Ver Flight.
            .ForMember(dest => dest.Currency, opt => opt.Ignore());
            
        CreateMap<UpdateTransferRequest, TransferBooking>()
            .ForMember(dest => dest.SupplierId, opt => opt.Ignore())
            // Tanda 6 (anti-clobber de estado): si el request no trae WorkflowStatus, se CONSERVA el estado
            // actual del traslado (ver map de UpdateFlightRequest). Un valor explicito se aplica como antes.
            .ForMember(dest => dest.Status, opt =>
            {
                opt.PreCondition(src => !string.IsNullOrWhiteSpace(src.WorkflowStatus));
                opt.MapFrom(src => src.WorkflowStatus);
            })
            .ForMember(dest => dest.RateId, opt => opt.Ignore())
            // Fuga 3 (F1b): costos asignados a mano segun permiso (ver UpdateFlightRequest arriba).
            .ForMember(dest => dest.NetCost, opt => opt.Ignore())
            .ForMember(dest => dest.Tax, opt => opt.Ignore())
            .ForMember(dest => dest.Commission, opt => opt.Ignore())
            // ADR-018 (anti-clobber): ProductName NO se mapea por convencion en el UPDATE (ver UpdateFlightRequest
            // arriba). El modal viejo edita estos traslados sin mandar ProductName -> null borraria la identidad.
            // El service lo asigna a mano solo cuando viene con valor (ver UpdateTransferAsync).
            .ForMember(dest => dest.ProductName, opt => opt.Ignore())
            // Auditoria ERP item 5 (anti-clobber): deadline asignado a mano solo si viene con valor.
            .ForMember(dest => dest.OperatorPaymentDeadline, opt => opt.Ignore());

        CreateMap<PackageBooking, PackageBookingDto>()
            .ForMember(dest => dest.PublicId, opt => opt.MapFrom(src => src.PublicId))
            .ForMember(dest => dest.SupplierPublicId, opt => opt.MapFrom(src => src.Supplier != null ? src.Supplier.PublicId : Guid.Empty))
            .ForMember(dest => dest.SupplierName, opt => opt.MapFrom(src => src.Supplier != null ? src.Supplier.Name : string.Empty))
            .ForMember(dest => dest.RatePublicId, opt => opt.MapFrom(src => src.Rate != null ? (Guid?)src.Rate.PublicId : null))
            .ForMember(dest => dest.WorkflowStatus, opt => opt.MapFrom(src => TravelApi.Domain.Entities.WorkflowStatusHelper.MapGenericStatus(src.Status)))
            // ADR-017 (pill "creado en esta venta"): ver nota en el map de FlightSegment de arriba.
            .ForMember(dest => dest.ProductCreatedInSale, opt => opt.MapFrom(src => src.Rate != null && src.Rate.CreatedInSale))
            .ForMember(dest => dest.IsPriceSynced, opt => opt.MapFrom(src => src.Rate == null || (src.Rate.SalePrice == src.SalePrice && src.Rate.NetCost == src.NetCost)));
            
        CreateMap<CreatePackageRequest, PackageBooking>()
            .ForMember(dest => dest.SupplierId, opt => opt.Ignore())
            .ForMember(dest => dest.RateId, opt => opt.Ignore())
            // ADR-017 F1.3: Currency la asigna el service (snapshot con OFF / request con ON). Ver Flight.
            .ForMember(dest => dest.Currency, opt => opt.Ignore())
            .ForMember(dest => dest.Status, opt => opt.MapFrom(src => src.WorkflowStatus))
            // ADR-018: EndDate puede venir null (ficha "producto-primero"). Se coalesce a StartDate
            // para que Nights quede en 0 sin inventar fecha (NO se persiste una fecha de fin falsa).
            .ForMember(dest => dest.Nights, opt => opt.MapFrom(src => ((src.EndDate ?? src.StartDate) - src.StartDate).Days));

        CreateMap<UpdatePackageRequest, PackageBooking>()
            .ForMember(dest => dest.SupplierId, opt => opt.Ignore())
            .ForMember(dest => dest.RateId, opt => opt.Ignore())
            // Fuga 3 (F1b): costos asignados a mano segun permiso (ver UpdateFlightRequest arriba).
            .ForMember(dest => dest.NetCost, opt => opt.Ignore())
            .ForMember(dest => dest.Tax, opt => opt.Ignore())
            .ForMember(dest => dest.Commission, opt => opt.Ignore())
            // Tanda 6 (anti-clobber de estado): si el request no trae WorkflowStatus, se CONSERVA el estado
            // actual del paquete (ver map de UpdateFlightRequest). Un valor explicito se aplica como antes.
            .ForMember(dest => dest.Status, opt =>
            {
                opt.PreCondition(src => !string.IsNullOrWhiteSpace(src.WorkflowStatus));
                opt.MapFrom(src => src.WorkflowStatus);
            })
            // Auditoria ERP item 5 (anti-clobber): deadline asignado a mano solo si viene con valor.
            .ForMember(dest => dest.OperatorPaymentDeadline, opt => opt.Ignore())
            // ADR-018: EndDate puede venir null (ficha "producto-primero"). Se coalesce a StartDate
            // para que Nights quede en 0 sin inventar fecha (NO se persiste una fecha de fin falsa).
            .ForMember(dest => dest.Nights, opt => opt.MapFrom(src => ((src.EndDate ?? src.StartDate) - src.StartDate).Days));

        // AssistanceBooking (Bloque 3). Espejo de HotelBooking: el DTO NO expone Commission
        // (queda solo en la entidad). WorkflowStatus se deriva del Status crudo igual que Hotel.
        CreateMap<AssistanceBooking, AssistanceBookingDto>()
            .ForMember(dest => dest.PublicId, opt => opt.MapFrom(src => src.PublicId))
            .ForMember(dest => dest.SupplierPublicId, opt => opt.MapFrom(src => src.Supplier != null ? src.Supplier.PublicId : Guid.Empty))
            .ForMember(dest => dest.SupplierName, opt => opt.MapFrom(src => src.Supplier != null ? src.Supplier.Name : string.Empty))
            .ForMember(dest => dest.RatePublicId, opt => opt.MapFrom(src => src.Rate != null ? (Guid?)src.Rate.PublicId : null))
            .ForMember(dest => dest.WorkflowStatus, opt => opt.MapFrom(src => TravelApi.Domain.Entities.WorkflowStatusHelper.MapGenericStatus(src.Status)))
            // ADR-017 (pill "creado en esta venta"): ver nota en el map de FlightSegment de arriba.
            .ForMember(dest => dest.ProductCreatedInSale, opt => opt.MapFrom(src => src.Rate != null && src.Rate.CreatedInSale))
            .ForMember(dest => dest.SnapshotSource, opt => opt.MapFrom(src => src.RateId.HasValue ? "TariffAtBookingTime" : "Manual"));

        CreateMap<CreateAssistanceRequest, AssistanceBooking>()
            .ForMember(dest => dest.SupplierId, opt => opt.Ignore())
            .ForMember(dest => dest.RateId, opt => opt.Ignore())
            // ADR-017 F1.3: Currency la asigna el service (snapshot con OFF / request con ON). Ver Flight.
            .ForMember(dest => dest.Currency, opt => opt.Ignore())
            .ForMember(dest => dest.Status, opt => opt.MapFrom(src => src.WorkflowStatus));

        CreateMap<UpdateAssistanceRequest, AssistanceBooking>()
            .ForMember(dest => dest.SupplierId, opt => opt.Ignore())
            .ForMember(dest => dest.RateId, opt => opt.Ignore())
            // Fuga 3 (F1b): costos asignados a mano segun permiso (ver UpdateFlightRequest arriba).
            .ForMember(dest => dest.NetCost, opt => opt.Ignore())
            .ForMember(dest => dest.Tax, opt => opt.Ignore())
            .ForMember(dest => dest.Commission, opt => opt.Ignore())
            // Auditoria ERP item 5 (anti-clobber): deadline asignado a mano solo si viene con valor.
            .ForMember(dest => dest.OperatorPaymentDeadline, opt => opt.Ignore())
            // Tanda 6 (anti-clobber de estado): si el request no trae WorkflowStatus, se CONSERVA el estado
            // actual de la asistencia (ver map de UpdateFlightRequest). Un valor explicito se aplica como antes.
            .ForMember(dest => dest.Status, opt =>
            {
                opt.PreCondition(src => !string.IsNullOrWhiteSpace(src.WorkflowStatus));
                opt.MapFrom(src => src.WorkflowStatus);
            });

        // Customers
        CreateMap<Customer, CustomerDto>();

        // Servicios
        CreateMap<ServicioReserva, ServicioReservaDto>()
            .ForMember(dest => dest.PublicId, opt => opt.MapFrom(src => src.PublicId))
            .ForMember(dest => dest.SupplierPublicId, opt => opt.MapFrom(src => src.Supplier != null ? (Guid?)src.Supplier.PublicId : null))
            .ForMember(dest => dest.RatePublicId, opt => opt.MapFrom(src => src.Rate != null ? (Guid?)src.Rate.PublicId : null))
            .ForMember(dest => dest.ReservaPublicId, opt => opt.MapFrom(src => src.Reserva != null ? (Guid?)src.Reserva.PublicId : null))
            .ForMember(dest => dest.WorkflowStatus, opt => opt.MapFrom(src => TravelApi.Domain.Entities.WorkflowStatusHelper.MapGenericStatus(src.Status)))
            // ADR-021 Capa 7: la moneda del servicio se normaliza (null/legacy -> ARS) para que el front
            // siempre reciba un codigo valido y nunca un null en el badge.
            .ForMember(dest => dest.Currency, opt => opt.MapFrom(src => TravelApi.Domain.Entities.Monedas.Normalizar(src.Currency)))
            .ForMember(dest => dest.SupplierName, opt => opt.MapFrom(src => src.Supplier != null ? src.Supplier.Name : src.SupplierName));

        // Reserva
        CreateMap<Reserva, ReservaDto>()
            .ForMember(dest => dest.PublicId, opt => opt.MapFrom(src => src.PublicId))
            .ForMember(dest => dest.CustomerPublicId, opt => opt.MapFrom(src => src.Payer != null ? (Guid?)src.Payer.PublicId : null))
            .ForMember(dest => dest.SourceLeadPublicId, opt => opt.MapFrom(src => src.SourceLead != null ? (Guid?)src.SourceLead.PublicId : null))
            .ForMember(dest => dest.SourceQuotePublicId, opt => opt.MapFrom(src => src.SourceQuote != null ? (Guid?)src.SourceQuote.PublicId : null))
            .ForMember(dest => dest.ResponsibleUserId, opt => opt.MapFrom(src => src.ResponsibleUserId))
            .ForMember(dest => dest.ResponsibleUserName, opt => opt.MapFrom(src => src.ResponsibleUserName))
            .ForMember(dest => dest.WhatsAppPhoneOverride, opt => opt.MapFrom(src => src.WhatsAppPhoneOverride))
            .ForMember(dest => dest.CustomerName, opt => opt.MapFrom(src => src.Payer != null ? src.Payer.FullName : string.Empty))
            .ForMember(dest => dest.Payer, opt => opt.MapFrom(src => src.Payer))
            .ForMember(dest => dest.Servicios, opt => opt.MapFrom(src => src.Servicios))
            .ForMember(dest => dest.HotelBookings, opt => opt.MapFrom(src => src.HotelBookings))
            .ForMember(dest => dest.FlightSegments, opt => opt.MapFrom(src => src.FlightSegments))
            .ForMember(dest => dest.TransferBookings, opt => opt.MapFrom(src => src.TransferBookings))
            .ForMember(dest => dest.PackageBookings, opt => opt.MapFrom(src => src.PackageBookings))
            .ForMember(dest => dest.AssistanceBookings, opt => opt.MapFrom(src => src.AssistanceBookings))
            .ForMember(dest => dest.Invoices, opt => opt.MapFrom(src => src.Invoices))
            // ADR-027 (detalle): PendingChanges se llena a mano en ReservaService (para enmascarar el costo
            // segun permiso). AutoMapper lo ignora; si no, intentaria mapear la entidad sin map definido.
            .ForMember(dest => dest.PendingChanges, opt => opt.Ignore())
            .ForMember(dest => dest.TotalPaid, opt => opt.MapFrom(src => src.TotalPaid));

        CreateMap<Reserva, ReservaListDto>()
            .ForMember(dest => dest.PublicId, opt => opt.MapFrom(src => src.PublicId))
            .ForMember(dest => dest.CustomerName, opt => opt.MapFrom(src => src.Payer != null ? src.Payer.FullName : string.Empty))
            .ForMember(dest => dest.ResponsibleUserId, opt => opt.MapFrom(src => src.ResponsibleUserId))
            .ForMember(dest => dest.ResponsibleUserName, opt => opt.MapFrom(src => src.ResponsibleUserName))
            // CRITICO: la venta de la reserva suma las 5 colecciones tipadas + servicios genericos.
            // Si Asistencia faltara aca, el TotalSale/Balance del listado descuadraria en silencio.
            .ForMember(dest => dest.TotalSale, opt => opt.MapFrom(src =>
                (src.FlightSegments.Sum(f => (decimal?)f.SalePrice) ?? 0) +
                (src.HotelBookings.Sum(h => (decimal?)h.SalePrice) ?? 0) +
                (src.TransferBookings.Sum(t => (decimal?)t.SalePrice) ?? 0) +
                (src.PackageBookings.Sum(p => (decimal?)p.SalePrice) ?? 0) +
                (src.AssistanceBookings.Sum(a => (decimal?)a.SalePrice) ?? 0) +
                (src.Servicios.Sum(r => (decimal?)r.SalePrice) ?? 0)
            ))
            .ForMember(dest => dest.Balance, opt => opt.MapFrom(src =>
                ((src.FlightSegments.Sum(f => (decimal?)f.SalePrice) ?? 0) +
                 (src.HotelBookings.Sum(h => (decimal?)h.SalePrice) ?? 0) +
                 (src.TransferBookings.Sum(t => (decimal?)t.SalePrice) ?? 0) +
                 (src.PackageBookings.Sum(p => (decimal?)p.SalePrice) ?? 0) +
                 (src.AssistanceBookings.Sum(a => (decimal?)a.SalePrice) ?? 0) +
                 (src.Servicios.Sum(r => (decimal?)r.SalePrice) ?? 0))
                -
                (src.Payments.Where(p => p.Status != "Cancelled" &&  !p.IsDeleted).Sum(p => (decimal?)p.Amount) ?? 0)
            ))
            .ForMember(dest => dest.TotalPaid, opt => opt.MapFrom(src => src.TotalPaid));
    }
}
