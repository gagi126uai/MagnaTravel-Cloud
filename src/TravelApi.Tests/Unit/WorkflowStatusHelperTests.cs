using TravelApi.Domain.Entities;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// P2: la deuda con el proveedor se decide con WorkflowStatusHelper.CountsForSupplierDebtByType,
/// la UNICA regla (antes era una lista de estados escrita a mano en SupplierService).
/// Regla de negocio (dueño): SOLO los servicios CONFIRMADOS generan deuda; "Solicitado" no.
/// Vuelos mapean por codigo IATA; el resto, por el texto del estado.
/// </summary>
public class WorkflowStatusHelperTests
{
    // --- Vuelos: confirmados por codigo IATA ---

    [Theory]
    [InlineData("HK")]
    [InlineData("TK")]
    [InlineData("KK")]
    [InlineData("KL")]
    public void SupplierDebt_Vuelo_CodigoConfirmado_GeneraDeuda(string status)
    {
        Assert.True(WorkflowStatusHelper.CountsForSupplierDebtByType("Vuelo", status));
    }

    [Theory]
    [InlineData("UN")]
    [InlineData("UC")]
    [InlineData("HX")]
    [InlineData("NO")]
    public void SupplierDebt_Vuelo_CodigoCancelado_NoGeneraDeuda(string status)
    {
        Assert.False(WorkflowStatusHelper.CountsForSupplierDebtByType("Vuelo", status));
    }

    [Theory]
    [InlineData("RR")]     // codigo desconocido -> Solicitado
    [InlineData("Solicitado")]
    [InlineData("Confirmado")] // OJO: un vuelo mapea por IATA, "Confirmado" (texto) NO cuenta
    public void SupplierDebt_Vuelo_NoConfirmadoPorIATA_NoGeneraDeuda(string status)
    {
        Assert.False(WorkflowStatusHelper.CountsForSupplierDebtByType("Vuelo", status));
    }

    // --- Resto de servicios: confirmados por texto (case-insensitive, robusto) ---

    [Theory]
    [InlineData("Hotel", "Confirmado")]
    [InlineData("Hotel", "confirmado")]     // minusculas: la lista vieja se las perdia
    [InlineData("Hotel", "CONFIRMADO")]
    [InlineData("Hotel", "Emitido")]
    [InlineData("Hotel", "emitido")]
    [InlineData("Traslado", "Confirmado")]
    [InlineData("Paquete", "Emitido")]
    [InlineData("Asistencia", "Confirmado")]
    public void SupplierDebt_Generico_Confirmado_GeneraDeuda(string type, string status)
    {
        Assert.True(WorkflowStatusHelper.CountsForSupplierDebtByType(type, status));
    }

    [Theory]
    [InlineData("Hotel", "Solicitado")]
    [InlineData("Hotel", "Pendiente")]
    [InlineData("Hotel", "Cancelado")]
    [InlineData("Hotel", "")]
    [InlineData("Hotel", "HK")]             // "HK" en un no-vuelo NO es confirmado (mapeo generico)
    public void SupplierDebt_Generico_NoConfirmado_NoGeneraDeuda(string type, string status)
    {
        Assert.False(WorkflowStatusHelper.CountsForSupplierDebtByType(type, status));
    }

    [Fact]
    public void SupplierDebt_Generico_CancelTienePrecedencia()
    {
        // El mapeo generico chequea "cancel" PRIMERO: aunque contenga "confirm", es Cancelado.
        Assert.False(WorkflowStatusHelper.CountsForSupplierDebtByType("Hotel", "Cancelado (era confirmado)"));
    }

    // --- Bordes: null / type nulo se trata como generico ---

    [Fact]
    public void SupplierDebt_TypeNull_SeTrataComoGenerico()
    {
        Assert.True(WorkflowStatusHelper.CountsForSupplierDebtByType(null, "Confirmado"));
        Assert.False(WorkflowStatusHelper.CountsForSupplierDebtByType(null, "Solicitado"));
    }

    [Fact]
    public void SupplierDebt_StatusNull_NoGeneraDeuda()
    {
        Assert.False(WorkflowStatusHelper.CountsForSupplierDebtByType("Hotel", null));
        Assert.False(WorkflowStatusHelper.CountsForSupplierDebtByType("Vuelo", null));
    }
}
