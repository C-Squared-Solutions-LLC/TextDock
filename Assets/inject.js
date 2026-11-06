// TextDock page bootstrap.
// WebView2 runs this in every new document before the page's own scripts. It only acts on
// the top-level messages.google.com document. The host appends the call with the config,
// so this file must stay a single function expression with no trailing semicolon:
//   (function (cfg) { ... })\n({ compact, css, userCss, muteChime, important });
(function (cfg) {
  'use strict';
  if (window.top !== window || location.hostname !== 'messages.google.com') return;
  if (window.__textdock) {
    window.__textdock.configure(cfg);
    return;
  }

  const doc = document;
  const post = (msg) => {
    try {
      window.chrome.webview.postMessage(msg);
    } catch (_) {
      // host not attached
    }
  };

  let compact = true;
  let muteChime = false;
  let important = new Set(); // conversation ids starred in the app

  // ---- styles -----------------------------------------------------------------------

  const baseStyle = doc.createElement('style');
  baseStyle.id = 'tdk-base';
  const userStyle = doc.createElement('style');
  userStyle.id = 'tdk-user';

  // The script runs before <html> exists, and Angular keeps appending component styles to
  // <head> as views load. Keep ours last so equal-specificity rules resolve our way.
  function mount() {
    const html = doc.documentElement;
    if (!html) return;
    if (html.classList.contains('tdk-compact') !== compact) html.classList.toggle('tdk-compact', compact);
    const parent = doc.head || html;
    if (parent.lastElementChild !== userStyle || userStyle.previousElementSibling !== baseStyle) {
      parent.appendChild(baseStyle);
      parent.appendChild(userStyle);
    }
  }

  // ---- Messages' own menu ---------------------------------------------------------------
  // Compact mode hides the page's top bar (logo, account, hamburger). The window title bar
  // gets a hamburger instead, which clicks the hidden one. The bar is only hidden once that
  // button has been found, so a renamed button leaves the page's own bar in place.

  const MENU_BUTTON = '[data-e2e-branded-header-menu-button], mw-branded-header .menu button';
  let lastBrand = null;

  function syncBrand() {
    const html = doc.documentElement;
    if (!html) return;
    const hide = compact && !!doc.querySelector(MENU_BUTTON);
    if (html.classList.contains('tdk-hide-brand') !== hide) html.classList.toggle('tdk-hide-brand', hide);
    if (hide !== lastBrand) {
      lastBrand = hide;
      post({ type: 'brand', hidden: hide });
    }
  }

  function openMenu() {
    const button = doc.querySelector(MENU_BUTTON);
    if (!button) return false;
    button.click();
    return true;
  }

  function configure(c) {
    if (!c) return;
    if (typeof c.css === 'string') baseStyle.textContent = c.css;
    if (typeof c.userCss === 'string') userStyle.textContent = c.userCss;
    if (typeof c.compact === 'boolean') compact = c.compact;
    if (typeof c.muteChime === 'boolean') muteChime = c.muteChime;
    if (Array.isArray(c.important)) important = new Set(c.important.map(String));
    mount();
    syncBrand();
  }

  configure(cfg);

  const early = new MutationObserver(mount);
  early.observe(doc, { childList: true, subtree: true });
  doc.addEventListener('DOMContentLoaded', () => {
    early.disconnect();
    mount();
    new MutationObserver(mount).observe(doc.head || doc.documentElement, { childList: true });
  });

  // ---- notifications ----------------------------------------------------------------------

  // Messages sends notifications that carry actions (inline reply) through its service worker
  // registration, and WebView2 only hands page-level notifications to the app. Send those
  // through the Notification constructor, minus the actions, so the app can show them.
  try {
    const proto = window.ServiceWorkerRegistration && window.ServiceWorkerRegistration.prototype;
    if (proto && typeof proto.showNotification === 'function') {
      const viaWorker = proto.showNotification;
      proto.showNotification = function (title, options) {
        try {
          const rest = Object.assign({}, options || {});
          delete rest.actions;
          const n = new Notification(title, rest);
          n.addEventListener('click', () => window.focus());
          return Promise.resolve();
        } catch (_) {
          return viaWorker.apply(this, arguments);
        }
      };
    }
  } catch (_) {
    // leave the original in place
  }

  // The page plays its own chime with each notification. The app's toast already makes the
  // Windows notification sound (which honors Do Not Disturb), so skip the chime while the
  // app's notifications are on.
  const play = HTMLMediaElement.prototype.play;
  HTMLMediaElement.prototype.play = function () {
    if (muteChime) {
      const source = this.querySelector && this.querySelector('source');
      const src = this.currentSrc || this.src || (source && source.src) || '';
      if (/incoming_message/i.test(src)) return Promise.resolve();
    }
    return play.apply(this, arguments);
  };

  // ---- theme -> title bar -------------------------------------------------------------

  // The title bar sits on top of the page; blend it into the page background.
  const THEME_PROBES = ['body', 'mw-app', 'html'];
  const ctx = doc.createElement('canvas').getContext('2d');

  // Canvas normalizes any CSS color (rgb, color(), oklch...) to #rrggbb or rgba().
  function toRgba(css) {
    if (!ctx || !css) return null;
    ctx.fillStyle = '#000';
    ctx.fillStyle = css;
    const v = String(ctx.fillStyle);
    if (v[0] === '#') {
      return [parseInt(v.slice(1, 3), 16), parseInt(v.slice(3, 5), 16), parseInt(v.slice(5, 7), 16), 1];
    }
    const m = /rgba?\(([^)]+)\)/.exec(v);
    if (!m) return null;
    const p = m[1].split(/[\s,/]+/).filter(Boolean).map(Number);
    return [p[0], p[1], p[2], p.length > 3 ? p[3] : 1];
  }

  let lastTheme = '';
  function reportTheme() {
    let rgba = null;
    for (const sel of THEME_PROBES) {
      const el = doc.querySelector(sel);
      if (!el) continue;
      const c = toRgba(getComputedStyle(el).backgroundColor);
      if (c && c[3] > 0.5) {
        rgba = c;
        break;
      }
    }
    if (!rgba) return;
    const rgb = rgba.slice(0, 3).map((v) => Math.max(0, Math.min(255, Math.round(v))));
    const key = rgb.join(',');
    if (key === lastTheme) return;
    lastTheme = key;
    const luminance = (0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2]) / 255;
    post({ type: 'theme', bg: rgb, dark: luminance < 0.5 });
  }

  // ---- unread count -------------------------------------------------------------------

  // Unread, unmuted conversations in the list. The "(N)" the page puts in the title counts
  // pending notifications instead, so it's only a fallback for views without the list.
  const LIST_ROW = 'mws-conversation-list-item a[data-e2e-is-unread]';
  const UNREAD_ROW = 'mws-conversation-list-item a[data-e2e-is-unread="true"]:not([data-e2e-is-muted="true"])';

  function countUnread() {
    if (doc.querySelector(LIST_ROW)) return doc.querySelectorAll(UNREAD_ROW).length;
    const fromTitle = /^\((\d+)\)/.exec(doc.title);
    return fromTitle ? Number(fromTitle[1]) : 0;
  }

  let lastUnread = -1;
  function reportUnread() {
    const n = countUnread();
    if (n === lastUnread) return;
    lastUnread = n;
    post({ type: 'unread', count: n });
  }

  // ---- selection & bulk delete ----------------------------------------------------------
  // Messages for web only deletes one conversation at a time. Selection mode ticks rows in
  // the list (Select button, Ctrl+click, or "Unknown" for every sender without a contact
  // name), then deletes them by driving the app's own menu: Delete, then confirm, one
  // conversation at a time. Nothing is deleted until the desktop app has shown its own
  // confirmation and handed back the one-time token issued with that request.

  const ROW = 'mws-conversation-list-item';
  const ROW_LINK = 'a.list-item[data-e2e-conversation]';
  const ROW_MENU = 'button[data-e2e-conversation-list-item-menu]';
  const MENU_DELETE = '.cdk-overlay-container button[data-e2e-conversation-delete]';
  const DIALOG_CONFIRM = '.cdk-overlay-container [data-e2e-action-button-confirm]:not([disabled])';
  const LOAD_MORE = 'mws-conversations-list button.load-more';
  const BACK_BUTTON = '[data-e2e-header-back-button]';
  const BIDI = /[\u200E\u200F\u202A-\u202E\u2066-\u2069]/g;
  const SVG_NS = 'http://www.w3.org/2000/svg';
  const ICONS = {
    select: 'M3 5h2v2H3zm4 0h14v2H7zM3 11h2v2H3zm4 0h14v2H7zm-4 6h2v2H3zm4 0h14v2H7z',
    close: 'M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z',
    trash: 'M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6zM8 9h8v10H8zm7.5-5-1-1h-5l-1 1H5v2h14V4z',
    star: 'M22 9.24l-7.19-.62L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21 12 17.27 18.18 21l-1.63-7.03L22 9.24zM12 15.4l-3.76 2.27 1-4.28-3.32-2.88 4.38-.38L12 6.1l1.71 4.04 4.38.38-3.32 2.88 1 4.28L12 15.4z',
    starFilled: 'M12 17.27 18.18 21l-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21z',
  };

  const selected = new Set(); // conversation hrefs
  let selecting = false;
  let busy = false;
  let stopRequested = false;
  let status = '';
  let pendingToken = null;
  let pendingTargets = null;
  let bar = null;

  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  async function waitFor(test, timeout) {
    const end = Date.now() + timeout;
    while (Date.now() < end) {
      const v = test();
      if (v) return v;
      await sleep(80);
    }
    return null;
  }

  const allRows = () => [...doc.querySelectorAll(ROW)];
  const linkOf = (row) => row.querySelector(ROW_LINK);
  const keyOf = (row) => {
    const a = linkOf(row);
    return a ? a.getAttribute('href') : null;
  };
  const rowFor = (key) => allRows().find((r) => keyOf(r) === key) || null;
  const nameOf = (row) => {
    const n = row.querySelector('[data-e2e-conversation-name]');
    return n ? n.textContent.replace(BIDI, '').trim() : '';
  };

  // "Not in contacts": Messages shows the raw number (or short code / email) when there's no
  // contact name. Groups and pinned conversations are never picked automatically.
  function isUnknownSender(row) {
    const a = linkOf(row);
    if (!a || a.getAttribute('data-e2e-is-pinned') === 'true') return false;
    if (row.querySelector('mws-group-avatar')) return false;
    const name = nameOf(row);
    if (!name) return false;
    const digits = (name.match(/\d/g) || []).length;
    if (/^[\s\d+()./#*\-\u2010-\u2015]+$/.test(name) && digits >= 3) return true;
    return /^[^\s@,]+@[^\s@,]+\.[^\s@,]+$/.test(name);
  }

  function icon(name) {
    const svg = doc.createElementNS(SVG_NS, 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('aria-hidden', 'true');
    const path = doc.createElementNS(SVG_NS, 'path');
    path.setAttribute('d', ICONS[name]);
    svg.appendChild(path);
    return svg;
  }

  function button(label, iconName, onClick, extraClass, title) {
    const b = doc.createElement('button');
    b.type = 'button';
    b.className = 'tdk-btn' + (extraClass ? ' ' + extraClass : '') + (label ? '' : ' tdk-icon-only');
    if (iconName) b.appendChild(icon(iconName));
    if (label) b.appendChild(doc.createTextNode(label));
    if (title) b.title = title;
    b.addEventListener('click', (e) => {
      e.preventDefault();
      e.stopPropagation();
      onClick();
    });
    return b;
  }

  let barSignature = '';
  let observedList = null;

  function renderBar() {
    const header = doc.querySelector('mw-main-nav .main-nav-header');
    if (!header) return;
    if (!bar) {
      bar = doc.createElement('div');
      bar.className = 'tdk-bar';
    }
    let moved = false;
    if (bar.parentElement !== header) {
      header.appendChild(bar);
      moved = true;
    }
    if (!header.classList.contains('tdk-has-bar')) header.classList.add('tdk-has-bar');
    doc.documentElement.classList.toggle('tdk-selecting', selecting || busy);

    // Repaint highlights as soon as Angular re-creates rows.
    const list = doc.querySelector('mws-conversations-list .conv-container');
    if (list && list !== observedList) {
      observedList = list;
      new MutationObserver(paintRows).observe(list, { childList: true, subtree: true });
    }

    const signature = `${busy}|${selecting}|${selected.size}|${status}`;
    if (!moved && signature === barSignature) return;
    barSignature = signature;

    const parts = [];
    if (busy) {
      const s = doc.createElement('span');
      s.className = 'tdk-status';
      s.textContent = status;
      parts.push(s, button('Stop', null, () => { stopRequested = true; }, null, 'Stop after the current conversation'));
    } else if (selecting) {
      parts.push(button('', 'close', stopSelecting, null, 'Done (Esc)'));
      const s = doc.createElement('span');
      s.className = 'tdk-status';
      s.textContent = status || `${selected.size} selected`;
      parts.push(s);
      parts.push(button('Unknown', null, selectUnknown, null, 'Select every conversation without a contact name (loads the whole list)'));
      parts.push(button('All', null, selectAllLoaded, null, 'Select all loaded conversations'));
      const del = button('Delete', 'trash', requestDelete, 'tdk-danger', 'Delete the selected conversations');
      del.disabled = selected.size === 0;
      parts.push(del);
    } else {
      parts.push(button('', 'select', startSelecting, null, 'Select conversations (or Ctrl+click one)'));
    }
    bar.replaceChildren(...parts);
  }

  const setClass = (el, name, on) => {
    if (el.classList.contains(name) !== on) el.classList.toggle(name, on);
  };

  // Keep row highlights in sync; Angular re-creates rows as the list updates.
  function paintRows() {
    for (const row of allRows()) {
      const key = keyOf(row);
      const id = idOfKey(key);
      setClass(row, 'tdk-sel', selected.has(key));
      setClass(row, 'tdk-important', !!id && important.has(id));
      const st = id ? attention.get(id) : null;
      setClass(row, 'tdk-attention', !!(st && st.pending && important.has(id)));
    }
  }

  function refresh() {
    paintRows();
    renderBar();
  }

  function startSelecting() {
    if (busy) return;
    selecting = true;
    status = '';
    refresh();
  }

  function stopSelecting() {
    if (busy) return;
    selecting = false;
    selected.clear();
    status = '';
    pendingToken = null;
    pendingTargets = null;
    refresh();
  }

  function toggleRow(row) {
    const key = keyOf(row);
    if (!key) return;
    if (selected.has(key)) selected.delete(key);
    else selected.add(key);
    status = '';
    refresh();
  }

  // Narrow windows show either the list or a conversation. Get back to the list first.
  async function showList() {
    const nav = doc.querySelector('mw-main-nav');
    if (nav && nav.offsetParent !== null) return true;
    const back = doc.querySelector(BACK_BUTTON);
    if (back) back.click();
    return !!(await waitFor(() => {
      const n = doc.querySelector('mw-main-nav');
      return n && n.offsetParent !== null;
    }, 3000));
  }

  function scrollerOf(el) {
    for (let e = el; e && e !== doc.body; e = e.parentElement) {
      const oy = getComputedStyle(e).overflowY;
      if ((oy === 'auto' || oy === 'scroll') && e.scrollHeight > e.clientHeight) return e;
    }
    return null;
  }

  // The list pages in ~25 at a time. Scroll and press "load more" until it stops growing.
  async function loadEverything() {
    const scroller = scrollerOf(doc.querySelector('mws-conversations-list .conv-container'));
    let last = -1;
    let flat = 0;
    for (let i = 0; i < 400 && !stopRequested; i++) {
      const n = allRows().length;
      status = `Loading conversations… ${n}`;
      renderBar();
      if (n === last) {
        if (++flat >= 4) break;
      } else {
        flat = 0;
        last = n;
      }
      if (scroller) scroller.scrollTop = scroller.scrollHeight;
      const more = doc.querySelector(LOAD_MORE);
      if (more && more.offsetParent !== null && !more.disabled) more.click();
      await sleep(800);
    }
    if (scroller) scroller.scrollTop = 0;
  }

  async function selectUnknown() {
    if (busy) return;
    selecting = true;
    busy = true;
    stopRequested = false;
    refresh();
    try {
      await showList();
      await loadEverything();
      let added = 0;
      for (const row of allRows()) {
        const key = keyOf(row);
        if (key && isUnknownSender(row) && !selected.has(key)) {
          selected.add(key);
          added++;
        }
      }
      status = `${selected.size} selected (${added} unknown)`;
    } finally {
      busy = false;
      refresh();
    }
  }

  function selectAllLoaded() {
    if (busy) return;
    for (const row of allRows()) {
      const key = keyOf(row);
      if (key) selected.add(key);
    }
    status = '';
    refresh();
  }

  function requestDelete() {
    // One confirmation at a time; the host always answers with runDelete or cancelDelete.
    if (busy || selected.size === 0 || pendingToken) return;
    pendingTargets = [...selected];
    pendingToken = Math.random().toString(36).slice(2) + Date.now().toString(36);
    const names = pendingTargets.slice(0, 12).map((k) => {
      const row = rowFor(k);
      return row ? nameOf(row) || '(no name)' : '(not loaded)';
    });
    post({ type: 'confirmDelete', token: pendingToken, count: pendingTargets.length, names });
  }

  function cancelDelete() {
    pendingToken = null;
    pendingTargets = null;
  }

  function closeOverlays() {
    const backdrop = doc.querySelector('.cdk-overlay-backdrop');
    if (backdrop) backdrop.click();
    doc.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', keyCode: 27, bubbles: true }));
  }

  // The list may have re-rendered with fewer rows loaded; page it back in until the row shows.
  async function findRow(key) {
    let row = rowFor(key);
    if (row) return row;
    const scroller = scrollerOf(doc.querySelector('mws-conversations-list .conv-container'));
    let last = -1;
    let flat = 0;
    for (let i = 0; i < 100 && !stopRequested; i++) {
      const n = allRows().length;
      if (n === last) {
        if (++flat >= 3) break;
      } else {
        flat = 0;
        last = n;
      }
      if (scroller) scroller.scrollTop = scroller.scrollHeight;
      const more = doc.querySelector(LOAD_MORE);
      if (more && more.offsetParent !== null && !more.disabled) more.click();
      await sleep(600);
      row = rowFor(key);
      if (row) return row;
    }
    return null;
  }

  const overlayOpen = () =>
    !!doc.querySelector('.cdk-overlay-container .mat-mdc-menu-panel, .cdk-overlay-container mat-dialog-container, .cdk-overlay-container mws-dialog');

  // One conversation through the app's own UI. Returns 'deleted' or a failure reason.
  async function deleteOne(key) {
    // Never start while the previous menu or dialog is still closing: a stale "Delete" or
    // "Confirm" button from it must not be mistaken for this conversation's.
    if (overlayOpen() && !(await waitFor(() => !overlayOpen(), 4000))) {
      closeOverlays();
      return 'no-menu';
    }
    const row = await findRow(key);
    if (!row) return 'missing';
    const menuButton = row.querySelector(ROW_MENU);
    if (!menuButton) return 'no-menu';
    menuButton.click();
    const item = await waitFor(() => doc.querySelector(MENU_DELETE), 2500);
    if (!item) {
      closeOverlays();
      return 'no-delete-item';
    }
    item.click();
    // A confirm dialog follows, or with Messages' Trash on the row may just leave the list.
    const dialogOrGone = () => (doc.querySelector(DIALOG_CONFIRM) ? 'dialog' : rowFor(key) ? null : 'gone');
    let next = await waitFor(dialogOrGone, 5000);
    if (next === 'gone') {
      // Rows also vanish for a moment while the list re-renders; give a late dialog a chance.
      await sleep(400);
      if (doc.querySelector(DIALOG_CONFIRM)) next = 'dialog';
      else if (rowFor(key)) next = await waitFor(() => (doc.querySelector(DIALOG_CONFIRM) ? 'dialog' : null), 3000);
    }
    if (next === 'dialog') {
      const confirm = doc.querySelector(DIALOG_CONFIRM);
      if (confirm) confirm.click();
      const gone = await waitFor(() => !rowFor(key), 10000);
      return gone ? 'deleted' : 'still-listed';
    }
    if (next === 'gone') return 'deleted';
    closeOverlays();
    return 'no-confirm';
  }

  async function runDelete(token) {
    if (busy || !pendingToken || token !== pendingToken || !pendingTargets) return false;
    const targets = pendingTargets;
    cancelDelete();
    busy = true;
    stopRequested = false;
    let deleted = 0;
    let failed = 0;
    let abortReason = '';
    try {
      await showList();
      for (let i = 0; i < targets.length && !stopRequested; i++) {
        status = `Deleting ${i + 1} of ${targets.length}…`;
        renderBar();
        const result = await deleteOne(targets[i]);
        if (result === 'deleted') {
          deleted++;
          selected.delete(targets[i]);
        } else {
          failed++;
          // If the menu itself looks different, stop instead of guessing.
          if (result === 'no-menu' || result === 'no-delete-item' || result === 'no-confirm') {
            abortReason = result;
            break;
          }
        }
        await sleep(300);
      }
    } finally {
      busy = false;
      status = `Deleted ${deleted}` + (failed ? `, ${failed} failed` : '') + (stopRequested ? ' (stopped)' : '');
      refresh();
      post({ type: 'deleteDone', deleted, failed, stopped: stopRequested, abortReason });
    }
    return true;
  }

  // Selection clicks. Only real user clicks count, so the automation above isn't intercepted.
  doc.addEventListener('click', (e) => {
    if (!e.isTrusted) return;
    const link = e.target && e.target.closest ? e.target.closest(ROW_LINK) : null;
    if (!link) return;
    const row = link.closest(ROW);
    if (!row) return;
    if (!(selecting || busy) && !(e.ctrlKey || e.metaKey)) return;
    e.preventDefault();
    e.stopImmediatePropagation();
    if (busy) return;
    if (!selecting) startSelecting();
    toggleRow(row);
  }, true);

  doc.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && selecting && !busy && e.isTrusted) stopSelecting();
  }, true);

  // ---- important conversations ----------------------------------------------------------
  // A star in the conversation header marks a conversation important (the desktop app keeps
  // the list). When an important conversation gets a new message from the other side, it
  // stays "waiting" until you reply, and the window blinks while anything is waiting.
  // "You replied" comes from the list snippet: Messages prefixes your own last message with
  // "You:" (English UI). The app is told ids, names and flags only, never message text.

  const ME_PREFIX = /^\s*You:/;
  const REACTION = /\breacted\b/i;
  const idOfKey = (key) => (key ? key.split('/').filter(Boolean).pop() : null);
  const openId = () => {
    const m = /\/conversations\/([^/?#]+)/.exec(location.pathname);
    return m ? m[1] : null;
  };
  const snippetOf = (row) => {
    const s = row.querySelector('[data-e2e-conversation-snippet]');
    return s ? s.textContent.replace(BIDI, '').trim() : '';
  };

  const attention = new Map(); // id -> { snippet, unread, pending }
  let lastAttention = '';

  function scanImportant() {
    const pending = [];
    for (const row of allRows()) {
      const id = idOfKey(keyOf(row));
      if (!id || !important.has(id)) continue;
      const unread = linkOf(row).getAttribute('data-e2e-is-unread') === 'true';
      const snippet = snippetOf(row);
      const mine = ME_PREFIX.test(snippet);
      let st = attention.get(id);
      if (!st) {
        // First sighting (page load or just starred): only an unread message counts as new.
        st = { snippet, unread, pending: unread && !mine };
        attention.set(id, st);
      } else {
        const arrived = snippet !== st.snippet || (unread && !st.unread);
        if (mine) st.pending = false;
        else if (arrived && !REACTION.test(snippet)) st.pending = true;
        st.snippet = snippet;
        st.unread = unread;
      }
      if (st.pending) pending.push({ id, name: nameOf(row) || 'A conversation' });
    }
    // No need to blink at the conversation you're typing in right now.
    const focused = doc.hasFocus() ? openId() : null;
    const blink = pending.some((p) => p.id !== focused);
    const payload = { type: 'attention', items: pending, blink };
    const signature = JSON.stringify(payload);
    if (signature !== lastAttention) {
      lastAttention = signature;
      post(payload);
    }
  }

  function setImportant(id, on) {
    if (!id) return;
    if (on) important.add(id);
    else {
      important.delete(id);
      attention.delete(id);
    }
    post({ type: 'setImportant', id, on });
    renderStar(true);
    paintRows();
    scanImportant();
  }

  function dismissAttention() {
    for (const st of attention.values()) st.pending = false;
    paintRows();
    scanImportant();
  }

  function openConversation(id) {
    const row = allRows().find((r) => idOfKey(keyOf(r)) === id);
    const link = row ? linkOf(row) : null;
    if (link) link.click();
  }

  let starButton = null;
  function renderStar(force) {
    const right = doc.querySelector('mws-header.header .right-content');
    const id = openId();
    if (!right || !id) {
      if (starButton && starButton.isConnected) starButton.remove();
      return;
    }
    if (!starButton) {
      starButton = doc.createElement('button');
      starButton.type = 'button';
      starButton.className = 'tdk-star';
      starButton.addEventListener('click', (e) => {
        e.preventDefault();
        e.stopPropagation();
        const current = openId();
        setImportant(current, !important.has(current));
      });
    }
    if (starButton.parentElement !== right) right.insertBefore(starButton, right.firstChild);
    const on = important.has(id);
    if (force || starButton.getAttribute('aria-pressed') !== String(on) || !starButton.firstChild) {
      starButton.setAttribute('aria-pressed', String(on));
      starButton.title = on
        ? 'Important: the window blinks orange until you reply. Click to unmark.'
        : 'Mark important: the window blinks orange when they write and you haven\'t replied';
      starButton.replaceChildren(icon(on ? 'starFilled' : 'star'));
    }
  }

  // Conversation switches don't reload the page; follow the URL closely for the star.
  let lastPath = '';
  setInterval(() => {
    if (location.pathname === lastPath) return;
    lastPath = location.pathname;
    renderStar();
    scanImportant();
  }, 250);
  window.addEventListener('focus', () => scanImportant());
  window.addEventListener('blur', () => scanImportant());

  // ---- loop ---------------------------------------------------------------------------

  function tick() {
    mount();
    syncBrand();
    reportTheme();
    reportUnread();
    renderBar();
    renderStar();
    scanImportant();
    paintRows();
  }
  setInterval(tick, 1500);
  window.addEventListener('load', tick);
  try {
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => setTimeout(tick, 50));
  } catch (_) {
    // older engines
  }

  window.__textdock = {
    configure,
    openMenu,
    startSelecting: () => showList().then(startSelecting),
    selectUnknown,
    runDelete,
    cancelDelete,
    dismissAttention,
    openConversation,
  };
})
