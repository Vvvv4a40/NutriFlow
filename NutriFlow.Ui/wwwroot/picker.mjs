// opens the system date picker of a hidden <input type="date"> from the visible date button. It has to run within
// the tap, so the diary imports this module before the first tap; focus is the fallback where showPicker is missing
export function showPicker(input) {
    try {
        input.showPicker();
    } catch {
        input.focus();
    }
}
