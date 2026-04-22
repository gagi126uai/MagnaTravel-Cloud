using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using Xunit;

namespace TravelApi.Tests.Architecture;

public class ArchitectureGuardTests
{
    [Fact]
    public void Controllers_Should_Not_Depend_On_DbContext_Or_Concrete_ReferenceResolver()
    {
        var forbiddenTypes = new[]
        {
            typeof(AppDbContext),
            typeof(EntityReferenceResolver)
        };

        var offenders = typeof(global::Program).Assembly
            .GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .SelectMany(type => type.GetConstructors().Select(ctor => new { Type = type, Constructor = ctor }))
            .Where(item => item.Constructor.GetParameters().Any(parameter => forbiddenTypes.Contains(parameter.ParameterType)))
            .Select(item => $"{item.Type.FullName}({string.Join(", ", item.Constructor.GetParameters().Select(parameter => parameter.ParameterType.Name))})")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Controllers should depend on application ports instead of persistence details. Offenders: {string.Join("; ", offenders)}");
    }

    [Fact]
    public void Application_Assembly_Should_Not_Reference_Infrastructure()
    {
        var references = typeof(ILeadService).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("TravelApi.Infrastructure", references);
    }

    [Fact]
    public void Domain_Assembly_Should_Not_Reference_Application_Or_Infrastructure()
    {
        var references = typeof(Lead).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("TravelApi.Application", references);
        Assert.DoesNotContain("TravelApi.Infrastructure", references);
    }

    [Fact]
    public void Application_Should_Not_Contain_SignalR_Hubs()
    {
        var offenders = typeof(ILeadService).Assembly
            .GetTypes()
            .Where(type => type != typeof(Hub) && typeof(Hub).IsAssignableFrom(type))
            .Select(type => type.FullName ?? type.Name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Application should stay free of ASP.NET SignalR hubs. Offenders: {string.Join("; ", offenders)}");
    }

    [Fact]
    public void Application_Assembly_Should_Not_Reference_AspNetCore()
    {
        var references = typeof(ILeadService).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        Assert.DoesNotContain(
            references,
            name => name!.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase));
    }
}
