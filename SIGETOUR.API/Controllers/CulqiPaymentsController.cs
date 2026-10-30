using System.Data;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    public CulqiPaymentsController(AppDbContext db, CulqiApiClient culqi, CheckoutAccess access, IConfiguration config)
    { _db = db; _culqi = culqi; _access = access; _config = config; }

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

        // Gestión inteligente de intentos pendientes: reutilizar o limpiar intentos caducados
        var existingAttempt = await _db.Set<CulqiPayment>().FirstOrDefaultAsync(p => p.BookingId == booking.Id &&
            (p.Status == "created" || p.Status == "processing" || p.Status == "verification"), ct);
            
        if (existingAttempt != null)
        {
            if (existingAttempt.CreatedAtUtc < DateTime.UtcNow.AddMinutes(-25))
            {
                existingAttempt.Status = "failed";
                await _db.SaveChangesAsync(ct);
            }
            else if (existingAttempt.Status == "created" && existingAttempt.AmountCents == cents && existingAttempt.Channel == request.Channel)
            {
                return Ok(new { attemptId = existingAttempt.Id, amountCents = cents, currency = "PEN",
                    publicKey = _config["Culqi:PublicKey"], status = existingAttempt.Status });
            }
            else if (existingAttempt.Status == "verification" && existingAttempt.ProviderId != null)
            {
                var isVerified = await VerifyAndApply(existingAttempt.Id, ct);
                if (isVerified)
                {
                    return Ok(new { attemptId = existingAttempt.Id, amountCents = cents, currency = "PEN",
                        publicKey = _config["Culqi:PublicKey"], status = "paid", message = "La reserva ya cuenta con un pago verificado" });
                }
                return Conflict(new { message = "Existe un pago en verificación en Culqi. Por favor consulte el estado antes de intentar otro abono.", attemptId = existingAttempt.Id });
            }
            else
            {
                return Conflict(new { message = "Existe un intento pendiente en proceso; consulte el estado o intente nuevamente en breves instantes." });
            }
        }

        var attempt = new CulqiPayment { BookingId = booking.Id, AmountCents = cents, Channel = request.Channel };
        _db.Set<CulqiPayment>().Add(attempt);
        await _db.SaveChangesAsync(ct);

        if (request.Channel == "wallet_qr")
        {
            var words = (booking.CustomerName ?? "Cliente SIGETOUR").Trim().Split(' ', 2);
            var phone = booking.CustomerPhone?.Trim() ?? "";
            using var order = await _culqi.PostAsync("orders", new
            {
                amount = cents, currency_code = "PEN", description = $"Reserva {booking.ReferenceCode}",
                order_number = $"SIG-{attempt.Id:N}",
                client_details = new
                {
                    first_name = words.ElementAtOrDefault(0) ?? "Cliente",
                    last_name = words.ElementAtOrDefault(1) ?? "Turismo",
                    email = booking.CustomerEmail,
                    phone_number = phone.StartsWith("+") ? phone : "+51" + phone
                },
                expiration_date = DateTimeOffset.UtcNow.AddMinutes(25).ToUnixTimeSeconds(),
                confirm = true,
                metadata = new { booking_id = booking.Id.ToString() }
            }, ct);
            if (!order.Success || order.Text("object") != "order" ||
                order.Number("amount") != cents || order.Text("id") is not { } orderId)
            {
                attempt.Status = "failed";
                await _db.SaveChangesAsync(ct);
                return StatusCode(502, new { message = "No fue posible crear la orden Culqi en sandbox" });
            }
            attempt.ProviderId = orderId;
            attempt.Status = "verification"; // QR: nunca confirmar por mostrarlo al usuario
            await _db.SaveChangesAsync(ct);
            return Ok(new { attemptId = attempt.Id, amountCents = cents, currency = "PEN", orderId,
                publicKey = _config["Culqi:PublicKey"], status = attempt.Status });
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
        if (payment.Channel != "charge" || payment.Status != "created")
            return Conflict(new { message = "Intento no disponible para realizar un cargo" });
        if (string.IsNullOrWhiteSpace(request.SourceId) ||
            !(request.SourceId.StartsWith("tkn_test_") || request.SourceId.StartsWith("ype_test_")))
            return BadRequest(new { message = "Debe enviar un token de sandbox emitido por Culqi" });

        // Cambio de estado atómico en PostgreSQL; impide cargos simultáneos al mismo intento.
        var claimed = await _db.Set<CulqiPayment>().Where(p => p.Id == payment.Id && p.Status == "created")
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "processing"), ct);
        if (claimed != 1) return Conflict(new { message = "El intento ya está en procesamiento" });
        payment.Status = "processing";
        try
        {
            using var charge = await _culqi.PostAsync("charges", new
            {
                amount = payment.AmountCents, currency_code = "PEN", email = payment.Booking.CustomerEmail,
                source_id = request.SourceId, capture = true,
                description = $"Reserva {payment.Booking.ReferenceCode}",
                metadata = new { booking_id = payment.BookingId.ToString(), attempt_id = payment.Id.ToString() }
            }, ct);
            if (charge.Success && charge.Text("object") == "charge" && charge.Number("amount") == payment.AmountCents
                && charge.Text("id") is { } chargeId)
            {
                payment.ProviderId = chargeId;
                payment.Status = "verification";
                await _db.SaveChangesAsync(ct);
                var applied = await VerifyAndApply(payment.Id, ct);
                return Ok(new { attemptId = payment.Id, status = applied ? "paid" : "verification",
                    message = applied ? "Cargo verificado" : "Esperando verificación del proveedor/3DS" });
            }
            payment.Status = "verification"; // Respuesta ambigua: NO reintentar token de inmediato
            await _db.SaveChangesAsync(ct);
            return StatusCode(202, new { status = "verification", message = "Verifique en Culqi antes de reintentar" });
        }
        catch (HttpRequestException)
        {
            payment.Status = "verification";
            await _db.SaveChangesAsync(ct);
            return StatusCode(202, new { status = "verification", message = "Resultado desconocido; requiere conciliación" });
        }
        catch (TaskCanceledException)
        {
            payment.Status = "verification";
            await _db.SaveChangesAsync(CancellationToken.None);
            return StatusCode(202, new { status = "verification", message = "Tiempo agotado; requiere conciliación" });
        }
    }

    [HttpPost("check")]
    [AllowAnonymous]
    public async Task<IActionResult> Check([FromBody] CheckPayment request, CancellationToken ct)
    {
        var payment = await _db.Set<CulqiPayment>().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.AttemptId, ct);
        if (payment == null) return NotFound();
        if (!_access.Verify(payment.BookingId, request.CheckoutToken)) return Unauthorized();
        if (payment.ProviderId != null && payment.Status != "paid") await VerifyAndApply(payment.Id, ct);
        var status = await _db.Set<CulqiPayment>().AsNoTracking()
            .Where(p => p.Id == request.AttemptId).Select(p => p.Status).SingleAsync(ct);
        return Ok(new { attemptId = payment.Id, status, bookingId = payment.BookingId });
    }

    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        // Activar autenticación BASIC en CulqiPanel y configurar usuario/clave fuera de Git.
        var auth = Request.Headers.Authorization.ToString();
        var user = _config["Culqi:WebhookUser"];
        var pass = _config["Culqi:WebhookPassword"];
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass)
            || !auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return Unauthorized();
        string supplied;
        try { supplied = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim())); }
        catch (FormatException) { return Unauthorized(); }
        var expected = Encoding.UTF8.GetBytes(user + ":" + pass);
        var actual = Encoding.UTF8.GetBytes(supplied);
        if (actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(actual, expected))
            return Unauthorized();
        using var body = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
        // La estructura exacta del evento deberá cotejarse con una entrega real de Culqi sandbox.
        var root = body.RootElement;
        if (!root.TryGetProperty("data", out var data)) return Ok();
        string? providerId = null;
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("object", out var obj) && obj.ValueKind == JsonValueKind.Object &&
                obj.TryGetProperty("id", out var embedded)) providerId = embedded.GetString();
            else if (data.TryGetProperty("id", out var direct)) providerId = direct.GetString();
        }
        if (providerId == null) return Ok();
        var attempt = await _db.Set<CulqiPayment>().AsNoTracking().FirstOrDefaultAsync(p => p.ProviderId == providerId, ct);
        if (attempt != null) await VerifyAndApply(attempt.Id, ct); // JAMÁS confiar solo en el cuerpo del webhook
        return Ok();
    }

    private async Task<bool> VerifyAndApply(Guid attemptId, CancellationToken ct)
    {
        var payment = await _db.Set<CulqiPayment>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == attemptId, ct);
        if (payment?.ProviderId == null) return false;
        var route = payment.Channel == "wallet_qr" ? $"orders/{Uri.EscapeDataString(payment.ProviderId)}"
            : $"charges/{Uri.EscapeDataString(payment.ProviderId)}";
        using var result = await _culqi.GetAsync(route, ct);
        if (!result.Success || result.Text("id") != payment.ProviderId || result.Number("amount") != payment.AmountCents)
            return false;
        var root = result.Body?.RootElement;
        if (root == null) return false;
        var isPaid = payment.Channel == "wallet_qr"
            ? result.Text("object") == "order" && result.Text("state") == "paid"
            : result.Text("object") == "charge" && root.Value.TryGetProperty("captured", out var captured)
                 && captured.ValueKind == JsonValueKind.True;
        if (!isPaid) return false;

        // La transacción en PostgreSQL evita contabilizar dos veces la MISMA operación.
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var current = await _db.Set<CulqiPayment>().FirstAsync(p => p.Id == attemptId, ct);
        if (current.Status == "paid") { await tx.CommitAsync(ct); return true; }
        var booking = await _db.Bookings
            .Include(b => b.Items)
            .ThenInclude(i => i.TourPackage)
            .FirstAsync(b => b.Id == current.BookingId, ct);
        if (booking.Status == BookingStatus.Cancelled || booking.PaidAmount + current.AmountCents / 100m > booking.TotalAmount)
        { await tx.RollbackAsync(ct); return false; }
        current.Status = "paid";
        current.ConfirmedAtUtc = DateTime.UtcNow;
        var paidIncrement = current.AmountCents / 100m;
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

        // Registrar ingreso en el turno de caja activo para conciliación inmediata
        var activeShift = await _db.CashRegisterShifts.FirstOrDefaultAsync(s => s.IsOpen, ct);
        if (activeShift != null && !_db.CashTransactions.Any(t => t.BookingId == booking.Id && t.ReferenceCode == current.ProviderId))
        {
            _db.CashTransactions.Add(new CashTransaction
            {
                Id = Guid.NewGuid(),
                ShiftId = activeShift.Id,
                TransactionType = "Ingreso",
                Category = "Cobro de Reserva",
                Amount = paidIncrement,
                PaymentMethod = booking.PaymentMethod,
                ReferenceCode = current.ProviderId ?? booking.ReferenceCode,
                BookingId = booking.Id,
                Description = $"Pago Culqi ({current.Channel} - {current.ProviderId}) para reserva {booking.ReferenceCode} - {booking.CustomerName}",
                RegisteredBy = "Pasarela Culqi Sandbox",
                CreatedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        // TODO: publicar evento PaymentConfirmed en un outbox; NO emitir boleta dentro de esta transacción.
        return true;
    }
}

public sealed record PreparePayment(Guid BookingId, string CheckoutToken, string Mode, string Channel);
public sealed record ChargePayment(Guid AttemptId, string CheckoutToken, string SourceId);
public sealed record CheckPayment(Guid AttemptId, string CheckoutToken);
