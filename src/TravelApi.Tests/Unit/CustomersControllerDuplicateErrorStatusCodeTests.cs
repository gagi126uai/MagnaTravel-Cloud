using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Controllers;
using TravelApi.Domain.Entities;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// B4 (plan 2026-07-31 tarde, deuda 31/07): un cliente duplicado (documento/email que ya usa OTRO
/// cliente, detectado por el indice unico de Postgres via <see cref="DbUpdateException"/>) es el MISMO
/// error de negocio tanto al CREAR como al EDITAR. Antes el alta lo devolvia como 409 (Conflict) y la
/// edicion como 400 (BadRequest) — dos varas para lo mismo. Este test fija que las DOS puertas devuelven
/// 409, con el mismo texto criollo de siempre (no se toca el mensaje, solo el codigo).
/// </summary>
public class CustomersControllerDuplicateErrorStatusCodeTests
{
    private static CustomersController BuildController(
        Mock<ICustomerService> customerService,
        Mock<IEntityReferenceResolver>? entityReferenceResolver = null)
    {
        var resolver = entityReferenceResolver ?? new Mock<IEntityReferenceResolver>();
        var clientCreditService = new Mock<IClientCreditService>();
        return new CustomersController(customerService.Object, resolver.Object, clientCreditService.Object);
    }

    private static CustomerUpsertRequest ValidRequest() => new(
        FullName: "Juan Perez",
        Email: null,
        Phone: null,
        DocumentType: "DNI",
        DocumentNumber: "12345678",
        Address: null,
        Notes: null,
        TaxId: null,
        TaxCondition: null,
        TaxConditionId: null);

    [Fact]
    public async Task CreateCustomer_DocumentoDuplicado_Devuelve409()
    {
        var customerService = new Mock<ICustomerService>();
        customerService
            .Setup(s => s.CreateCustomerAsync(It.IsAny<Customer>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("duplicado"));
        var controller = BuildController(customerService);

        var result = await controller.CreateCustomer(ValidRequest(), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

        // Q1 (tanda Q, gap detectado): el test original solo miraba el status code. T-6 pide
        // fijar el TEXTO exacto que ve el vendedor, y T-2/T-5 piden que ese texto NUNCA traiga
        // jerga de programador (nombre de excepcion, stack trace, palabras en ingles tecnico).
        var message = GetMessage(conflict.Value);
        Assert.Equal("No se pudo crear el cliente. Verifica que el documento y el email no esten duplicados.", message);
        AssertMensajeSinJergaTecnica(message);
    }

    [Fact]
    public async Task UpdateCustomer_DocumentoDuplicado_Devuelve409_MismaVaraQueElAlta()
    {
        // ANTES de este fix, esta misma situacion devolvia 400 (BadRequest) en vez de 409 — la
        // inconsistencia que arregla B4.
        var customerService = new Mock<ICustomerService>();
        customerService
            .Setup(s => s.UpdateCustomerAsync(It.IsAny<int>(), It.IsAny<Customer>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("duplicado"));

        var resolver = new Mock<IEntityReferenceResolver>();
        resolver
            .Setup(r => r.ResolveRequiredIdAsync<Customer>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var controller = BuildController(customerService, resolver);

        var result = await controller.UpdateCustomer("cliente-publicid", ValidRequest(), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

        // Mismo texto que el alta (B4: "misma vara"), no una redaccion distinta para la edicion.
        var message = GetMessage(conflict.Value);
        Assert.Equal("No se pudo actualizar el cliente. Verifica que el documento y el email no esten duplicados.", message);
        AssertMensajeSinJergaTecnica(message);
    }

    /// <summary>Extrae el campo "message" del objeto anonimo que devuelve el controller.</summary>
    private static string GetMessage(object? body)
    {
        var property = body!.GetType().GetProperty("message");
        Assert.NotNull(property);
        return (string)property!.GetValue(body)!;
    }

    /// <summary>
    /// T-2/T-5: ningun mensaje que llega al vendedor puede traer jerga de programador (nombre de
    /// excepcion .NET, "stack", "exception", GUID crudo). Si esto llegara a fallar, el mensaje
    /// dejo de ser criollo y paso a filtrar un detalle interno del motor.
    /// </summary>
    private static void AssertMensajeSinJergaTecnica(string message)
    {
        Assert.DoesNotContain("Exception", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DbUpdate", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("null", message, StringComparison.OrdinalIgnoreCase);
    }
}
