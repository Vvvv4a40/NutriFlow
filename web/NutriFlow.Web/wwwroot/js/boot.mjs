import { chooseStartupScreen } from "./startup.mjs";

const screen = await chooseStartupScreen();
if (screen === "app") {
    await Blazor.start({ applicationCulture: "ru-RU" });
    if ("serviceWorker" in navigator) {
        navigator.serviceWorker.register("service-worker.js", { updateViaCache: "none" });
    }
} else {
    showStartupScreen(screen);
}

function showStartupScreen(name) {
    const template = document.getElementById(`startup-${name}`);
    const app = document.getElementById("app");
    app.replaceChildren(template.content.cloneNode(true));
    app.querySelector("[data-action=reload]")?.addEventListener("click", () => location.reload());
}
