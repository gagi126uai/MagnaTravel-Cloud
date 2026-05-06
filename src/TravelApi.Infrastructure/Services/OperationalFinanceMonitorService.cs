using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class OperationalFinanceMonitorService
{
    private readonly AppDbContext _dbContext;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;
    private readonly INotificationService _notificationService;
    private readonly UserManager<ApplicationUser> _userManager;

    public OperationalFinanceMonitorService(
        AppDbContext dbContext,
        IOperationalFinanceSettingsService operationalFinanceSettingsService,
        INotificationService notificationService,
        UserManager<ApplicationUser> userManager)
    {
        _dbContext = dbContext;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
        _notificationService = notificationService;
        _userManager = userManager;
    }

    public async Task GenerateUpcomingUnpaidReservationNotificationsAsync()
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(CancellationToken.None);
        if (!settings.EnableUpcomingUnpaidReservationNotifications)
            return;

        var today = DateTime.UtcNow.Date;
        var threshold = today.AddDays(Math.Max(settings.UpcomingUnpaidReservationAlertDays, 1));

        var reservas = await _dbContext.Reservas
            .Include(r => r.Payer)
            .Where(r =>
                r.Balance > 0 &&
                r.StartDate.HasValue &&
                r.StartDate.Value.Date >= today &&
                r.StartDate.Value.Date <= threshold &&
                r.Status != EstadoReserva.Cancelled &&
                r.Status != EstadoReserva.Closed)
            .ToListAsync();

        if (reservas.Count == 0)
            return;

        var adminUsers = await _userManager.GetUsersInRoleAsync("Admin");

        foreach (var reserva in reservas)
        {
            var userIds = new HashSet<string>(adminUsers.Select(a => a.Id));
            if (!string.IsNullOrWhiteSpace(reserva.ResponsibleUserId))
                userIds.Add(reserva.ResponsibleUserId);

            foreach (var userId in userIds)
            {
                var alreadyExists = await _dbContext.Notifications.AnyAsync(n =>
                    n.UserId == userId &&
                    n.RelatedEntityType == "ReservaUnpaidDeparture" &&
                    n.RelatedEntityId == reserva.Id &&
                    n.CreatedAt.Date == today);

                if (alreadyExists)
                    continue;

                await _notificationService.CreateAndSendAsync(new Notification
                {
                    UserId = userId,
                    Type = "Warning",
                    Priority = "Urgent",
                    RelatedEntityId = reserva.Id,
                    RelatedEntityType = "ReservaUnpaidDeparture",
                    Message = $"La reserva {reserva.NumeroReserva} sale el {reserva.StartDate:dd/MM/yyyy} y mantiene un saldo pendiente de {reserva.Balance:C2}."
                });
            }
        }
    }
}
