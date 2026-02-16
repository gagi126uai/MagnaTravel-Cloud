using AutoMapper;
using TravelApi.DTOs;
using TravelApi.Models;

namespace TravelApi.Mappings;

public class MappingProfile : Profile
{
    public MappingProfile()
    {
        // Suppliers
        CreateMap<Supplier, SupplierDto>();

        // Passengers
        CreateMap<Passenger, PassengerDto>();
        
        // Financials
        CreateMap<Payment, PaymentDto>();
        CreateMap<Invoice, InvoiceDto>()
            .ForMember(dest => dest.Amount, opt => opt.MapFrom(src => src.ImporteTotal))
            .ForMember(dest => dest.Date, opt => opt.MapFrom(src => src.CreatedAt))
            .ForMember(dest => dest.Status, opt => opt.MapFrom(src => src.Resultado))
            .ForMember(dest => dest.PointOfSale, opt => opt.MapFrom(src => src.PuntoDeVenta))
            .ForMember(dest => dest.InvoiceNumber, opt => opt.MapFrom(src => src.NumeroComprobante))
            .ForMember(dest => dest.TipoComprobante, opt => opt.MapFrom(src => src.TipoComprobante))
            .ForMember(dest => dest.InvoiceType, opt => opt.MapFrom(src => 
                src.TipoComprobante == 1 ? "A" :
                src.TipoComprobante == 6 ? "B" :
                src.TipoComprobante == 11 ? "C" : 
                src.TipoComprobante == 51 ? "M" : 
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

        // Travel File
        CreateMap<TravelFile, TravelFileDto>()
            .ForMember(dest => dest.CustomerId, opt => opt.MapFrom(src => src.PayerId))
            .ForMember(dest => dest.CustomerName, opt => opt.MapFrom(src => src.Payer != null ? src.Payer.FullName : string.Empty));

        CreateMap<TravelFile, TravelFileListDto>()
            .ForMember(dest => dest.CustomerName, opt => opt.MapFrom(src => src.Payer != null ? src.Payer.FullName : string.Empty));
    }
}
