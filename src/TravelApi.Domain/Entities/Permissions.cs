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
    // B1.15: granularidad fina sobre reservas.
    public const string ReservasViewAll = "reservas.view_all";
    public const string ReservasCancel = "reservas.cancel";
    public const string ReservasCancelWithPayment = "reservas.cancel_with_payment";
    public const string ReservasDiscountAboveThreshold = "reservas.discount_above_threshold";

    // Vouchers
    public const string VouchersGenerate = "vouchers.generate";
    public const string VouchersIssue = "vouchers.issue";
    public const string VouchersUpload = "vouchers.upload";
    public const string VouchersSend = "vouchers.send";
    public const string VouchersAuthorizeException = "vouchers.authorize_exception";
    public const string VouchersRevoke = "vouchers.revoke";

    // Mensajes
    public const string MessagesView = "messages.view";
    public const string MessagesSend = "messages.send";

    // Clientes
    public const string ClientesView = "clientes.view";
    public const string ClientesEdit = "clientes.edit";

    // Proveedores
    public const string ProveedoresView = "proveedores.view";
    public const string ProveedoresEdit = "proveedores.edit";
    // B1.15: editar datos fiscales sensibles del proveedor (CUIT, condicion ARCA, etc.).
    public const string ProveedoresEditFiscal = "proveedores.edit_fiscal";

    // Cobranzas y Facturación
    public const string CobranzasView = "cobranzas.view";
    public const string CobranzasEdit = "cobranzas.edit";
    // B1.15: granularidad fina sobre cobranzas + facturacion.
    public const string CobranzasViewAll = "cobranzas.view_all";
    public const string CobranzasAnnul = "cobranzas.annul";
    public const string CobranzasInvoice = "cobranzas.invoice";
    public const string CobranzasInvoiceAnnul = "cobranzas.invoice_annul";
    public const string CobranzasSeeCost = "cobranzas.see_cost";

    // Caja
    public const string CajaView = "caja.view";
    public const string CajaEdit = "caja.edit";

    // B1.15 Fase 0' (CODE-09): pagos a proveedores. Antes este modulo era
    // [Authorize] sin permiso especifico — cualquier autenticado podia crear,
    // editar y borrar pagos egresos. Ahora requiere permiso explicito.
    public const string TesoreriaSupplierPayments = "tesoreria.supplier_payments";

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

    // Aprobaciones (B1.15 Fase B')
    /// <summary>Crear solicitudes de aprobacion (Vendedor pide override). Default: Admin/Colaborador/Vendedor.</summary>
    public const string ApprovalsRequest = "approvals.request";
    /// <summary>Aprobar/rechazar solicitudes desde la bandeja. Default: Admin/Colaborador.</summary>
    public const string ApprovalsReview = "approvals.review";

    /// <summary>
    /// Todos los permisos disponibles agrupados por módulo para la UI.
    /// </summary>
    public static readonly Dictionary<string, string[]> AllByModule = new()
    {
        ["Reservas"] = new[]
        {
            ReservasView, ReservasEdit, ReservasDelete,
            ReservasViewAll, ReservasCancel, ReservasCancelWithPayment, ReservasDiscountAboveThreshold,
        },
        ["Vouchers"] = new[] { VouchersGenerate, VouchersIssue, VouchersUpload, VouchersSend, VouchersAuthorizeException, VouchersRevoke },
        ["Mensajes"] = new[] { MessagesView, MessagesSend },
        ["Clientes"] = new[] { ClientesView, ClientesEdit },
        ["Proveedores"] = new[] { ProveedoresView, ProveedoresEdit, ProveedoresEditFiscal },
        ["Cobranzas"] = new[]
        {
            CobranzasView, CobranzasEdit,
            CobranzasViewAll, CobranzasAnnul, CobranzasInvoice, CobranzasInvoiceAnnul, CobranzasSeeCost,
        },
        ["Caja"] = new[] { CajaView, CajaEdit },
        ["Tesoreria"] = new[] { TesoreriaSupplierPayments },
        ["Reportes"] = new[] { ReportesView },
        ["Configuración"] = new[] { ConfiguracionView, ConfiguracionUsers, ConfiguracionAfip },
        ["CRM"] = new[] { CrmView, CrmEdit },
        ["Tarifario"] = new[] { TarifarioView, TarifarioEdit },
        ["Paises y destinos"] = new[] { PaquetesView, PaquetesEdit, PaquetesPublish },
        ["Auditoría"] = new[] { AuditoriaView },
        ["Aprobaciones"] = new[] { ApprovalsRequest, ApprovalsReview },
    };

    public static readonly string[] All = AllByModule.Values.SelectMany(v => v).ToArray();

    /// <summary>Permisos default para Admin (todos).</summary>
    public static string[] DefaultAdmin => All;

    /// <summary>Permisos default para Colaborador (ver casi todo, editar lo operativo).</summary>
    public static readonly string[] DefaultColaborador = new[]
    {
        ReservasView, ReservasEdit,
        // B1.15: el Colaborador opera reservas globalmente, puede cancelar (incluso con pagos)
        // y opera cobranzas/facturacion completas. NO discount_above_threshold (Admin-only por seguridad).
        ReservasViewAll, ReservasCancel, ReservasCancelWithPayment,
        VouchersGenerate, VouchersIssue, VouchersUpload, VouchersSend, VouchersRevoke,
        MessagesView, MessagesSend,
        ClientesView, ClientesEdit,
        ProveedoresView, ProveedoresEditFiscal,
        CobranzasView,
        CobranzasViewAll, CobranzasAnnul, CobranzasInvoice, CobranzasInvoiceAnnul, CobranzasSeeCost,
        CajaView,
        TesoreriaSupplierPayments,
        TarifarioView,
        PaquetesView,
        // B1.15 Fase B': el Colaborador es reviewer principal de la bandeja
        // (decision 31). Tambien puede crear sus propias solicitudes si necesita
        // override (ej. cancel-with-payment ya esta en su default, pero por
        // consistencia con el workflow generico).
        ApprovalsRequest, ApprovalsReview,
    };

    /// <summary>Permisos default para Vendedor (CRM + reservas + clientes).</summary>
    public static readonly string[] DefaultVendedor = new[]
    {
        ReservasView, ReservasEdit,
        // B1.15 (Decisión 1 de Gaston): el Vendedor SI puede cancelar reservas propias y SI puede facturar.
        // NO permisos *_all, *_annul, see_cost, edit_fiscal, discount_above_threshold,
        // authorize_exception (Admin override), tesoreria.supplier_payments (back-office).
        ReservasCancel,
        // B1.15 smoke 2026-05-09: Vendedor opera vouchers de SUS reservas (con ownership):
        // genera, emite, sube externos, envia y revoca. NO authorize_exception.
        VouchersGenerate, VouchersIssue, VouchersUpload, VouchersSend, VouchersRevoke,
        MessagesView, MessagesSend,
        ClientesView, ClientesEdit,
        // B1.15 smoke 2026-05-09: necesita ver lista basica de proveedores para asociar
        // al servicio. NO datos fiscales (eso es proveedores.edit_fiscal).
        ProveedoresView,
        CrmView, CrmEdit,
        // B1.15 smoke 2026-05-09: necesita CobranzasEdit para registrar pagos del cliente
        // en SUS reservas (con ownership). CobranzasInvoice para facturar (decision 1).
        // 2026-05-10: CobranzasInvoiceAnnul agregado para que el Vendedor pueda anular SUS
        // facturas (cuando carga mal el cliente/monto). El endpoint de annul ya valida
        // ownership (RequireOwnership Invoice, bypass via cobranzas.view_all), por lo que
        // el Vendedor solo anula sus propias facturas. La operacion queda auditada en
        // AnnulledByUser*, AnnulledAt y AnnulmentReason (fiscal: emite NC en AFIP).
        CobranzasView, CobranzasEdit, CobranzasInvoice, CobranzasInvoiceAnnul,
        TarifarioView,
        PaquetesView,
        // B1.15 Fase B' (2026-05-11): el Vendedor crea solicitudes de aprobacion
        // para acciones que no puede ejecutar directamente (override deadline,
        // descuento alto, etc.). NO tiene ApprovalsReview — eso es back-office.
        ApprovalsRequest,
    };
}
