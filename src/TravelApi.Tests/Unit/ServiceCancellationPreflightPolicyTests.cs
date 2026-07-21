using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tanda 7 (plan de remediacion "contrato pantalla-motor", 2026-07-20): tests PUROS (sin base de datos) del
/// predicado <see cref="ServiceCancellationPreflightPolicy"/> — la fuente unica que decide si LA PAPELERA de
/// "Anular" de un servicio puntual debe ir apagada, y por que. Cubre los 3 motivos documentados en la spec
/// de pantalla y el orden de prioridad cuando mas de uno aplicaria a la vez.
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
    public void PagoAlOperadorSinFactura_R1_BloqueaConSuMotivoReal()
    {
        var cap = ServiceCancellationPreflightPolicy.Evaluate(new ServiceCancellationPreflightContext(
            HasLiveVoucher: false,
            HasLiveSaleInvoiceWithoutPayer: false,
            HasUnanchoredOperatorRefund: true));

        Assert.False(cap.Allowed);
        Assert.Equal(ServiceCancellationPreflightPolicy.UnanchoredOperatorRefundBlockedReason, cap.Reason);
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
    public void VoucherEmitido_TienePrioridadSobreR1YSinCliente_CuandoLosTresAplicarian()
    {
        // Orden de evaluacion identico al guard real (CancelServiceAsync): voucher primero, R1 segundo,
        // sin-cliente tercero. Nunca se muestran dos motivos a la vez.
        var cap = ServiceCancellationPreflightPolicy.Evaluate(new ServiceCancellationPreflightContext(
            HasLiveVoucher: true,
            HasLiveSaleInvoiceWithoutPayer: true,
            HasUnanchoredOperatorRefund: true));

        Assert.Equal(ServiceCancellationPreflightPolicy.VoucherBlockedReason, cap.Reason);
    }

    [Fact]
    public void R1_TienePrioridadSobreSinCliente_CuandoLosDosAplicarian()
    {
        var cap = ServiceCancellationPreflightPolicy.Evaluate(new ServiceCancellationPreflightContext(
            HasLiveVoucher: false,
            HasLiveSaleInvoiceWithoutPayer: true,
            HasUnanchoredOperatorRefund: true));

        Assert.Equal(ServiceCancellationPreflightPolicy.UnanchoredOperatorRefundBlockedReason, cap.Reason);
    }
}
