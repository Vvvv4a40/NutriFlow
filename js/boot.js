performance.mark('boot-script');
const defaultCulture = new URLSearchParams(location.search).has('default-culture');
Blazor.start(defaultCulture ? {} : { applicationCulture: 'ru-RU' });
if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('service-worker.js', { updateViaCache: 'none' });
}
