# 2026-05-21 — Criterio contador NC parcial + nuevo subagente integrado

> Nivel trainee. Ejemplos pelotudos incluidos. Documento explicativo de sesion.

## Que pasa hoy

Cerramos 4 decisiones de FC1.3 (modulo cancelacion con NC parcial), recibimos el criterio fiscal del contador sobre como emitir la NC en cancelaciones de turismo, y creamos un nuevo subagente especializado.

## Las 4 decisiones FC1.3 cerradas

| # | Decision | Para que sirve |
|---|---|---|
| 1 | Facturacion configurable **por operador/proveedor** | Cada operador en alta tiene flag: factura total al cliente o solo comision |
| 2 | Penalidades: **tabla por antelacion + override manual** | El operador define una tabla (ej: >30d 0%, <15d 50%), el vendedor puede overridear |
| 3 | Fee agencia depende de **5 dimensiones** | antelacion, monto, quien cancela, politica operador, decision vendedor |
| 4 | Items no reintegrables = **flag por item** | El vendedor marca `IsRefundable=false` al cargar items como seguro, gestion, etc. |

### Ejemplo pelotudo

Imaginate que abris una franquicia de kiosco. Cada local elige sus reglas:
- "Yo vendo todo a mi nombre" vs "Yo soy intermediario" → eso es la decision 1.
- "Yo doy 20% descuento si devuelve a tiempo" → tabla de la decision 2.
- "El envoltorio de regalo... a veces lo devuelvo, depende" → el caos de la decision 3 (5 dimensiones).
- "El seguro de transporte no se devuelve aunque devuelvas el regalo" → flag por item de la decision 4.

## Criterio del contador sobre NC parcial

Esta fue **la pieza clave** del dia. El contador nos respondio con criterio fiscal-operativo (no regla universal inventada).

### Regla base

**La NC refleja la parte del comprobante original que pierde causa economica/fiscal por la cancelacion.**

NO es "NC = monto devuelto al cliente". El monto devuelto es **financiero**. La NC es **fiscal**. Suelen coincidir, pero no siempre.

### Ejemplo pelotudo

Cliente compro 4 milanesas a $250 c/u en tu fiambreria. Vuelve y dice "me llevo 3, no 4".

❌ **Mal**: anular toda la cuenta y hacer una nueva por 3 milanesas. (NC total + factura nueva)
✅ **Bien**: hacer una nota de credito por $250 (UNA milanesa). La factura original queda valida por las 3 que se queda. (NC parcial)

Si en cambio el cliente dice "me equivoque, no eran milanesas eran supremas, anula todo" → ahi si NC total + factura nueva, porque cambia la naturaleza.

### Matriz de 8 casos

| # | Caso | Tratamiento |
|---|---|---|
| 1 | Factura total + devolucion parcial | NC parcial por importe acreditado |
| 2 | Factura total + cancelacion 100% sin retenciones | NC total |
| 3 | Factura total + penalidades/fees validos | NC parcial, neto facturado por retenido |
| 4 | Factura original confusa/mal discriminada | NC total + nueva factura |
| 5 | Solo comision + devuelve parte | NC parcial sobre comision |
| 6 | Solo comision + devuelve toda | NC total |
| 7 | Cambia naturaleza fiscal del retenido | NC total + nueva factura |
| 8 | Factura A / RI / caso sensible | Revision manual obligatoria |

### Los 2 escenarios concretos

**Escenario A**: Cliente paga $1.000.000 (factura A), cancela con 20d antelacion, operador retiene $200.000 + 1 item no reintegrable $50.000.

- Devolver al cliente: $750.000.
- **NC parcial $750.000**.
- Neto facturado final: $250.000.
- Pero **revision manual** porque es Factura A o tiene items no reintegrables.

**Escenario B**: Solo comision $100.000, cancela, retiene $50.000.

- **NC parcial $50.000** sobre factura de comision.
- Neto comision retenida: $50.000.
- Tambien **revision manual**.

## Lo que NO permite el contador

> "No permitiria que el sistema diga simplemente: NC = monto devuelto al cliente. Eso puede estar mal."

El sistema debe **calcular una liquidacion fiscal** con esta estructura:

| Campo | Ejemplo A |
|---|---|
| Factura original | $1.000.000 |
| Monto cancelado | $1.000.000 |
| Penalidad retenida | $200.000 |
| Items no reintegrables | $50.000 |
| **Importe fiscal a acreditar** | **$750.000** |
| Importe a devolver al cliente | $750.000 |
| **Neto facturado final** | **$250.000** |

Y el admin debe aprobar.

## Cuando si NC total + nueva factura (excepciones)

El contador marco 7 excepciones para usar la via conservadora:

1. Factura original mal emitida.
2. Factura original no discrimina conceptos.
3. Penalidad no estaba prevista ni aceptada por el cliente.
4. Seguro no reintegrable no estaba identificado.
5. Cambia tratamiento fiscal del concepto retenido.
6. Caso complejo Factura A / RI que el contador quiera limpiar documentalmente.
7. Cliente RI necesita claridad para credito fiscal.

## Nuevo subagente travel-agency-accountant-argentina

### Por que existe

Gaston identifico una necesidad real: tenemos 3 subagentes especialistas (`arca-tax-expert-argentina`, `accounting-expert-argentina`, `travel-agency-domain-expert`), pero para casos como FC1.3 hay que invocarlos a los 3 y cruzar respuestas manualmente.

En el mercado real, los **contadores especializados en agencias de viaje** existen como nicho profesional. Conocen profundamente RG 4540 + intermediacion vs reseller + multimoneda + Ley 25.345 + NC parcial sector turismo. Una persona, una respuesta integrada.

### Que hace el agente

Combina los 3 skills (ARCA + contable + travel-agency) y entrega respuesta UNICA con 14 secciones estructuradas (resumen ejecutivo, ejemplo pelotudo, hechos verificados, alcance impositivo, alcance contable, alcance negocio, riesgos integrados, datos requeridos, criterios aceptacion, citas normativas, necesita confirmacion, no verificado, interaccion con otros agentes).

### Cuando usarlo vs los 3 actuales

| Tipo de pregunta | Agente a usar |
|---|---|
| 100% fiscal sin componente contable ni negocio | `arca-tax-expert-argentina` |
| 100% contable sin componente fiscal ni negocio | `accounting-expert-argentina` |
| 100% de negocio sin componente fiscal/contable | `travel-agency-domain-expert` |
| Integrado (fiscal + contable + negocio cruzados) | `travel-agency-accountant-argentina` ← nuevo |

### Limitaciones (hard rules)

- No firma compromisos fiscales (sigue siendo asistente, requiere contador real para firma).
- Evidence-driven (no inventa tasas, regimenes, requisitos ARCA).
- Marca todo lo que necesita confirmacion profesional.
- Cita normativa especifica cuando aplica (RG 4540, Ley 25.345, monotributo, etc.).

### Ejemplo pelotudo

Es como tener un mecanico general, un electricista y un soldador. Cada uno te resuelve su parte, pero cuando se rompe un auto turismo modificado, queres un **mecanico de turismo de carretera** que sabe los 3 oficios aplicados a esa categoria especifica.

## Que queda pendiente

1. **Reiniciar Claude Code** para que el subagente nuevo este disponible (los agentes se cargan al inicio de sesion, no en vivo).
2. **Lanzar el subagente** con el brief completo:
   - 4 decisiones FC1.3 previas (2026-05-19).
   - 4 decisiones FC1.3 nuevas (2026-05-21).
   - Respuesta textual del contador.
   - Matriz 8 casos.
   - 2 escenarios concretos.
   - 4 preguntas abiertas.
   - Estructura pantalla liquidacion.
3. **El subagente devuelve**: modelo datos + maquina estados + criterios aceptacion + casos prueba.
4. **Pasar a `software-architect`** para implementacion.
5. **Codear**: backend-dotnet-senior + frontend-senior.

## 4 preguntas que abri y necesitan cerrarse en Fase 1

1. ¿Como detecta el sistema "cambia naturaleza fiscal del retenido"?
2. ¿Como detecta "factura original confusa"?
3. ¿Factura A siempre va a revision manual?
4. ¿Mensaje en NC parcial: parametrizable o hardcodeado?

El subagente nuevo deberia cerrar estas.

## Archivos creados/modificados

- `.claude/agents/travel-agency-accountant-argentina.md` — nuevo subagente.
- `.claude/CLAUDE.md` — actualizada seccion "Tax and accounting agents" con regla de routing del nuevo.
- `docs/explicaciones/2026-05-21-criterio-contador-nc-parcial-y-nuevo-agente.md` — este archivo.

## Commits pendientes

No se commitearon los cambios de hoy. Conviene hacer commit con:
- Nuevo agente.
- CLAUDE.md actualizado.
- Este documento explicativo.

Mensaje sugerido (para la proxima sesion despues del reinicio):

```
docs(agents): nuevo subagente travel-agency-accountant-argentina + criterio contador NC parcial

- Crea agente integrado contable+ARCA+turismo para casos sector minorista viajes
- Actualiza CLAUDE.md routing tax/accounting
- Documenta criterio contador NC parcial (matriz 8 casos + escenarios A/B)
- Cierra 4 decisiones FC1.3 nuevas (modelo facturacion, penalidades, fee 5d, items no reintegrables)
```
