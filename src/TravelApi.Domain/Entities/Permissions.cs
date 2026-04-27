namespace TravelApi.Domain.Entities;

/// <summary>
/// Catálogo de permisos del sistema. Agregar nuevos strings aquí para escalar.
/// </summary>
public static class Permissions
{
    // Reservas
    public const string ReservasView = "reservas.view";
    public const string ReservasEdit = "reservas.edit";
    public const string ReservasDelete = "reservas.delete";

    // Vouchers
    public const string VouchersGenerate = "vouchers.generate";
    public const string VouchersIssue = "vouchers.issue";
    public const string VouchersUpload = "vouchers.upload";
    public const string VouchersSend = "vouchers.send";
    public const string VouchersAuthorizeException = "vouchers.authorize_exception";

    // Mensajes
    public const string MessagesView = "messages.view";
    public const string MessagesSend = "messages.send";

    // Clientes
    public const string ClientesView = "clientes.view";
    public const string ClientesEdit = "clientes.edit";

    // Proveedores
    public const string ProveedoresView = "proveedores.view";
    public const string ProveedoresEdit = "proveedores.edit";

    // Cobranzas y Facturación
    public const string CobranzasView = "cobranzas.view";
    public const string CobranzasEdit = "cobranzas.edit";

    // Caja
    public const string CajaView = "caja.view";
    public const string CajaEdit = "caja.edit";

    // Reportes
    public const string ReportesView = "reportes.view";

    // Configuración
    public const string ConfiguracionView = "configuracion.view";
    public const string ConfiguracionUsers = "configuracion.users";
    public const string ConfiguracionAfip = "configuracion.afip";

    // CRM
    public const string CrmView = "crm.view";
    public const string CrmEdit = "crm.edit";

    // Tarifario
    public const string TarifarioView = "tarifario.view";
    public const string TarifarioEdit = "tarifario.edit";

    // Paquetes
    public const string PaquetesView = "paquetes.view";
    public const string PaquetesEdit = "paquetes.edit";

    // Auditoría
    public const string AuditoriaView = "auditoria.view";
    public const string PaquetesPublish = "paquetes.publish";

    /// <summary>
    /// Todos los permisos disponibles agrupados por módulo para la UI.
    /// </summary>
    public static readonly Dictionary<string, string[]> AllByModule = new()
    {
        ["Reservas"] = new[] { ReservasView, ReservasEdit, ReservasDelete },
        ["Vouchers"] = new[] { VouchersGenerate, VouchersIssue, VouchersUpload, VouchersSend, VouchersAuthorizeException },
        ["Mensajes"] = new[] { MessagesView, MessagesSend },
        ["Clientes"] = new[] { ClientesView, ClientesEdit },
        ["Proveedores"] = new[] { ProveedoresView, ProveedoresEdit },
        ["Cobranzas"] = new[] { CobranzasView, CobranzasEdit },
        ["Caja"] = new[] { CajaView, CajaEdit },
        ["Reportes"] = new[] { ReportesView },
        ["Configuración"] = new[] { ConfiguracionView, ConfiguracionUsers, ConfiguracionAfip },
        ["CRM"] = new[] { CrmView, CrmEdit },
        ["Tarifario"] = new[] { TarifarioView, TarifarioEdit },
        ["Paises y destinos"] = new[] { PaquetesView, PaquetesEdit, PaquetesPublish },
        ["Auditoría"] = new[] { AuditoriaView },
    };

    public static readonly string[] All = AllByModule.Values.SelectMany(v => v).ToArray();

    /// <summary>Permisos default para Admin (todos).</summary>
    public static string[] DefaultAdmin => All;

    /// <summary>Permisos default para Colaborador (ver casi todo, editar lo operativo).</summary>
    public static readonly string[] DefaultColaborador = new[]
    {
        ReservasView, ReservasEdit,
        VouchersGenerate, VouchersIssue, VouchersUpload, VouchersSend,
        MessagesView, MessagesSend,
        ClientesView, ClientesEdit,
        ProveedoresView,
        CobranzasView,
        CajaView,
        TarifarioView,
        PaquetesView,
    };

    /// <summary>Permisos default para Vendedor (CRM + reservas + clientes).</summary>
    public static readonly string[] DefaultVendedor = new[]
    {
        ReservasView, ReservasEdit,
        VouchersGenerate, VouchersSend,
        MessagesView, MessagesSend,
        ClientesView, ClientesEdit,
        CrmView, CrmEdit,
        CobranzasView,
        TarifarioView,
        PaquetesView,
    };
}
