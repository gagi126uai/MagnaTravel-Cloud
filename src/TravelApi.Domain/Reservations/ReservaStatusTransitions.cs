using System;
using System.Collections.Generic;
using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-035 C6 (2026-06-19): FUENTE UNICA de las transiciones manuales de estado de la Reserva.
///
/// <para>Antes estas dos tablas vivian como <c>private static</c> dentro de <c>ReservaService</c>
/// (Infrastructure). Se movieron al dominio sin cambiar su contenido para que DOS lectores las
/// compartan sin duplicarlas: (a) <c>ReservaService.UpdateStatusAsync</c>/<c>RevertStatusAsync</c>,
/// que las usan como gate de escritura (la defensa final), y (b) <see cref="ReservaCapabilities"/>,
/// la fachada de lectura que el frontend consulta para apagar botones. Una sola tabla -> front y back
/// nunca divergen sobre "a que estado puedo pasar".</para>
///
/// <para>Reglas que NO viven aca (a proposito, igual que cuando estaban en ReservaService):</para>
/// <list type="bullet">
///   <item>InManagement &lt;-&gt; Confirmed lo maneja SOLO el motor automatico (ReservaAutoStateService),
///     NUNCA una transicion manual. Por eso Confirmed no es destino forward ni InManagement es revert
///     manual de Confirmed.</item>
///   <item>Cancelacion ADR-002 / PendingOperatorRefund / Archived: flujos dedicados que escriben Status
///     por fuera de estas matrices.</item>
/// </list>
///
/// <para>Clase PURA (sin EF, sin DB): se lee igual desde el dominio que desde Infrastructure.</para>
/// </summary>
public static class ReservaStatusTransitions
{
    /// <summary>
    /// Matriz FORWARD unica (transiciones manuales via <c>UpdateStatusAsync</c>). Confirmed como destino
    /// esta AUSENTE adrede: solo el motor automatico lleva InManagement -&gt; Confirmed.
    ///
    /// <para>Cancelled aparece desde {InManagement, Confirmed} (cancelacion manual sin factura viva). Desde
    /// Quotation/Budget la salida es Lost, no Cancelled. Closed NO tiene salida forward (no se puede cancelar
    /// una Finalizada — Decision 4 del dueño, ADR-035).</para>
    ///
    /// <para><b>ADR-035 (2026-06-19): Traveling YA NO se cancela</b> (decision del dueño). Una reserva "En
    /// viaje" significa que el servicio ya empezo/se presto: cancelarla no tiene sentido operativo; si hay
    /// algo que corregir se hace por nota de credito/ajuste, no cancelando. Confirmed e InManagement SIGUEN
    /// pudiendo cancelar.</para>
    ///
    /// <para><b>ADR-036 (2026-06-21, prepago puro): murio ToSettle.</b> La fila de ToSettle se elimino y
    /// Traveling pierde a ToSettle como destino: ahora la unica salida forward de Traveling es Closed (el
    /// cierre por fin de viaje). No hay etapa de liquidacion posterior — el operador ya cobro el 100% antes
    /// del viaje.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Forward =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [EstadoReserva.Quotation] = new[] { EstadoReserva.Budget, EstadoReserva.Lost },
            [EstadoReserva.Budget] = new[] { EstadoReserva.InManagement, EstadoReserva.Lost },
            [EstadoReserva.InManagement] = new[] { EstadoReserva.Cancelled },
            [EstadoReserva.Confirmed] = new[] { EstadoReserva.Traveling, EstadoReserva.Cancelled },
            // ADR-036: la unica salida de Traveling es Closed (cierre por fin de viaje). SIN ToSettle
            // (estado eliminado) y SIN Cancelled (ADR-035: en viaje no se cancela, se corrige por NC/ajuste).
            [EstadoReserva.Traveling] = new[] { EstadoReserva.Closed },
        };

    /// <summary>
    /// Matriz REVERT unica (transiciones hacia atras manuales, con la autorizacion de supervisor existente).
    /// Confirmed -&gt; InManagement NO esta: la regresion es automatica (motor).
    ///
    /// <para><c>Lost</c> revierte a {Quotation, Budget}, pero el target REAL es deterministico: el
    /// <c>FromStatus</c> de la ultima transicion a Lost. Ambos se listan aca solo para que el guard de
    /// matriz acepte el target correcto.</para>
    ///
    /// <para><b>ADR-036 (2026-06-21, prepago puro): <c>Closed</c> revierte SOLO a {Traveling}</b> (revert de
    /// cierre prematuro). Se ELIMINO el destino Closed -&gt; ToSettle (la "reapertura para facturar tarde"):
    /// ya no se reabre una Finalizada. Si hay que corregir una factura de una reserva finalizada, se hace por
    /// Nota de Credito/Debito (flujo ya permitido sin reabrir el estado). Tambien murio la fila de ToSettle.
    /// El hard-block CAE de <c>RevertStatusAsync</c> sigue vigente: una reserva con factura viva no se reabre
    /// por aca.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Revert =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [EstadoReserva.Budget] = new[] { EstadoReserva.Quotation },
            [EstadoReserva.InManagement] = new[] { EstadoReserva.Budget },
            [EstadoReserva.Lost] = new[] { EstadoReserva.Quotation, EstadoReserva.Budget },
            [EstadoReserva.Traveling] = new[] { EstadoReserva.Confirmed },
            // ADR-036: Closed revierte SOLO a Traveling (revert de cierre prematuro). NO mas Closed -> ToSettle
            // (reapertura para facturar tarde eliminada): corregir una factura de una Finalizada es por NC/ND,
            // sin reabrir el estado. La fila ToSettle -> Traveling tambien desaparecio (estado eliminado).
            [EstadoReserva.Closed] = new[] { EstadoReserva.Traveling },
            // ADR-033 (2026-06-16): una Cancelada se puede REABRIR a En gestion SOLO si la cancelacion no dejo
            // huella fiscal ni de plata. El gate duro vive en RevertStatusAsync (query D2); aca solo se declara
            // el destino legal. Simetrico con Lost.
            [EstadoReserva.Cancelled] = new[] { EstadoReserva.InManagement },
        };
}
