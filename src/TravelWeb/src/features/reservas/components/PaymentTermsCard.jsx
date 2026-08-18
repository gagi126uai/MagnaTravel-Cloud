import { useEffect, useRef, useState } from "react";
import { CreditCard } from "lucide-react";
import { api } from "../../../api";
import { showError } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { cargarTextoPrecargadoFormasDePago, textoFormasDePagoFueEditado } from "../lib/paymentTermsCardLogic";

/**
 * Card "Formas de pago" de la solapa Servicios (spec
 * docs/ux/2026-08-12-spec-pdf-emision-y-formas-de-pago.md, §1). Es el texto que el PDF de
 * presupuesto muestra bajo "Formas de pago" — solo se usa en etapa Presupuesto (el padre la
 * renderiza condicionada a reserva.status === "Budget", misma condición que los botones
 * "Emitir PDF"/"Enviar por WhatsApp" de la cabecera).
 *
 * Precarga (§1.2, decisión #2 firmada): si la reserva ya tiene `budgetPaymentTermsText`
 * propio, se muestra ESE tal cual. Si no, se pide la plantilla de Configuración con
 * `GET /reports/budget-payment-terms-template` → `{ text }` y se precarga como
 * previsualización editable — todavía SIN guardar nada. El texto recién "se materializa"
 * como propio de la reserva cuando el vendedor escribe algo distinto de lo precargado.
 *
 * Fix bloqueante (2026-08-13, hallazgo de frontend-reviewer): ANTES esta card pedía la
 * plantilla con `GET /reports/settings`, que es Admin-only — un vendedor/colaborador
 * recibía 403 silencioso y el textarea quedaba vacío, aunque la agencia sí tuviera una
 * plantilla cargada. El endpoint nuevo es de LECTURA MÍNIMA (solo el texto, permiso base
 * de reservas): cualquiera parado en la ficha lo puede leer. `/reports/settings` sigue
 * siendo Admin-only para Configuración (BudgetPdfSettingsTab.jsx no se tocó).
 *
 * Autoguardado (precedente P8c, 2026-08-03, `PassengerCountsWidget` de esta misma ficha):
 * debounce de 600ms, sin botón "Guardar", feedback discreto "Guardando…"/"Guardado ✓" —
 * NUNCA un toast en éxito (interrumpiría al vendedor mientras sigue editando). Si falla,
 * el error SÍ se avisa (showError) y lo tipeado nunca se pierde.
 *
 * Props:
 *  - reservaPublicId: string — publicId de la reserva, para el PATCH.
 *  - initialText: string|null — reserva.budgetPaymentTermsText tal como vino del backend.
 *  - onSaved: callback opcional, se llama tras un guardado exitoso (el padre lo usa para
 *    refrescar la reserva en segundo plano, mismo patrón que handleSavePassengerCounts).
 */
export function PaymentTermsCard({ reservaPublicId, initialText, onSaved }) {
  const [texto, setTexto] = useState(initialText || "");
  // textoPrecargado es la referencia contra la que comparamos "¿el vendedor tocó algo?"
  // (ver textoFormasDePagoFueEditado). Arranca igual a initialText y se actualiza cuando
  // llega la plantilla de Configuración (si la reserva no tenía texto propio) o después
  // de cada guardado exitoso.
  const [textoPrecargado, setTextoPrecargado] = useState(initialText || "");
  const [cargandoPlantilla, setCargandoPlantilla] = useState(false);
  const [guardando, setGuardando] = useState(false);
  const [mostrarGuardado, setMostrarGuardado] = useState(false);
  const ocultarGuardadoTimer = useRef(null);
  // El vendedor puede empezar a escribir MIENTRAS todavía está en vuelo el GET de la
  // plantilla (conexión lenta). Sin esta bandera, la respuesta de la plantilla llegaría
  // tarde y le pisaría lo que ya tipeó — un caso real de "el texto se pierde", que la spec
  // prohíbe explícitamente (P-7). Con la bandera, si ya escribió algo, ignoramos la
  // plantilla que llega después.
  const usuarioYaEscribioRef = useRef(false);

  // useEffect con dependencias vacías: solo corre una vez al montar la card. Si la reserva
  // YA tiene texto propio, no hay nada que precargar — se muestra tal cual (§1.2: "no se
  // pisa con la plantilla"). Si no tiene, pedimos la plantilla de Configuración para armar
  // la previsualización editable.
  useEffect(() => {
    if (initialText && initialText.trim().length > 0) return undefined;

    let cancelado = false;
    setCargandoPlantilla(true);
    // La llamada real al endpoint de lectura mínima vive acá (el componente es el único
    // que conoce `api`); la ORQUESTACIÓN (¿hace falta pedirla? ¿qué hago si falla?) vive en
    // cargarTextoPrecargadoFormasDePago, que se testea aparte sin jsdom.
    cargarTextoPrecargadoFormasDePago(initialText, async () => {
      const template = await api.get("/reports/budget-payment-terms-template");
      return template?.text;
    })
      .then((textoDePrevisualizacion) => {
        if (cancelado || usuarioYaEscribioRef.current) return;
        setTexto(textoDePrevisualizacion);
        setTextoPrecargado(textoDePrevisualizacion);
      })
      .finally(() => {
        if (!cancelado) setCargandoPlantilla(false);
      });

    return () => {
      cancelado = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const editado = textoFormasDePagoFueEditado(texto, textoPrecargado);

  // Auto-guardado con debounce de 600ms, mismo mecanismo que PassengerCountsWidget (P8c).
  // Deps: [texto] a propósito — si agregáramos `editado`/`textoPrecargado` (que cambian de
  // identidad en cada guardado), el efecto se re-dispararía de más.
  useEffect(() => {
    if (!editado) return undefined;
    const timer = setTimeout(async () => {
      setGuardando(true);
      try {
        const textoAGuardar = texto.trim();
        await api.patch(`/reservas/${reservaPublicId}/budget-payment-terms`, {
          text: textoAGuardar.length > 0 ? textoAGuardar : null,
        });
        setTextoPrecargado(texto);
        setMostrarGuardado(true);
        clearTimeout(ocultarGuardadoTimer.current);
        ocultarGuardadoTimer.current = setTimeout(() => setMostrarGuardado(false), 2000);
        onSaved?.();
      } catch (error) {
        showError(getApiErrorMessage(error, "No se pudo guardar el texto de formas de pago."));
      } finally {
        setGuardando(false);
      }
    }, 600);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [texto]);

  // Limpia el timer del "Guardado ✓" al desmontar, para no tocar estado de un componente muerto.
  useEffect(() => () => clearTimeout(ocultarGuardadoTimer.current), []);

  return (
    <div className="rounded-[14px] border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <div className="mb-1 flex items-center gap-2">
        <CreditCard className="h-4 w-4 text-slate-400" aria-hidden="true" />
        <h3 className="text-sm font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
          Formas de pago
        </h3>
      </div>
      <p className="mb-3 text-xs text-slate-400">Así se ve en el presupuesto que recibe el cliente.</p>
      <label htmlFor="formas-de-pago-textarea" className="sr-only">
        Formas de pago del presupuesto
      </label>
      <textarea
        id="formas-de-pago-textarea"
        value={texto}
        onChange={(event) => {
          usuarioYaEscribioRef.current = true;
          setTexto(event.target.value);
        }}
        rows={4}
        placeholder="Ej: Seña del 30% al reservar, saldo antes de la salida…"
        disabled={cargandoPlantilla}
        data-testid="textarea-formas-de-pago-reserva"
        className="w-full rounded-[10px] border border-slate-200 bg-white px-3 py-2.5 text-sm text-slate-700 outline-none transition focus:border-primary focus:ring-2 focus:ring-primary/20 disabled:opacity-60 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200"
      />
      {/* Feedback discreto de guardado (P8c): nunca un toast, para no interrumpir al
          vendedor mientras sigue editando. */}
      <div className="mt-2 flex justify-end text-xs font-semibold" data-testid="formas-de-pago-autosave-feedback">
        {guardando && <span className="text-slate-400">Guardando…</span>}
        {!guardando && mostrarGuardado && <span className="text-emerald-600 dark:text-emerald-400">Guardado ✓</span>}
      </div>
    </div>
  );
}
