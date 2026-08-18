# 2026-08-17 — Rollout del estándar visual a TODO el sistema (tandas D2 a D10)

> Nivel: trainee. Qué se hizo, por qué, y qué mirar en PROD.

## De dónde veníamos

El 16/08 Gastón firmó (multiple choice) que el estándar visual de Reservas
(documento del 11/08 + enmienda "chau sello") se aplicara **al resto del
sistema**. Ese día salieron las tandas A, B, C, D0 (moldes) y D1 (Clientes).
La sesión se cortó sin querer a mitad de la tanda D2.

Hoy se retomó exactamente ahí y se terminó **todo el rollout**: tandas D2 a
D10, cada una con su circuito completo (implementación con Sonnet → review de
frontend contra la guía → gate de data-exposure → commit → push → CI deploya).

## Qué es "el molde" (repaso de 30 segundos)

- **Un solo color de acción**: azul boleto (`--primary`). Chau índigo, chau
  botones verdes/negros/rojos rellenos.
- **Botones**: siempre `<Button>` de `components/ui/button.jsx` con 5 niveles:
  relleno azul (UNO por pantalla), outline, ghost/link, destructive (borde
  rojo discreto, jamás relleno — P-14), apagado con motivo escrito (P-9).
- **Chips de estado**: siempre `<StatusChip>` con 5 tonos: neutro / azul (en
  curso) / ámbar (te pide algo) / verde (listo) / rojo (freno).
- **Redondeos**: tarjetas 14px, controles 10px. **Letras**: mínimo 11px.
- **Prohibido tocar los huesos**: mismos campos, botones, textos y flujos.
  Esto fue piel, no estructura.

## Las tandas de hoy (cada una = un commit deployado)

| Tanda | Módulo | Commit | Qué se ve distinto |
|---|---|---|---|
| D2+D3 | Cobranzas / Facturación / Caja | `af60d0c2` | Botones al azul único, chips ARCA con el molde ("En proceso" ahora azul, no gris), 11px mínimo. |
| D4 | Proveedores / Operadores | `98f404ab` | Cuenta corriente con jerarquía B.3 (un solo azul), reversiones en rojo discreto, paneles temáticos a tarjeta neutra. |
| D5 | Tarifario | `df6a7113` | Molde en listado y formularios; fix: nunca dos "Agregar producto" azules a la vez. |
| D6 | CRM / Posibles clientes | `a67f967f` | Chips de estado del molde (Cotizado quedó ámbar — no hay violeta en el molde), acciones de la ficha con jerarquía; sin tocar el rediseño pendiente de Posibles clientes. |
| D7 | Dashboard / Ajustes / topbar | `a2aba6b8` | 36 archivos: Ajustes completos (AFIP, roles, backups, WhatsApp, IA), Login, Auditoría, Reportes, Cotizaciones, Notificaciones. "Empezar de cero" y "Restaurar backup" en destructivo discreto con sus confirmaciones intactas. Bonus: el dashboard ya no muestra el estado crudo de la reserva. |
| D8 | Módulos menores | `0a0db294` | Cancelaciones (solo piel, handlers intactos), comisiones, aprobaciones, cuentas bancarias, AFIP pendientes, movimientos, mensajes, conciliación NC, paquetes backoffice. |
| D9 | Componentes compartidos | `73e5759d` | Diálogos del sistema (ConfirmModal/CartelEmergente), fichas de servicio/pasajero, solapa Voucher (Aprobar ahora azul, Rechazar rojo discreto — el voucher que ve el cliente NO cambió), CurrencyBadge a 11px. |
| D10 | Reservas (cierre) | _(commit al aprobar review)_ | El módulo donde nació el estándar recibió el barrido completo: 44 archivos, ~380 restos. El chip firmado `ReservaStatusBadge` no se tocó. |

Cada tanda pasó por: `frontend-reviewer` (citando reglas por número, con la
suite de **3733 tests** verde en cada corrida) + `data-exposure-reviewer`
(gate obligatorio del dueño) — los bloqueantes que aparecieron (P-14 en
reversiones de Proveedores, B.3 en Tarifario/Aprobaciones/Mensajes) se
corrigieron antes de cada commit.

## Qué quedó EXCLUIDO a propósito

- **Páginas públicas / embeds de paquetes** (PreviewCountry, PreviewPackage,
  PublicEmbeds, PackagePreviewShell, PackageEmbedExperience): son la web
  pública, tienen identidad propia — el estándar es del backoffice.
- **Isotipo "MT"** (topbar, sidebar, login): sigue índigo. La guía dice
  explícito que es decisión de marca de Gastón (ver preguntas abajo).
- **✨ de la IA**: intacta (E.7, IA discreta).
- **Colores con significado propio**: verde WhatsApp en burbujas de chat,
  sky de Homologación-vs-Producción en Ajustes AFIP (señal de seguridad),
  badge violeta "Bypass 4 ojos" en conciliación (no hay tono equivalente).

## Decisiones de Gastón (firmadas 17/08, multiple choice, ya aplicadas)

1. **Isotipo "MT": queda índigo** como acento de marca, separado del azul de
   acción (sin cambio de código — ya estaba así).
2. **Botón "Enviar" del chat WhatsApp en CRM: verde WhatsApp**, haciendo juego
   con las burbujas (excepción firmada al molde, comentada en el código).
3. **Avatares de Proveedores: unificados en grises** como Clientes (círculo
   neutro; los colores quedan para significados, no decoración).

## Deuda anotada (no de esta obra)

- "Marcar no continuo" en CRM nunca tuvo confirmación (hueso, no piel) —
  cierra la gestión sin paso atrás en la UI.
- `ServiceFormModal` quedó con botones `<button>` crudos (ya con tokens del
  estándar, pero sin el molde `Button`) — tanda de limpieza futura.
- `KPICard` del CRM sigue siendo botón a mano.
- Fallo transitorio del CI en el run de D6 (pruebas backend PostgreSQL sobre
  un commit 100% frontend; el run siguiente salió verde con el mismo backend).

## Cómo verificar en PROD (Gastón)

Entrar a https://backoffice.magnaviajesyturismo.com/ y recorrer: Cobranzas,
Facturación, Caja, Proveedores (cuenta corriente), Tarifario, CRM, Ajustes,
y una reserva (ficha completa + solapa Voucher). Todo debería verse con el
mismo molde que Reservas/Clientes: azul único, chips parejos, sin sellos,
sin botones de colores sueltos. Facturas: probar SOLO en homologación.
