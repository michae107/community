import ReactDOM from "react-dom/client";
import App from "./App";
import "./index.css";

// No StrictMode: its dev-only double-mount would kick off two filesystem
// scans and two watchers.
ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <App />,
);
