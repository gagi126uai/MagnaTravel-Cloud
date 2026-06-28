/**
 * Bandeja "Reembolsos a cobrar del operador" — vista global de todos los operadores.
 *
 * ADR-041 Tanda 4: cuando la agencia anula una reserva y ya le había pagado algo al
 * operador, ese operador queda debiendo devolver la plata. Esta bandeja lista TODOS
 * los casos pendientes de todos los operadores en un solo lugar.
 *
 * La lista incluye items vencidos (en rojo) que NO desaparecen: Gastón decidió que
 * los vencidos tienen que seguir visibles para poder reclamarlos o gestionarlos.
 *
 * Permiso requerido: tesoreria.supplier_payments (validado en la ruta de App.jsx y en
 * el componente de sección).
 */

import { Wallet } from "lucide-react";
import { OperatorRefundsPendingSection } from "../components/OperatorRefundsPendingSection";

export default function OperatorRefundsPage() {
  return (
    <div className="space-y-6">
      {/* ── Encabezado de la página ── */}
      <div className="flex items-center gap-3">
        <div className="rounded-lg bg-teal-100 p-2 text-teal-700 dark:bg-teal-900/30 dark:text-teal-300">
          <Wallet className="h-5 w-5" />
        </div>
        <div>
          <h1 className="text-2xl font-bold text-slate-900 dark:text-white">
            Reembolsos a cobrar del operador
          </h1>
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Cancelaciones donde los operadores te deben devolver plata. Los vencidos
            quedan en la lista hasta que se cobren o se gestionen.
          </p>
        </div>
      </div>

      {/* ── Lista de reembolsos pendientes ──
          showSupplierColumn=true: en la vista global se muestra el nombre del operador
          en cada fila porque el usuario puede ver casos de distintos proveedores a la vez. */}
      <OperatorRefundsPendingSection showSupplierColumn />
    </div>
  );
}
