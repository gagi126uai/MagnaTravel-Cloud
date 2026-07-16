using System.ComponentModel.DataAnnotations;
using System.Reflection;
using TravelApi.Application.DTOs;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// GUARDIAN anti-regresion del bug "[property: Required] en un record" — la tercera vez que
/// entro al sistema (2026-06-06 catalogo, 2026-07-16 facturas del operador + formulario
/// publico de consultas), siempre descubierto por Gaston probando en el servidor.
///
/// <para><b>El bug, en criollo:</b> en un record con constructor primario, si el atributo de
/// validacion se escribe como <c>[property: Required]</c> queda pegado a la PROPIEDAD generada,
/// pero ASP.NET exige que la metadata de validacion viva en el PARAMETRO del constructor
/// (atributo sin "property:"). Al recibir el primer request con ese tipo en el body, el
/// pipeline MVC tira <c>InvalidOperationException</c> ("validation metadata must be associated
/// with the constructor parameter") ANTES de llegar a la action → 500 con el mensaje generico
/// en CADA intento, sin que ningun test de service lo atrape (los tests de service saltean el
/// model binding de MVC).</para>
///
/// <para><b>Que hace este test:</b> recorre por reflexion TODOS los tipos del assembly de DTOs
/// (TravelApi.Application) y falla nombrando al culpable si algun record con constructor
/// primario tiene atributos de validacion declarados sobre una propiedad que corresponde a un
/// parametro de ese constructor. Asi el patron enfermo no compila... bueno, compila, pero no
/// pasa la suite — que para el CI es lo mismo: no llega a prod.</para>
/// </summary>
public class RecordValidationAttributePlacementTests
{
    [Fact]
    public void NingunRecordDeLaApplicationTieneValidacionEnLaPropiedadEnVezDelParametro()
    {
        // Tomamos el assembly de DTOs a partir de un tipo cualquiera que viva ahi.
        var assembly = typeof(SupplierInvoiceCreateRequest).Assembly;

        var culpables = new List<string>();

        foreach (var tipo in assembly.GetTypes())
        {
            // Un "record con constructor primario" no es distinguible al 100% por reflexion,
            // pero alcanza con esta aproximacion: tiene algun constructor publico con
            // parametros cuyos nombres coinciden (case-insensitive) con propiedades publicas.
            foreach (var ctor in tipo.GetConstructors())
            {
                foreach (var parametro in ctor.GetParameters())
                {
                    if (parametro.Name is null) continue;

                    var propiedad = tipo.GetProperty(
                        parametro.Name,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (propiedad is null) continue;

                    // ¿La PROPIEDAD tiene atributos de validacion propios (no heredados del
                    // parametro)? Eso es exactamente lo que ASP.NET rechaza en runtime.
                    var atributosEnPropiedad = propiedad
                        .GetCustomAttributes<ValidationAttribute>(inherit: false)
                        .ToList();

                    if (atributosEnPropiedad.Count > 0)
                    {
                        culpables.Add(
                            $"{tipo.FullName}.{propiedad.Name} — mover " +
                            $"[{string.Join(", ", atributosEnPropiedad.Select(a => a.GetType().Name.Replace("Attribute", "")))}] " +
                            "al parametro del constructor (sacar el 'property:')");
                    }
                }
            }
        }

        Assert.True(
            culpables.Count == 0,
            "Estos DTOs tienen atributos de validacion como [property: X] en un record — " +
            "eso revienta con 500 en el primer request real (ver doc de esta clase):\n" +
            string.Join("\n", culpables));
    }
}
