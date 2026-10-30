/*
  Integración Culqi Checkout Sandbox para SIGETOUR
  Prerrequisito: <script src="https://js.culqi.com/checkout-js"></script>
*/
let lastCulqiAttempt = null;

async function abrirPagoSigetour({ bookingId, checkoutToken, email, mode = "full", channel = "charge", onStatus = null }) {
  if (onStatus) onStatus("Preparando pago seguro con Culqi...", "info");
  
  const prepareRes = await fetch("/api/culqi/prepare", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ bookingId, checkoutToken, mode, channel })
  });
  
  const data = await prepareRes.json();
  if (!prepareRes.ok) {
    const errorMsg = data.message || "No se pudo preparar el cobro en Culqi";
    if (onStatus) onStatus(errorMsg, "error");
    throw new Error(errorMsg);
  }

  lastCulqiAttempt = data.attemptId;
  try {
    sessionStorage.setItem("culqi_attempt_" + bookingId, data.attemptId);
  } catch (e) {
    console.warn("Storage not available", e);
  }

  // Idempotencia: si ya estaba acreditado
  if (data.status === "paid") {
    if (onStatus) onStatus("La reserva ya cuenta con un pago verificado. Redirigiendo al voucher...", "success");
    setTimeout(() => {
      window.location.href = `/Booking/Voucher/${bookingId}`;
    }, 800);
    return { attemptId: data.attemptId, status: "paid" };
  }

  const methods = channel === "wallet_qr"
    ? { tarjeta: false, yape: false, billetera: true, bancaMovil: false, agente: false, cuotealo: false }
    : { tarjeta: true, yape: true, billetera: false, bancaMovil: false, agente: false, cuotealo: false };

  const settings = { title: "Turismo Chilón - SIGETOUR", currency: "PEN", amount: data.amountCents };
  if (data.orderId) settings.order = data.orderId;

  if (typeof CulqiCheckout === "undefined") {
    const msg = "La pasarela de pago Culqi no pudo cargarse en el navegador. Por favor refresque la página.";
    if (onStatus) onStatus(msg, "error");
    throw new Error(msg);
  }

  const checkout = new CulqiCheckout(data.publicKey, {
    settings,
    client: { email: email || "cliente@turismochilon.com" },
    options: {
      lang: "es",
      modal: true,
      paymentMethods: methods,
      paymentMethodsSort: Object.keys(methods)
    }
  });

  checkout.culqi = async () => {
    try {
      if (checkout.token) {
        checkout.close(); // Cerramos el modal de inmediato para dar retroalimentación clara
        if (onStatus) onStatus("Procesando cobro en Culqi...", "info");
        const res = await fetch("/api/culqi/charge", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ attemptId: data.attemptId, checkoutToken, sourceId: checkout.token.id })
        });
        const statusData = await res.json();
        if (res.ok && statusData.status === "paid") {
          if (onStatus) onStatus("¡Pago exitoso verificado! Redirigiendo a tu voucher...", "success");
          setTimeout(() => {
            window.location.href = `/Booking/Voucher/${bookingId}`;
          }, 800);
        } else {
          if (onStatus) onStatus(statusData.message || "Pago en verificación. Consulta el estado antes de intentar nuevamente.", "warning");
        }
      } else if (checkout.order) {
        checkout.close();
        if (onStatus) onStatus("Orden QR Culqi generada exitosamente. Se confirmará automáticamente al completar el pago.", "info");
      } else {
        if (onStatus) onStatus("No se completó la operación en Culqi.", "warning");
      }
    } catch (err) {
      if (onStatus) onStatus("Error al procesar cargo: " + err.message, "error");
    }
  };

  checkout.open();
  return { attemptId: data.attemptId };
}

// Para verificar sin generar cargos nuevamente
async function revisarPagoSigetour({ attemptId = null, checkoutToken, bookingId = null, onStatus = null }) {
  if (!attemptId && bookingId) {
    try {
      attemptId = sessionStorage.getItem("culqi_attempt_" + bookingId) || lastCulqiAttempt;
    } catch (e) {
      attemptId = lastCulqiAttempt;
    }
  }

  if (onStatus) onStatus("Consultando estado de pago en el servidor...", "info");
  const res = await fetch("/api/culqi/check", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      attemptId: attemptId || "00000000-0000-0000-0000-000000000000",
      bookingId: bookingId || null,
      checkoutToken
    })
  });

  const data = await res.json();
  if (!res.ok) {
    const errorMsg = data.message || "No fue posible consultar el intento de pago";
    if (onStatus) onStatus(errorMsg, "error");
    throw new Error(errorMsg);
  }

  if (data.status === "paid") {
    if (onStatus) onStatus("¡Pago confirmado y acreditado! Redirigiendo al voucher...", "success");
    const targetBookingId = data.bookingId || bookingId;
    if (targetBookingId) {
      setTimeout(() => {
        window.location.href = `/Booking/Voucher/${targetBookingId}`;
      }, 800);
    }
  } else if (data.status === "verification" || data.status === "processing") {
    if (onStatus) onStatus(`El pago está en estado "${data.status}". Si ya pagó, espere unos segundos y vuelva a consultar.`, "warning");
  } else {
    if (onStatus) onStatus(`Estado del intento: ${data.status}.`, "info");
  }

  return data;
}
