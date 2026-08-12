namespace TravelApi.Domain.Helpers;

/// <summary>
/// Obra "PDF de presupuesto" (2026-08-11/12): chequeo de "firma" del archivo (magic bytes) para el
/// logo de la agencia — mismo criterio de seguridad que <c>AttachmentService.MatchesFileSignature</c>
/// (fix mejora #1, review de seguridad 2026-08-12): el <c>Content-Type</c> que manda el navegador es un
/// dato que el cliente puede mentir; miramos los primeros bytes REALES del archivo para confirmar que
/// "dice ser PNG/JPG" y "ES realmente PNG/JPG". Acotado a los 2 formatos que acepta el logo (a
/// diferencia de <c>AttachmentService</c>, que cubre PDF/Word/Excel además).
/// </summary>
public static class ImageFileSignatureValidator
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47 };

    /// <summary>True si <paramref name="bytes"/> empieza con la firma real de PNG o JPG.</summary>
    public static bool IsPngOrJpg(byte[] bytes)
    {
        if (bytes.Length < 4)
        {
            return false;
        }

        bool isPng = bytes.AsSpan(0, 4).SequenceEqual(PngSignature);
        bool isJpg = bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

        return isPng || isJpg;
    }
}
