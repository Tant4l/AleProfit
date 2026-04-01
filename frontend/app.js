const Config = {
  API_BASE_URL: "aleprofit-functionapp-f3fqgwbzavheg4ad.westeurope-01.azurewebsites.net/api",
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
}

function loadView(viewId) {
  State.currentView = viewId;
  document.querySelectorAll(".view-section").forEach((section) => {
    section.style.display = section.id === viewId ? "block" : "none";
  });

  if (viewId === "master-view") renderMasterData();
}

function handleConnectAllegro() {
  const redirectUri = encodeURIComponent(
    "http://localhost:7071/api/AllegroAuthCallback",
  );
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
    const taxRate = document.getElementById("tax-rate-dropdown")?.value || 19.0;
    const url = `${Config.API_BASE_URL}/GetDashboardSummary?clientId=${Config.CLIENT_ID}&startDate=${State.startDate}&endDate=${State.endDate}&taxRate=${taxRate}`;
    const res = await fetch(url);
    if (!res.ok) throw new Error("API Error");
    const data = await res.json();

    const revenueNet = data.GrandTotalRevenueNet || 0;
    const totalCosts =
      (data.TotalCOGS || 0) +
      (data.TotalPackaging || 0) +
      (data.TotalCourierCosts || 0) +
      (data.TotalAllegroCommissions || 0);
    const totalTax = data.EstimatedIncomeTax || 0;
    const pureProfit = data.PureProfitAfterTax || 0;

    document.getElementById("dash-revenue").innerText =
      formatCurrency(revenueNet);
    document.getElementById("dash-costs").innerText =
      formatCurrency(totalCosts);
    document.getElementById("dash-tax").innerText = formatCurrency(totalTax);

    const profitEl = document.getElementById("dash-profit");
    profitEl.innerText = formatCurrency(pureProfit);
    profitEl.className =
      pureProfit >= 0
        ? "mb-0 text-success fw-bold"
        : "mb-0 text-danger fw-bold";
  } catch (err) {
    console.error("Dashboard Load Error:", err);
    showError("Nie udało się pobrać danych pulpitu.");
  } finally {
    toggleLoader(false);
  }
}

async function fetchLedger() {
  const tbody = document.getElementById("ledger-tbody");
  tbody.innerHTML = `<tr><td colspan="6" class="text-center py-3"><div class="spinner-border text-primary-accent spinner-border-sm"></div> Fetching ledger...</td></tr>`;

  try {
    const url = `${Config.API_BASE_URL}/GetOrderDetails?clientId=${Config.CLIENT_ID}&startDate=${State.startDate}&endDate=${State.endDate}`;
    const res = await fetch(url);
    if (!res.ok) throw new Error("API Error");
    const data = await res.json();

    State.ordersCache = data;
    tbody.innerHTML = "";

    if (data.length === 0) {
      tbody.innerHTML = `<tr><td colspan="6" class="text-center text-muted py-4">No transactions found for this period.</td></tr>`;
      return;
    }

    data.forEach((order, index) => {
      const totalCostsNet =
        order.TotalCogsNet +
        order.TotalPackagingNet +
        order.CommissionsNet +
        order.CourierCostsNet;
      const estTax = order.IncomeBeforeTax * 0.19;
      const pureProfit = order.IncomeBeforeTax - estTax;

      const isCancelled = order.InternalStatus === 'CANCELLED';
      const statusBadge = `<span class="badge ${isCancelled ? 'bg-danger' : 'bg-success'} ms-2">${order.InternalStatus}</span>`;
      const rowClass = isCancelled ? 'opacity-50 text-decoration-line-through' : '';

      const tr = document.createElement("tr");
      tr.className = rowClass;
      tr.onclick = () => showOrderModal(index);
      tr.innerHTML = `
                <td>${formatDate(order.OrderDatePL)}</td>
                <td class="font-monospace">
                    ${order.AllegroOrderId.substring(0, 8)}...
                    ${statusBadge}
                    <br><small class="text-white opacity-75">${order.ProductSummary.substring(0, 50)}...</small>
                </td>
                <td class="text-end">${formatCurrency(order.RevenueGross)}</td>
                <td class="text-end text-danger">-${formatCurrency(totalCostsNet)}</td>
                <td class="text-end text-danger">-${formatCurrency(estTax)}</td>
                <td class="text-end fw-bold ${pureProfit >= 0 ? "text-success" : "text-danger"}">${formatCurrency(pureProfit)}</td>
            `;
      tbody.appendChild(tr);
    });
  } catch (err) {
    console.error("Ledger Fetch Error:", err);
    tbody.innerHTML = `<tr><td colspan="6" class="text-center text-danger py-4">Error loading ledger data. Check console for details.</td></tr>`;
  }
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
  const originalText = btn.innerText;

  try {
    btn.disabled = true;

    btn.innerHTML = `<span class="spinner-border spinner-border-sm"></span> Syncing Orders...`;
    let res = await fetch(
      `${Config.API_BASE_URL}/SyncAllegroOrders?clientId=${Config.CLIENT_ID}`,
      { method: "POST" },
    );
    if (!res.ok) throw new Error("Order Sync Failed");

    btn.innerHTML = `<span class="spinner-border spinner-border-sm"></span> Syncing Ledger...`;
    res = await fetch(
      `${Config.API_BASE_URL}/SyncAllegroBilling?clientId=${Config.CLIENT_ID}`,
      { method: "POST" },
    );
    if (!res.ok) throw new Error("Billing Sync Failed");

    btn.innerText = "Sync Complete!";
    btn.classList.replace("btn-primary-accent", "btn-success");

    setTimeout(() => {
      btn.innerText = originalText;
      btn.classList.replace("btn-success", "btn-primary-accent");
      btn.disabled = false;
      fetchDashboard();
      fetchLedger();
    }, 2000);
  } catch (err) {
    btn.innerText = "Sync Failed!";
    btn.classList.replace("btn-primary-accent", "btn-danger");
    setTimeout(() => {
      btn.innerText = originalText;
      btn.classList.replace("btn-danger", "btn-primary-accent");
      btn.disabled = false;
    }, 3000);
  }
}

async function renderMasterData() {
  const tbody = document.getElementById("master-tbody");
  tbody.innerHTML = `<tr><td colspan="4" class="text-center py-3"><div class="spinner-border spinner-border-sm"></div> Loading offers...</td></tr>`;

  try {
    const res = await fetch(
      `${Config.API_BASE_URL}/GetOffers?clientId=${Config.CLIENT_ID}`,
    );
    const offers = await res.json();
    tbody.innerHTML = "";

    if (offers.length === 0) {
      tbody.innerHTML = `<tr><td colspan="4" class="text-center text-muted py-4">No offers found. Run a Data Sync first.</td></tr>`;
      return;
    }

    offers.forEach((offer) => {
      const tr = document.createElement("tr");
      tr.innerHTML = `
        <td class="font-monospace text-primary-accent">${offer.offerId}<br><small class="text-white">${offer.name.substring(0, 45)}...</small></td>
        <td>
            <div class="input-group input-group-sm">
                <input type="number" id="cogs-${offer.offerId}" class="form-control bg-dark text-light border-secondary" value="${offer.cogs}" step="0.01" min="0">
            </div>
        </td>
        <td>
            <div class="input-group input-group-sm">
                <input type="number" id="pkg-${offer.offerId}" class="form-control bg-dark text-light border-secondary" value="${offer.pkg}" step="0.01" min="0">
            </div>
        </td>
        <td>
            <select id="vat-${offer.offerId}" class="form-select form-select-sm bg-dark text-light border-secondary">
                <option value="23.00" ${offer.vat === 23 ? 'selected' : ''}>23%</option>
                <option value="8.00" ${offer.vat === 8 ? 'selected' : ''}>8%</option>
                <option value="5.00" ${offer.vat === 5 ? 'selected' : ''}>5%</option>
                <option value="0.00" ${offer.vat === 0 ? 'selected' : ''}>0% (ZW/Exempt)</option>
            </select>
        </td>
        <td class="text-end">
            <button class="btn btn-sm btn-outline-primary px-3" onclick="saveOfferCosts(event, '${offer.offerId}')">Save</button>
        </td>
    `;
      tbody.appendChild(tr);
    });
  } catch (err) {
    tbody.innerHTML = `<tr><td colspan="4" class="text-center text-danger py-4">Failed to load master data.</td></tr>`;
  }
}

async function saveOfferCosts(event, offerId) {
  const btn = event.target;
  const cogs = parseFloat(document.getElementById(`cogs-${offerId}`).value) || 0;
  const pkg = parseFloat(document.getElementById(`pkg-${offerId}`).value) || 0;
  const vatRate = parseFloat(document.getElementById(`vat-${offerId}`).value) || 23.0;

  try {
    const res = await fetch(`${Config.API_BASE_URL}/UpdateOfferCosts`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ clientId: Config.CLIENT_ID, offerId, cogs, pkg, vatRate }), // Added vatRate
    });

    if (res.ok) {
      btn.innerText = "Saved!";
      btn.className = "btn btn-sm btn-success px-3";
      setTimeout(() => {
        btn.innerText = "Save";
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
      btn.innerText = "Allegro Połączone";
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
