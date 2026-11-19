// Small behaviours for the app pages (not the landing page).
// Lives in a file, not in onsubmit="..." attributes: the Content-Security-Policy blocks inline scripts.

// <form data-confirm="Are you sure?">: ask before submitting.
document.addEventListener('submit', (e) => {
  const question = e.target.dataset?.confirm;
  if (question && !confirm(question)) e.preventDefault();
});

// Order form: show only the parts that belong to the chosen kind of order (data-show-for="OriginalFile" ...).
const orderForm = document.querySelector('[data-order-form]');
if (orderForm) {
  const notListed = orderForm.querySelector('[data-not-listed-toggle]');
  const refresh = () => {
    const type = orderForm.querySelector('input[name="Input.Type"]:checked')?.value;
    orderForm.querySelectorAll('[data-show-for]').forEach((el) => { el.hidden = el.dataset.showFor !== type; });
    // A special request may be for a vehicle that isn't in the price list: then it's typed instead of picked.
    const typed = type === 'SpecialRequest' && notListed.checked;
    orderForm.querySelector('[data-picker]').hidden = typed;
    orderForm.querySelector('[data-typed-vehicle]').hidden = !typed;
  };
  orderForm.addEventListener('change', refresh);
  refresh();
}

// Vehicle picker: the page has ONE long <select> with every line of the price list (it works without JavaScript).
// This turns it into four short ones: brand -> model -> engine -> computer. Each list shows only what exists
// for the choices before it, and the long <select> (now hidden) still carries the answer to the server.
const source = document.querySelector('select[data-picker-source]');
if (source) {
  const levels = ['brand', 'model', 'engine', 'ecu'];
  const labels = { brand: 'Brand', model: 'Model', engine: 'Engine', ecu: 'Computer (ECU)' };
  const entries = [...source.options].filter((o) => o.value).map((o) => ({ id: o.value, ...o.dataset }));
  const steps = document.querySelector('[data-picker-steps]');
  const priceBox = document.querySelector('[data-picker-price]');

  const selects = levels.map((level) => {
    const field = document.createElement('div');
    const label = document.createElement('label');
    const select = document.createElement('select');
    field.className = 'field';
    select.id = `pick-${level}`;
    label.htmlFor = select.id;
    label.textContent = labels[level];
    field.append(label, select);
    steps.append(field);
    return select;
  });

  // The price list lines that agree with the first `count` choices.
  const matching = (count) => entries.filter((e) => levels.slice(0, count).every((level, i) => e[level] === selects[i].value));

  // Refill the lists from `from` to the end; everything after a changed choice starts again.
  const fill = (from) => {
    for (let i = from; i < levels.length; i++) {
      const ready = i === 0 || selects[i - 1].value !== '';
      const values = ready ? [...new Set(matching(i).map((e) => e[levels[i]]))] : [];
      selects[i].replaceChildren(new Option(ready ? 'Choose…' : '—', ''), ...values.map((v) => new Option(v, v)));
      selects[i].disabled = !ready;
      if (values.length === 1) selects[i].value = values[0]; // only one possibility: choose it for the customer
    }
    const done = selects.every((s) => s.value !== '');
    const entry = done ? matching(levels.length)[0] : null;
    source.value = entry ? entry.id : '';
    priceBox.hidden = !entry;
    priceBox.textContent = entry ? `Original file for this computer: ${entry.price}. You pay only when it's ready.` : '';
  };

  selects.forEach((select, i) => select.addEventListener('change', () => fill(i + 1)));

  // After a validation error the page comes back with the earlier choice: show it again.
  const chosen = entries.find((e) => e.id === source.value);
  fill(0);
  if (chosen) levels.forEach((level, i) => { selects[i].value = chosen[level]; fill(i + 1); });

  source.closest('.field').hidden = true;
  steps.hidden = false;
}
