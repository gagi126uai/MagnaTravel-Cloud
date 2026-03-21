using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class OperationalFinanceSettingsService : IOperationalFinanceSettingsService
{
    private readonly AppDbContext _dbContext;

    public OperationalFinanceSettingsService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<OperationalFinanceSettingsDto> GetAsync(CancellationToken cancellationToken)
    {
        var entity = await GetEntityAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<OperationalFinanceSettingsDto> UpdateAsync(OperationalFinanceSettingsDto request, CancellationToken cancellationToken)
    {
        var entity = await GetEntityAsync(cancellationToken);
        entity.RequireFullPaymentForOperativeStatus = request.RequireFullPaymentForOperativeStatus;
        entity.RequireFullPaymentForVoucher = request.RequireFullPaymentForVoucher;
        entity.AfipInvoiceControlMode = string.IsNullOrWhiteSpace(request.AfipInvoiceControlMode)
            ? AfipInvoiceControlModes.AllowAgentOverrideWithReason
            : request.AfipInvoiceControlMode;
        entity.EnableUpcomingUnpaidReservationNotifications = request.EnableUpcomingUnpaidReservationNotifications;
        entity.UpcomingUnpaidReservationAlertDays = Math.Clamp(request.UpcomingUnpaidReservationAlertDays, 1, 60);
        entity.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<OperationalFinanceSettings> GetEntityAsync(CancellationToken cancellationToken)
    {
        var entity = await _dbContext.OperationalFinanceSettings
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (entity != null)
            return entity;

        entity = new OperationalFinanceSettings();
        _dbContext.OperationalFinanceSettings.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return entity;
    }

    private static OperationalFinanceSettingsDto Map(OperationalFinanceSettings entity)
    {
        return new OperationalFinanceSettingsDto
        {
            RequireFullPaymentForOperativeStatus = entity.RequireFullPaymentForOperativeStatus,
            RequireFullPaymentForVoucher = entity.RequireFullPaymentForVoucher,
            AfipInvoiceControlMode = entity.AfipInvoiceControlMode,
            EnableUpcomingUnpaidReservationNotifications = entity.EnableUpcomingUnpaidReservationNotifications,
            UpcomingUnpaidReservationAlertDays = entity.UpcomingUnpaidReservationAlertDays
        };
    }
}
