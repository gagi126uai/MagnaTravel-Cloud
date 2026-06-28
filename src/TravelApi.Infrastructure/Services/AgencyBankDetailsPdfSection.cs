using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-041 (2026-06-28): dibuja en un PDF la seccion "Para abonar por transferencia:" con los datos bancarios
/// de la AGENCIA (Banco · Titular · CBU · Alias), para que el cliente sepa a donde transferir. Se usa hoy en el
/// recibo de cobro; queda reutilizable para el presupuesto u otros comprobantes cuando existan.
///
/// <para><b>Que cuentas se muestran</b> lo decide la funcion pura <see cref="AgencyBankAccountSelector"/>; aca solo
/// se RENDERIZA la lista ya elegida. El CBU va COMPLETO: es dato de la propia agencia (no de un tercero), su
/// finalidad es que el cliente transfiera, asi que NO se enmascara (a diferencia de los listados de la pantalla).</para>
/// </summary>
public static class AgencyBankDetailsPdfSection
{
    /// <summary>
    /// Renderiza la seccion dentro del <paramref name="container"/> dado. El llamador DEBE invocar esto solo cuando
    /// <paramref name="accounts"/> tiene elementos: QuestPDF exige que un IContainer reciba exactamente un elemento,
    /// asi que si no hay cuentas el llamador NO debe reservar el slot (de lo contrario el layout falla).
    /// </summary>
    public static void Compose(IContainer container, IReadOnlyList<BankAccount> accounts)
    {
        // Guarda defensiva: si llega vacio igualmente dibujamos un bloque vacio para no romper el layout, pero
        // el contrato es que el llamador no llegue aca sin cuentas (ver doc del metodo).
        if (accounts is null || accounts.Count == 0)
        {
            container.Text(string.Empty);
            return;
        }

        var labelStyle = TextStyle.Default.FontSize(9).FontColor(Colors.Grey.Darken2);
        var valueStyle = TextStyle.Default.FontSize(9).FontColor(Colors.Black);

        container.Column(section =>
        {
            section.Item().PaddingBottom(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
            section.Item().Text("Para abonar por transferencia:").SemiBold().FontSize(10);

            foreach (var account in accounts)
            {
                section.Item().PaddingTop(4).Column(accountBlock =>
                {
                    // Encabezado de la cuenta: moneda + banco. La moneda ayuda al cliente a no transferir a la
                    // cuenta equivocada cuando la agencia opera en pesos y en dolares.
                    var bankLabel = string.IsNullOrWhiteSpace(account.Bank) ? "Banco no informado" : account.Bank;
                    accountBlock.Item().Text($"{account.Currency} · {bankLabel}").Style(valueStyle).SemiBold();

                    accountBlock.Item().Text(text =>
                    {
                        text.Span("Titular: ").Style(labelStyle);
                        text.Span(account.HolderName).Style(valueStyle);
                    });

                    // CBU COMPLETO (dato propio de la agencia, destino de transferencia). Solo si existe.
                    if (!string.IsNullOrWhiteSpace(account.Cbu))
                    {
                        accountBlock.Item().Text(text =>
                        {
                            text.Span("CBU: ").Style(labelStyle);
                            text.Span(account.Cbu).Style(valueStyle);
                        });
                    }

                    if (!string.IsNullOrWhiteSpace(account.Alias))
                    {
                        accountBlock.Item().Text(text =>
                        {
                            text.Span("Alias: ").Style(labelStyle);
                            text.Span(account.Alias).Style(valueStyle);
                        });
                    }
                });
            }
        });
    }
}
