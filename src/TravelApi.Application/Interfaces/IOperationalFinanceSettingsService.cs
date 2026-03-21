using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface IOperationalFinanceSettingsService
{
    Task<OperationalFinanceSettingsDto> GetAsync(CancellationToken cancellationToken);
    Task<OperationalFinanceSettingsDto> UpdateAsync(OperationalFinanceSettingsDto request, CancellationToken cancellationToken);
    Task<OperationalFinanceSettings> GetEntityAsync(CancellationToken cancellationToken);
}
