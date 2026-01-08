using System.Data;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Services;

public class CupoAllocationService
{
    private readonly AppDbContext _dbContext;

    public CupoAllocationService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CupoAssignment> AllocateAsync(int cupoId, int quantity, int? reservationId, CancellationToken cancellationToken)
    {
        if (quantity <= 0)
        {
            throw new CupoOverbookingException("La cantidad solicitada debe ser mayor a cero.");
        }

        var executionStrategy = _dbContext.Database.CreateExecutionStrategy();

        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            var cupo = await _dbContext.Cupos
                .FirstOrDefaultAsync(item => item.Id == cupoId, cancellationToken);

            if (cupo is null)
            {
                throw new CupoNotFoundException(cupoId);
            }

            var maxAllowed = cupo.Capacity + cupo.OverbookingLimit;
            var available = maxAllowed - cupo.Reserved;

            if (quantity > available)
            {
                throw new CupoOverbookingException("El cupo no tiene disponibilidad suficiente.");
            }

            cupo.Reserved += quantity;
            cupo.RowVersion = Guid.NewGuid();

            var assignment = new CupoAssignment
            {
                CupoId = cupo.Id,
                ReservationId = reservationId,
                Quantity = quantity,
                AssignedAt = DateTime.UtcNow
            };

            _dbContext.CupoAssignments.Add(assignment);

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new CupoConcurrencyException();
            }

            return assignment;
        });
    }
}
