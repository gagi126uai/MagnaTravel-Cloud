using System.ComponentModel.DataAnnotations;

namespace TravelApi.Application.Contracts.Auth;

public record RegisterRequest(
    [property: Required, MinLength(2)] string FullName,
    [property: Required, EmailAddress] string Email,
    [property: Required, MinLength(8)] string Password);
