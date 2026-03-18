using AutoMapper;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.Mappings;

public class MappingProfile : Profile
{
    public MappingProfile()
    {
        // Suppliers
        CreateMap<Supplier, SupplierDto>();

        // Passengers
        CreateMap<Passenger, PassengerDto>();
        
        // Financials
        CreateMap<Payment, PaymentDto>()
            .ForMember(dest => dest.ReservaId, opt => opt.MapFrom(src => src.ReservaId))
            .ForMember(dest => dest.NumeroReserva, opt => opt.MapFrom(src => src.Reserva != null ? src.Reserva.NumeroReserva : null));
            
        CreateMap<Invoice, InvoiceDto>()
            .ForMember(dest => dest.Reserva, opt => opt.MapFrom(src => src.Reserva))
            .ForMember(dest => dest.InvoiceType, opt => opt.MapFrom(src => 
                src.TipoComprobante == 1 || src.TipoComprobante == 2 || src.TipoComprobante == 3 ? "A" :
                src.TipoComprobante == 6 || src.TipoComprobante == 7 || src.TipoComprobante == 8 ? "B" :
                src.TipoComprobante == 11 || src.TipoComprobante == 12 || src.TipoComprobante == 13 ? "C" : 
                src.TipoComprobante == 51 ? "M" : 
                "UNK"));

        // Services
        CreateMap<FlightSegment, FlightSegmentDto>()
            .ForMember(dest => dest.SupplierName, opt => opt.MapFrom(src => src.Supplier != null ? src.Supplier.Name : string.Empty));
            
        CreateMap<CreateFlightRequest, FlightSegment>();
        CreateMap<UpdateFlightRequest, FlightSegment>();

        CreateMap<HotelBooking, HotelBookingDto>()
            .ForMember(dest => dest.SupplierName, opt => opt.MapFrom(src => src.Supplier != null ? src.Supplier.Name : string.Empty));

        CreateMap<CreateHotelRequest, HotelBooking>()
            .ForMember(dest => dest.Nights, opt => opt.MapFrom(src => (src.CheckOut - src.CheckIn).Days));
        CreateMap<UpdateHotelRequest, HotelBooking>()
            .ForMember(dest => dest.Nights, opt => opt.MapFrom(src => (src.CheckOut - src.CheckIn).Days));

        CreateMap<TransferBooking, TransferBookingDto>()
            .ForMember(dest => dest.SupplierName, opt => opt.MapFrom(src => src.Supplier != null ? src.Supplier.Name : string.Empty));

        CreateMap<CreateTransferRequest, TransferBooking>();
        CreateMap<UpdateTransferRequest, TransferBooking>();

        CreateMap<PackageBooking, PackageBookingDto>()
            .ForMember(dest => dest.SupplierName, opt => opt.MapFrom(src => src.Supplier != null ? src.Supplier.Name : string.Empty));
            
        CreateMap<CreatePackageRequest, PackageBooking>()
            .ForMember(dest => dest.Nights, opt => opt.MapFrom(src => (src.EndDate - src.StartDate).Days));
        CreateMap<UpdatePackageRequest, PackageBooking>()
            .ForMember(dest => dest.Nights, opt => opt.MapFrom(src => (src.EndDate - src.StartDate).Days));

        // Customers
        CreateMap<Customer, CustomerDto>();

        // Reserva
        CreateMap<Reserva, ReservaDto>()
            .ForMember(dest => dest.CustomerId, opt => opt.MapFrom(src => src.PayerId))
            .ForMember(dest => dest.CustomerName, opt => opt.MapFrom(src => src.Payer != null ? src.Payer.FullName : string.Empty))
            .ForMember(dest => dest.Payer, opt => opt.MapFrom(src => src.Payer))
            .ForMember(dest => dest.Invoices, opt => opt.Ignore()); 

        CreateMap<Reserva, ReservaListDto>()
            .ForMember(dest => dest.CustomerName, opt => opt.MapFrom(src => src.Payer != null ? src.Payer.FullName : string.Empty))
            .ForMember(dest => dest.TotalSale, opt => opt.MapFrom(src => 
                (src.FlightSegments.Sum(f => (decimal?)f.SalePrice) ?? 0) +
                (src.HotelBookings.Sum(h => (decimal?)h.SalePrice) ?? 0) +
                (src.TransferBookings.Sum(t => (decimal?)t.SalePrice) ?? 0) +
                (src.PackageBookings.Sum(p => (decimal?)p.SalePrice) ?? 0) +
                (src.Servicios.Sum(r => (decimal?)r.SalePrice) ?? 0)
            ))
            .ForMember(dest => dest.Balance, opt => opt.MapFrom(src => 
                ((src.FlightSegments.Sum(f => (decimal?)f.SalePrice) ?? 0) +
                 (src.HotelBookings.Sum(h => (decimal?)h.SalePrice) ?? 0) +
                 (src.TransferBookings.Sum(t => (decimal?)t.SalePrice) ?? 0) +
                 (src.PackageBookings.Sum(p => (decimal?)p.SalePrice) ?? 0) +
                 (src.Servicios.Sum(r => (decimal?)r.SalePrice) ?? 0)) 
                - 
                (src.Payments.Where(p => p.Status != "Cancelled" &&  !p.IsDeleted).Sum(p => (decimal?)p.Amount) ?? 0)
            ));
    }
}
