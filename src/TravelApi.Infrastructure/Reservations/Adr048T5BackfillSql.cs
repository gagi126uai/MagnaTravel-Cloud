namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// ADR-048 T5 (2026-07-17, hardening — MT1 del review backend): las 4 sentencias SQL crudas del
/// backfill de <c>Reserva.DerivedCollectionStatus</c>/<c>DerivedInvoicingStatus</c>, en UN solo lugar.
///
/// <para><b>Por que existe esta clase (y no el SQL solo adentro de la migración)</b>: antes de este fix
/// el UNICO lugar donde vivía el texto de estas 4 sentencias era la migración EF
/// <c>Adr048_M2_AddDerivedStatusColumnsToReserva</c> — nada corría ese SQL fuera del deploy real, así
/// que la "equivalencia backfill SQL == derivación en vivo" (el riesgo central de esta tanda, señalado
/// por el review backend como MT1) quedaba verificada SOLO por inspección visual. Sacando el texto a
/// constantes compartidas, la migración y el test de integración
/// <c>Adr048T5BackfillSqlIntegrationTests</c> ejecutan el MISMO SQL — si alguien edita la migración sin
/// tocar esta clase (o al revés), compila igual pero el test corre el SQL VIEJO: por eso el comentario
/// de la migración dice explícitamente "tocá esta clase, no el SQL inline".</para>
///
/// <para><b>Estas 4 sentencias son un ESPEJO deliberado de reglas que YA existen en otro lado</b> (no
/// inventan ningún criterio nuevo): el eje de cobro espeja
/// <c>ReservaCollectionStatus.Derive</c>/<c>ReservaService.FillPorMonedaForListAsync</c>; el eje de
/// facturación espeja <c>ReservaInvoicingStatus.Derive</c> +
/// <c>ReservaInvoicingCuadreCalculator</c>/<c>ReservaService.FillInvoicingStatusForListAsync</c>. El
/// criterio de "qué tipo de comprobante suma/resta" está ALINEADO con
/// <c>InvoiceComprobanteHelpers.Categorize</c> (tipo desconocido = 0, no cuenta) — ver el XML-doc de la
/// migración para el detalle de por qué esto importa.</para>
/// </summary>
internal static class Adr048T5BackfillSql
{
    /// <summary>
    /// BACKFILL 1/4 — eje de COBRO, reservas CON filas en <c>ReservaMoneyByCurrency</c> (el caso normal:
    /// ya pasaron por <c>ReservaMoneyPersister</c> al menos una vez). Mismo umbral de centavo (0.005) que
    /// <c>ReservaCollectionStatus.Derive</c>.
    /// </summary>
    public const string CollectionAxisWithChildRows = @"
        WITH money_agg AS (
            SELECT ""ReservaId"" AS reserva_id,
                   bool_or(""Balance"" > 0.005) AS any_debt,
                   bool_or(""Balance"" < -0.005) AS any_credit,
                   bool_or(""ConfirmedSale"" > 0 OR ""TotalPaid"" > 0) AS any_activity
            FROM ""ReservaMoneyByCurrency""
            GROUP BY ""ReservaId""
        )
        UPDATE ""TravelFiles"" tf
        SET ""DerivedCollectionStatus"" = CASE
            WHEN ma.any_debt THEN 'ConDeuda'
            WHEN ma.any_credit THEN 'SaldoAFavor'
            WHEN ma.any_activity THEN 'Saldado'
            ELSE 'SinMovimientos'
        END
        FROM money_agg ma
        WHERE tf.""Id"" = ma.reserva_id;
    ";

    /// <summary>
    /// BACKFILL 2/4 — eje de COBRO, reservas SIN ninguna fila en <c>ReservaMoneyByCurrency</c> (legacy que
    /// nunca pasó por el persister, o reserva nueva sin ningún servicio/pago todavía). Mismo fallback que
    /// <c>FillPorMonedaForListAsync</c>: una única "línea" con el escalar de la cabecera.
    ///
    /// <para>Truco de SQL: <c>NOT EXISTS</c> y NO <c>NOT IN (SELECT ...)</c>. <c>NOT IN</c> con una
    /// subquery que puede traer algún <c>NULL</c> se rompe en silencio: si UNA sola fila de la subquery
    /// tiene <c>NULL</c>, la comparación completa se vuelve <c>UNKNOWN</c> para TODAS las filas y el
    /// <c>UPDATE</c> deja de tocar nada. <c>NOT EXISTS</c> no tiene ese problema.</para>
    /// </summary>
    public const string CollectionAxisFallback = @"
        UPDATE ""TravelFiles"" tf
        SET ""DerivedCollectionStatus"" = CASE
            WHEN tf.""Balance"" > 0.005 THEN 'ConDeuda'
            WHEN tf.""Balance"" < -0.005 THEN 'SaldoAFavor'
            WHEN tf.""Balance"" <> 0 OR tf.""TotalPaid"" > 0 THEN 'Saldado'
            ELSE 'SinMovimientos'
        END
        WHERE NOT EXISTS (
            SELECT 1 FROM ""ReservaMoneyByCurrency"" mbc WHERE mbc.""ReservaId"" = tf.""Id""
        );
    ";

    /// <summary>
    /// BACKFILL 3/4 — eje de FACTURACION, reservas CON al menos un comprobante con CAE aprobado
    /// (<c>Resultado='A'</c>). OJO: <c>"Invoices"</c> referencia la reserva por la columna
    /// <c>"TravelFileId"</c> (no <c>"ReservaId"</c> — la propiedad C# se llama <c>ReservaId</c> pero
    /// <c>AppDbContext</c> la remapea con <c>HasColumnName("TravelFileId")</c>, la trampa clásica de este
    /// repo si se copia el nombre de la propiedad en SQL crudo).
    ///
    /// <para>CRITERIO DE TIPOS (alineado con el escritor go-forward, 2026-07-17 review backend — ver
    /// <c>ReservaDerivedAxesProjector</c> + <c>ReservaInvoicingCuadreCalculator.SignedNetAmount</c>/
    /// <c>IsInvoiceOrDebitNote</c>): SOLO los tipos AFIP conocidos suman/restan. Un <c>TipoComprobante</c>
    /// que no es ninguno de los 12 conocidos (dato corrupto — no debería poder existir con CAE aprobado en
    /// un comprobante real) NO cuenta ni para el neto ni para el bruto (aporta 0), igual que
    /// <c>InvoiceComprobanteHelpers.Categorize -> Unknown</c>. Factura: 1(A)/6(B)/11(C)/51(M) suma; Nota de
    /// Débito: 2(A)/7(B)/12(C)/52(M) suma; Nota de Crédito: 3(A)/8(B)/13(C)/53(M) resta el neto y NO
    /// participa del bruto; cualquier otro tipo aporta 0 en ambos.</para>
    /// </summary>
    public const string InvoicingAxisWithInvoices = @"
        WITH invoice_agg AS (
            SELECT ""TravelFileId"" AS reserva_id,
                   SUM(CASE
                       WHEN ""TipoComprobante"" IN (3, 8, 13, 53) THEN -""ImporteTotal""
                       WHEN ""TipoComprobante"" IN (1, 6, 11, 51, 2, 7, 12, 52) THEN ""ImporteTotal""
                       ELSE 0
                   END) AS facturado_neto,
                   SUM(CASE
                       WHEN ""TipoComprobante"" IN (1, 6, 11, 51, 2, 7, 12, 52) THEN ""ImporteTotal""
                       ELSE 0
                   END) AS bruto_emitido
            FROM ""Invoices""
            WHERE ""Resultado"" = 'A'
            GROUP BY ""TravelFileId""
        )
        UPDATE ""TravelFiles"" tf
        SET ""DerivedInvoicingStatus"" = CASE
            WHEN ia.facturado_neto <= 0.005 THEN
                CASE WHEN ia.bruto_emitido > 0.005 THEN 'FullyReturned' ELSE 'NotInvoiced' END
            WHEN ia.facturado_neto >= tf.""TotalSale"" - 0.005 THEN 'FullyInvoiced'
            ELSE 'PartiallyInvoiced'
        END
        FROM invoice_agg ia
        WHERE tf.""Id"" = ia.reserva_id;
    ";

    /// <summary>
    /// BACKFILL 4/4 — eje de FACTURACION, reservas SIN ningún comprobante con CAE aprobado: nunca se les
    /// facturó nada -> <c>NotInvoiced</c> directo (facturado neto y bruto emitido son 0 por definición, el
    /// mismo default que usa el DTO cuando el agregado no trae fila para la reserva).
    ///
    /// <para>Mismo truco que <see cref="CollectionAxisFallback"/>: <c>NOT EXISTS</c> en vez de
    /// <c>NOT IN</c>. Acá es CRÍTICO, no solo defensivo — <c>"Invoices"."TravelFileId"</c> ES nullable en
    /// el modelo (una factura puede no estar ligada a ninguna reserva), así que un <c>NOT IN</c> se hubiera
    /// roto en silencio apenas existiera UNA factura huérfana en toda la tabla.</para>
    /// </summary>
    public const string InvoicingAxisFallback = @"
        UPDATE ""TravelFiles"" tf
        SET ""DerivedInvoicingStatus"" = 'NotInvoiced'
        WHERE NOT EXISTS (
            SELECT 1 FROM ""Invoices"" inv
            WHERE inv.""TravelFileId"" = tf.""Id"" AND inv.""Resultado"" = 'A'
        );
    ";
}
