export function applyTheme(id, variables, themeColor, flatNavigation, dark = false,
    root = document.documentElement, meta = document.querySelector("meta[name=theme-color]")) {
    for (const [name, value] of Object.entries(variables)) root.style.setProperty(name, value);
    root.dataset.theme = id;
    // the bottom navigation in the page colour gets a hairline; a coloured one stands out by itself
    if (flatNavigation) root.dataset.flatNav = "";
    else delete root.dataset.flatNav;
    // native controls (slider tracks, the date picker, scroll bars) take the light or dark look of the theme
    root.style.colorScheme = dark ? "dark" : "light";
    meta?.setAttribute("content", themeColor);
}
