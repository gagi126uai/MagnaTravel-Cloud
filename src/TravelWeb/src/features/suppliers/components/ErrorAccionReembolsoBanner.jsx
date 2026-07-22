/**
 * Cartel de error compartido por "Deshacer" y "Corregir reserva" (Tanda P2, spec
 * 2026-07-22). Muestra el mensaje del motor TAL CUAL (nunca se reescribe en el front) y,
 * si el rechazo es EXACTAMENTE "el cliente ya usó ese saldo a favor" (P4=B de la spec),
 * suma el botón "Ir a la cuenta del cliente" para que el usuario pueda resolverlo ahí.
 *
 * Aviso 5 del inventario del cartel emergente (spec 2026-07-22): este es un rechazo LARGO
 * del motor disparado por un click ("Deshacer"/"Corregir") → Cartel Emergente (ventana),
 * ya no un recuadro incrustado en la ficha.
 *
 * Se usa en DeshacerReembolsoInline y CorregirReembolsoInline — factorizado acá para no
 * repetir el mismo bloque dos veces (mismo mensaje, mismo botón, mismo criterio).
 *
 * Props:
 *   - mensaje: string — texto de error a mostrar (ya resuelto con getApiErrorMessage).
 *   - mostrarBotonCuentaCliente: bool — true cuando el error es REFUND_CREDIT_ALREADY_USED.
 *   - clientePublicId: string — GUID del cliente titular de la reserva de esta fila.
 *   - onClose: () => void — limpia el error en el padre (botón "Entendido"/X/Escape).
 */

import { CartelEmergente, CARTEL_EMERGENTE_VARIANTES } from "../../../components/CartelEmergente";

export function ErrorAccionReembolsoBanner({ mensaje, mostrarBotonCuentaCliente, clientePublicId, onClose }) {
  return (
    <CartelEmergente
      isOpen={Boolean(mensaje)}
      variant={CARTEL_EMERGENTE_VARIANTES.BLOQUEO}
      message={mensaje}
      onClose={onClose}
      dataTestId="reembolso-accion-error"
      actionTestId="reembolso-ir-cuenta-cliente"
      action={
        mostrarBotonCuentaCliente && clientePublicId
          ? { label: "Ir a la cuenta del cliente", to: `/customers/${clientePublicId}/account` }
          : null
      }
    />
  );
}
