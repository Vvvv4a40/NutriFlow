// native modal dialogs: focus stays inside and the page behind is inert; escape becomes the cancel button,
// and a tap on the dimmed page cancels an action sheet as it does on android
export function open(dialog, host) {
    if (!dialog || dialog.open) return;
    dialog.addEventListener("cancel", event => {
        event.preventDefault();
        host.invokeMethodAsync("CancelAsync");
    });
    if (dialog.classList.contains("nf-dialog--sheet")) {
        dialog.addEventListener("click", event => {
            if (event.target === dialog && isOutside(dialog.getBoundingClientRect(), event)) host.invokeMethodAsync("CancelAsync");
        });
    }

    dialog.showModal();
}

export function close(dialog) {
    if (dialog?.open) dialog.close();
}

// the backdrop belongs to the dialog element, so a tap on it arrives as a click on the dialog outside its box
export function isOutside(box, event) {
    return event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom;
}
