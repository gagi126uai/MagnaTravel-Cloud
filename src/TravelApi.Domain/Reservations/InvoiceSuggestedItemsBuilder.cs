using System.Globalization;
using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// Hallazgo auditoria ERP #9 (2026-06-13): arma los items SUGERIDOS de una factura a partir de los
/// servicios CONFIRMADOS de una reserva, en vez del unico item generico "Servicios Turisticos" que
/// hoy prerellena el operador a mano. Una linea de factura por cada servicio confirmado, con una
/// descripcion legible y su monto de VENTA.
///
/// <para><b>Que es "confirmado" aca</b>: usamos exactamente el mismo predicado que alimenta
/// <c>ConfirmedSale</c> (<see cref="ServiceResolutionRules.IsResolved"/>): el servicio esta RESUELTO
/// (asegurado para viajar) y NO esta cancelado. Es deliberado: si la sugerencia usara otra definicion,
/// la suma de los items sugeridos NO cuadraria con <c>ConfirmedSale</c> y el aviso de descuadre
/// (<see cref="InvoiceMismatchChecker"/>) saltaria sobre la propia sugerencia, que es absurdo. En el
/// aereo "resuelto" = ticket emitido (no alcanza el PNR confirmado); ver <see cref="ServiceResolutionRules"/>.</para>
///
/// <para><b>Por moneda</b>: una factura ARCA lleva UNA sola moneda. Si la reserva mezcla ARS y USD,
/// devolvemos un grupo de sugerencia por cada moneda (el front arma una factura por moneda, no mezcla).
/// La moneda viaja en codigo ISO ("ARS"/"USD") — es el eje del dominio de reservas; el mapeo a codigo
/// ARCA ("PES"/"DOL") lo hace el boundary de facturacion, no esta clase.</para>
///
/// <para><b>IVA</b>: NO tocamos la logica fiscal. Cada item sugerido sale con <see cref="DefaultAlicuotaIvaId"/>
/// = 3 (0%), que es el mismo default que usa hoy el modal de creacion para emisor Monotributo (factura C).
/// El operador / la capa fiscal puede ajustarlo; aca solo armamos descripciones y montos.</para>
///
/// <para>Clase PURA (sin EF, sin DB): se testea sin Postgres, igual que <see cref="ReservaMoneyCalculator"/>.
/// El llamador es responsable de cargar las 6 colecciones de servicios (Includes en EF); una coleccion
/// null se trata como vacia.</para>
/// </summary>
public static class InvoiceSuggestedItemsBuilder
{
    /// <summary>
    /// Alicuota de IVA por defecto de los items sugeridos: 3 = 0%. Coincide con el default del modal
    /// de facturacion para Monotributo (factura C, sin IVA discriminado). No es una decision fiscal
    /// nueva — replica lo que ya hace el frontend hoy al prerrellenar el item generico.
    /// </summary>
    public const int DefaultAlicuotaIvaId = 3;

    /// <summary>
    /// Recorre los servicios confirmados de la reserva y devuelve un grupo de sugerencia por cada
    /// moneda presente. Cada grupo trae sus lineas (una por servicio) y el total sugerido de esa moneda.
    /// Si no hay servicios confirmados, devuelve lista vacia (el front cae al armado manual de hoy).
    ///
    /// <para>Es un atajo sobre <see cref="BuildWithDiagnostics"/>: devuelve SOLO los grupos, sin el
    /// diagnostico de servicios excluidos. Delegamos para no duplicar la logica de inclusion (asi los
    /// grupos de <c>Build</c> y de <c>BuildWithDiagnostics</c> son, por construccion, identicos).</para>
    /// </summary>
    public static IReadOnlyList<InvoiceSuggestedItemGroup> Build(Reserva reserva)
        => BuildWithDiagnostics(reserva).Groups;

    /// <summary>
    /// Tanda 6 (bug "$0 mudo", 2026-07-05): igual que <see cref="Build"/> pero ADEMAS explica por que
    /// cada servicio que NO aporto a la sugerencia quedo afuera. Nace de un caso real: el dueño editó un
    /// servicio en pesos, el servicio dejo de estar "resuelto" y desaparecio de la sugerencia; el modal
    /// precargaba el grupo vacio y mostraba un renglon en $0 SIN explicar nada. Este diagnostico deja que
    /// el modal diga "este servicio no aparece porque esta sin resolver / cancelado / en $0".
    ///
    /// <para><b>La logica de INCLUSION en los grupos NO cambia</b> respecto de <c>Build</c>: entra en un
    /// grupo exactamente el mismo conjunto de servicios que antes (resueltos y no cancelados). Lo NUEVO es
    /// que, en la misma pasada, juntamos los que quedaron afuera con su motivo. Un servicio resuelto con
    /// venta &lt;= 0 SIGUE entrando al grupo como linea $0 (igual que antes) y ADEMAS se reporta como
    /// <see cref="SuggestedServiceExclusionReasons.ZeroPrice"/>, para que el modal explique ese $0 en vez
    /// de mostrarlo mudo.</para>
    /// </summary>
    public static InvoiceSuggestedItemsResult BuildWithDiagnostics(Reserva reserva)
    {
        ArgumentNullException.ThrowIfNull(reserva);

        // Acumulamos las lineas por moneda canonica (ISO). Diccionario insertion-ordered para que
        // el orden de salida sea estable (primer servicio confirmado de cada moneda define el orden).
        var linesByCurrency = new Dictionary<string, List<InvoiceSuggestedItem>>(StringComparer.Ordinal);

        // Servicios que NO aportaron a la sugerencia (o que aportaron una linea $0), con su motivo.
        var excludedServices = new List<ExcludedSuggestedService>();

        Classify(reserva.FlightSegments, linesByCurrency, excludedServices,
            ServiceResolutionRules.IsResolved, ServiceResolutionRules.IsCancelled,
            flight => flight.Currency, flight => flight.SalePrice, DescribeFlight,
            CancellableServiceTable.Flight, flight => flight.PublicId);

        Classify(reserva.HotelBookings, linesByCurrency, excludedServices,
            ServiceResolutionRules.IsResolved, ServiceResolutionRules.IsCancelled,
            hotel => hotel.Currency, hotel => hotel.SalePrice, DescribeHotel,
            CancellableServiceTable.Hotel, hotel => hotel.PublicId);

        Classify(reserva.TransferBookings, linesByCurrency, excludedServices,
            ServiceResolutionRules.IsResolved, ServiceResolutionRules.IsCancelled,
            transfer => transfer.Currency, transfer => transfer.SalePrice, DescribeTransfer,
            CancellableServiceTable.Transfer, transfer => transfer.PublicId);

        Classify(reserva.PackageBookings, linesByCurrency, excludedServices,
            ServiceResolutionRules.IsResolved, ServiceResolutionRules.IsCancelled,
            package => package.Currency, package => package.SalePrice, DescribePackage,
            CancellableServiceTable.Package, package => package.PublicId);

        Classify(reserva.AssistanceBookings, linesByCurrency, excludedServices,
            ServiceResolutionRules.IsResolved, ServiceResolutionRules.IsCancelled,
            assistance => assistance.Currency, assistance => assistance.SalePrice, DescribeAssistance,
            CancellableServiceTable.Assistance, assistance => assistance.PublicId);

        Classify(reserva.Servicios, linesByCurrency, excludedServices,
            ServiceResolutionRules.IsResolved, ServiceResolutionRules.IsCancelled,
            service => service.Currency, service => service.SalePrice, DescribeGeneric,
            CancellableServiceTable.Generic, service => service.PublicId);

        var groups = new List<InvoiceSuggestedItemGroup>();
        foreach (var (currency, lines) in linesByCurrency)
        {
            decimal total = 0m;
            foreach (var line in lines)
            {
                total += line.Total;
            }
            groups.Add(new InvoiceSuggestedItemGroup(currency, lines, Math.Round(total, 2)));
        }
        return new InvoiceSuggestedItemsResult(groups, excludedServices);
    }

    /// <summary>
    /// Clasifica cada servicio de una coleccion: o entra al grupo de su moneda (una linea sugerida), o
    /// va a la lista de excluidos con su motivo. Reemplaza al viejo <c>AddIfConfirmed</c>: ademas de
    /// agregar los confirmados, explica por que los demas no aparecen.
    ///
    /// <para><b>Orden de evaluacion del motivo</b> (decidido en Tanda 6):
    /// <list type="number">
    /// <item><b>Cancelado</b> gana sobre "no resuelto": un servicio cancelado se reporta como
    ///   <see cref="SuggestedServiceExclusionReasons.Cancelled"/>, que es la causa que el usuario
    ///   reconoce, aunque tecnicamente tambien sea "no resuelto".</item>
    /// <item><b>No resuelto</b>: vivo (no cancelado) pero todavia no asegurado para viajar
    ///   (<see cref="ServiceResolutionRules.IsResolved"/> = false). Es la causa mas comun del "$0 mudo".</item>
    /// <item><b>Precio cero</b>: resuelto y no cancelado pero con venta &lt;= 0. SIGUE entrando al grupo
    ///   como linea $0 (no cambiamos la inclusion) y ADEMAS se marca para que el modal explique el $0.</item>
    /// </list></para>
    ///
    /// <para>Cada linea que entra es Quantity=1, UnitPrice=Total=SalePrice (una factura turistica describe
    /// el servicio, no desglosa por noche/pax — eso ya vive en el detalle del servicio).</para>
    ///
    /// <para><b>Trazabilidad al servicio de origen (2026-07-16)</b>: cada linea que entra al grupo lleva
    /// tambien de que tabla y de que servicio concreto (<c>PublicId</c>) salio. Esto es lo que despues
    /// permite, al crear la factura, grabar en <c>InvoiceItem</c> de que servicio proviene cada renglon
    /// (objetivo: poder decirle al usuario en que factura esta un servicio cuando lo cancela). El caller
    /// pasa la tabla fija de esta coleccion (ej. <c>CancellableServiceTable.Hotel</c> para
    /// <c>reserva.HotelBookings</c>) y el extractor del <c>PublicId</c> de la entidad.</para>
    /// </summary>
    private static void Classify<T>(
        IEnumerable<T>? items,
        Dictionary<string, List<InvoiceSuggestedItem>> linesByCurrency,
        List<ExcludedSuggestedService> excludedServices,
        Func<T, bool> isResolved,
        Func<T, bool> isCancelled,
        Func<T, string?> currencyOf,
        Func<T, decimal> salePriceOf,
        Func<T, string> describe,
        CancellableServiceTable serviceTable,
        Func<T, Guid> publicIdOf)
    {
        if (items == null) return;

        foreach (var item in items)
        {
            string currency = Monedas.Normalizar(currencyOf(item));
            string description = describe(item);

            // 1) Cancelado gana sobre no-resuelto.
            if (isCancelled(item))
            {
                excludedServices.Add(new ExcludedSuggestedService(
                    description, currency, SuggestedServiceExclusionReasons.Cancelled));
                continue;
            }

            // 2) Vivo pero sin resolver: no entra al grupo (misma exclusion que el Build historico).
            if (!isResolved(item))
            {
                excludedServices.Add(new ExcludedSuggestedService(
                    description, currency, SuggestedServiceExclusionReasons.NotResolved));
                continue;
            }

            // 3) Resuelto y no cancelado: ENTRA al grupo, igual que siempre. Si quedo en venta <= 0, lo
            // marcamos PrecioCero (la linea $0 igual entra: no cambiamos la logica de inclusion).
            decimal salePrice = Math.Round(salePriceOf(item), 2);
            if (salePrice <= 0m)
            {
                excludedServices.Add(new ExcludedSuggestedService(
                    description, currency, SuggestedServiceExclusionReasons.ZeroPrice));
            }

            if (!linesByCurrency.TryGetValue(currency, out var lines))
            {
                lines = new List<InvoiceSuggestedItem>();
                linesByCurrency[currency] = lines;
            }

            lines.Add(new InvoiceSuggestedItem(
                Description: description,
                Quantity: 1m,
                UnitPrice: salePrice,
                Total: salePrice,
                AlicuotaIvaId: DefaultAlicuotaIvaId,
                SourceServiceTable: serviceTable,
                SourceServicePublicId: publicIdOf(item)));
        }
    }

    // ===================== Descripciones legibles por tipo de servicio =====================
    // Cada descripcion arma un texto corto que el operador reconoce en la factura. Si los campos
    // ricos estan vacios (dato viejo o incompleto), caemos a una etiqueta generica del tipo para
    // no emitir una linea con descripcion vacia (ARCA exige descripcion no vacia por item).

    private static string DescribeFlight(FlightSegment flight)
    {
        // Preferimos el nombre de producto cargado; si no, armamos "Aereo MIA-EZE" con los IATA.
        if (!string.IsNullOrWhiteSpace(flight.ProductName))
            return flight.ProductName!.Trim();

        string origin = FirstNonEmpty(flight.OriginCity, flight.Origin);
        string destination = FirstNonEmpty(flight.DestinationCity, flight.Destination);
        if (origin.Length > 0 && destination.Length > 0)
            return $"Aereo {origin} - {destination}";

        return "Aereo";
    }

    private static string DescribeHotel(HotelBooking hotel)
    {
        string name = FirstNonEmpty(hotel.HotelName, hotel.City);
        if (name.Length == 0)
            return "Alojamiento";

        // "Hotel Sheraton (3 noches)" — las noches ayudan al operador a reconocer la linea.
        if (hotel.Nights > 0)
            return $"Hotel {name} ({hotel.Nights} {(hotel.Nights == 1 ? "noche" : "noches")})";

        return $"Hotel {name}";
    }

    private static string DescribeTransfer(TransferBooking transfer)
    {
        if (!string.IsNullOrWhiteSpace(transfer.ProductName))
            return transfer.ProductName!.Trim();

        string pickup = FirstNonEmpty(transfer.PickupLocation);
        string dropoff = FirstNonEmpty(transfer.DropoffLocation);
        if (pickup.Length > 0 && dropoff.Length > 0)
            return $"Traslado {pickup} - {dropoff}";

        return "Traslado";
    }

    private static string DescribePackage(PackageBooking package)
    {
        string name = FirstNonEmpty(package.PackageName);
        if (name.Length == 0)
            return "Paquete turistico";

        string destination = FirstNonEmpty(package.Destination);
        if (destination.Length > 0)
            return $"Paquete {name} - {destination}";

        return $"Paquete {name}";
    }

    private static string DescribeAssistance(AssistanceBooking assistance)
    {
        string plan = FirstNonEmpty(assistance.PlanType);
        if (plan.Length > 0)
            return $"Asistencia al viajero - {plan}";

        return "Asistencia al viajero";
    }

    private static string DescribeGeneric(ServicioReserva service)
    {
        // El servicio generico tiene una Description cargada por el operador; es lo mas fiel.
        if (!string.IsNullOrWhiteSpace(service.Description))
            return service.Description!.Trim();

        // Sin descripcion, caemos al tipo de servicio si lo trae (ej "Flight"); si no, etiqueta neutra.
        if (!string.IsNullOrWhiteSpace(service.ServiceType))
            return service.ServiceType!.Trim();

        return "Servicio turistico";
    }

    /// <summary>Devuelve el primer string no vacio (trim) de los candidatos, o string vacio si todos lo son.</summary>
    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate!.Trim();
        }
        return string.Empty;
    }
}

/// <summary>
/// Hallazgo #9: un grupo de items sugeridos de UNA moneda (todos los servicios confirmados de esa
/// moneda). Una factura se arma desde UN grupo (una factura = una moneda).
/// </summary>
/// <param name="Currency">Moneda ISO del grupo ("ARS"/"USD").</param>
/// <param name="Items">Lineas sugeridas (una por servicio confirmado de esta moneda).</param>
/// <param name="SuggestedTotal">Suma de los <c>Total</c> de las lineas, redondeada a 2 decimales. Igual a <c>ConfirmedSale</c> de la moneda.</param>
public record InvoiceSuggestedItemGroup(
    string Currency,
    IReadOnlyList<InvoiceSuggestedItem> Items,
    decimal SuggestedTotal);

/// <summary>
/// Hallazgo #9: una linea de factura sugerida desde un servicio. Tiene la misma forma que
/// <c>InvoiceItemDto</c> (Description/Quantity/UnitPrice/Total/AlicuotaIvaId) para que el front la
/// vuelque directo al modal sin transformar.
///
/// <para><b>SourceServiceTable/SourceServicePublicId (2026-07-16)</b>: de que servicio concreto salio
/// esta linea. Siempre vienen completos aca (a diferencia de <c>InvoiceItem.SourceServiceTable</c>, que
/// es nullable porque hay items legacy o conceptos sueltos sin servicio de origen): toda linea que arma
/// este builder proviene de una entidad de servicio real con <c>PublicId</c>. El front los reenvia tal
/// cual al crear la factura para que quede la trazabilidad grabada.</para>
/// </summary>
public record InvoiceSuggestedItem(
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal Total,
    int AlicuotaIvaId,
    CancellableServiceTable SourceServiceTable,
    Guid SourceServicePublicId);

/// <summary>
/// Tanda 6 (bug "$0 mudo"): resultado completo de <see cref="InvoiceSuggestedItemsBuilder.BuildWithDiagnostics"/>.
/// Trae los grupos sugeridos (lo mismo que devuelve <see cref="InvoiceSuggestedItemsBuilder.Build"/>) MAS la
/// lista de servicios que quedaron afuera de la sugerencia, con el motivo, para que el modal de factura pueda
/// explicar por que un servicio no aparece (en vez de mostrar un renglon en $0 sin explicacion).
/// </summary>
/// <param name="Groups">Grupos de items sugeridos, uno por moneda. Identico a <see cref="InvoiceSuggestedItemsBuilder.Build"/>.</param>
/// <param name="ExcludedServices">Servicios que no aportaron a la sugerencia (o aportaron una linea $0), con su motivo.</param>
public record InvoiceSuggestedItemsResult(
    IReadOnlyList<InvoiceSuggestedItemGroup> Groups,
    IReadOnlyList<ExcludedSuggestedService> ExcludedServices);

/// <summary>
/// Tanda 6: un servicio que NO aporto una linea "normal" a la sugerencia de factura, con el motivo por el que
/// quedo afuera. Es informativo para el usuario final (no lleva IDs ni datos internos): solo el nombre visible
/// del servicio, su moneda y el motivo. La moneda ayuda al modal a explicar por que un grupo de moneda quedo vacio.
/// </summary>
/// <param name="Description">Nombre del servicio como lo ve el usuario (misma descripcion que tendria la linea).</param>
/// <param name="Currency">Moneda ISO del servicio ("ARS"/"USD"). Sirve para explicar por moneda.</param>
/// <param name="Reason">Motivo de exclusion: uno de <see cref="SuggestedServiceExclusionReasons"/>.</param>
public record ExcludedSuggestedService(
    string Description,
    string Currency,
    string Reason);

/// <summary>
/// Tanda 6: motivos por los que un servicio no aporta una linea "normal" a la sugerencia de factura. Son
/// tokens en castellano (mismo estilo que <c>CancelledMoneyContext</c>): el front los mapea a un texto amable.
/// </summary>
public static class SuggestedServiceExclusionReasons
{
    /// <summary>Vivo (no cancelado) pero todavia no asegurado para viajar (no resuelto). Causa mas comun del "$0 mudo".</summary>
    public const string NotResolved = "NoResuelto";

    /// <summary>El servicio esta cancelado: no se factura. Gana sobre "no resuelto".</summary>
    public const string Cancelled = "Cancelado";

    /// <summary>Resuelto pero con venta &lt;= 0: entra al grupo como linea $0. Se marca para explicar ese $0.</summary>
    public const string ZeroPrice = "PrecioCero";
}
