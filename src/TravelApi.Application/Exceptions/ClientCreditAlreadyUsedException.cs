namespace TravelApi.Application.Exceptions;

/// <summary>
/// Spec "el paso de multa vive en la ficha" (A6, 2026-07-08): se lanza al intentar REABRIR un cierre sin multa
/// (revert-waive) cuando el saldo a favor del cliente originado por esa anulacion YA fue usado/retirado por completo.
///
/// <para><b>Por que existe como tipo propio</b>: el controller la traduce a un 409 con un body de negocio
/// especifico (<c>code = "SALDO_YA_USADO"</c>) para que el front muestre el cartel exacto ("el cliente ya uso ese
/// saldo, cobrale la multa al cliente como cargo de la agencia desde la ficha" — voz autocontenida, 2026-07-08:
/// no deriva a un rol que quien lee el cartel probablemente ES). Distinguirla de las demas
/// <c>InvalidOperationException</c> del modulo evita mapearla al 409 generico.</para>
///
/// <para><b>Criterio del freno</b> (conservador y simple): al deshacer no se conoce cuanto seria la multa, asi que
/// se bloquea solo el caso sin retorno — el saldo a favor de esta cancelacion quedo en CERO (todo consumido). Si
/// todavia queda algo disponible, se permite reabrir (la multa que se confirme despues queda capeada por los
/// RefundCaps, como hoy). Ver el detalle en <c>RevertWaivedOperatorPenaltyAsync</c>.</para>
/// </summary>
public class ClientCreditAlreadyUsedException : Exception
{
    /// <summary>Codigo de negocio estable que el front matchea para mostrar el cartel de A6.</summary>
    public const string ErrorCode = "SALDO_YA_USADO";

    public ClientCreditAlreadyUsedException(string message) : base(message)
    {
    }
}
