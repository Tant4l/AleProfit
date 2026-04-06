const Config = {
  API_BASE_URL:
    "https://aleprofit-functionapp-f3fqgwbzavheg4ad.westeurope-01.azurewebsites.net/api",
  CLIENT_ID:
    new URLSearchParams(window.location.search).get("clientId") ||
    localStorage.getItem("activeClientId"),
  ALLEGRO_APP_ID: "32267e2cb34d44399652f70c156e0615",
};

function showError(message, type = "danger") {
  const toastEl = document.getElementById("liveToast");
  const toastBody = document.getElementById("toast-body");

  toastEl.classList.remove("bg-danger", "bg-warning", "bg-success");
  toastEl.classList.add(`bg-${type}`);
  toastBody.innerText = message;

  const toast = new bootstrap.Toast(toastEl, { delay: 5000 });
  toast.show();
}

const State = {
  currentView: "dashboard-view",
  startDate: "",
  endDate: "",
  ordersCache: [],
  offersCache: new Set(),
  chartInstance: null,
};

document.addEventListener("DOMContentLoaded", () => {
  const urlParams = new URLSearchParams(window.location.search);
  const clientIdParam = urlParams.get("clientId");

  if (clientIdParam) {
    localStorage.setItem("activeClientId", clientIdParam);
    Config.CLIENT_ID = clientIdParam;
  } else {
    Config.CLIENT_ID = localStorage.getItem("activeClientId");
  }

  if (!Config.CLIENT_ID) {
    showError(
      "Sesja wygasła. Proszę wejść przez link z identyfikatorem klienta.",
      "warning",
    );
    return;
  }
  initDates();
  bindEvents();
  checkConnectionStatus();
  fetchDashboard();
  fetchLedger();
});

function initDates() {
  const now = new Date();
  const lastDay = new Date(now.getFullYear(), now.getMonth() + 1, 0);

  const formatDateInput = (dateObj) => {
    const y = dateObj.getFullYear();
    const m = String(dateObj.getMonth() + 1).padStart(2, "0");
    const d = String(dateObj.getDate()).padStart(2, "0");
    return `${y}-${m}-${d}`;
  };

  const startInput = document.getElementById("filter-start");
  const endInput = document.getElementById("filter-end");

  startInput.value = "2026-03-01";
  endInput.value = formatDateInput(lastDay);

  State.startDate = startInput.value;
  State.endDate = endInput.value;
}

function bindEvents() {
  document.querySelectorAll(".nav-link").forEach((link) => {
    link.addEventListener("click", (e) => {
      e.preventDefault();
      document
        .querySelectorAll(".nav-link")
        .forEach((l) => l.classList.remove("active"));
      e.currentTarget.classList.add("active");

      const targetView = e.currentTarget.getAttribute("data-target");
      loadView(targetView);
    });
  });

  document.getElementById("btn-refresh").addEventListener("click", () => {
    State.startDate = document.getElementById("filter-start").value;
    State.endDate = document.getElementById("filter-end").value;
    fetchDashboard();
    fetchLedger();
  });

  document.getElementById("btn-sync").addEventListener("click", handleSync);

  document
    .getElementById("btn-connect-allegro")
    .addEventListener("click", handleConnectAllegro);

  document.getElementById("menu-toggle").addEventListener("click", (e) => {
    e.preventDefault();
    document.getElementById("wrapper").classList.toggle("toggled");
  });

  document.getElementById("ledger-search").addEventListener("input", (e) => {
    renderLedger(e.target.value.toLowerCase());
  });
}

function loadView(viewId) {
  State.currentView = viewId;
  document.querySelectorAll(".view-section").forEach((section) => {
    section.style.display = section.id === viewId ? "block" : "none";
  });

  if (viewId === "master-view") renderMasterData();
}

function handleConnectAllegro() {
  const liveCallback =
    "https://aleprofit-functionapp-f3fqgwbzavheg4ad.westeurope-01.azurewebsites.net/api/AllegroAuthCallback";
  const redirectUri = encodeURIComponent(liveCallback);
  const oauthUrl = `https://allegro.pl.allegrosandbox.pl/auth/oauth/authorize?response_type=code&client_id=${Config.ALLEGRO_APP_ID}&redirect_uri=${redirectUri}&state=${Config.CLIENT_ID}`;
  window.location.href = oauthUrl;
}

const formatCurrency = (amount) => {
  return new Intl.NumberFormat("pl-PL", {
    style: "currency",
    currency: "PLN",
  }).format(amount || 0);
};

const formatDate = (dateString) => {
  if (!dateString) return "";
  return new Intl.DateTimeFormat("pl-PL", {
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  }).format(new Date(dateString));
};

async function fetchDashboard() {
  toggleLoader(true);
  try {
    const taxRate = document.getElementById("tax-rate-dropdown")?.value || 23.9;
    const url = `${Config.API_BASE_URL}/GetDashboardSummary?clientId=${Config.CLIENT_ID}&startDate=${State.startDate}&endDate=${State.endDate}&taxRate=${taxRate}`;
    const res = await fetch(url);
    if (!res.ok) throw new Error("API Error");
    const data = await res.json();

    const revenueNet = data.GrandTotalRevenueNet || 0;
    const cogs = data.TotalCOGS || 0;
    const packaging = data.TotalPackaging || 0;
    const courier = data.TotalCourierCosts || 0;
    const commissions = data.TotalAllegroCommissions || 0;

    const totalCosts = cogs + packaging + courier + commissions;
    const totalTax = data.EstimatedIncomeTax || 0;
    const pureProfit = data.PureProfitAfterTax || 0;

    // KPI Cards
    document.getElementById("dash-revenue").innerText =
      formatCurrency(revenueNet);
    document.getElementById("dash-costs").innerText =
      formatCurrency(totalCosts);
    document.getElementById("dash-tax").innerText = formatCurrency(totalTax);

    const margin = revenueNet > 0 ? (pureProfit / revenueNet) * 100 : 0;
    document.getElementById("dash-margin").innerText = margin.toFixed(1) + "%";

    const roi = cogs > 0 ? (pureProfit / cogs) * 100 : 0;
    document.getElementById("dash-roi").innerText = Math.round(roi) + "%";

    const profitEl = document.getElementById("dash-profit");
    profitEl.innerText = formatCurrency(pureProfit);
    profitEl.className =
      pureProfit >= 0
        ? "mb-0 text-success fw-bold"
        : "mb-0 text-danger fw-bold";

    updateDashboardCharts({
      revenue: revenueNet,
      cogs,
      packaging,
      courier,
      commissions,
      tax: totalTax,
      profit: pureProfit,
    });
  } catch (err) {
    console.error("Dashboard Load Error:", err);
    showError("Nie udało się pobrać danych pulpitu.");
  } finally {
    toggleLoader(false);
  }
}

function updateDashboardCharts(data) {
  updateFinancialStructureChart(data);
  updateCostDistributionChart(data);
}

function updateFinancialStructureChart(data) {
  const ctx = document.getElementById("profitabilityChart").getContext("2d");

  if (State.chartInstance) {
    State.chartInstance.destroy();
  }

  State.chartInstance = new Chart(ctx, {
    type: "bar",
    data: {
      labels: [
        "Przychód",
        "Koszt Towaru",
        "Prowizje",
        "Logistyka",
        "Podatek",
        "Zysk Netto",
      ],
      datasets: [
        {
          label: "Kwota (PLN)",
          data: [
            data.revenue,
            -data.cogs,
            -data.commissions,
            -(data.packaging + data.courier),
            -data.tax,
            data.profit,
          ],
          backgroundColor: [
            "rgba(59, 130, 246, 0.8)", // Blue (Revenue)
            "rgba(239, 68, 68, 0.8)", // Red (COGS)
            "rgba(249, 115, 22, 0.8)", // Orange (Commissions)
            "rgba(139, 92, 246, 0.8)", // Purple (Logistics)
            "rgba(245, 158, 11, 0.8)", // Amber (Tax)
            "rgba(16, 185, 129, 0.8)", // Green (Profit)
          ],
          borderRadius: 6,
          borderWidth: 0,
          barThickness: 45,
        },
      ],
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      plugins: {
        legend: { display: false },
        tooltip: {
          callbacks: {
            label: (context) => " " + formatCurrency(Math.abs(context.raw)),
          },
        },
      },
      scales: {
        x: {
          grid: { display: false },
          ticks: { color: "#94a3b8", font: { weight: "500" } },
        },
        y: {
          grid: { color: "rgba(148, 163, 184, 0.1)" },
          ticks: {
            color: "#94a3b8",
            callback: (value) => formatCurrency(value).replace(",00", ""),
          },
        },
      },
    },
  });
}

function updateCostDistributionChart(data) {
  const canvas = document.getElementById("costDistributionChart");
  const ctx = canvas.getContext("2d");

  // Check if there is an existing chart on this canvas and destroy it
  const existingChart = Chart.getChart(canvas);
  if (existingChart) {
    existingChart.destroy();
  }

  new Chart(ctx, {
    type: "doughnut",
    data: {
      labels: ["Towar", "Prowizje", "Logistyka", "Podatek"],
      datasets: [
        {
          data: [
            data.cogs,
            data.commissions,
            data.packaging + data.courier,
            data.tax,
          ],
          backgroundColor: [
            "rgba(239, 68, 68, 0.8)", // Red
            "rgba(249, 115, 22, 0.8)", // Orange
            "rgba(139, 92, 246, 0.8)", // Purple
            "rgba(245, 158, 11, 0.8)", // Amber
          ],
          borderColor: "rgba(26, 29, 33, 1)",
          borderWidth: 4,
          hoverOffset: 15,
        },
      ],
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      cutout: "70%",
      plugins: {
        legend: {
          position: "bottom",
          labels: {
            color: "#94a3b8",
            padding: 20,
            usePointStyle: true,
            font: { size: 12, weight: "500" },
          },
        },
        tooltip: {
          callbacks: {
            label: (context) => {
              const label = context.label || "";
              const value = context.raw || 0;
              const total = context.dataset.data.reduce((a, b) => a + b, 0);
              const percentage = ((value / total) * 100).toFixed(1);
              return ` ${label}: ${formatCurrency(value)} (${percentage}%)`;
            },
          },
        },
      },
    },
  });
}
async function fetchLedger() {
  const tbody = document.getElementById("ledger-tbody");
  tbody.innerHTML = `<tr><td colspan="6" class="text-center py-5"><div class="spinner-border text-primary-accent spinner-border-sm"></div> <span class="ms-2">Pobieranie rejestru...</span></td></tr>`;

  try {
    const taxRate = document.getElementById("tax-rate-dropdown")?.value || 23.9;
    const url = `${Config.API_BASE_URL}/GetOrderDetails?clientId=${Config.CLIENT_ID}&startDate=${State.startDate}&endDate=${State.endDate}&taxRate=${taxRate}`;
    const res = await fetch(url);
    if (!res.ok) throw new Error("API Error");
    const data = await res.json();

    State.ordersCache = data;
    renderLedger();
  } catch (err) {
    console.error("Ledger Fetch Error:", err);
    tbody.innerHTML = `<tr><td colspan="6" class="text-center text-danger py-5">Błąd podczas ładowania danych rejestru. Sprawdź konsolę.</td></tr>`;
  }
}

function renderLedger(filter = "") {
  const tbody = document.getElementById("ledger-tbody");
  tbody.innerHTML = "";

  const filtered = State.ordersCache.filter((order) => {
    return (
      order.AllegroOrderId.toLowerCase().includes(filter) ||
      order.ProductSummary.toLowerCase().includes(filter)
    );
  });

  if (filtered.length === 0) {
    tbody.innerHTML = `
      <tr>
        <td colspan="6" class="text-center text-muted py-5">
          <i class="bi bi-search fs-2 d-block mb-3 opacity-25"></i>
          Nie znaleziono transakcji pasujących do kryteriów.
        </td>
      </tr>`;
    return;
  }

  filtered.forEach((order) => {
    const totalCostsNet =
      order.TotalCogsNet +
      order.TotalPackagingNet +
      order.CommissionsNet +
      order.CourierCostsNet;

    const estTax = order.EstimatedTax;
    const pureProfit = order.PureProfitAfterTax;

    const isCancelled = order.InternalStatus === "CANCELLED";
    const statusBadge = `<span class="badge ${isCancelled ? "bg-danger" : "bg-success"} ms-2">${isCancelled ? "ANULOWANE" : "ZREALIZOWANE"}</span>`;
    const rowClass = isCancelled
      ? "opacity-50 text-decoration-line-through"
      : "";

    const tr = document.createElement("tr");
    tr.className = rowClass;
    tr.innerHTML = `
                <td class="ps-4" onclick="showOrderModalByOrderId('${order.AllegroOrderId}')">${formatDate(order.OrderDatePL)}</td>
                <td class="font-monospace">
                    <span class="text-primary-accent">${order.AllegroOrderId.substring(0, 8)}...</span>
                    <button class="btn btn-link btn-sm text-muted p-0 ms-1" onclick="copyToClipboard('${order.AllegroOrderId}')">
                        <i class="bi bi-clipboard small"></i>
                    </button>
                    ${statusBadge}
                    <br><small class="text-muted" onclick="showOrderModalByOrderId('${order.AllegroOrderId}')">${order.ProductSummary.substring(0, 50)}...</small>
                </td>
                <td class="text-end" onclick="showOrderModalByOrderId('${order.AllegroOrderId}')">${formatCurrency(order.RevenueGross)}</td>
                <td class="text-end text-danger" onclick="showOrderModalByOrderId('${order.AllegroOrderId}')">-${formatCurrency(totalCostsNet)}</td>
                <td class="text-end text-danger" onclick="showOrderModalByOrderId('${order.AllegroOrderId}')">-${formatCurrency(estTax)}</td>
                <td class="text-end pe-4 fw-bold ${pureProfit >= 0 ? "text-success" : "text-danger"}" onclick="showOrderModalByOrderId('${order.AllegroOrderId}')">${formatCurrency(pureProfit)}</td>
            `;
    tbody.appendChild(tr);
  });
}

function showOrderModalByOrderId(orderId) {
  const orderIndex = State.ordersCache.findIndex(
    (o) => o.AllegroOrderId === orderId,
  );
  if (orderIndex !== -1) showOrderModal(orderIndex);
}

function copyToClipboard(text) {
  navigator.clipboard.writeText(text).then(() => {
    showError(
      "Skopiowano do schowka: " + text.substring(0, 8) + "...",
      "success",
    );
  });
}

function showOrderModal(orderIndex) {
  const order = State.ordersCache[orderIndex];
  if (!order) return;

  document.getElementById("modal-order-id").innerText = order.AllegroOrderId;
  document.getElementById("modal-json-payload").innerText = JSON.stringify(
    order,
    null,
    4,
  );

  new bootstrap.Modal(document.getElementById("orderModal")).show();
}

async function handleSync() {
  const btn = document.getElementById("btn-sync");
  const originalHTML = btn.innerHTML;

  try {
    btn.disabled = true;

    btn.innerHTML = `<span class="spinner-border spinner-border-sm"></span> Sync zamówień...`;
    let res = await fetch(
      `${Config.API_BASE_URL}/SyncAllegroOrders?clientId=${Config.CLIENT_ID}`,
      { method: "POST" },
    );
    if (!res.ok) throw new Error("Order Sync Failed");

    btn.innerHTML = `<span class="spinner-border spinner-border-sm"></span> Sync bilingów...`;
    res = await fetch(
      `${Config.API_BASE_URL}/SyncAllegroBilling?clientId=${Config.CLIENT_ID}`,
      { method: "POST" },
    );
    if (!res.ok) throw new Error("Billing Sync Failed");

    btn.innerHTML = `<i class="bi bi-check-lg me-1"></i> Zakończono!`;
    btn.classList.replace("btn-primary-accent", "btn-success");

    setTimeout(() => {
      btn.innerHTML = originalHTML;
      btn.classList.replace("btn-success", "btn-primary-accent");
      btn.disabled = false;
      fetchDashboard();
      fetchLedger();
    }, 2000);
  } catch (err) {
    btn.innerHTML = `<i class="bi bi-exclamation-triangle me-1"></i> Błąd!`;
    btn.classList.replace("btn-primary-accent", "btn-danger");
    setTimeout(() => {
      btn.innerHTML = originalHTML;
      btn.classList.replace("btn-danger", "btn-primary-accent");
      btn.disabled = false;
    }, 3000);
  }
}

async function renderMasterData() {
  const tbody = document.getElementById("master-tbody");
  tbody.innerHTML = `<tr><td colspan="5" class="text-center py-5"><div class="spinner-border spinner-border-sm"></div> <span class="ms-2">Ładowanie ofert...</span></td></tr>`;

  try {
    const res = await fetch(
      `${Config.API_BASE_URL}/GetOffers?clientId=${Config.CLIENT_ID}`,
    );
    const offers = await res.json();
    tbody.innerHTML = "";

    if (offers.length === 0) {
      tbody.innerHTML = `
        <tr>
          <td colspan="5" class="text-center text-muted py-5">
            <i class="bi bi-box-seam fs-2 d-block mb-3 opacity-25"></i>
            Nie znaleziono ofert. Uruchom najpierw <strong class="text-white">Synchronizację danych</strong>.
          </td>
        </tr>`;
      return;
    }

    offers.forEach((offer) => {
      const tr = document.createElement("tr");
      tr.innerHTML = `
        <td class="ps-3 font-monospace text-primary-accent">${offer.offerId}<br><small class="text-muted">${offer.name.substring(0, 45)}...</small></td>
        <td>
            <div class="input-group input-group-sm">
                <input type="number" id="cogs-${offer.offerId}" class="form-control bg-dark text-light border-secondary" value="${offer.cogs}" step="0.01" min="0">
                <span class="input-group-text bg-surface border-secondary text-muted">PLN</span>
            </div>
        </td>
        <td>
            <div class="input-group input-group-sm">
                <input type="number" id="pkg-${offer.offerId}" class="form-control bg-dark text-light border-secondary" value="${offer.pkg}" step="0.01" min="0">
                <span class="input-group-text bg-surface border-secondary text-muted">PLN</span>
            </div>
        </td>
        <td>
            <select id="vat-${offer.offerId}" class="form-select form-select-sm bg-dark text-light border-secondary">
                <option value="23.00" ${offer.vat === 23 ? "selected" : ""}>23%</option>
                <option value="8.00" ${offer.vat === 8 ? "selected" : ""}>8%</option>
                <option value="5.00" ${offer.vat === 5 ? "selected" : ""}>5%</option>
                <option value="0.00" ${offer.vat === 0 ? "selected" : ""}>0% (ZW)</option>
            </select>
        </td>
        <td class="text-end pe-3">
            <button class="btn btn-sm btn-outline-primary px-3" onclick="saveOfferCosts(event, '${offer.offerId}')">
                <i class="bi bi-save me-1"></i>Zapisz
            </button>
        </td>
    `;
      tbody.appendChild(tr);
    });
  } catch (err) {
    tbody.innerHTML = `<tr><td colspan="5" class="text-center text-danger py-5">Błąd podczas ładowania danych ofert.</td></tr>`;
  }
}

async function saveOfferCosts(event, offerId) {
  const btn = event.currentTarget;
  const originalHTML = btn.innerHTML;

  const cogs =
    parseFloat(document.getElementById(`cogs-${offerId}`).value) || 0;
  const pkg = parseFloat(document.getElementById(`pkg-${offerId}`).value) || 0;
  const vatRate =
    parseFloat(document.getElementById(`vat-${offerId}`).value) || 23.0;

  try {
    const res = await fetch(`${Config.API_BASE_URL}/UpdateOfferCosts`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        clientId: Config.CLIENT_ID,
        offerId,
        cogs,
        pkg,
        vatRate,
      }),
    });

    if (res.ok) {
      btn.innerHTML = `<i class="bi bi-check-lg me-1"></i> Zapisano!`;
      btn.className = "btn btn-sm btn-success px-3";
      setTimeout(() => {
        btn.innerHTML = originalHTML;
        btn.className = "btn btn-sm btn-outline-primary px-3";
      }, 2000);
    } else {
      showError("Błąd podczas zapisywania kosztów.");
    }
  } catch (err) {
    showError("Błąd połączenia z API.");
  }
}

async function checkConnectionStatus() {
  try {
    const res = await fetch(
      `${Config.API_BASE_URL}/GetConnectionStatus?clientId=${Config.CLIENT_ID}`,
    );
    const data = await res.json();
    const btn = document.getElementById("btn-connect-allegro");

    if (data.connected) {
      btn.innerHTML = `<i class="bi bi-check-circle-fill me-1"></i> Allegro Połączone`;
      btn.classList.replace("btn-allegro", "btn-outline-success");
      btn.disabled = true;
    }
  } catch (err) {
    console.error("Status Check Failed", err);
  }
}

function toggleLoader(show) {
  document.getElementById("loader").style.display = show ? "flex" : "none";
}
