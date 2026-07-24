using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tanda 7 (plan de remediacion "contrato pantalla-motor", 2026-07-20): tests PUROS (sin base de datos) del
/// predicado <see cref="ServiceCancellationPreflightPolicy"/> — la fuente unica que decide si LA PAPELERA de
/// "Anular" de un servicio puntual debe ir apagada, y por que.
///
/// <para><b>Obra "anular sin factura" (2026-07-23, decisión del dueño)</b>: el motivo R1 (pago al operador sin
/// factura) DEJÓ de bloquear "anular servicio" — ahora esa acción SIEMPRE procede y deja la línea-ancla del
/// receivable (ver <c>BookingCancellationService.RecordPartialCancellationLineAsync</c>). Solo quedan 2
/// motivos que evalúa <see cref="ServiceCancellationPreflightPolicy.Evaluate"/>: voucher emitido y factura
/// viva sin cliente asignado. El parámetro <c>HasUnanchoredOperatorRefund</c> del contexto sigue existiendo
/// (no se borra: otros componentes lo siguen calculando/pasando), pero <c>Evaluate</c> ya no lo mira — los
/// tests de abajo prueban explícitamente esa indiferencia.</para>
///
/// El cross-check contra el guard REAL (que SI toca la base) vive aparte, como test de INTEGRACION Postgres
/// (regla dura de merge del plan, T7): ver
/// <c>TravelApi.Tests.Cancellation.Integration.ServiceCancellationPreflightIntegrationTests</c>.
/// </summary>
public class ServiceCancellationPreflightPolicyTests
{
    [Fact]
    public void SinNingunMotivo_PermiteAnular()
    {
        var cap = ServiceCancellationPreflightPolicy.Evaluate(new ServiceCancellationPreflightContext(
            HasLiveVoucher: false,
            HasLiveSaleInvoiceWithoutPayer: false,
            HasUnanchoredOperatorRefund: false));

        Assert.True(cap.Allowed);
        Assert.Null(cap.Reason);
    }

    [Fact]
    public void VoucherEmitido_BloqueaConSuMotivoReal()
    {
        var cap = ServiceCancellationPreflightPolicy.Evaluate(new ServiceCancellationPreflightContext(
            HasLiveVoucher: true,
            HasLiveSaleInvoiceWithoutPayer: false,
            HasUnanchoredOperatorRefund: false));

        Assert.False(cap.Allowed);
        Assert.Equal(ServiceCancellationPreflightPolicy.VoucherBlockedReason, cap.Reason);
    }

    [Fact]
    public void PagoAlOperadorSinFactura_YaNoBloquea_ObraAnularSinFactura20260723()
    {
        // Antes (R1, hasta 2026-07-20): este caso bloqueaba con UnanchoredOperatorRefundBlockedReason.
        // Desde la obra "anular sin factura" (2026-07-23): anular el servicio SIEMPRE procede, dejando la
        // línea-ancla del receivable en vez de rechazar. Evaluate() ya no mira este flag.
        var cap = ServiceCancellationPreflightPolicy.Evaluate(new ServiceCancellationPreflightContext(
            HasLiveVoucher: false,
            HasLiveSaleInvoiceWithoutPayer: false,
            HasUnanchoredOperatorRefund: true));

        Assert.True(cap.Allowed);
        Assert.Null(cap.Reason);
    }

    [Fact]
    public void FacturaVivaSinCliente_BloqueaConSuMotivoReal()
    {
        var cap = ServiceCancellationPreflightPolicy.Evaluate(new ServiceCancellationPreflightContext(
            HasLiveVoucher: false,
            HasLiveSaleInvoiceWithoutPayer: true,
            HasUnanchoredOperatorRefund: false));

        Assert.False(cap.Allowed);
        Assert.Equal(ServiceCancellationPreflightPolicy.NoPayerBlockedReason, cap.Reason);
    }

    [Fact]
    public void VoucherEmitido_TienePrioridadSobreSinCliente_CuandoLosDosAplicarian()
    {
        // Orden de evaluacion identico al guard real (CancelServiceAsync): voucher primero, sin-cliente
        // segundo. HasUnanchoredOperatorRefund=true no suma un tercer motivo (ya no bloquea, ver test de
        // arriba); nunca se muestran dos motivos a la vez.
        var cap = ServiceCancellationPreflightPolicy.Evaluate(new ServiceCancellationPreflightContext(
            HasLiveVoucher: true,
            HasLiveSaleInvoiceWithoutPayer: true,
            HasUnanchoredOperatorRefund: true));

        Assert.Equal(ServiceCancellationPreflightPolicy.VoucherBlockedReason, cap.Reason);
    }

    [Fact]
    public void SinClienteBloquea_AunConHasUnanchoredOperatorRefundEnTrue_PorqueYaNoAplica()
    {
        var cap = ServiceCancellationPreflightPolicy.Evaluate(new ServiceCancellationPreflightContext(
            HasLiveVoucher: false,
            HasLiveSaleInvoiceWithoutPayer: true,
            HasUnanchoredOperatorRefund: true));

        Assert.Equal(ServiceCancellationPreflightPolicy.NoPayerBlockedReason, cap.Reason);
    }
}
