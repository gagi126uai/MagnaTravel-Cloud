using System.Reflection;
using TravelApi.Authorization;
using TravelApi.Controllers;
using TravelApi.Domain.Entities;
using Xunit;

namespace TravelApi.Tests.Unit;

public class BudgetEntryPointAuthorizationTests
{
    [Theory]
    [InlineData(typeof(LeadsController), nameof(LeadsController.CreateBudget))]
    [InlineData(typeof(QuotesController), nameof(QuotesController.ConvertToFile))]
    public void BudgetEntryPoint_RequiresCrmAndReservaEdit_WithOwnership(Type controller, string methodName)
    {
        var method = controller.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);

        var policies = method!.GetCustomAttributes<RequirePermissionAttribute>()
            .Select(attribute => attribute.Policy)
            .ToArray();
        Assert.Contains($"{RequirePermissionAttribute.PolicyPrefix}{Permissions.CrmEdit}", policies);
        Assert.Contains($"{RequirePermissionAttribute.PolicyPrefix}{Permissions.ReservasEdit}", policies);
        Assert.NotNull(method.GetCustomAttribute<RequireOwnershipAttribute>());
    }
}
