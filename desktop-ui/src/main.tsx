import React from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import "./styles.css";
import "./premium-fidelity.css";
import "./mod-fidelity.css";
import "./cluster-gallery.css";
import "./update-fidelity.css";
import "./rcon-fidelity.css";
import "./travel-fidelity.css";
import "./approved-dashboard-fidelity.css";
import "./world-backup-fidelity.css";

const root = document.getElementById("root");
if (!root) throw new Error("Missing root");
createRoot(root).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
