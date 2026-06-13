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
    /// </summary>
    public static IReadOnlyList<InvoiceSuggestedItemGroup> Build(Reserva reserva)
    {
        ArgumentNullException.ThrowIfNull(reserva);

        // Acumulamos las lineas por moneda canonica (ISO). Diccionario insertion-ordered para que
        // el orden de salida sea estable (primer servicio confirmado de cada moneda define el orden).
        var linesByCurrency = new Dictionary<string, List<InvoiceSuggestedItem>>(StringComparer.Ordinal);

        AddIfConfirmed(reserva.FlightSegments, linesByCurrency,
            flight => ServiceResolutionRules.IsResolved(flight),
            flight => flight.Currency,
            flight => flight.SalePrice,
            DescribeFlight);

        AddIfConfirmed(reserva.HotelBookings, linesByCurrency,
            hotel => ServiceResolutionRules.IsResolved(hotel),
            hotel => hotel.Currency,
            hotel => hotel.SalePrice,
            DescribeHotel);

        AddIfConfirmed(reserva.TransferBookings, linesByCurrency,
            transfer => ServiceResolutionRules.IsResolved(transfer),
            transfer => transfer.Currency,
            transfer => transfer.SalePrice,
            DescribeTransfer);

        AddIfConfirmed(reserva.PackageBookings, linesByCurrency,
            package => ServiceResolutionRules.IsResolved(package),
            package => package.Currency,
            package => package.SalePrice,
            DescribePackage);

        AddIfConfirmed(reserva.AssistanceBookings, linesByCurrency,
            assistance => ServiceResolutionRules.IsResolved(assistance),
            assistance => assistance.Currency,
            assistance => assistance.SalePrice,
            DescribeAssistance);

        AddIfConfirmed(reserva.Servicios, linesByCurrency,
            service => ServiceResolutionRules.IsResolved(service),
            service => service.Currency,
            service => service.SalePrice,
            DescribeGeneric);

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
        return groups;
    }

    /// <summary>
    /// Agrega una linea sugerida por cada servicio CONFIRMADO de la coleccion, a la lista de su moneda.
    /// Cada linea es Quantity=1, UnitPrice=Total=SalePrice (una factura turistica describe el servicio,
    /// no desglosa por noche/pax — eso ya vive en el detalle del servicio). El servicio no confirmado o
    /// cancelado simplemente no entra (el predicado <paramref name="isConfirmed"/> ya excluye cancelados).
    /// </summary>
    private static void AddIfConfirmed<T>(
        IEnumerable<T>? items,
        Dictionary<string, List<InvoiceSuggestedItem>> linesByCurrency,
        Func<T, bool> isConfirmed,
        Func<T, string?> currencyOf,
        Func<T, decimal> salePriceOf,
        Func<T, string> describe)
    {
        if (items == null) return;

        foreach (var item in items)
        {
            if (!isConfirmed(item)) continue;

            string currency = Monedas.Normalizar(currencyOf(item));
            if (!linesByCurrency.TryGetValue(currency, out var lines))
            {
                lines = new List<InvoiceSuggestedItem>();
                linesByCurrency[currency] = lines;
            }

            decimal salePrice = Math.Round(salePriceOf(item), 2);
            lines.Add(new InvoiceSuggestedItem(
                Description: describe(item),
                Quantity: 1m,
                UnitPrice: salePrice,
                Total: salePrice,
                AlicuotaIvaId: DefaultAlicuotaIvaId));
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
/// </summary>
public record InvoiceSuggestedItem(
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal Total,
    int AlicuotaIvaId);
