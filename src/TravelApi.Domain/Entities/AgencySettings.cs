using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace TravelApi.Domain.Entities;

/// <summary>
/// Configuración global de la agencia
/// </summary>
public class AgencySettings
{
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string AgencyName { get; set; } = "Mi Agencia de Viajes"; // Nombre de Fantasía

    [MaxLength(200)]
    public string? LegalName { get; set; } // Razón Social

    [MaxLength(50)]
    public string? TaxCondition { get; set; } // Responsable Inscripto, Monotributo

    public DateTime? ActivityStartDate { get; set; } // Inicio de Actividades

    [MaxLength(20)]
    public string? TaxId { get; set; } // CUIT

    [MaxLength(500)]
    public string? Address { get; set; } // Domicilio Fiscal

    [MaxLength(100)]
    public string? Phone { get; set; }

    [MaxLength(200)]
    public string? Email { get; set; }

    /// <summary>
    /// % de comisión por defecto para nuevos servicios (ej: 10 = 10%)
    /// </summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal DefaultCommissionPercent { get; set; } = 10;

    /// <summary>
    /// Moneda principal de la agencia
    /// </summary>
    [MaxLength(3)]
    public string Currency { get; set; } = "ARS";

    // ============================================================================================
    // Obra "PDF de presupuesto" (decisión firmada del dueño, 2026-08-11/12), TANDA 1: datos que se
    // imprimen en el encabezado/pie del PDF que ve el cliente. Todos opcionales — una agencia puede
    // seguir sin cargarlos y el PDF simplemente no muestra esa línea (mismo criterio "nada obligatorio"
    // que el resto de esta obra).
    // ============================================================================================

    /// <summary>
    /// Legajo de EVT (Empresa de Viajes y Turismo) que exige mostrar la normativa de turismo en la
    /// papelería que ve el cliente. Texto libre: el formato exacto lo define el ente que lo emite y
    /// varía según jurisdicción — no inventamos una validación de formato sin confirmación del
    /// contador/organismo (ver nota "Necesita confirmación" en el reporte de esta obra).
    /// </summary>
    [MaxLength(50)]
    public string? AgencyLicenseNumber { get; set; }

    /// <summary>
    /// Color hexadecimal (ej. "#1E40AF") de la banda/encabezado del PDF de presupuesto. Null = el PDF
    /// usa el color por defecto de la plantilla. Se valida con <c>TravelApi.Domain.Helpers.HexColorValidator</c>
    /// (mismo criterio "solo si cambia" que el resto de los campos de esta pantalla).
    /// </summary>
    [MaxLength(7)]
    public string? PdfBandColorHex { get; set; }

    /// <summary>
    /// Clave del objeto en MinIO donde vive el logo de la agencia (mismo mecanismo de almacenamiento
    /// que usa <c>Voucher.StoredFileName</c>, vía <c>IFileStoragePort</c>). NUNCA viaja al frontend: es
    /// una referencia de almacenamiento interna, no un dato de negocio (gate de exposición de datos).
    /// El front pide el logo por <c>GET /api/reports/settings/logo</c> y sabe si hay uno cargado por
    /// <see cref="HasLogo"/>.
    /// </summary>
    [MaxLength(500)]
    [JsonIgnore]
    public string? LogoStoredFileName { get; set; }

    /// <summary>Nombre de archivo original del logo (para el Content-Disposition de la descarga).</summary>
    [MaxLength(200)]
    [JsonIgnore]
    public string? LogoFileName { get; set; }

    [MaxLength(100)]
    [JsonIgnore]
    public string? LogoContentType { get; set; }

    [JsonIgnore]
    public long? LogoFileSize { get; set; }

    /// <summary>
    /// Derivado, no persistido: le dice al front si hay un logo cargado sin exponer la clave interna
    /// de MinIO. <c>[NotMapped]</c> = EF no crea columna para esto, se calcula en memoria al leer.
    /// </summary>
    [NotMapped]
    public bool HasLogo => !string.IsNullOrWhiteSpace(LogoStoredFileName);

    /// <summary>
    /// Obra "PDF de presupuesto" (decisión #2 firmada del dueño, 2026-08-11/12), TANDA 3: plantilla de
    /// "Formas de pago" que la agencia carga UNA vez en Configuración (cuotas estándar, medios de pago
    /// aceptados, etc.). El PDF de un presupuesto puntual usa PRIMERO el texto propio de esa reserva
    /// (<see cref="Reserva.BudgetPaymentTermsText"/>); si esa reserva no tiene nada escrito, cae acá; si
    /// tampoco hay plantilla, la sección "FORMAS DE PAGO" se omite entera del PDF (nunca se inventa un
    /// texto — regla madre de la obra, decisión #8).
    /// </summary>
    [MaxLength(4000)]
    public string? BudgetPaymentTermsTemplate { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
