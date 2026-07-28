namespace TravelApi.Application.DTOs;

/// <summary>
/// Obra "Empezar de cero" (2026-07-27): conteos de datos de negocio agrupados por tema, tal como los va a
/// ver el usuario en la pantalla de confirmación. Se usa TANTO en el preview (GET) como en el resultado real
/// del borrado (POST) — mismo shape para que el front pueda reusar el mismo componente de "resumen" en las
/// dos pantallas. También se reusa en el resultado de la restauración de PRUEBA (Parte B) para poder mostrar
/// "esto es lo que hay en la base de prueba" con el mismo componente.
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
/// Respuesta de <c>GET /admin/danger/wipe/preview</c>: cuánto hay para borrar, si el candado fiscal está
/// activo (y por qué), y el mapa de DEPENDENCIAS entre grupos para que el front pueda "tildar solo" (regla
/// firmada 2026-07-27, Parte A "borrado selectivo por grupos"). SOLO LECTURA: este endpoint no cambia nada.
///
/// <para><b>Dependencias</b>: cada clave es un nombre de grupo (ver <c>TravelApi.Application.Constants.WipeGroups</c>)
/// y el valor es la lista de grupos que ESE grupo arrastra consigo. Un array vacío significa "no arrastra a
/// nadie". El front usa esto para tildar automáticamente los grupos dependientes apenas el usuario tilda uno
/// que los arrastra.</para>
/// </summary>
public sealed class SystemDataWipePreviewResponse
{
    public SystemDataWipeCounts Conteos { get; set; } = new();
    public bool Bloqueado { get; set; }
    public string? MotivoBloqueo { get; set; }
    public Dictionary<string, string[]> Dependencias { get; set; } = new();
}

/// <summary>
/// Body de <c>POST /admin/danger/wipe</c> (Parte A, 2026-07-27: borrado selectivo por grupos). La frase y la
/// contraseña son el candado "a prueba de dedos" (nadie borra por accidente con un solo click); <see cref="Grupos"/>
/// es la lista de grupos a borrar (ver <c>TravelApi.Application.Constants.WipeGroups</c>) — el motor VALIDA
/// que la lista sea coherente con las dependencias forzosas antes de tocar un solo dato (409 si falta algún
/// grupo dependiente).
///
/// <para><b>Reemplaza al viejo <c>IncluirConfiguracion</c></b> (borrado del contrato, no queda como alias
/// deprecado — decisión consistente con la política del producto de no dejar banderas viejas colgadas):
/// para borrar también la configuración de la agencia, el caller ahora incluye
/// <c>"configuracion"</c> dentro de <see cref="Grupos"/>.</para>
/// </summary>
public sealed class SystemDataWipeRequest
{
    public string Password { get; set; } = string.Empty;
    public string Phrase { get; set; } = string.Empty;
    public List<string> Grupos { get; set; } = new();
}

/// <summary>
/// Respuesta 200 de <c>POST /admin/danger/wipe</c>: qué se borró de verdad, dónde quedó el archivo de backup
/// de Postgres, y qué grupos se incluyeron en el borrado (el resuelto final, incluidas las dependencias
/// forzosas — coincide con lo que el caller mandó porque el POST ya validó coherencia antes de ejecutar).
/// </summary>
public sealed class SystemDataWipeResponse
{
    public SystemDataWipeCounts Borrado { get; set; } = new();
    public string BackupArchivo { get; set; } = string.Empty;
    public List<string> GruposBorrados { get; set; } = new();
}
