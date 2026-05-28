using System;
using System.Collections.Generic;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC1.3 Fase 2 — F2.3 helpers unitarios.
///
/// <para>Cubren reglas chicas y puras del service que no necesitan DB ni Docker.
/// El proyecto tiene <c>InternalsVisibleTo("TravelApi.Tests")</c> configurado en
/// <c>TravelApi.Infrastructure.csproj</c>, por eso podemos llamar a
/// <c>BookingCancellationService.GetDominantAlicuotaId</c> aunque sea internal.</para>
/// </summary>
public class BookingCancellationServiceHelpersTests
{
    /// <summary>
    /// R2 contador (2026-05-28): <c>GetDominantAlicuotaId</c> NO debe devolver el
    /// default 5 (21%) cuando la lista de items llega vacia. Ahora tira
    /// <see cref="InvalidOperationException"/> con mensaje explicito.
    ///
    /// <para>Razon fiscal: una factura de hoteleria puede estar al 10.5% (alicuota 4);
    /// si por un bug aguas arriba los items quedan vacios, devolver 21% por defecto
    /// haria salir la NC parcial al ARCA con alicuota equivocada = error fiscal
    /// silencioso. Preferimos romper temprano y ruidoso a "callar y daniar".</para>
    /// </summary>
    [Fact]
    public void GetDominantAlicuotaId_EmptyList_ThrowsInvalidOperationException()
    {
        var emptyItems = new List<InvoiceItem>();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BookingCancellationService.GetDominantAlicuotaId(emptyItems));

        // El mensaje debe ser autocontenido (no requerir abrir el codigo para entender).
        Assert.Contains("sin items", ex.Message);
        Assert.Contains("alicuota IVA", ex.Message);
    }

    /// <summary>
    /// Caso positivo: con items, devuelve la alicuota dominante por suma de Total.
    /// Confirma que el cambio del fallback no rompio el camino feliz.
    /// </summary>
    [Fact]
    public void GetDominantAlicuotaId_WithItems_ReturnsAlicuotaWithHighestTotal()
    {
        // Items: $400 al 10.5% (alic 4) + $600 al 21% (alic 5). El 21% gana por $600.
        var items = new List<InvoiceItem>
        {
            new InvoiceItem { AlicuotaIvaId = 4, Total = 400m, Description = "Hotel limitrofe" },
            new InvoiceItem { AlicuotaIvaId = 5, Total = 600m, Description = "Hotel local" },
        };

        var dominant = BookingCancellationService.GetDominantAlicuotaId(items);

        Assert.Equal(5, dominant);
    }
}
