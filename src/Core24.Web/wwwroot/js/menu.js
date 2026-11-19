// Account dropdown behaviour, shared by the app pages and the landing page.
(() => {
  const openMenus = () => document.querySelectorAll('details.menu[open]');

  // Close when clicking anywhere outside the menu, or pressing Escape.
  document.addEventListener('click', (e) => openMenus().forEach((m) => { if (!m.contains(e.target)) m.open = false; }));
  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape') return;
    openMenus().forEach((m) => { m.open = false; m.querySelector('summary').focus(); });
  });

  // The landing page is a static file: the server can't write "logged in as ..." into it.
  // So it asks /api/me and builds its menu here. App pages get theirs from _AccountMenu.cshtml.
  const panel = document.querySelector('details.menu[data-dynamic] .menu-panel');
  if (!panel) return;

  const link = (href, text, cls) => {
    const a = document.createElement('a');
    a.href = href;
    a.textContent = text;
    if (cls) a.className = cls;
    return a;
  };
  const el = (tag, cls, text) => {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text) e.textContent = text;
    return e;
  };

  fetch('/api/me', { credentials: 'same-origin', cache: 'no-store' })
    .then((r) => (r.ok ? r.json() : { signedIn: false }))
    .catch(() => ({ signedIn: false }))
    .then((me) => {
      if (!me.signedIn) return; // the HTML already contains the logged-out menu
      panel.replaceChildren();
      const who = el('p', 'menu-who', 'Logged in as');
      who.append(document.createElement('br'), el('b', null, me.email)); // textContent: an email can't inject HTML
      // The workshop works in the admin area; it has no orders of its own and doesn't send files.
      if (me.isAdmin) {
        // On the public landing page, the "Send a file" / "Upload a file" buttons are for customers.
        // For the workshop they become one clear way into the admin area.
        document.querySelectorAll('a[href="/order/new"]').forEach((a) => { a.href = '/admin'; a.textContent = 'Open admin'; });
        document.querySelectorAll('a[href="/account"]').forEach((a) => a.remove()); // "My orders": the workshop has none
        // Say it plainly: this page talks to customers, and you are not one.
        const bar = el('div', 'admin-bar', 'You are logged in as the workshop. This is the public site your customers see. ');
        bar.append(link('/admin', 'Go to admin'));
        document.body.append(bar);
        panel.append(who, el('p', 'menu-section', 'Workshop'), link('/admin', 'Orders'), link('/admin/price-list', 'Price list'));
      } else {
        panel.append(who, link('/account', 'My orders'), link('/order/new', 'Send a file'));
      }
      // A static page has no anti-forgery token, so this goes to a page with a real logout form.
      panel.append(el('hr'), link('/account/logout', 'Log out', 'danger'));
      document.querySelector('details.menu[data-dynamic] summary').append(Object.assign(el('i', 'dot'), { title: 'Logged in' }));
    });
})();
