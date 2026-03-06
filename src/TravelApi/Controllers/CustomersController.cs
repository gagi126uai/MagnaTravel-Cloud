using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/customers")]
[Authorize]
public class CustomersController : ControllerBase
{
    private readonly ICustomerService _customerService;

    public CustomersController(ICustomerService customerService)
    {
        _customerService = customerService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> GetCustomers([FromQuery] bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        var customers = await _customerService.GetCustomersAsync(includeInactive, cancellationToken);
        return Ok(customers);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<object>> GetCustomer(int id, CancellationToken cancellationToken)
    {
        try
        {
            var customer = await _customerService.GetCustomerAsync(id, cancellationToken);
            return Ok(customer);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    public async Task<ActionResult<Customer>> CreateCustomer(Customer customer, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _customerService.CreateCustomerAsync(customer, cancellationToken);
            return CreatedAtAction(nameof(GetCustomer), new { id = result.Id }, result);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
        {
            return BadRequest($"Error creando cliente (Posible duplicado de Documento/Email): {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error interno: {ex.Message}");
        }
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<Customer>> UpdateCustomer(int id, Customer customer, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _customerService.UpdateCustomerAsync(id, customer, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
        {
            return BadRequest($"Error actualizando cliente: {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
             return StatusCode(500, $"Error interno: {ex.Message}");
        }
    }

    /// <summary>
    /// Cuenta corriente del cliente: expedientes, pagos y saldo
    /// </summary>
    [HttpGet("{id:int}/account")]
    public async Task<ActionResult> GetCustomerAccount(int id, CancellationToken cancellationToken)
    {
        try
        {
            var accountDto = await _customerService.GetCustomerAccountAsync(id, cancellationToken);
            return Ok(accountDto);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error obteniendo cuenta corriente: {ex.Message}");
        }
    }
}
