  let currentHospital = "CUH";

    // simple shared state so the advice box can look across all feeds
    let latestWeather = null;
    let latestBusStatus = null;
    let latestCarparkStatus = null;

    // === Fetch Weather ===
    async function loadWeather(hospitalCode) {
      try {
        const response = await fetch(`http://localhost:5028/api/weather/${hospitalCode}`);
        if (!response.ok) throw new Error("Weather API not responding");
        const data = await response.json();

        document.getElementById("weather-temp").textContent =
          `${data.temperature.toFixed(1)}°C`;
        document.getElementById("weather-cond").textContent = data.condition;
        document.getElementById("weather-uv").textContent =
          `UV Index: ${data.uv.toFixed(1)}`;
        document.getElementById("last-updated").textContent =
          `Last updated: ${new Date(data.timestamp).toLocaleString()}`;

        // basic flags for advice engine
        const cond = (data.condition || "").toLowerCase();
        const isWet =
          cond.includes("rain") ||
          cond.includes("shower") ||
          cond.includes("drizzle") ||
          cond.includes("snow");

        latestWeather = {
          temp: data.temperature,
          condition: data.condition,
          uv: data.uv,
          isWet: isWet
        };

        updateAdvice();
      } catch (err) {
        console.error("Error fetching weather:", err);
        document.getElementById("weather-temp").textContent = "--°C";
        document.getElementById("weather-cond").textContent = "Error fetching data";
      }
    }

    // === Fetch Bus Data ===
    async function loadBusTimes(hospitalCode) {
      try {
        const response = await fetch(`http://localhost:5030/api/bus/${hospitalCode}`);
        if (!response.ok) throw new Error("Bus API not responding");

        const data = await response.json();
        const buses = data.results || data.buses || [];
        const tbody = document.getElementById("bus-body");
        tbody.innerHTML = "";

        if (buses.length === 0) {
          tbody.innerHTML = "<tr><td colspan='6'>No upcoming buses found.</td></tr>";
          latestBusStatus = null;
          updateAdvice();
          return;
        }

        let delayedCount = 0;

        buses.forEach(bus => {
          const row = document.createElement("tr");

          const delayText = bus.delay_status || "";
          let delayColour = "text-success";
          const delayLower = delayText.toLowerCase();

          if (delayLower.includes("delay")) {
            delayedCount++;
            delayColour = "text-danger";
          } else if (delayLower.includes("early")) {
            delayColour = "text-warning";
          }

          row.innerHTML = `
            <td>${bus.route || ""}</td>
            <td>${bus.destination || ""}</td>
            <td>${bus.scheduled || ""}</td>
            <td>${bus.expected || ""}</td>
            <td class="${delayColour}">${delayText}</td>
            <td>${bus.live_hit || "N"}</td>
          `;

          tbody.appendChild(row);
        });

        const busStamp = document.getElementById("bus-last-updated");
        busStamp.textContent = `Last updated: ${new Date(data.timestamp).toLocaleTimeString()}`;

        // trigger green flash animation
        busStamp.classList.remove("bus-updated-flash");
        void busStamp.offsetWidth;  // restart animation trick
        busStamp.classList.add("bus-updated-flash");

        // summarise bus status for the advice box
        let level = "OK";
        const ratio = delayedCount / buses.length;

        if (delayedCount === 0) {
          level = "OK";
        } else if (ratio <= 0.33) {
          level = "SOME_DELAYS";
        } else {
          level = "HEAVY_DELAYS";
        }

        latestBusStatus = {
          total: buses.length,
          delayed: delayedCount,
          level: level
        };

        updateAdvice();

      } catch (err) {
        console.error("Error fetching bus times:", err);
        document.getElementById("bus-body").innerHTML =
          "<tr><td colspan='6'>Error fetching data</td></tr>";
      }
    }

    // === Switch Hospital ===
    function setHospital(hospitalCode) {
      currentHospital = hospitalCode;
      document.getElementById("selected-hospital").textContent = hospitalCode;
      document.getElementById("bus-panel-title").textContent =
        `Next Buses at ${hospitalCode}`;
      document.getElementById("carpark-status").innerHTML =
        "Loading car park data...";

      // flip background based on hospital
      document.body.classList.remove("cuh-bg", "sfh-bg", "default-bg");
      if (hospitalCode === "CUH") {
        document.body.classList.add("cuh-bg");
      } else if (hospitalCode === "SFH") {
        document.body.classList.add("sfh-bg");
      } else {
        document.body.classList.add("default-bg");
      }

      // kick off data loads
      loadWeather(hospitalCode);
      loadBusTimes(hospitalCode);
      loadCarparks(hospitalCode);
    }

    // === Fetch Car Park Data ===
    async function loadCarparks(hospitalCode) {
      const output = document.getElementById("carpark-status");
      output.innerHTML = "Loading car park data...";

      try {
        const response = await fetch(`http://localhost:5040/api/carparks/${hospitalCode}`);
        if (!response.ok) {
          throw new Error("Carpark API not responding");
        }

        const data = await response.json();
        const carparks = data.carparks || [];

        if (carparks.length === 0) {
          output.innerHTML = "No car park data available.";
          latestCarparkStatus = null;
          updateAdvice();
          return;
        }

        let html = "";
        let sumOccupied = 0;
        let sumTotal = 0;
        let maxRatio = 0;

        carparks.forEach(cp => {
          const total = cp.total ?? cp.Total ?? 0;
          const occupied = cp.occupied ?? cp.Occupied ?? 0;

          sumOccupied += occupied;
          sumTotal += total;
          const ratio = total > 0 ? occupied / total : 0;
          if (ratio > maxRatio) maxRatio = ratio;

          html += `${cp.name || cp.Name}: ${occupied}/${total}<br>`;
        });

        output.innerHTML = html;

        const avgRatio = sumTotal > 0 ? sumOccupied / sumTotal : 0;
        let pressure = "LOW";

        if (avgRatio > 0.85 || maxRatio > 0.9) {
          pressure = "HIGH";
        } else if (avgRatio > 0.6 || maxRatio > 0.75) {
          pressure = "MEDIUM";
        }

        latestCarparkStatus = {
          avgRatio: avgRatio,
          maxRatio: maxRatio,
          pressure: pressure
        };

        updateAdvice();

      } catch (err) {
        console.error("Error fetching car park data:", err);
        output.innerHTML = "Error fetching car park data.";
      }
    }

    // === Advice box logic ===
    function updateAdvice() {
      const box = document.getElementById("advice-box");
      if (!box) return;

      let text =
        "Travel conditions look normal. Give yourself a small buffer and travel in your usual way.";

      const hasWeather = latestWeather !== null;
      const hasBus = latestBusStatus !== null;
      const hasCarpark = latestCarparkStatus !== null;

      if (hasWeather && hasCarpark && latestWeather.isWet &&
          latestCarparkStatus.pressure === "HIGH") {
        text =
          "Heavy rain and very busy car parks are likely. Aim to arrive early and give yourself extra time to park.";
      } else if (hasBus && hasCarpark &&
                 latestBusStatus.level === "HEAVY_DELAYS" &&
                 latestCarparkStatus.pressure !== "LOW") {
        text =
          "Bus services are heavily delayed and parking is tight. Leave early and pick the option that you know best today.";
      } else if (hasBus && latestBusStatus.level === "HEAVY_DELAYS") {
        text =
          "Bus services are showing heavy delays. If you can, leave earlier than usual or use a different route.";
      } else if (hasCarpark && latestCarparkStatus.pressure === "HIGH") {
        text =
          "Car parks are close to full most of the time. Think about using bus, drop-off or park-and-ride where it suits.";
      } else if (hasWeather && latestWeather.isWet) {
        text =
          "Wet weather is forecast. Give yourself extra time for traffic and parking, and bring a good coat or umbrella.";
      } else if (hasWeather && hasCarpark &&
                 latestWeather.uv >= 5 &&
                 latestCarparkStatus.pressure === "LOW") {
        text =
          "Weather looks dry and bright with good parking. On a fine day like this, bus or park-and-ride are good options.";
      }

      box.textContent = text;
    }

    function showUpdateToast() {
      const box = document.getElementById("updateToast");
      const fill = document.getElementById("updateToastFill");

      // Reset the bar instantly
      fill.style.width = "0%";

      // Slide in
      box.classList.add("show");

      // Start filling animation (slight delay so CSS sees the reset)
      setTimeout(() => {
        fill.style.width = "100%";
      }, 50);

      // Slide out after 2 seconds
      setTimeout(() => {
        box.classList.remove("show");
      }, 2000);
    }


    // === Auto-load CUH on start ===
    window.onload = () => {
      setHospital("CUH");

      // Weather updates once per minute
      setInterval(() => {
        loadWeather(currentHospital);
      }, 60000);

      // Bus updates once per minute
      setInterval(() => {
        loadBusTimes(currentHospital);
      }, 60000);

      // Car park once per minute
      setInterval(() => {
        loadCarparks(currentHospital);
      }, 60000);

      // Re-evaluate advice once per minute using the latest snapshots
      setInterval(() => {
        updateAdvice();
      }, 60000);

      //show data update alert
      setInterval(() => {
        showUpdateToast();
      }, 60000); 
    };