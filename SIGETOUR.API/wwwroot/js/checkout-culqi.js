/*
  Integración Culqi Checkout Sandbox y 3D Secure para SIGETOUR
  - Script oficial Checkout: https://js.culqi.com/checkout-js (CulqiCheckout)
  - Script oficial 3DS: https://3ds.culqi.com/js/v1 (Culqi3DS)
*/
let lastCulqiAttempt = null;
let currentDeviceFingerprint = null;
let currentBookingContext = null;
let qrPollingTimer = null;
let qrCountdownTimer = null;

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

// Detener temporizadores de sondeo QR
function detenerSondeoQr() {
  if (qrPollingTimer) {
    clearInterval(qrPollingTimer);
    qrPollingTimer = null;
  }
  if (qrCountdownTimer) {
    clearInterval(qrCountdownTimer);
    qrCountdownTimer = null;
  }
}

// Iniciar temporizador regresivo visual de orden QR (25 minutos oficiales Culqi)
function iniciarCuentaRegresivaQr(segundosRestantes, onTick, onExpired) {
  if (qrCountdownTimer) clearInterval(qrCountdownTimer);
  let tiempo = segundosRestantes;
  qrCountdownTimer = setInterval(() => {
    tiempo--;
    if (tiempo <= 0) {
      clearInterval(qrCountdownTimer);
      qrCountdownTimer = null;
      if (onExpired) onExpired();
    } else {
      const min = Math.floor(tiempo / 60);
      const seg = tiempo % 60;
      const fmt = `${min.toString().padStart(2, '0')}:${seg.toString().padStart(2, '0')}`;
      if (onTick) onTick(fmt);
    }
  }, 1000);
}

function isValid3DSOrigin(origin) {
  return origin === "https://3ds.culqi.com" || origin === "https://checkout.culqi.com";
}

function handle3DSChargeResult(isOk, result, bookingId, onStatus) {
  if (isOk && result.status === "paid") {
    if (onStatus) onStatus("¡Pago 3D Secure exitoso! Redirigiendo a tu voucher...", "success");
    setTimeout(() => {
      window.location.href = `/Booking/Voucher/${bookingId}`;
    }, 800);
    return;
  }
  const msg = result.message || "El pago no pudo confirmarse tras la validación 3DS.";
  if (onStatus) onStatus(msg, "warning");
}

async function execute3DSCharge(parameters3DS) {
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
        parameters3DS,
        deviceFingerPrintId: currentDeviceFingerprint
      })
    });

    const result = await res.json();
    handle3DSChargeResult(res.ok, result, bookingId, onStatus);
  } catch (err) {
    if (onStatus) onStatus("Error enviando autenticación 3DS: " + err.message, "error");
  }
}

function handle3DSError(errorData) {
  if (!currentBookingContext?.onStatus) return;
  const msg = errorData.user_message || errorData;
  currentBookingContext.onStatus("Validación 3D Secure cancelada o rechazada: " + msg, "error");
}

// Escuchador global de respuestas de autenticación 3D Secure del banco emisor
window.addEventListener("message", async function (event) {
  // Validación estricta del origen del mensaje cross-origin (Regla SonarQube S2819)
  if (!isValid3DSOrigin(event.origin)) return;
  if (!currentBookingContext?.attemptId) return;

  const data = event.data;
  if (data?.parameters3DS) {
    await execute3DSCharge(data.parameters3DS);
  } else if (data?.error) {
    handle3DSError(data.error);
  }
}, false);

function handleQrPollResponse(checkData, bookingId, checkout, onStatus, onQrState) {
  if (checkData.status === "paid") {
    detenerSondeoQr();
    try { checkout.close(); } catch (e) {}
    if (onStatus) onStatus("¡Pago QR con Yape/Plin verificado exitosamente! Redirigiendo...", "success");
    if (onQrState) onQrState({ state: "paid" });
    setTimeout(() => {
      window.location.href = `/Booking/Voucher/${bookingId}`;
    }, 900);
    return;
  }

  if (checkData.status === "expired" || checkData.status === "failed") {
    detenerSondeoQr();
    try { checkout.close(); } catch (e) {}
    if (onStatus) onStatus("La orden QR expiró o fue cancelada en Culqi. Genera un nuevo código para pagar.", "warning");
    if (onQrState) onQrState({ state: "expired" });
  }
}

async function checkQrStatus(data, bookingId, checkoutToken, checkout, onStatus, onQrState) {
  try {
    const checkRes = await fetch("/api/culqi/check", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        attemptId: data.attemptId,
        bookingId,
        checkoutToken
      })
    });
    if (!checkRes.ok) return;
    const checkData = await checkRes.json();
    handleQrPollResponse(checkData, bookingId, checkout, onStatus, onQrState);
  } catch (pollErr) {
    // Red transitoria: el sondeo continuará en el siguiente intervalo
  }
}

function calcularSegundosRestantes(data) {
  if (typeof data.expiresInSeconds === "number" && data.expiresInSeconds > 0) {
    return data.expiresInSeconds;
  }
  if (data.expirationDateUnix) {
    return Math.max(0, Math.floor(data.expirationDateUnix - (Date.now() / 1000)));
  }
  return 24 * 60;
}

function startQrPolling(data, bookingId, checkoutToken, checkout, onStatus, onQrState) {
  const remainingSeconds = calcularSegundosRestantes(data);

  iniciarCuentaRegresivaQr(remainingSeconds, (timerFmt) => {
    if (onQrState) onQrState({ state: "tick", timer: timerFmt });
  }, () => {
    detenerSondeoQr();
    if (onStatus) onStatus("La orden QR de Culqi ha expirado. Por favor, genera un nuevo código QR.", "warning");
    if (onQrState) onQrState({ state: "expired" });
  });

  qrPollingTimer = setInterval(() => {
    void checkQrStatus(data, bookingId, checkoutToken, checkout, onStatus, onQrState);
  }, 3500);
}

function handleQrStateInit(data, onQrState) {
  if (!onQrState) return;
  onQrState({
    state: "waiting",
    orderId: data.orderId,
    amount: data.amountCents / 100,
    reused: !!data.reused,
    qrCodeUrl: data.qrCodeUrl || null,
    paymentCode: data.paymentCode || null,
    urlPe: data.urlPe || null,
    message: data.reused ? "Orden QR activa recuperada" : "Orden QR creada"
  });
}

async function trigger3DSAuthentication(tokenId, onStatus) {
  if (onStatus) onStatus("Validando identidad con el banco (3D Secure)...", "info");
  if (typeof Culqi3DS !== "undefined" && typeof Culqi3DS.initAuthentication === "function") {
    Culqi3DS.options = { showModal: true, showLoading: true };
    await Culqi3DS.initAuthentication(tokenId);
  } else if (onStatus) {
    onStatus("Autenticación 3DS requerida. Actualiza tu navegador.", "warning");
  }
}

async function handleTokenChargeResponse(res, statusData, tokenId, bookingId, onStatus) {
  if (res.ok && statusData.status === "3ds_required") {
    await trigger3DSAuthentication(tokenId, onStatus);
    return;
  }
  if (res.ok && statusData.status === "paid") {
    if (onStatus) onStatus("¡Pago exitoso verificado! Redirigiendo a tu voucher...", "success");
    setTimeout(() => {
      window.location.href = `/Booking/Voucher/${bookingId}`;
    }, 800);
    return;
  }
  if (!res.ok && statusData.status === "failed") {
    // Rechazo definitivo (fondos insuficientes, tarjeta rechazada)
    if (onStatus) onStatus(statusData.message || "Tarjeta rechazada. Puedes intentar con otra tarjeta inmediatamente.", "error");
    return;
  }
  if (onStatus) onStatus(statusData.message || "Pago en verificación. Consulta el estado antes de intentar nuevamente.", "warning");
}

async function processCulqiToken(checkout, attemptId, checkoutToken, bookingId, onStatus) {
  checkout.close();
  if (onStatus) onStatus("Procesando cobro seguro en Culqi...", "info");

  currentBookingContext.sourceId = checkout.token.id;
  const deviceId = await getDeviceFingerprint();

  const res = await fetch("/api/culqi/charge", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      attemptId,
      checkoutToken,
      sourceId: checkout.token.id,
      deviceFingerPrintId: deviceId
    })
  });

  const statusData = await res.json();
  await handleTokenChargeResponse(res, statusData, checkout.token.id, bookingId, onStatus);
}

function createCheckoutHandler(checkout, attemptId, checkoutToken, bookingId, onStatus) {
  return async () => {
    try {
      if (checkout.token) {
        await processCulqiToken(checkout, attemptId, checkoutToken, bookingId, onStatus);
      } else if (checkout.order) {
        // En Culqi Checkout Custom, cuando el usuario ve el QR de la orden generada
        if (onStatus) onStatus("Código QR Culqi en pantalla. Escanéalo desde tu app Yape o Plin. El pago se confirmará automáticamente.", "info");
      } else {
        if (onStatus) onStatus("Ventana de pago cerrada o cancelada por el usuario.", "info");
      }
    } catch (err) {
      if (onStatus) onStatus("Error al procesar cargo: " + err.message, "error");
    }
  };
}

async function prepararCobroCulqi(bookingId, checkoutToken, mode, channel, onStatus) {
  detenerSondeoQr();
  if (onStatus) {
    const msg = channel === "wallet_qr" ? "Generando orden QR dinámica con Culqi Sandbox..." : "Preparando pago seguro con Culqi...";
    onStatus(msg, "info");
  }

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

  return data;
}

function inicializarCulqi3DS(publicKey) {
  // Pre-generar fingerprint de dispositivo y configurar Culqi3DS
  if (typeof Culqi3DS !== "undefined") {
    Culqi3DS.publicKey = publicKey;
    Culqi3DS.options = { showModal: true, showLoading: true, showDirect: true };
  }
}

async function abrirPagoSigetour({ bookingId, checkoutToken, email, mode = "full", channel = "charge", onStatus = null, onQrState = null }) {
  const data = await prepararCobroCulqi(bookingId, checkoutToken, mode, channel, onStatus);

  // Idempotencia: si ya estaba acreditado previamente
  if (data.status === "paid") {
    if (onStatus) onStatus("La reserva ya cuenta con un pago verificado. Redirigiendo al voucher...", "success");
    setTimeout(() => {
      window.location.href = `/Booking/Voucher/${bookingId}`;
    }, 800);
    return { attemptId: data.attemptId, status: "paid" };
  }

  if (typeof CulqiCheckout === "undefined") {
    const msg = "La pasarela de pago Culqi no pudo cargarse. Verifica tu conexión a internet y recarga la página.";
    if (onStatus) onStatus(msg, "error");
    throw new Error(msg);
  }

  inicializarCulqi3DS(data.publicKey);
  await getDeviceFingerprint();

  currentBookingContext = {
    attemptId: data.attemptId,
    bookingId,
    checkoutToken,
    onStatus
  };

  const methods = channel === "wallet_qr"
    ? { tarjeta: false, yape: true, billetera: true, bancaMovil: false, agente: false, cuotealo: false }
    : { tarjeta: true, yape: true, billetera: false, bancaMovil: false, agente: false, cuotealo: false };

  const settings = { title: "Turismo Chilón - SIGETOUR", currency: "PEN", amount: data.amountCents };
  if (data.orderId) settings.order = data.orderId;

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

  // Si es canal wallet_qr, iniciar sondeo automático cada 3.5 segundos en paralelo
  if (channel === "wallet_qr") {
    handleQrStateInit(data, onQrState);
    startQrPolling(data, bookingId, checkoutToken, checkout, onStatus, onQrState);
  }

  checkout.culqi = createCheckoutHandler(checkout, data.attemptId, checkoutToken, bookingId, onStatus);
  checkout.open();

  return { attemptId: data.attemptId, orderId: data.orderId };
}

function handleRevisarStatus(data, bookingId, onStatus) {
  if (data.status === "paid") {
    if (onStatus) onStatus("¡Pago confirmado y acreditado! Redirigiendo al voucher...", "success");
    const targetBookingId = data?.bookingId || bookingId;
    if (targetBookingId) {
      setTimeout(() => {
        window.location.href = `/Booking/Voucher/${targetBookingId}`;
      }, 800);
    }
    return;
  }
  if (data.status === "verification" || data.status === "processing") {
    if (onStatus) onStatus(`El pago está en proceso de verificación ("${data.status}"). Si ya abonó en Yape o banco, espere unos segundos y vuelva a pulsar consultar.`, "warning");
    return;
  }
  if (data.status === "failed") {
    if (onStatus) onStatus("El último intento fue rechazado o caducó. Puedes realizar un nuevo intento con total seguridad.", "info");
    return;
  }
  if (onStatus) onStatus(`Estado del intento: ${data.status}.`, "info");
}

// Para verificar sin generar cargos nuevamente (recuperación de estado al recargar)
async function revisarPagoSigetour({ attemptId = null, checkoutToken, bookingId = null, onStatus = null }) {
  let resolvedAttempt = attemptId;
  if (!resolvedAttempt && bookingId) {
    try {
      resolvedAttempt = sessionStorage.getItem("culqi_attempt_" + bookingId) || lastCulqiAttempt;
    } catch (e) {
      resolvedAttempt = lastCulqiAttempt;
    }
  }

  if (onStatus) onStatus("Consultando estado de pago en el servidor...", "info");
  const res = await fetch("/api/culqi/check", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      attemptId: resolvedAttempt || "00000000-0000-0000-0000-000000000000",
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

  handleRevisarStatus(data, bookingId, onStatus);
  return data;
}
