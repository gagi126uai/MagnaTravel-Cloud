import { useCallback, useEffect, useState } from "react";
import { Loader2, RefreshCw, Sparkles } from "lucide-react";
import { api } from "../../../api";
import { showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import {
  AI_SCREEN_MODE,
  calcularModoCampoClave,
  construirResultadoPrueba,
  debeDeshabilitarBotonGuardar,
  detectarErrorDeClaveFaltante,
  refrescarFotoTrasPrueba,
  resolverModoDePantalla,
  validarAjustesAvanzados,
} from "../lib/aiSettingsPresentation.js";
import { AiStatusBanner } from "./AiStatusBanner";
import { AiProviderRadioList } from "./AiProviderRadioList";
import { AiApiKeyField } from "./AiApiKeyField";
import { AiAdvancedSettings } from "./AiAdvancedSettings";
import { AiTestConnectionRow } from "./AiTestConnectionRow";

const MENSAJE_ERROR_CARGA = "No se pudo cargar la configuración de inteligencia artificial.";
const MENSAJE_ERROR_PRUEBA_GENERICO = "No se pudo probar la conexión. Intentá de nuevo.";
const MENSAJE_ERROR_GUARDADO_GENERICO = "No se pudo guardar la configuración.";

/**
 * "Configuración → Inteligencia artificial" (solapa solo-Admin).
 * Deja que el dueño elija con que inteligencia artificial trabajar (Groq, OpenAI, Claude,
 * Gemini, Grok, OpenRouter u "Otra" a mano), pegue su clave, la pruebe y la guarde.
 * Spec firmada: docs/ux/specs/2026-08-07-tarifario-inteligente-FIRMADA.md §15.
 *
 * La pantalla vive acá, pero toda la logica de "que texto mostrar segun que codigo" esta
 * en ../lib/aiSettingsPresentation.js — así se puede probar sin renderizar React.
 */
export default function AiSettingsTab() {
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState(null); // distinto de saveError: tapa el formulario entero
  const [settings, setSettings] = useState(null); // ultimo AiSettingsDto recibido del motor
  const [providers, setProviders] = useState([]);

  // Formulario editable. Arranca vacio y se llena cuando llega la respuesta del GET.
  const [providerCode, setProviderCode] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [model, setModel] = useState("");
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [apiKeyInput, setApiKeyInput] = useState("");
  const [queriendoCambiarClave, setQueriendoCambiarClave] = useState(false);

  const [testing, setTesting] = useState(false);
  const [testResult, setTestResult] = useState(null); // { texto, esExito } | null

  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState(null);
  const [fieldErrorClave, setFieldErrorClave] = useState(null);
  const [fieldErrorBaseUrl, setFieldErrorBaseUrl] = useState(null);
  const [fieldErrorModel, setFieldErrorModel] = useState(null);

  const selectedProvider = providers.find((item) => item.code === providerCode) || null;

  // Precarga el formulario con lo que vino del motor. Se usa tanto en la carga inicial
  // como despues de guardar (para que la foto y el resto de la pantalla queden al dia).
  const aplicarSettingsAlFormulario = useCallback((dto, providersList) => {
    setSettings(dto);
    setProviderCode(dto.providerCode || providersList.find((p) => p.isRecommended)?.code || "");
    setBaseUrl(dto.baseUrl || "");
    setModel(dto.model || "");
    setApiKeyInput("");
    setQueriendoCambiarClave(false);
    // "Otra" exige direccion y modelo a mano: los ajustes avanzados se abren solos.
    const provider = providersList.find((p) => p.code === dto.providerCode);
    setShowAdvanced(Boolean(provider?.requiresManualEndpoint));
  }, []);

  // Carga inicial (settings + lista de proveedores). Se saca del useEffect a un
  // useCallback aparte para que el boton "Reintentar" (fix reviewer B2) pueda disparar
  // EXACTAMENTE la misma carga sin duplicar el codigo.
  const cargarConfiguracion = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      const [settingsData, providersData] = await Promise.all([
        api.get("/settings/ai"),
        api.get("/settings/ai/providers"),
      ]);
      const providersList = providersData?.providers || [];
      setProviders(providersList);
      aplicarSettingsAlFormulario(settingsData, providersList);
    } catch (error) {
      // Estado de error de carga DEDICADO (no saveError): mientras loadError este
      // presente, la pantalla NO dibuja el formulario (ver resolverModoDePantalla) — antes
      // un GET fallido dejaba radios vacios y "Probar conexión" habilitado disparando con
      // providerCode: "" (fix reviewer, bloqueante B2).
      setLoadError(getApiErrorMessage(error, MENSAJE_ERROR_CARGA));
    } finally {
      setLoading(false);
    }
  }, [aplicarSettingsAlFormulario]);

  // useEffect con dependencia en cargarConfiguracion (memoizada, referencia estable):
  // corre una sola vez al montar la pantalla, no en cada tipeo del usuario.
  useEffect(() => {
    cargarConfiguracion();
  }, [cargarConfiguracion]);

  // Elegir un proveedor de la lista precarga solo su direccion/modelo recomendados
  // (§15.2). El usuario no los ve ni los toca salvo que abra "Ajustes avanzados" o
  // elija "Otra", que los exige a mano.
  const handleSelectProvider = (provider) => {
    setProviderCode(provider.code);
    // Si habia una clave a medio pegar, se descarta: al volver a un proveedor ya
    // configurado el campo se esconde, y una clave invisible no puede viajar en Guardar.
    setApiKeyInput("");
    setFieldErrorClave(null);
    setFieldErrorBaseUrl(null);
    setFieldErrorModel(null);
    setTestResult(null); // fix reviewer, hallazgo menor 6: un "Funciona ✓" viejo no sobrevive el cambio de proveedor
    if (provider.requiresManualEndpoint) {
      setShowAdvanced(true);
      setBaseUrl("");
      setModel("");
    } else {
      setShowAdvanced(false);
      setBaseUrl(provider.baseUrl);
      setModel(provider.model);
    }
  };

  const handleChangeBaseUrl = (value) => {
    setBaseUrl(value);
    setFieldErrorBaseUrl(null);
    setTestResult(null); // editar la direccion invalida el resultado de la prueba anterior
  };

  const handleChangeModel = (value) => {
    setModel(value);
    setFieldErrorModel(null);
    setTestResult(null);
  };

  const handleChangeApiKeyInput = (value) => {
    setApiKeyInput(value);
    setFieldErrorClave(null);
    setTestResult(null);
  };

  const handleVolverARecomendados = () => {
    if (!selectedProvider) return;
    setBaseUrl(selectedProvider.baseUrl);
    setModel(selectedProvider.model);
    setFieldErrorBaseUrl(null);
    setFieldErrorModel(null);
    setTestResult(null);
  };

  const handleProbarConexion = async () => {
    setTesting(true);
    setTestResult(null);
    try {
      // Prueba lo que hay EN PANTALLA, este guardado o no (§15.4). Si el usuario no
      // tipeo una clave nueva, se manda vacio y el motor prueba con la que ya tiene.
      //
      // OJO: baseUrl/model se mandan SIEMPRE, este "Ajustes avanzados" abierto o
      // plegado — el estado ya tiene el valor correcto (el recomendado del preset, o el
      // que el usuario haya tocado a mano). Condicionar esto a si el acordeon esta
      // abierto tiraria un cambio hecho a mano si el usuario lo pliega antes de probar.
      const resultado = await api.post("/settings/ai/test-connection", {
        providerCode,
        baseUrl: baseUrl || undefined,
        model: model || undefined,
        apiKey: apiKeyInput || undefined,
      });
      setTestResult(construirResultadoPrueba(resultado));
    } catch (error) {
      // Fix reviewer (bloqueante B3): antes esto era un mensaje fijo que descartaba el
      // mensaje real del servidor — un 429 por tope de intentos o un 400 de validacion
      // (ambos ya en criollo, listos para mostrar) se veian como la misma frase generica.
      setTestResult({ esExito: false, texto: getApiErrorMessage(error, MENSAJE_ERROR_PRUEBA_GENERICO) });
      setTesting(false);
      return;
    }

    setTesting(false);

    // La prueba puede haber actualizado la foto guardada en el motor (solo si probo
    // exactamente la configuracion guardada). Se refresca SOLO la foto, en su PROPIO
    // intento aislado — si el refresco falla, NO pisa el resultado que ya se mostro
    // arriba (fix reviewer, bloqueante B3: antes vivia en el mismo try que la prueba).
    const settingsData = await refrescarFotoTrasPrueba(() => api.get("/settings/ai"));
    if (settingsData) setSettings(settingsData);
  };

  const handleCancelar = () => {
    if (!settings) return;
    aplicarSettingsAlFormulario(settings, providers);
    setSaveError(null);
    setFieldErrorClave(null);
    setFieldErrorBaseUrl(null);
    setFieldErrorModel(null);
    setTestResult(null);
  };

  const handleGuardar = async () => {
    setSaveError(null);
    setFieldErrorClave(null);
    setFieldErrorBaseUrl(null);
    setFieldErrorModel(null);

    // Fix reviewer (hallazgo menor 2, §15.6 "quedan obligatorios"): con "Otra", Dirección
    // y Modelo se validan ACA (usabilidad — el error real y definitivo lo hace igual el
    // motor al guardar) para mostrar el error corto pegado al campo en vez de mandar un
    // guardado que el servidor va a rechazar igual.
    const { baseUrlError, modelError } = validarAjustesAvanzados({
      requiresManualEndpoint: Boolean(selectedProvider?.requiresManualEndpoint),
      baseUrl,
      model,
    });
    if (baseUrlError || modelError) {
      setFieldErrorBaseUrl(baseUrlError);
      setFieldErrorModel(modelError);
      return;
    }

    setSaving(true);
    try {
      // Mismo motivo que en la prueba: baseUrl/model se mandan siempre, no solo cuando
      // el acordeon esta abierto (ver comentario en handleProbarConexion).
      const actualizado = await api.put("/settings/ai", {
        providerCode,
        baseUrl: baseUrl || undefined,
        model: model || undefined,
        apiKey: apiKeyInput || undefined,
      });
      aplicarSettingsAlFormulario(actualizado, providers);
      setTestResult(null);
      showSuccess("Listo, la inteligencia artificial quedó configurada.");
    } catch (error) {
      const mensaje = getApiErrorMessage(error, MENSAJE_ERROR_GUARDADO_GENERICO);
      // Regla de la spec (§15.8): "cambiaste de proveedor sin pegar clave nueva" es un
      // error CORTO pegado al campo Clave, no el cartel rojo general de arriba. Fix
      // reviewer (hallazgo menor 4): se mira PRIMERO el codigo estructurado que ya manda
      // el motor (ProblemDetails.Extensions.validationCode === "aiClaveFaltante"); el
      // match de texto queda como fallback para una respuesta vieja sin el codigo.
      if (detectarErrorDeClaveFaltante({ validationCode: error?.payload?.validationCode, mensaje })) {
        setFieldErrorClave(mensaje);
      } else {
        setSaveError(mensaje);
      }
      // La pantalla queda intacta a proposito (§15.8: "con todo lo cargado intacto"):
      // no se toca ni el formulario ni la seleccion del usuario ante un error de guardado.
    } finally {
      setSaving(false);
    }
  };

  // Fix reviewer (bloqueante B1): la clave guardada es de OTRO proveedor si el usuario
  // cambio la seleccion de radio respecto al `providerCode` que trae el ultimo GET/PUT.
  // Sin esto, el campo seguia mostrando "Configurada ✓" con el prefijo del proveedor
  // viejo despues de elegir uno nuevo, y el Guardar fallaba en silencio.
  const cambioDeProveedor = Boolean(settings) && providerCode !== settings.providerCode;

  const modoClave = settings
    ? calcularModoCampoClave({
        hasApiKey: settings.hasApiKey,
        apiKeySource: settings.apiKeySource,
        queriendoCambiarClave,
        cambioDeProveedor,
      })
    : null;

  const modoPantalla = resolverModoDePantalla({ loading, loadError });

  if (modoPantalla === AI_SCREEN_MODE.LOADING) {
    return (
      <div className="max-w-3xl mx-auto space-y-4 animate-pulse" data-testid="ai-settings-loading">
        <div className="h-10 rounded-xl bg-slate-100 dark:bg-slate-800" />
        <div className="h-16 rounded-2xl bg-slate-100 dark:bg-slate-800" />
        <div className="h-64 rounded-2xl bg-slate-100 dark:bg-slate-800" />
      </div>
    );
  }

  if (modoPantalla === AI_SCREEN_MODE.LOAD_ERROR) {
    return (
      <div className="max-w-3xl mx-auto" data-testid="ai-settings-load-error">
        <div className="flex flex-col items-center gap-3 rounded-2xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 py-10 text-center">
          <p className="text-sm text-rose-600 dark:text-rose-400">{loadError}</p>
          <button
            type="button"
            onClick={cargarConfiguracion}
            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-bold text-slate-600 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
            data-testid="ai-settings-load-retry"
          >
            <RefreshCw className="h-3.5 w-3.5" />
            Reintentar
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="max-w-3xl mx-auto space-y-6">
      <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm p-6 space-y-6">
        <div>
          <h2 className="text-2xl font-bold text-slate-900 dark:text-white flex items-center gap-3">
            <Sparkles className="h-6 w-6 text-indigo-600" />
            Inteligencia artificial
          </h2>
          <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">
            El sistema usa la inteligencia artificial para entender lo que escribís al cargar un
            servicio y para ordenar el tarifario. Si no hay nada configurado, todo funciona igual,
            sin esas ayudas.
          </p>
        </div>

        {settings && (
          <AiStatusBanner
            statusCode={settings.statusCode}
            providerDisplayName={settings.providerDisplayName}
            providerCode={settings.providerCode}
          />
        )}

        <AiProviderRadioList providers={providers} selectedCode={providerCode} onSelect={handleSelectProvider} />

        {modoClave && (
          <AiApiKeyField
            modo={modoClave}
            providerDisplayName={selectedProvider?.displayName}
            providerCode={selectedProvider?.code}
            apiKeyPrefix={settings?.apiKeyPrefix}
            apiKeyInput={apiKeyInput}
            onChangeApiKeyInput={handleChangeApiKeyInput}
            onCambiarClave={() => {
              setQueriendoCambiarClave(true);
              setTestResult(null);
            }}
            onCancelarCambio={() => {
              setQueriendoCambiarClave(false);
              setApiKeyInput("");
              setTestResult(null);
            }}
            fieldError={fieldErrorClave}
          />
        )}

        <AiTestConnectionRow testing={testing} resultado={testResult} onProbar={handleProbarConexion} />

        <AiAdvancedSettings
          open={showAdvanced}
          forced={Boolean(selectedProvider?.requiresManualEndpoint)}
          onToggle={() => setShowAdvanced((open) => !open)}
          baseUrl={baseUrl}
          model={model}
          onChangeBaseUrl={handleChangeBaseUrl}
          onChangeModel={handleChangeModel}
          onVolverARecomendados={handleVolverARecomendados}
          puedeVolverARecomendados={Boolean(selectedProvider && !selectedProvider.requiresManualEndpoint)}
          baseUrlError={fieldErrorBaseUrl}
          modelError={fieldErrorModel}
        />

        {saveError && (
          <div
            role="alert"
            className="rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-900/40 dark:bg-rose-900/10 dark:text-rose-300"
          >
            {saveError}
          </div>
        )}

        <div className="flex justify-end gap-2 pt-2">
          <button
            type="button"
            onClick={handleCancelar}
            disabled={saving}
            className="rounded-xl border border-slate-200 dark:border-slate-700 px-5 py-2.5 text-sm font-semibold text-slate-600 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-slate-800 disabled:opacity-50"
          >
            Cancelar
          </button>
          <button
            type="button"
            onClick={handleGuardar}
            disabled={debeDeshabilitarBotonGuardar({ hasApiKey: Boolean(settings?.hasApiKey), claveTipeada: apiKeyInput, guardando: saving })}
            className="inline-flex items-center gap-2 rounded-xl bg-indigo-600 hover:bg-indigo-700 px-6 py-2.5 text-sm font-semibold text-white shadow-sm disabled:opacity-50"
          >
            {saving && <Loader2 className="h-4 w-4 animate-spin" />}
            {saving ? "Guardando…" : "Guardar"}
          </button>
        </div>
      </div>
    </div>
  );
}
