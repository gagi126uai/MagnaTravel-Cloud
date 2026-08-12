namespace TravelApi.Application.Constants;

/// <summary>
/// Obra "Borrado selectivo por grupos" (2026-07-27, PARTE A firmada por el dueño): en vez de "todo o nada",
/// el borrado masivo ahora se pide por GRUPOS de datos. Esta clase es el catalogo central de nombres de
/// grupo + el mapa de DEPENDENCIAS FORZOSAS entre ellos (regla firmada "tilda solo y avisa": el front tilda
/// solo los grupos que dependen unos de otros, y el backend VALIDA que la eleccion sea coherente).
///
/// <para><b>Como se armo el mapa de dependencias</b>: NO de memoria — se revisaron las foreign keys reales
/// del modelo de EF (ver <c>SystemDataWipeService</c>, region "detach de referencias cruzadas" para el
/// detalle completo). Dos hallazgos importantes de esa revision:</para>
/// <list type="bullet">
///   <item><b>"paisesYDestinos" NO obliga a "tarifario"</b>: <c>Rate.Destination</c>/<c>CatalogPackage.Destination</c>
///   son campos de TEXTO libre (ciudad/pais escritos a mano), no hay ninguna foreign key fisica de la tabla
///   <c>Rates</c>/<c>CatalogPackages</c> hacia <c>Countries</c>/<c>Destinations</c>. Borrar paises y destinos
///   no puede dejar un tarifario "huerfano" a nivel de base de datos.</item>
///   <item><b>Los demas cruces (Quote→Customer, Quote→Reserva, Reserva→Quote/Lead, Rate→Supplier,
///   QuoteItem→Supplier/Rate, reservas→Rate) son TODOS foreign keys OPCIONALES (columna nullable)</b>: en vez
///   de forzar una dependencia mas (que haria que, por ejemplo, borrar el tarifario solo arrastre TODAS las
///   reservas), <c>SystemDataWipeService</c> "desengancha" esas referencias (le pone NULL a la columna) antes
///   de truncar el grupo ajeno. Asi cada grupo se borra en soledad de verdad, sin sorpresas por CASCADE.
///   Ver el detalle de cada UPDATE de desenganche en <c>SystemDataWipeService.DetachCrossGroupReferencesAsync</c>.</item>
/// </list>
/// </summary>
public static class WipeGroups
{
    /// <summary>Reservas, servicios, pasajeros, cobros, facturas, caja, cancelaciones, vouchers, adjuntos, creditos, comisiones devengadas.</summary>
    public const string ReservasYPlata = "reservasYPlata";

    /// <summary>Clientes y sus limites de credito por moneda.</summary>
    public const string Clientes = "clientes";

    /// <summary>Proveedores/operadores y su cuenta corriente (facturas, pagos, saldos).</summary>
    public const string Operadores = "operadores";

    /// <summary>Tarifario: Rates + CatalogPackages (y sus salidas).</summary>
    public const string Tarifario = "tarifario";

    /// <summary>Paises y destinos cargados.</summary>
    public const string PaisesYDestinos = "paisesYDestinos";

    /// <summary>Clientes potenciales: Leads + Presupuestos (Quotes).</summary>
    public const string PosiblesClientes = "posiblesClientes";

    /// <summary>Configuracion de la agencia (AFIP, politicas de aprobacion, bot de WhatsApp, reglas de comision/multas).</summary>
    public const string Configuracion = "configuracion";

    public static readonly string[] All =
    {
        ReservasYPlata, Clientes, Operadores, Tarifario, PaisesYDestinos, PosiblesClientes, Configuracion,
    };

    /// <summary>
    /// Dependencias FORZOSAS verificadas contra las foreign keys reales: grupo -&gt; grupos que arrastra
    /// consigo (porque hay una FK fisica NO nullable, o porque borrarlo sin el otro dejaria el otro grupo
    /// vacio de sentido). Un grupo que no aparece aca (o que mapea a un array vacio) no arrastra a nadie.
    ///
    /// <para><b>clientes -&gt; reservasYPlata</b>: <c>Reserva.PayerId</c> (columna "PayerId" en la tabla
    /// "TravelFiles") es la referencia del CLIENTE TITULAR de la reserva. Es nullable a nivel de EF pero
    /// borrar clientes sin las reservas dejaria reservas sin titular de forma silenciosa (dato de negocio
    /// roto, no solo un problema de FK) — se prefiere forzar el grupo entero.</para>
    ///
    /// <para><b>operadores -&gt; reservasYPlata</b>: los servicios tipados (<c>ServicioReserva.SupplierId</c>,
    /// <c>HotelBooking</c>/<c>TransferBooking</c>/etc.) referencian al operador que presta el servicio. Mismo
    /// criterio: un servicio sin su proveedor es un dato de negocio incompleto, se prefiere forzar el grupo.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> ForcedDependencies = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [ReservasYPlata] = Array.Empty<string>(),
        [Clientes] = new[] { ReservasYPlata },
        [Operadores] = new[] { ReservasYPlata },
        [Tarifario] = Array.Empty<string>(),
        [PaisesYDestinos] = Array.Empty<string>(),
        [PosiblesClientes] = Array.Empty<string>(),
        [Configuracion] = Array.Empty<string>(),
    };

    public static bool IsValid(string group) => All.Contains(group, StringComparer.Ordinal);

    /// <summary>
    /// Hallazgo bloqueante de data-exposure (ronda de revisión, 2026-07-27): las claves internas
    /// (<see cref="ReservasYPlata"/>="reservasYPlata", etc.) son vocabulario de PROGRAMADOR — nunca deben
    /// llegar a un mensaje que lea el usuario ni a la auditoría de negocio (T-5: "los nombres internos jamás
    /// aparecen en una respuesta de API ni en un texto de usuario"). Este mapa traduce cada clave a su nombre
    /// de NEGOCIO en criollo, para que <c>SystemDataWipeService</c> lo use en los mensajes de rechazo por
    /// grupos incoherentes Y en <c>gruposBorrados</c> del audit log — nunca la clave cruda.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> GrupoLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ReservasYPlata] = "Reservas y su plata",
        [Clientes] = "Clientes",
        [Operadores] = "Operadores",
        [Tarifario] = "Tarifario",
        [PaisesYDestinos] = "Países y destinos",
        [PosiblesClientes] = "Posibles clientes",
        [Configuracion] = "Configuración",
    };

    /// <summary>
    /// Tablas del grupo "configuracion" (AFIP, políticas de aprobación, bot de WhatsApp, ajustes generales).
    /// Fuente única compartida entre <c>SystemDataWipeService</c> (las trunca) y <c>SystemDataRestoreService</c>
    /// (Parte B, "restaurar desde la app": es la lista blanca de tablas permitidas para el modo <c>real</c> de
    /// la restauración — data-only, solo sobre estas tablas cuando están vacías, ver el comentario de
    /// <c>ISystemDataRestoreService</c> para la justificación completa de por qué se restringe a este grupo).
    /// Ninguna de estas tablas tiene foreign keys hacia otra tabla del sistema (son ajustes standalone), por
    /// eso un restore data-only tabla-por-tabla es seguro sin preocuparse por el orden.
    ///
    /// <para><b>AiSettings entró acá el 2026-08-09</b> (review de seguridad de la obra M-28): si "borrar la
    /// configuración" dejaba viva la clave de la inteligencia artificial, una instalación que se entrega o se
    /// limpia se quedaba con la credencial del dueño anterior adentro. Va con el resto de la configuración.</para>
    ///
    /// <para><b>BudgetConditionBlocks entró acá el 2026-08-12</b> (obra "PDF de presupuesto"): son las
    /// condiciones de la agencia (letra chica del PDF), standalone, sin FK a ninguna reserva — mismo
    /// criterio que AgencySettings. Sin este alta, "borrar la configuración" dejaba viva la letra chica
    /// de una agencia anterior en una instalación que se entrega o se limpia.</para>
    /// </summary>
    public static readonly string[] ConfiguracionTables =
    {
        "AgencySettings", "AfipSettings", "OperationalFinanceSettings", "ApprovalPolicies", "WhatsAppBotConfigs",
        "AiSettings", "BudgetConditionBlocks",
    };

    /// <summary>
    /// Nombre de NEGOCIO (criollo) de cada tabla de <see cref="ConfiguracionTables"/>, para que
    /// <c>SystemDataRestoreService</c> nunca tenga que mostrarle al usuario (ni escribir en el audit log) un
    /// nombre técnico de tabla — regla T-5 (los nombres internos jamás aparecen en una respuesta de API ni en
    /// un texto de usuario). Fuente única compartida para que Parte A y Parte B usen siempre el mismo texto.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ConfiguracionTableLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AgencySettings"] = "los datos generales de la agencia",
        ["AfipSettings"] = "la conexión con AFIP",
        ["OperationalFinanceSettings"] = "los ajustes operativos",
        ["ApprovalPolicies"] = "las reglas de aprobación",
        ["WhatsAppBotConfigs"] = "la configuración del bot de WhatsApp",
        ["AiSettings"] = "la configuración de inteligencia artificial",
        ["BudgetConditionBlocks"] = "las condiciones del presupuesto",
    };
}
