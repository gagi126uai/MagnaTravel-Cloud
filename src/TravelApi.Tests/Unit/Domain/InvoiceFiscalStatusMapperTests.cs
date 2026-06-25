using TravelApi.Domain.Entities;
using Xunit;

namespace TravelApi.Tests.Unit.Domain;

/// <summary>
/// H2 (2026-06-24): el mapper PURO que traduce el <c>Invoice.Resultado</c> crudo ("PENDING"/"A"/"R") al
/// estado claro de tres valores que ve el front (InProcess/Issued/Rejected). Es la fuente unica de "que
/// significa cada Resultado", asi que se testea aislado.
/// </summary>
public class InvoiceFiscalStatusMapperTests
{
    [Fact]
    public void Resultado_A_MapsToIssued()
    {
        Assert.Equal(InvoiceFiscalStatus.Issued, InvoiceFiscalStatusMapper.FromResultado("A"));
    }

    [Fact]
    public void Resultado_R_MapsToRejected()
    {
        Assert.Equal(InvoiceFiscalStatus.Rejected, InvoiceFiscalStatusMapper.FromResultado("R"));
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cualquier-otra-cosa")]
    public void Resultado_PendingOrUnknown_MapsToInProcess(string? resultado)
    {
        // Default conservador: una factura recien creada (sin Resultado) o cualquier valor que no sea el
        // "A"/"R" explicito de ARCA se considera "en proceso", nunca emitida ni rechazada.
        Assert.Equal(InvoiceFiscalStatus.InProcess, InvoiceFiscalStatusMapper.FromResultado(resultado));
    }
}
