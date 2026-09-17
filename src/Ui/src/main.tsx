/** Web UI 入口：在 index.html 的 #root 上创建 React 根节点并挂载 App。 */
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "./index.css";
import App from "./App";

// StrictMode 的双重渲染用于尽早暴露不纯的副作用；生产构建不受影响
createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>
);
