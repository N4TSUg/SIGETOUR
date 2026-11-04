using System.Data;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SIGETOUR.API.Application.Invoicing;
using SIGETOUR.API.Application.Payments;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;

namespace SIGETOUR.API.Controllers;

[ApiController]
[Route("api/culqi")]
public sealed class CulqiPaymentsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CulqiApiClient _culqi;
    private readonly CheckoutAccess _access;
    private readonly IConfiguration _config;
    private readonly IElectronicReceiptService _receiptService;

    public CulqiPaymentsController(
        AppDbContext db,
        CulqiApiClient culqi,
        CheckoutAccess access,
        IConfiguration config,
        IElectronicReceiptService receiptService)
    {
        _db = db;
        _culqi = culqi;
        _access = access;
        _config = config;
        _receiptService = receiptService;
    }

    private static DateTime GetPeruLocalDateTime()
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Lima");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
        catch
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById("SA Pacific Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            }
            catch
            {
                return DateTime.UtcNow.AddHours(-5);
            }
        }
    }

    private async Task<CashRegisterShift> GetOrCreateWebShiftAsync(CancellationToken ct)
    {
        var activeShift = await _db.CashRegisterShifts.FirstOrDefaultAsync(s => s.IsOpen, ct);
        if (activeShift != null) return activeShift;

        var peruNow = GetPeruLocalDateTime();
        var todayStr = peruNow.ToString("yyyyMMdd");
        var shiftNumber = $"CJ-WEB-{todayStr}";

        var webShift = await _db.CashRegisterShifts.FirstOrDefaultAsync(s => s.ShiftNumber == shiftNumber, ct);
        if (webShift == null)
        {
            webShift = new CashRegisterShift
            {
                Id = Guid.NewGuid(),
                ShiftNumber = shiftNumber,
                OpenedByUserName = "Sistema / Pasarela Culqi",
                OpenedAt = DateTime.UtcNow,
                InitialAmount = 0m,
                IsOpen = false,
                Notes = "Turno automático de recaudación online (pagos confirmados sin turno presencial abierto)."
            };
            try
            {
                _db.CashRegisterShifts.Add(webShift);
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                webShift = await _db.CashRegisterShifts.FirstOrDefaultAsync(s => s.ShiftNumber == shiftNumber, ct)
                    ?? webShift;
            }
        }
        return webShift;
    }

    private async Task ProcessPendingWebhookEventsAsync(string providerId, CancellationToken ct)
    {
        try
        {
            var pendingEvents = await _db.PaymentWebhookEvents
                .Where(e => e.ProviderId == providerId && !e.Processed)
                .ToListAsync(ct);

            if (pendingEvents.Any())
            {
                foreach (var ev in pendingEvents)
                {
                    ev.Processed = true;
                    ev.ProcessedAtUtc = DateTime.UtcNow;
                }
                await _db.SaveChangesAsync(ct);
            }
        }
        catch
        {
            // Ignorar fallo transitorio
        }
    }

    [HttpPost("prepare")]
    [AllowAnonymous]
    public async Task<IActionResult> Prepare([FromBody] PreparePayment request, CancellationToken ct)
    {
        if (!_access.Verify(request.BookingId, request.CheckoutToken)) return Unauthorized();
        var mode = request.Mode == "deposit" ? "advance" : request.Mode;
        if (mode is not ("full" or "advance") || request.Channel is not ("charge" or "wallet_qr"))
            return BadRequest(new { message = "Modo o canal inválido" });

        var booking = await _db.Bookings.Include(b => b.Items).ThenInclude(i => i.TourPackage)
            .FirstOrDefaultAsync(b => b.Id == request.BookingId, ct);
        if (booking == null || booking.Status == BookingStatus.Cancelled) return NotFound();

        decimal balance = booking.TotalAmount - booking.PaidAmount;
        if (balance <= 0) return Conflict(new { message = "Reserva sin deuda pendiente" });

        decimal amount = balance;
        if (mode == "advance")
        {
            // Política de anticipos: adelanto aplicado a los ítems del tour a partir del porcentaje requerido.
            var advanceRequired = booking.Items.Sum(i => i.SubTotal *
                (i.TourPackage?.RequiredAdvancePercentage > 0 ? i.TourPackage.RequiredAdvancePercentage : 50) / 100m);
            if (advanceRequired <= 0) advanceRequired = decimal.Round(booking.TotalAmount / 2m, 2);
            amount = Math.Min(balance, Math.Max(0m, advanceRequired - booking.PaidAmount));
            if (amount <= 0) return Conflict(new { message = "El adelanto ya está cubierto" });
        }
        var cents = checked((long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
        if (cents < 100 || cents > 100000000) return BadRequest(new { message = "Importe fuera del rango de demo" });
        if (request.Channel == "wallet_qr" && cents < 600)
            return BadRequest(new { message = "El método QR requiere al menos S/ 6" });

        // Gestión inteligente y consulta de intentos previos antes de permitir nuevos cargos
        var existingAttempts = await _db.Set<CulqiPayment>()
            .Where(p => p.BookingId == booking.Id && (p.Status == "created" || p.Status == "processing" || p.Status == "verification" || p.Status == "pending"))
            .OrderByDescending(p => p.CreatedAtUtc)
            .ToListAsync(ct);

        foreach (var existingAttempt in existingAttempts)
        {
            // 1. Si tiene ProviderId (orden o cargo en Culqi), SIEMPRE conciliar con Culqi primero sin importar antigüedad
            if (existingAttempt.ProviderId != null)
            {
                var isVerified = await VerifyAndApply(existingAttempt.Id, ct);
                if (isVerified)
                {
                    await _db.SaveChangesAsync(ct);
                    return Ok(new { attemptId = existingAttempt.Id, amountCents = cents, currency = "PEN",
                        publicKey = _config["Culqi:PublicKey"], status = "paid", message = "La reserva ya cuenta con un pago verificado" });
                }

                // Consultar si la orden o cargo expiró o fue rechazado en Culqi
                var route = existingAttempt.Channel == "wallet_qr"
                    ? $"orders/{Uri.EscapeDataString(existingAttempt.ProviderId)}"
                    : $"charges/{Uri.EscapeDataString(existingAttempt.ProviderId)}";

                try
                {
                    using var queryResult = await _culqi.GetAsync(route, ct);
                    if (queryResult.Success)
                    {
                        var culqiState = queryResult.Text("state") ?? queryResult.Text("status");
                        if (culqiState is "expired" or "failed" or "canceled" or "deleted")
                        {
                            existingAttempt.Status = "failed";
                            continue;
                        }

                        if (culqiState is "paid")
                        {
                            var verifiedNow = await VerifyAndApply(existingAttempt.Id, ct);
                            if (verifiedNow)
                            {
                                await _db.SaveChangesAsync(ct);
                                return Ok(new { attemptId = existingAttempt.Id, amountCents = cents, currency = "PEN",
                                    publicKey = _config["Culqi:PublicKey"], status = "paid", message = "Pago verificado exitosamente" });
                            }
                        }

                        // Si es una orden QR activa con el mismo monto y canal, reutilizarla calculando expiración real
                        var isQrActive = existingAttempt.ExpirationDateUtc.HasValue
                            ? existingAttempt.ExpirationDateUtc.Value > DateTime.UtcNow
                            : existingAttempt.CreatedAtUtc > DateTime.UtcNow.AddMinutes(-20);

                        if (existingAttempt.Channel == "wallet_qr" && request.Channel == "wallet_qr"
                            && existingAttempt.AmountCents == cents && isQrActive)
                        {
                            var expDate = existingAttempt.ExpirationDateUtc ?? existingAttempt.CreatedAtUtc.AddMinutes(25);
                            var expUnix = new DateTimeOffset(expDate).ToUnixTimeSeconds();
                            var remainingSecs = Math.Max(0, (int)(expDate - DateTime.UtcNow).TotalSeconds);

                            await _db.SaveChangesAsync(ct);
                            return Ok(new
                            {
                                attemptId = existingAttempt.Id,
                                amountCents = cents,
                                currency = "PEN",
                                orderId = existingAttempt.ProviderId,
                                publicKey = _config["Culqi:PublicKey"],
                                status = existingAttempt.Status,
                                reused = true,
                                qrCodeUrl = queryResult.Text("qr"),
                                paymentCode = queryResult.Text("payment_code"),
                                urlPe = queryResult.Text("url_pe"),
                                expirationDateUnix = expUnix,
                                expiresInSeconds = remainingSecs,
                                message = "Orden QR activa recuperada exitosamente"
                            });
                        }
                    }
                    else if (queryResult.HttpStatus == 404)
                    {
                        existingAttempt.Status = "failed";
                        continue;
                    }
                    else
                    {
                        // Fallo en la consulta (5xx u otro error): NO asumir que falló, resultado ambiguo
                        await _db.SaveChangesAsync(ct);
                        return Conflict(new { message = "Existe un intento previo en verificación en Culqi pero no se pudo consultar su estado actual. Por favor consulte el estado antes de iniciar un nuevo cobro.", attemptId = existingAttempt.Id });
                    }
                }
                catch (Exception)
                {
                    // Fallo o timeout consultando Culqi: no marcar failed
                    await _db.SaveChangesAsync(ct);
                    return Conflict(new { message = "Existe un pago pendiente de confirmación en Culqi y el servicio no responde. Espere un momento y consulte el estado.", attemptId = existingAttempt.Id });
                }

                await _db.SaveChangesAsync(ct);
                return Conflict(new { message = "Existe un pago en verificación en Culqi. Por favor consulte el estado antes de intentar otro abono.", attemptId = existingAttempt.Id });
            }

            // 2. Si no tiene ProviderId (interacción pendiente o en vuelo con Culqi):
            // Si es un intento activo en created con el mismo importe y canal, reutilizarlo
            if (existingAttempt.Status == "created" && existingAttempt.AmountCents == cents && existingAttempt.Channel == request.Channel && existingAttempt.CreatedAtUtc > DateTime.UtcNow.AddMinutes(-20))
            {
                await _db.SaveChangesAsync(ct);
                return Ok(new { attemptId = existingAttempt.Id, amountCents = cents, currency = "PEN",
                    publicKey = _config["Culqi:PublicKey"], status = existingAttempt.Status });
            }

            // Si es un intento de cargo sin ProviderId en estado ambiguo (processing o verification):
            if (existingAttempt.Channel == "charge" && (existingAttempt.Status == "verification" || existingAttempt.Status == "processing"))
            {
                // Conciliación verificable: consultar si el cargo existe en Culqi antes de cualquier decisión
                bool chargeReconciled = await ReconcileMissingProviderChargeAsync(existingAttempt, ct);
                if (chargeReconciled)
                {
                    await _db.SaveChangesAsync(ct);
                    var verifiedNow = await VerifyAndApply(existingAttempt.Id, ct);
                    if (verifiedNow)
                    {
                        await _db.SaveChangesAsync(ct);
                        return Ok(new { attemptId = existingAttempt.Id, amountCents = cents, currency = "PEN",
                            publicKey = _config["Culqi:PublicKey"], status = "paid", message = "La reserva ya cuenta con un cargo verificado tras conciliación" });
                    }
                }

                // Bloquear todo resultado no concluyente para prevenir doble cargo.
                // NUNCA usar un ping charges?limit=1 y 10 minutos para asumir que el cargo no existió.
                await _db.SaveChangesAsync(ct);
                return Conflict(new
                {
                    message = "Existe una transacción previa en verificación pendiente de confirmación. Por seguridad financiera para evitar cargos dobles, consulte el estado antes de iniciar un nuevo cobro.",
                    attemptId = existingAttempt.Id,
                    status = existingAttempt.Status
                });
            }
        }

        await _db.SaveChangesAsync(ct);

        var attempt = new CulqiPayment { BookingId = booking.Id, AmountCents = cents, Channel = request.Channel };
        _db.Set<CulqiPayment>().Add(attempt);
        await _db.SaveChangesAsync(ct);

        if (request.Channel == "wallet_qr")
        {
            var words = (booking.CustomerName ?? "Cliente SIGETOUR").Trim().Split(' ', 2);
            var phone = booking.CustomerPhone?.Trim() ?? "";
            var expTime = DateTimeOffset.UtcNow.AddMinutes(25);
            var expUnix = expTime.ToUnixTimeSeconds();

            using var order = await _culqi.PostAsync("orders", new
            {
                amount = cents,
                currency_code = "PEN",
                description = $"Reserva {booking.ReferenceCode}",
                order_number = $"SIG-{attempt.Id:N}",
                client_details = new
                {
                    first_name = words.ElementAtOrDefault(0) ?? "Cliente",
                    last_name = words.ElementAtOrDefault(1) ?? "Turismo",
                    email = booking.CustomerEmail,
                    phone_number = phone.StartsWith("+") ? phone : "+51" + phone
                },
                expiration_date = expUnix,
                confirm = false, // Compatible con Culqi Checkout: orden creada sin confirmar (state: created) para que el modal de Culqi Checkout la confirme al pulsar 'Generar QR' sin error CCKT-408
                metadata = new { booking_id = booking.Id.ToString(), attempt_id = attempt.Id.ToString() }
            }, ct);

            if (!order.Success || order.Text("object") != "order" ||
                order.Number("amount") != cents || order.Text("id") is not { } orderId)
            {
                attempt.Status = "failed";
                await _db.SaveChangesAsync(ct);
                return StatusCode(502, new { message = "No fue posible crear la orden Culqi en sandbox" });
            }

            // Guardar el expiration_date real devuelto por la API de Culqi
            var actualExpUnix = order.Number("expiration_date") ?? expUnix;
            var actualExpDate = DateTimeOffset.FromUnixTimeSeconds(actualExpUnix).UtcDateTime;

            attempt.ProviderId = orderId;
            attempt.Status = "verification"; // QR: en espera de pago del usuario en app
            attempt.ExpirationDateUtc = actualExpDate;
            await _db.SaveChangesAsync(ct);

            var expRemaining = Math.Max(0, (int)(actualExpDate - DateTime.UtcNow).TotalSeconds);
            return Ok(new
            {
                attemptId = attempt.Id,
                amountCents = cents,
                currency = "PEN",
                orderId,
                publicKey = _config["Culqi:PublicKey"],
                status = attempt.Status,
                reused = false,
                qrCodeUrl = order.Text("qr"),
                paymentCode = order.Text("payment_code"),
                urlPe = order.Text("url_pe"),
                expirationDateUnix = actualExpUnix,
                expiresInSeconds = expRemaining
            });
        }

        return Ok(new { attemptId = attempt.Id, amountCents = cents, currency = "PEN",
            publicKey = _config["Culqi:PublicKey"], status = attempt.Status });
    }

    [HttpPost("charge")]
    [AllowAnonymous]
    public async Task<IActionResult> Charge([FromBody] ChargePayment request, CancellationToken ct)
    {
        var payment = await _db.Set<CulqiPayment>().Include(p => p.Booking)
            .FirstOrDefaultAsync(p => p.Id == request.AttemptId, ct);
        if (payment == null) return NotFound();
        if (!_access.Verify(payment.BookingId, request.CheckoutToken)) return Unauthorized();
        if (payment.Channel != "charge" || (payment.Status != "created" && payment.Status != "3ds_required"))
        {
            if (payment.Status is "verification" or "processing")
            {
                return Conflict(new
                {
                    attemptId = payment.Id,
                    status = payment.Status,
                    message = "El cargo previo se encuentra en estado de verificación o procesamiento. Debe conciliarse antes de realizar otra operación."
                });
            }
            if (payment.Status == "paid")
            {
                return Ok(new { attemptId = payment.Id, status = "paid", message = "La operación ya fue pagada exitosamente." });
            }
            return Conflict(new { message = "Intento no disponible para realizar un cargo" });
        }

        // Si ya cuenta con un ProviderId registrado, no volver a disparar el cargo externo; verificar su estado:
        if (payment.ProviderId != null)
        {
            var applied = await VerifyAndApply(payment.Id, ct);
            if (applied)
            {
                return Ok(new { attemptId = payment.Id, status = "paid", message = "Cargo verificado exitosamente" });
            }
            return Conflict(new { attemptId = payment.Id, status = payment.Status, message = "El cargo ya fue enviado al proveedor y está en proceso de verificación." });
        }

        if (string.IsNullOrWhiteSpace(request.SourceId) ||
            !(request.SourceId.StartsWith("tkn_test_") || request.SourceId.StartsWith("ype_test_")))
            return BadRequest(new { message = "Debe enviar un token de sandbox emitido por Culqi" });

        // Cambio de estado atómico en PostgreSQL; impide cargos simultáneos al mismo intento.
        int claimed;
        if (_db.Database.IsRelational())
        {
            claimed = await _db.Set<CulqiPayment>().Where(p => p.Id == payment.Id && (p.Status == "created" || p.Status == "3ds_required"))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "processing"), ct);
        }
        else
        {
            payment.Status = "processing";
            await _db.SaveChangesAsync(ct);
            claimed = 1;
        }
        if (claimed != 1) return Conflict(new { message = "El intento ya está en procesamiento" });
        payment.Status = "processing";

        try
        {
            var chargePayload = new Dictionary<string, object?>
            {
                ["amount"] = payment.AmountCents,
                ["currency_code"] = "PEN",
                ["email"] = payment.Booking.CustomerEmail,
                ["source_id"] = request.SourceId,
                ["capture"] = true,
                ["description"] = $"Reserva {payment.Booking.ReferenceCode}",
                ["metadata"] = new { booking_id = payment.BookingId.ToString(), attempt_id = payment.Id.ToString() }
            };

            // Soporte oficial Culqi 3D Secure
            if (request.Parameters3DS.HasValue)
            {
                chargePayload["authentication_3DS"] = request.Parameters3DS.Value;
            }

            if (!string.IsNullOrWhiteSpace(request.DeviceFingerPrintId))
            {
                var names = (payment.Booking.CustomerName ?? "Cliente").Split(' ', 2);
                chargePayload["antifraud_details"] = new
                {
                    first_name = names[0],
                    last_name = names.Length > 1 ? names[1] : "Pasajero",
                    phone_number = payment.Booking.CustomerPhone ?? "999999999",
                    device_finger_print_id = request.DeviceFingerPrintId
                };
            }

            using var charge = await _culqi.PostAsync("charges", chargePayload, ct);

            // 1. Manejo de autenticación 3D Secure (banco emisor requiere reto)
            if (charge.Text("action_code") == "REVIEW" ||
                (charge.Body != null && charge.Body.RootElement.TryGetProperty("action_code", out var actCode) && actCode.GetString() == "REVIEW"))
            {
                payment.Status = "3ds_required"; // Estado específico para 3DS
                await _db.SaveChangesAsync(ct);
                return Ok(new
                {
                    attemptId = payment.Id,
                    status = "3ds_required",
                    tokenId = request.SourceId,
                    message = "El banco emisor requiere validación 3D Secure"
                });
            }

            // 2. Cargo exitoso
            if (charge.Success && charge.Text("object") == "charge" && charge.Number("amount") == payment.AmountCents
                && charge.Text("id") is { } chargeId)
            {
                payment.ProviderId = chargeId;
                payment.Status = "verification";
                await _db.SaveChangesAsync(ct);
                var applied = await VerifyAndApply(payment.Id, ct);
                return Ok(new { attemptId = payment.Id, status = applied ? "paid" : "verification",
                    message = applied ? "Cargo verificado exitosamente" : "Esperando confirmación final del proveedor" });
            }

            // 3. Diferenciación de Rechazo Definitivo (fondos insuficientes, tarjeta inválida/declinada, 400/402)
            if (charge.HttpStatus is >= 400 and < 500)
            {
                var errorMsg = charge.Text("user_message") ?? charge.Text("merchant_message") ?? "Tarjeta rechazada por la entidad bancaria";
                payment.Status = "failed";
                await _db.SaveChangesAsync(ct);
                return BadRequest(new { attemptId = payment.Id, status = "failed", message = errorMsg });
            }

            // 4. Errores ambiguos / servidor externo (HTTP 5xx)
            payment.Status = "verification";
            await _db.SaveChangesAsync(ct);
            return StatusCode(202, new { status = "verification", message = "Operación en verificación; consulte el estado en unos momentos" });
        }
        catch (HttpRequestException)
        {
            // Error de conexión con Culqi: estado ambiguo, requiere verificación antes de reintentar
            payment.Status = "verification";
            await _db.SaveChangesAsync(ct);
            return StatusCode(202, new { status = "verification", message = "Tiempo de espera agotado; requiere conciliación" });
        }
        catch (TaskCanceledException)
        {
            payment.Status = "verification";
            await _db.SaveChangesAsync(CancellationToken.None);
            return StatusCode(202, new { status = "verification", message = "Tiempo agotado de conexión; requiere conciliación" });
        }
    }

    [HttpPost("check")]
    [AllowAnonymous]
    public async Task<IActionResult> Check([FromBody] CheckPayment request, CancellationToken ct)
    {
        try
        {
            CulqiPayment? payment = null;
            if (request.AttemptId != Guid.Empty)
            {
                payment = await _db.Set<CulqiPayment>()
                    .FirstOrDefaultAsync(p => p.Id == request.AttemptId, CancellationToken.None);
            }
            else if (request.BookingId.HasValue && request.BookingId.Value != Guid.Empty)
            {
                payment = await _db.Set<CulqiPayment>()
                    .Where(p => p.BookingId == request.BookingId.Value)
                    .OrderByDescending(p => p.CreatedAtUtc)
                    .FirstOrDefaultAsync(CancellationToken.None);
            }

            if (payment == null) return NotFound();
            if (!_access.Verify(payment.BookingId, request.CheckoutToken)) return Unauthorized();

            if (!string.IsNullOrWhiteSpace(payment.ProviderId))
            {
                await ProcessPendingWebhookEventsAsync(payment.ProviderId, CancellationToken.None);
            }

            if (payment.ProviderId != null && payment.Status != "paid")
            {
                // Reconciliación inmediata: si Culqi ya registró el pago, aplicar con safe token para no abortar si el cliente cierra la pestaña
                var applied = await VerifyAndApply(payment.Id, CancellationToken.None);
                if (!applied && !ct.IsCancellationRequested)
                {
                    var route = payment.Channel == "wallet_qr"
                        ? $"orders/{Uri.EscapeDataString(payment.ProviderId)}"
                        : $"charges/{Uri.EscapeDataString(payment.ProviderId)}";

                    try
                    {
                        using var queryResult = await _culqi.GetAsync(route, ct);
                        if (queryResult.Success)
                        {
                            var culqiState = queryResult.Text("state") ?? queryResult.Text("status");
                            if (culqiState is "expired" or "failed" or "canceled" or "deleted")
                            {
                                payment.Status = "expired";
                                await _db.SaveChangesAsync(CancellationToken.None);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Desconexión normal del cliente HTTP durante el sondeo
                    }
                    catch
                    {
                        // Timeout o error de red: se mantiene en su estado previo para siguiente sondeo
                    }
                }
            }
            else if (payment.ProviderId == null && payment.Channel == "charge" && (payment.Status == "verification" || payment.Status == "processing"))
            {
                // Conciliación de cargos sin ProviderId: verificar si el cargo fue capturado en Culqi
                var reconciled = await ReconcileMissingProviderChargeAsync(payment, CancellationToken.None);
                if (reconciled)
                {
                    await _db.SaveChangesAsync(CancellationToken.None);
                    await VerifyAndApply(payment.Id, CancellationToken.None);
                }
            }

            if (ct.IsCancellationRequested)
            {
                return StatusCode(499);
            }

            var refreshed = await _db.Set<CulqiPayment>().AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == payment.Id, CancellationToken.None);
            var status = refreshed?.Status ?? payment.Status;

            var booking = await _db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == payment.BookingId, CancellationToken.None);

            long? expirationDateUnix = null;
            int? expiresInSeconds = null;
            if (payment.Channel == "wallet_qr")
            {
                var expDate = payment.ExpirationDateUtc ?? payment.CreatedAtUtc.AddMinutes(25);
                expirationDateUnix = new DateTimeOffset(expDate).ToUnixTimeSeconds();
                expiresInSeconds = Math.Max(0, (int)(expDate - DateTime.UtcNow).TotalSeconds);
            }

            return Ok(new
            {
                attemptId = payment.Id,
                status,
                bookingId = payment.BookingId,
                orderId = payment.ProviderId,
                channel = payment.Channel,
                amountCents = payment.AmountCents,
                paidAmount = booking?.PaidAmount ?? 0m,
                pendingBalance = booking != null ? Math.Max(0m, booking.TotalAmount - booking.PaidAmount) : 0m,
                expirationDateUnix,
                expiresInSeconds
            });
        }
        catch (OperationCanceledException)
        {
            // El navegador recargó, cerró la pestaña o abortó el sondeo fetch()
            return StatusCode(499);
        }
    }

    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        // 1. Autenticación HTTP Basic Auth conforme a CulqiPanel
        var auth = Request.Headers.Authorization.ToString();
        var user = _config["Culqi:WebhookUser"];
        var pass = _config["Culqi:WebhookPassword"];
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass)
            || !auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized();
        }

        string supplied;
        try
        {
            supplied = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim()));
        }
        catch (FormatException)
        {
            return Unauthorized();
        }

        var expected = Encoding.UTF8.GetBytes(user + ":" + pass);
        var actual = Encoding.UTF8.GetBytes(supplied);
        if (actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return Unauthorized();
        }

        // 2. Parseo del JSON del evento
        using var body = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
        var root = body.RootElement;
        var eventType = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

        if (!root.TryGetProperty("data", out var dataProp))
        {
            return Ok();
        }

        JsonDocument? innerDoc = null;
        JsonElement data;
        if (dataProp.ValueKind == JsonValueKind.String)
        {
            try
            {
                innerDoc = JsonDocument.Parse(dataProp.GetString()!);
                data = innerDoc.RootElement;
            }
            catch
            {
                data = dataProp;
            }
        }
        else
        {
            data = dataProp;
        }

        string? providerId = null;
        string? orderState = null;
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("id", out var directId))
            {
                providerId = directId.GetString();
            }
            else if (data.TryGetProperty("object", out var obj) && obj.ValueKind == JsonValueKind.Object &&
                     obj.TryGetProperty("id", out var embeddedId))
            {
                providerId = embeddedId.GetString();
            }

            if (data.TryGetProperty("state", out var stateProp))
            {
                orderState = stateProp.GetString();
            }
        }

        var rawJson = root.GetRawText();
        innerDoc?.Dispose();

        if (string.IsNullOrWhiteSpace(providerId))
        {
            return Ok();
        }

        var attempt = await _db.Set<CulqiPayment>().FirstOrDefaultAsync(p => p.ProviderId == providerId, ct);
        if (attempt == null)
        {
            // Evento para orden/cargo que aún no tiene ProviderId guardado en esta base de datos:
            // Persistir en PaymentWebhookEvents para recuperación sin perder confirmación ni estado.
            _db.PaymentWebhookEvents.Add(new PaymentWebhookEvent
            {
                Id = Guid.NewGuid(),
                ProviderId = providerId,
                EventType = eventType,
                OrderState = orderState,
                RawPayload = rawJson,
                Processed = false,
                ReceivedAtUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(CancellationToken.None);
            return Ok();
        }

        // 3. Manejo de orden expirada o cancelada
        if (orderState is "expired" or "canceled" or "deleted")
        {
            if (attempt.Status != "paid")
            {
                attempt.Status = "expired";
                await _db.SaveChangesAsync(ct);
            }
            return Ok();
        }

        // 4. Verificación oficial en la API de Culqi (no confiar únicamente en el cuerpo del webhook)
        await VerifyAndApply(attempt.Id, ct);

        return Ok();
    }

    private async Task<bool> ReconcileMissingProviderChargeAsync(CulqiPayment attempt, CancellationToken ct)
    {
        if (attempt.ProviderId != null || attempt.Channel != "charge") return false;

        try
        {
            var attemptIdStr = attempt.Id.ToString();
            var bookingIdStr = attempt.BookingId.ToString();
            string? cursor = null;
            bool hasMore = true;
            int pageCount = 0;

            while (hasMore && pageCount < 10) // Paginación de hasta 1000 cargos
            {
                pageCount++;
                var url = string.IsNullOrWhiteSpace(cursor)
                    ? "charges?limit=100"
                    : $"charges?limit=100&after={Uri.EscapeDataString(cursor)}";

                using var chargesResp = await _culqi.GetAsync(url, ct);
                if (!chargesResp.Success || chargesResp.Body == null) break;

                var root = chargesResp.Body.RootElement;
                if (!root.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array)
                    break;

                string? lastIdOnPage = null;
                int countOnPage = 0;

                foreach (var chr in dataArr.EnumerateArray())
                {
                    countOnPage++;
                    if (chr.TryGetProperty("id", out var idProp))
                    {
                        lastIdOnPage = idProp.GetString();
                    }

                    if (chr.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
                    {
                        // 1. Correlación estricta e inequívoca por attempt_id
                        if (meta.TryGetProperty("attempt_id", out var attProp) &&
                            string.Equals(attProp.GetString(), attemptIdStr, StringComparison.OrdinalIgnoreCase))
                        {
                            var foundId = idProp.GetString();
                            if (!string.IsNullOrWhiteSpace(foundId))
                            {
                                attempt.ProviderId = foundId;
                                return true;
                            }
                        }
                        // 2. Cargo huérfano con misma reserva y monto pero sin attempt_id o con attempt_id diferente:
                        // NUNCA asociar automáticamente (podría cruzar un adelanto con el saldo).
                        // Abrir incidencia de auditoría si aún no existe.
                        else if (meta.TryGetProperty("booking_id", out var bkProp) &&
                                 string.Equals(bkProp.GetString(), bookingIdStr, StringComparison.OrdinalIgnoreCase) &&
                                 chr.TryGetProperty("amount", out var amProp) && amProp.GetInt64() == attempt.AmountCents)
                        {
                            var foundId = idProp.GetString() ?? "";
                            var existsIncident = await _db.PaymentIncidents.AnyAsync(i => i.ProviderId == foundId, ct);
                            if (!existsIncident && !string.IsNullOrWhiteSpace(foundId))
                            {
                                _db.PaymentIncidents.Add(new PaymentIncident
                                {
                                    Id = Guid.NewGuid(),
                                    IncidentType = "AmbiguousChargeMatch",
                                    Severity = "Warning",
                                    BookingId = attempt.BookingId,
                                    CulqiPaymentId = attempt.Id,
                                    ProviderId = foundId,
                                    Amount = attempt.AmountCents / 100m,
                                    Currency = "PEN",
                                    Description = $"Cargo ambiguo {foundId} detectado para reserva {attempt.BookingId} (S/ {attempt.AmountCents / 100m}) sin coincidencia exacta de attempt_id.",
                                    Status = "PendingReview",
                                    CreatedAtUtc = DateTime.UtcNow
                                });
                                await _db.SaveChangesAsync(ct);
                            }
                        }
                    }
                }

                if (countOnPage < 100 || string.IsNullOrWhiteSpace(lastIdOnPage))
                {
                    hasMore = false;
                }
                else
                {
                    cursor = lastIdOnPage;
                }
            }
        }
        catch
        {
            // Error transitorio consultando Culqi
        }

        return false;
    }

    private async Task<bool> IsOrderPaidInCulqi(string orderId, long expectedAmountCents, CancellationToken ct)
    {
        // 1. Verificación directa mediante el endpoint oficial de orden /v2/orders/{id}
        try
        {
            using var direct = await _culqi.GetAsync($"orders/{Uri.EscapeDataString(orderId)}", ct);
            if (direct.Success && direct.Text("id") == orderId && direct.Number("amount") == expectedAmountCents)
            {
                var currency = direct.Text("currency_code") ?? direct.Text("currency");
                if (currency == null || currency == "PEN")
                {
                    var state = direct.Text("state");
                    if (state == "paid") return true;
                }
            }
        }
        catch
        {
            // Continuar con verificación de reconciliación
        }

        // 2. Reconciliación Culqi Sandbox: en ambiente de prueba, Culqi puede retener la instantánea inicial 'pending'
        // en GET /orders/{id} tras el abono en simulador/billetera. Consultamos la lista oficial de órdenes pagadas (?state=paid):
        try
        {
            using var paidList = await _culqi.GetAsync("orders?state=paid&limit=50", ct);
            if (paidList.Success && paidList.Body != null &&
                paidList.Body.RootElement.TryGetProperty("data", out var dataArr) &&
                dataArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataArr.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idProp) && idProp.GetString() == orderId &&
                        item.TryGetProperty("state", out var stProp) && stProp.GetString() == "paid" &&
                        item.TryGetProperty("amount", out var amProp) && amProp.GetInt64() == expectedAmountCents)
                    {
                        var curr = item.TryGetProperty("currency_code", out var currProp) ? currProp.GetString() : "PEN";
                        if (curr == null || curr == "PEN")
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch
        {
            // Continuar con eventos
        }

        // 3. Reconciliación por eventos Culqi /v2/events (order.status.changed)
        try
        {
            using var eventsList = await _culqi.GetAsync("events?limit=50", ct);
            if (eventsList.Success && eventsList.Body != null &&
                eventsList.Body.RootElement.TryGetProperty("data", out var evArr) &&
                evArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var ev in evArr.EnumerateArray())
                {
                    if (ev.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "order.status.changed" &&
                        ev.TryGetProperty("data", out var evData) && evData.ValueKind == JsonValueKind.Object &&
                        evData.TryGetProperty("id", out var idProp) && idProp.GetString() == orderId &&
                        evData.TryGetProperty("state", out var stProp) && stProp.GetString() == "paid" &&
                        evData.TryGetProperty("amount", out var amProp) && amProp.GetInt64() == expectedAmountCents)
                    {
                        var curr = evData.TryGetProperty("currency_code", out var currProp) ? currProp.GetString() : "PEN";
                        if (curr == null || curr == "PEN")
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallo transitorio de red
        }

        return false;
    }

    private async Task<bool> VerifyAndApply(Guid attemptId, CancellationToken ct = default)
    {
        // Se utiliza CancellationToken.None para la persistencia transaccional crítica en base de datos.
        // Esto garantiza que si el cliente HTTP desconecta o recarga su navegador durante el sondeo,
        // no se anule ni aborte el registro financiero ni la emisión tributaria.
        var safeCt = CancellationToken.None;

        var payment = await _db.Set<CulqiPayment>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == attemptId, safeCt);
        if (payment?.ProviderId == null) return false;

        await ProcessPendingWebhookEventsAsync(payment.ProviderId, safeCt);

        var isPaid = false;
        if (payment.Channel == "wallet_qr")
        {
            isPaid = await IsOrderPaidInCulqi(payment.ProviderId, payment.AmountCents, safeCt);
            if (!isPaid)
            {
                // Si aún no está confirmado como pagado, verificar si la orden fue expirada o cancelada
                try
                {
                    using var result = await _culqi.GetAsync($"orders/{Uri.EscapeDataString(payment.ProviderId)}", safeCt);
                    if (result.Success)
                    {
                        var orderState = result.Text("state");
                        if (orderState is "expired" or "canceled" or "deleted")
                        {
                            var curExp = await _db.Set<CulqiPayment>().FirstAsync(p => p.Id == attemptId, safeCt);
                            if (curExp.Status != "paid")
                            {
                                curExp.Status = "expired";
                                await _db.SaveChangesAsync(safeCt);
                            }
                        }
                    }
                }
                catch
                {
                    // Error transitorio
                }
                return false;
            }
        }
        else
        {
            var route = $"charges/{Uri.EscapeDataString(payment.ProviderId)}";
            using var result = await _culqi.GetAsync(route, safeCt);
            if (!result.Success || result.Text("id") != payment.ProviderId || result.Number("amount") != payment.AmountCents)
                return false;

            var root = result.Body?.RootElement;
            if (root == null) return false;

            var isChargeObj = result.Text("object") == "charge";
            var hasCapture = (root.Value.TryGetProperty("capture", out var capProp) && capProp.ValueKind == JsonValueKind.True)
                             || (root.Value.TryGetProperty("captured", out var capDProp) && capDProp.ValueKind == JsonValueKind.True);
            var isSuccessfulOutcome = root.Value.TryGetProperty("outcome", out var outcomeProp)
                                      && outcomeProp.ValueKind == JsonValueKind.Object
                                      && outcomeProp.TryGetProperty("type", out var typeProp)
                                      && typeProp.GetString() == "venta_exitosa";

            isPaid = isChargeObj && (hasCapture || isSuccessfulOutcome);
            if (!isPaid) return false;
        }

        // La transacción en PostgreSQL evita contabilizar dos veces la MISMA operación.
        await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, safeCt) : null;
        var current = await _db.Set<CulqiPayment>().FirstAsync(p => p.Id == attemptId, safeCt);
        if (current.Status == "paid")
        {
            if (tx != null) await tx.CommitAsync(safeCt);
            return true;
        }

        var booking = await _db.Bookings
            .Include(b => b.Items)
            .ThenInclude(i => i.TourPackage)
            .FirstAsync(b => b.Id == current.BookingId, safeCt);

        var paidIncrement = current.AmountCents / 100m;
        bool isCancelled = booking.Status == BookingStatus.Cancelled;
        bool isOverpayment = (booking.PaidAmount + paidIncrement > booking.TotalAmount);

        if (isCancelled || isOverpayment)
        {
            // Bloque 1 Item 7: Registrar el dinero externo confirmado aun si la reserva está cancelada o hay exceso.
            // Crear incidencia para su resolución y no descartar el movimiento.
            current.Status = isOverpayment ? "paid_excess" : "paid";
            current.ConfirmedAtUtc = DateTime.UtcNow;

            var webShift = await GetOrCreateWebShiftAsync(safeCt);
            if (!_db.CashTransactions.Any(t => t.CulqiPaymentId == current.Id))
            {
                _db.CashTransactions.Add(new CashTransaction
                {
                    Id = Guid.NewGuid(),
                    ShiftId = webShift.Id,
                    CulqiPaymentId = current.Id,
                    TransactionType = "Ingreso",
                    Category = isCancelled ? "Cobro de Reserva Cancelada" : "Exceso de Pago / Cobro No Asignado",
                    Amount = paidIncrement,
                    PaymentMethod = current.Channel == "wallet_qr" ? "Culqi QR" : "Culqi",
                    ReferenceCode = booking.ReferenceCode,
                    BookingId = booking.Id,
                    Description = isCancelled
                        ? $"Cobro Culqi verificado para reserva previamente anulada {booking.ReferenceCode}. Requiere revisión administrativa o reprogramación."
                        : $"Cobro Culqi con exceso de pago sobre reserva {booking.ReferenceCode}. Cobrado: S/ {paidIncrement}, Total reserva: S/ {booking.TotalAmount}.",
                    RegisteredBy = "Pasarela Culqi Sandbox",
                    CreatedAt = DateTime.UtcNow
                });
            }

            _db.PaymentIncidents.Add(new PaymentIncident
            {
                Id = Guid.NewGuid(),
                IncidentType = isCancelled ? "CancelledBookingPayment" : "Overpayment",
                Severity = "Critical",
                BookingId = booking.Id,
                CulqiPaymentId = current.Id,
                ProviderId = current.ProviderId,
                Amount = paidIncrement,
                Currency = "PEN",
                Description = isCancelled
                    ? $"El cliente abonó S/ {paidIncrement} externamente pero la reserva {booking.ReferenceCode} se encontraba en estado Cancelada."
                    : $"El cliente abonó S/ {paidIncrement} en la reserva {booking.ReferenceCode}, excediendo el saldo pendiente.",
                Status = "PendingReview",
                CreatedAtUtc = DateTime.UtcNow
            });

            await _db.SaveChangesAsync(safeCt);
            if (tx != null) await tx.CommitAsync(safeCt);
            return true;
        }

        current.Status = "paid";
        current.ConfirmedAtUtc = DateTime.UtcNow;
        booking.PaidAmount += paidIncrement;
        booking.PaymentMethod = current.Channel == "wallet_qr" ? "Culqi QR" : "Culqi";
        booking.OperationNumber = current.ProviderId ?? "";
        
        // Confirmar reserva si se completó el total o el adelanto mínimo requerido
        var requiredAdvance = booking.Items.Sum(i => i.SubTotal *
            (i.TourPackage?.RequiredAdvancePercentage > 0 ? i.TourPackage.RequiredAdvancePercentage : 50) / 100m);
        if (booking.PaidAmount >= booking.TotalAmount || (requiredAdvance > 0 && booking.PaidAmount >= requiredAdvance))
        {
            booking.Status = BookingStatus.Confirmed;
        }

        // Registrar ingreso para conciliación contable garantizada (incluso si no hay turno presencial abierto)
        // estrictamente sin duplicados por CulqiPaymentId
        if (!_db.CashTransactions.Any(t => t.CulqiPaymentId == current.Id))
        {
            var shift = await GetOrCreateWebShiftAsync(safeCt);
            var isPartial = booking.PaidAmount < booking.TotalAmount;
            _db.CashTransactions.Add(new CashTransaction
            {
                Id = Guid.NewGuid(),
                ShiftId = shift.Id,
                CulqiPaymentId = current.Id,
                TransactionType = "Ingreso",
                Category = "Cobro de Reserva",
                Amount = paidIncrement,
                PaymentMethod = booking.PaymentMethod,
                ReferenceCode = booking.ReferenceCode,
                BookingId = booking.Id,
                Description = $"Pago Culqi confirmado {(isPartial ? "(Adelanto)" : "(Saldo / Total)")} para reserva {booking.ReferenceCode} - {booking.CustomerName}",
                RegisteredBy = "Pasarela Culqi Sandbox",
                CreatedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync(safeCt);
        if (tx != null) await tx.CommitAsync(safeCt);

        // Disparo desacoplado para emisión asíncrona de comprobante NubeFact (modo simulación en Sandbox)
        try
        {
            await _receiptService.GenerateReceiptAsync(booking.Id, current.Id, safeCt);
        }
        catch
        {
            // Falla en emisión no anula el cobro verificado
        }

        return true;
    }
}

public sealed record PreparePayment(Guid BookingId, string CheckoutToken, string Mode, string Channel);
public sealed record ChargePayment(Guid AttemptId, string CheckoutToken, string SourceId, JsonElement? Parameters3DS = null, string? DeviceFingerPrintId = null);
public sealed record CheckPayment(Guid AttemptId, string CheckoutToken, Guid? BookingId = null);
