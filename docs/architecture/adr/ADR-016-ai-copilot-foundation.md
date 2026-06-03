# ADR-016 — Base del Copiloto de IA (cerebro configurable + piloto "cliente por vencer")

- **Status**: ✅ **Ajustado y listo para construir F0a (2026-06-03)** — pasó por `software-architect-reviewer` (veredicto "Adjust before building": diseño sólido, sin reescritura; ajustes de secuenciación + 2 decisiones cerradas). Ver §0bis "Decisiones y ajustes post-review".
- **Status original**: Proposed (Round 1 — NO implementado).
- **Date**: 2026-06-02.
- **Author(s)**: software-architect agent.
- **Driver de negocio**: Gaston (dueño). Visión aprobada en `docs/producto/vision-copiloto-ia-2026-06-02.md`: un copiloto de IA transversal construido como UN solo cerebro central reutilizable, que aprende del cliente vía supervisión humana. Arrancamos por UN caso de uso piloto de bajo riesgo.
- **Related**:
  - `docs/producto/vision-copiloto-ia-2026-06-02.md` — visión + decisión de modelo (Groq/Gemini/OpenRouter).
  - `AlertService.cs` / `AlertsContext.jsx` — sistema de alertas existente que el piloto enriquece.
  - `OperationalFinanceSettings` + `OperationalFlagsContext.jsx` — patrón de flags ya establecido.
  - `BnaExchangeRateService.cs` — patrón de integración HTTP externa con degradación elegante (referencia para resiliencia).

> **Aviso de alcance.** Esto diseña la BASE: la capa de IA configurable + el primer piloto + el esqueleto del loop de aprendizaje. NO diseña el chat, ni "ayudame con esto" contextual, ni la clasificación fiscal de cancelación. Esos se enchufan al cerebro después, caso por caso.

---

## 0bis. Decisiones y ajustes post-review (2026-06-03)

Cierre del review de `software-architect-reviewer` (veredicto: *Adjust before building*). El diseño no se reescribe; se ajusta secuenciación y alcance del primer merge, y se cierran 2 preguntas abiertas.

### Decisiones del dueño (Gaston)
- **Proveedor del piloto = Groq** (cierra §6.4). Razón: el piloto manda PII de clientes (nombre + saldo) a la nube; Groq no usa los datos para entrenar. La visión ya reservaba la privacidad por defecto. Cambiar de proveedor sigue siendo env + restart, sin código.
- **Definición del piloto = ENRIQUECER** las alertas existentes con una frase IA, NO que la IA detecte sola los vencimientos (confirma §3.1 / §6.1). Bajo riesgo: el QUÉ alertar lo sigue decidiendo la query determinística.
- **Audiencia (§6.2)**: se difiere a F1 (no bloquea F0). Default propuesto: solo-Admin (como hoy las alertas), a confirmar antes de F1.
- Preguntas §6.3 (loop), §6.5 (cadencia), §6.6 (chip de prioridad) diferidas. Recomendación: **sin chip de prioridad en el piloto** (menos output = menos a validar).

### Ajustes de arquitectura (del review)
- **F0 se parte en F0a + F0b** (ver §8 actualizado). F0a = spike mínimo y validable contra Groq real ANTES de invertir en toda la robustez. F0b = endurecimiento (breaker, métricas persistentes, auditoría, truncado).
- **Precondición P1 — `AiChatResult` con vocabulario NEUTRO** (no formato OpenAI): expone `Text`, `ApproxTokens`, `WasTruncated`, `RawFinishReason` (string opaco). El mapeo desde el JSON estilo OpenAI vive DENTRO de `OpenAiCompatibleChatProvider`. Esto hace real el aislamiento que promete R4.
- **Precondición P2 — cuota cross-process**: el circuit breaker y el contador de uso son **per-process** y eso es suficiente MIENTRAS el único caller sea el job del worker. Queda escrito como precondición dura: **antes de sumar cualquier caller inline (ej. chat en la API), el rate-limit/contador debe moverse a un store compartido (DB/cache)**. No construir el breaker compartido ahora, pero NO diseñar asumiendo que el contador en memoria alcanza para 2 procesos.
- **Precondición P3 — idempotencia atómica del job (F1)**: el job debe marcar el ítem "en proceso/fresco" de forma atómica ANTES de llamar a la IA (no después), para que una re-ejecución solapada de Hangfire no gaste 2 llamadas de cuota. Mismo patrón que `ProcessPartialCreditNoteJob`.
- **`AiFeedbackExample` NO se construye en F0/F1.** Se reserva el CONCEPTO (capability como eje del few-shot transversal) pero la tabla se materializa recién en F3, con la UI de captura de F2 ya definida. Migración aditiva → esperar no cuesta; congelar un esquema mal cuesta.
- **Sin esqueleto de inyección few-shot en F0/F1**: solo el contrato de método. La interfaz prevé el futuro; la implementación no paga por él todavía.
- **Frescura (F1)**: invalidar el texto IA cuando cambian los campos que entraron al prompt (Balance/StartDate), no solo por TTL temporal.
- **Test obligatorio (no opcional) en F1**: assert de que el prompt NO contiene PII prohibida (CUIT, datos de pago).

---

## 1. Contexto

### 1.1 Hechos verificados en el repo (2026-06-02)

Confirmados leyendo código, no asumidos:

- **Arquitectura en capas (Clean Architecture).** `TravelApi.Domain` (entidades), `TravelApi.Application` (interfaces + DTOs), `TravelApi.Infrastructure` (implementaciones de servicios), `TravelApi` (API/controllers). Las interfaces viven en `Application/Interfaces`, las implementaciones en `Infrastructure/Services`. Ejemplo: `IBnaExchangeRateService` → `BnaExchangeRateService`.
- **Integraciones HTTP externas usan `IHttpClientFactory`.** `Program.cs:415` `AddHttpClient()`; `Program.cs:416` typed client `AddHttpClient<IAfipService, AfipService>()`. `BnaExchangeRateService` crea el client vía factory y le setea `Timeout = 10s` por llamada.
- **Patrón de resiliencia ya existe y es bueno.** `BnaExchangeRateService.GetUsdSellerRateAsync` hace: cache en memoria → fetch con timeout → fallback a cache → fallback a snapshot persistido → devuelve `null` con warning si todo falla. NUNCA tira hacia arriba para una funcionalidad no crítica. Este es el modelo a replicar para la IA.
- **Secrets van por variable de entorno, validados en deploy.** `scripts/ops/deploy.sh:35-48` define `required_secrets` (JWT_KEY, SECURITY_ENCRYPTION_KEY, etc.) y aborta el deploy si están vacíos o con placeholder. Se leen vía `builder.Configuration["X:Y"] ?? builder.Configuration["X__Y"]` (`Program.cs:77-79`). `.env.example` documenta cada uno con valor `CHANGE_THIS_*`.
- **Los flags operativos viven en `OperationalFinanceSettings`** (entidad singleton en DB, `Id` único). Se editan vía `PUT /api/settings/operational-finance` (Admin) con patrón patch-like (solo se persiste lo que viene con valor) y validación cruzada entre flags. Se EXPONEN read-only al frontend vía `GET /afip/settings` (`AfipController:40-52`), que el frontend consume desde `OperationalFlagsContext.jsx` (un solo fetch montado en `PrivateRoute`).
- **El sistema de alertas ya existe.** `AlertService.GetAlertsAsync` devuelve `{ UrgentTrips, SupplierDebts, TotalCount }`. `UrgentTrips` = reservas activas (Sold/Confirmed/Traveling) con `StartDate` entre hoy y `hoy + UpcomingUnpaidReservationAlertDays` y `Balance > 0`, ordenadas por `StartDate`. El frontend (`AlertsContext.jsx`) lo poll-ea cada 30s (solo Admin) y lo muestra. Hay además `UpcomingUnpaidReservationAlertDays` (default 7) y `EnableUpcomingUnpaidReservationNotifications` ya en settings.
- **Hay un canal de notificaciones persistidas** (`NotificationsController`, `/notifications`, `markAsRead`), separado de las alertas calculadas en vivo.

### 1.2 Restricciones duras (del dueño)

1. Cambiar de proveedor de IA SIN reescribir código (Groq hoy; Gemini/OpenRouter/otros mañana — todos OpenAI-compatible: cambian base_url + api_key + modelo).
2. Presupuesto ~$0 (free tier). El VPS no corre el modelo; solo HTTP a la API externa.
3. Producto multi-cliente (1 install por cliente): cada install configura su propio proveedor/clave. Nada hardcodeado.
4. La API key NO va al repo: variable de entorno / secret.
5. Detrás de un flag. Con flag OFF, el copiloto no existe (byte-idéntico al comportamiento actual).
6. Validar la salida del modelo: el "JSON garantizado" no es 100% confiable → deserialización estricta + reintento, nunca confiar ciego.

---

## 2. Decisión

### 2.1 El cerebro: una interfaz, una implementación, configuración en dos lugares

Se introduce **una sola abstracción de proveedor de chat/IA** en la capa Application, con una implementación OpenAI-compatible en Infrastructure. Todos los módulos consumen el cerebro a través de un **servicio de orquestación** (`IAiAssistantService`), nunca el cliente HTTP crudo.

```
TravelApi.Application/Interfaces/
  IAiChatProvider.cs        # abstracción de bajo nivel (un turno de chat → texto/JSON)
  IAiAssistantService.cs    # orquestación de alto nivel que consumen los módulos

TravelApi.Application/Ai/    # DTOs/contratos del cerebro (request, response, options)
  AiChatRequest.cs, AiChatMessage.cs, AiChatResult.cs, AiProviderOptions.cs

TravelApi.Infrastructure/Ai/
  OpenAiCompatibleChatProvider.cs   # única implementación; sirve Groq, Gemini, OpenRouter
  AiAssistantService.cs             # resiliencia, validación, fallback, métricas
```

**Por qué dos niveles y no uno:**
- `IAiChatProvider` es el "único punto de integración con el modelo" que pide la visión. Es deliberadamente tonto: recibe mensajes + opciones, hace UN POST a `{baseUrl}/chat/completions`, devuelve el texto crudo. No sabe de dominio.
- `IAiAssistantService` es donde vive la inteligencia reutilizable: armado de prompt, inyección de ejemplos few-shot (loop de aprendizaje), validación/parseo estricto del JSON, reintento, timeout, degradación elegante, y emisión de métricas/auditoría. Los módulos (alertas, futuro chat, futuro "ayudame con esto") consumen ESTE servicio.

**Por qué una sola implementación de provider:** la visión y la investigación confirman que Groq, Gemini y OpenRouter son TODOS OpenAI-compatible (cambian solo base_url + api_key + modelo). NO se construye una implementación por proveedor (eso sería sobre-ingeniería y una abstracción que no remueve duplicación real). Se construye UNA implementación parametrizada por config. Si algún día aparece un proveedor que NO sea OpenAI-compatible, ahí —y solo ahí— se agrega una segunda implementación de `IAiChatProvider` detrás de la misma interfaz.

### 2.2 Dónde vive la configuración (split deliberado: secret en env, resto en DB)

| Config | Dónde | Por qué |
|---|---|---|
| `Ai__ApiKey` | Variable de entorno / secret (igual que JWT_KEY) | Es un secreto. NUNCA al repo ni a la DB en claro. Se agrega a `required_secrets` de `deploy.sh` SOLO si el flag está pensado para prenderse en ese install (ver §2.3). |
| `Ai__BaseUrl` | Env (con default Groq) | Cambia por proveedor. No es secreto pero acompaña a la key y al modelo; tenerlo junto a la key evita config mixta. |
| `Ai__Model` | Env (con default) | Idem. Volátil (los modelos free cambian de nombre). |
| `Ai__TimeoutSeconds`, `Ai__MaxTokens`, `Ai__MaxRetries` | Env con defaults sanos | Tuning operativo, no secreto. |
| `EnableAiCopilot` (flag maestro) | `OperationalFinanceSettings` (DB) | Sigue EXACTAMENTE el patrón de los demás flags (`EnableMultiCurrencyInvoicing`, etc.): editable desde el panel admin, expuesto read-only por `/afip/settings`. |
| `EnableAiUpcomingClientAlerts` (flag del piloto) | `OperationalFinanceSettings` (DB) | Permite prender el cerebro sin prender el piloto, y viceversa-no (ver validación cruzada). |

**Por qué la key va a env y no a la DB:** consistencia con cómo el proyecto ya maneja TODOS los secretos (JWT, MinIO, RabbitMQ). Meter una API key en una tabla de settings editable por Admin sería un retroceso de seguridad (quedaría en backups de DB en claro, visible en queries, etc.). El panel admin maneja COMPORTAMIENTO (flags), no CREDENCIALES.

**Por qué base_url/model van a env y no a la DB:** son inseparables de la key (un base_url de Groq con una key de Gemini no funciona). Mantener los tres juntos en env evita estados incoherentes y deja el "cambio de proveedor" como una operación atómica de redeploy con tres variables. Trade-off explícito: cambiar de proveedor requiere editar `.env` + restart, NO un toggle en el panel. Es aceptable porque cambiar de proveedor es una operación rara y deliberada del operador, no algo cotidiano. (Si en el futuro se quisiera cambiar sin restart, se podría mover base_url/model a la DB y dejar SOLO la key en env — la interfaz no cambia. Se deja como evolución posible, no se construye ahora.)

### 2.3 Cómo se cambia de proveedor SIN tocar código

Operación completa para migrar de Groq a (p.ej.) Gemini:

1. Editar `.env`:
   ```
   Ai__BaseUrl=https://generativelanguage.googleapis.com/v1beta/openai/
   Ai__ApiKey=<key de Gemini>
   Ai__Model=gemini-2.0-flash
   ```
2. `restart` de api + worker.

Cero cambios de código, cero migración. Eso es exactamente lo que pide la restricción 1. La implementación `OpenAiCompatibleChatProvider` no contiene el string "groq" en ninguna parte salvo el default de `.env.example`.

### 2.4 Manejo de errores, timeouts, rate limits, reintentos, fallback

`AiAssistantService` aplica, replicando el espíritu de `BnaExchangeRateService` (degradación elegante para funcionalidad no crítica):

- **Timeout:** por llamada, desde `Ai__TimeoutSeconds` (default 15s). Vía `IHttpClientFactory` + `CancellationToken` encadenado.
- **Reintentos:** `Ai__MaxRetries` (default 2). Se reintenta SOLO en: (a) timeout/error de red, (b) HTTP 429 (rate limit) respetando `Retry-After` si viene, (c) HTTP 5xx, (d) respuesta que NO deserializa al schema esperado (un reintento con instrucción reforzada de "responde SOLO JSON válido"). NO se reintenta en 401/403 (config mala — se loguea error claro y se degrada).
- **Rate limit del free tier:** además del backoff por 429, el piloto NO es de tiempo real (corre en un job, ver §3), así que un 429 esporádico simplemente posterga ese ítem al próximo ciclo. Se cuenta con un contador de uso para medir consumo (visión: "arrancar con lo gratis, medir").
- **Fallback / degradación elegante:** si la IA no responde tras los reintentos, el piloto **cae al comportamiento actual** (la alerta se muestra SIN texto de IA — ver §3.4). El sistema nunca queda peor que hoy por una falla de IA. Esto es la regla de oro: **la IA enriquece, nunca bloquea.**
- **Circuit breaker liviano:** si N llamadas consecutivas fallan (config, default 5), `AiAssistantService` deja de llamar por un cooldown corto y degrada directo, para no quemar cuota ni latencia en cada request mientras el proveedor está caído. (Se puede implementar con un contador en memoria; no requiere Polly, pero Polly es aceptable si ya estuviera disponible — verificar antes de agregar dependencia.)

### 2.5 Validación / parseo robusto de la respuesta

Regla dura 6. El cerebro NUNCA confía en que el modelo devolvió JSON válido:

1. Se pide al modelo salida estructurada (response_format JSON si el proveedor lo soporta) Y se incluye el contrato en el prompt.
2. La respuesta se deserializa con `System.Text.JsonSerializer` en modo **estricto**: tipos exactos, `JsonSerializerOptions` que rechaza propiedades desconocidas en el objeto raíz crítico, y validación post-deserialización de los campos (longitud máxima, no-null donde corresponde).
3. Si falla → un reintento con instrucción reforzada.
4. Si vuelve a fallar → **degradación** (no se inventa contenido). Para el piloto: la alerta sale sin la frase de IA. Se loguea + contador `ai_output_invalid`.
5. **Límite de longitud de salida** server-side: se trunca defensivamente cualquier texto que vaya a UI, para que un modelo "verborrágico" no rompa el layout ni infle tokens.

### 2.6 Seguridad y privacidad de datos

- API key solo por env; nunca logueada (igual que el proyecto ya hace con otros secretos).
- **Minimización de datos enviados al modelo:** el piloto envía SOLO lo necesario para redactar el aviso (nombre del cliente, días hasta el viaje, saldo, número de reserva). NO se envían documentos, CUIT, datos de pago, ni PII innecesaria. Esto se hace explícito en el armado del prompt (un mapper dedicado, no "mandar la entidad entera"). Justificación: la visión acepta que los datos vayan a la nube, pero seguimos siendo responsables de minimizar (son datos de terceros / clientes de la agencia).
- El `security-data-risk-reviewer` DEBE revisar el mapper de datos→prompt antes de prender el flag (toca passengers/clientes/saldos).
- Con flag OFF: cero datos salen del sistema (el cerebro ni se instancia / no se llama).

### 2.7 Trazabilidad / auditoría

Cada invocación al cerebro registra (sin el contenido sensible del prompt en claro si la política lo exige): timestamp, módulo que llamó, modelo usado, tokens aproximados, latencia, resultado (ok / inválido / timeout / degradado). Esto alimenta la medición de costo/adopción que pide la visión y da base para el loop de aprendizaje.

---

## 3. El piloto: "cliente por vencer" (definición concreta — A VALIDAR CON EL DUEÑO)

### 3.1 Qué significa concretamente (propuesta)

**El piloto NO crea un concepto de alerta nuevo. Enriquece las alertas que YA existen.** El dato duro "cliente/reserva por vencer" ya lo calcula `AlertService` (`UrgentTrips`: reserva activa, viaje inminente, saldo > 0). El copiloto agrega, encima de ese dato, una **frase en lenguaje natural redactada por IA** que resume y prioriza el aviso para el equipo interno.

Ejemplo. Hoy la alerta es una fila: `Reserva 1234 — Juan Pérez — sale 09/06 — saldo $250.000`. El piloto agrega: *"Juan Pérez viaja en 5 días y todavía debe $250.000. Conviene contactarlo hoy para cerrar el cobro antes del viaje."*

**Por qué esta definición (y no "la IA detecta sola los vencimientos"):**
- Bajo riesgo: el QUÉ alertar lo sigue decidiendo la query determinística existente (auditada, fiscal-neutra). La IA solo redacta el CÓMO se comunica al equipo. Si la IA falla, la alerta cruda sigue ahí.
- No toca fiscal, ni plata, ni acciones automáticas. Es texto de ayuda al equipo interno (no al cliente — eso es el bot de WhatsApp, fuera de alcance por la visión).
- Reusa toda la infra de alertas (`/alerts`, `AlertsContext`, polling) → cambio mínimo, alto valor visible.
- Encaja con el loop de aprendizaje: el humano puede corregir/editar la frase → ejemplo few-shot.

**Marcado como A VALIDAR CON EL DUEÑO** — preguntas en §6.

### 3.2 Alternativa considerada para el piloto (y por qué no)

"Que la IA decida sola qué clientes están por vencer mirando todo el dataset." Rechazada para el piloto: mayor riesgo (la IA podría omitir o inventar casos), mayor costo (más datos al modelo), y compite con una query que ya funciona bien. La selección determinística + redacción IA es el punto pragmático.

### 3.3 Flujo de punta a punta (piloto)

**Backend (recomendado: enriquecer en un job, no en el request de `/alerts`):**

1. Un job en background (worker) corre cada X (alineado con la cadencia de alertas; p.ej. cada 15-30 min) — **solo si `EnableAiCopilot && EnableAiUpcomingClientAlerts`**.
2. Toma las `UrgentTrips` actuales (misma query que `AlertService`).
3. Para cada una (o en lote, con límite para respetar cuota), arma un prompt mínimo (§2.6) y pide a `IAiAssistantService` una frase corta + un nivel de prioridad sugerido (enum acotado, validado).
4. Persiste el resultado enriquecido (texto IA + prioridad) asociado a la reserva, con TTL/marca de frescura (para no re-llamar lo mismo cada ciclo — clave para la cuota free).
5. `AlertService.GetAlertsAsync` (o un campo nuevo en su respuesta) adjunta el texto IA si existe y está fresco; si no existe, devuelve la alerta cruda como hoy.

**Por qué job y no inline en el request `/alerts`:** `/alerts` se poll-ea cada 30s por cada Admin. Llamar a la IA en cada poll quemaría la cuota free en minutos y agregaría latencia/timeout al dashboard. El job desacopla el costo de IA del tráfico de UI y hace el rate-limit del free tier manejable. Trade-off: el texto IA aparece con algunos minutos de latencia respecto del dato crudo. Aceptable (es un aviso, no tiempo real).

**Frontend:**

6. `AlertsContext` ya trae las alertas; se muestra el texto IA debajo/al lado de la fila cruda cuando viene.
7. El flag se lee del `OperationalFlagsContext` (mismo patrón que los demás): si `EnableAiCopilot`/piloto está OFF, la UI no muestra nada nuevo (idéntico a hoy).
8. **Anti-parpadeo:** diferir el render del bloque IA hasta tener los flags (mismo aprendizaje que ya se aplicó en `OperationalFlagsContext`).

### 3.4 Estados de UX (obligatorios)

- **Sin IA / flag OFF:** alerta cruda como hoy. (default, byte-idéntico)
- **IA pendiente (job aún no corrió para ese ítem):** alerta cruda, sin bloque IA.
- **IA ok:** alerta cruda + frase IA + (opcional) chip de prioridad.
- **IA falló/inválida:** alerta cruda, sin bloque IA (degradación silenciosa). No mostrar errores de IA al usuario por una alerta.
- **Marca visual de "generado por IA":** el texto debe estar claramente etiquetado como sugerencia de IA (honestidad + base para el feedback del loop).

---

## 4. El loop de aprendizaje (esqueleto PREVISTO, no construido entero en el piloto)

La visión pide que las correcciones humanas se guarden por cliente (agencia) como ejemplos few-shot/RAG, y que apliquen a TODO el copiloto, no solo al piloto. Para no pintarnos en un rincón, el diseño RESERVA la estructura desde ahora aunque el piloto la use mínimamente:

**Entidad prevista (no necesariamente construida en el MVP del piloto):** `AiFeedbackExample`

- `Id`
- `Capability` (string/enum): a qué caso de uso pertenece (p.ej. `"upcoming_client_alert"`, futuro `"reservation_summary"`, `"cancellation_classification"`). **Clave**: el few-shot se filtra por capability, así el aprendizaje es transversal pero no mezcla dominios.
- `InputContext` (json): los datos que se le dieron al modelo (minimizados).
- `AiOutput` (text): lo que generó la IA.
- `CorrectedOutput` (text, nullable): lo que el humano corrigió (si corrigió).
- `Feedback` (enum): `Accepted` / `Edited` / `Rejected`.
- `UserId`, `CreatedAt`.

**Cómo se usaría a futuro:** al armar el prompt para una capability, `AiAssistantService` levanta los N ejemplos `Edited`/`Accepted` más recientes/relevantes de ESA capability y los inyecta como few-shot. Para escalar (cuando haya muchos ejemplos) se evoluciona a recuperación por similitud (RAG) — pero la interfaz `IAiAssistantService` NO cambia; cambia solo la estrategia de selección interna. Por eso el diseño no se pinta en un rincón.

**Qué se construye en el piloto (mínimo viable):** capturar el feedback (Accepted/Edited/Rejected + texto corregido) cuando el usuario interactúe con la frase IA, y persistirlo en `AiFeedbackExample`. La INYECCIÓN few-shot puede quedar como paso 2 (el piloto puede correr sin few-shot al principio). Esto es una decisión a confirmar con el dueño según cuánto del loop quiere ver funcionando en el piloto (§6).

---

## 5. Consecuencias

**Positivas:**
- Un solo punto de integración con el modelo → un solo lugar para cuidar costo, seguridad, resiliencia (exactamente la visión).
- Cambio de proveedor = 3 variables de env + restart. Sin código, sin migración.
- Con flag OFF, cero cambios de comportamiento y cero datos salen del sistema.
- El piloto reusa infra existente (alertas) → footprint chico, valor visible.
- El loop de aprendizaje queda con estructura desde el día 1 → los próximos casos de uso se enchufan sin rediseñar el cerebro.

**Negativas / costos:**
- El texto IA en alertas aparece con latencia (job, no inline). Aceptado.
- Cambiar base_url/model requiere restart (no es un toggle de panel). Aceptado; evolución futura posible.
- Se agrega una dependencia externa (proveedor de IA) con cuota volátil. Mitigado por degradación elegante + medición.
- Nueva tabla (`AiFeedbackExample`) y posible tabla de enriquecimiento de alertas → migración aditiva.

---

## 6. Preguntas abiertas (a validar con el dueño / reviewers)

1. **Definición del piloto (§3.1):** ¿confirmás que el piloto = enriquecer con IA las alertas de "viaje inminente con saldo" que ya existen, y NO que la IA detecte sola los vencimientos? (Mi recomendación: sí, enriquecer.)
2. **Audiencia:** las alertas hoy son solo para Admin (`isAdmin()` en `AlertsContext`). ¿El piloto sigue solo-Admin o también vendedores? (Afecta permisos y volumen de llamadas.)
3. **Loop en el piloto (§4):** ¿querés ver el loop completo (capturar feedback + inyectar few-shot) en el piloto, o alcanza con capturar feedback ahora e inyectar después?
4. **Proveedor del piloto:** la visión dice Groq (privacidad por defecto, son datos de clientes) o Gemini (más fácil). Para un piloto que manda nombre+saldo de clientes, ¿confirmás Groq por privacidad?
5. **Frescura/cadencia del job:** ¿cada cuánto es razonable refrescar el texto IA? (define consumo de cuota).
6. **¿Prioridad sugerida por IA?** ¿Querés que la IA además sugiera un nivel de urgencia (chip), o solo la frase? (Más output = más a validar.)

## 7. Riesgos

- **R1 — Cuota free tier insuficiente con uso real.** La visión ya lo anticipa. Mitigación piloto: job + caché de frescura + límite por lote. Medir antes de sumar casos de uso.
- **R2 — Salida del modelo inválida/alucinada.** Mitigación: validación estricta + reintento + degradación (§2.5). Para el piloto el riesgo es bajo (es texto de ayuda interno, no acción).
- **R3 — Fuga de datos de clientes a la nube.** Mitigación: minimización (§2.6) + revisión de `security-data-risk-reviewer` del mapper datos→prompt antes de prender.
- **R4 — Acoplamiento oculto al "ser OpenAI-compatible".** Si un futuro proveedor no lo es, hay que agregar una 2ª implementación de `IAiChatProvider`. Mitigado porque la interfaz ya existe; el costo está acotado a Infrastructure.
- **R5 — Costo de latencia en el dashboard.** Mitigado por el diseño job (no inline).

---

## 8. Plan de implementación (incremental, detrás de flag)

1. **F0a — Spike validable (cerebro mínimo, sin piloto):** interfaces `IAiChatProvider` / `IAiAssistantService` + DTOs en Application (`AiChatResult` con vocabulario NEUTRO, P1); `OpenAiCompatibleChatProvider` mínimo (un POST a `{baseUrl}/chat/completions`, timeout, mapeo del JSON OpenAI a `AiChatResult` dentro del provider) + `AiAssistantService` con SOLO deserialización estricta + 1 reintento + degradación elegante; registro en `Program.cs` (`AddHttpClient` typed); config de env (`Ai__ApiKey/BaseUrl/Model/...`, default Groq) + `.env.example`; flag `EnableAiCopilot` en `OperationalFinanceSettings` (default false) + exposición read-only en `/afip/settings`. Smoke con `FakeAiChatProvider` en CI (NO llamar a la nube) + smoke MANUAL real contra Groq en staging. **Sin breaker, sin métricas persistentes, sin few-shot.** Flag OFF = byte-idéntico.
   - **Objetivo de F0a:** probar que el cerebro habla con Groq real desde el VPS y que el cambio de proveedor por env funciona, ANTES de invertir en robustez sobre un contrato no validado.
1b. **F0b — Endurecimiento del cerebro:** circuit breaker liviano (per-process, con la nota P2 documentada como limitación), contador/métrica de uso persistente + auditoría de invocaciones (§2.7), truncado defensivo de salida, reintentos completos (429 con Retry-After, 5xx). Se mergea cuando F0a probó el contrato real.
2. **F1 — Piloto backend:** flag `EnableAiUpcomingClientAlerts` (+ validación cruzada: requiere `EnableAiCopilot`); mapper minimizado datos→prompt; job de enriquecimiento; persistencia del enriquecimiento + frescura; adjuntar a la respuesta de alertas.
3. **F2 — Piloto frontend:** mostrar la frase IA (+ chip si aplica) gateado por flags vía `OperationalFlagsContext`; estados de UX (§3.4); etiqueta "IA".
4. **F3 — Loop (mínimo):** entidad `AiFeedbackExample` + captura de Accepted/Edited/Rejected. (Inyección few-shot: F4, opcional según respuesta §6.3.)

Cada fase mergeable con flag OFF (byte-idéntico). Revisores por fase: backend-reviewer + security-data-risk-reviewer (F0/F1, tocan datos de clientes), frontend-reviewer (F2), qa-automation-senior (flujo de alertas).

---

## 9. Estrategia de rollback

- **Inmediato:** apagar `EnableAiCopilot` desde el panel admin (Admin) → el copiloto deja de existir, alertas vuelven a crudo. Sin deploy.
- **Por proveedor:** si el proveedor falla o cambia términos, cambiar `Ai__*` en env + restart (o apagar flag).
- **Esquema:** las migraciones son aditivas (tablas nuevas). Apagar el flag no requiere revertir migración. Si se revierte código, las tablas nuevas quedan inertes (no las usa nadie con flag OFF).

---

## 10. Estrategia de testing

- **Provider:** test con un `FakeAiChatProvider` (no se llama a la nube en CI). Cubrir: timeout, 429 con reintento, 5xx con reintento, 401 sin reintento (degrada), JSON inválido → reintento → degradación.
- **AiAssistantService:** validación estricta del JSON (caso válido / inválido / verborrágico truncado), circuit breaker, minimización (assert de que el prompt NO contiene campos prohibidos como CUIT/datos de pago).
- **Flags:** con `EnableAiCopilot=false` la respuesta de `/alerts` es byte-idéntica a hoy (test de regresión). Validación cruzada del flag del piloto.
- **Job:** idempotencia (no re-llamar lo fresco), respeto del límite por lote.
- **Frontend:** estados loading/empty/ok/degradado; flag OFF no renderiza nada nuevo.
- **No** testear contra la API real del proveedor en CI (cuota + no determinismo). A lo sumo un smoke manual en staging.
