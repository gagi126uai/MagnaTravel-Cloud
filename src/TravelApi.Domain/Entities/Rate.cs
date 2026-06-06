using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// Tarifario Profesional - precios de referencia por proveedor y tipo de servicio
/// Incluye campos dinámicos según el tipo de servicio y estructura de precios completa
/// </summary>
public class Rate : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    
    // === PROVEEDOR ===
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    
    // === TIPO DE SERVICIO ===
    [Required]
    [MaxLength(50)]
    public string ServiceType { get; set; } = string.Empty; // Aereo, Hotel, Traslado, Paquete, Asistencia, Excursion
    
    // === INFORMACIÓN DEL PRODUCTO ===
    [Required]
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;
    
    [MaxLength(1000)]
    public string? Description { get; set; } // Descripción detallada del servicio
    
    /// <summary>
    /// Unidad de medida del precio: noche, pasajero, servicio, trayecto
    /// </summary>
    [MaxLength(50)]
    public string PriceUnit { get; set; } = "servicio"; // noche, pasajero, servicio, trayecto
    
    // === CAMPOS DINÁMICOS POR TIPO ===
    
    // --- Aéreo ---
    [MaxLength(100)]
    public string? Airline { get; set; } // Nombre de aerolínea
    
    [MaxLength(10)]
    public string? AirlineCode { get; set; } // Código IATA (AA, AR, etc)
    
    [MaxLength(100)]
    public string? Origin { get; set; } // Ciudad/Aeropuerto origen
    
    [MaxLength(100)]
    public string? Destination { get; set; } // Ciudad/Aeropuerto destino
    
    [MaxLength(50)]
    public string? CabinClass { get; set; } // Economy, Business, First
    
    [MaxLength(100)]
    public string? BaggageIncluded { get; set; } // Ej: "23kg + carry-on"
    
    // --- Hotel ---
    [MaxLength(150)]
    public string? HotelName { get; set; }
    
    [MaxLength(100)]
    public string? City { get; set; }
    
    public int? StarRating { get; set; } // 1-5 estrellas
    
    [MaxLength(100)]
    public string? RoomType { get; set; } // Capacidad: Single, Double, Triple, Quadruple, Family
    
    [MaxLength(100)]
    public string? RoomCategory { get; set; } // Categoría: Standard, Superior, Executive, Suite
    
    [MaxLength(200)]
    public string? RoomFeatures { get; set; } // Comma separated: SeaView, CityView, Connecting, Balcony
    
    [MaxLength(50)]
    public string? MealPlan { get; set; } // RO, BB, HB, FB, AI
    
    /// <summary>
    /// Tipo de precio: "por_persona" o "base_doble" (precio por habitación para 2 personas)
    /// </summary>
    [MaxLength(20)]
    public string? HotelPriceType { get; set; } = "base_doble"; // por_persona, base_doble
    
    /// <summary>
    /// Porcentaje que pagan los niños (0 = gratis, 50 = mitad, 100 = pago completo)
    /// </summary>
    public int ChildrenPayPercent { get; set; } = 0; // 0-100%
    
    /// <summary>
    /// Edad máxima considerada niño (ej: menores de 12)
    /// </summary>
    public int ChildMaxAge { get; set; } = 12;
    
    // --- Traslado ---
    [MaxLength(100)]
    public string? PickupLocation { get; set; }
    
    [MaxLength(100)]
    public string? DropoffLocation { get; set; }
    
    [MaxLength(50)]
    public string? VehicleType { get; set; } // Sedan, Van, Bus, etc
    
    public int? MaxPassengers { get; set; }
    
    public bool IsRoundTrip { get; set; } = false;
    
    // --- Paquete ---
    public bool IncludesFlight { get; set; } = false;
    public bool IncludesHotel { get; set; } = false;
    public bool IncludesTransfer { get; set; } = false;
    public bool IncludesExcursions { get; set; } = false;
    public bool IncludesInsurance { get; set; } = false;
    
    public int? DurationDays { get; set; } // Duración del paquete en días
    
    [MaxLength(2000)]
    public string? Itinerary { get; set; } // Descripción del itinerario
    
    // === ESTRUCTURA DE PRECIOS ===
    
    /// <summary>
    /// Costo neto (lo que pagamos al proveedor) - precio unitario
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal NetCost { get; set; }
    
    /// <summary>
    /// Impuestos incluidos en el costo
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Tax { get; set; } = 0;
    
    /// <summary>
    /// Precio de venta al público - precio unitario
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal SalePrice { get; set; }
    
    /// <summary>
    /// Comisión calculada (SalePrice - NetCost - Tax)
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Commission { get; set; } = 0;
    
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";
    
    // === VIGENCIA ===
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    
    // === METADATA ===
    public bool IsActive { get; set; } = true;

    [MaxLength(500)]
    public string? InternalNotes { get; set; } // Notas internas (no visibles al cliente)

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // ============================================================
    // ADR-017 F1.1 (catalogo find-or-create, 2026-06-05): el Rate ES el "producto"
    // del catalogo (no se crea una entidad CatalogProduct aparte). Estos 3 campos son
    // 100% aditivos con default neutro, asi las filas existentes no cambian. En F1.1
    // todavia NADIE los escribe desde la app (eso es F1.2+); solo se crea la estructura.
    // ============================================================

    /// <summary>
    /// Nombre NORMALIZADO del producto, usado SOLO para el buscador find-or-create de la venta
    /// (no se le muestra al usuario; el nombre lindo sigue siendo HotelName/ProductName).
    /// Se calcula en la app con <c>TextNormalizer.NormalizeForCatalog</c> al escribir el Rate.
    ///
    /// <para>Fuente por tipo (regla UNICA, misma en backfill SQL y en escritura de app): Hotel =
    /// <c>HotelName</c> si no esta vacio, si no <c>ProductName</c>; el resto de los tipos =
    /// <c>ProductName</c>. En hoteles legacy el nombre real vive en HotelName y ProductName suele ser
    /// generico ("Tarifa hotel doble"); por eso Hotel prioriza HotelName, para que el anti-duplicados
    /// no nazca roto. Tiene un indice GIN trigram (creado por SQL crudo en la migracion, igual que los
    /// indices de HotelName/ProductName).</para>
    /// </summary>
    [MaxLength(200)]
    public string? SearchName { get; set; }

    /// <summary>
    /// Marca el "pill violeta" Creado en venta: <c>true</c> cuando el producto nacio inline durante la
    /// carga de un servicio (no desde el back-office del tarifario). Default <c>false</c> = curado en
    /// back-office o legacy. En F1.1 nadie lo setea todavia (la creacion inline es F1.2).
    /// </summary>
    public bool CreatedInSale { get; set; } = false;

    /// <summary>
    /// Trazabilidad: id de la Reserva donde se creo este producto en venta (null si no nacio asi o si
    /// esa Reserva se borro — la FK es ON DELETE SET NULL para no bloquear el borrado de reservas).
    /// </summary>
    public int? CreatedFromReservaId { get; set; }
}
