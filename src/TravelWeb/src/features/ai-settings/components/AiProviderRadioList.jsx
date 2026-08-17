/**
 * Lista de radios "¿Con cuál querés trabajar?" (§15.2). Los proveedores vienen del motor
 * (GET /settings/ai/providers, M-32) — nada se escribe a mano acá, así sumar un proveedor
 * mañana no obliga a tocar esta pantalla.
 *
 * Fix reviewer (hallazgo menor 1): la pregunta ahora es el <legend> REAL del fieldset
 * (visible, no sr-only) — antes era un <div> suelto afuera y el legend oculto decía
 * "Proveedor de inteligencia artificial", una palabra prohibida en pantalla (§15.9/P-17)
 * que igual leía el lector de pantalla.
 */
export function AiProviderRadioList({ providers, selectedCode, onSelect }) {
  return (
    <fieldset className="space-y-3">
      <legend className="text-sm font-semibold text-slate-900 dark:text-white mb-2">¿Con cuál querés trabajar?</legend>
      <div className="space-y-2">
        {providers.map((provider) => {
          const inputId = `ai-provider-${provider.code}`;
          return (
            <label
              key={provider.code}
              htmlFor={inputId}
              className={`flex items-start gap-3 rounded-[10px] border px-4 py-3 cursor-pointer transition-colors ${
                selectedCode === provider.code
                  ? "border-primary bg-primary/10"
                  : "border-slate-200 dark:border-slate-800 hover:border-slate-300 dark:hover:border-slate-700"
              }`}
            >
              <input
                type="radio"
                id={inputId}
                name="ai-provider"
                className="mt-1 h-4 w-4 text-primary focus:ring-ring"
                checked={selectedCode === provider.code}
                onChange={() => onSelect(provider)}
              />
              <span>
                <span className="block text-sm font-semibold text-slate-900 dark:text-white">
                  {provider.displayName}
                </span>
                <span className="block text-xs text-slate-500 dark:text-slate-400">{provider.tagline}</span>
              </span>
            </label>
          );
        })}
      </div>
    </fieldset>
  );
}
