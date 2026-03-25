using System.ComponentModel.DataAnnotations;

namespace TravelApi.Application.Contracts.Auth;

public record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password,
    bool RememberMe = false);
