using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-019 (avisos "Proximos inicios", 2026-06-06): registro del boton "Listo" con el que un usuario
/// apaga A MANO el aviso de "la reserva empieza pronto" en la campanita. El descarte es GLOBAL
/// (decision del dueño, Q1): apaga el aviso para TODOS los usuarios, y queda registrado quien y
/// cuando lo apago (supervision por auditoria, no por re-aviso).
///
/// <para><b>Semantica de re-armado (D3)</b>: el aviso queda oculto UNICAMENTE mientras
/// <see cref="DismissedFirstStartDate"/> coincida con el primer inicio ACTUAL de la reserva
/// (calculado por <c>UpcomingStartCalculator</c>). Si el primer inicio cambia — se agrega un
/// servicio que empieza antes, se corre la fecha, se cancela el servicio que era el primero —
/// las fechas dejan de coincidir y el aviso REAPARECE. Es preferible re-avisar de mas que
/// callar de menos.</para>
///
/// <para><b>Unicidad</b>: a lo sumo UNA fila por reserva (indice UNIQUE en <see cref="ReservaId"/>,
/// definido en AppDbContext). Re-descartar = upsert de la misma fila (se pisa fecha + auditoria).
/// La fila se borra en cascada con la Reserva, asi que no hay basura que crezca ni hace falta
/// job de limpieza.</para>
/// </summary>
public class UpcomingStartAlertDismissal
{
    public int Id { get; set; }

    /// <summary>FK a Reservas (tabla legacy TravelFiles), ON DELETE CASCADE + UNIQUE.</summary>
    public int ReservaId { get; set; }

    /// <summary>
    /// El primer inicio que se descarto, como fecha "de pared" a medianoche con Kind=Utc (mismo
    /// contrato date-only del resto del sistema). La calcula SIEMPRE el server al momento del
    /// dismiss — el cliente no manda fecha (elimina la carrera "vi una fecha, descarto otra").
    /// </summary>
    public DateTime DismissedFirstStartDate { get; set; }

    /// <summary>Auditoria: quien apreto "Listo" (Id de ApplicationUser; columna texto sin FK).</summary>
    [Required]
    [MaxLength(450)]
    public string DismissedByUserId { get; set; } = string.Empty;

    /// <summary>Auditoria: cuando se descarto (instante UTC real).</summary>
    public DateTime DismissedAtUtc { get; set; }
}
