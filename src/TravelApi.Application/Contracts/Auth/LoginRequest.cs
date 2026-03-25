using System.ComponentModel.DataAnnotations;

namespace TravelApi.Application.Contracts.Auth;

public record LoginRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] string Password,
    bool RememberMe = false);
