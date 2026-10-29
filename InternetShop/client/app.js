(() => {
  'use strict';

  const $ = selector =>
    document.querySelector(selector);

  const requestedClient = new URLSearchParams(location.search).get('client') || '';
  const clientId = /^[1-9]\d{0,8}$/.test(requestedClient)
    ? requestedClient : crypto.randomUUID().slice(0, 8);
  const activeRequests = new Map();
  const activity = [];
  let requestSequence = 0;

  const requestedOffset = Number(new URLSearchParams(location.search).get('offset') || 0);
  const clockOffset = Number.isFinite(requestedOffset) && Math.abs(requestedOffset) <= 86400
    ? requestedOffset * 1000 : 0;
  let correction = 0;
  let lastClockSample = 0;
  const localTime = () => Date.now() + clockOffset;

  function updateClock(response, sent, received, number) {
    const serverReceived = Date.parse(response.headers.get('X-Server-Received-Time'));
    const serverSent = Date.parse(response.headers.get('X-Server-Time'));
    if (!Number.isFinite(serverReceived) || !Number.isFinite(serverSent) ||
        serverSent < serverReceived || received < sent || number < lastClockSample) return;
    lastClockSample = number;
    correction = ((serverReceived - sent) + (serverSent - received)) / 2;
    const rtt = received - sent;
    const networkDelay = Math.max(0, rtt - (serverSent - serverReceived)) / 2;
    $('#lab-clock').textContent = [
      `Client local time: ${new Date(localTime()).toISOString()}`,
      `Server time at response: ${new Date(serverSent).toISOString()}`,
      `Saved correction: ${(correction / 1000).toFixed(3)} seconds`,
      `Corrected client time: ${new Date(localTime() + correction).toISOString()}`,
      `Request sent time: ${new Date(sent).toISOString()}`,
      `Response received time: ${new Date(received).toISOString()}`,
      `Request RTT (including server work): ${rtt.toFixed(3)} ms`,
      `Estimated one-way network delay: ${networkDelay.toFixed(3)} ms`
    ].join('\n');
  }

  function logActivity(message) {
    const time = new Date(localTime() + correction).toISOString().slice(11, 23);
    activity.push(`[${time} UTC] Client ${clientId} ${message}`);
    if (activity.length > 60) activity.shift();
    $('#lab-log').textContent = activity.join('\n');
    $('#lab-log').scrollTop = $('#lab-log').scrollHeight;
  }

  const state = {
    api: localStorage.getItem('internetShopApiUrl') ||
      'http://localhost:5000',

    bootstrap: null,
    products: [],
    orders: [],
    cart: new Map(),
    selectedOrder: null,
    search: '',
    busy: false
  };

  $('#api-url').value = state.api;
  $('#client-label').textContent = `Client ${clientId}`;

  const money = value =>
    new Intl.NumberFormat(undefined, {
      style: 'currency',
      currency: 'USD'
    }).format(value);

  async function api(path, options = {}) {
    const number = ++requestSequence;
    const controller = new AbortController();
    activeRequests.set(number, controller);
    const timer = setTimeout(() => controller.abort('timeout'), 60000);
    const method = options.method || 'GET';
    logActivity(`-> SEND #${number} ${method} ${path}`);
    const sent = localTime();
    try {
      const response = await fetch(state.api + path, {
        method,
        cache: 'no-store',
        headers: {
          'X-Client-Id': clientId,
          ...(options.body ? { 'Content-Type': 'application/json' } : {})
        },
        body: options.body ? JSON.stringify(options.body) : undefined,
        signal: controller.signal
      });
      const received = localTime();
      updateClock(response, sent, received, number);
      const text = await response.text();
      let data;
      try { data = text ? JSON.parse(text) : null; }
      catch { data = { message: text }; }
      const trace = response.headers.get('X-Request-Id');
      logActivity(`<- HTTP ${response.status} #${number}${trace ? ` Request=${trace}` : ''}`);
      if (!response.ok) {
        const error = new Error(data?.detail || data?.title || data?.message || `HTTP ${response.status}`);
        error.status = response.status;
        error.data = data;
        throw error;
      }
      return { status: response.status, data };
    } catch (error) {
      if (error?.status) throw error;
      const message = controller.signal.aborted
        ? (controller.signal.reason === 'timeout' ? 'The request timed out.' : 'Request cancelled.')
        : 'Cannot reach the shop API. Check the address, server, and CORS settings.';
      logActivity(`<- ERROR #${number}: ${message}`);
      throw new Error(message);
    } finally {
      clearTimeout(timer);
      activeRequests.delete(number);
    }
  }

  async function operation(name, body) {
    const result = await api(
      `/api/operations/${name}`,
      {
        method: 'POST',
        body
      }
    );

    return result.data;
  }

  let toastTimer;

  function toast(message, failed = false) {
    const element = $('#toast');

    element.textContent = message;

    element.className =
      `toast show${failed ? ' error' : ''}`;

    clearTimeout(toastTimer);

    toastTimer = setTimeout(() => {
      element.classList.remove('show');
    }, 4000);
  }

  function handleError(error) {
    console.error(error);
    toast(error.message || 'Something went wrong.', true);
  }

  async function busy(button, task) {
    if (state.busy) {
      return;
    }

    state.busy = true;

    const originalText =
      button.textContent;

    button.disabled = true;
    button.textContent = 'Please wait…';

    try {
      await task();
    } finally {
      state.busy = false;
      button.textContent = originalText;
      button.disabled = false;

      renderCart();
      renderOrderDetail();
    }
  }

  function productById(productId) {
    return state.products.find(
      product =>
        Number(product.id) === Number(productId)
    );
  }

  function selectedOrderById(orderId) {
    return state.orders.find(
      order =>
        Number(order.id) === Number(orderId)
    );
  }

  async function loadBootstrap() {
    const result =
      await api('/api/bootstrap');

    state.bootstrap = result.data;

    $('#customer-name').textContent =
      result.data.customerName;
  }

  async function loadProducts() {
    const result =
      await api('/api/products');

    state.products =
      Array.isArray(result.data)
        ? result.data
        : [];

    reconcileCart();
    renderProducts();
    renderCart();
  }

  async function loadOrders() {
    const result =
      await api('/api/orders');

    state.orders =
      Array.isArray(result.data)
        ? result.data
        : [];

    if (state.selectedOrder) {
      const updated = selectedOrderById(
        state.selectedOrder.id
      );

      if (updated) {
        state.selectedOrder = updated;
      }
    }

    renderOrders();
    renderOrderDetail();
  }

  function reconcileCart() {
    for (const [productId, quantity] of state.cart) {
      const product = productById(productId);

      if (!product || product.stockQuantity === 0) {
        state.cart.delete(productId);
        continue;
      }

      if (quantity > product.stockQuantity) {
        state.cart.set(
          productId,
          product.stockQuantity
        );
      }
    }
  }

  function productSymbol(product) {
    const source =
      product.name.trim() ||
      product.sku.trim();

    return source
      .charAt(0)
      .toUpperCase();
  }

  function createProductCard(product) {
    const article =
      document.createElement('article');

    article.className = 'product-card';

    const visual =
      document.createElement('div');

    visual.className =
      'product-visual' +
      (product.stockQuantity === 0
        ? ' out-of-stock'
        : '');

    const sku =
      document.createElement('span');

    sku.className = 'product-sku';
    sku.textContent = product.sku;

    const symbol =
      document.createElement('span');

    symbol.className = 'product-symbol';
    symbol.textContent = productSymbol(product);

    const stock =
      document.createElement('span');

    stock.className =
      'stock-label' +
      (product.stockQuantity === 0
        ? ' empty'
        : '');

    stock.textContent =
      product.stockQuantity === 0
        ? 'Out of stock'
        : `${product.stockQuantity} available`;

    visual.append(
      sku,
      symbol,
      stock
    );

    const info =
      document.createElement('div');

    info.className = 'product-info';

    const name =
      document.createElement('h3');

    name.textContent = product.name;

    const bottom =
      document.createElement('div');

    bottom.className = 'product-bottom';

    const price =
      document.createElement('span');

    price.className = 'product-price';
    price.textContent = money(product.price);

    const add =
      document.createElement('button');

    add.type = 'button';
    add.className = 'add-button';
    add.dataset.action = 'add';
    add.dataset.productId = product.id;
    add.textContent = '+';
    add.disabled =
      product.stockQuantity === 0;

    bottom.append(price, add);
    info.append(name, bottom);
    article.append(visual, info);

    return article;
  }

  function renderProducts() {
    const grid = $('#product-grid');

    grid.replaceChildren();

    const search =
      state.search.trim().toLowerCase();

    const products = state.products.filter(
      product =>
        !search ||
        product.name.toLowerCase().includes(search) ||
        product.sku.toLowerCase().includes(search)
    );

    $('#catalog-count').textContent =
      state.products.length.toLocaleString();

    if (!products.length) {
      const empty =
        document.createElement('div');

      empty.className = 'no-results';

      empty.textContent = search
        ? 'No products match your search.'
        : 'No products are available.';

      grid.append(empty);
      return;
    }

    for (const product of products) {
      grid.append(
        createProductCard(product)
      );
    }
  }

  function addToCart(productId) {
    const product =
      productById(productId);

    if (!product) {
      throw new Error(
        'The selected product is unavailable.'
      );
    }

    const current =
      state.cart.get(productId) || 0;

    if (current >= product.stockQuantity) {
      throw new Error(
        `Only ${product.stockQuantity} units of ${product.name} are available.`
      );
    }

    state.cart.set(
      productId,
      current + 1
    );

    renderCart();

    toast(
      `${product.name} added to your cart.`
    );
  }

  function changeCartQuantity(
    productId,
    change
  ) {
    const product =
      productById(productId);

    if (!product) {
      return;
    }

    const current =
      state.cart.get(productId) || 0;

    const next =
      current + change;

    if (next <= 0) {
      state.cart.delete(productId);
    } else if (next <= product.stockQuantity) {
      state.cart.set(productId, next);
    } else {
      toast(
        `Only ${product.stockQuantity} units are available.`,
        true
      );
    }

    renderCart();
  }

  function createCartItem(
    product,
    quantity
  ) {
    const item =
      document.createElement('div');

    item.className = 'cart-item';

    const copy =
      document.createElement('div');

    const name =
      document.createElement('h4');

    name.textContent = product.name;

    const sku =
      document.createElement('small');

    sku.textContent = product.sku;

    copy.append(name, sku);

    const price =
      document.createElement('span');

    price.className = 'cart-item-price';

    price.textContent =
      money(product.price * quantity);

    const controls =
      document.createElement('div');

    controls.className =
      'cart-item-controls';

    const quantityControl =
      document.createElement('div');

    quantityControl.className =
      'quantity-control';

    const decrease =
      document.createElement('button');

    decrease.type = 'button';
    decrease.dataset.action = 'decrease';
    decrease.dataset.productId = product.id;
    decrease.textContent = '−';

    const amount =
      document.createElement('span');

    amount.textContent = quantity;

    const increase =
      document.createElement('button');

    increase.type = 'button';
    increase.dataset.action = 'increase';
    increase.dataset.productId = product.id;
    increase.textContent = '+';

    quantityControl.append(
      decrease,
      amount,
      increase
    );

    const remove =
      document.createElement('button');

    remove.type = 'button';
    remove.className = 'remove-cart-item';
    remove.dataset.action = 'remove';
    remove.dataset.productId = product.id;
    remove.textContent = 'Remove';

    controls.append(
      quantityControl,
      remove
    );

    item.append(
      copy,
      price,
      controls
    );

    return item;
  }

  function renderCart() {
    const container =
      $('#cart-items');

    container.replaceChildren();

    let total = 0;
    let count = 0;

    for (const [productId, quantity] of state.cart) {
      const product =
        productById(productId);

      if (!product) {
        continue;
      }

      total +=
        product.price * quantity;

      count += quantity;

      container.append(
        createCartItem(
          product,
          quantity
        )
      );
    }

    $('#cart-count').textContent =
      count.toLocaleString();

    $('#cart-total').textContent =
      money(total);

    $('#cart-empty').classList.toggle(
      'hidden',
      state.cart.size > 0
    );

    $('#checkout').disabled =
      state.cart.size === 0 ||
      state.busy;
  }

  function statusClass(status) {
    return String(status)
      .toLowerCase();
  }

  function createOrderCard(order) {
    const card =
      document.createElement('article');

    card.className =
      'order-card' +
      (state.selectedOrder?.id === order.id
        ? ' selected'
        : '');

    const main =
      document.createElement('div');

    main.className = 'order-main';

    const id =
      document.createElement('strong');

    id.textContent = `Order #${order.id}`;

    const items =
      document.createElement('small');

    items.textContent =
      `${order.items.length} product` +
      (order.items.length === 1
        ? ''
        : 's');

    main.append(id, items);

    const status =
      document.createElement('span');

    status.className =
      `order-status ${statusClass(order.status)}`;

    status.textContent =
      order.status;

    const date =
      document.createElement('span');

    date.className = 'order-date';

    date.textContent =
      new Date(order.createdAt)
        .toLocaleDateString();

    const amount =
      document.createElement('span');

    amount.className = 'order-amount';

    amount.textContent =
      money(order.total);

    const open =
      document.createElement('button');

    open.type = 'button';
    open.className = 'open-order';
    open.dataset.orderId = order.id;
    open.textContent = 'View';

    card.append(
      main,
      status,
      date,
      amount,
      open
    );

    return card;
  }

  function renderOrders() {
    const list = $('#orders-list');

    list.replaceChildren();

    if (!state.orders.length) {
      const empty =
        document.createElement('div');

      empty.className = 'orders-empty';
      empty.textContent =
        'You have not created any orders yet.';

      list.append(empty);
      return;
    }

    for (const order of state.orders) {
      list.append(
        createOrderCard(order)
      );
    }
  }

  function renderOrderDetail() {
    const order =
      state.selectedOrder;

    $('#order-detail-empty')
      .classList.toggle(
        'hidden',
        Boolean(order)
      );

    $('#order-detail-content')
      .classList.toggle(
        'hidden',
        !order
      );

    if (!order) {
      return;
    }

    $('#detail-order-id').textContent =
      `Order #${order.id}`;

    const status =
      $('#detail-status');

    status.textContent =
      order.status;

    status.className =
      `order-status ${statusClass(order.status)}`;

    $('#detail-total').textContent =
      money(order.total);

    const items =
      $('#detail-items');

    items.replaceChildren();

    for (const orderItem of order.items) {
      const item =
        document.createElement('div');

      item.className = 'detail-item';

      const copy =
        document.createElement('div');

      const name =
        document.createElement('strong');

      name.textContent =
        orderItem.productName;

      const quantity =
        document.createElement('small');

      quantity.textContent =
        `${orderItem.quantity} × ` +
        money(orderItem.unitPrice);

      copy.append(name, quantity);

      const total =
        document.createElement('span');

      total.textContent =
        money(orderItem.lineTotal);

      item.append(copy, total);
      items.append(item);
    }

    const pending =
      order.status === 'Pending';

    $('#pay-order').disabled =
      !pending || state.busy;

    $('#cancel-order').disabled =
      !pending || state.busy;

    $('#payment-method').disabled =
      !pending;

    const paymentResult =
      $('#payment-result');

    paymentResult.classList.toggle(
      'hidden',
      !order.payment
    );

    if (order.payment) {
      $('#payment-description').textContent =
        `${money(order.payment.amount)} via ` +
        order.payment.method;
    }
  }

  async function refreshShop() {
    await loadProducts();
    await loadOrders();
  }

  async function connect() {
    const indicator =
      $('#health');

    indicator.className = 'health';

    indicator.innerHTML =
      '<i></i> Connecting';

    try {
      await api('/health');

      indicator.className =
        'health online';

      indicator.innerHTML =
        '<i></i> Online';
    } catch (error) {
      indicator.className =
        'health offline';

      indicator.innerHTML =
        '<i></i> Offline';

      handleError(error);
      return;
    }

    try {
      await loadBootstrap();
      await refreshShop();
    } catch (error) {
      handleError(error);
    }
  }

  $('#product-grid').addEventListener(
    'click',
    event => {
      const button =
        event.target.closest(
          '[data-action="add"]'
        );

      if (!button) {
        return;
      }

      try {
        addToCart(
          Number(button.dataset.productId)
        );
      } catch (error) {
        handleError(error);
      }
    }
  );

  $('#cart-items').addEventListener(
    'click',
    event => {
      const button =
        event.target.closest(
          '[data-action]'
        );

      if (!button) {
        return;
      }

      const productId =
        Number(button.dataset.productId);

      if (button.dataset.action === 'increase') {
        changeCartQuantity(productId, 1);
      }

      if (button.dataset.action === 'decrease') {
        changeCartQuantity(productId, -1);
      }

      if (button.dataset.action === 'remove') {
        state.cart.delete(productId);
        renderCart();
      }
    }
  );

  $('#checkout').addEventListener(
    'click',
    event => {
      busy(
        event.currentTarget,
        async () => {
          try {
            if (!state.bootstrap) {
              throw new Error(
                'Customer data is not loaded.'
              );
            }

            if (!state.cart.size) {
              throw new Error(
                'Your cart is empty.'
              );
            }

            const order =
              await operation(
                'CreateOrder',
                {
                  customerId:
                    state.bootstrap.customerId,

                  items: Array.from(
                    state.cart,
                    ([productId, quantity]) => ({
                      productId,
                      quantity
                    })
                  )
                }
              );

            state.cart.clear();
            state.selectedOrder = order;

            await refreshShop();

            toast(
              `Order #${order.id} was created.`
            );

            $('#orders').scrollIntoView({
              behavior: 'smooth',
              block: 'start'
            });
          } catch (error) {
            handleError(error);
          }
        }
      );
    }
  );

  $('#orders-list').addEventListener(
    'click',
    event => {
      const button =
        event.target.closest(
          '[data-order-id]'
        );

      if (!button) {
        return;
      }

      state.selectedOrder =
        selectedOrderById(
          Number(button.dataset.orderId)
        );

      renderOrders();
      renderOrderDetail();
    }
  );

  $('#pay-order').addEventListener(
    'click',
    event => {
      busy(
        event.currentTarget,
        async () => {
          try {
            if (!state.selectedOrder) {
              throw new Error(
                'Select an order first.'
              );
            }

            state.selectedOrder =
              await operation(
                'PayOrder',
                {
                  orderId:
                    state.selectedOrder.id,

                  amount:
                    state.selectedOrder.total,

                  method:
                    $('#payment-method').value
                }
              );

            await loadOrders();

            toast(
              `Order #${state.selectedOrder.id} was paid successfully.`
            );
          } catch (error) {
            handleError(error);
          }
        }
      );
    }
  );

  $('#cancel-order').addEventListener(
    'click',
    event => {
      busy(
        event.currentTarget,
        async () => {
          try {
            if (!state.selectedOrder) {
              throw new Error(
                'Select an order first.'
              );
            }

            state.selectedOrder =
              await operation(
                'CancelOrder',
                {
                  orderId:
                    state.selectedOrder.id
                }
              );

            await refreshShop();

            toast(
              `Order #${state.selectedOrder.id} was cancelled.`
            );
          } catch (error) {
            handleError(error);
          }
        }
      );
    }
  );

  $('#product-search').addEventListener(
    'input',
    event => {
      state.search =
        event.currentTarget.value;

      renderProducts();
    }
  );

  $('#refresh').addEventListener(
    'click',
    event => {
      busy(
        event.currentTarget,
        async () => {
          try {
            await refreshShop();

            toast(
              'The shop has been refreshed.'
            );
          } catch (error) {
            handleError(error);
          }
        }
      );
    }
  );

  $('#connection-form').addEventListener(
    'submit',
    event => {
      event.preventDefault();

      busy(
        event.submitter,
        async () => {
          try {
            const url =
              new URL(
                $('#api-url').value.trim()
              );

            if (
              !['http:', 'https:']
                .includes(url.protocol)
            ) {
              throw new Error(
                'Use an HTTP or HTTPS address.'
              );
            }

            state.api =
              url.origin +
              url.pathname.replace(/\/+$/, '');

            localStorage.setItem(
              'internetShopApiUrl',
              state.api
            );

            await connect();

            toast(
              'API connection updated.'
            );
          } catch (error) {
            handleError(error);
          }
        }
      );
    }
  );

  async function labAction(button, task) {
    await busy(button, async () => {
      try { await task(); } catch (error) { handleError(error); }
    });
  }

  function raceProductId() {
    const id = Number($('#lab-product-id').value);
    if (!Number.isSafeInteger(id) || id <= 0) throw new Error('Enter a positive Product ID.');
    return id;
  }

  $('#lab-sync-time').addEventListener('click', event => labAction(event.currentTarget, async () => {
    await api('/api/time', { cache: 'no-store' });
    logActivity('Time synchronization sample received.');
  }));

  $('#lab-parallel').addEventListener('click', event => labAction(event.currentTarget, async () => {
    const products = await operation('GetProducts', {});
    logActivity(`Test request completed: ${products.length} products.`);
    toast('Test request completed. Compare START and END in the server console.');
  }));

  $('#lab-prepare').addEventListener('click', event => labAction(event.currentTarget, async () => {
    const { data } = await api('/api/lab/prepare-race', { method: 'POST', body: {} });
    $('#lab-product-id').value = data.product.id;
    logActivity(`Prepared Product=${data.product.id}, StockQuantity=1. Copy this ID to Client 2.`);
    await loadProducts();
    toast(`Product ${data.product.id} is ready. Use this same ID in the other tab.`);
  }));

  $('#lab-order').addEventListener('click', event => labAction(event.currentTarget, async () => {
    const productId = raceProductId();
    const mode = $('#lab-order-mode').value || 'RaceCreateOrder';
    if (!['RaceCreateOrder', 'TransactionCreateOrder', 'SyncCreateOrder', 'SemaphoreCreateOrder'].includes(mode))
      throw new Error('Select a valid order mode.');
    if (!state.bootstrap) await loadBootstrap();
    const order = await operation(mode, {
      customerId: state.bootstrap.customerId,
      items: [{ productId, quantity: 1 }]
    });
    logActivity(`SUCCESS Mode=${mode} Order=${order.id}, Product=${productId}, Quantity=1.`);
    toast(`Order #${order.id} created. Read shared stock after BOTH responses.`);
    await refreshShop();
  }));

  $('#lab-state').addEventListener('click', event => labAction(event.currentTarget, async () => {
    const { data } = await api(`/api/lab/products/${raceProductId()}/state`);
    logActivity(`STATE Product=${data.productId} StockQuantity=${data.stockQuantity} ` +
      `OrderedQuantity=${data.orderedQuantity} Orders=[${data.orderIds.join(', ')}]`);
  }));

  $('#lab-cancel').addEventListener('click', () => {
    logActivity(`Cancelling ${activeRequests.size} pending request(s).`);
    for (const controller of activeRequests.values()) controller.abort('manual');
  });

  window.addEventListener('pagehide', () => {
    for (const controller of activeRequests.values()) controller.abort('manual');
  });

  connect();
})();
