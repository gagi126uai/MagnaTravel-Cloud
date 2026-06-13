using System;
using AutoMapper;
using TravelApi.Application.DTOs;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Auditoria de cancelacion por servicio (ADR-020): verifica que <c>CancelledAt</c> y
/// <c>CancelledByUserName</c>, que ya existen en las entidades de servicio, se proyecten en sus DTOs.
/// El front los muestra como "Cancelado por X el DD/MM/YYYY". El mapeo es por convencion (mismo nombre
/// en entidad y DTO), asi que estos tests son la red de seguridad de que la convencion sigue funcionando
/// si manana alguien agrega un ForMember o renombra un campo.
///
/// No hay motivo de cancelacion a nivel servicio en las entidades (solo va al audit log): por eso aca
/// solo se aseveran quien + cuando.
/// </summary>
public class CancellationAuditDtoMappingTests
{
    private static IMapper NewMapper()
        => new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

    // Valores compartidos para no repetir la fecha/usuario en cada test.
    private static readonly DateTime CancelledAt = new(2026, 6, 13, 10, 30, 0, DateTimeKind.Utc);
    private const string CancelledBy = "Gaston Albornoz";

    [Fact]
    public void HotelBooking_proyecta_auditoria_de_cancelacion()
    {
        var mapper = NewMapper();
        var entity = new HotelBooking { CancelledAt = CancelledAt, CancelledByUserName = CancelledBy };

        var dto = mapper.Map<HotelBookingDto>(entity);

        Assert.Equal(CancelledAt, dto.CancelledAt);
        Assert.Equal(CancelledBy, dto.CancelledByUserName);
    }

    [Fact]
    public void FlightSegment_proyecta_auditoria_de_cancelacion()
    {
        var mapper = NewMapper();
        var entity = new FlightSegment { CancelledAt = CancelledAt, CancelledByUserName = CancelledBy };

        var dto = mapper.Map<FlightSegmentDto>(entity);

        Assert.Equal(CancelledAt, dto.CancelledAt);
        Assert.Equal(CancelledBy, dto.CancelledByUserName);
    }

    [Fact]
    public void TransferBooking_proyecta_auditoria_de_cancelacion()
    {
        var mapper = NewMapper();
        var entity = new TransferBooking { CancelledAt = CancelledAt, CancelledByUserName = CancelledBy };

        var dto = mapper.Map<TransferBookingDto>(entity);

        Assert.Equal(CancelledAt, dto.CancelledAt);
        Assert.Equal(CancelledBy, dto.CancelledByUserName);
    }

    [Fact]
    public void PackageBooking_proyecta_auditoria_de_cancelacion()
    {
        var mapper = NewMapper();
        var entity = new PackageBooking { CancelledAt = CancelledAt, CancelledByUserName = CancelledBy };

        var dto = mapper.Map<PackageBookingDto>(entity);

        Assert.Equal(CancelledAt, dto.CancelledAt);
        Assert.Equal(CancelledBy, dto.CancelledByUserName);
    }

    [Fact]
    public void AssistanceBooking_proyecta_auditoria_de_cancelacion()
    {
        var mapper = NewMapper();
        var entity = new AssistanceBooking { CancelledAt = CancelledAt, CancelledByUserName = CancelledBy };

        var dto = mapper.Map<AssistanceBookingDto>(entity);

        Assert.Equal(CancelledAt, dto.CancelledAt);
        Assert.Equal(CancelledBy, dto.CancelledByUserName);
    }

    [Fact]
    public void ServicioReserva_proyecta_auditoria_de_cancelacion()
    {
        var mapper = NewMapper();
        var entity = new ServicioReserva { CancelledAt = CancelledAt, CancelledByUserName = CancelledBy };

        var dto = mapper.Map<ServicioReservaDto>(entity);

        Assert.Equal(CancelledAt, dto.CancelledAt);
        Assert.Equal(CancelledBy, dto.CancelledByUserName);
    }

    [Fact]
    public void Servicio_no_cancelado_deja_la_auditoria_en_null()
    {
        var mapper = NewMapper();
        var entity = new HotelBooking(); // sin cancelar

        var dto = mapper.Map<HotelBookingDto>(entity);

        Assert.Null(dto.CancelledAt);
        Assert.Null(dto.CancelledByUserName);
    }
}
