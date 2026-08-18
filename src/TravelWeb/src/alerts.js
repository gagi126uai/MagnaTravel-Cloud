import { toast } from "sonner";
import Swal from "sweetalert2";
import { normalizeMessage } from "./lib/errors";

export function showSuccess(message, title = "Listo") {
  toast.success(title, {
    description: normalizeMessage(message, ""),
    duration: 3000,
  });
}

export function showError(message, title = "Error") {
  toast.error(title, {
    description: normalizeMessage(message, ""),
    duration: 4000,
  });
}

export function showInfo(message, title = "Informacion") {
  toast.info(title, {
    description: normalizeMessage(message, ""),
    duration: 3000,
  });
}

export function showWarning(message, title = "Advertencia") {
  toast.warning(title, {
    description: normalizeMessage(message, ""),
    duration: 3500,
  });
}

const confirmPalettes = {
  indigo: {
    badgeTone: "bg-blue-50 text-blue-600",
    buttonTone: "bg-primary hover:bg-primary/90 focus-visible:ring-ring",
  },
  red: {
    badgeTone: "bg-rose-50 text-rose-600",
    buttonTone: "bg-rose-600 hover:bg-rose-700 focus-visible:ring-rose-500/30",
  },
  emerald: {
    badgeTone: "bg-emerald-50 text-emerald-600",
    // El botón de confirmar es una ACCIÓN (azul boleto del molde); el verde queda
    // solo en el badge de contexto, que sí es un significado (B.1).
    buttonTone: "bg-primary hover:bg-primary/90 focus-visible:ring-ring",
  },
  amber: {
    badgeTone: "bg-amber-50 text-amber-600",
    buttonTone: "bg-amber-500 hover:bg-amber-600 focus-visible:ring-amber-500/30",
  },
};

function escapeHtml(value = "") {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

function formatConfirmCopy(value = "") {
  return escapeHtml(value).replace(/\n/g, "<br />");
}

function buildConfirmHtml({ eyebrow, text, details, badgeTone }) {
  const eyebrowLabel = eyebrow || "Confirmacion";
  const textBlock = text
    ? `<p class="text-sm leading-6 text-slate-600">${formatConfirmCopy(text)}</p>`
    : "";
  const detailsBlock = details
    ? `<div class="rounded-[10px] border border-slate-200 bg-slate-50 px-3.5 py-3 text-xs leading-5 text-slate-500">${formatConfirmCopy(details)}</div>`
    : "";

  return `
    <div class="space-y-4 text-left">
      <div class="inline-flex items-center rounded-full px-2.5 py-1 text-[11px] font-bold uppercase tracking-[0.18em] ${badgeTone}">
        ${escapeHtml(eyebrowLabel)}
      </div>
      ${textBlock}
      ${detailsBlock}
    </div>
  `;
}

export async function showConfirm(input, text, confirmText = "Si, confirmar", confirmColor = "indigo") {
  const options =
    typeof input === "object" && input !== null
      ? input
      : {
          title: input,
          text,
          confirmText,
          confirmColor,
        };

  const palette = confirmPalettes[options.confirmColor] || confirmPalettes.indigo;

  const result = await Swal.fire({
    title: options.title || "Confirmar accion",
    html: buildConfirmHtml({
      eyebrow: options.eyebrow,
      text: options.text,
      details: options.details,
      badgeTone: palette.badgeTone,
    }),
    showCancelButton: true,
    showCloseButton: true,
    focusCancel: true,
    confirmButtonText: options.confirmText || confirmText,
    cancelButtonText: options.cancelText || "Cancelar",
    customClass: {
      popup: "w-full max-w-[26rem] rounded-[14px] border border-slate-200 bg-white p-0 shadow-[0_24px_64px_-28px_rgba(15,23,42,0.45)]",
      title: "px-6 pt-6 text-left text-xl font-bold tracking-tight text-slate-950",
      htmlContainer: "mx-0 mt-0 px-6 pb-0 text-left",
      actions: "mt-0 flex flex-col-reverse gap-2 px-6 pb-6 pt-4 sm:flex-row sm:justify-end",
      confirmButton: `inline-flex min-h-10 items-center justify-center rounded-[10px] px-4 py-2.5 text-sm font-semibold text-white transition focus-visible:outline-none focus-visible:ring-4 ${palette.buttonTone}`,
      cancelButton: "inline-flex min-h-10 items-center justify-center rounded-[10px] border border-slate-200 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50",
      closeButton: "text-slate-300 transition hover:!text-slate-500 focus:!shadow-none focus:!outline-none",
    },
    buttonsStyling: false,
  });

  return result.isConfirmed;
}

/**
 * Cartel con DOS acciones nombradas explícitas (ninguna es "Cancelar") + cualquier
 * descarte (ESC, la X, click afuera del cartel) como una TERCERA salida, sin ejecutar
 * ninguna de las dos acciones. Se usa cuando las dos opciones son igual de válidas y
 * ninguna debería quedar escondida detrás de un botón "Cancelar" genérico — mismo
 * estilo visual que showConfirm (patrón único), pero sin el botón Cancelar de ahí.
 *
 * Fix 2026-08-07 (freno de repetidos del Tarifario): antes se reusaba showConfirm()
 * con la acción peligrosa puesta en el botón "Cancelar" — que además tenía el foco por
 * default (focusCancel) — así que ESC, la X o un click afuera terminaban ejecutando la
 * acción peligrosa. Acá el llamador recibe el resultado CRUDO de SweetAlert2
 * (isConfirmed/isDenied/isDismissed) para interpretarlo con su propia lógica pura —
 * nunca se colapsa a un booleano ambiguo. El foco por default queda en "confirmText"
 * (la opción seteada como más segura), nunca en "denyText".
 */
export async function showConfirmWithAlternative({ title, text, confirmText, denyText, confirmColor = "indigo" }) {
  const palette = confirmPalettes[confirmColor] || confirmPalettes.indigo;

  const result = await Swal.fire({
    title: title || "Confirmar accion",
    html: buildConfirmHtml({ text, badgeTone: palette.badgeTone }),
    showDenyButton: true,
    showCancelButton: false,
    showCloseButton: true,
    confirmButtonText: confirmText,
    denyButtonText: denyText,
    customClass: {
      popup: "w-full max-w-[26rem] rounded-[14px] border border-slate-200 bg-white p-0 shadow-[0_24px_64px_-28px_rgba(15,23,42,0.45)]",
      title: "px-6 pt-6 text-left text-xl font-bold tracking-tight text-slate-950",
      htmlContainer: "mx-0 mt-0 px-6 pb-0 text-left",
      actions: "mt-0 flex flex-col-reverse gap-2 px-6 pb-6 pt-4 sm:flex-row sm:justify-end",
      confirmButton: `inline-flex min-h-10 items-center justify-center rounded-[10px] px-4 py-2.5 text-sm font-semibold text-white transition focus-visible:outline-none focus-visible:ring-4 ${palette.buttonTone}`,
      denyButton: "inline-flex min-h-10 items-center justify-center rounded-[10px] border border-slate-200 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50",
      closeButton: "text-slate-300 transition hover:!text-slate-500 focus:!shadow-none focus:!outline-none",
    },
    buttonsStyling: false,
  });

  return {
    isConfirmed: Boolean(result.isConfirmed),
    isDenied: Boolean(result.isDenied),
    isDismissed: Boolean(result.isDismissed),
  };
}

export async function showTextPrompt({
  title,
  text,
  placeholder = "",
  confirmText = "Confirmar",
  minLength = 1,
}) {
  const result = await Swal.fire({
    title,
    text,
    input: "textarea",
    inputPlaceholder: placeholder,
    inputAttributes: { "aria-label": placeholder || title },
    showCancelButton: true,
    confirmButtonText: confirmText,
    cancelButtonText: "Cancelar",
    inputValidator: (value) => {
      const trimmed = value?.trim() || "";
      return trimmed.length >= minLength ? undefined : `Ingresá al menos ${minLength} caracteres.`;
    },
  });
  return result.isConfirmed ? result.value.trim() : null;
}

export function showToastSuccess(message) {
  toast.success(normalizeMessage(message, ""));
}

export function showToastError(message) {
  toast.error(normalizeMessage(message, ""));
}
