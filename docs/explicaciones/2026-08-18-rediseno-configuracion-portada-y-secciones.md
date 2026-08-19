# 2026-08-18 (noche) — Rediseño de Configuración: portada + secciones

> Nivel: trainee. Complementa los docs de la misma sesión (tandas finales
> post-rollout y dashboard/cuentas corrientes).

## Qué pasó

Gastón pidió rediseñar Configuración entera ("hoy está horrible, fue hecha
con antigravity"). Circuito: se maquetaron 3 direcciones en Claude Design,
eligió por multiple choice la **Mezcla A+B**, dio el "me cierra", y salió a
código con spec formal en el mismo día.

## El diseño firmado (Mezcla A+B)

- **Al tocar Configuración** caés en una PORTADA de tarjetas por sección,
  agrupadas en criollo: "Tu empresa" (Agencia · Facturación · Operativa y
  Caja), "Lo que ve el cliente" (Presupuestos y PDF · WhatsApp Bot · IA) y
  "Reglas y sistema" (Workflows de aprobación · Logs y Programación). Las
  tarjetas con dato real muestran su estado: WhatsApp "Conectado" verde,
  Facturación "Homologación" ámbar; sin dato, flechita a secas.
- **Adentro de una sección** hay un menú propio a la izquierda con los
  mismos grupos para saltar entre secciones sin volver; "← Configuración"
  arriba vuelve a la portada.

## Cómo quedó el código (`f7c48400`)

- Rutas: `/settings` (portada) y `/settings/{slug}` (slugs de negocio:
  agencia, facturacion, whatsapp…). Deep-link a un slug sin permiso cae a
  la portada SIN montar el componente.
- Las 7 solapas existentes se REUSAN tal cual como contenido de cada
  sección — el rediseño fue navegación y piel, no reescribir pantallas.
- El form de Agencia se extrajo a `AgencySettingsTab` (mismos campos y
  endpoints; el "Guardar cambios" se mudó a la cabecera, sin duplicado).
- El `SettingsPage.jsx` viejo (pestañas horizontales) se BORRÓ entero.
- Visibilidad por rol idéntica a la de siempre (IA/Logs solo admin,
  Aprobaciones por permiso).
- Lógica pura en `features/settings/lib/settingsSections.js` con 17 tests.

## Enmienda de estándar firmada en el camino

La guía visual decía "títulos 24px peso 800", pero todo el sistema se
construyó con 700. Gastón eligió **dejar 700** y corregir la guía
(`docs/ux/2026-08-16-guia-rollout-estandar-visual.md`).

## Referencias

- Spec: `docs/ux/2026-08-18-spec-rediseno-configuracion-mezcla-a-b.md`
- Canvas con las maquetas (3 direcciones + mezcla firmada):
  https://claude.ai/code/artifact/18cc53e4-09bf-4584-9dd0-241d35a5c14b
- Reviews: frontend Approved (suite 3756/3756, extracción de Agencia
  comparada campo por campo) + data-exposure Approved.
