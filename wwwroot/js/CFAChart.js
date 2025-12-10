let chartInstance = null;
let lastData = null;
let highlightedIndex = null;

// prettier colors -----------------
const colorFail = "#ffb3b3";      // pastel red
const colorPass = "#b9f2c3";      // pastel green
const colorSpecial = "#fafafa";   // pastel white
const colorHighlight = "#ffe08f"; // soft orange

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
        "TS_CFA_CYCLET": data.vCycleTime ?? 0,
        "TS_CFA_ADF": data.vAdditionalFills ?? 0
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
        document.getElementById("productText").innerText = data.vDataProduct + " : " + (data.vTobType || "N/A");

        renderKPIs(data);
        renderTable(data);
        renderVisualCheck(data);
        renderChart(data);

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

// ----------------- SAVE RESULT -----------------
document.getElementById('saveResultTestBtn').addEventListener('click', async function () {

    const resultRows = document.querySelectorAll('#resultsTable tbody tr');
    const resultData = [];

    resultRows.forEach((row) => {
        const partNo = row.getAttribute('data-part');
        const resultAct = row.getAttribute('data-actual');
        const tstStatus = row.getAttribute('data-status');
        resultData.push({
            PartNo: partNo,
            ResultValue: resultAct,
            tstStatus: tstStatus
        });
    });

    const visualCheckRows = document.querySelectorAll('#visualCheckTable tbody tr');
    const visualCheckData = [];

    visualCheckRows.forEach((row) => {
        const partNo = row.getAttribute('data-part');
        const resultAct = row.getAttribute('data-actual');
        const tstStatus = row.getAttribute('data-status');
        visualCheckData.push({
            PartNo: partNo,
            ResultValue: resultAct,
            tstStatus: tstStatus
        });
    });

    if (!resultData || resultData.length === 0) {
        alert("Result Data cannot be empty.");
        return;
    }
    if (!visualCheckData || visualCheckData.length === 0) {
        alert("Visual Check Data cannot be empty.");
        return;
    }

    for (let i = 1; i < resultData.length; i++) {
        const item = resultData[i];
        if (!item.PartNo || !item.ResultValue || !item.tstStatus) {
            alert(`Result Data is missing required fields at index ${i + 1}.`);
            return;
        }
    }

    for (let i = 1; i < visualCheckData.length; i++) {
        const item = visualCheckData[i];
        if (!item.PartNo || !item.ResultValue || !item.tstStatus) {
            alert(`Visual Check Data is missing required fields at index ${i + 1}.`);
            return;
        }
    }

    if (!lastData || !lastData.vDataProduct) {
        alert("Product data (PartProduct) cannot be empty.");
        return;
    }

    const dataToSave = {
        vAutoResults: resultData,
        vVisualResults: visualCheckData,
        PartProduct: lastData.vDataProduct
    };

    try {
        const response = await fetch('api/CFACal/SaveResultTest', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(dataToSave)
        });

        if (response.ok) {
            alert('Result saved successfully');
        } else {
            alert('Error saving result');
        }
    } catch (error) {
        console.error('Error:', error);
        alert('An error occurred while saving the result.');
    }
});

// ----------------- KPI -----------------
function renderKPIs(data) {
    if (!data) return;

    const partLimits = Array.isArray(data.vPartLimits) ? data.vPartLimits : [];
    const visualChecks = Array.isArray(data.vVisualChecks) ? data.vVisualChecks : [];
    const total = partLimits.length + visualChecks.length;

    const passPartLimits = partLimits.filter(x => {
        const act = classMapping(x.class, data);

        const skipClasses = ['TS_CFA_ENER', 'TS_CFA_CYCLET', 'TS_CFA_HEATUP', 'TS_CFA_ADF'];
        if (skipClasses.includes(x.class)) return true;

        return x.lowerLimit <= act && act <= x.upperLimit;
    }).length;

    let passVisual = 0;
    visualChecks.forEach(x => {
        if (x.class !== 'TS_CFATEST') {
            const val = parseFloat(x.result || 0);
            const lower = parseFloat(x.lowerLimit);
            const upper = parseFloat(x.upperLimit);
            if (!isNaN(val) && val >= lower && val <= upper) passVisual++;
        } else {
            if (x.result?.startsWith("OK")) passVisual++;


        }
    });

    const pass = passPartLimits + passVisual;
    const fail = total - pass;
    document.getElementById("kpiContainer").innerHTML = `
       
                             <div class="kpi-box">Total<div class="kpi-value">${total}</div></div>
                             <div class="kpi-box kpi-pass">Passed<div class="kpi-value" style="color:green">${pass}</div></div>
                              <div class="kpi-box kpi-fail">Failed<div class="kpi-value" style="color:red">${fail}</div></div>
       
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
        row.setAttribute('data-part', item.part);

        [item.part, item.class, item.description, item.lowerLimit, actual, item.upperLimit].forEach(v => {
            row.insertCell().textContent = v ?? 0;
        });

        row.setAttribute('data-actual', actual);

        if (actual < item.lowerLimit || actual > item.upperLimit)
            row.style.background = colorFail;
        else
            row.style.background = colorPass;

        row.setAttribute('data-status',
            (actual < item.lowerLimit || actual > item.upperLimit) ? "F" : "P"
        );

        const skipClasses = ['TS_CFA_ENER', 'TS_CFA_CYCLET', 'TS_CFA_HEATUP', 'TS_CFA_ADF'];
        if (skipClasses.includes(item.class)) {
            row.style.background = colorSpecial;
            row.setAttribute('data-status', "P");
        }

        row.addEventListener("click", () => highlightRow(index));
    });
}

function renderVisualCheck(data) {
    const table = document.getElementById("visualCheckTable");
    table.innerHTML = "";

    const header = table.insertRow();
    ["P/N", "Desc.", "Result", "Comment"].forEach(h => {
        const th = header.insertCell();
        th.textContent = h;
        th.style.fontWeight = "bold";
        th.style.padding = "8px 12px";
        th.style.textAlign = "left";
    });

    if (!data || !Array.isArray(data.vVisualChecks)) return;

    data.vVisualChecks.forEach((item, index) => {
        const row = table.insertRow();
        row.setAttribute('data-part', item.part ?? "");
        row.setAttribute('data-status', "F");

        // P/N
        const pnCell = row.insertCell();
        pnCell.textContent = item.part ?? "";
        pnCell.style.padding = "6px 12px";
        pnCell.style.textAlign = "left";

        // Desc
        const descCell = row.insertCell();
        descCell.textContent = item.description ?? "";
        descCell.style.padding = "6px 12px";
        descCell.style.textAlign = "left";

        // Result
        const resultCell = row.insertCell();
        resultCell.style.padding = "6px 12px";
        resultCell.style.textAlign = "left";

        // Comment
        const commentCell = row.insertCell();
        commentCell.style.padding = "6px 12px";
        commentCell.style.textAlign = "left";

        if (item.class === 'TS_CFATEST') {
            // Checkbox
            const checkbox = document.createElement("input");
            checkbox.type = "checkbox";
            checkbox.style.width = "18px";
            checkbox.style.height = "18px";
            checkbox.dataset.index = index;
            checkbox.checked = item.result?.startsWith("OK");

            // Comment input (always enabled)
            const commentInput = document.createElement("input");
            commentInput.type = "text";
            commentInput.className = "vc-comment";
            commentInput.placeholder = "Key comment here";
            commentInput.value = item.comment ?? "";

            // Event: checkbox change
            checkbox.addEventListener("change", (e) => {
                updateVisualKPI(checkbox, index);
            });

            // Event: comment input
            commentInput.addEventListener("input", (e) => {
                item.comment = e.target.value;
                updateVisualKPI(checkbox, index);
            });

            // Clicking result cell toggles checkbox
            resultCell.addEventListener("click", (e) => {
                if (e.target !== checkbox) {
                    checkbox.checked = !checkbox.checked;
                    checkbox.dispatchEvent(new Event('change', { bubbles: true }));
                }
            });

            resultCell.appendChild(checkbox);
            commentCell.appendChild(commentInput);

            // Set initial row attributes
            row.setAttribute('data-actual', checkbox.checked ? "OK" : "NG");
            row.setAttribute('data-status', checkbox.checked ? "P" : "F");

        } else {
            // Other numeric/text result
            const input = document.createElement("input");
            input.type = "text";
            input.style.width = "60%";
            input.style.height = "32px";
            input.style.fontSize = "16px";
            input.style.padding = "4px";
            input.value = item.result ?? "";
            input.dataset.index = index;

            input.addEventListener("input", (e) => updateVisualKPI(e.target, index));

            const limitText = document.createElement("span");
            limitText.style.fontSize = "14px";
            limitText.style.color = "#555";
            limitText.textContent = `[${item.lowerLimit ?? "-"} - ${item.upperLimit ?? "-"}]`;

            const wrapper = document.createElement("div");
            wrapper.style.display = "flex";
            wrapper.style.alignItems = "center";
            wrapper.style.gap = "8px";
            wrapper.appendChild(input);
            wrapper.appendChild(limitText);
            resultCell.appendChild(wrapper);

            commentCell.textContent = "-";
            row.setAttribute('data-actual', input.value);
        }
    });

    table.style.borderCollapse = "collapse";
    table.querySelectorAll("td").forEach(td => td.style.border = "1px solid #ccc");
}

// ----------------- UPDATE VISUAL KPI -----------------
function updateVisualKPI(el, index) {
    if (!lastData) return;

    const item = lastData.vVisualChecks[index];
    const row = el.closest("tr");
    let val = el.value?.trim() || "";

    if (item.class === 'TS_CFATEST') {
        const checkbox = row.querySelector("input[type='checkbox']");
        const comment = item.comment?.trim() || "";

        if (checkbox.checked) {
            item.result = comment.length > 0 ? `OK : '${comment}'` : "OK";
            row.setAttribute('data-status', "P");
        } else {
            item.result = comment.length > 0 ? `NG : '${comment}'` : "NG";
            row.setAttribute('data-status', "F");
        }

    } else {
        const numVal = parseFloat(val);
        const lower = parseFloat(item.lowerLimit);
        const upper = parseFloat(item.upperLimit);

        if (!isNaN(numVal) && numVal >= lower && numVal <= upper) {
            row.setAttribute('data-status', "P");
        } else {
            row.setAttribute('data-status', "F");
        }
        item.result = val;
    }

    row.setAttribute('data-actual', item.result);
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
    for (let i = 1; i < tableRows.length; i++) {
        const isSelected = i - 1 === index;
        tableRows[i].classList.toggle("highlight", isSelected);

        if (isSelected)
            tableRows[i].style.background = colorHighlight;
        else {
            const item = lastData.vPartLimits[i - 1];
            const actual = classMapping(item.class, lastData);

            const skipClasses = ['TS_CFA_ENER', 'TS_CFA_CYCLET', 'TS_CFA_HEATUP', 'TS_CFA_ADF'];

            if (skipClasses.includes(item.class)) {
                tableRows[i].style.background = colorSpecial;
            } else {
                if (actual < item.lowerLimit || actual > item.upperLimit)
                    tableRows[i].style.background = colorFail;
                else
                    tableRows[i].style.background = colorPass;
            }
        }
    }
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

// ----------------- TABS -----------------
function openTab(evt, tabName) {
    const tabs = document.querySelectorAll(".tab-content");
    tabs.forEach(t => t.style.display = "none");
    document.getElementById(tabName).style.display = "block";

    const buttons = document.querySelectorAll(".tab-button");
    buttons.forEach(btn => btn.classList.remove("active"));
    evt.currentTarget.classList.add("active");
}


function copyProductInfo() {
    if (!lastData || !lastData.vDataProduct) return;

    const parts = lastData.vDataProduct.split(" ");
    const productCode = parts[1] || "";

    if (navigator.clipboard && navigator.clipboard.writeText) {
        // Modern way
        navigator.clipboard.writeText(productCode)
            .catch(err => console.error("Failed to copy text:", err));
    } else {
        // Fallback for older browsers
        const textarea = document.createElement("textarea");
        textarea.value = productCode;
        document.body.appendChild(textarea);
        textarea.select();
        try {
            document.execCommand("copy");
        } catch (err) {
            console.error("Fallback: unable to copy", err);
        }
        document.body.removeChild(textarea);
    }
}