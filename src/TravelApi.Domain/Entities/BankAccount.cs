using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-041 TANDA 2 (2026-06-27): cuenta bancaria POLIMORFICA. Una sola tabla guarda las cuentas de los
/// tres tipos de dueño del sistema (la Agencia, un Cliente o un Proveedor). Sirve para anotar a donde se
/// transfiere/recibe plata: el CBU/alias, el titular y la moneda.
///
/// <para><b>Por que polimorfica (una tabla) y no tres</b>: el dato bancario es identico para los tres dueños
/// (CBU, alias, titular, banco, moneda). Tres tablas espejo serian duplicacion pura. Se distingue el dueño con
/// <see cref="OwnerType"/> + <see cref="OwnerId"/>.</para>
///
/// <para><b>OwnerId es FK LOGICA, no fisica</b>: NO hay una FK de base de datos a Customers/Suppliers/Agency,
/// porque una sola columna no puede apuntar a tres tablas distintas. La integridad (que el OwnerId exista) se
/// valida en la capa de servicio. A cambio, NO hay borrado en cascada: si se borra un cliente, sus cuentas
/// quedan huerfanas — se limpian aparte o quedan inertes (no se listan si el dueño no existe).</para>
///
/// <para><b>Regla de oro de la plata</b>: una cuenta es de UNA sola moneda (<see cref="Currency"/> obligatorio,
/// ARS o USD). La plata nunca cruza monedas, asi que una cuenta en pesos y una en dolares son filas distintas.</para>
/// </summary>
public class BankAccount : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>Tipo de dueño de la cuenta (Agencia / Cliente / Proveedor). Se persiste como int.</summary>
    public BankAccountOwnerType OwnerType { get; set; }

    /// <summary>
    /// Id del dueño (Agency=0 fijo / Customer.Id / Supplier.Id). FK LOGICA: sin constraint fisica en BD,
    /// la validacion de existencia vive en el servicio.
    /// </summary>
    public int OwnerId { get; set; }

    /// <summary>
    /// CBU (22 digitos). Sensible: se enmascara en los listados (solo ultimos 4) y se muestra completo solo
    /// en el detalle/edicion. Al menos uno de <see cref="Cbu"/> o <see cref="Alias"/> debe estar presente
    /// (CHECK en BD + validacion en servicio).
    /// </summary>
    [MaxLength(22)]
    public string? Cbu { get; set; }

    /// <summary>Alias bancario (formato AR). Alternativa al CBU para identificar la cuenta.</summary>
    [MaxLength(50)]
    public string? Alias { get; set; }

    /// <summary>Titular de la cuenta. OBLIGATORIO: sin titular no sabemos a nombre de quien esta la cuenta.</summary>
    [Required]
    [MaxLength(200)]
    public string HolderName { get; set; } = string.Empty;

    /// <summary>Moneda de la cuenta (ARS/USD). OBLIGATORIO. Una cuenta = una moneda (regla de oro).</summary>
    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>Banco (texto libre). Opcional.</summary>
    [MaxLength(100)]
    public string? Bank { get; set; }

    /// <summary>Tipo de cuenta (Caja de ahorro / Cuenta corriente). Opcional. Se persiste como int.</summary>
    public BankAccountType? AccountType { get; set; }

    /// <summary>CUIT/CUIL del titular. Opcional.</summary>
    [MaxLength(20)]
    public string? HolderTaxId { get; set; }

    /// <summary>Notas internas. Opcional.</summary>
    [MaxLength(500)]
    public string? Notes { get; set; }

    /// <summary>
    /// Soft-delete: el DELETE no borra la fila, la desactiva (IsActive=false). Asi no perdemos la trazabilidad
    /// de una cuenta que se uso para cobrar/pagar. Los listados solo muestran las activas.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Cuenta PRINCIPAL del dueño PARA ESTA MONEDA. Al pagar/mostrar a donde transferir, se elige la principal
    /// de la moneda en juego. La regla es: una sola principal por (OwnerType, OwnerId, Currency) — un mismo dueño
    /// puede tener una principal en pesos y otra en dolares. Marcar una principal DESMARCA la anterior del mismo
    /// dueño+moneda, en forma atomica (lo coordina <c>BankAccountService</c>; no hay constraint fisica que lo
    /// imponga). Default false.
    /// </summary>
    public bool IsPrimary { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Quien creo la cuenta (trazabilidad). Id de AspNetUsers.</summary>
    [MaxLength(450)]
    public string? CreatedByUserId { get; set; }

    public DateTime? UpdatedAt { get; set; }
}

/// <summary>ADR-041: tipo de dueño de una <see cref="BankAccount"/>. Valores estables (se persisten como int).</summary>
public enum BankAccountOwnerType
{
    Agency = 0,
    Customer = 1,
    Supplier = 2,
}

/// <summary>ADR-041: tipo de cuenta bancaria. Valores estables (se persisten como int).</summary>
public enum BankAccountType
{
    CajaAhorro = 0,
    CuentaCorriente = 1,
}
