using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Helpers;

/// <summary>
/// FC1.2.0 v3 §2.4 + §6.6 (MR-V2-01, 2026-05-17): helper estatico para armar
/// <see cref="ManualCashMovement"/> desde el modulo de cancelacion/refund.
///
/// **Por que existe**:
///   - <c>OperatorRefundService.AllocateAsync</c> (T2) genera un movimiento
///     Income cuando el operador deposita la devolucion.
///   - <c>ClientCreditService.WithdrawAsync</c> (T3) genera un movimiento
///     Expense cuando el cliente retira su saldo (o Income cuando devuelve).
///   - Sin este helper, esa logica quedaria duplicada en ambos services con
///     riesgo de drift (validaciones distintas, campos olvidados, etc.).
///
/// **Decisiones de diseno** (ver plan tactico v3 §6.6):
///   - Es <c>static</c>: no necesita DbContext ni inyeccion. Solo arma POCOs.
///   - **NO** llama a <c>_db.Add(...)</c> ni <c>SaveChangesAsync</c>: eso lo
///     hace el caller dentro de su transaccion envolvente.
///   - **NO** depende de <c>EconomicRulesHelper</c> de Infrastructure: usa
///     <see cref="ReservationEconomicPolicy.RoundCurrency"/> que vive en
///     Domain (evita ciclo Domain -> Infrastructure).
///   - Reusable desde tests unit sin fixture.
///
/// **Plan tactico vs codigo real (inconsistencia documentada por backend-senior 2026-05-17)**:
///   - El plan v3 §6.6 referenciaba <c>withdrawal.Method</c>,
///     <c>withdrawal.Reference</c> y <c>withdrawal.OccurredAt</c>, pero la
///     entity <see cref="ClientCreditWithdrawal"/> NO tiene esas propiedades.
///     Tiene <c>ExecutedAt</c>. Adapto las firmas para usar los campos reales
///     y dejo la decision documentada. El service caller pasara explicitamente
///     metodo + categoria + referencia cuando los necesite.
/// </summary>
public static class ManualCashMovementBuilder
{
    /// <summary>
    /// Construye un <see cref="ManualCashMovement"/> de tipo <c>Income</c>
    /// representando el ingreso fisico que la agencia recibe de un operador
    /// como parte de un refund (T2 del flujo de cancelacion).
    ///
    /// **Preconditions** (caller responsability):
    ///   - <c>refund.Supplier</c> debe estar <c>Include</c>-do (MR-V2-04):
    ///     usamos <c>refund.Supplier.Name</c> en la descripcion para que el
    ///     Libro de Caja muestre algo legible al cashier.
    ///   - <c>refund.ReceivedAmount &gt; 0</c>: el helper rechaza con
    ///     <see cref="ArgumentException"/> si llega cero o negativo.
    ///   - <c>refund.Method</c> no puede ser vacio (es <c>[Required]</c> en
    ///     la entity pero la validacion adicional cubre llamadas desde tests).
    ///
    /// **N:M y trazabilidad** (MR-V2-05, plan v3):
    /// <c>RelatedReservaId = null</c> porque un mismo ingreso del operador
    /// puede cubrir N <see cref="BookingCancellation"/> distintas. La
    /// trazabilidad va por <see cref="ManualCashMovement.OperatorRefundReceivedId"/>,
    /// y <c>TreasuryService.GetCashSummaryAsync</c> sabe renderizar
    /// "Devolucion operador X (N BCs asociados)" cuando detecta este patron.
    ///
    /// **NO commitea**: el caller hace <c>_db.ManualCashMovements.Add(result)</c>
    /// y <c>SaveChangesAsync</c> dentro de su transaccion envolvente.
    /// </summary>
    /// <exception cref="ArgumentNullException">Si <paramref name="refund"/> es null.</exception>
    /// <exception cref="ArgumentException">Si createdByUserId esta vacio.</exception>
    /// <exception cref="InvalidOperationException">
    /// Si refund.ReceivedAmount &lt;= 0, refund.Supplier no esta Included, o refund.Method esta vacio.
    /// </exception>
    public static ManualCashMovement BuildIncomeForRefund(
        OperatorRefundReceived refund,
        string createdByUserId)
    {
        if (refund is null)
        {
            throw new ArgumentNullException(nameof(refund));
        }
        if (string.IsNullOrWhiteSpace(createdByUserId))
        {
            throw new ArgumentException("createdByUserId requerido.", nameof(createdByUserId));
        }
        if (refund.ReceivedAmount <= 0m)
        {
            throw new InvalidOperationException("OperatorRefundReceived.ReceivedAmount debe ser > 0.");
        }
        if (refund.Supplier is null)
        {
            // Detalle didactico: en EF Core, si el caller no hace
            // `.Include(r => r.Supplier)`, la navigation queda null aun cuando
            // SupplierId tenga valor. Detectamos eso aca para no caer en NRE
            // dentro del string interpolado de Description.
            throw new InvalidOperationException(
                "OperatorRefundReceived.Supplier no esta cargado. El caller debe hacer .Include(r => r.Supplier) antes de invocar el builder.");
        }
        if (string.IsNullOrWhiteSpace(refund.Method))
        {
            throw new InvalidOperationException("OperatorRefundReceived.Method requerido.");
        }

        return new ManualCashMovement
        {
            Direction = CashMovementDirections.Income,
            // RoundCurrency aca es DEFENSIVO, no la fuente de verdad del redondeo.
            //
            // Por que existe el round si "ya deberia venir redondeado":
            //   - Cuando el caller carga un OperatorRefundReceived desde la BD, el
            //     monto ya esta a 2 decimales porque EF lo persistio con
            //     HasPrecision(18, 2). En ese caso este round es no-op.
            //   - PERO cuando el caller construye el monto EN MEMORIA antes del
            //     SaveChanges (caso tipico T2: la allocation calcula el net como
            //     gross - SUM(deductions) y puede salirse de 2 decimales por
            //     divisiones), el monto llega aca con mas de 2 decimales y este
            //     round asegura el contrato de la columna.
            //
            // Recomendacion para el caller: redondear UPSTREAM (al setear el monto
            // en la entity) para evitar drift silencioso entre lo que muestra la
            // UI ("$123.45") y lo que termina en BD si por algun motivo el helper
            // dejara de redondear. Si la agencia migra a monedas con 0 decimales
            // (CLP, JPY) se ajusta en ReservationEconomicPolicy y todos los
            // callers heredan.
            Amount = ReservationEconomicPolicy.RoundCurrency(refund.ReceivedAmount),
            OccurredAt = refund.ReceivedAt,
            Method = refund.Method,
            Category = "OperatorRefund",
            Description = $"Devolucion del operador {refund.Supplier.Name} ({refund.PublicId})",
            Reference = refund.Reference,
            CreatedBy = createdByUserId,
            RelatedSupplierId = refund.SupplierId,

            // MR-V2-05: N:M. La trazabilidad viaja por OperatorRefundReceivedId.
            RelatedReservaId = null,
            OperatorRefundReceivedId = refund.Id,
            ClientCreditWithdrawalId = null,
        };
    }

    /// <summary>
    /// Construye un <see cref="ManualCashMovement"/> para un retiro de
    /// <see cref="ClientCreditEntry"/> (T3 del flujo). El kind del retiro
    /// determina el <c>Direction</c>:
    ///   - <c>PhysicalCash</c> / <c>Transfer</c> -> <c>Expense</c> (sale plata de caja).
    ///   - <c>ReversedToOperator</c> -> <c>Income</c> (cliente devuelve plata,
    ///     vuelve a caja para que el siguiente paso la re-pague al operador).
    ///
    /// **Kinds NO soportados**:
    ///   - <c>KeptAsCredit</c>: el cliente decide dejar el saldo, no hay flujo
    ///     fisico, no se debe llamar al builder. Lanza
    ///     <see cref="InvalidOperationException"/> para que el caller lo trate
    ///     como bug.
    ///   - <c>AppliedToNewBooking</c>: pendiente para FC4 (se modelara como
    ///     Payment de la nueva reserva, no como ManualCashMovement). Lanza
    ///     <see cref="NotImplementedException"/>.
    ///
    /// **NO commitea**: el caller hace <c>Add</c> + <c>SaveChangesAsync</c>
    /// y luego setea <c>withdrawal.ManualCashMovementId = movement.Id</c>
    /// dentro de la misma tx.
    /// </summary>
    /// <param name="withdrawal">El retiro que origina el movimiento. NO debe estar trackeado todavia.</param>
    /// <param name="entry">El saldo del cliente del que sale el retiro. Si tiene
    /// <see cref="ClientCreditEntry.BookingCancellation"/> Include-da, el movimiento
    /// queda linkeado a la Reserva para que aparezca en el filtro por reserva del
    /// Libro de Caja.</param>
    /// <param name="createdByUserId">UserId del cashier que ejecuta la accion. Audit fiscal.</param>
    /// <param name="methodOverride">
    /// **Opcional**. Forma de pago real del retiro, tomada del request del usuario
    /// (ej. <c>"Cheque"</c>, <c>"MercadoPago"</c>, <c>"Transfer-BBVA"</c>).
    ///
    /// Si se pasa <c>null</c> (default), el helper resuelve un Method razonable
    /// por <see cref="WithdrawalKind"/>:
    ///   - <c>PhysicalCash</c> -> <c>"Cash"</c>
    ///   - <c>Transfer</c>     -> <c>"Transfer"</c>
    ///   - <c>ReversedToOperator</c> -> <c>"Transfer"</c>
    ///
    /// **Por que existe este parametro**: la entity <see cref="ClientCreditWithdrawal"/>
    /// NO tiene un campo <c>Method</c> propio (decision al modelar FC1.1: el kind
    /// es el discriminador principal, el method era ruido para una sola row). Pero
    /// cuando el cashier retira "por transferencia bancaria a cuenta XYZ", el dato
    /// fino vive en el request del controller — el caller lo pasa por aca para
    /// que termine en <c>ManualCashMovement.Method</c> y aparezca legible en el
    /// reporte de caja. Sin este parametro, el caller tenia que mutar
    /// <c>movement.Method</c> DESPUES del Build (patron fragil: si alguien refactoriza
    /// el helper y agrega validaciones post-Method, esa mutacion se las saltea).
    /// </param>
    /// <exception cref="ArgumentNullException">Si withdrawal o entry son null.</exception>
    /// <exception cref="ArgumentException">Si createdByUserId esta vacio.</exception>
    /// <exception cref="InvalidOperationException">Kind no soporta movimiento fisico o Amount &lt;= 0.</exception>
    /// <exception cref="NotImplementedException">Kind = AppliedToNewBooking (FC4).</exception>
    public static ManualCashMovement BuildExpenseForWithdrawal(
        ClientCreditWithdrawal withdrawal,
        ClientCreditEntry entry,
        string createdByUserId,
        string? methodOverride = null)
    {
        if (withdrawal is null)
        {
            throw new ArgumentNullException(nameof(withdrawal));
        }
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }
        if (string.IsNullOrWhiteSpace(createdByUserId))
        {
            throw new ArgumentException("createdByUserId requerido.", nameof(createdByUserId));
        }
        if (withdrawal.Amount <= 0m)
        {
            throw new InvalidOperationException("ClientCreditWithdrawal.Amount debe ser > 0.");
        }

        // Validaciones tempranas por kind antes de construir el POCO:
        if (withdrawal.Kind == WithdrawalKind.AppliedToNewBooking)
        {
            throw new NotImplementedException(
                "AppliedToNewBooking se modelara en FC4 como Payment, no como ManualCashMovement.");
        }
        if (withdrawal.Kind == WithdrawalKind.KeptAsCredit)
        {
            throw new InvalidOperationException(
                "KeptAsCredit no genera ManualCashMovement: el cliente decide dejar el saldo. " +
                "El caller no debe invocar el builder en este kind.");
        }

        // Caso especial: ReversedToOperator devuelve plata a caja (cliente
        // re-entrega dinero ya recibido). El resto sale como Expense.
        var direction = withdrawal.Kind == WithdrawalKind.ReversedToOperator
            ? CashMovementDirections.Income
            : CashMovementDirections.Expense;

        var category = withdrawal.Kind switch
        {
            WithdrawalKind.PhysicalCash => "ClientCreditWithdrawal",
            WithdrawalKind.Transfer => "ClientCreditWithdrawal",
            WithdrawalKind.ReversedToOperator => "ClientCreditReversal",
            // Cualquier kind nuevo que aparezca obliga a actualizar este switch:
            // mejor fallar ruidosamente que persistir una category vacia o equivocada.
            _ => throw new InvalidOperationException($"WithdrawalKind no soportado por el builder: {withdrawal.Kind}"),
        };

        // El Method viene del caller via methodOverride si tiene info fina del
        // request (ej. "Cheque", "MercadoPago", "Transfer-BBVA"). Si el caller
        // no especifica nada, hacemos un fallback por kind. Ver XML doc del
        // parametro arriba para el razonamiento completo.
        //
        // string.IsNullOrWhiteSpace en lugar de "is null" para que
        // methodOverride="" o "   " caigan al default (defensivo contra DTOs
        // mal armados; un Method vacio rompe la columna Required en BD).
        var method = !string.IsNullOrWhiteSpace(methodOverride)
            ? methodOverride
            : withdrawal.Kind switch
            {
                WithdrawalKind.PhysicalCash => "Cash",
                WithdrawalKind.Transfer => "Transfer",
                WithdrawalKind.ReversedToOperator => "Transfer",
                _ => "Transfer",
            };

        return new ManualCashMovement
        {
            Direction = direction,
            // Round defensivo — mismo razonamiento que en BuildIncomeForRefund:
            // si el Amount fue persistido por EF, ya esta a 2 decimales; si el
            // caller lo construyo en memoria (T3 cuando se calcula el retiro
            // parcial = saldo - acumulado), puede llegar con drift y este round
            // asegura el contrato. Caller deberia redondear upstream tambien.
            Amount = ReservationEconomicPolicy.RoundCurrency(withdrawal.Amount),
            OccurredAt = withdrawal.ExecutedAt,
            Method = method,
            Category = category,
            Description = $"Retiro credito cliente {entry.PublicId} ({withdrawal.Kind})",
            // Reference no existe en la entity Withdrawal; lo dejamos null.
            // El service caller puede setearlo a un dato externo si aplica
            // (ej. numero de transferencia bancaria).
            Reference = null,
            CreatedBy = createdByUserId,
            RelatedSupplierId = null,

            // Retiro de credito = 1:1 con la BC. El entry tiene la BC navigation
            // si fue Include-da; si no, el ReservaId del movimiento queda null
            // (degradacion suave; el reporte sigue agrupando por ClientCreditWithdrawalId).
            RelatedReservaId = entry.BookingCancellation?.ReservaId,
            OperatorRefundReceivedId = null,
            ClientCreditWithdrawalId = withdrawal.Id,
        };
    }
}
