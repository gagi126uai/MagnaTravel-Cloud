namespace TravelApi.Infrastructure.Services;

internal static class WhatsAppPhoneHelper
{
    public static string? NormalizeDigits(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(digits) ? null : digits;
    }

    public static string? Canonicalize(string? phone)
    {
        var digits = NormalizeDigits(phone);
        return digits == null ? null : $"+{digits}";
    }
}
