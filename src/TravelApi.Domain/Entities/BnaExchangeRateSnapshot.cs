using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class BnaExchangeRateSnapshot
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    [Column(TypeName = "numeric(18,2)")]
    public decimal UsdSeller { get; set; }

    [Column(TypeName = "numeric(18,2)")]
    public decimal EuroSeller { get; set; }

    [Column(TypeName = "numeric(18,2)")]
    public decimal RealSeller { get; set; }

    [Required]
    [MaxLength(20)]
    public string PublishedDate { get; set; } = string.Empty;

    [Required]
    [MaxLength(10)]
    public string PublishedTime { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string Source { get; set; } = string.Empty;

    public DateTime FetchedAt { get; set; }
}
