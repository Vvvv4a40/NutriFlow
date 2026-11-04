import { captureInstallPrompt, startUpdates } from "./platform.mjs";
import { chooseStartupScreen, movedToAppKey } from "./startup.mjs";

captureInstallPrompt(window);
const screen = await chooseStartupScreen();
if (screen === "app") {
    await Blazor.start({ applicationCulture: "ru-RU" });
    await startUpdates();
} else {
    showStartupScreen(screen);
}

function showStartupScreen(name) {
    const template = document.getElementById(`startup-${name}`);
    const app = document.getElementById("app");
    app.replaceChildren(template.content.cloneNode(true));
    app.querySelector("[data-action=reload]")?.addEventListener("click", () => location.reload());
    app.querySelector("[data-action=open-here]")?.addEventListener("click", () => {
        sessionStorage.removeItem(movedToAppKey);
        location.reload();
    });
}
