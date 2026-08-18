using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Filters;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Decision firmada 2026-08-18 (Gaston): estos tests corren <see cref="NotificarFalloDeResolucionAlUsuarioAttribute"/>
/// como lo haria ASP.NET Core (armando a mano <see cref="ActionExecutingContext"/>/<see cref="ActionExecutedContext"/>,
/// sin levantar un servidor HTTP real), para probar la parte que <c>ServiceResolutionFailureNotifierTests</c>
/// NO cubre: que el filter lea bien el resultado de los 7 endpoints (400/409 con "message", 2xx, excepcion
/// tecnica) y que un fallo del filter mismo jamas cambie la respuesta que el vendedor ya recibio.
/// </summary>
public class NotificarFalloDeResolucionAlUsuarioAttributeTests
{
    private const string ServiceIdRouteKey = "id";
    private const string ServiceIdValue = "501";

    private static (ActionExecutingContext executing, ActionContext actionContext) BuildExecutingContext(IServiceProvider services)
    {
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var routeData = new RouteData();
        routeData.Values[ServiceIdRouteKey] = ServiceIdValue;
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var executing = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());

        return (executing, actionContext);
    }

    private static (ActionExecutionDelegate next, ActionExecutedContext executed) NextReturning(
        ActionContext actionContext, IActionResult? result, Exception? exception = null)
    {
        var executed = new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), controller: new object())
        {
            Result = result,
            Exception = exception,
        };
        return (() => Task.FromResult(executed), executed);
    }

    private static IServiceProvider BuildServices(IServiceResolutionFailureNotifier? notifier)
    {
        var services = new ServiceCollection();
        if (notifier != null)
        {
            services.AddSingleton(notifier);
        }
        return services.BuildServiceProvider();
    }

    // ============================================================
    // A) Rechazo de negocio (400/409 con "message") -> el filter avisa el FALLO.
    // ============================================================

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status409Conflict)]
    public async Task BusinessRejection_400Or409WithMessage_CallsNotifyFailure(int statusCode)
    {
        var notifierMock = new Mock<IServiceResolutionFailureNotifier>();
        var services = BuildServices(notifierMock.Object);
        var (executing, actionContext) = BuildExecutingContext(services);
        var rejection = new ObjectResult(new { message = "El operador rechazó la confirmación." }) { StatusCode = statusCode };
        var (next, _) = NextReturning(actionContext, rejection);

        var attribute = new NotificarFalloDeResolucionAlUsuarioAttribute(ServiceResolutionKind.FlightSegment, ServiceIdRouteKey);
        await attribute.OnActionExecutionAsync(executing, next);

        notifierMock.Verify(n => n.NotifyFailureAsync(
            ServiceResolutionKind.FlightSegment, ServiceIdValue, "El operador rechazó la confirmación.", It.IsAny<CancellationToken>()),
            Times.Once);
        notifierMock.Verify(n => n.NotifyResolvedAsync(It.IsAny<ServiceResolutionKind>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NotFound_DoesNotCallNotifier_AtAll()
    {
        // 404: el servicio/reserva no existe. Distinto de "fallo de resolucion" - no hay nada que avisar.
        var notifierMock = new Mock<IServiceResolutionFailureNotifier>();
        var services = BuildServices(notifierMock.Object);
        var (executing, actionContext) = BuildExecutingContext(services);
        var (next, _) = NextReturning(actionContext, new NotFoundResult());

        var attribute = new NotificarFalloDeResolucionAlUsuarioAttribute(ServiceResolutionKind.FlightSegment, ServiceIdRouteKey);
        await attribute.OnActionExecutionAsync(executing, next);

        notifierMock.VerifyNoOtherCalls();
    }

    // ============================================================
    // B) Exito (2xx) -> el filter avisa que se RESOLVIO (apaga un aviso viejo si habia). Nunca crea nada nuevo.
    // ============================================================

    [Fact]
    public async Task Success_CallsNotifyResolved_NeverNotifyFailure()
    {
        var notifierMock = new Mock<IServiceResolutionFailureNotifier>();
        var services = BuildServices(notifierMock.Object);
        var (executing, actionContext) = BuildExecutingContext(services);
        var (next, _) = NextReturning(actionContext, new OkObjectResult(new { status = "Confirmado" }));

        var attribute = new NotificarFalloDeResolucionAlUsuarioAttribute(ServiceResolutionKind.FlightSegment, ServiceIdRouteKey);
        await attribute.OnActionExecutionAsync(executing, next);

        notifierMock.Verify(n => n.NotifyResolvedAsync(ServiceResolutionKind.FlightSegment, ServiceIdValue, It.IsAny<CancellationToken>()), Times.Once);
        notifierMock.Verify(n => n.NotifyFailureAsync(It.IsAny<ServiceResolutionKind>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ============================================================
    // C) Excepcion TECNICA (no ValidationException) sin atrapar -> el filter NO hace nada (su mensaje no
    //    es apto para usuario, avisarla seria fuga de datos tecnicos hacia la campanita).
    // ============================================================

    [Fact]
    public async Task UnhandledTechnicalException_NeverValidationException_DoesNotCallNotifier()
    {
        var notifierMock = new Mock<IServiceResolutionFailureNotifier>();
        var services = BuildServices(notifierMock.Object);
        var (executing, actionContext) = BuildExecutingContext(services);
        var (next, _) = NextReturning(actionContext, result: null, exception: new InvalidOperationException("boom tecnico"));

        var attribute = new NotificarFalloDeResolucionAlUsuarioAttribute(ServiceResolutionKind.FlightSegment, ServiceIdRouteKey);
        await attribute.OnActionExecutionAsync(executing, next);

        notifierMock.VerifyNoOtherCalls();
    }

    // ============================================================
    // D) Red de seguridad: ValidationException sin atrapar SI avisa (y no se toca la excepcion:
    //    GlobalExceptionHandler sigue respondiendo el 400 como siempre).
    // ============================================================

    [Fact]
    public async Task UnhandledValidationException_CallsNotifyFailure_AndDoesNotTouchTheException()
    {
        var notifierMock = new Mock<IServiceResolutionFailureNotifier>();
        var services = BuildServices(notifierMock.Object);
        var (executing, actionContext) = BuildExecutingContext(services);
        var validationEx = new ValidationException("El costo no puede ser negativo.");
        var (next, executed) = NextReturning(actionContext, result: null, exception: validationEx);

        var attribute = new NotificarFalloDeResolucionAlUsuarioAttribute(ServiceResolutionKind.FlightSegment, ServiceIdRouteKey);
        await attribute.OnActionExecutionAsync(executing, next);

        notifierMock.Verify(n => n.NotifyFailureAsync(
            ServiceResolutionKind.FlightSegment, ServiceIdValue, "El costo no puede ser negativo.", It.IsAny<CancellationToken>()),
            Times.Once);
        // El filter solo OBSERVA: no toca la excepcion ni la marca "handled" — sigue siendo
        // GlobalExceptionHandler quien responde el 400, como con cualquier otra excepcion sin atrapar.
        Assert.Same(validationEx, executed.Exception);
        Assert.False(executed.ExceptionHandled);
    }

    // ============================================================
    // E) Blindaje: si el propio filter no puede resolver el notifier (DI mal configurado), la respuesta
    //    que el endpoint ya armo NO se toca y no se tira ninguna excepcion nueva.
    // ============================================================

    [Fact]
    public async Task NotifierNotRegistered_DoesNotThrow_AndLeavesOriginalResponseUntouched()
    {
        var services = BuildServices(notifier: null); // simula DI mal configurado.
        var (executing, actionContext) = BuildExecutingContext(services);
        var conflict = new ObjectResult(new { message = "rechazo" }) { StatusCode = StatusCodes.Status409Conflict };
        var (next, executed) = NextReturning(actionContext, conflict);

        var attribute = new NotificarFalloDeResolucionAlUsuarioAttribute(ServiceResolutionKind.FlightSegment, ServiceIdRouteKey);
        var exception = await Record.ExceptionAsync(() => attribute.OnActionExecutionAsync(executing, next));

        Assert.Null(exception);
        // La respuesta original (el 409 que ya armo el controller) sigue intacta: el filter nunca la toca.
        Assert.Same(conflict, executed.Result);
    }
}
