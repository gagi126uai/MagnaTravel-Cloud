namespace TravelApi.Domain.Entities;

/// <summary>
/// B1.15 Fase 2a (FIX 6): estados del flujo de anulacion de un comprobante AFIP.
///
/// El flujo:
///  1. Estado inicial: <see cref="None"/> (la factura no tiene anulacion solicitada).
///  2. <c>POST /api/invoices/{id}/annul</c> -> <see cref="Pending"/>.
///     Se persiste tambien quien solicito la anulacion, cuando, y el motivo.
///  3. El job de Hangfire procesa la anulacion en AFIP (emite Nota de Credito):
///     - Si AFIP aprueba (Resultado="A"): <see cref="Succeeded"/> + <c>AnnulledAt</c>
///       toma el <c>IssuedAt</c> de la NC.
///     - Si AFIP rechaza o hay error tecnico: <see cref="Failed"/>.
///
/// Importancia fiscal: el guard de cancelacion de reserva (FIX 7) bloquea cancel
/// si hay <c>Invoice.CAE</c> vivo y <c>AnnulmentStatus != Succeeded</c> — por eso
/// los estados <see cref="Pending"/>/<see cref="Failed"/> NO permiten cancelar.
/// Solo el flujo completo (NC emitida y aprobada) levanta el bloqueo.
/// </summary>
public enum AnnulmentStatus
{
    /// <summary>Sin solicitud de anulacion. Estado default para facturas activas.</summary>
    None = 0,

    /// <summary>Anulacion encolada en background, esperando confirmacion AFIP.</summary>
    Pending = 1,

    /// <summary>Nota de credito emitida y aprobada por AFIP. La factura quedo anulada.</summary>
    Succeeded = 2,

    /// <summary>El job fallo (rechazo AFIP o error tecnico). Requiere accion manual o reintento.</summary>
    Failed = 3,
}
