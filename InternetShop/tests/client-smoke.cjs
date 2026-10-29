// Node-only smoke tests. Executes the real app.js with a minimal DOM and mocked HTTP.
// It verifies request behavior, not browser rendering or the .NET server.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { randomUUID } = require('node:crypto');

class Element {
  constructor() {
    this.value = ''; this.disabled = false; this.handlers = new Map();
    this.children = []; this.dataset = {}; this.style = { setProperty() {} };
    this.classList = { add() {}, remove() {}, toggle() {} };
    this.className = ''; this.innerHTML = ''; this.scrollHeight = 0;
  }
  set textContent(value) { this.text = String(value); }
  get textContent() { return this.text || ''; }
  addEventListener(type, handler) { this.handlers.set(type, handler); }
  append(...elements) { this.children.push(...elements); }
  replaceChildren(...elements) { this.children = elements; }
  setAttribute() {}
  scrollIntoView() {}
}

let stock = 1;
const orders = [];
const requests = [];
let hold = false;
const releases = [];
let offline = false;
let failNext = false;
const product = () => ({ id: 17, sku: 'LAB2-RACE-17', name: 'Lab 2: last unit', price: 1, stockQuantity: stock });

async function mockFetch(address, options) {
  const serverReceived = new Date().toISOString();
  const pathname = new URL(address).pathname;
  requests.push({ pathname, ...options });
  if (offline) throw new Error('Connection refused');
  let status = 200;
  let data;
  if (pathname === '/api/operations/GetProducts') {
    if (hold) await new Promise((resolve, reject) => {
      releases.push(resolve);
      options.signal.addEventListener('abort', () => reject(new Error('Aborted')), { once: true });
    });
    if (failNext) { failNext = false; status = 500; data = { title: 'Internal server error' }; }
    else data = [product()];
  } else if (pathname === '/api/time') data = new Date().toISOString();
  else if (pathname === '/health') data = { status: 'Healthy' };
  else if (pathname === '/api/bootstrap') data = { customerId: 1, customerName: 'Demo Customer', customerEmail: 'demo@internetshop.local' };
  else if (pathname === '/api/products') data = [product()];
  else if (pathname === '/api/orders') data = orders;
  else if (pathname === '/api/lab/prepare-race') { stock = 1; data = { customerId: 1, product: product() }; }
  else if (['CreateOrder', 'RaceCreateOrder', 'TransactionCreateOrder', 'SyncCreateOrder', 'SemaphoreCreateOrder'].some(mode => pathname === `/api/operations/${mode}`)) {
    const payload = JSON.parse(options.body);
    assert.equal(payload.customerId, 1);
    assert.deepEqual(payload.items, [{ productId: 17, quantity: 1 }]);
    if (pathname !== '/api/operations/RaceCreateOrder' && stock === 0) {
      return { status: 400, ok: false, headers: { get: () => null },
        text: async () => JSON.stringify({ title: 'Not enough stock' }) };
    }
    stock = 0;
    data = { id: orders.length + 1, customerId: 1, status: 'Pending', total: 1,
      createdAt: new Date().toISOString(), paidAt: null, cancelledAt: null,
      items: [{ productId: 17, productName: 'Lab 2: last unit', quantity: 1, unitPrice: 1, lineTotal: 1 }], payment: null };
    orders.push(data);
  } else if (pathname === '/api/lab/products/17/state') {
    data = { productId: 17, stockQuantity: stock, orderedQuantity: orders.length, orderIds: orders.map(x => x.id) };
  } else { status = 404; data = { title: 'Object not found' }; }
  return { status, ok: status < 400, headers: { get: name => ({ 'X-Request-Id': 'client-smoke-request',
      'X-Server-Received-Time': serverReceived, 'X-Server-Time': new Date().toISOString() })[name] || null },
    text: async () => JSON.stringify(data) };
}

function createClient(id, offset = 0) {
  const elements = new Map();
  const get = selector => {
    if (!elements.has(selector)) elements.set(selector, new Element());
    return elements.get(selector);
  };
  const sandbox = {
    document: { querySelector: get, createElement: () => new Element() },
    window: { addEventListener() {} }, location: { search: `?client=${id}&offset=${offset}` },
    localStorage: { getItem: () => null, setItem() {} }, crypto: { randomUUID },
    fetch: mockFetch, AbortController, URL, URLSearchParams, Intl,
    console: { error() {} },
    setTimeout: (callback, ms) => { const timer = setTimeout(callback, ms); timer.unref(); return timer; },
    clearTimeout
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../client/app.js'), 'utf8'), sandbox);
  return { get, click: selector => {
    const node = get(selector);
    return node.handlers.get('click')({ currentTarget: node });
  } };
}

async function until(condition) {
  const timeout = Date.now() + 3000;
  while (!condition()) {
    if (Date.now() > timeout) throw new Error('Timed out waiting for the client');
    await new Promise(resolve => setTimeout(resolve, 5));
  }
}

(async () => {
  const first = createClient(1, 120);
  const second = createClient(2);
  await until(() => first.get('#catalog-count').textContent === '1' && second.get('#catalog-count').textContent === '1');
  assert.equal(first.get('#client-label').textContent, 'Client 1');
  assert.equal(second.get('#client-label').textContent, 'Client 2');

  await first.click('#lab-sync-time');
  const clock = first.get('#lab-clock').textContent;
  const correction = Number(clock.match(/Saved correction: ([-\d.]+) seconds/)[1]);
  assert.ok(Math.abs(correction + 120) < 0.05, clock);
  assert.match(clock, /Corrected client time:/);
  assert.match(clock, /Estimated one-way network delay:/);
  assert.ok(requests.some(x => x.pathname === '/api/time'));
  console.log('PASS: time sync endpoint, simulated +120 second client offset and corrected display');

  hold = true;
  const a = first.click('#lab-parallel');
  const b = second.click('#lab-parallel');
  await until(() => releases.length === 2);
  assert.equal(first.get('#lab-parallel').disabled, true);
  assert.equal(second.get('#lab-parallel').disabled, true);
  const sent = requests.filter(x => x.pathname === '/api/operations/GetProducts');
  assert.deepEqual(sent.map(x => x.headers['X-Client-Id']).sort(), ['1', '2']);
  hold = false;
  releases.splice(0).forEach(resolve => resolve());
  await Promise.all([a, b]);
  assert.equal(first.get('#lab-parallel').disabled, false);
  assert.equal(second.get('#lab-parallel').disabled, false);
  console.log('PASS: two independent client IDs, simultaneous pending requests, response handling');

  await first.click('#lab-prepare');
  assert.equal(first.get('#lab-product-id').value, 17);
  second.get('#lab-product-id').value = '17';
  await first.click('#lab-order');
  await second.click('#lab-order');
  await first.click('#lab-state');
  assert.match(first.get('#lab-log').textContent, /StockQuantity=0 OrderedQuantity=2 Orders=\[1, 2\]/);
  assert.equal(requests.filter(x => x.pathname === '/api/operations/RaceCreateOrder').length, 2);
  console.log('PASS: preparation, order payloads and shared-resource result display (mock API)');

  for (const mode of ['TransactionCreateOrder', 'SyncCreateOrder', 'SemaphoreCreateOrder']) {
    await first.click('#lab-prepare');
    first.get('#lab-order-mode').value = mode;
    await first.click('#lab-order');
    await first.click('#lab-order');
    assert.match(first.get('#lab-log').textContent, /HTTP 400/);
    assert.equal(first.get('#lab-order').disabled, false);
    assert.equal(requests.filter(x => x.pathname === `/api/operations/${mode}`).length, 2);
  }
  console.log('PASS: transaction/lock/semaphore mode routing and HTTP 400 recovery (mock API)');

  hold = true;
  const pending = first.click('#lab-parallel');
  await until(() => releases.length === 1);
  first.click('#lab-cancel');
  await pending;
  assert.match(first.get('#lab-log').textContent, /Request cancelled/);
  assert.equal(first.get('#lab-parallel').disabled, false);
  hold = false;
  releases.splice(0).forEach(resolve => resolve());
  console.log('PASS: cancellation restores the client');

  offline = true;
  await first.click('#lab-parallel');
  assert.match(first.get('#lab-log').textContent, /Cannot reach the shop API/);
  assert.equal(first.get('#lab-parallel').disabled, false);
  offline = false;
  failNext = true;
  await first.click('#lab-parallel');
  assert.match(first.get('#lab-log').textContent, /HTTP 500/);
  assert.equal(first.get('#lab-parallel').disabled, false);
  await first.click('#lab-parallel');
  assert.equal(first.get('#lab-parallel').disabled, false);
  console.log('PASS: unavailable server, HTTP 500 and subsequent successful request');

  first.get('#lab-product-id').value = '999';
  await first.click('#lab-state');
  assert.match(first.get('#lab-log').textContent, /HTTP 404/);
  assert.equal(first.get('#lab-state').disabled, false);
  console.log('PASS: missing resource and button recovery');
})().catch(error => { console.error(error); process.exitCode = 1; });
