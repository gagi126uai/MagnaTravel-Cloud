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
    /// <para>Cancelled aparece desde {InManagement, Confirmed, Traveling, ToSettle} (cancelacion manual sin
    /// factura viva). Desde Quotation/Budget la salida es Lost, no Cancelled. Closed NO tiene salida forward
    /// (no se puede cancelar una Finalizada — Decision 4 del dueño, ADR-035).</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Forward =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [EstadoReserva.Quotation] = new[] { EstadoReserva.Budget, EstadoReserva.Lost },
            [EstadoReserva.Budget] = new[] { EstadoReserva.InManagement, EstadoReserva.Lost },
            [EstadoReserva.InManagement] = new[] { EstadoReserva.Cancelled },
            [EstadoReserva.Confirmed] = new[] { EstadoReserva.Traveling, EstadoReserva.Cancelled },
            // Traveling: Closed = cierre por default, ToSettle = desvio opcional (apartar para liquidar).
            [EstadoReserva.Traveling] = new[] { EstadoReserva.Closed, EstadoReserva.ToSettle, EstadoReserva.Cancelled },
            [EstadoReserva.ToSettle] = new[] { EstadoReserva.Closed, EstadoReserva.Cancelled },
        };

    /// <summary>
    /// Matriz REVERT unica (transiciones hacia atras manuales, con la autorizacion de supervisor existente).
    /// Confirmed -&gt; InManagement NO esta: la regresion es automatica (motor).
    ///
    /// <para><c>Lost</c> revierte a {Quotation, Budget}, pero el target REAL es deterministico: el
    /// <c>FromStatus</c> de la ultima transicion a Lost. Ambos se listan aca solo para que el guard de
    /// matriz acepte el target correcto.</para>
    ///
    /// <para>ADR-035 Decision 4-bis: <c>Closed</c> revierte a {Traveling, ToSettle}. El destino ToSettle
    /// es la reapertura "para liquidar/facturar tarde" de una venta ya finalizada (Closed -&gt; ToSettle).
    /// El saldo NO se recalcula al reabrir; pasa por <c>RevertStatusAsync</c> (actor + razon obligatoria +
    /// autorizacion de supervisor), igual que el resto de los revert sensibles.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Revert =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [EstadoReserva.Budget] = new[] { EstadoReserva.Quotation },
            [EstadoReserva.InManagement] = new[] { EstadoReserva.Budget },
            [EstadoReserva.Lost] = new[] { EstadoReserva.Quotation, EstadoReserva.Budget },
            [EstadoReserva.Traveling] = new[] { EstadoReserva.Confirmed },
            [EstadoReserva.ToSettle] = new[] { EstadoReserva.Traveling },
            // ADR-035 Decision 4-bis (2026-06-19): se AGREGA ToSettle a los destinos de Closed (antes solo
            // {Traveling}). Reabrir una Finalizada a "A liquidar" habilita FACTURAR TARDE (Decision 5) sin
            // tocar importes: pasa por RevertStatusAsync (actor + razon obligatoria + autorizacion de
            // supervisor) y NO recalcula el saldo (queda como estaba en Closed). El hard-block CAE de
            // RevertStatusAsync sigue vigente: una reserva con factura viva no se reabre por aca.
            [EstadoReserva.Closed] = new[] { EstadoReserva.Traveling, EstadoReserva.ToSettle },
            // ADR-033 (2026-06-16): una Cancelada se puede REABRIR a En gestion SOLO si la cancelacion no dejo
            // huella fiscal ni de plata. El gate duro vive en RevertStatusAsync (query D2); aca solo se declara
            // el destino legal. Simetrico con Lost.
            [EstadoReserva.Cancelled] = new[] { EstadoReserva.InManagement },
        };
}
