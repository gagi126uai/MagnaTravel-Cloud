using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class TravelFileService : ITravelFileService
{
    private readonly AppDbContext _context;
    private readonly IMapper _mapper;

    public TravelFileService(AppDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper = mapper;
    }

    public async Task<IEnumerable<TravelFileListDto>> GetFilesAsync()
    {
        return await _context.TravelFiles
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new TravelFileListDto 
            {
                Id = f.Id,
                FileNumber = f.FileNumber,
                Name = f.Name,
                Status = f.Status,
                CustomerName = f.Payer != null ? f.Payer.FullName : "",
                CreatedAt = f.CreatedAt,
                StartDate = f.StartDate,
                EndDate = f.EndDate,
                PassengerCount = f.Passengers.Count,
                TotalCost = (f.FlightSegments.Sum(x => (decimal?)x.NetCost) ?? 0) +
                            (f.HotelBookings.Sum(x => (decimal?)x.NetCost) ?? 0) +
                            (f.TransferBookings.Sum(x => (decimal?)x.NetCost) ?? 0) +
                            (f.PackageBookings.Sum(x => (decimal?)x.NetCost) ?? 0) +
                            (f.Reservations.Sum(x => (decimal?)x.NetCost) ?? 0),
                TotalPaid = f.Payments.Where(p => p.Status != "Cancelled" && !p.IsDeleted).Sum(p => (decimal?)p.Amount) ?? 0,
                TotalSale = (f.FlightSegments.Sum(x => (decimal?)x.SalePrice) ?? 0) +
                            (f.HotelBookings.Sum(x => (decimal?)x.SalePrice) ?? 0) +
                            (f.TransferBookings.Sum(x => (decimal?)x.SalePrice) ?? 0) +
                            (f.PackageBookings.Sum(x => (decimal?)x.SalePrice) ?? 0) +
                            (f.Reservations.Sum(x => (decimal?)x.SalePrice) ?? 0),
                Balance = ((f.FlightSegments.Sum(x => (decimal?)x.SalePrice) ?? 0) +
                           (f.HotelBookings.Sum(x => (decimal?)x.SalePrice) ?? 0) +
                           (f.TransferBookings.Sum(x => (decimal?)x.SalePrice) ?? 0) +
                           (f.PackageBookings.Sum(x => (decimal?)x.SalePrice) ?? 0) +
                           (f.Reservations.Sum(x => (decimal?)x.SalePrice) ?? 0)) -
                           (f.Payments.Where(p => p.Status != "Cancelled" && !p.IsDeleted).Sum(p => (decimal?)p.Amount) ?? 0)
            })
            .ToListAsync();
    }

    public async Task<TravelFileDto> GetFileAsync(int id)
    {
        var file = await _context.TravelFiles
            .Include(f => f.Payer)
            .Include(f => f.Passengers)
            .Include(f => f.Payments)
            .Include(f => f.Invoices)
            .Include(f => f.Reservations)
            .Include(f => f.FlightSegments).ThenInclude(fs => fs.Supplier)
            .Include(f => f.HotelBookings).ThenInclude(hb => hb.Supplier)
            .Include(f => f.TransferBookings).ThenInclude(tb => tb.Supplier)
            .Include(f => f.PackageBookings).ThenInclude(pb => pb.Supplier)
            .FirstOrDefaultAsync(f => f.Id == id);

        if (file == null) 
        {
            throw new KeyNotFoundException($"File with ID {id} not found locally");
        }

        var totalSale = 
            (file.FlightSegments?.Sum(f => f.SalePrice) ?? 0) +
            (file.HotelBookings?.Sum(h => h.SalePrice) ?? 0) +
            (file.TransferBookings?.Sum(t => t.SalePrice) ?? 0) +
            (file.PackageBookings?.Sum(p => p.SalePrice) ?? 0) +
            (file.Reservations?.Sum(r => r.SalePrice) ?? 0);

        var totalCost = 
            (file.FlightSegments?.Sum(f => f.NetCost) ?? 0) +
            (file.HotelBookings?.Sum(h => h.NetCost) ?? 0) +
            (file.TransferBookings?.Sum(t => t.NetCost) ?? 0) +
            (file.PackageBookings?.Sum(p => p.NetCost) ?? 0) +
            (file.Reservations?.Sum(r => r.NetCost) ?? 0);

        var totalPaid = file.Payments?.Where(p => p.Status != "Cancelled").Sum(p => p.Amount) ?? 0;

        file.TotalSale = totalSale;
        file.TotalCost = totalCost;
        file.Balance = totalSale - totalPaid;

        return _mapper.Map<TravelFileDto>(file);
    }

    public async Task<TravelFile> CreateFileAsync(CreateFileRequest request)
    {
        var nextId = await _context.TravelFiles.CountAsync() + 1000;
        var fileNumber = $"F-{DateTime.Now.Year}-{nextId}";
        
        var fileName = !string.IsNullOrWhiteSpace(request.Name) 
            ? request.Name 
            : $"File {fileNumber}";

        var file = new TravelFile
        {
            Name = fileName,
            FileNumber = fileNumber,
            PayerId = request.PayerId,
            StartDate = request.StartDate,
            Description = request.Description,
            Status = FileStatus.Budget
        };
        
        _context.TravelFiles.Add(file);
        await _context.SaveChangesAsync();
        
        return file;
    }

    public async Task<(Reservation Reservation, string? Warning)> AddServiceAsync(int fileId, AddServiceRequest request)
    {
        var file = await _context.TravelFiles.FindAsync(fileId);
        if (file == null) throw new KeyNotFoundException("Expediente no encontrado");

        if (string.IsNullOrWhiteSpace(request.ServiceType)) throw new ArgumentException("Debe seleccionar un tipo de servicio");
        if (request.DepartureDate == default) throw new ArgumentException("La fecha de salida es obligatoria");
        if (request.SalePrice <= 0) throw new ArgumentException("El precio de venta debe ser mayor a 0");
        if (request.NetCost < 0) throw new ArgumentException("El costo neto no puede ser negativo");

        string? warning = null;
        if (request.NetCost > request.SalePrice)
        {
            warning = $"Atención: el costo ({request.NetCost:C}) supera el precio de venta ({request.SalePrice:C}). Se está vendiendo a pérdida.";
        }

        var reservation = new Reservation
        {
            TravelFileId = fileId,
            ServiceType = request.ServiceType,
            ProductType = request.ServiceType,
            SupplierId = request.SupplierId,
            CustomerId = file.PayerId,
            Description = request.Description ?? request.ServiceType,
            ConfirmationNumber = request.ConfirmationNumber ?? "PENDIENTE",
            Status = "Solicitado",
            DepartureDate = request.DepartureDate.ToUniversalTime(),
            ReturnDate = request.ReturnDate?.ToUniversalTime(),
            SalePrice = request.SalePrice,
            NetCost = request.NetCost,
            Commission = request.SalePrice - request.NetCost,
            CreatedAt = DateTime.UtcNow
        };

        _context.Reservations.Add(reservation);
        await _context.SaveChangesAsync();

        return (reservation, warning);
    }

    public async Task<Reservation> UpdateServiceAsync(int serviceId, AddServiceRequest request)
    {
        var service = await _context.Reservations
            .Include(r => r.TravelFile)
            .FirstOrDefaultAsync(r => r.Id == serviceId);

        if (service == null) throw new KeyNotFoundException("Servicio no encontrado");

        if (string.IsNullOrWhiteSpace(request.ServiceType)) throw new ArgumentException("Debe seleccionar un tipo de servicio");
        if (request.SalePrice <= 0) throw new ArgumentException("El precio de venta debe ser mayor a 0");

        service.ServiceType = request.ServiceType;
        service.ProductType = request.ServiceType;
        service.Description = request.Description ?? request.ServiceType;
        service.ConfirmationNumber = request.ConfirmationNumber ?? service.ConfirmationNumber;
        service.DepartureDate = request.DepartureDate.ToUniversalTime();
        service.ReturnDate = request.ReturnDate?.ToUniversalTime();
        service.SupplierId = request.SupplierId;
        service.SalePrice = request.SalePrice;
        service.NetCost = request.NetCost;
        service.Commission = request.SalePrice - request.NetCost;

        await _context.SaveChangesAsync();
        return service;
    }

    public async Task RemoveServiceAsync(int serviceId)
    {
        var service = await _context.Reservations
            .Include(r => r.TravelFile)
            .FirstOrDefaultAsync(r => r.Id == serviceId);
            
        if (service == null) throw new KeyNotFoundException("Servicio no encontrado");

        _context.Reservations.Remove(service);
        await _context.SaveChangesAsync();
    }

    public async Task<IEnumerable<PassengerDto>> GetPassengersAsync(int fileId)
    {
        return await _context.Passengers
            .Where(p => p.TravelFileId == fileId)
            .OrderBy(p => p.FullName)
            .ProjectTo<PassengerDto>(_mapper.ConfigurationProvider)
            .ToListAsync();
    }

    public async Task<PassengerDto> AddPassengerAsync(int fileId, Passenger passenger)
    {
        var file = await _context.TravelFiles.FindAsync(fileId);
        if (file == null) throw new KeyNotFoundException("Expediente no encontrado");

        if (string.IsNullOrWhiteSpace(passenger.FullName)) throw new ArgumentException("El nombre del pasajero es obligatorio");
        if (passenger.FullName.Length < 3) throw new ArgumentException("El nombre debe tener al menos 3 caracteres");

        if (passenger.BirthDate.HasValue)
        {
            passenger.BirthDate = DateTime.SpecifyKind(passenger.BirthDate.Value, DateTimeKind.Utc);
        }

        passenger.TravelFileId = fileId;
        passenger.CreatedAt = DateTime.UtcNow;

        _context.Passengers.Add(passenger);
        await _context.SaveChangesAsync();

        return _mapper.Map<PassengerDto>(passenger);
    }

    public async Task<PassengerDto> UpdatePassengerAsync(int passengerId, Passenger updated)
    {
        var passenger = await _context.Passengers.FindAsync(passengerId);
        if (passenger == null) throw new KeyNotFoundException("Pasajero no encontrado");

        if (string.IsNullOrWhiteSpace(updated.FullName)) throw new ArgumentException("El nombre del pasajero es obligatorio");
        if (updated.FullName.Length < 3) throw new ArgumentException("El nombre debe tener al menos 3 caracteres");

        passenger.FullName = updated.FullName;
        passenger.DocumentType = updated.DocumentType;
        passenger.DocumentNumber = updated.DocumentNumber;
        
        if (updated.BirthDate.HasValue)
        {
            passenger.BirthDate = DateTime.SpecifyKind(updated.BirthDate.Value, DateTimeKind.Utc);
        }
        else 
        {
            passenger.BirthDate = null;
        }

        passenger.Nationality = updated.Nationality;
        passenger.Phone = updated.Phone;
        passenger.Email = updated.Email;
        passenger.Gender = updated.Gender;
        passenger.Notes = updated.Notes;

        await _context.SaveChangesAsync();
        return _mapper.Map<PassengerDto>(passenger);
    }

    public async Task RemovePassengerAsync(int passengerId)
    {
        var passenger = await _context.Passengers.FindAsync(passengerId);
        if (passenger == null) throw new KeyNotFoundException("Pasajero no encontrado");

        _context.Passengers.Remove(passenger);
        await _context.SaveChangesAsync();
    }

    public async Task<IEnumerable<PaymentDto>> GetFilePaymentsAsync(int fileId)
    {
        return await _context.Payments
            .Where(p => p.TravelFileId == fileId)
            .OrderByDescending(p => p.PaidAt)
            .ProjectTo<PaymentDto>(_mapper.ConfigurationProvider)
            .ToListAsync();
    }

    public async Task<PaymentDto> AddPaymentAsync(int fileId, Payment payment)
    {
        var file = await _context.TravelFiles.FindAsync(fileId);
        if (file == null) throw new KeyNotFoundException("Expediente no encontrado");

        if (payment.Amount <= 0) throw new ArgumentException("El monto debe ser mayor a 0");
        if (string.IsNullOrWhiteSpace(payment.Method)) throw new ArgumentException("Debe seleccionar un método de pago");
        
        payment.TravelFileId = fileId;
        payment.PaidAt = DateTime.UtcNow;
        payment.Status = "Paid";

        _context.Payments.Add(payment);
        await _context.SaveChangesAsync();

        return _mapper.Map<PaymentDto>(payment);
    }

    public async Task<PaymentDto> UpdatePaymentAsync(int fileId, int paymentId, Payment updatedPayment)
    {
        var payment = await _context.Payments.FindAsync(paymentId);
        if (payment == null) throw new KeyNotFoundException("Pago no encontrado");
        
        if (payment.TravelFileId != fileId) throw new ArgumentException("El pago no corresponde al File");

        var file = await _context.TravelFiles.FindAsync(fileId);
        if (file == null) throw new KeyNotFoundException("File no encontrado");

        if (updatedPayment.Amount <= 0) throw new ArgumentException("El monto debe ser mayor a 0");
        
        payment.Amount = updatedPayment.Amount;
        payment.Method = updatedPayment.Method;
        payment.PaidAt = updatedPayment.PaidAt.ToUniversalTime();
        payment.Notes = updatedPayment.Notes;

        await _context.SaveChangesAsync();
        return _mapper.Map<PaymentDto>(payment);
    }

    public async Task DeletePaymentAsync(int fileId, int paymentId)
    {
        var payment = await _context.Payments.FindAsync(paymentId);
        if (payment == null) throw new KeyNotFoundException("Pago no encontrado");
        
        if (payment.TravelFileId != fileId) throw new ArgumentException("El pago no corresponde al File");

        var file = await _context.TravelFiles.FindAsync(fileId);
        if (file == null) throw new KeyNotFoundException("File no encontrado");

        payment.IsDeleted = true;
        payment.DeletedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
    }

    public async Task<TravelFile> UpdateStatusAsync(int id, string status)
    {
        var file = await _context.TravelFiles.FindAsync(id);
        if (file == null) throw new KeyNotFoundException("Expediente no encontrado");

        var validStatuses = new[] { FileStatus.Budget, FileStatus.Reserved, FileStatus.Operational, FileStatus.Closed, FileStatus.Cancelled };
        if (!validStatuses.Contains(status)) throw new ArgumentException("Estado no válido");

        if (file.Status == FileStatus.Reserved && status == FileStatus.Budget)
        {
             var hasPayments = await _context.Payments.AnyAsync(p => p.TravelFileId == id);
             if (hasPayments) throw new InvalidOperationException("No se puede volver a Presupuesto porque hay pagos registrados. Elimínalos primero.");

             var hasInvoices = await _context.Invoices.AnyAsync(i => i.TravelFileId == id);
             if (hasInvoices) throw new InvalidOperationException("No se puede volver a Presupuesto porque hay facturas emitidas. Debes anularlas primero (Nota de Crédito).");
        }

        file.Status = status;
        if (status == FileStatus.Closed) file.ClosedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return file;
    }

    public async Task<TravelFile> ArchiveFileAsync(int id)
    {
        var file = await _context.TravelFiles.FindAsync(id);
        if (file == null) throw new KeyNotFoundException("File no encontrado");
        
        file.Status = "Archived";
        await _context.SaveChangesAsync();
        return file;
    }

    public async Task DeleteFileAsync(int id)
    {
        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            var file = await _context.TravelFiles
                .Include(f => f.Payments)
                .Include(f => f.Reservations)
                .Include(f => f.Passengers)
                .Include(f => f.FlightSegments)
                .Include(f => f.HotelBookings)
                .Include(f => f.TransferBookings)
                .Include(f => f.PackageBookings)
                .FirstOrDefaultAsync(f => f.Id == id);

            if (file == null) throw new KeyNotFoundException("File no encontrado");

            if (file.Status != FileStatus.Budget)
            {
                throw new InvalidOperationException("Solo se pueden eliminar Files en estado Presupuesto.");
            }

            if (file.Payments.Any())
            {
                throw new InvalidOperationException("No se puede eliminar un File con pagos registrados. Elimine los pagos primero.");
            }

            if (file.Reservations.Any()) _context.Reservations.RemoveRange(file.Reservations);
            if (file.Passengers.Any()) _context.Passengers.RemoveRange(file.Passengers);
            if (file.FlightSegments.Any()) _context.FlightSegments.RemoveRange(file.FlightSegments);
            if (file.HotelBookings.Any()) _context.HotelBookings.RemoveRange(file.HotelBookings);
            if (file.TransferBookings.Any()) _context.TransferBookings.RemoveRange(file.TransferBookings);
            if (file.PackageBookings.Any()) _context.PackageBookings.RemoveRange(file.PackageBookings);

            _context.TravelFiles.Remove(file);
            
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (Exception)
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
