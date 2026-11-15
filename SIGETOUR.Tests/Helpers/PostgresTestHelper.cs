using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SIGETOUR.API.Infrastructure.Data;
using Xunit;
using Xunit.Abstractions;

namespace SIGETOUR.Tests.Helpers
{
    /// <summary>
    /// Helper centralizado para pruebas de integración con PostgreSQL en SIGETOUR.
    /// Garantiza:
    /// 1. Cero credenciales incrustadas en código fuente.
    /// 2. Omisión explícita (Skip.If / OMITIDA) si no hay configuración en variables de entorno.
    /// 3. Fallo explícito (Assert.Fail) si la variable está configurada pero la base de datos es inalcanzable.
    /// 4. Protección estricta contra bases de datos operativas (sigetour_db, prod, etc.).
    /// </summary>
    public static class PostgresTestHelper
    {
        public const string EnvVarName = "TEST_POSTGRES_CONNECTION";
        public const string FallbackEnvVarName = "POSTGRES_TEST_CONNECTION";

        private static readonly string[] ForbiddenOperativeKeywords =
        {
            "sigetour_db",
            "production",
            "prod",
            "live",
            "operativa",
            "operacion"
        };

        /// <summary>
        /// Obtiene la cadena de conexión configurada únicamente desde variables de entorno.
        /// NUNCA expone ni devuelve credenciales por defecto incrustadas en código.
        /// </summary>
        public static string? GetConfiguredConnectionString()
        {
            var conn = Environment.GetEnvironmentVariable(EnvVarName);
            if (string.IsNullOrWhiteSpace(conn))
            {
                conn = Environment.GetEnvironmentVariable(FallbackEnvVarName);
            }
            return string.IsNullOrWhiteSpace(conn) ? null : conn.Trim();
        }

        /// <summary>
        /// Valida que la base de datos no sea una base operativa ni contenga patrones de producción.
        /// Requiere que el nombre contenga explícitamente 'test'.
        /// </summary>
        public static void EnsureSafeTestDatabase(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("La cadena de conexión a PostgreSQL no puede estar vacía.");
            }

            try
            {
                var builder = new NpgsqlConnectionStringBuilder(connectionString);
                var dbName = (builder.Database ?? string.Empty).Trim().ToLowerInvariant();

                if (string.IsNullOrEmpty(dbName))
                {
                    Assert.Fail("[SEGURIDAD CRÍTICA] La cadena de conexión no especifica el nombre de la base de datos.");
                }

                // Verificación contra la base de datos operativa conocida
                if (dbName == "sigetour_db")
                {
                    Assert.Fail("[SEGURIDAD CRÍTICA] Se bloqueó la ejecución de pruebas contra la base de datos operativa 'sigetour_db'. Las pruebas solo deben apuntar a bases de datos de prueba.");
                }

                foreach (var keyword in ForbiddenOperativeKeywords)
                {
                    if (dbName.Contains(keyword) && !dbName.Contains("test"))
                    {
                        Assert.Fail($"[SEGURIDAD CRÍTICA] Se detectó un intento de ejecutar pruebas contra una base de datos potencialmente operativa ('{builder.Database}').");
                    }
                }

                if (!dbName.Contains("test"))
                {
                    Assert.Fail($"[SEGURIDAD] Por directriz de seguridad de SIGETOUR, el nombre de la base de datos de pruebas ('{builder.Database}') debe contener la palabra 'test' (ej: sigetour_test_db).");
                }
            }
            catch (Exception ex) when (!(ex is Xunit.Sdk.XunitException))
            {
                Assert.Fail($"[SEGURIDAD] Cadena de conexión a PostgreSQL inválida o no analizable: {ex.Message}");
            }
        }

        /// <summary>
        /// Exige conexión a PostgreSQL. Si no hay variable configurada, emite Skip.If (marca la prueba como [SKIP] / Omitida).
        /// Si está configurada pero falla la conexión, emite Assert.Fail().
        /// Permite inyectar explicitConnectionString para pruebas unitarias sin contaminar variables de entorno de proceso.
        /// </summary>
        public static async Task<AppDbContext> RequirePostgresAsync(ITestOutputHelper? output = null, string? explicitConnectionString = null)
        {
            var conn = explicitConnectionString ?? GetConfiguredConnectionString();
            if (string.IsNullOrWhiteSpace(conn))
            {
                output?.WriteLine("[OMITIDA] Prueba PostgreSQL omitida: variable de entorno TEST_POSTGRES_CONNECTION no configurada.");
                Skip.If(true, "Prueba PostgreSQL omitida: variable de entorno TEST_POSTGRES_CONNECTION no configurada.");
                return null!;
            }

            EnsureSafeTestDatabase(conn);

            AppDbContext db;
            try
            {
                var options = new DbContextOptionsBuilder<AppDbContext>()
                    .UseNpgsql(conn, npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                    .Options;

                db = new AppDbContext(options);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Fallo al instanciar el contexto de PostgreSQL de prueba: {ex.Message}");
                return null!;
            }

            try
            {
                var canConnect = await db.Database.CanConnectAsync();
                if (!canConnect)
                {
                    Assert.Fail($"Fallo de conexión a la base de datos PostgreSQL de prueba ('{conn}'): CanConnectAsync retornó false.");
                }
            }
            catch (Exception ex)
            {
                Assert.Fail($"Fallo de conexión a la base de datos PostgreSQL de prueba configurada: {ex.Message}");
            }

            return db;
        }

        public static DbContextOptions<AppDbContext> CreatePostgresOptions(string connectionString)
        {
            EnsureSafeTestDatabase(connectionString);
            return new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .Options;
        }
    }
}
