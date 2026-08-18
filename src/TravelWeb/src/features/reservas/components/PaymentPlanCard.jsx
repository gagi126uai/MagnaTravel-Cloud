import { useEffect, useRef, useState } from "react";
import { CalendarClock, Plus, X } from "lucide-react";
import { api } from "../../../api";
import { showError } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { Button } from "../../../components/ui/button";
import { MoneyInput } from "../../../components/ui/MoneyInput";
import {
  MAX_FILAS_PLAN_DE_PAGOS,
  armarPayloadPlanDePagos,
  crearFilaVacia,
  filasDesdeInstallments,
  filasEstanCompletas,
  filasExcedenElMaximo,
  filasFueronEditadas,
  resolverMonedaPorDefectoDelPlan,
} from "../lib/paymentPlanCardLogic";

const INPUT_CLASS =
  "w-full rounded-[10px] border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700 outline-none transition focus:border-primary focus:ring-2 focus:ring-primary/20 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200";

/**
 * Card "Plan de pagos" de la solapa Servicios (spec 2026-08-14, ronda 2, §6): tabla de filas
 * [cuándo / monto / moneda] que el PDF de presupuesto dibuja debajo de la tarjeta de total.
 * Va justo debajo de "Formas de pago" (PaymentTermsCard) — solo en etapa Presupuesto, mismo
 * criterio que esa card (fuera de Presupuesto no tiene sentido seguir armando el plan que
 * alimenta un PDF que ya no se vuelve a emitir desde acá).
 *
 * Es 100% informativo: no toca cobranzas, cuenta corriente ni el saldo real del cliente — el
 * vendedor anota el plan tal como se lo comunicó al cliente, con texto libre para el "cuándo"
 * (puede ser "Al confirmar la reserva" o una fecha escrita a mano).
 *
 * Autoguardado (mismo patrón que PaymentTermsCard, precedente P8c): debounce de 600ms sin
 * botón "Guardar", feedback discreto "Guardando…"/"Guardado ✓". A diferencia del texto libre
 * de esa card, acá cada fila tiene DOS datos obligatorios (cuándo + monto) — mientras una fila
 * quede a medio completar, el autoguardado espera en silencio (no manda algo que sabemos que
 * el backend va a rechazar). Reemplazo total en cada guardado: se manda la tabla completa, no
 * un patch fila-por-fila (mismo criterio que el backend, ver UpdatePaymentPlanAsync).
 *
 * Props:
 *  - reservaPublicId: string — publicId de la reserva, para el PUT.
 *  - initialInstallments: array — reserva.paymentPlanInstallments tal como vino del backend.
 *  - reserva: objeto reserva completo — solo se usa para inferir la moneda por defecto de
 *    una fila nueva (resolverMonedaPorDefectoDelPlan).
 *  - onSaved: callback opcional tras un guardado exitoso (el padre refresca la reserva).
 */
export function PaymentPlanCard({ reservaPublicId, initialInstallments, reserva, onSaved }) {
  const monedaPorDefecto = resolverMonedaPorDefectoDelPlan(reserva);
  const [filas, setFilas] = useState(() => filasDesdeInstallments(initialInstallments));
  const [filasPrecargadas, setFilasPrecargadas] = useState(() => filasDesdeInstallments(initialInstallments));
  const [guardando, setGuardando] = useState(false);
  const [mostrarGuardado, setMostrarGuardado] = useState(false);
  const ocultarGuardadoTimer = useRef(null);
  // Contador propio para las `key` de fila nueva — mismo motivo que en cualquier lista
  // editable de React: el `key` tiene que sobrevivir a agregar/borrar filas en el medio,
  // así que no puede depender del índice del array.
  const proximaKeyRef = useRef(0);

  const editado = filasFueronEditadas(filas, filasPrecargadas);
  const excedeMaximo = filasExcedenElMaximo(filas);

  // Autoguardado con debounce de 600ms (precedente P8c) — pero solo dispara cuando la tabla
  // está completa Y dentro del tope: mientras el vendedor termina de tipear una fila nueva, o
  // si se pasó de las 24 filas, esperamos en silencio (el aviso de tope ya se ve abajo, no
  // hace falta además un toast de error por algo que el vendedor está a mitad de corregir).
  useEffect(() => {
    if (!editado || excedeMaximo || !filasEstanCompletas(filas)) return undefined;
    const timer = setTimeout(async () => {
      setGuardando(true);
      try {
        await api.put(`/reservas/${reservaPublicId}/budget-payment-plan`, armarPayloadPlanDePagos(filas));
        setFilasPrecargadas(filas);
        setMostrarGuardado(true);
        clearTimeout(ocultarGuardadoTimer.current);
        ocultarGuardadoTimer.current = setTimeout(() => setMostrarGuardado(false), 2000);
        onSaved?.();
      } catch (error) {
        showError(getApiErrorMessage(error, "No se pudo guardar el plan de pagos."));
      } finally {
        setGuardando(false);
      }
    }, 600);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filas]);

  // Limpia el timer del "Guardado ✓" al desmontar, para no tocar estado de un componente muerto.
  useEffect(() => () => clearTimeout(ocultarGuardadoTimer.current), []);

  const actualizarFila = (key, cambios) => {
    setFilas((prev) => prev.map((fila) => (fila.key === key ? { ...fila, ...cambios } : fila)));
  };

  const agregarFila = () => {
    proximaKeyRef.current += 1;
    setFilas((prev) => [...prev, crearFilaVacia(`nueva-${proximaKeyRef.current}`, monedaPorDefecto)]);
  };

  const borrarFila = (key) => {
    setFilas((prev) => prev.filter((fila) => fila.key !== key));
  };

  return (
    <div className="rounded-[14px] border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <div className="mb-1 flex items-center gap-2">
        <CalendarClock className="h-4 w-4 text-slate-400" aria-hidden="true" />
        <h3 className="text-sm font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
          Plan de pagos
        </h3>
      </div>
      <p className="mb-3 text-xs text-slate-400">
        Se muestra en el presupuesto, debajo del total. Si no cargás ninguna fila, no aparece.
      </p>

      {filas.length > 0 && (
        <div className="mb-3 space-y-2" data-testid="plan-de-pagos-filas">
          {filas.map((fila) => (
            <div key={fila.key} className="flex items-start gap-2">
              <div className="flex-1">
                <label htmlFor={`plan-cuando-${fila.key}`} className="sr-only">
                  Cuándo se paga
                </label>
                <input
                  id={`plan-cuando-${fila.key}`}
                  type="text"
                  value={fila.dueText}
                  onChange={(event) => actualizarFila(fila.key, { dueText: event.target.value })}
                  placeholder="Ej: Al confirmar la reserva"
                  maxLength={200}
                  className={INPUT_CLASS}
                  data-testid={`plan-de-pagos-cuando-${fila.key}`}
                />
              </div>
              <div className="w-32">
                <label htmlFor={`plan-monto-${fila.key}`} className="sr-only">
                  Monto
                </label>
                <MoneyInput
                  id={`plan-monto-${fila.key}`}
                  className={INPUT_CLASS}
                  value={fila.amount}
                  onChange={(nuevoValor) => actualizarFila(fila.key, { amount: nuevoValor })}
                  data-testid={`plan-de-pagos-monto-${fila.key}`}
                  aria-label="Monto de la fila del plan de pagos"
                />
              </div>
              <div className="w-24">
                <label htmlFor={`plan-moneda-${fila.key}`} className="sr-only">
                  Moneda
                </label>
                <select
                  id={`plan-moneda-${fila.key}`}
                  value={fila.currency}
                  onChange={(event) => actualizarFila(fila.key, { currency: event.target.value })}
                  className={INPUT_CLASS}
                  data-testid={`plan-de-pagos-moneda-${fila.key}`}
                >
                  <option value="ARS">ARS</option>
                  <option value="USD">USD</option>
                </select>
              </div>
              <Button
                type="button"
                variant="ghost"
                size="icon"
                onClick={() => borrarFila(fila.key)}
                aria-label="Eliminar esta fila del plan de pagos"
                data-testid={`plan-de-pagos-borrar-${fila.key}`}
              >
                <X className="h-4 w-4" />
              </Button>
            </div>
          ))}
        </div>
      )}

      {/* Aviso amable del tope (mismo límite que valida el backend, UpdatePaymentPlanAsync):
          se frena en pantalla ANTES de que el vendedor se entere por un error tardío del
          servidor. Ver filasExcedenElMaximo en paymentPlanCardLogic.js. */}
      {excedeMaximo && (
        <p className="mb-3 text-xs font-semibold text-red-600 dark:text-red-400" data-testid="plan-de-pagos-error-maximo">
          El plan de pagos admite hasta {MAX_FILAS_PLAN_DE_PAGOS} filas — sacá alguna para poder guardar.
        </p>
      )}

      <Button
        type="button"
        variant="outline"
        size="sm"
        onClick={agregarFila}
        disabled={filas.length >= MAX_FILAS_PLAN_DE_PAGOS}
        data-testid="plan-de-pagos-agregar-fila"
      >
        <Plus className="mr-1 h-4 w-4" />
        Agregar fila
      </Button>

      {/* Feedback discreto de guardado (P8c): nunca un toast, para no interrumpir al
          vendedor mientras sigue editando. */}
      <div className="mt-2 flex justify-end text-xs font-semibold" data-testid="plan-de-pagos-autosave-feedback">
        {guardando && <span className="text-slate-400">Guardando…</span>}
        {!guardando && mostrarGuardado && <span className="text-emerald-600 dark:text-emerald-400">Guardado ✓</span>}
      </div>
    </div>
  );
}
