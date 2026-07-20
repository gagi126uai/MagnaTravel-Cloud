using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tanda 6 (plan de remediacion "contrato pantalla-motor", 2026-07-20): tests PUROS (sin base de datos)
/// del predicado <see cref="PaymentCapabilityPolicy"/> — la fuente unica que decide si UN pago puntual
/// admite editar/eliminar. Cubre los 5 casos documentados en la spec de pantalla y la asimetria a proposito
/// entre editar y eliminar (ver doc de <see cref="PaymentCapabilityContext.IsLinkedToAnyInvoice"/>).
///
/// El cross-check contra los guards REALES (que SI tocan la base) vive aparte, como test de INTEGRACION
/// Postgres (regla dura de merge del plan, T6): ver
/// <c>TravelApi.Tests.Cancellation.Integration.PaymentRowCapabilityFlagsIntegrationTests</c>.
/// </summary>
public class PaymentCapabilityPolicyTests
{
    [Fact]
    public void SinRecibo_SinFactura_PermiteEditarYEliminar()
    {
        var caps = PaymentCapabilityPolicy.For(new PaymentCapabilityContext(
            HasIssuedReceipt: false,
            HasOnlyVoidedReceipt: false,
            IsLinkedToLiveInvoice: false,
            IsLinkedToAnyInvoice: false));

        Assert.True(caps.CanEdit.Allowed);
        Assert.Null(caps.CanEdit.Reason);
        Assert.True(caps.CanDelete.Allowed);
        Assert.Null(caps.CanDelete.Reason);
    }

    [Fact]
    public void ReciboEmitido_BloqueaEditarYEliminar_ConSusMotivosReales()
    {
        var caps = PaymentCapabilityPolicy.For(new PaymentCapabilityContext(
            HasIssuedReceipt: true,
            HasOnlyVoidedReceipt: false,
            IsLinkedToLiveInvoice: false,
            IsLinkedToAnyInvoice: false));

        Assert.False(caps.CanEdit.Allowed);
        Assert.Equal(PaymentCapabilityPolicy.EditBlockedByIssuedReceiptReason, caps.CanEdit.Reason);

        Assert.False(caps.CanDelete.Allowed);
        Assert.Equal(PaymentCapabilityPolicy.DeleteBlockedByIssuedReceiptReason, caps.CanDelete.Reason);
    }

    [Fact]
    public void ReciboSoloAnulado_BloqueaEditar_PeroPermiteEliminar()
    {
        // Regla vigente desde 2026-05-11 (C28): un recibo Voided-solo (sin ninguno Issued) NO bloquea
        // eliminar — el pago se puede borrar y el recibo anulado se preserva aparte para auditoria.
        var caps = PaymentCapabilityPolicy.For(new PaymentCapabilityContext(
            HasIssuedReceipt: false,
            HasOnlyVoidedReceipt: true,
            IsLinkedToLiveInvoice: false,
            IsLinkedToAnyInvoice: false));

        Assert.False(caps.CanEdit.Allowed);
        Assert.Equal(PaymentCapabilityPolicy.EditBlockedByVoidedReceiptReason, caps.CanEdit.Reason);

        Assert.True(caps.CanDelete.Allowed);
        Assert.Null(caps.CanDelete.Reason);
    }

    [Fact]
    public void VinculadoAFacturaViva_BloqueaEditarYEliminar()
    {
        var caps = PaymentCapabilityPolicy.For(new PaymentCapabilityContext(
            HasIssuedReceipt: false,
            HasOnlyVoidedReceipt: false,
            IsLinkedToLiveInvoice: true,
            IsLinkedToAnyInvoice: true));

        Assert.False(caps.CanEdit.Allowed);
        Assert.Equal(PaymentCapabilityPolicy.EditBlockedByLiveInvoiceReason, caps.CanEdit.Reason);

        Assert.False(caps.CanDelete.Allowed);
        Assert.Equal(PaymentCapabilityPolicy.DeleteBlockedByLiveInvoiceReason, caps.CanDelete.Reason);
    }

    [Fact]
    public void VinculadoAFacturaYaAnulada_PermiteEditar_PeroSigueBloqueandoEliminar()
    {
        // Asimetria a proposito (comportamiento PREEXISTENTE de DeleteGuards, C28 — esta clase solo lo
        // expone, no lo inventa): editar SI se libera cuando la factura ya no esta viva (NC aprobada),
        // pero eliminar sigue bloqueado porque el pago ESTUVO vinculado a una factura alguna vez. Borrar
        // es irreversible, asi que se prefiere conservar el rastro completo.
        var caps = PaymentCapabilityPolicy.For(new PaymentCapabilityContext(
            HasIssuedReceipt: false,
            HasOnlyVoidedReceipt: false,
            IsLinkedToLiveInvoice: false, // la factura ya se anulo del todo (NC Succeeded)
            IsLinkedToAnyInvoice: true));  // pero el vinculo historico sigue estando

        Assert.True(caps.CanEdit.Allowed);
        Assert.Null(caps.CanEdit.Reason);

        Assert.False(caps.CanDelete.Allowed);
        Assert.Equal(PaymentCapabilityPolicy.DeleteBlockedByLiveInvoiceReason, caps.CanDelete.Reason);
    }

    [Fact]
    public void ReciboEmitido_TieneMasPrioridadQueFacturaViva_ParaEditar()
    {
        // Orden de evaluacion identico al guard real: si hay recibo Issued, ESE es el motivo que se
        // muestra, aunque tambien haya una factura viva de fondo — nunca dos motivos a la vez.
        var caps = PaymentCapabilityPolicy.For(new PaymentCapabilityContext(
            HasIssuedReceipt: true,
            HasOnlyVoidedReceipt: false,
            IsLinkedToLiveInvoice: true,
            IsLinkedToAnyInvoice: true));

        Assert.Equal(PaymentCapabilityPolicy.EditBlockedByIssuedReceiptReason, caps.CanEdit.Reason);
    }
}
