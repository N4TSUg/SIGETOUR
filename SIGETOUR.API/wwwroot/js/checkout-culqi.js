/*
  Integración Culqi Checkout Sandbox y 3D Secure para SIGETOUR
  - Script oficial Checkout: https://js.culqi.com/checkout-js (CulqiCheckout)
  - Script oficial 3DS: https://3ds.culqi.com/js/v1 (Culqi3DS)
*/
let lastCulqiAttempt = null;
let currentDeviceFingerprint = null;
let currentBookingContext = null;

async function getDeviceFingerprint() {
  if (currentDeviceFingerprint) return currentDeviceFingerprint;
  if (typeof Culqi3DS !== "undefined" && typeof Culqi3DS.generateDevice === "function") {
    try {
      currentDeviceFingerprint = await Culqi3DS.generateDevice();
    } catch (e) {
      console.warn("No se pudo generar huella 3DS de dispositivo:", e);
    }
  }
  return currentDeviceFingerprint;
}

// Escuchador global de respuestas de autenticación 3D Secure del banco emisor
window.addEventListener("message", async function (event) {
  // Validación estricta del origen del evento (solo Culqi 3DS oficial)
  const origin = event.origin || "";
  if (origin !== "https://3ds.culqi.com" && !origin.endsWith(".culqi.com")) {
    return;
  }

  if (!currentBookingContext || !currentBookingContext.attemptId) return;

  const data = event.data;
  if (data && data.parameters3DS) {
    const { attemptId, checkoutToken, bookingId, onStatus, sourceId } = currentBookingContext;
    if (onStatus) onStatus("Autenticación 3D Secure aprobada por el banco. Finalizando cobro...", "info");

    try {
      const res = await fetch("/api/culqi/charge", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          attemptId,
          checkoutToken,
          sourceId,
          parameters3DS: data.parameters3DS,
          deviceFingerPrintId: currentDeviceFingerprint
        })
      });

      const result = await res.json();
      if (res.ok && result.status === "paid") {
        if (onStatus) onStatus("¡Pago 3D Secure exitoso! Redirigiendo a tu voucher...", "success");
        setTimeout(() => {
          window.location.href = `/Booking/Voucher/${bookingId}`;
        }, 800);
      } else {
        const msg = result.message || "El pago no pudo confirmarse tras la validación 3DS.";
        if (onStatus) onStatus(msg, "warning");
      }
    } catch (err) {
      if (onStatus) onStatus("Error enviando autenticación 3DS: " + err.message, "error");
    }
  } else if (data && data.error) {
    if (currentBookingContext.onStatus) {
      currentBookingContext.onStatus("Validación 3D Secure cancelada o rechazada: " + (data.error.user_message || data.error), "error");
    }
  }
}, false);

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
    const msg = "La pasarela de pago Culqi no pudo cargarse. Verifica tu conexión a internet y recarga la página.";
    if (onStatus) onStatus(msg, "error");
    throw new Error(msg);
  }

  // Pre-generar fingerprint de dispositivo y configurar Culqi3DS
  if (typeof Culqi3DS !== "undefined") {
    Culqi3DS.publicKey = data.publicKey;
    Culqi3DS.options = { showModal: true, showLoading: true, showDirect: true };
  }
  await getDeviceFingerprint();

  currentBookingContext = {
    attemptId: data.attemptId,
    bookingId,
    checkoutToken,
    onStatus
  };

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
        checkout.close();
        if (onStatus) onStatus("Procesando cobro seguro en Culqi...", "info");
        
        currentBookingContext.sourceId = checkout.token.id;
        const deviceId = await getDeviceFingerprint();

        const res = await fetch("/api/culqi/charge", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            attemptId: data.attemptId,
            checkoutToken,
            sourceId: checkout.token.id,
            deviceFingerPrintId: deviceId
          })
        });

        const statusData = await res.json();

        // Si requiere autenticación 3DS
        if (res.ok && statusData.status === "3ds_required") {
          if (onStatus) onStatus("Validando identidad con el banco (3D Secure)...", "info");
          if (typeof Culqi3DS !== "undefined" && typeof Culqi3DS.initAuthentication === "function") {
            Culqi3DS.options = { showModal: true, showLoading: true };
            await Culqi3DS.initAuthentication(checkout.token.id);
          } else {
            if (onStatus) onStatus("Autenticación 3DS requerida. Actualiza tu navegador.", "warning");
          }
          return;
        }

        if (res.ok && statusData.status === "paid") {
          if (onStatus) onStatus("¡Pago exitoso verificado! Redirigiendo a tu voucher...", "success");
          setTimeout(() => {
            window.location.href = `/Booking/Voucher/${bookingId}`;
          }, 800);
        } else if (!res.ok && statusData.status === "failed") {
          // Rechazo definitivo (fondos insuficientes, tarjeta rechazada)
          if (onStatus) onStatus(statusData.message || "Tarjeta rechazada. Puedes intentar con otra tarjeta inmediatamente.", "error");
        } else {
          if (onStatus) onStatus(statusData.message || "Pago en verificación. Consulta el estado antes de intentar nuevamente.", "warning");
        }
      } else if (checkout.order) {
        checkout.close();
        if (onStatus) onStatus("Orden QR Culqi generada exitosamente. Se confirmará automáticamente al completar el pago en tu aplicación.", "info");
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

// Para verificar sin generar cargos nuevamente (recuperación de estado al recargar)
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
    if (onStatus) onStatus(`El pago está en proceso de verificación ("${data.status}"). Si ya abonó en Yape o banco, espere unos segundos y vuelva a pulsar consultar.`, "warning");
  } else if (data.status === "failed") {
    if (onStatus) onStatus("El último intento fue rechazado o caducó. Puedes realizar un nuevo intento con total seguridad.", "info");
  } else {
    if (onStatus) onStatus(`Estado del intento: ${data.status}.`, "info");
  }

  return data;
}
