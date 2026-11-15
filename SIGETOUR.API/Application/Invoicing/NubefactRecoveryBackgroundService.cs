using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SIGETOUR.API.Application.Invoicing
{
    /// <summary>
    /// Proceso de fondo resiliente que ejecuta periódicamente la recuperación de comprobantes electrónicos
    /// pendientes o en espera de validación de SUNAT, aplicando límites estrictos, esperas exponenciales
    /// y evitando reemisiones duplicadas en estados inciertos.
    /// </summary>
    public class NubefactRecoveryBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<NubefactRecoveryBackgroundService> _logger;
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

        public NubefactRecoveryBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<NubefactRecoveryBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Servicio de fondo para recuperación y conciliación de NubeFact iniciado.");

            // Espera inicial para no competir con el arranque del servidor y migraciones
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var receiptService = scope.ServiceProvider.GetRequiredService<IElectronicReceiptService>();
                        var processed = await receiptService.ProcessPendingReceiptsAsync(stoppingToken);
                        if (processed > 0)
                        {
                            _logger.LogInformation("Recuperación automática NubeFact procesó {Count} comprobantes pendientes.", processed);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error durante la ejecución del ciclo de recuperación automática de NubeFact.");
                }

                try
                {
                    await Task.Delay(Interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("Servicio de fondo para recuperación y conciliación de NubeFact detenido.");
        }
    }
}
