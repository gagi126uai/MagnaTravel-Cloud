# Visión de producto — Copiloto de IA transversal

**Fecha:** 2026-06-02
**Origen:** del parate de la UX de cancelación surgió una visión más grande. La IA no es para un módulo puntual: es un **copiloto de toda la agencia**.

---

## La visión

Una IA que **ayuda al dueño, a los vendedores y a todos los colaboradores** a trabajar más fácil, **presente en toda la plataforma**. La cancelación de reservas (ver `vision-cancelacion-2026-06-02.md`) es **solo uno** de los lugares donde la IA ayuda, no el centro.

---

## Qué hace el copiloto (capacidades confirmadas por el dueño)

1. **Chat asistente.** Cualquiera le pregunta en lenguaje natural sobre la operación: *"¿cuánto debe el cliente X?"*, *"armame un presupuesto de..."*.
2. **"Ayudame con esto" contextual.** Acciones de IA embebidas dentro de cada pantalla: resumir una reserva, sugerir un precio, etc.
3. **Resumir y explicar.** Dada una reserva, una deuda o un estado, la IA lo resume/explica en criollo.
4. **Alertas y sugerencias proactivas.** La IA avisa sola: *"este cliente está por vencer"*, *"conviene cobrar acá"*, etc.

**Para quién:** dueño, vendedores y todos los colaboradores.

---

## Qué NO es (límites)

- **NO es la comunicación con el cliente.** Mandar mensajes/mails a los clientes va por el **bot de WhatsApp existente**, que es un canal aparte. El copiloto asiste al equipo interno, no habla con el cliente final.
- **NO es IA autónoma sin control.** Para acciones sensibles (fiscal, plata) hay supervisión humana (ver loop de aprendizaje).
- **NO se construye todo de una.** Es una plataforma; se hace por casos de uso priorizados.

---

## Principio arquitectónico central: un solo "cerebro"

No se implementa "IA suelta" en cada módulo. Se construye **una capa central de IA reutilizable** — un servicio interno que:

- **Conoce el contexto de la agencia** (datos, configuración, historial).
- Es el **único punto de integración con el modelo** (un solo lugar que conectar, cuidar y monitorear en costo).
- **Aprende del cliente en un solo lado**: el loop de supervisión (las correcciones humanas se guardan como ejemplos few-shot/RAG por cada agencia) aplica a TODO el copiloto, no solo a cancelación.
- Cualquier módulo nuevo que quiera IA se **enchufa** al cerebro; no reinventa la integración.

---

## Restricciones técnicas (del dueño y la infra)

- **Presupuesto ~$0.** Free tiers, modelos chinos/open baratos (DeepSeek, Qwen, GLM, Kimi). Abierto a que los datos vayan a la nube.
- **El VPS NO corre el modelo** (8GB RAM compartidos, CPU virtual sin AVX2, sin GPU). El modelo vive en la nube; el server solo hace llamadas HTTP.
- **Tensión a tener en cuenta:** un copiloto usado intensivamente (todo el día, varios usuarios) consume mucho más que un clasificador esporádico → es probable que supere las capas gratuitas y tenga un costo real (chico por consulta, pero acumulable). Estrategia: arrancar con lo gratis, medir, y trasladar el costo al precio del producto si crece (es un diferencial vendible).
### Decisión de modelo (investigación deep-research, 2026-06-02, fuentes oficiales)

Opciones viables a presupuesto $0 para el volumen actual:

- **Google Gemini free tier (Flash / Flash-Lite) — PRIMARIO por facilidad.** Structured output con JSON Schema garantizado *en forma* (no en corrección fiscal). Integración trivial desde .NET 8: es compatible con la API de OpenAI cambiando solo el base_url a `https://generativelanguage.googleapis.com/v1beta/openai/`. **Reserva de privacidad:** NO quedó confirmado con fuente primaria qué hace Google con los datos del *free tier* (históricamente los usa para mejorar productos) → para datos fiscales de terceros: verificar términos, o usar el tier pago (centavos a este volumen), o usar Groq.
- **Groq free tier — PLAN B, mejor privacidad por defecto.** NO retiene datos de inferencia por defecto + Zero Data Retention activable por cualquier cuenta. Compatible con OpenAI. Structured output usable pero NO 100% garantizado → validar el JSON en el backend (deserialización estricta + reintento).
- **OpenRouter modelos `:free` (GLM 4.5 Air, Kimi K2.6 — chinos) — ESCAPE multimodelo.** $0, un solo endpoint OpenAI-compatible. Límites ~20 req/min y 50/día sin créditos (o 1000/día tras cargar US$10 de por vida). Privacidad depende del provider upstream → activar ZDR + opt-out de training.

**Evitar como destino directo:** API directa de DeepSeek (almacena datos en China + entrena por defecto + falla `json_schema`) y Mistral free tier (entrena por defecto, requiere opt-out manual). Nota: los modelos chinos servidos vía OpenRouter/Groq (no-China) se rigen por la política de ESE provider, no la del laboratorio.

**Caveats:** (1) los límites/precios de free tiers son volátiles — no hardcodear, verificar al implementar. (2) "JSON garantizado" = forma, NO corrección fiscal → la clasificación sigue requiriendo validación de negocio + firma de contador (encaja con el loop de supervisión). (3) No verificados en esta ronda: Cloudflare Workers AI, GitHub Models, Qwen/GLM directos.

**Recomendación para el piloto:** Groq (privacidad por defecto, son datos fiscales) o Gemini (más fácil + JSON más confiable); decisión fina al construir. Validar siempre el JSON en el backend.

---

## Cómo se construye (enfoque por fases)

1. **Elegir el modelo/servicio** (resultado de la investigación).
2. **Construir el "cerebro"** (capa central de IA + el loop de supervisión/aprendizaje) junto con **UN primer caso de uso** de punta a punta, como piloto.
3. **Probar, medir** (calidad, costo, adopción).
4. **Sumar casos de uso** uno por uno, enchufándolos al cerebro.

Candidatos a primer caso de uso (a priorizar): alertas inteligentes ("cliente por vencer", bajo riesgo, alto valor visible), resúmenes/explicaciones (bajo riesgo), chat asistente (más ambicioso), clasificación fiscal de cancelación (alto valor pero toca lo fiscal — más delicado), sugerencia de precio (toca decisiones comerciales).

---

## Próximos pasos

1. Terminar la investigación técnica del modelo/servicio (en curso).
2. Con eso + esta visión: **priorizar el primer caso de uso** (piloto).
3. Diseñar la capa central (cerebro + loop de aprendizaje) + el piloto.
4. Construir el piloto de punta a punta detrás de un flag, probar, iterar.
