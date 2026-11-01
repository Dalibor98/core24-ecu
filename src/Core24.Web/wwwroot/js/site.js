// Small behaviours for the app pages (not the landing page).
// Lives in a file, not in onsubmit="..." attributes: the Content-Security-Policy blocks inline scripts.

// <form data-confirm="Are you sure?">: ask before submitting.
document.addEventListener('submit', (e) => {
  const question = e.target.dataset?.confirm;
  if (question && !confirm(question)) e.preventDefault();
});
