namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1.3 Fase 2 (plan tactico Fase 2 §FC1.3.F2.2, 2026-05-27): tabla OPERACIONAL
/// (NO fiscal) que evita el doble-POST de una Nota de Credito parcial al ARCA
/// cuando Hangfire reintenta el job de emision.
///
/// <para><b>El problema que cierra</b>: el job que emite la NC parcial corre en
/// Hangfire. Si el job se cuelga, crashea o supera el timeout, Hangfire lo
/// reintenta. Sin un guard, el reintento podria llamar de nuevo a ARCA y emitir
/// DOS comprobantes con CAE para la misma cancelacion. Eso es un problema fiscal
/// grave (dos NCs por el mismo hecho economico).</para>
///
/// <para><b>Como funciona</b>: antes de llamar al ARCA, el job inserta una fila
/// con una <see cref="Key"/> deterministica (un hash que depende de la factura
/// origen + el approval + el monto + la moneda). La columna <see cref="Key"/>
/// tiene un indice UNIQUE: si el reintento intenta insertar la misma key, el
/// INSERT falla y el job sabe que ya hubo un intento previo, entonces consulta el
/// estado real en ARCA en vez de re-emitir a ciegas.</para>
///
/// <para><b>Esta entidad es solo la ESTRUCTURA</b>. La logica anti-duplicados
/// completa (el flujo de "stale key recovery") la implementa F2.2 — esta sub-fase
/// (Etapa 0) solo crea la tabla y sus columnas para que F2.2 tenga donde escribir.</para>
/// </summary>
public class ArcaIdempotencyKey
{
    /// <summary>PK identity (mismo patron int autoincremental que el resto del modulo).</summary>
    public int Id { get; set; }

    /// <summary>
    /// Clave de idempotencia deterministica (sera UNIQUE en BD). La calcula F2.2
    /// como un hash de (facturaOrigen + approval + monto fiscal + moneda): dos
    /// intentos por la misma cancelacion producen la misma key, y el indice UNIQUE
    /// rechaza el segundo INSERT. Ese rechazo es la senal anti-duplicado.
    /// </summary>
    public string Key { get; set; } = null!;

    /// <summary>
    /// Id del job de Hangfire que creo la key. Sirve para correlacionar la fila
    /// con los logs del job en una investigacion. Nullable: una key puede crearse
    /// fuera del contexto de un job concreto (por ahora siempre viene de un job,
    /// pero no lo forzamos a nivel schema).
    /// </summary>
    public string? JobId { get; set; }

    /// <summary>Momento UTC en que se inserto la key, ANTES del POST a ARCA (timestamptz).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Momento UTC en que el intento termino (exito o fallo terminal). Null mientras
    /// el intento sigue "abierto". F2.2 usa la antiguedad de una key sin resolver
    /// (CreatedAt viejo + ResolvedAt null) para detectar keys huerfanas de un crash
    /// y disparar el recovery. Las keys resueltas viejas se pueden purgar luego.
    /// </summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// FC1.3 Fase 2 (plan §F2.2 Sub-tarea A.4): snapshot del numerador de ARCA
    /// (ultimo comprobante autorizado del punto de venta + tipo) tomado JUSTO antes
    /// del POST.
    ///
    /// <para><b>Para que se usa</b>: si un reintento encuentra una key huerfana, no
    /// puede saber por si mismo si el POST original llego a viajar a ARCA o no. La
    /// logica de recovery (F2.2) compara el numerador actual de ARCA contra este
    /// valor: si el numerador avanzo, el comprobante SI se emitio (y hay que
    /// recuperar su CAE); si no avanzo, el POST nunca viajo (y se puede reintentar
    /// limpio). Null para keys creadas sin esta info (recovery se degrada a
    /// "reintentar limpio").</para>
    /// </summary>
    public int? LastSeenNumeroBeforePost { get; set; }
}
