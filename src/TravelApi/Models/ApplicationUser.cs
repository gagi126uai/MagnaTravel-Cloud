using Microsoft.AspNetCore.Identity;

namespace TravelApi.Models;

public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    public int? AgencyId { get; set; }
    public Agency? Agency { get; set; }
}
