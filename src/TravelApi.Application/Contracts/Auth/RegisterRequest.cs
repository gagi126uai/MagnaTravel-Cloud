using System.ComponentModel.DataAnnotations;

namespace TravelApi.Application.Contracts.Auth;

public record RegisterRequest(
    [Required, MinLength(2)] string FullName,
    [Required, EmailAddress] string Email,
    [Required, MinLength(8)] string Password);
