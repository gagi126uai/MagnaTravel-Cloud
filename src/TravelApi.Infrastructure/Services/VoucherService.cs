using System.Text;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class VoucherService : IVoucherService
{
    private readonly AppDbContext _db;

    public VoucherService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<byte[]> GenerateVoucherAsync(int travelFileId, CancellationToken cancellationToken)
    {
        var file = await _db.TravelFiles
            .Include(f => f.Payer)
            .Include(f => f.Passengers)
            .Include(f => f.HotelBookings).ThenInclude(h => h.Supplier)
            .Include(f => f.FlightSegments)
            .Include(f => f.TransferBookings).ThenInclude(t => t.Supplier)
            .Include(f => f.PackageBookings).ThenInclude(p => p.Supplier)
            .FirstOrDefaultAsync(f => f.Id == travelFileId, cancellationToken)
            ?? throw new KeyNotFoundException($"Expediente {travelFileId} no encontrado.");

        var agency = await _db.AgencySettings.FirstOrDefaultAsync(cancellationToken);

        // Generate HTML voucher as PDF-ready content
        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        html.AppendLine("<style>");
        html.AppendLine("body { font-family: 'Segoe UI', Arial, sans-serif; margin: 40px; color: #1e293b; }");
        html.AppendLine("h1 { color: #4f46e5; font-size: 28px; margin-bottom: 5px; }");
        html.AppendLine("h2 { color: #334155; font-size: 18px; margin-top: 30px; border-bottom: 2px solid #e2e8f0; padding-bottom: 8px; }");
        html.AppendLine(".header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 30px; border-bottom: 3px solid #4f46e5; padding-bottom: 20px; }");
        html.AppendLine(".info-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 15px; margin: 15px 0; }");
        html.AppendLine(".info-item { background: #f8fafc; padding: 12px; border-radius: 8px; }");
        html.AppendLine(".info-label { font-size: 11px; color: #94a3b8; text-transform: uppercase; font-weight: 700; letter-spacing: 0.05em; }");
        html.AppendLine(".info-value { font-size: 14px; font-weight: 600; color: #1e293b; margin-top: 4px; }");
        html.AppendLine("table { width: 100%; border-collapse: collapse; margin: 10px 0; }");
        html.AppendLine("th { background: #f1f5f9; padding: 10px; text-align: left; font-size: 11px; text-transform: uppercase; color: #64748b; }");
        html.AppendLine("td { padding: 10px; border-bottom: 1px solid #e2e8f0; font-size: 13px; }");
        html.AppendLine(".footer { margin-top: 40px; text-align: center; font-size: 11px; color: #94a3b8; border-top: 1px solid #e2e8f0; padding-top: 20px; }");
        html.AppendLine("</style></head><body>");

        // Header
        html.AppendLine("<div class='header'>");
        html.AppendLine($"<div><h1>{agency?.AgencyName ?? "Agencia de Viajes"}</h1>");
        html.AppendLine($"<p style='color:#64748b;font-size:12px'>{agency?.Address ?? ""} | {agency?.Phone ?? ""}</p></div>");
        html.AppendLine($"<div style='text-align:right'><div style='font-size:12px;color:#94a3b8'>VOUCHER</div>");
        html.AppendLine($"<div style='font-size:20px;font-weight:800;color:#4f46e5'>{file.FileNumber}</div></div>");
        html.AppendLine("</div>");

        // Client info
        html.AppendLine("<h2>Datos del Pasajero</h2>");
        html.AppendLine("<div class='info-grid'>");
        html.AppendLine($"<div class='info-item'><div class='info-label'>Titular</div><div class='info-value'>{file.Payer?.FullName ?? "---"}</div></div>");
        html.AppendLine($"<div class='info-item'><div class='info-label'>Documento</div><div class='info-value'>{file.Payer?.DocumentNumber ?? "---"}</div></div>");
        html.AppendLine($"<div class='info-item'><div class='info-label'>Salida</div><div class='info-value'>{file.StartDate?.ToString("dd/MM/yyyy") ?? "---"}</div></div>");
        html.AppendLine($"<div class='info-item'><div class='info-label'>Regreso</div><div class='info-value'>{file.EndDate?.ToString("dd/MM/yyyy") ?? "---"}</div></div>");
        html.AppendLine("</div>");

        // Passengers
        if (file.Passengers.Any())
        {
            html.AppendLine("<h2>Pasajeros</h2><table><thead><tr><th>Nombre</th><th>Documento</th><th>Nacimiento</th></tr></thead><tbody>");
            foreach (var p in file.Passengers)
            {
                html.AppendLine($"<tr><td>{p.FullName}</td><td>{p.DocumentType} {p.DocumentNumber}</td><td>{p.BirthDate?.ToString("dd/MM/yyyy") ?? "---"}</td></tr>");
            }
            html.AppendLine("</tbody></table>");
        }

        // Hotels
        if (file.HotelBookings.Any())
        {
            html.AppendLine("<h2>Alojamiento</h2><table><thead><tr><th>Hotel</th><th>Check-In</th><th>Check-Out</th><th>Noches</th><th>Habitación</th><th>Confirmación</th></tr></thead><tbody>");
            foreach (var h in file.HotelBookings)
            {
                html.AppendLine($"<tr><td><strong>{h.HotelName}</strong><br/><span style='font-size:11px;color:#64748b'>{h.City}</span></td><td>{h.CheckIn:dd/MM/yyyy}</td><td>{h.CheckOut:dd/MM/yyyy}</td><td>{h.Nights}</td><td>{h.RoomType} ({h.MealPlan})</td><td>{h.ConfirmationNumber ?? "Pendiente"}</td></tr>");
            }
            html.AppendLine("</tbody></table>");
        }

        // Flights
        if (file.FlightSegments.Any())
        {
            html.AppendLine("<h2>Vuelos</h2><table><thead><tr><th>Vuelo</th><th>Origen</th><th>Destino</th><th>Fecha</th><th>Clase</th><th>PNR</th></tr></thead><tbody>");
            foreach (var f2 in file.FlightSegments)
            {
                html.AppendLine($"<tr><td>{f2.AirlineCode} {f2.FlightNumber}</td><td>{f2.OriginCity ?? f2.Origin}</td><td>{f2.DestinationCity ?? f2.Destination}</td><td>{f2.DepartureTime:dd/MM/yyyy HH:mm}</td><td>{f2.CabinClass}</td><td>{f2.PNR ?? "---"}</td></tr>");
            }
            html.AppendLine("</tbody></table>");
        }

        // Transfers
        if (file.TransferBookings.Any())
        {
            html.AppendLine("<h2>Transfers</h2><table><thead><tr><th>Vehículo</th><th>Origen</th><th>Destino</th><th>Fecha</th><th>Confirmación</th></tr></thead><tbody>");
            foreach (var t in file.TransferBookings)
            {
                html.AppendLine($"<tr><td>{t.VehicleType}</td><td>{t.PickupLocation}</td><td>{t.DropoffLocation}</td><td>{t.PickupDateTime:dd/MM/yyyy HH:mm}</td><td>{t.ConfirmationNumber ?? "Pendiente"}</td></tr>");
            }
            html.AppendLine("</tbody></table>");
        }

        // Packages
        if (file.PackageBookings.Any())
        {
            html.AppendLine("<h2>Paquetes</h2><table><thead><tr><th>Paquete</th><th>Destino</th><th>Fechas</th><th>Noches</th><th>Confirmación</th></tr></thead><tbody>");
            foreach (var p in file.PackageBookings)
            {
                html.AppendLine($"<tr><td>{p.PackageName}</td><td>{p.Destination}</td><td>{p.StartDate:dd/MM/yyyy} - {p.EndDate:dd/MM/yyyy}</td><td>{p.Nights}</td><td>{p.ConfirmationNumber ?? "Pendiente"}</td></tr>");
            }
            html.AppendLine("</tbody></table>");
        }

        // Footer
        html.AppendLine("<div class='footer'>");
        html.AppendLine($"<p>Voucher generado el {DateTime.UtcNow.AddHours(-3):dd/MM/yyyy HH:mm} hs (Argentina)</p>");
        html.AppendLine($"<p><strong>{agency?.AgencyName ?? "MagnaTravel"}</strong> | {agency?.Email ?? ""} | {agency?.Phone ?? ""}</p>");
        html.AppendLine("<p style='margin-top:8px;font-style:italic'>Este documento no tiene validez como comprobante fiscal.</p>");
        html.AppendLine("</div></body></html>");

        return Encoding.UTF8.GetBytes(html.ToString());
    }
}
