using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Mejora #1 (review de seguridad "PDF de presupuesto", 2026-08-12): el Content-Type que manda el
/// navegador es un dato que el cliente puede mentir; <see cref="ImageFileSignatureValidator"/> mira los
/// primeros bytes REALES del archivo.
/// </summary>
public class ImageFileSignatureValidatorTests
{
    [Fact]
    public void IsPngOrJpg_RealPngSignature_IsAccepted()
    {
        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Assert.True(ImageFileSignatureValidator.IsPngOrJpg(png));
    }

    [Fact]
    public void IsPngOrJpg_RealJpgSignature_IsAccepted()
    {
        byte[] jpg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        Assert.True(ImageFileSignatureValidator.IsPngOrJpg(jpg));
    }

    [Fact]
    public void IsPngOrJpg_FileRenamedButNotReallyAnImage_IsRejected()
    {
        // Un .exe (o cualquier binario) renombrado a "logo.png" con Content-Type falseado a "image/png":
        // esto es justamente lo que el chequeo de firma tiene que atrapar.
        byte[] fakeImage = { 0x4D, 0x5A, 0x90, 0x00 }; // firma real de un ejecutable Windows (MZ)
        Assert.False(ImageFileSignatureValidator.IsPngOrJpg(fakeImage));
    }

    [Fact]
    public void IsPngOrJpg_TooShort_IsRejected()
    {
        Assert.False(ImageFileSignatureValidator.IsPngOrJpg(new byte[] { 0x89, 0x50 }));
        Assert.False(ImageFileSignatureValidator.IsPngOrJpg(Array.Empty<byte>()));
    }
}
