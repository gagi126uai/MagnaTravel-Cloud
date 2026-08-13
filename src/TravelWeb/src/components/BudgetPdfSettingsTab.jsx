/**
 * Solapa de Configuración "Presupuestos y PDF" (spec docs/ux/2026-08-12-spec-pdf-presupuesto-ui.md,
 * §4, + Card 3 agregada por docs/ux/2026-08-12-spec-pdf-emision-y-formas-de-pago.md, §2). Tres
 * cards independientes:
 *   - "Identidad del PDF": logo, color de la banda y legajo EVT — datos que van en la cabecera
 *     del PDF de presupuesto (obra "PDF de presupuesto", QuestPDF llega en una tanda siguiente).
 *   - "Condiciones que van en el PDF": la "letra chica" por tipo de servicio (6 bloques de texto
 *     libre), con un borrador opcional armado por IA que el dueño siempre revisa antes de guardar.
 *   - "Formas de pago" (Card 3, ancho completo): la PLANTILLA general que se precarga en cada
 *     presupuesto nuevo — distinta del texto propio de cada reserva (ese vive en la ficha,
 *     PaymentTermsCard.jsx, con autoguardado). Acá, como en Card 1/2, el guardado es explícito
 *     ("Guardar cambios"): dos pantallas, dos patrones ya establecidos, no se unifican (§2.2).
 *
 * `agencyLicenseNumber`/`pdfBandColorHex`/`budgetPaymentTermsTemplate` viajan en el MISMO endpoint
 * que ya usa la solapa "Agencia" (`GET`/`PUT /reports/settings`) — por eso el guardado de
 * cualquiera de las tres cards reenvía TODOS los campos que trae el GET (`AgencySettingsUpsertRequest`
 * no es anti-clobber: si mandáramos solo el campo que cambió, el backend pisaría agencyName/taxId/
 * etc con los defaults del request). Ver `armarPayloadSettings` más abajo — un solo lugar arma ese
 * payload completo para que Card 1 y Card 3 no repitan la lista de 12 campos cada una.
 */

import { useEffect, useRef, useState } from "react";
import { Image as ImageIcon, FileText, ChevronDown, Sparkles, CreditCard } from "lucide-react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { getApiErrorMessage } from "../lib/errors";
import { Button } from "./ui/button";

// Los 6 bloques de condiciones, en el mismo orden fijo que usa el backend
// (BudgetConditionBlockKindText.All, C#). `kind` es el token EXACTO que espera la API — "Aereos"
// SIN tilde no es un typo, es el texto literal que usa el backend como clave. `label` es solo
// para mostrar en pantalla, en español correcto.
const BLOQUES_CONDICIONES = [
  { kind: "Aereos", label: "Aéreos" },
  { kind: "Hoteles", label: "Hoteles" },
  { kind: "Traslados", label: "Traslados" },
  { kind: "Paquetes", label: "Paquetes" },
  { kind: "Asistencias", label: "Asistencias" },
  { kind: "Generales", label: "Generales" },
];

const INPUT_CLASSNAME =
  "flex h-10 w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm placeholder:text-slate-400 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent dark:bg-slate-950 dark:border-slate-800 dark:text-slate-50";

export default function BudgetPdfSettingsTab() {
  // ─── Card 1: Identidad del PDF ────────────────────────────────────────────
  // `settingsCompletos` guarda el GET entero (todos los campos de la agencia, no solo los 2
  // nuevos) — lo necesitamos para reenviarlo completo al PUT (ver el comentario de arriba).
  const [settingsCompletos, setSettingsCompletos] = useState(null);
  const [legajoEvt, setLegajoEvt] = useState("");
  const [colorBanda, setColorBanda] = useState("#0e3a4f");
  const [guardandoIdentidad, setGuardandoIdentidad] = useState(false);

  // ─── Card 3: Formas de pago (la PLANTILLA de Configuración, no el texto de cada
  // reserva — ese vive en PaymentTermsCard.jsx dentro de la ficha) ──────────────────
  const [textoFormasDePago, setTextoFormasDePago] = useState("");
  const [guardandoFormasDePago, setGuardandoFormasDePago] = useState(false);
  const [redactandoFormasDePago, setRedactandoFormasDePago] = useState(false);

  useEffect(() => {
    let cancelado = false;
    (async () => {
      try {
        const data = await api.get("/reports/settings");
        if (cancelado) return;
        setSettingsCompletos(data);
        setLegajoEvt(data?.agencyLicenseNumber || "");
        setColorBanda(data?.pdfBandColorHex || "#0e3a4f");
        setTextoFormasDePago(data?.budgetPaymentTermsTemplate || "");
      } catch {
        // Degradación elegante: si el GET falla, el formulario queda con los defaults —
        // el dueño puede escribir de cero; el guardado reintenta el PUT igual.
      }
    })();
    return () => {
      cancelado = true;
    };
  }, []);

  // Arma el payload COMPLETO que espera PUT /reports/settings (no es anti-clobber: hay que
  // mandar los 12 campos siempre, aunque el usuario haya tocado uno solo). Lee siempre los
  // 3 estados ACTUALES (legajoEvt/colorBanda/textoFormasDePago) — así Card 1 y Card 3
  // comparten un solo lugar con la lista completa, en vez de repetirla cada una: cuando se
  // guarda desde Card 1, el texto de Card 3 viaja intacto (y viceversa), porque ambos leen
  // el mismo estado en memoria, no un snapshot viejo del último GET.
  const armarPayloadSettings = () => ({
    agencyName: settingsCompletos?.agencyName || "",
    legalName: settingsCompletos?.legalName || null,
    taxCondition: settingsCompletos?.taxCondition || null,
    activityStartDate: settingsCompletos?.activityStartDate || null,
    taxId: settingsCompletos?.taxId || null,
    address: settingsCompletos?.address || null,
    phone: settingsCompletos?.phone || null,
    email: settingsCompletos?.email || null,
    defaultCommissionPercent: settingsCompletos?.defaultCommissionPercent ?? 10,
    currency: settingsCompletos?.currency || "ARS",
    agencyLicenseNumber: legajoEvt.trim() || null,
    pdfBandColorHex: colorBanda || null,
    budgetPaymentTermsTemplate: textoFormasDePago.trim() || null,
  });

  const handleGuardarIdentidad = async () => {
    setGuardandoIdentidad(true);
    try {
      await api.put("/reports/settings", armarPayloadSettings());
      showSuccess("Identidad del PDF actualizada.");
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudo guardar la identidad del PDF."));
    } finally {
      setGuardandoIdentidad(false);
    }
  };

  const handleGuardarFormasDePago = async () => {
    setGuardandoFormasDePago(true);
    try {
      await api.put("/reports/settings", armarPayloadSettings());
      showSuccess("Plantilla de formas de pago actualizada.");
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudo guardar la plantilla de formas de pago."));
    } finally {
      setGuardandoFormasDePago(false);
    }
  };

  // "✨ Ayudame a redactarlo" de Card 3 — gemelo EXACTO del de Card 2 (handleAyudaIa), pero
  // sin categoría: acá hay una sola plantilla, no una por rubro. Nunca guarda nada solo (P-21).
  const handleAyudaIaFormasDePago = async () => {
    setRedactandoFormasDePago(true);
    try {
      const draft = await api.post("/reports/budget-payment-terms-template/draft", {
        currentText: textoFormasDePago || null,
      });
      setTextoFormasDePago(draft?.text || "");
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudo redactar el borrador."));
    } finally {
      setRedactandoFormasDePago(false);
    }
  };

  // ─── Logo ──────────────────────────────────────────────────────────────────
  // El logo es una imagen protegida por sesión (no un <img src> directo): se pide como blob
  // con el cliente de API (que manda las credenciales) y se arma una URL local para mostrarlo.
  const [logoUrl, setLogoUrl] = useState(null);
  // "cargando" | "con-logo" | "sin-logo" — un 404 del GET es un estado ESPERADO (agencia sin
  // logo todavía), no un error real.
  const [logoEstado, setLogoEstado] = useState("cargando");
  const [subiendoLogo, setSubiendoLogo] = useState(false);
  const [recargarLogoSignal, setRecargarLogoSignal] = useState(0);
  const inputLogoRef = useRef(null);

  useEffect(() => {
    let cancelado = false;
    let urlCreada = null;
    (async () => {
      setLogoEstado("cargando");
      try {
        const blob = await api.get("/reports/settings/logo", { responseType: "blob" });
        if (cancelado) return;
        urlCreada = URL.createObjectURL(blob);
        setLogoUrl(urlCreada);
        setLogoEstado("con-logo");
      } catch {
        if (cancelado) return;
        setLogoUrl(null);
        setLogoEstado("sin-logo");
      }
    })();
    return () => {
      cancelado = true;
      if (urlCreada) URL.revokeObjectURL(urlCreada);
    };
  }, [recargarLogoSignal]);

  const handleCambiarLogo = async (event) => {
    const file = event.target.files?.[0];
    event.target.value = ""; // permite volver a elegir el mismo archivo si hace falta
    if (!file) return;
    if (file.size > 2 * 1024 * 1024) {
      showError("El logo pesa más de 2 MB.");
      return;
    }

    const formData = new FormData();
    formData.append("file", file);
    setSubiendoLogo(true);
    try {
      await api.post("/reports/settings/logo", formData);
      showSuccess("Logo actualizado.");
      setRecargarLogoSignal((previo) => previo + 1);
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudo subir el logo."));
    } finally {
      setSubiendoLogo(false);
    }
  };

  // ─── Card 2: Condiciones que van en el PDF ────────────────────────────────
  const [bloques, setBloques] = useState(() => BLOQUES_CONDICIONES.map((bloque) => ({ ...bloque, text: "" })));
  const [cargandoBloques, setCargandoBloques] = useState(true);
  // Acordeón vertical (spec §4, Card 2): un solo bloque abierto a la vez, ninguno por default.
  const [bloqueAbiertoKind, setBloqueAbiertoKind] = useState(null);
  const [guardandoBloques, setGuardandoBloques] = useState(false);
  // Kind del bloque cuyo borrador IA está en curso (deshabilita SOLO ese link, no toda la card).
  const [redactandoKind, setRedactandoKind] = useState(null);

  useEffect(() => {
    let cancelado = false;
    (async () => {
      try {
        const data = await api.get("/reports/budget-conditions");
        if (cancelado || !Array.isArray(data)) return;
        setBloques((previo) =>
          previo.map((bloque) => {
            const encontrado = data.find((item) => item.kind === bloque.kind);
            return { ...bloque, text: encontrado?.text || "" };
          })
        );
      } catch {
        // Degradación elegante: los 6 bloques quedan vacíos, editables desde cero.
      } finally {
        if (!cancelado) setCargandoBloques(false);
      }
    })();
    return () => {
      cancelado = true;
    };
  }, []);

  const actualizarTextoBloque = (kind, texto) => {
    setBloques((previo) => previo.map((bloque) => (bloque.kind === kind ? { ...bloque, text: texto } : bloque)));
  };

  const handleGuardarBloques = async () => {
    setGuardandoBloques(true);
    try {
      // El backend no tiene un endpoint "los 6 juntos" — un PUT por bloque, en paralelo, detrás
      // de un solo botón (spec §4: "un solo botón Guardar cambios al pie de la card, para los 6
      // bloques juntos").
      await Promise.all(
        bloques.map((bloque) => api.put(`/reports/budget-conditions/${bloque.kind}`, { text: bloque.text || null }))
      );
      showSuccess("Condiciones del presupuesto actualizadas.");
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudieron guardar las condiciones."));
    } finally {
      setGuardandoBloques(false);
    }
  };

  // "✨ Ayudame a redactarlo" (P-21: el sistema sugiere, nunca decide): el borrador cae en el
  // textarea para que el dueño lo revise y edite — NUNCA se guarda solo.
  const handleAyudaIa = async (kind) => {
    setRedactandoKind(kind);
    try {
      const bloqueActual = bloques.find((bloque) => bloque.kind === kind);
      const draft = await api.post(`/reports/budget-conditions/${kind}/draft`, {
        currentText: bloqueActual?.text || null,
      });
      actualizarTextoBloque(kind, draft?.text || "");
    } catch (error) {
      // 409 = la IA no está disponible ahora mismo (mensaje ya en criollo del backend, se
      // muestra tal cual). Cualquier otro error usa el genérico de siempre.
      showError(getApiErrorMessage(error, "No se pudo redactar el borrador."));
    } finally {
      setRedactandoKind(null);
    }
  };

  return (
    <div className="grid gap-6 lg:grid-cols-2">
      {/* Card 1 — Identidad del PDF */}
      <div className="bg-white dark:bg-slate-900 rounded-xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden h-fit">
        <div className="px-6 py-4 border-b border-slate-100 dark:border-slate-800 flex items-center gap-3 bg-slate-50/50 dark:bg-slate-800/20">
          <div className="p-2 bg-indigo-100 dark:bg-indigo-900/30 rounded-lg text-indigo-600 dark:text-indigo-400">
            <ImageIcon className="h-5 w-5" />
          </div>
          <div>
            <h3 className="font-semibold text-slate-900 dark:text-white">Identidad del PDF</h3>
            <p className="text-xs text-slate-500">Logo, color y legajo que se ven en el presupuesto</p>
          </div>
        </div>

        <div className="p-6 space-y-6">
          {/* Logo */}
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">Logo</label>
            <div className="flex items-center gap-4">
              <div
                className="flex h-16 w-16 shrink-0 items-center justify-center rounded-lg border border-dashed border-slate-300 dark:border-slate-700 bg-slate-50 dark:bg-slate-800 overflow-hidden"
                data-testid="miniatura-logo-agencia"
              >
                {logoEstado === "con-logo" && logoUrl ? (
                  <img src={logoUrl} alt="Logo de la agencia" className="h-full w-full object-contain" />
                ) : (
                  <span className="text-[10px] text-slate-400 text-center px-1">
                    {logoEstado === "cargando" ? "Cargando…" : "Sin logo cargado"}
                  </span>
                )}
              </div>
              <div>
                <input
                  ref={inputLogoRef}
                  type="file"
                  accept=".png,.jpg,.jpeg"
                  className="hidden"
                  onChange={handleCambiarLogo}
                  data-testid="input-logo-agencia"
                />
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={() => inputLogoRef.current?.click()}
                  disabled={subiendoLogo}
                  data-testid="btn-cambiar-logo"
                >
                  {subiendoLogo ? "Subiendo…" : "Cambiar logo"}
                </Button>
                <p className="mt-1 text-[11px] text-slate-400">PNG o JPG, hasta 2 MB.</p>
              </div>
            </div>
          </div>

          {/* Color de la banda: selector nativo + muestra en vivo */}
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2" htmlFor="pdf-color-banda">
              Color de la banda
            </label>
            <div className="flex items-center gap-3">
              <input
                id="pdf-color-banda"
                type="color"
                value={colorBanda}
                onChange={(event) => setColorBanda(event.target.value)}
                className="h-10 w-14 rounded-md border border-slate-300 dark:border-slate-700 cursor-pointer bg-transparent"
                data-testid="input-color-banda"
              />
              <div
                className="h-6 flex-1 rounded"
                style={{ backgroundColor: colorBanda }}
                data-testid="muestra-color-banda"
                aria-hidden="true"
              />
            </div>
          </div>

          {/* Legajo EVT: sin cartelito de ayuda (P-15) — el label alcanza */}
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5" htmlFor="pdf-legajo-evt">
              Legajo EVT
            </label>
            <input
              id="pdf-legajo-evt"
              type="text"
              placeholder="Ej: 12345"
              value={legajoEvt}
              onChange={(event) => setLegajoEvt(event.target.value)}
              className={INPUT_CLASSNAME}
              data-testid="input-legajo-evt"
            />
          </div>
        </div>

        <div className="px-6 py-4 bg-slate-50 dark:bg-slate-900/50 border-t border-slate-100 dark:border-slate-800 flex justify-end">
          <Button
            type="button"
            onClick={handleGuardarIdentidad}
            // Deshabilitado también mientras el GET inicial no resolvió: sin `settingsCompletos`
            // no tenemos el resto de los campos de la agencia (agencyName es obligatorio en el
            // backend) — guardar en ese momento podría reenviar el PUT con campos en blanco.
            disabled={guardandoIdentidad || !settingsCompletos}
            data-testid="btn-guardar-identidad-pdf"
          >
            {guardandoIdentidad ? "Guardando..." : "Guardar cambios"}
          </Button>
        </div>
      </div>

      {/* Card 2 — Condiciones que van en el PDF */}
      <div className="bg-white dark:bg-slate-900 rounded-xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden h-fit">
        <div className="px-6 py-4 border-b border-slate-100 dark:border-slate-800 flex items-center gap-3 bg-slate-50/50 dark:bg-slate-800/20">
          <div className="p-2 bg-indigo-100 dark:bg-indigo-900/30 rounded-lg text-indigo-600 dark:text-indigo-400">
            <FileText className="h-5 w-5" />
          </div>
          <div>
            <h3 className="font-semibold text-slate-900 dark:text-white">Condiciones que van en el PDF</h3>
            <p className="text-xs text-slate-500">Letra chica del presupuesto, por tipo de servicio</p>
          </div>
        </div>

        <div className="p-6 space-y-2">
          {bloques.map((bloque) => {
            const estaAbierto = bloqueAbiertoKind === bloque.kind;
            return (
              <div key={bloque.kind} className="rounded-lg border border-slate-200 dark:border-slate-800">
                <button
                  type="button"
                  onClick={() => setBloqueAbiertoKind(estaAbierto ? null : bloque.kind)}
                  className="flex w-full items-center justify-between px-4 py-3 text-sm font-semibold text-slate-800 dark:text-slate-200"
                  aria-expanded={estaAbierto}
                  data-testid={`acordeon-condicion-${bloque.kind}`}
                >
                  {bloque.label}
                  <ChevronDown className={`h-4 w-4 text-slate-400 transition-transform ${estaAbierto ? "rotate-180" : ""}`} aria-hidden="true" />
                </button>
                {estaAbierto && (
                  <div className="px-4 pb-4 space-y-2">
                    <textarea
                      className="w-full rounded-lg border border-slate-300 dark:border-slate-700 dark:bg-slate-950 text-sm p-3 min-h-[100px] focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
                      value={bloque.text || ""}
                      onChange={(event) => actualizarTextoBloque(bloque.kind, event.target.value)}
                      disabled={cargandoBloques}
                      data-testid={`textarea-condicion-${bloque.kind}`}
                    />
                    {/* Link terciario discreto (criterio 2026-08-07/10: IA invisible, sin caja
                        nueva, sin color fuerte) — no compite con el textarea. */}
                    <button
                      type="button"
                      onClick={() => handleAyudaIa(bloque.kind)}
                      disabled={redactandoKind === bloque.kind}
                      className="inline-flex items-center gap-1 text-xs font-medium text-slate-400 hover:text-indigo-600 dark:hover:text-indigo-400 disabled:opacity-60 disabled:cursor-not-allowed"
                      data-testid={`btn-ayuda-ia-${bloque.kind}`}
                    >
                      <Sparkles className="h-3 w-3" aria-hidden="true" />
                      {redactandoKind === bloque.kind ? "Redactando…" : "Ayudame a redactarlo"}
                    </button>
                  </div>
                )}
              </div>
            );
          })}
        </div>

        <div className="px-6 py-4 bg-slate-50 dark:bg-slate-900/50 border-t border-slate-100 dark:border-slate-800 flex justify-end">
          <Button
            type="button"
            onClick={handleGuardarBloques}
            disabled={guardandoBloques || cargandoBloques}
            data-testid="btn-guardar-condiciones"
          >
            {guardandoBloques ? "Guardando..." : "Guardar cambios"}
          </Button>
        </div>
      </div>

      {/* Card 3 — Formas de pago (spec docs/ux/2026-08-12-spec-pdf-emision-y-formas-de-pago.md,
          §2): ancho completo, DEBAJO de las otras dos — es una sola card, no tiene sentido
          angostarla a la mitad y dejar un hueco vacío al lado (§2.2). Un solo textarea (no
          acordeón: acá hay UN dato, no seis por rubro como en Card 2). */}
      <div className="lg:col-span-2 bg-white dark:bg-slate-900 rounded-xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden h-fit">
        <div className="px-6 py-4 border-b border-slate-100 dark:border-slate-800 flex items-center gap-3 bg-slate-50/50 dark:bg-slate-800/20">
          <div className="p-2 bg-indigo-100 dark:bg-indigo-900/30 rounded-lg text-indigo-600 dark:text-indigo-400">
            <CreditCard className="h-5 w-5" />
          </div>
          <div>
            <h3 className="font-semibold text-slate-900 dark:text-white">Formas de pago</h3>
            <p className="text-xs text-slate-500">
              Plantilla que se precarga en cada presupuesto nuevo — cada vendedor la puede editar para SU reserva sin tocar esto de acá
            </p>
          </div>
        </div>

        <div className="p-6 space-y-2">
          <label htmlFor="formas-de-pago-plantilla" className="sr-only">
            Plantilla de formas de pago
          </label>
          <textarea
            id="formas-de-pago-plantilla"
            className="w-full rounded-lg border border-slate-300 dark:border-slate-700 dark:bg-slate-950 text-sm p-3 min-h-[100px] focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
            value={textoFormasDePago}
            onChange={(event) => setTextoFormasDePago(event.target.value)}
            placeholder="Ej: Seña del 30% al reservar. Saldo 21 días antes de la salida. Transferencia bancaria o efectivo en la agencia."
            data-testid="textarea-formas-de-pago-plantilla"
          />
          {/* Link terciario, idéntico al de Card 2 (mismo criterio: IA invisible, sin caja
              nueva, sin color fuerte) — el borrador cae en el textarea, nunca se guarda solo. */}
          <button
            type="button"
            onClick={handleAyudaIaFormasDePago}
            disabled={redactandoFormasDePago}
            className="inline-flex items-center gap-1 text-xs font-medium text-slate-400 hover:text-indigo-600 dark:hover:text-indigo-400 disabled:opacity-60 disabled:cursor-not-allowed"
            data-testid="btn-ayuda-ia-formas-de-pago"
          >
            <Sparkles className="h-3 w-3" aria-hidden="true" />
            {redactandoFormasDePago ? "Redactando…" : "Ayudame a redactarlo"}
          </button>
        </div>

        <div className="px-6 py-4 bg-slate-50 dark:bg-slate-900/50 border-t border-slate-100 dark:border-slate-800 flex justify-end">
          <Button
            type="button"
            onClick={handleGuardarFormasDePago}
            disabled={guardandoFormasDePago || !settingsCompletos}
            data-testid="btn-guardar-formas-de-pago"
          >
            {guardandoFormasDePago ? "Guardando..." : "Guardar cambios"}
          </Button>
        </div>
      </div>
    </div>
  );
}
