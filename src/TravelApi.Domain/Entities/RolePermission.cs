using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

public class RolePermission
{
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string RoleName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Permission { get; set; } = string.Empty;
}
