namespace TravelApi.Application.Interfaces;

public interface ISensitiveDataProtector
{
    string? ProtectString(string? value);
    string? UnprotectString(string? value);
    byte[]? ProtectBytes(byte[]? value);
    byte[]? UnprotectBytes(byte[]? value);
}
