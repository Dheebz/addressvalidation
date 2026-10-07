// Add a stable route back to the application without changing Swagger's generated UI.
(() => {
  function addHomeLink() {
    const header = document.querySelector('.swagger-ui .topbar .wrapper');
    if (!header) return false;
    if (document.getElementById('addressdesk-home')) return true;
    const brand = document.createElement('a');
    brand.className = 'addressdesk-wordmark';
    brand.href = '/';
    brand.setAttribute('aria-label', 'AddressDesk home');
    brand.append(document.createTextNode('Address'));
    const accent = document.createElement('span');
    accent.textContent = 'Desk';
    brand.append(accent);
    header.prepend(brand);
    const link = document.createElement('a');
    link.id = 'addressdesk-home';
    link.href = '/';
    link.textContent = '← Back to AddressDesk';
    header.append(link);
    return true;
  }
  // Swagger mounts its React header after DOMContentLoaded on some loads.
  if (addHomeLink()) return;
  const observer = new MutationObserver(() => {
    if (addHomeLink()) observer.disconnect();
  });
  observer.observe(document.documentElement, { childList: true, subtree: true });
})();
