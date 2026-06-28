namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-041 (2026-06-27): mapeo PURO "tipo de dueño -> permiso requerido" para operar sus cuentas bancarias.
/// Vive aca (y no inline en el controller) para poder testearlo unitariamente sin levantar HTTP, y para que
/// el mapeo sea una sola fuente de verdad (lectura y escritura del mismo modulo lo consultan).
///
/// <para><b>Regla de escritura de la Agencia</b>: las cuentas de la Agencia son configuracion del sistema.
/// Igual que el resto de la configuracion sensible (ver OperationalFinanceSettingsController), su escritura es
/// SOLO Admin — por eso <see cref="RequiredWritePermission"/> devuelve <c>null</c> para Agency: no hay permiso
/// que la habilite salvo el rol Admin (el controller hace el bypass por rol).</para>
/// </summary>
public static class BankAccountAuthorization
{
    /// <summary>
    /// Permiso necesario para LISTAR/VER las cuentas de un dueño. Siempre hay un permiso de lectura por dueño.
    /// El rol Admin igual bypassea (eso lo resuelve el controller).
    /// </summary>
    public static string RequiredReadPermission(BankAccountOwnerType ownerType) => ownerType switch
    {
        BankAccountOwnerType.Supplier => Permissions.ProveedoresView,
        BankAccountOwnerType.Customer => Permissions.ClientesView,
        BankAccountOwnerType.Agency => Permissions.ConfiguracionView,
        _ => throw new ArgumentOutOfRangeException(nameof(ownerType), ownerType, "Tipo de dueño desconocido."),
    };

    /// <summary>
    /// Permiso necesario para CREAR/EDITAR/DESACTIVAR la cuenta de un dueño. <c>null</c> = no hay permiso que lo
    /// habilite (caso Agency): solo el rol Admin puede escribir. El controller interpreta null como "Admin-only".
    /// </summary>
    public static string? RequiredWritePermission(BankAccountOwnerType ownerType) => ownerType switch
    {
        BankAccountOwnerType.Supplier => Permissions.ProveedoresEdit,
        BankAccountOwnerType.Customer => Permissions.ClientesEdit,
        BankAccountOwnerType.Agency => null, // Configuracion: escritura Admin-only (sin permiso dedicado).
        _ => throw new ArgumentOutOfRangeException(nameof(ownerType), ownerType, "Tipo de dueño desconocido."),
    };
}
