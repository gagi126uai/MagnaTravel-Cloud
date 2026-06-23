namespace TravelApi.Application.DTOs;

/// <summary>
/// Resultado de buscar pasajeros ya cargados en el sistema para reutilizar sus datos
/// ("ficha de pasajero reutilizable"). Cada Passenger vive por-reserva, asi que una misma
/// persona aparece muchas veces; este DTO representa UNA persona ya deduplicada por documento
/// (o por nombre normalizado si no tiene documento), con sus datos mas recientes.
///
/// <para>PRIVACIDAD (decision de negocio): solo expone los datos de identidad de la persona
/// para autocompletar el formulario de pasajero. NO trae ReservaId, numero de reserva, ni nada
/// de las reservas donde viajo — un usuario no debe inferir en que viajes estuvo una persona a
/// partir de esta busqueda. <see cref="UsageCount"/> es solo un conteo agregado (en cuantas
/// reservas figura), sin identificar cuales.</para>
/// </summary>
public class PassengerSimilarMatchDto
{
    public string FullName { get; set; } = string.Empty;
    public string? DocumentType { get; set; }
    public string? DocumentNumber { get; set; }
    public DateTime? BirthDate { get; set; }
    public string? Nationality { get; set; }
    public string? Gender { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public DateTime? PassportExpiry { get; set; }

    /// <summary>En cuantas reservas figura esta persona (conteo agregado, sin exponer cuales).</summary>
    public int UsageCount { get; set; }

    /// <summary>
    /// Puntaje de coincidencia (mayor = mejor match). Documento exacto pesa mas que nombre parcial,
    /// igual que en la busqueda de clientes (CustomerService.SearchSimilarAsync).
    /// </summary>
    public int Score { get; set; }
}
