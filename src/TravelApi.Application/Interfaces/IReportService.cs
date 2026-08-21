using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface IReportService
{
    Task<DashboardResponse> GetDashboardAsync(CancellationToken cancellationToken);
    Task<ReportsSummaryResponse> GetSummaryAsync(CancellationToken cancellationToken);
    Task<object> GetDetailedReportAsync(DateTime? from, DateTime? to, CancellationToken cancellationToken);
    Task<IEnumerable<object>> GetDetailedReceivablesAsync(CancellationToken cancellationToken);
    Task<byte[]> ExportReportAsync(DateTime? from, DateTime? to, bool includeSales, bool includeReceivables, bool includePayables, CancellationToken cancellationToken);

    /// <summary>
    /// "Facturas en dolares" (spec firmada 2026-08-06, Parte B): una fila por factura de venta emitida en
    /// moneda extranjera dentro del periodo, con lo que se facturo en pesos y lo que efectivamente se
    /// cobro. Solo lectura, sin botones de accion: es la planilla que el contador se lleva una vez por mes.
    /// </summary>
    Task<UsdInvoicesReportResponse> GetUsdInvoicesReportAsync(DateTime? from, DateTime? to, CancellationToken cancellationToken);

    /// <summary>El mismo reporte, en Excel, con las MISMAS columnas que la pantalla.</summary>
    Task<byte[]> ExportUsdInvoicesReportAsync(DateTime? from, DateTime? to, CancellationToken cancellationToken);
    Task<AgencySettings?> GetAgencySettingsAsync(CancellationToken cancellationToken);
    Task<AgencySettings> UpdateAgencySettingsAsync(AgencySettings updated, CancellationToken cancellationToken);

    /// <summary>
    /// Fix bloqueante (2026-08-13): SOLO el texto de la plantilla de "Formas de pago" — no la entidad
    /// <see cref="AgencySettings"/> completa (esa vive detrás de <see cref="GetAgencySettingsAsync"/>,
    /// Admin-only). Lo usa la ficha de reserva para precargar el textarea de "Formas de pago" propias de
    /// esa reserva sin exigirle Admin a un vendedor/colaborador que solo necesita ver la reserva.
    /// </summary>
    Task<BudgetPaymentTermsTemplateDto> GetBudgetPaymentTermsTemplateAsync(CancellationToken cancellationToken);

    // ============================================================================================
    // Obra "PDF de presupuesto" (decisión firmada del dueño, 2026-08-11/12), TANDA 1: logo de la
    // agencia (para el encabezado del PDF) y los 6 bloques de condiciones (letra chica del PDF).
    // Mismo controller/autorización que el resto de Configuración de la agencia (Admin only).
    // ============================================================================================

    /// <summary>Sube/reemplaza el logo de la agencia. Si ya había uno cargado, lo borra del almacenamiento.</summary>
    Task<AgencySettings> UpdateAgencyLogoAsync(Stream fileStream, string fileName, string contentType, CancellationToken cancellationToken);

    /// <summary>Borra el logo cargado (si hay). No falla si no hay logo (idempotente).</summary>
    Task RemoveAgencyLogoAsync(CancellationToken cancellationToken);

    /// <summary>Descarga los bytes del logo cargado. Lanza <see cref="KeyNotFoundException"/> si no hay ninguno.</summary>
    Task<(byte[] Bytes, string ContentType, string FileName)> GetAgencyLogoAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Las 6 categorías SIEMPRE, en orden fijo (ver <see cref="BudgetConditionBlockKindText.All"/>), con
    /// texto vacío para las que la agencia todavía no cargó — el front nunca ve "falta la categoría X".
    /// </summary>
    Task<IReadOnlyList<BudgetConditionBlockDto>> GetBudgetConditionBlocksAsync(CancellationToken cancellationToken);

    /// <summary>Upsert del texto de UNA categoría. <paramref name="kindText"/> es uno de <see cref="BudgetConditionBlockKindText.All"/>.</summary>
    Task<BudgetConditionBlockDto> UpdateBudgetConditionBlockAsync(string kindText, string? text, CancellationToken cancellationToken);

    /// <summary>
    /// Mini-tanda PDF-2a (2026-08-12): le pide a la inteligencia artificial un BORRADOR de condiciones
    /// para UNA categoría ("✨ Ayudame a redactarlo" de la spec de UI). <paramref name="currentText"/> es
    /// opcional: si el dueño ya escribió algo, se lo pasa de base para que la IA lo mejore en vez de
    /// arrancar de cero. El resultado NUNCA se persiste acá — el guardado sigue siendo, únicamente,
    /// <see cref="UpdateBudgetConditionBlockAsync"/> (regla P-21: la IA sugiere, no decide).
    ///
    /// <para>Lanza <see cref="System.ComponentModel.DataAnnotations.ValidationException"/> si
    /// <paramref name="kindText"/> no es una categoría conocida (mismo error que el PUT) e
    /// <see cref="InvalidOperationException"/>, con un mensaje en criollo apto para mostrar tal cual al
    /// usuario, si la inteligencia artificial no está configurada o no pudo redactar el borrador.</para>
    /// </summary>
    Task<BudgetConditionDraftDto> GenerateBudgetConditionDraftAsync(string kindText, string? currentText, CancellationToken cancellationToken);

    /// <summary>
    /// TANDA 4 (2026-08-13): genera con IA un BORRADOR del texto de "Formas de pago" que la agencia
    /// carga UNA vez en Configuración (<see cref="TravelApi.Domain.Entities.AgencySettings.BudgetPaymentTermsTemplate"/>).
    /// Gemelo EXACTO de <see cref="GenerateBudgetConditionDraftAsync"/> pero sin categoría — el borrador
    /// NUNCA se guarda solo (regla P-21) y, si la IA no está disponible, tira
    /// <see cref="InvalidOperationException"/> con un mensaje en criollo apto para mostrar tal cual.
    /// </summary>
    Task<BudgetConditionDraftDto> GenerateBudgetPaymentTermsTemplateDraftAsync(string? currentText, CancellationToken cancellationToken);

    // BI Analytics
    Task<List<SellerRankingDto>> GetSellerRankingAsync(DateTime? from, DateTime? to, CancellationToken cancellationToken);
    Task<List<DestinationAnalyticsDto>> GetDestinationAnalyticsAsync(DateTime? from, DateTime? to, CancellationToken cancellationToken);
    Task<CashFlowProjectionResponse> GetCashFlowProjectionAsync(int days, CancellationToken cancellationToken);
    Task<YearOverYearResponse> GetYearOverYearAsync(CancellationToken cancellationToken);
}

// DTOs
public record DashboardResponse(
    int Presupuestos,
    int Reservados,
    int Operativos,
    decimal CobrosDelMes,
    decimal SaldoPendiente,
    decimal VentasDelMes,
    decimal CostosDelMes,
    decimal MargenBruto,
    decimal PagosProveedores,
    List<PendingReservaDto> ReservasPendientes,
    List<UpcomingTripDto> ProximosViajes,
    List<MonthlyMetricDto> TendenciaHistorica,
    StatusDistributionDto DistribucionEstados,
    BnaUsdSellerRateDto? BnaUsdSellerRate,
    int ActivePotentialCustomers,
    // ADR-021 Capa 6 (aditivos): los escalares de arriba quedan para compat (hoy, todo ARS, coinciden con
    // el unico item ARS de cada lista). Estos desgloses NUNCA mezclan monedas en un solo total.
    DashboardByCurrencyDto? PorMoneda = null,
    // ADR-011 (enmienda 2026-08-05, decision firmada del dueño): segunda tarjeta del dashboard, "Dólar
    // para facturar (ARCA)" — el MISMO valor que la pantalla de facturar va a sugerir ahora mismo (no
    // "solo datos reales" como BnaUsdSellerRate: en homologacion es correcto mostrar el numero de
    // practica, con el aviso EsDePrueba prendido).
    DolarParaFacturarDto? DolarParaFacturar = null);

/// <summary>
/// ADR-021 Capa 6: desgloses del dashboard SEPARADOS por moneda. Cada lista tiene a lo sumo una linea
/// por moneda presente. Cobros/pagos van por la moneda REAL del movimiento; saldo/cuentas por cobrar y
/// por pagar por la moneda del SALDO contra las tablas hijas materializadas.
/// </summary>
public record DashboardByCurrencyDto(
    List<CurrencyAmount> CobrosDelMes,
    List<CurrencyAmount> PagosProveedores,
    List<CurrencyAmount> VentasDelMes,
    List<CurrencyAmount> CostosDelMes,
    // Ventas menos costos, POR MONEDA (nunca mezclada con otra moneda). Igual criterio de enmascarado
    // que CostosDelMes: si el usuario no puede ver costos, esta lista viene vacia (no se puede mostrar
    // margen sin mostrar de donde sale, porque venta - margen revelaria el costo).
    List<CurrencyAmount> MargenBruto,
    List<CurrencyAmount> SaldoPendiente,
    List<CurrencyAmount> CuentasPorPagar);

public record CurrencyAmount(string Currency, decimal Amount);

// ADR-021 Capa 6 (B2): el top-N de deudoras se calcula POR MONEDA contra la tabla hija. El DTO gana
// Currency (contrato aditivo; default ARS para compat con el front viejo). Una instalacion 100% ARS
// solo produce items ARS = identico a hoy.
public record PendingReservaDto(Guid PublicId, string NumeroReserva, string Name, decimal Balance, string Status, string Currency = "ARS");

/// <summary>
/// Una salida de los proximos 7 dias, para la tarjeta "Salidas próximas" del dashboard.
/// </summary>
/// <param name="PaxCount">
/// R4 (spec dashboard 2026-08-18): cuantos pasajeros lleva la reserva (cuenta simple de
/// <c>Passenger</c> cargados). 0 si todavia no se cargo ningun pasajero — no es un error, muchas
/// reservas recien confirmadas todavia no tienen la nomina completa.
/// </param>
/// <param name="PendingBalances">
/// R4: saldo pendiente de la reserva, UNA linea por moneda (nunca un escalar que sume ARS+USD,
/// P-3). Misma logica de "que cuenta como deuda real" que <see cref="PendingReservaDto"/> (tabla
/// hija ReservaMoneyByCurrency + el eje DerivedCollectionStatus cuando ya esta calculado, ver
/// H15). Lista VACIA significa reserva saldada — el front pinta el chip verde "Saldada"; si trae
/// alguna moneda, pinta el chip rojo "Debe $ X" con esa moneda.
/// </param>
public record UpcomingTripDto(
    Guid PublicId,
    string NumeroReserva,
    string Name,
    DateTime StartDate,
    string Status,
    int PaxCount,
    List<CurrencyAmount> PendingBalances);
public record MonthlyMetricDto(string Month, decimal Sales, decimal Costs, decimal Profit);
public record StatusDistributionDto(int Budgets, int Reserved, int Operational, int Closed, int Cancelled);
/// <summary>
/// Tarjeta 1 del dashboard, "Dólar Banco Nación (venta)" (decision firmada del dueño 2026-08-05):
/// SOLO datos reales — scraper BNA fresco, o su ultimo snapshot persistido, o (ADR-011, enmienda
/// 2026-08-05) el respaldo de la API publica cuando ninguno de los dos anteriores sirvio. Nunca un
/// dato de práctica de ARCA: para eso esta la tarjeta 2 (<see cref="DolarParaFacturarDto"/>).
/// </summary>
/// <param name="Value">Dolar vendedor (billetes BNA, o su equivalente real via la API publica de respaldo).</param>
/// <param name="EuroValue">Euro vendedor BNA, o su equivalente via la API publica de respaldo
/// (ampliacion 2026-08-06: la libreta ahora TAMBIEN sincroniza EUR, ver <c>ExchangeRateSyncJob</c>).
/// <c>Null</c> cuando NINGUNA de las dos fuentes tiene un dato al menos tan fresco como
/// <see cref="Value"/> (nunca se muestra un euro mas viejo que el dolar de al lado sin poder
/// avisarlo — ver <c>ReportService.AttachFreshAuxiliaryCurrenciesAsync</c>). Nunca un numero
/// inventado.</param>
/// <param name="RealValue">Real vendedor BNA. Misma regla que <paramref name="EuroValue"/> (la
/// libreta sincroniza BRL con una escalera de proveedores mas chica, ver el doc de clase de
/// <c>IOfficialDollarPublicApiService</c>).</param>
public record BnaUsdSellerRateDto(
    decimal Value,
    decimal? EuroValue,
    decimal? RealValue,
    string PublishedDate,
    string PublishedTime,
    string Source,
    bool IsStale,
    DateTime FetchedAt);

/// <summary>
/// Tarjeta 2 del dashboard, "Dólar para facturar (ARCA)" (ADR-011, enmienda 2026-08-05, decision
/// firmada del dueño): el MISMO valor que <c>GET /api/exchange-rates/suggestion</c> le sugeriria
/// ahora mismo a la pantalla de facturar — a proposito NO se filtra por "solo datos reales" (a
/// diferencia de la tarjeta 1): en homologacion, ARCA exige facturar con SU numero de practica, asi
/// que mostrar ese mismo numero aca (con el aviso <see cref="EsDePrueba"/> prendido) es lo honesto.
/// </summary>
/// <param name="Value">El tipo de cambio, tal cual lo devuelve el resolver.</param>
/// <param name="RateDate">Fecha REAL del dato (puede ser anterior a hoy si vino de un walk-back).</param>
/// <param name="EsDePrueba">
/// SIEMPRE <c>false</c> desde la "ayuda invisible del tipo de cambio" (spec firmada 2026-08-06,
/// decision P6=A): cuando el numero no es plata de verdad, la tarjeta entera viene en <c>null</c> y no
/// se muestra — ya no existe el caso "se muestra con un aviso al lado". El campo se conserva para no
/// romper el contrato con la pantalla que ya lo lee (regla T-8) y quedara sin uso.
/// </param>
public record DolarParaFacturarDto(decimal Value, DateOnly RateDate, bool EsDePrueba);

// ============================================================================================
// "Facturas en dolares" (spec firmada 2026-08-06, Parte B). El dueño lo resumio asi: cobraste
// US$ 1.000 a $1.500 (te entraron $1.500.000) pero la factura salio al dolar del techo, $1.234,50
// ($1.234.500). Esos $265.500 de diferencia son REALES y NORMALES: el contador los necesita
// ordenados, mes por mes. El vendedor no los tiene que ver nunca — por eso esto vive detras del
// permiso de Reportes y no en el estado de cuenta de la reserva.
// ============================================================================================

/// <param name="Filas">Una por factura de venta en moneda extranjera del periodo, de la mas nueva a la mas vieja.</param>
/// <param name="Totales">Totales del periodo (el pie de la tabla).</param>
public record UsdInvoicesReportResponse(
    List<UsdInvoiceRowDto> Filas,
    UsdInvoicesReportTotalsDto Totales);

/// <param name="Fecha">Dia de emision del comprobante.</param>
/// <param name="Comprobante">Etiqueta legible ("Factura B 0001-00000012").</param>
/// <param name="ComprobanteId">Identificador publico de la factura, para que el front arme el link.</param>
/// <param name="NumeroReserva">Numero de la reserva a la que pertenece ("R-1042"). Null si la factura no tiene reserva.</param>
/// <param name="ReservaId">Identificador publico de la reserva, para el link. Null si no tiene reserva.</param>
/// <param name="Cliente">Nombre del cliente que pago.</param>
/// <param name="Moneda">Moneda del comprobante, en codigo corto ("USD").</param>
/// <param name="MontoEnMonedaExtranjera">Lo que dice la factura en su moneda (los US$ de la tabla).</param>
/// <param name="TipoCambioFactura">El tipo de cambio con el que se emitio.</param>
/// <param name="PesosDeLaFactura">Monto x tipo de cambio: lo que la factura vale en pesos.</param>
/// <param name="PesosCobrados">
/// La plata que efectivamente entro imputada a ESTA factura. <c>null</c> = todavia no se cobro nada
/// (no es un pendiente ni un error: simplemente no hay dato que mostrar).
/// </param>
/// <param name="Diferencia">
/// Cobrado menos facturado. <c>null</c> cuando no hay cobros o cuando da exactamente cero — en los dos
/// casos no hay nada que contar y la tabla muestra un guion.
/// </param>
public record UsdInvoiceRowDto(
    DateTime Fecha,
    string Comprobante,
    Guid ComprobanteId,
    string? NumeroReserva,
    Guid? ReservaId,
    string Cliente,
    string Moneda,
    decimal MontoEnMonedaExtranjera,
    decimal TipoCambioFactura,
    decimal PesosDeLaFactura,
    decimal? PesosCobrados,
    decimal? Diferencia);

/// <summary>Pie de la tabla. Mismo criterio de "null = nada que mostrar" que las filas.</summary>
public record UsdInvoicesReportTotalsDto(
    decimal PesosDeLaFactura,
    decimal? PesosCobrados,
    decimal? Diferencia);

public record ReportsSummaryResponse(
    int TotalCustomers,
    int TotalReservas,
    int TotalReservations,
    decimal TotalRevenue,
    decimal OutstandingBalance,
    decimal TotalCosts,
    decimal TotalSupplierPayments,
    decimal TotalSales,
    decimal GrossMargin);

// BI Analytics DTOs

/// <param name="TotalSales">
/// TOTAL de venta confirmada del vendedor, TODAS las monedas sumadas en un solo numero. Se conserva
/// SOLO por compatibilidad con el front actual de "Ranking de vendedores" (P-3 prohibe sumar monedas
/// distintas) — un consumidor NUEVO tiene que usar <see cref="TotalSalesByCurrency"/>.
/// </param>
/// <param name="TotalCosts">
/// Igual que <paramref name="TotalSales"/> pero de costo — Y ADEMAS en 0 sin <c>cobranzas.see_cost</c>
/// (F-14: es informacion de costo, mismo criterio que <c>CostosDelMes</c> del dashboard). Antes este
/// endpoint era Admin-only (que siempre ve costo) asi que nunca hacia falta enmascarar; al abrirlo a
/// <c>reportes.view</c> (ver <c>ReportsController</c>) el enmascarado pasa a ser obligatorio.
/// </param>
/// <param name="GrossMargin">Igual criterio que <paramref name="TotalCosts"/>: en 0 sin <c>cobranzas.see_cost</c>.</param>
/// <param name="MarginPercent">Igual criterio: en 0 sin <c>cobranzas.see_cost</c> (revelaria costo por division).</param>
/// <param name="TotalSalesByCurrency">P-3: venta confirmada del vendedor, UNA linea por moneda (ADR-021, tabla hija ReservaMoneyByCurrency).</param>
/// <param name="TotalCostsByCurrency">
/// P-3 + F-14: costo por moneda. Lista VACIA (no solo en 0) sin <c>cobranzas.see_cost</c> — es costo, se omite entero.
/// </param>
/// <param name="GrossMarginByCurrency">Igual criterio que <paramref name="TotalCostsByCurrency"/>: lista vacia sin permiso.</param>
public record SellerRankingDto(
    string UserId,
    string SellerName,
    int ReservasCreated,
    decimal TotalSales,
    decimal TotalCosts,
    decimal GrossMargin,
    decimal MarginPercent,
    List<CurrencyAmount> TotalSalesByCurrency,
    List<CurrencyAmount> TotalCostsByCurrency,
    List<CurrencyAmount> GrossMarginByCurrency);

/// <param name="TotalRevenue">TOTAL de venta del destino, TODAS las monedas sumadas — se conserva por compatibilidad (P-3), ver <see cref="TotalRevenueByCurrency"/>.</param>
/// <param name="TotalCost">Igual criterio que <see cref="SellerRankingDto.TotalCosts"/>: en 0 sin <c>cobranzas.see_cost</c> (F-14).</param>
/// <param name="Margin">Igual criterio: en 0 sin <c>cobranzas.see_cost</c>.</param>
/// <param name="TotalRevenueByCurrency">P-3: venta del destino, UNA linea por moneda (la moneda de cada servicio: hotel/paquete/vuelo).</param>
/// <param name="TotalCostByCurrency">P-3 + F-14: costo por moneda, lista VACIA sin <c>cobranzas.see_cost</c>.</param>
/// <param name="MarginByCurrency">Igual criterio que <see cref="TotalCostByCurrency"/>.</param>
public record DestinationAnalyticsDto(
    string Destination,
    int BookingCount,
    decimal TotalRevenue,
    decimal TotalCost,
    decimal Margin,
    int PassengerCount,
    List<CurrencyAmount> TotalRevenueByCurrency,
    List<CurrencyAmount> TotalCostByCurrency,
    List<CurrencyAmount> MarginByCurrency);

/// <param name="CurrentBalance">
/// Saldo actual TOTAL, todas las monedas sumadas en un solo numero. Se conserva SOLO por
/// compatibilidad con <c>AnalyticsPage.jsx</c> (P-3 prohibe sumar monedas distintas) — un
/// consumidor NUEVO tiene que usar <see cref="CurrentBalanceByCurrency"/>.
/// </param>
/// <param name="CurrentBalanceByCurrency">
/// P-3: mismo saldo actual que <see cref="CurrentBalance"/>, pero UNA linea por moneda — es el
/// ultimo dia historico de <c>RunningBalanceByCurrency</c> (mismo enmascarado de costos que esa
/// serie, no se recalcula distinto).
/// </param>
/// <param name="ProjectedBalance30ByCurrency">
/// P-3: mismo saldo proyectado a 30 dias que <see cref="ProjectedBalance30"/>, pero UNA linea por
/// moneda — mismo dia de la proyeccion, solo que en <c>RunningBalanceByCurrency</c>.
/// </param>
/// <param name="ProjectedBalance60ByCurrency">Igual que <see cref="ProjectedBalance30ByCurrency"/> pero a 60 dias.</param>
/// <param name="ProjectedBalance90ByCurrency">Igual que <see cref="ProjectedBalance30ByCurrency"/> pero a 90 dias.</param>
public record CashFlowProjectionResponse(
    List<CashFlowDayDto> Historical,
    List<CashFlowDayDto> Projected,
    decimal CurrentBalance,
    decimal ProjectedBalance30,
    decimal ProjectedBalance60,
    decimal ProjectedBalance90,
    List<CurrencyAmount> CurrentBalanceByCurrency,
    List<CurrencyAmount> ProjectedBalance30ByCurrency,
    List<CurrencyAmount> ProjectedBalance60ByCurrency,
    List<CurrencyAmount> ProjectedBalance90ByCurrency);

/// <summary>
/// Un dia de la curva de caja (historica o proyectada), "Ritmo de cobros y pagos" del dashboard.
/// </summary>
/// <param name="CashIn">
/// TOTAL de cobros de ese dia, TODAS las monedas sumadas en un solo numero. Se conserva SOLO por
/// compatibilidad con <c>AnalyticsPage.jsx</c> (unico consumidor legacy, ver R2 spec dashboard
/// 2026-08-18) — mismo criterio ya usado en <see cref="DashboardResponse"/> para sus escalares
/// viejos. Un consumidor NUEVO (la tarjeta "Ritmo de cobros y pagos") NUNCA debe leer este campo:
/// tiene que usar <see cref="CashInByCurrency"/>, que nunca mezcla ARS con USD (P-3).
/// </param>
/// <param name="CashOut">
/// Igual que <paramref name="CashIn"/> pero de pagos a operadores — Y ADEMAS enmascarado a 0 sin
/// <c>cobranzas.see_cost</c> (es informacion de costo, mismo criterio que PagosProveedores del
/// dashboard).
/// </param>
/// <param name="RunningBalance">
/// Saldo acumulado (cobros menos pagos), TOTAL todas las monedas, ya calculado con el
/// <see cref="CashOut"/> enmascarado — si se calculara con el pago REAL sin enmascarar, cualquiera
/// podria despejar el costo real restando el cobro (visible) del cambio de saldo dia a dia.
/// </param>
/// <param name="CashInByCurrency">R2: cobros de ese dia, UNA linea por moneda.</param>
/// <param name="CashOutByCurrency">
/// R2+R3: pagos a operadores de ese dia, UNA linea por moneda. Lista VACIA (no solo en 0) sin
/// <c>cobranzas.see_cost</c> — es costo, se omite entero.
/// </param>
/// <param name="RunningBalanceByCurrency">Saldo acumulado, UNA linea por moneda (mismo enmascarado que <see cref="RunningBalance"/>).</param>
public record CashFlowDayDto(
    DateTime Date,
    decimal CashIn,
    decimal CashOut,
    decimal RunningBalance,
    List<CurrencyAmount> CashInByCurrency,
    List<CurrencyAmount> CashOutByCurrency,
    List<CurrencyAmount> RunningBalanceByCurrency);

public record YearOverYearResponse(
    List<YoyMonthDto> CurrentYear,
    List<YoyMonthDto> PreviousYear,
    decimal CurrentYearTotal,
    decimal PreviousYearTotal,
    decimal GrowthPercent);

/// <param name="Sales">TOTAL de venta del mes, TODAS las monedas sumadas — se conserva por compatibilidad (P-3), ver <see cref="SalesByCurrency"/>.</param>
/// <param name="Costs">Igual criterio que <see cref="SellerRankingDto.TotalCosts"/>: en 0 sin <c>cobranzas.see_cost</c> (F-14).</param>
/// <param name="Margin">Igual criterio: en 0 sin <c>cobranzas.see_cost</c>.</param>
/// <param name="SalesByCurrency">P-3: venta del mes, UNA linea por moneda (ADR-021, tabla hija ReservaMoneyByCurrency).</param>
/// <param name="CostsByCurrency">P-3 + F-14: costo por moneda, lista VACIA sin <c>cobranzas.see_cost</c>.</param>
/// <param name="MarginByCurrency">Igual criterio que <see cref="CostsByCurrency"/>.</param>
public record YoyMonthDto(
    string Month,
    int MonthNumber,
    decimal Sales,
    decimal Costs,
    decimal Margin,
    int ReservaCount,
    List<CurrencyAmount> SalesByCurrency,
    List<CurrencyAmount> CostsByCurrency,
    List<CurrencyAmount> MarginByCurrency);
