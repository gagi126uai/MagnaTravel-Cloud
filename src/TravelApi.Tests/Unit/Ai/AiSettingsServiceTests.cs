using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using Xunit;

namespace TravelApi.Tests.Unit.Ai;

/// <summary>
/// Configuracion → Inteligencia artificial, del lado del motor (spec firmada 2026-08-07 §15,
/// M-28/M-31/M-32).
///
/// <para>Lo que estos tests cuidan, en criollo: que <b>la clave entre y no salga</b>, que quede
/// guardada de forma que no se lea de un vistazo, que no se pueda apuntar el sistema a la red
/// interna, y que la foto de estado de arriba diga la verdad.</para>
/// </summary>
public class AiSettingsServiceTests
{
    private static UpdateAiSettingsRequest GroqRequest(string? apiKey = "gsk_clave_secreta_de_prueba") => new()
    {
        ProviderCode = "groq",
        ApiKey = apiKey,
    };

    // ============================================================
    // La clave: entra y no sale (M-28)
    // ============================================================

    [Fact]
    public async Task Update_GuardaLaClaveCifrada_YSoloDevuelveLosPrimerosCuatroCaracteres()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var protector = AiTestDoubles.BuildRealProtector();
        var service = AiTestDoubles.BuildSettingsService(
            db, protector, new FakeAiConnectionTester(), AiTestDoubles.EmptyEnvironmentOptions());

        var dto = await service.UpdateAsync(
            GroqRequest(), updatedByUserId: "admin-1", updatedByUserName: "Gastón", CancellationToken.None);

        // Lo que sale por la API: que hay clave y como empieza. Nada mas.
        Assert.True(dto.HasApiKey);
        Assert.Equal("gsk_", dto.ApiKeyPrefix);
        Assert.Equal(AiApiKeySources.Saved, dto.ApiKeySource);

        // Y en la base no quedo la clave en claro: quedo el texto cifrado, que se puede volver a
        // leer SOLO con la llave del servidor.
        db.ChangeTracker.Clear();
        var stored = await db.AiSettings.SingleAsync();
        Assert.NotNull(stored.EncryptedApiKey);
        Assert.DoesNotContain("gsk_clave_secreta_de_prueba", stored.EncryptedApiKey!, StringComparison.Ordinal);
        Assert.StartsWith("enc:", stored.EncryptedApiKey!, StringComparison.Ordinal);
        Assert.Equal("gsk_clave_secreta_de_prueba", protector.UnprotectString(stored.EncryptedApiKey));
    }

    [Fact]
    public void ElContratoDeSalida_NoTieneNingunCampoQuePuedaLlevarLaClave()
    {
        // Candado de diseño: si alguien agrega una propiedad "ApiKey" al DTO de respuesta, este
        // test se rompe. Es barato y evita el peor error posible de esta pantalla.
        var propertyNames = typeof(AiSettingsDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToList();

        Assert.DoesNotContain("ApiKey", propertyNames);
        Assert.DoesNotContain("EncryptedApiKey", propertyNames);
        Assert.Contains("ApiKeyPrefix", propertyNames);
    }

    [Fact]
    public async Task Get_SinNadaConfigurado_DiceSinConfigurar_YNoInventaClave()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var service = AiTestDoubles.BuildSettingsService(
            db,
            AiTestDoubles.BuildRealProtector(),
            new FakeAiConnectionTester(),
            AiTestDoubles.EmptyEnvironmentOptions());

        var dto = await service.GetAsync(CancellationToken.None);

        Assert.Equal(AiSettingsStatusCodes.NotConfigured, dto.StatusCode);
        Assert.False(dto.HasApiKey);
        Assert.Null(dto.ApiKeyPrefix);
        Assert.Equal(AiApiKeySources.None, dto.ApiKeySource);
        // Sin nada cargado, la pantalla viene con el recomendado marcado.
        Assert.Equal("groq", dto.ProviderCode);
    }

    // ============================================================
    // Guardar: que se puede y que no
    // ============================================================

    [Fact]
    public async Task Update_SinClaveYSinNingunaGuardada_PideLaClaveConNombreDelProveedor()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var service = AiTestDoubles.BuildSettingsService(
            db,
            AiTestDoubles.BuildRealProtector(),
            new FakeAiConnectionTester(),
            AiTestDoubles.EmptyEnvironmentOptions());

        // ThrowsAny y no Throws: este rechazo ahora viaja con codigo (CodedValidationException, que
        // ES una ValidationException). Lo que este test cuida es el mensaje, no el tipo exacto.
        var error = await Assert.ThrowsAnyAsync<ValidationException>(() =>
            service.UpdateAsync(GroqRequest(apiKey: null), "admin-1", "Gastón", CancellationToken.None));

        Assert.Contains("Groq", error.Message, StringComparison.Ordinal);
        Assert.Empty(await db.AiSettings.ToListAsync());
    }

    [Fact]
    public async Task Update_CambiandoDeProveedorSinPegarLaClaveNueva_NoDejaGuardar()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var service = AiTestDoubles.BuildSettingsService(
            db,
            AiTestDoubles.BuildRealProtector(),
            new FakeAiConnectionTester(),
            AiTestDoubles.EmptyEnvironmentOptions());
        await service.UpdateAsync(GroqRequest(), "admin-1", "Gastón", CancellationToken.None);

        // ThrowsAny: mismo motivo que arriba — el rechazo por falta de clave ahora lleva codigo.
        var error = await Assert.ThrowsAnyAsync<ValidationException>(() =>
            service.UpdateAsync(
                new UpdateAiSettingsRequest { ProviderCode = "openai" },
                "admin-1", "Gastón", CancellationToken.None));

        // La clave de Groq no sirve para OpenAI: guardar asi dejaria la instalacion rota.
        Assert.Contains("OpenAI", error.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();
        var stored = await db.AiSettings.SingleAsync();
        Assert.Equal(AiProviderKey.Groq, stored.Provider);
    }

    [Fact]
    public async Task Update_CuandoFaltaLaClave_ElRechazoViajaConCodigo()
    {
        // T-13: la pantalla tiene que poder reconocer ESTE rechazo sin leer la frase. Antes lo hacia
        // mirando como empezaba el texto ("Pegá la clave..."), que se rompia con solo mejorar la
        // redaccion. Ahora viaja el codigo al lado, y el texto en criollo queda libre de cambiar.
        await using var db = AiTestDoubles.BuildDbContext();
        var service = AiTestDoubles.BuildSettingsService(
            db,
            AiTestDoubles.BuildRealProtector(),
            new FakeAiConnectionTester(),
            AiTestDoubles.EmptyEnvironmentOptions());

        var error = await Assert.ThrowsAsync<CodedValidationException>(() =>
            service.UpdateAsync(GroqRequest(apiKey: null), "admin-1", "Gastón", CancellationToken.None));

        Assert.Equal(ValidationCodes.AiApiKeyMissing, error.Code);
        Assert.Equal("aiClaveFaltante", error.Code);
        // El texto NO cambia: sigue siendo el mismo que ya estaba firmado.
        Assert.Contains("Pegá la clave", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_SinClaveNuevaPeroConUnaGuardada_ConservaLaQueEstaba()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var protector = AiTestDoubles.BuildRealProtector();
        var service = AiTestDoubles.BuildSettingsService(
            db, protector, new FakeAiConnectionTester(), AiTestDoubles.EmptyEnvironmentOptions());
        await service.UpdateAsync(GroqRequest(), "admin-1", "Gastón", CancellationToken.None);

        // Cambiar SOLO el modelo, sin volver a pegar la clave, tiene que andar.
        var dto = await service.UpdateAsync(
            new UpdateAiSettingsRequest { ProviderCode = "groq", Model = "llama-3.1-8b-instant" },
            "admin-1", "Gastón", CancellationToken.None);

        Assert.True(dto.HasApiKey);
        Assert.Equal("llama-3.1-8b-instant", dto.Model);
        db.ChangeTracker.Clear();
        var stored = await db.AiSettings.SingleAsync();
        Assert.Equal("gsk_clave_secreta_de_prueba", protector.UnprotectString(stored.EncryptedApiKey));
    }

    [Fact]
    public async Task Update_SinDireccionNiModelo_UsaLosRecomendadosDelProveedor()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var service = AiTestDoubles.BuildSettingsService(
            db,
            AiTestDoubles.BuildRealProtector(),
            new FakeAiConnectionTester(),
            AiTestDoubles.EmptyEnvironmentOptions());

        var dto = await service.UpdateAsync(GroqRequest(), "admin-1", "Gastón", CancellationToken.None);

        Assert.Equal("https://api.groq.com/openai/v1", dto.BaseUrl);
        Assert.Equal("openai/gpt-oss-120b", dto.Model);
    }

    [Fact]
    public async Task Update_ProveedorInexistente_NoSeGuarda()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var service = AiTestDoubles.BuildSettingsService(
            db,
            AiTestDoubles.BuildRealProtector(),
            new FakeAiConnectionTester(),
            AiTestDoubles.EmptyEnvironmentOptions());

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.UpdateAsync(
                new UpdateAiSettingsRequest { ProviderCode = "copilot", ApiKey = "x" },
                "admin-1", "Gastón", CancellationToken.None));
    }

    [Fact]
    public async Task Update_ConDireccionInterna_NoSeGuarda()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var service = AiTestDoubles.BuildSettingsService(
            db,
            AiTestDoubles.BuildRealProtector(),
            new FakeAiConnectionTester(),
            AiTestDoubles.EmptyEnvironmentOptions());

        // Si esto se pudiera guardar, el candado del probador seria inutil: alcanzaria con guardar
        // la direccion interna y esperar a que la use el sistema en la proxima llamada.
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.UpdateAsync(
                new UpdateAiSettingsRequest
                {
                    ProviderCode = "otra",
                    BaseUrl = "https://169.254.169.254/latest/meta-data",
                    Model = "lo-que-sea",
                    ApiKey = "clave",
                },
                "admin-1", "Gastón", CancellationToken.None));

        Assert.Empty(await db.AiSettings.ToListAsync());
    }

    [Fact]
    public async Task Update_DejaRegistradoQuienYCuando()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var service = AiTestDoubles.BuildSettingsService(
            db,
            AiTestDoubles.BuildRealProtector(),
            new FakeAiConnectionTester(),
            AiTestDoubles.EmptyEnvironmentOptions());
        var antes = DateTime.UtcNow.AddSeconds(-1);

        var dto = await service.UpdateAsync(GroqRequest(), "admin-7", "Gastón Albornoz", CancellationToken.None);

        Assert.Equal("Gastón Albornoz", dto.UpdatedByUserName);
        Assert.True(dto.UpdatedAt >= antes);
        db.ChangeTracker.Clear();
        var stored = await db.AiSettings.SingleAsync();
        Assert.Equal("admin-7", stored.UpdatedByUserId);
    }

    // ============================================================
    // Probar conexion (M-31)
    // ============================================================

    [Fact]
    public async Task Test_SinClaveEnElPedido_UsaLaGuardada()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var protector = AiTestDoubles.BuildRealProtector();
        var tester = new FakeAiConnectionTester();
        var service = AiTestDoubles.BuildSettingsService(
            db, protector, tester, AiTestDoubles.EmptyEnvironmentOptions());
        await service.UpdateAsync(GroqRequest(), "admin-1", "Gastón", CancellationToken.None);

        var result = await service.TestConnectionAsync(new TestAiConnectionRequest(), CancellationToken.None);

        Assert.Equal(AiConnectionTestCodes.Ok, result.ResultCode);
        Assert.Equal("gsk_clave_secreta_de_prueba", tester.LastProbe!.ApiKey);
    }

    [Fact]
    public async Task Test_ConDatosDePantallaSinGuardar_PruebaEsosDatos()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var tester = new FakeAiConnectionTester();
        var service = AiTestDoubles.BuildSettingsService(
            db, AiTestDoubles.BuildRealProtector(), tester, AiTestDoubles.EmptyEnvironmentOptions());

        await service.TestConnectionAsync(
            new TestAiConnectionRequest { ProviderCode = "openai", ApiKey = "sk-la-nueva" },
            CancellationToken.None);

        Assert.Equal("sk-la-nueva", tester.LastProbe!.ApiKey);
        Assert.Equal("https://api.openai.com/v1", tester.LastProbe!.BaseUrl);

        // Y probar NO guardo NADA: ni siquiera creo la fila de configuracion.
        db.ChangeTracker.Clear();
        Assert.Empty(await db.AiSettings.ToListAsync());
    }

    [Fact]
    public async Task Test_QueFalla_DejaLaFotoEnAmbar()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var protector = AiTestDoubles.BuildRealProtector();
        var service = AiTestDoubles.BuildSettingsService(
            db,
            protector,
            new FakeAiConnectionTester(AiConnectionTestCodes.InvalidKey),
            AiTestDoubles.EmptyEnvironmentOptions());
        await service.UpdateAsync(GroqRequest(), "admin-1", "Gastón", CancellationToken.None);

        await service.TestConnectionAsync(new TestAiConnectionRequest(), CancellationToken.None);
        var dto = await service.GetAsync(CancellationToken.None);

        Assert.Equal(AiSettingsStatusCodes.LastTestFailed, dto.StatusCode);
        Assert.Equal(AiConnectionTestCodes.InvalidKey, dto.LastTestCode);
        Assert.NotNull(dto.LastTestAt);
    }

    [Fact]
    public async Task Test_QueAnda_DejaLaFotoEnVerde()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var service = AiTestDoubles.BuildSettingsService(
            db,
            AiTestDoubles.BuildRealProtector(),
            new FakeAiConnectionTester(AiConnectionTestCodes.Ok),
            AiTestDoubles.EmptyEnvironmentOptions());
        await service.UpdateAsync(GroqRequest(), "admin-1", "Gastón", CancellationToken.None);

        await service.TestConnectionAsync(new TestAiConnectionRequest(), CancellationToken.None);
        var dto = await service.GetAsync(CancellationToken.None);

        Assert.Equal(AiSettingsStatusCodes.Working, dto.StatusCode);
        Assert.Equal("Groq", dto.ProviderDisplayName);
    }

    [Fact]
    public async Task Update_DespuesDeUnaPruebaFallida_LimpiaElAmbar()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var service = AiTestDoubles.BuildSettingsService(
            db,
            AiTestDoubles.BuildRealProtector(),
            new FakeAiConnectionTester(AiConnectionTestCodes.InvalidKey),
            AiTestDoubles.EmptyEnvironmentOptions());
        await service.UpdateAsync(GroqRequest(), "admin-1", "Gastón", CancellationToken.None);
        await service.TestConnectionAsync(new TestAiConnectionRequest(), CancellationToken.None);

        // Pegar una clave nueva invalida lo que decia la prueba anterior: no puede seguir en ambar
        // por un resultado que ya no describe a esta configuracion.
        var dto = await service.UpdateAsync(
            GroqRequest(apiKey: "gsk_otra_clave_distinta"), "admin-1", "Gastón", CancellationToken.None);

        Assert.Equal(AiSettingsStatusCodes.Working, dto.StatusCode);
        Assert.Null(dto.LastTestCode);
    }

    // ============================================================
    // La clave guardada NO se le presta a cualquier direccion (hallazgo de la review 2026-08-09)
    // ============================================================

    [Fact]
    public async Task Test_ContraOtraDireccionSinPegarClave_NoMandaLaClaveGuardadaANingunLado()
    {
        // El agujero que cierra: un Admin elegia "Otra", ponia la direccion de un servidor propio,
        // dejaba el campo de la clave vacio y apretaba "Probar conexion" — y el sistema le mandaba
        // la clave del proveedor de la agencia a ESE servidor. El boton servia para robarse la clave.
        await using var db = AiTestDoubles.BuildDbContext();
        var tester = new FakeAiConnectionTester();
        var service = AiTestDoubles.BuildSettingsService(
            db, AiTestDoubles.BuildRealProtector(), tester, AiTestDoubles.EmptyEnvironmentOptions());
        await service.UpdateAsync(GroqRequest(), "admin-1", "Gastón", CancellationToken.None);

        var result = await service.TestConnectionAsync(
            new TestAiConnectionRequest
            {
                ProviderCode = "otra",
                BaseUrl = "https://servidor-del-curioso.example/v1",
                Model = "lo-que-sea",
            },
            CancellationToken.None);

        Assert.Equal(AiConnectionTestCodes.InvalidKey, result.ResultCode);
        Assert.Equal(0, tester.CallCount); // ni siquiera se intento la llamada
    }

    [Fact]
    public async Task Test_ContraLaMismaDireccionGuardadaSinPegarClave_SiUsaLaGuardada()
    {
        // La contracara del test de arriba: si se prueba la MISMA direccion a la que pertenece la
        // clave, reusarla es justamente lo que la pantalla necesita (no obligar a repegarla).
        await using var db = AiTestDoubles.BuildDbContext();
        var tester = new FakeAiConnectionTester();
        var service = AiTestDoubles.BuildSettingsService(
            db, AiTestDoubles.BuildRealProtector(), tester, AiTestDoubles.EmptyEnvironmentOptions());
        await service.UpdateAsync(GroqRequest(), "admin-1", "Gastón", CancellationToken.None);

        var result = await service.TestConnectionAsync(
            new TestAiConnectionRequest { ProviderCode = "groq", BaseUrl = "https://api.groq.com/openai/v1" },
            CancellationToken.None);

        Assert.Equal(AiConnectionTestCodes.Ok, result.ResultCode);
        Assert.Equal("gsk_clave_secreta_de_prueba", tester.LastProbe!.ApiKey);
    }

    [Fact]
    public async Task Test_DeUnaConfiguracionQueNoEsLaGuardada_NoTocaLaFotoDeEstado()
    {
        // La foto de arriba describe a la configuracion GUARDADA. Si una prueba de otra cosa la
        // pisara, el semaforo mentiria: verde o ambar por algo que la agencia no tiene configurado.
        await using var db = AiTestDoubles.BuildDbContext();
        var service = AiTestDoubles.BuildSettingsService(
            db,
            AiTestDoubles.BuildRealProtector(),
            new FakeAiConnectionTester(AiConnectionTestCodes.InvalidKey),
            AiTestDoubles.EmptyEnvironmentOptions());
        await service.UpdateAsync(GroqRequest(), "admin-1", "Gastón", CancellationToken.None);

        // Se prueba OTRA cosa (otro proveedor, con su propia clave pegada a mano) y le va mal.
        await service.TestConnectionAsync(
            new TestAiConnectionRequest { ProviderCode = "openai", ApiKey = "sk-otra-clave" },
            CancellationToken.None);

        db.ChangeTracker.Clear();
        var stored = await db.AiSettings.SingleAsync();
        Assert.Null(stored.LastTestOutcome);
        Assert.Null(stored.LastTestAt);
        var dto = await service.GetAsync(CancellationToken.None);
        Assert.Equal(AiSettingsStatusCodes.Working, dto.StatusCode);
    }

    // ============================================================
    // Rastro de auditoria (la clave no puede quedar ni ahi)
    // ============================================================

    [Fact]
    public async Task Update_DejaRastroDeAuditoria_SinLaClaveNiSiquieraCifrada()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var service = AiTestDoubles.BuildSettingsService(
            db,
            AiTestDoubles.BuildRealProtector(),
            new FakeAiConnectionTester(),
            AiTestDoubles.EmptyEnvironmentOptions());

        await service.UpdateAsync(GroqRequest(), "admin-1", "Gastón", CancellationToken.None);

        db.ChangeTracker.Clear();
        var trail = await db.AuditLogs.Where(log => log.EntityName == "AiSettings").ToListAsync();
        var entry = Assert.Single(trail);
        Assert.NotNull(entry.Changes);
        // Ni el nombre del campo de la clave ni su contenido (cifrado o no) entran al historial.
        Assert.DoesNotContain("EncryptedApiKey", entry.Changes!, StringComparison.Ordinal);
        Assert.DoesNotContain("gsk_clave_secreta_de_prueba", entry.Changes!, StringComparison.Ordinal);
        Assert.DoesNotContain("enc:", entry.Changes!, StringComparison.Ordinal);
        // Y si queda el rastro de lo que SI se puede contar: con cual proveedor se trabaja.
        Assert.Contains("Provider", entry.Changes!, StringComparison.Ordinal);
    }

    // ============================================================
    // Bordes de la clave
    // ============================================================

    [Fact]
    public async Task Update_ConClaveCortita_NoGuardaNingunPrefijo()
    {
        // Mostrar "los primeros 4" de una clave de 4 seria mostrar la clave entera.
        await using var db = AiTestDoubles.BuildDbContext();
        var service = AiTestDoubles.BuildSettingsService(
            db,
            AiTestDoubles.BuildRealProtector(),
            new FakeAiConnectionTester(),
            AiTestDoubles.EmptyEnvironmentOptions());

        var dto = await service.UpdateAsync(GroqRequest(apiKey: "abcd"), "admin-1", "Gastón", CancellationToken.None);

        Assert.True(dto.HasApiKey);
        Assert.Null(dto.ApiKeyPrefix);
        db.ChangeTracker.Clear();
        var stored = await db.AiSettings.SingleAsync();
        Assert.Null(stored.ApiKeyPrefix);
    }

    [Fact]
    public async Task Update_ConClavePegadaConSaltoDeLinea_LaGuardaLimpia()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var protector = AiTestDoubles.BuildRealProtector();
        var service = AiTestDoubles.BuildSettingsService(
            db, protector, new FakeAiConnectionTester(), AiTestDoubles.EmptyEnvironmentOptions());

        await service.UpdateAsync(
            GroqRequest(apiKey: "  gsk_clave_secreta_de_prueba\n"), "admin-1", "Gastón", CancellationToken.None);

        db.ChangeTracker.Clear();
        var stored = await db.AiSettings.SingleAsync();
        Assert.Equal("gsk_clave_secreta_de_prueba", protector.UnprotectString(stored.EncryptedApiKey));
    }

    [Fact]
    public async Task Update_SinLlaveDeCifradoEnElServidor_NoGuardaNadaYAvisaEnCriollo()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var service = AiTestDoubles.BuildSettingsService(
            db,
            AiTestDoubles.BuildProtectorWithoutServerKey(),
            new FakeAiConnectionTester(),
            AiTestDoubles.EmptyEnvironmentOptions());

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            service.UpdateAsync(GroqRequest(), "admin-1", "Gastón", CancellationToken.None));

        // Guardar la clave en claro "para salir del paso" no es una opcion, y el error tecnico
        // tampoco puede llegar a la pantalla.
        Assert.DoesNotContain("EncryptionKey", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.AiSettings.ToListAsync());
    }

    // ============================================================
    // La lista de proveedores sale del motor (M-32)
    // ============================================================

    [Fact]
    public async Task Presets_TraenGroqRecomendado_YNoOfrecenLoQueNoSePuedeConectar()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var service = AiTestDoubles.BuildSettingsService(
            db,
            AiTestDoubles.BuildRealProtector(),
            new FakeAiConnectionTester(),
            AiTestDoubles.EmptyEnvironmentOptions());

        var presets = service.GetProviderPresets().Providers;

        var recomendado = Assert.Single(presets.Where(preset => preset.IsRecommended));
        Assert.Equal("groq", recomendado.Code);

        var codigos = presets.Select(preset => preset.Code).ToList();
        Assert.Equal(new[] { "groq", "openai", "claude", "gemini", "grok", "openrouter", "otra" }, codigos);

        // GitHub Copilot y "Codex" NO se conectan de esta forma: ofrecerlos seria prometer algo
        // que no funciona (§15 de la spec firmada).
        Assert.DoesNotContain(presets, preset =>
            preset.DisplayName.Contains("Copilot", StringComparison.OrdinalIgnoreCase)
            || preset.DisplayName.Contains("Codex", StringComparison.OrdinalIgnoreCase));

        // "Otra" es la unica que obliga a cargar direccion y modelo a mano.
        var otra = presets.Single(preset => preset.Code == "otra");
        Assert.True(otra.RequiresManualEndpoint);
        Assert.Empty(otra.BaseUrl);
    }

    // ============================================================
    // "La puso el tecnico al instalar" (§15.8)
    // ============================================================

    [Fact]
    public async Task Get_ConClaveSoloEnElServidor_DiceQueVieneDelServidor_YNoMuestraPrefijo()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var service = AiTestDoubles.BuildSettingsService(
            db,
            AiTestDoubles.BuildRealProtector(),
            new FakeAiConnectionTester(),
            AiTestDoubles.EnvironmentOptions(
                "https://api.groq.com/openai/v1", "gsk_del_tecnico", "llama-3.3-70b-versatile"));

        var dto = await service.GetAsync(CancellationToken.None);

        Assert.Equal(AiSettingsStatusCodes.Working, dto.StatusCode);
        Assert.True(dto.HasApiKey);
        Assert.Equal(AiApiKeySources.Server, dto.ApiKeySource);
        Assert.Null(dto.ApiKeyPrefix);
        // Se reconoce por la direccion, asi la linea de arriba dice "Funcionando con Groq".
        Assert.Equal("Groq", dto.ProviderDisplayName);
    }
}
