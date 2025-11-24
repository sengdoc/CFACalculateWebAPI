let chartInstance = null;
let lastData = null;
let highlightedIndex = null;

// ----------------- UTILITIES -----------------
function safeArray(arr, index) { return Array.isArray(arr) && arr.length > index ? arr[index] : 0; }

function classMapping(className, data) {
    if (!data) return 0;
    const map = {
        "TS_CFA_INWT": data.vIncomingWaterTemperature ?? 0,
        "TS_CFA_FVFR": data.vFVFR ?? 0,
        "TS_CFA_FVOL": data.vFillVolume ?? 0,
        "TS_CFA_FT1": safeArray(data.vTimedFills, 0),
        "TS_CFA_FT2": safeArray(data.vTimedFills, 1),
        "TS_CFA_FT3": safeArray(data.vTimedFills, 2),
        "TS_CFA_FT4": safeArray(data.vTimedFills, 3),
        "TS_CFA_FT5": safeArray(data.vTimedFills, 4),
        "TS_CFA_FF1": safeArray(data.vFinalFills, 0),
        "TS_CFA_FF2": safeArray(data.vFinalFills, 1),
        "TS_CFA_FF3": safeArray(data.vFinalFills, 2),
        "TS_CFA_FF4": safeArray(data.vFinalFills, 3),
        "TS_CFA_FF5": safeArray(data.vFinalFills, 4),
        "TS_CFA_MWT": data.vMainWashTemp ?? 0,
        "TS_CFA_FNT": data.vFinalRinseTemp ?? 0,
        "TS_CFA_ENER": data.vEnergyKWh ?? 0,
        "TS_CFA_HEATUP": data.vHeatUpRateCPerS ?? 0,
        "TS_CFA_VOLT": data.vVoltage ?? 0,
        "TS_CFA_MWA": data.vMainWashAmperage ?? 0,
        "TS_CFA_FRA": data.vFinalRinseAmperage ?? 0,
        "TS_CFA_CYCLET": data.vCycleTime ?? 0
    };
    return map[className] ?? 0;
}

function showLoading() { document.getElementById("loadingOverlay").style.display = "flex"; }
function hideLoading() { document.getElementById("loadingOverlay").style.display = "none"; }

// ----------------- LOAD DATA -----------------
async function loadResults() {
    const id = document.getElementById("auditInput").value.trim();
    const tubType = document.getElementById("tubTypeSelect").value;
    if (!id) return alert("Enter Audit ID");

    showLoading();
    document.getElementById("loadBtn").disabled = true;

    try {
        const response = await fetch(`./api/CFACal/RunFullCalculation?auditId=${id}&TubType=${tubType}`);
        const data = await response.json();
        if (!response.ok) throw new Error(data?.message || `Server responded ${response.status}`);

        lastData = data;
        document.getElementById("productText").innerText = data.vDataProduct || "N/A";

        renderKPIs(data);
        renderTable(data);
        renderVisualCheck(data);
        renderChart(data);

        // Auto show Results tab after load
        openTab({ currentTarget: document.querySelector(".tab-button") }, 'ResultTableTab');

    } catch (err) {
        console.error(err);
        alert("Error loading data: " + err.message);
        document.getElementById("productText").innerText = "Error loading product info";
        lastData = null;
        document.getElementById("kpiContainer").innerHTML = "";
        document.getElementById("resultsTable").innerHTML = "";
        document.getElementById("visualCheckTable").innerHTML = "";
        if (chartInstance) chartInstance.destroy();
    } finally {
        hideLoading();
        document.getElementById("loadBtn").disabled = false;
    }
}

// ----------------- KPI -----------------
function renderKPIs(data) {
    if (!data) return;

    const partLimits = Array.isArray(data.vPartLimits) ? data.vPartLimits : [];
    const visualChecks = Array.isArray(data.vVisualChecks) ? data.vVisualChecks : [];
    const total = partLimits.length + visualChecks.length;

    const passPartLimits = partLimits.filter(x => {
        const act = classMapping(x.class, data);
        return x.lowerLimit <= act && act <= x.upperLimit;
    }).length;

    let passVisual = 0;
    visualChecks.forEach(x => {
        if (x.class !== 'TS_CFATEST') {
            const val = parseFloat(x.result);
            const lower = parseFloat(x.lowerLimit);
            const upper = parseFloat(x.upperLimit);
            if (!isNaN(val) && val >= lower && val <= upper) passVisual++;
        } else {
            if (x.result === "checked") passVisual++;
        }
    });

    const pass = passPartLimits + passVisual;
    const fail = total - pass;

    document.getElementById("kpiContainer").innerHTML = `
        <div class="kpi-row">
            <div class="kpi-box">Total Tests<div class="kpi-value">${total}</div></div>
            <div class="kpi-box">Passed<div class="kpi-value" style="color:green">${pass}</div></div>
            <div class="kpi-box">Failed<div class="kpi-value" style="color:red">${fail}</div></div>
        </div>
    `;
}

// ----------------- TABLES -----------------
function renderTable(data) {
    const table = document.getElementById("resultsTable");
    table.innerHTML = "";
    const header = table.insertRow();
    ["Part", "Class", "Description", "Lower", "Actual", "Upper"].forEach(h => header.insertCell().textContent = h);

    data.vPartLimits.forEach((item, index) => {
        const actual = classMapping(item.class, data);
        const row = table.insertRow();
        [item.part, item.class, item.description, item.lowerLimit, actual, item.upperLimit].forEach(v => row.insertCell().textContent = v ?? 0);
        row.style.background = (actual < item.lowerLimit || actual > item.upperLimit) ? "#f8d7da" : "#d4edda";
        row.addEventListener("click", () => highlightRow(index));
    });
}

function renderVisualCheck(data) {
    const table = document.getElementById("visualCheckTable");
    table.innerHTML = "";

    const header = table.insertRow();
    ["P/N", "Desc.", "Result"].forEach(h => {
        const th = header.insertCell();
        th.textContent = h;
        th.style.fontWeight = "bold";
        th.style.padding = "8px 12px";
        th.style.textAlign = "left";
    });

    data.vVisualChecks?.forEach((item, index) => {
        const row = table.insertRow();
        const pnCell = row.insertCell(); pnCell.textContent = item.part ?? ""; pnCell.style.padding = "6px 12px"; pnCell.style.textAlign = "left";
        const descCell = row.insertCell(); descCell.textContent = item.description ?? ""; descCell.style.padding = "6px 12px"; descCell.style.textAlign = "left";
        const resultCell = row.insertCell(); resultCell.style.padding = "6px 12px"; resultCell.style.textAlign = "left";

        if (item.class !== 'TS_CFATEST') {
            const wrapper = document.createElement("div");
            wrapper.style.display = "flex"; wrapper.style.alignItems = "center"; wrapper.style.gap = "8px";

            const input = document.createElement("input");
            input.type = "text"; input.style.width = "60%"; input.style.height = "32px"; input.style.fontSize = "16px"; input.style.padding = "4px";
            input.value = item.result ?? ""; input.dataset.index = index;
            input.addEventListener("input", (e) => updateVisualKPI(e.target, index));

            const limitText = document.createElement("span");
            limitText.style.fontSize = "14px"; limitText.style.color = "#555";
            limitText.textContent = `[${item.lowerLimit ?? "-"} - ${item.upperLimit ?? "-"}]`;

            wrapper.appendChild(input); wrapper.appendChild(limitText);
            resultCell.appendChild(wrapper);
        } else {
            const checkbox = document.createElement("input"); checkbox.type = "checkbox"; checkbox.style.width = "24px"; checkbox.style.height = "24px";
            checkbox.dataset.index = index; if (item.result === "checked") checkbox.checked = true;
            checkbox.addEventListener("change", (e) => updateVisualKPI(e.target, index));
            resultCell.appendChild(checkbox);
            resultCell.addEventListener("click", e => { if (e.target !== checkbox) checkbox.checked = !checkbox.checked; updateVisualKPI(checkbox, index); });
        }
    });

    table.style.borderCollapse = "collapse";
    table.querySelectorAll("td").forEach(td => td.style.border = "1px solid #ccc");
}

// ----------------- UPDATE VISUAL KPI -----------------
function updateVisualKPI(el, index) {
    if (!lastData) return;
    const item = lastData.vVisualChecks[index];
    if (item.class !== 'TS_CFATEST') {
        item.result = el.value;
        const val = parseFloat(el.value);
        const lower = parseFloat(item.lowerLimit);
        const upper = parseFloat(item.upperLimit);
        el.style.borderColor = (isNaN(val) || val < lower || val > upper) ? "red" : "#ccc";
    } else {
        item.result = el.checked ? "checked" : "";
    }
    renderKPIs(lastData);
}

// ----------------- CHART -----------------
function renderChart(data) {
    if (!data || !Array.isArray(data.vPartLimits)) return;
    const labels = data.vPartLimits.map(x => x.class);
    const actual = data.vPartLimits.map(x => classMapping(x.class, data));
    const lower = data.vPartLimits.map(x => x.lowerLimit);
    const upper = data.vPartLimits.map(x => x.upperLimit);

    if (chartInstance) chartInstance.destroy();
    const ctx = document.getElementById("limitChart");
    chartInstance = new Chart(ctx, {
        type: "bar",
        data: {
            labels,
            datasets: [
                { label: "Actual", data: actual, backgroundColor: actual.map((v, i) => v < lower[i] || v > upper[i] ? "#e74c3c" : "#3498db") },
                { label: "Lower Limit", data: lower, type: "line", borderColor: "green", fill: false },
                { label: "Upper Limit", data: upper, type: "line", borderColor: "red", fill: false }
            ]
        },
        options: {
            onClick: (e, elms) => { if (elms.length) highlightRow(elms[0].index); },
            plugins: { tooltip: { callbacks: { label: (ctx) => { const i = ctx.dataIndex; return `Actual: ${actual[i]}, Lower: ${lower[i]}, Upper: ${upper[i]}`; } } } }
        }
    });
}

// ----------------- HIGHLIGHT -----------------
function highlightRow(index) {
    highlightedIndex = index;
    const tableRows = document.getElementById("resultsTable").rows;
    for (let i = 1; i < tableRows.length; i++) tableRows[i].classList.toggle("highlight", i - 1 === index);
    updateChartColors();
}

function updateChartColors() {
    if (!chartInstance || !lastData) return;
    chartInstance.data.datasets[0].backgroundColor = lastData.vPartLimits.map((item, i) => {
        const actual = classMapping(item.class, lastData);
        const fail = actual < item.lowerLimit || actual > item.upperLimit;
        if (i === highlightedIndex) return "#f39c12";
        return fail ? "#e74c3c" : "#3498db";
    });
    chartInstance.update();
}

// ----------------- EXPORT -----------------
function exportTable(type) {
    if (!lastData) return;
    const ws = XLSX.utils.json_to_sheet(lastData.vPartLimits.map(x => ({
        Part: x.part, Class: x.class, Description: x.description,
        Lower: x.lowerLimit, Actual: classMapping(x.class, lastData), Upper: x.upperLimit
    })));
    const wb = XLSX.utils.book_new(); XLSX.utils.book_append_sheet(wb, ws, "Results");
    XLSX.writeFile(wb, `CFA_Results.${type}`);
}

function exportVisualCheck(type) {
    if (!lastData) return;
    const ws = XLSX.utils.json_to_sheet(lastData.vVisualChecks.map(x => ({
        Part: x.part, Description: x.description, Result: x.result
    })));
    const wb = XLSX.utils.book_new(); XLSX.utils.book_append_sheet(wb, ws, "VisualCheck");
    XLSX.writeFile(wb, `CFA_VisualCheck.${type}`);
}

function exportChart() {
    if (!chartInstance) return;
    const a = document.createElement('a');
    a.href = chartInstance.toBase64Image();
    a.download = 'CFA_Chart.png';
    a.click();
}

// ----------------- TAB & COLLAPSIBLE -----------------
function openTab(evt, tabName) {
    document.querySelectorAll(".tab-content").forEach(tab => tab.style.display = "none");
    document.querySelectorAll(".tab-button").forEach(btn => btn.classList.remove("active"));
    document.getElementById(tabName).style.display = "block";
    evt.currentTarget.classList.add("active");
}

document.querySelectorAll(".collapsible").forEach(btn => {
    btn.addEventListener("click", () => {
        btn.classList.toggle("active");
        const content = btn.nextElementSibling;
        content.style.display = (content.style.display === "block") ? "none" : "block";
    });
});

// ----------------- ENTER KEY -----------------
document.getElementById("auditInput").addEventListener("keydown", e => { if (e.key === "Enter") loadResults(); });

// ----------------- COPY PRODUCT INFO -----------------
function copyProductInfo() {
    if (!lastData) return;
    navigator.clipboard.writeText(lastData.vDataProduct || "").then(() => alert("Copied!"));
}
