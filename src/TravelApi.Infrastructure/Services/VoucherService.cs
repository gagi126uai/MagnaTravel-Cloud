using System.Text;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class VoucherService : IVoucherService
{
    private readonly AppDbContext _db;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;

    public VoucherService(AppDbContext db, IOperationalFinanceSettingsService operationalFinanceSettingsService)
    {
        _db = db;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public async Task<byte[]> GenerateVoucherHtmlAsync(int reservaId, CancellationToken cancellationToken)
    {
        var (reserva, agency) = await LoadVoucherDataAsync(reservaId, cancellationToken);

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

        html.AppendLine("<div class='header'>");
        html.AppendLine($"<div><h1>{agency?.AgencyName ?? "Agencia de Viajes"}</h1>");
        html.AppendLine($"<p style='color:#64748b;font-size:12px'>{agency?.Address ?? ""} | {agency?.Phone ?? ""}</p></div>");
        html.AppendLine($"<div style='text-align:right'><div style='font-size:12px;color:#94a3b8'>RESERVA</div>");
        html.AppendLine($"<div style='font-size:20px;font-weight:800;color:#4f46e5'>{reserva.NumeroReserva}</div></div>");
        html.AppendLine("</div>");

        html.AppendLine("<h2>Datos del Pasajero</h2>");
        html.AppendLine("<div class='info-grid'>");
        html.AppendLine($"<div class='info-item'><div class='info-label'>Titular</div><div class='info-value'>{reserva.Payer?.FullName ?? "---"}</div></div>");
        html.AppendLine($"<div class='info-item'><div class='info-label'>Documento</div><div class='info-value'>{reserva.Payer?.DocumentNumber ?? "---"}</div></div>");
        html.AppendLine($"<div class='info-item'><div class='info-label'>Salida</div><div class='info-value'>{reserva.StartDate?.ToString("dd/MM/yyyy") ?? "---"}</div></div>");
        html.AppendLine($"<div class='info-item'><div class='info-label'>Regreso</div><div class='info-value'>{reserva.EndDate?.ToString("dd/MM/yyyy") ?? "---"}</div></div>");
        html.AppendLine("</div>");

        AppendPassengers(reserva, html);
        AppendHotels(reserva, html);
        AppendFlights(reserva, html);
        AppendTransfers(reserva, html);
        AppendPackages(reserva, html);

        html.AppendLine("<div class='footer'>");
        html.AppendLine($"<p>Voucher generado el {DateTime.UtcNow.AddHours(-3):dd/MM/yyyy HH:mm} hs (Argentina)</p>");
        html.AppendLine($"<p><strong>{agency?.AgencyName ?? "MagnaTravel"}</strong> | {agency?.Email ?? ""} | {agency?.Phone ?? ""}</p>");
        html.AppendLine("<p style='margin-top:8px;font-style:italic'>Este documento no tiene validez como comprobante fiscal.</p>");
        html.AppendLine("</div></body></html>");

        return Encoding.UTF8.GetBytes(html.ToString());
    }

    public async Task<byte[]> GenerateVoucherPdfAsync(int reservaId, CancellationToken cancellationToken)
    {
        var (reserva, agency) = await LoadVoucherDataAsync(reservaId, cancellationToken);
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);
        var blockReason = EconomicRulesHelper.GetVoucherBlockReason(reserva, settings);
        if (!string.IsNullOrWhiteSpace(blockReason))
            throw new InvalidOperationException(blockReason);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Element(header => ComposeHeader(header, reserva, agency));
                page.Content().Element(content => ComposeContent(content, reserva));
                page.Footer()
                    .AlignCenter()
                    .DefaultTextStyle(x => x.FontSize(9).FontColor(Colors.Grey.Darken1))
                    .Text(text =>
                    {
                        text.Span($"Voucher generado el {DateTime.UtcNow.AddHours(-3):dd/MM/yyyy HH:mm} hs");
                        text.Span(" | ");
                        text.Span(agency?.AgencyName ?? "MagnaTravel").SemiBold();
                    });
            });
        });

        return document.GeneratePdf();
    }

    private async Task<(Reserva Reserva, AgencySettings? Agency)> LoadVoucherDataAsync(int reservaId, CancellationToken cancellationToken)
    {
        var reserva = await _db.Reservas
            .Include(f => f.Payer)
            .Include(f => f.Passengers)
            .Include(f => f.HotelBookings).ThenInclude(h => h.Supplier)
            .Include(f => f.FlightSegments)
            .Include(f => f.TransferBookings).ThenInclude(t => t.Supplier)
            .Include(f => f.PackageBookings).ThenInclude(p => p.Supplier)
            .FirstOrDefaultAsync(f => f.Id == reservaId, cancellationToken)
            ?? throw new KeyNotFoundException($"Reserva {reservaId} no encontrada.");

        var agency = await _db.AgencySettings.FirstOrDefaultAsync(cancellationToken);
        return (reserva, agency);
    }

    private static void ComposeHeader(IContainer container, Reserva reserva, AgencySettings? agency)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    left.Item().Text(agency?.AgencyName ?? "Agencia de Viajes").FontSize(22).Bold().FontColor(Colors.Blue.Medium);
                    if (!string.IsNullOrWhiteSpace(agency?.Address))
                        left.Item().Text(agency.Address).FontSize(9).FontColor(Colors.Grey.Darken1);
                    if (!string.IsNullOrWhiteSpace(agency?.Phone) || !string.IsNullOrWhiteSpace(agency?.Email))
                        left.Item().Text($"{agency?.Phone ?? "-"} | {agency?.Email ?? "-"}").FontSize(9).FontColor(Colors.Grey.Darken1);
                });

                row.ConstantItem(170).AlignRight().Column(right =>
                {
                    right.Item().Text("VOUCHER").FontSize(20).Bold();
                    right.Item().Text($"Reserva {reserva.NumeroReserva}").FontSize(12).SemiBold().FontColor(Colors.Blue.Medium);
                    right.Item().Text($"Salida: {FormatDate(reserva.StartDate)}").FontSize(9);
                    right.Item().Text($"Regreso: {FormatDate(reserva.EndDate)}").FontSize(9);
                });
            });

            column.Item().PaddingTop(10).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
        });
    }

    private static void ComposeContent(IContainer container, Reserva reserva)
    {
        container.Column(column =>
        {
            column.Spacing(12);

            column.Item().Element(card =>
            {
                card.Border(1).BorderColor(Colors.Grey.Lighten2).Padding(10).Column(details =>
                {
                    details.Item().Text("Datos del pasajero").SemiBold().FontSize(13);
                    details.Item().Text($"Titular: {reserva.Payer?.FullName ?? "---"}");
                    details.Item().Text($"Documento: {reserva.Payer?.DocumentNumber ?? "---"}");
                });
            });

            if (reserva.Passengers.Any())
                column.Item().Element(x => ComposeSimpleTable(x, "Pasajeros", new[] { "Nombre", "Documento", "Nacimiento" },
                    reserva.Passengers.Select(p => new[]
                    {
                        p.FullName,
                        $"{p.DocumentType} {p.DocumentNumber}".Trim(),
                        FormatDate(p.BirthDate)
                    })));

            if (reserva.HotelBookings.Any())
                column.Item().Element(x => ComposeSimpleTable(x, "Alojamiento", new[] { "Hotel", "Fechas", "Habitacion", "Confirmacion" },
                    reserva.HotelBookings.Select(h => new[]
                    {
                        $"{h.HotelName} ({h.City})",
                        $"{FormatDate(h.CheckIn)} - {FormatDate(h.CheckOut)}",
                        $"{h.RoomType} / {h.MealPlan}",
                        h.ConfirmationNumber ?? "Pendiente"
                    })));

            if (reserva.FlightSegments.Any())
                column.Item().Element(x => ComposeSimpleTable(x, "Vuelos", new[] { "Vuelo", "Ruta", "Salida", "PNR" },
                    reserva.FlightSegments.Select(f => new[]
                    {
                        $"{f.AirlineCode} {f.FlightNumber}",
                        $"{f.OriginCity ?? f.Origin} -> {f.DestinationCity ?? f.Destination}",
                        f.DepartureTime.ToString("dd/MM/yyyy HH:mm"),
                        f.PNR ?? "---"
                    })));

            if (reserva.TransferBookings.Any())
                column.Item().Element(x => ComposeSimpleTable(x, "Traslados", new[] { "Vehiculo", "Ruta", "Fecha", "Confirmacion" },
                    reserva.TransferBookings.Select(t => new[]
                    {
                        t.VehicleType,
                        $"{t.PickupLocation} -> {t.DropoffLocation}",
                        t.PickupDateTime.ToString("dd/MM/yyyy HH:mm"),
                        t.ConfirmationNumber ?? "Pendiente"
                    })));

            if (reserva.PackageBookings.Any())
                column.Item().Element(x => ComposeSimpleTable(x, "Paquetes", new[] { "Paquete", "Destino", "Fechas", "Confirmacion" },
                    reserva.PackageBookings.Select(p => new[]
                    {
                        p.PackageName,
                        p.Destination,
                        $"{FormatDate(p.StartDate)} - {FormatDate(p.EndDate)}",
                        p.ConfirmationNumber ?? "Pendiente"
                    })));

            column.Item().PaddingTop(6).Text("Este documento no tiene validez como comprobante fiscal.")
                .Italic().FontSize(9).FontColor(Colors.Grey.Darken1);
        });
    }

    private static void ComposeSimpleTable(IContainer container, string title, string[] headers, IEnumerable<string[]> rows)
    {
        container.Column(column =>
        {
            column.Item().Text(title).SemiBold().FontSize(13);
            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    foreach (var _ in headers)
                        columns.RelativeColumn();
                });

                table.Header(header =>
                {
                    foreach (var item in headers)
                    {
                        header.Cell().Element(CellHeaderStyle).Text(item);
                    }
                });

                foreach (var row in rows)
                {
                    foreach (var value in row)
                    {
                        table.Cell().Element(CellBodyStyle).Text(value);
                    }
                }
            });
        });

        static IContainer CellHeaderStyle(IContainer container) =>
            container.Background(Colors.Grey.Lighten3).Padding(6).DefaultTextStyle(x => x.SemiBold().FontSize(9));

        static IContainer CellBodyStyle(IContainer container) =>
            container.BorderBottom(1).BorderColor(Colors.Grey.Lighten3).Padding(6).DefaultTextStyle(x => x.FontSize(9));
    }

    private static void AppendPassengers(Reserva reserva, StringBuilder html)
    {
        if (!reserva.Passengers.Any())
            return;

        html.AppendLine("<h2>Pasajeros</h2><table><thead><tr><th>Nombre</th><th>Documento</th><th>Nacimiento</th></tr></thead><tbody>");
        foreach (var passenger in reserva.Passengers)
        {
            html.AppendLine($"<tr><td>{passenger.FullName}</td><td>{passenger.DocumentType} {passenger.DocumentNumber}</td><td>{FormatDate(passenger.BirthDate)}</td></tr>");
        }
        html.AppendLine("</tbody></table>");
    }

    private static void AppendHotels(Reserva reserva, StringBuilder html)
    {
        if (!reserva.HotelBookings.Any())
            return;

        html.AppendLine("<h2>Alojamiento</h2><table><thead><tr><th>Hotel</th><th>Check-In</th><th>Check-Out</th><th>Noches</th><th>Habitacion</th><th>Confirmacion</th></tr></thead><tbody>");
        foreach (var hotel in reserva.HotelBookings)
        {
            html.AppendLine($"<tr><td><strong>{hotel.HotelName}</strong><br/><span style='font-size:11px;color:#64748b'>{hotel.City}</span></td><td>{hotel.CheckIn:dd/MM/yyyy}</td><td>{hotel.CheckOut:dd/MM/yyyy}</td><td>{hotel.Nights}</td><td>{hotel.RoomType} ({hotel.MealPlan})</td><td>{hotel.ConfirmationNumber ?? "Pendiente"}</td></tr>");
        }
        html.AppendLine("</tbody></table>");
    }

    private static void AppendFlights(Reserva reserva, StringBuilder html)
    {
        if (!reserva.FlightSegments.Any())
            return;

        html.AppendLine("<h2>Vuelos</h2><table><thead><tr><th>Vuelo</th><th>Origen</th><th>Destino</th><th>Fecha</th><th>Clase</th><th>PNR</th></tr></thead><tbody>");
        foreach (var flight in reserva.FlightSegments)
        {
            html.AppendLine($"<tr><td>{flight.AirlineCode} {flight.FlightNumber}</td><td>{flight.OriginCity ?? flight.Origin}</td><td>{flight.DestinationCity ?? flight.Destination}</td><td>{flight.DepartureTime:dd/MM/yyyy HH:mm}</td><td>{flight.CabinClass}</td><td>{flight.PNR ?? "---"}</td></tr>");
        }
        html.AppendLine("</tbody></table>");
    }

    private static void AppendTransfers(Reserva reserva, StringBuilder html)
    {
        if (!reserva.TransferBookings.Any())
            return;

        html.AppendLine("<h2>Transfers</h2><table><thead><tr><th>Vehiculo</th><th>Origen</th><th>Destino</th><th>Fecha</th><th>Confirmacion</th></tr></thead><tbody>");
        foreach (var transfer in reserva.TransferBookings)
        {
            html.AppendLine($"<tr><td>{transfer.VehicleType}</td><td>{transfer.PickupLocation}</td><td>{transfer.DropoffLocation}</td><td>{transfer.PickupDateTime:dd/MM/yyyy HH:mm}</td><td>{transfer.ConfirmationNumber ?? "Pendiente"}</td></tr>");
        }
        html.AppendLine("</tbody></table>");
    }

    private static void AppendPackages(Reserva reserva, StringBuilder html)
    {
        if (!reserva.PackageBookings.Any())
            return;

        html.AppendLine("<h2>Paquetes</h2><table><thead><tr><th>Paquete</th><th>Destino</th><th>Fechas</th><th>Noches</th><th>Confirmacion</th></tr></thead><tbody>");
        foreach (var package in reserva.PackageBookings)
        {
            html.AppendLine($"<tr><td>{package.PackageName}</td><td>{package.Destination}</td><td>{package.StartDate:dd/MM/yyyy} - {package.EndDate:dd/MM/yyyy}</td><td>{package.Nights}</td><td>{package.ConfirmationNumber ?? "Pendiente"}</td></tr>");
        }
        html.AppendLine("</tbody></table>");
    }

    private static string FormatDate(DateTime? date)
    {
        return date?.ToString("dd/MM/yyyy") ?? "---";
    }
}
