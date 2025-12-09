//let lastData = null;

//function showLoading() {
//    document.getElementById("loadingOverlay").style.display = "flex";
//}
//function hideLoading() {
//    document.getElementById("loadingOverlay").style.display = "none";
//}

//// ================= LOAD =================
//async function loadResults() {
//    const auditId = document.getElementById("auditInput").value.trim();
//    const tubType = document.getElementById("tubTypeSelect").value;

//    if (!auditId) {
//        alert("Enter Audit ID");
//        return;
//    }

//    showLoading();

//    try {
//        const res = await fetch(`./api/CFACal/RunFullCalculation?auditId=${auditId}&TubType=${tubType}`);
//        const data = await res.json();
//        if (!res.ok) throw new Error("Load failed");

//        lastData = data;
//        document.getElementById("productText").innerText = data.vDataProduct;

//        renderVisualCheck(data);

//    } catch (e) {
//        alert(e.message);
//    } finally {
//        hideLoading();
//    }
//}

//// ================= RENDER =================
//function renderVisualCheck(data) {
//    const table = document.getElementById("visualCheckTable");
//    table.innerHTML = "";

//    const header = table.insertRow();
//    ["P/N", "Description", "Result", "Comment"].forEach(h => {
//        const th = header.insertCell();
//        th.textContent = h;
//        th.style.fontWeight = "bold";
//        th.style.textAlign = "left";
//    });

//    data.vVisualChecks.forEach((item, index) => {
//        const row = table.insertRow();
//        row.dataset.part = item.part;
//        row.dataset.status = "F";

//        row.insertCell().textContent = item.part ?? "";
//        row.insertCell().textContent = item.description ?? "";

//        const resultCell = row.insertCell();
//        const commentCell = row.insertCell();

//        if (item.class === "TS_CFATEST") {
//            const chk = document.createElement("input");
//            chk.type = "checkbox";
//            chk.checked = item.result?.startsWith("OK");

//            const comment = document.createElement("input");
//            comment.type = "text";
//            comment.className = "vc-comment";
//            comment.value = item.comment ?? "";
//            //comment.disabled = !chk.checked;

//            chk.addEventListener("change", () => updateVisual(chk, comment, row, item));
//            comment.addEventListener("input", () => updateVisual(chk, comment, row, item));

//            resultCell.appendChild(chk);
//            commentCell.appendChild(comment);

//            updateVisual(chk, comment, row, item);
//        } else {
//            resultCell.textContent = "-";
//            commentCell.textContent = "-";
//        }
//    });
//}

//function updateVisual(chk, comment, row, item) {
//    if (chk.checked) {
//        item.result = comment.value
//            ? `OK : '${comment.value}'`
//            : "OK";
//        row.dataset.status = "P";
//    } else {
//        item.result = comment.value
//            ? `NG : '${comment.value}'`
//            : "NG";
//        row.dataset.status = "F";
//    }
//}

//// ================= SAVE =================
//document.getElementById("saveVisualBtn").addEventListener("click", async () => {
//    if (!lastData) {
//        alert("No data");
//        return;
//    }

//    const visualCheckData = lastData.vVisualChecks.map(x => ({
//        PartNo: x.part,
//        ResultValue: x.result,
//        tstStatus: x.result?.startsWith("OK") ? "P" : "F"
//    }));

//    const payload = {
//        vAutoResults: [],
//        vVisualResults: visualCheckData,
//        PartProduct: lastData.vDataProduct
//    };

//    const res = await fetch("api/CFACal/SaveResultTest", {
//        method: "POST",
//        headers: { "Content-Type": "application/json" },
//        body: JSON.stringify(payload)
//    });

//    if (res.ok) alert("Visual Check submitted ✅");
//    else alert("Submit failed ❌");
//});
