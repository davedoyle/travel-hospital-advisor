//admin script for Travel to Hospital Advisor – Admin page

const ADMIN_API_BASE = "http://localhost:5050"; 
let adminToken = null;

// bootstrap modal helper
function getBootstrapModal(elementId) {
    const el = document.getElementById(elementId);
    return bootstrap.Modal.getOrCreateInstance(el);
}

// password hashing
async function hashPassword(password) {
    const encoder = new TextEncoder();
    const data = encoder.encode(password);
    const hashBuffer = await crypto.subtle.digest("SHA-256", data);
    const hashArray = Array.from(new Uint8Array(hashBuffer));
    return hashArray.map(b => b.toString(16).padStart(2, "0")).join("");
}

// wrapper for authenticated calls
async function adminFetch(path, options = {}) {
    if (!adminToken) throw new Error("No admin token in memory.");

    const headers = options.headers || {};
    headers["x-admin-auth"] = adminToken;
    headers["Content-Type"] = headers["Content-Type"] || "application/json";

    const response = await fetch(ADMIN_API_BASE + path, {
        ...options,
        headers
    });

    if (response.status === 401) {
        alert("Session expired. Please log in again.");
        sessionStorage.removeItem("adminToken");
        adminToken = null;
        lockAdminUi();
        showLoginModal();
        throw new Error("Unauthorised");
    }

    return response;
}

// lock/unlock UI
function lockAdminUi() { document.body.classList.add("locked"); }
function unlockAdminUi() { document.body.classList.remove("locked"); }

// login modal
function showLoginModal() {
    getBootstrapModal("adminLoginModal").show();
}

// pick up saved token if present
function restoreTokenIfPresent() {
    const stored = sessionStorage.getItem("adminToken");
    if (stored) {
        adminToken = stored;
        unlockAdminUi();
    }
}


// ====================
// CARPARK HELPERS
// ====================

// open modal for adding
function openAddCarparkModal() {
    document.getElementById("carparkModalTitle").textContent = "Add Carpark";

    document.getElementById("carparkId").value = "";
    document.getElementById("carparkHeorg").value = "";
    document.getElementById("carparkName").value = "";
    document.getElementById("carparkCapacity").value = "";
    document.getElementById("carparkStatus").value = "OPEN";

    getBootstrapModal("modalCarparkEdit").show();
}

// open modal for editing
function openEditCarparkModal(cp) {
    document.getElementById("carparkModalTitle").textContent = "Edit Carpark";

    document.getElementById("carparkId").value = cp.carparkId;
    document.getElementById("carparkHeorg").value = cp.hospitalCode;
    document.getElementById("carparkName").value = cp.name;
    document.getElementById("carparkCapacity").value = cp.capacity;
    document.getElementById("carparkStatus").value = cp.status.toUpperCase();

    getBootstrapModal("modalCarparkEdit").show();
}

// archive carpark
async function archiveCarpark(id) {
    const ok = confirm("Archive this carpark?");
    if (!ok) return;

    try {
        const resp = await adminFetch(`/admin/carparks/${id}/archive`, { method: "PUT" });
        if (!resp.ok) {
            alert("Archive failed");
            return;
        }
        loadCarparksTable();
    } catch (err) {
        console.error("Archive error:", err);
        alert("Archive error");
    }
}

// save carpark from modal
async function saveCarparkForm() {
    const id = document.getElementById("carparkId").value.trim();
    const heorg = document.getElementById("carparkHeorg").value.trim();
    const name = document.getElementById("carparkName").value.trim();
    const capacity = parseInt(document.getElementById("carparkCapacity").value);
    const status = document.getElementById("carparkStatus").value.trim();

    const payload = {
        heorgId: heorg,
        carparkName: name,
        totalSpaces: capacity,
        status: status
    };

    try {
        let resp;

        if (id === "") {
            resp = await adminFetch("/admin/carparks", {
                method: "POST",
                body: JSON.stringify(payload)
            });
        } else {
            resp = await adminFetch(`/admin/carparks/${id}`, {
                method: "PUT",
                body: JSON.stringify(payload)
            });
        }

        if (!resp.ok) {
            alert("Save failed");
            return;
        }

        getBootstrapModal("modalCarparkEdit").hide();
        loadCarparksTable();

    } catch (err) {
        console.error("Save error:", err);
        alert("Save error");
    }
}

// reload table 
async function loadCarparksTable() {
    const tbody = document.getElementById("carparkTableBody");

    tbody.innerHTML = `
        <tr>
            <td colspan="6" class="text-center text-muted">Loading carparks...</td>
        </tr>
    `;

    try {
        const response = await adminFetch("/admin/carparks");
        const carparks = await response.json();

        if (!Array.isArray(carparks) || carparks.length === 0) {
            tbody.innerHTML = `
                <tr>
                    <td colspan="6" class="text-center text-muted">No carparks found.</td>
                </tr>
            `;
            return;
        }

        tbody.innerHTML = "";

        carparks.forEach(cp => {
            let statusBadge = "";
            const st = cp.status.toLowerCase();
            if (st === "open") statusBadge = `<span class="badge bg-success">Open</span>`;
            else if (st === "closed") statusBadge = `<span class="badge bg-danger">Closed</span>`;
            else statusBadge = `<span class="badge bg-secondary">${cp.status}</span>`;

            const hospitalText = cp.hospitalCode ? `HEORG ${cp.hospitalCode}` : "HEORG ?";

            const row = document.createElement("tr");
            row.innerHTML = `
                <td>${cp.name}</td>
                <td>${hospitalText}</td>
                <td>${cp.capacity}</td>
                <td>${statusBadge}</td>
                <td>${cp.used} / ${cp.capacity}</td>
                <td>
                    <button class="btn btn-sm btn-outline-primary btn-edit" data-id="${cp.carparkId}">
                        Edit
                    </button>
                    <button class="btn btn-sm btn-outline-danger btn-del" data-id="${cp.carparkId}">
                        Delete
                    </button>
                </td>
            `;

            tbody.appendChild(row);

            // attach edit / delete
            row.querySelector(".btn-edit").addEventListener("click", () => openEditCarparkModal(cp));
            row.querySelector(".btn-del").addEventListener("click", () => archiveCarpark(cp.carparkId));
        });

    } catch (err) {
        console.error("Error loading carparks:", err);

        tbody.innerHTML = `
            <tr>
                <td colspan="6" class="text-danger text-center">
                    Failed to load carparks. Please try again.
                </td>
            </tr>
        `;
    }
}

function addSimLog(text) {
    const box = document.getElementById("simLogBox");
    if (!box) return;

    const line = document.createElement("div");
    line.textContent = `${new Date().toLocaleTimeString()} - ${text}`;
    box.appendChild(line);
    box.scrollTop = box.scrollHeight;
}



// =====================================================
// MAIN PAGE BOOTSTRAP
// =====================================================

document.addEventListener("DOMContentLoaded", () => {

    const loginForm = document.getElementById("adminLoginForm");
    const loginError = document.getElementById("adminLoginError");

    restoreTokenIfPresent();

    if (!adminToken) {
        lockAdminUi();
        showLoginModal();
    }

    // login handler
    loginForm.addEventListener("submit", async (e) => {
        e.preventDefault();
        loginError.style.display = "none";

        const username = document.getElementById("adminUsername").value.trim();
        const password = document.getElementById("adminPassword").value;

        if (!username || !password) {
            loginError.textContent = "Please enter username and password.";
            loginError.style.display = "block";
            return;
        }

        try {
            const passwordHash = await hashPassword(password);

            const response = await fetch(ADMIN_API_BASE + "/admin/login", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    username: username,
                    passwordHash: passwordHash
                })
            });

            if (!response.ok) {
                loginError.textContent = "Login failed. Please check username and password.";
                loginError.style.display = "block";
                return;
            }

            const data = await response.json();
            adminToken = data.token;
            sessionStorage.setItem("adminToken", adminToken);

            getBootstrapModal("adminLoginModal").hide();
            unlockAdminUi();

        } catch (err) {
            console.error("Login error:", err);
            loginError.textContent = "Login failed due to a network or script problem.";
            loginError.style.display = "block";
        }
    });

    

    // ==========================
    // Simulation Handlers
    // ==========================
    async function simCommand(endpoint) {
        try {
            const res = await adminFetch(`/admin/sim/${endpoint}`, { method: "POST" });

            if (!res.ok) {
                addSimLog(`ERROR: ${endpoint}`);
                return;
            }

            const data = await res.json();
            addSimLog(data.message);
        } catch (err) {
            console.error("Simulation error:", err);
            addSimLog(`Failed: ${endpoint}`);
        }
    }

    document.getElementById("btnSimStart").addEventListener("click", () => simCommand("start"));
    document.getElementById("btnSimPause").addEventListener("click", () => simCommand("pause"));
    document.getElementById("btnSimTick").addEventListener("click", () => simCommand("tick"));
    document.getElementById("btnSimFastForward").addEventListener("click", () => simCommand("fastforward"));
    document.getElementById("btnResetSimulation").addEventListener("click", () => simCommand("reset"));


    // ==========================
    // Carparks modal - load table
    // ==========================
    document.getElementById("modalCarparks").addEventListener("show.bs.modal", loadCarparksTable);

    // ==========================
    // Add carpark button
    // ==========================
    document.getElementById("btnAddCarpark").addEventListener("click", openAddCarparkModal);

    // ==========================
    // Save button in modal
    // ==========================
    document.getElementById("carparkForm").addEventListener("submit", (e) => {
        e.preventDefault();
        saveCarparkForm();
    });

    // ==========================
    // load system info when the modal opens
    // ==========================
    document.getElementById("modalSystemInfo").addEventListener("show.bs.modal", async () => {
        try {
            const response = await adminFetch("/admin/system/info");
            const data = await response.json();

            // built-in admin-api data
            document.getElementById("sysDbPath").textContent = data.dbPath;
            document.getElementById("sysCarparks").textContent = data.carparkCount;
            document.getElementById("sysAdmins").textContent = data.adminUserCount;

            // launcher data
            const l = data.launcher;

            document.getElementById("sysMainDb").textContent = l.mainDb;
            document.getElementById("sysTfiDb").textContent = l.tfiDb;

            document.getElementById("sysWeather").textContent = l.weatherStatus;
            document.getElementById("sysTfi").textContent = l.tfiStatus;
            document.getElementById("sysSim").textContent = l.simStatus;

            document.getElementById("sysLastWeather").textContent = l.lastWeather;
            document.getElementById("sysLastTfi").textContent = l.lastTfi;

            document.getElementById("sysStart").textContent = l.startTimeUtc;

        } catch (err) {
            console.error("system info load error:", err);
        }
    });

    // ==========================
    // STATUS BADGE POLLING
    // ==========================
    function updateBadges() {
        fetch(LAUNCHER)
            .then(r => r.json())
            .then(data => {
                // TFI badge
                const badgeTfi = document.getElementById("badgeTfi");
                if (data.tfiStatus === "OK") {
                    badgeTfi.textContent = "TFI OK";
                    badgeTfi.className = "status-badge status-ok";
                } else {
                    badgeTfi.textContent = "TFI DOWN";
                    badgeTfi.className = "status-badge status-warn";
                }

                // Weather badge
                const badgeWeather = document.getElementById("badgeWeather");
                if (data.weatherStatus === "OK") {
                    badgeWeather.textContent = "Weather OK";
                    badgeWeather.className = "status-badge status-ok";
                } else {
                    badgeWeather.textContent = "Weather DOWN";
                    badgeWeather.className = "status-badge status-warn";
                }

                // Simulation badge
                const badgeSim = document.getElementById("badgeSim");
                const simStatus = data.simStatus ?? "Unknown";

                if (simStatus.includes("Running")) {
                    badgeSim.textContent = "Simulation Running";
                    badgeSim.className = "status-badge status-ok";
                } else {
                    badgeSim.textContent = "Simulation Paused";
                    badgeSim.className = "status-badge status-warn";
                }
            })
            .catch(() => {
                console.warn("Launcher check failed");

                // --- Force badges red on failure ---
                const badgeTfi = document.getElementById("badgeTfi");
                badgeTfi.textContent = "TFI DOWN";
                badgeTfi.className = "status-badge status-warn";

                const badgeWeather = document.getElementById("badgeWeather");
                badgeWeather.textContent = "Weather DOWN";
                badgeWeather.className = "status-badge status-warn";

                const badgeSim = document.getElementById("badgeSim");
                badgeSim.textContent = "Simulation DOWN";
                badgeSim.className = "status-badge status-warn";
            });
        }

    // start periodic badge updates
        setInterval(updateBadges, 3000);
        updateBadges();



});
