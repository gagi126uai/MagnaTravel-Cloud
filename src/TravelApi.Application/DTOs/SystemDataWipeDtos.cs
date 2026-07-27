namespace TravelApi.Application.DTOs;

/// <summary>
/// Obra "Empezar de cero" (2026-07-27): conteos de datos de negocio agrupados por tema, tal como los va a
/// ver el usuario en la pantalla de confirmación. Se usa TANTO en el preview (GET) como en el resultado real
/// del borrado (POST) — mismo shape para que el front pueda reusar el mismo componente de "resumen" en las
/// dos pantallas.
/// </summary>
public sealed class SystemDataWipeCounts
{
    public int Reservas { get; set; }
    public int Clientes { get; set; }
    public int Operadores { get; set; }
    public int Pasajeros { get; set; }
    public int Facturas { get; set; }
    public int Cobros { get; set; }
    public int MovimientosCaja { get; set; }
    public int Archivos { get; set; }
    public int PaisesYDestinos { get; set; }
    public int Tarifario { get; set; }
    public int PosiblesClientes { get; set; }
}

/// <summary>
/// Respuesta de <c>GET /admin/danger/wipe/preview</c>: cuánto hay para borrar y si el candado fiscal está
/// activo (y por qué). SOLO LECTURA: este endpoint no cambia nada.
///
/// <para>Revisión 2026-07-27 (fix menor #6): se sacó <c>NombreBackupEstimado</c> del contrato — el front no
/// lo usa (el nombre real del backup se sabe recién en la respuesta del POST, cuando el wipe ya se ejecutó).
/// </para>
/// </summary>
public sealed class SystemDataWipePreviewResponse
{
    public SystemDataWipeCounts Conteos { get; set; } = new();
    public bool Bloqueado { get; set; }
    public string? MotivoBloqueo { get; set; }
}

/// <summary>
/// Body de <c>POST /admin/danger/wipe</c>. Los tres campos son obligatorios: la frase y la contraseña son el
/// candado "a prueba de dedos" (nadie borra todo por accidente con un solo click), y <see cref="IncluirConfiguracion"/>
/// decide si TAMBIÉN se borra la configuración de la agencia (AFIP, reglas de comisión/multas, políticas de
/// aprobación, bot de WhatsApp) — default esperado por el front: <c>false</c> (limpieza normal).
/// </summary>
public sealed class SystemDataWipeRequest
{
    public string Password { get; set; } = string.Empty;
    public string Phrase { get; set; } = string.Empty;
    public bool IncluirConfiguracion { get; set; }
}

/// <summary>
/// Respuesta 200 de <c>POST /admin/danger/wipe</c>: qué se borró de verdad, dónde quedó el archivo de backup
/// de Postgres y si el grupo de configuración se incluyó en el borrado.
///
/// <para>Revisión 2026-07-27 (fix menor #6): se sacó <c>BackupMinioPrefijo</c> del contrato — el front no lo
/// usa (la restauración de MinIO es 100% procedimiento de servidor, ver docs/db-operations.md; el prefijo
/// real queda igual en el <c>AuditLog</c> del wipe para quien necesite restaurar).</para>
/// </summary>
public sealed class SystemDataWipeResponse
{
    public SystemDataWipeCounts Borrado { get; set; } = new();
    public string BackupArchivo { get; set; } = string.Empty;
    public bool ConfiguracionBorrada { get; set; }
}
