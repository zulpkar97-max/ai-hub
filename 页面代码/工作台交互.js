(() => {
  const webview = window.chrome && window.chrome.webview;
  const pendingReqs = new Map();
  let reqSeq = 1;

  let appState = null;
  let uiReadySent = false;
  let activeTab = "workspace";
  let sidebarToolsKey;
  let settingsRenderKey;
  let workspaceRenderKey;
  let projectsRenderKey;
  let projectDrag = null;
  let projectDrop = false;
  let savingProjectOrder = false;
  const projectMoves = new WeakMap();
  let backlogRenderKey;
  let backlogProject = "all";
  let backlogStatus = "all";
  let backlogQuery = "";
  const expandedSettings = new Set();
  const expandedBacklog = new Set();
  let cmdkItems = [];
  let cmdkSelected = 0;
  let networkBusy = "";
  let onNetworkPickerClose = null;

  const $ = (sel) => document.querySelector(sel);
  const $$ = (sel) => Array.from(document.querySelectorAll(sel));
  const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
  const layerExits = new WeakMap();

  const ICONS = {
    power: '<svg class="ic ic-sm" viewBox="0 0 24 24"><path d="M12 3v8M6.3 6.3a8 8 0 1 0 11.4 0"/></svg>',
    switch: '<svg class="ic ic-sm" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 8h15m-4-4 4 4-4 4M20 16H5m4-4-4 4 4 4"/></svg>',
    refresh: '<svg class="ic ic-sm" viewBox="0 0 24 24"><path d="M21 12a9 9 0 1 1-2.64-6.36"/><path d="M21 3v6h-6"/></svg>',
    plus: '<svg class="ic ic-sm" viewBox="0 0 24 24"><path d="M12 5v14M5 12h14"/></svg>',
    play: '<svg class="ic ic-xs" viewBox="0 0 24 24"><polygon points="6 4 20 12 6 20 6 4" fill="currentColor" stroke="none"/></svg>',
    more: '<svg class="ic ic-sm" viewBox="0 0 24 24"><circle cx="5" cy="12" r="1.4" fill="currentColor"/><circle cx="12" cy="12" r="1.4" fill="currentColor"/><circle cx="19" cy="12" r="1.4" fill="currentColor"/></svg>',
    edit: '<svg class="ic ic-sm" viewBox="0 0 24 24"><path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z"/></svg>',
    sync: '<svg class="ic ic-sm" viewBox="0 0 24 24"><path d="M3 12a9 9 0 0 1 15.5-6.3L21 8"/><path d="M21 3v5h-5"/><path d="M21 12a9 9 0 0 1-15.5 6.3L3 16"/><path d="M3 21v-5h5"/></svg>',
    monitor: '<svg class="ic ic-sm" viewBox="0 0 24 24"><rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8M12 17v4"/></svg>',
    trash: '<svg class="ic ic-sm" viewBox="0 0 24 24"><path d="M3 6h18"/><path d="M8 6V4h8v2"/><path d="M6 6l1 14h10l1-14"/></svg>',
    close: '<svg class="ic ic-sm" viewBox="0 0 24 24"><path d="M18 6 6 18M6 6l12 12"/></svg>',
    ext: '<svg class="ic ic-xs" viewBox="0 0 24 24"><path d="M14 4h6v6"/><path d="M10 14 20 4"/><path d="M18 13v6a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h6"/></svg>',
    crop: '<svg class="ic ic-sm" viewBox="0 0 24 24"><path d="M6 2v14a2 2 0 0 0 2 2h14"/><path d="M18 22V8a2 2 0 0 0-2-2H2"/></svg>',
    mic: '<svg class="ic ic-sm" viewBox="0 0 24 24" aria-hidden="true"><rect x="9" y="2" width="6" height="12" rx="3"/><path d="M5 10v2a7 7 0 0 0 14 0v-2M12 19v3M9 22h6"/></svg>',
    ledger: '<svg class="ic ic-sm" viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="3" width="15" height="18" rx="2"/><path d="M9 3v18m3-13h5m-5 4h5m-5 4h3"/></svg>',
    changelog: '<svg class="ic ic-sm" viewBox="0 0 24 24" aria-hidden="true"><path d="M6 3h8l4 4v14H6zM14 3v5h4M9 12h6m-6 4h5"/></svg>',
    chevronDown: '<svg class="ic ic-xs" viewBox="0 0 24 24" aria-hidden="true"><path d="m6 9 6 6 6-6"/></svg>',
    copy: '<svg class="ic ic-sm" viewBox="0 0 24 24"><rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>',
    folder: '<svg class="ic ic-sm" viewBox="0 0 24 24"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z"/></svg>',
    pin: '<svg class="ic ic-sm" viewBox="0 0 24 24"><path d="M12 17v5"/><path d="M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1Z"/></svg>',
    eye: '<svg class="ic ic-sm" viewBox="0 0 24 24"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z"/><circle cx="12" cy="12" r="3"/></svg>',
    eyeOff: '<svg class="ic ic-sm" viewBox="0 0 24 24"><path d="M9.88 9.88a3 3 0 1 0 4.24 4.24"/><path d="M10.73 5.08A10.4 10.4 0 0 1 12 5c6.5 0 10 7 10 7a13.2 13.2 0 0 1-1.67 2.68"/><path d="M6.61 6.61A13.5 13.5 0 0 0 2 12s3.5 7 10 7a9.7 9.7 0 0 0 5.39-1.61"/><line x1="2" y1="2" x2="22" y2="22"/></svg>',
    key: '<svg class="ic ic-sm" viewBox="0 0 24 24"><circle cx="7.5" cy="15.5" r="5.5"/><path d="m21 2-9.6 9.6"/><path d="m15.5 7.5 3 3L22 7l-3-3"/></svg>',
  };

  function esc(str) {
    return String(str ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function postAction(action, data = {}) {
    if (!webview) return;
    webview.postMessage({ action, data });
  }

  function callHost(action, data = {}, onProgress) {
    return new Promise((resolve, reject) => {
      if (!webview) return reject(new Error("WebView2 bridge unavailable"));
      const reqId = "r" + (reqSeq++);
      pendingReqs.set(reqId, { resolve, reject, onProgress });
      webview.postMessage({ action, reqId, data });
    });
  }

  function showToast(text, isError = false) {
    if (!text) return;
    const host = $("#toast-host");
    const el = document.createElement("div");
    el.className = "toast" + (isError ? " err" : "");
    el.textContent = text;
    host.appendChild(el);
    setTimeout(() => dismissLayer(el, () => el.remove()), 3000);
  }

  // ==================== DATE & COUNTDOWN ====================

  function toLocalInputValue(date) {
    const p = (n) => String(n).padStart(2, "0");
    return `${date.getFullYear()}-${p(date.getMonth() + 1)}-${p(date.getDate())}T${p(date.getHours())}:${p(date.getMinutes())}`;
  }

  function toIsoWithOffset(date) {
    const p = (n) => String(n).padStart(2, "0");
    const tz = -date.getTimezoneOffset();
    const sgn = tz >= 0 ? "+" : "-";
    const abs = Math.abs(tz);
    return `${date.getFullYear()}-${p(date.getMonth() + 1)}-${p(date.getDate())}T${p(date.getHours())}:${p(date.getMinutes())}:${p(date.getSeconds())}${sgn}${p(Math.floor(abs / 60))}:${p(abs % 60)}`;
  }

  function fmtDate(iso, shortMode = false) {
    if (!iso) return "—";
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    const p = (n) => String(n).padStart(2, "0");
    return shortMode
      ? `${p(d.getMonth() + 1)}/${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`
      : `${d.getFullYear()}/${p(d.getMonth() + 1)}/${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
  }

  function computeCountdown(acc) {
    if (!acc || acc.account_type !== "recycled" || !acc.recycle_at) return null;
    const endMs = new Date(acc.recycle_at).getTime();
    if (Number.isNaN(endMs)) return null;
    const savedStart = new Date(acc.started_at || "").getTime();
    const days = Number(acc.recycle_days);
    const startMs = Number.isFinite(savedStart) ? savedStart : endMs - (Number.isFinite(days) && days > 0 ? days : 25) * 86400000;
    const totalMs = Math.max(1000, endMs - startMs);
    const remainMs = endMs - Date.now();
    const expired = remainMs <= 0;
    const abs = Math.abs(remainMs);

    const d = Math.floor(abs / 86400000);
    const h = Math.floor((abs % 86400000) / 3600000);
    const m = Math.floor((abs % 3600000) / 60000);
    const s = Math.floor((abs % 60000) / 1000);
    const pct = expired ? 0 : Math.max(0, Math.min(100, (remainMs / totalMs) * 100));
    const level = (expired || remainMs <= 48 * 3600000) ? "danger" : (remainMs <= 5 * 86400000 ? "warn" : "ok");
    const p2 = (n) => String(n).padStart(2, "0");
    const compact = expired ? `已超期 ${d}天 ${p2(h)}:${p2(m)}:${p2(s)}` : `${d}天 ${p2(h)}时 ${p2(m)}分 ${p2(s)}秒`;
    const meterValue = d > 0 ? `${d}天 ${p2(h)}时` : (h > 0 ? `${h}时 ${p2(m)}分` : `${m}分 ${p2(s)}秒`);
    return { expired, pct, level, compact, meterValue, targetShort: fmtDate(acc.recycle_at, true) };
  }

  function recycleStatus(cd) {
    if (cd.expired) return "回收已超期";
    if (cd.level === "danger") return "临近回收";
    return cd.level === "warn" ? "即将回收" : "回收";
  }

  function renderGradientBar(pct, tone, fillAttr = "") {
    const clamped = Math.max(0, Math.min(100, Number(pct) || 0));
    return `<div class="grad-track"><div class="grad-fill tone-${tone}${clamped > 0 && clamped < 100 ? " is-partial" : ""}" ${fillAttr} style="width:${clamped}%"></div></div>`;
  }

  function planTagHtml(auth) {
    if (!auth || !auth.logged_in) return `<span class="tag tag-warn">待登录</span>`;
    const p = String(auth.plan_type || "").toLowerCase();
    if (p === "plus") return `<span class="tag tag-ok">Plus</span>`;
    if (p === "pro") return `<span class="tag tag-pro">Pro</span>`;
    if (p === "team" || p === "enterprise") return `<span class="tag tag-pro">${esc(p.toUpperCase())}</span>`;
    if (p === "free") return `<span class="tag">Free</span>`;
    return `<span class="tag">${esc(p || "已登录")}</span>`;
  }

  // ==================== RENDERING ====================

  function renderAll() {
    if (!appState) return;
    renderSidebarTools();

    const pending = !!appState.pending_update;
    const refreshingProjects = activeTab === "projects" && !!appState.projects_refreshing;
    const headerActions = $("#header-actions");
    const headerKey = `${pending}|${activeTab === "projects"}|${refreshingProjects}`;
    if (headerKey !== headerActions.dataset.key) {
      const parts = [];
      if (activeTab === "projects") {
        parts.push(iconButton("refreshProjects", "刷新项目 Git 状态", ICONS.refresh, { disabled: refreshingProjects, spinning: refreshingProjects }));
      }
      if (pending) {
        parts.push('<button class="btn btn-soft btn-sm" data-act="applyLocalUpdate">重启应用更新</button>');
      }
      headerActions.innerHTML = parts.join("");
      headerActions.dataset.key = headerKey;
    }
    $$(".nav-item").forEach((el) => {
      const selected = el.dataset.tab === activeTab;
      el.classList.toggle("active", selected);
      if (selected) el.setAttribute("aria-current", "page");
      else el.removeAttribute("aria-current");
    });

    if (activeTab === "workspace") renderUnifiedWorkspace();
    else if (activeTab === "projects") renderProjects();
    else if (activeTab === "backlog") renderBacklog();
    else renderManage();
  }

  function switchTab(tab) {
    closePopover();
    if (activeTab === tab) return;
    if (projectDrag) finishProjectDrag(false);
    activeTab = tab;
    expandedSettings.clear();
    renderAll();
    const view = $("#view-content");
    view.scrollTop = 0;
    if (!reducedMotion.matches) {
      view.classList.remove("view-entering");
      void view.offsetWidth;
      view.classList.add("view-entering");
      [...view.querySelectorAll(".surface-card")].slice(0, 4).forEach((card, index) => {
        card.style.setProperty("--card-delay", `${index * 25}ms`);
        card.classList.add("view-card-entering");
      });
    }
    if (tab === "manage") postAction("refreshInstalledApps");
    else if (tab === "projects") postAction("refreshProjects");
  }

  function shortcutLabel(hotkey) {
    const modifiers = { Ctrl: "⌃", Shift: "⇧", Win: "⊞", Alt: "⌥" };
    return hotkey.split("+").map((part) => {
      const key = part.trim();
      return modifiers[key] || key;
    }).join("");
  }

  function renderSidebarTools() {
    const shot = appState.screenshot;
    const shotKey = shot?.enabled && shot.registered ? shot.hotkey : "";
    const voiceKey = appState.voice_recorder_hotkey || "";
    const ledger = appState.quick_ledger_forms?.[0];
    const version = appState.version || "v5.5.2";
    const key = JSON.stringify([shotKey, voiceKey, ledger, version]);
    if (key === sidebarToolsKey) return;
    sidebarToolsKey = key;
    $("#sidebar-tools").innerHTML = `
      <h2>快捷工具</h2>
      <button class="btn btn-paper sidebar-tool" data-act="startScreenshot" title="${esc(shotKey || "截图")}">
        ${ICONS.crop}<span>截图</span>${shotKey ? `<kbd aria-label="${esc(shotKey)}">${esc(shortcutLabel(shotKey))}</kbd>` : ""}
      </button>
      <button class="btn btn-paper sidebar-tool" data-act="startVoiceRecorder" title="${esc(voiceKey || "AI 录音笔不可用")}" ${voiceKey ? "" : "disabled"}>
        ${ICONS.mic}<span>AI 录音笔</span>${voiceKey ? `<kbd aria-label="${esc(voiceKey)}">${esc(shortcutLabel(voiceKey))}</kbd>` : ""}
      </button>
      ${ledger ? `<button class="btn btn-paper sidebar-tool" data-act="openLedgerForm" data-id="${esc(ledger)}" title="打开飞书账本">
        ${ICONS.ledger}<span>记账</span><span class="sidebar-tool-external">${ICONS.ext}</span>
      </button>` : ""}
      <button class="btn btn-paper sidebar-tool" data-act="openChangelog">
        ${ICONS.changelog}<span>更新日志</span><kbd>${esc(version)}</kbd>
      </button>`;
  }

  function iconButton(action, label, glyph, { disabled = false, danger = false, spinning = false, attrs = "" } = {}) {
    return `<button type="button" class="btn icon-tool ${danger ? "btn-danger-outline" : "btn-ghost"}${spinning ? " is-spinning" : ""}"
      ${action ? `data-act="${action}"` : ""} title="${esc(label)}" aria-label="${esc(label)}" ${disabled ? "disabled" : ""} ${attrs}>${glyph}</button>`;
  }

  function statusHtml(label, tone = "") {
    return `<span class="status-inline ${tone}"><span class="status-dot"></span>${esc(label)}</span>`;
  }

  function toolHeading(id, icon, name, description, status = "", metadata = "") {
    return `<div class="tool-identity">
      ${icon ? `<img class="app-icon" src="${esc(icon)}" alt="" />` : `<span class="tool-icon-fallback" aria-hidden="true">${ICONS.monitor}</span>`}
      <div class="app-info">
        <div class="app-name-line"><h2 class="app-name" id="${id}">${esc(name)}</h2>${status}${metadata}</div>
        ${description ? `<div class="tool-purpose">${esc(description)}</div>` : ""}
      </div>
    </div>`;
  }

  function quotaTone(percent) {
    return percent <= 10 ? "danger" : (percent <= 30 ? "warn" : "ok");
  }

  function renderQuotaMeter({ title, value, when, percent, tone = "", labelAttrs = "", valueAttrs = "", fillAttrs = "", timeAttrs = "", action = "" }) {
    return `<div class="meter-block${action ? " meter-with-action" : ""}">
      <span class="meter-name" ${labelAttrs}>${esc(title)}</span>
      <strong class="meter-pct${tone ? ` tone-${tone}` : ""}" ${valueAttrs}>${esc(value)}</strong>
      <span class="meter-reset" ${timeAttrs}>${esc(when)}</span>
      ${action}
      ${percent == null ? '<div class="grad-track"></div>' : renderGradientBar(percent, tone, fillAttrs)}
    </div>`;
  }

  function quotaMessage(message, type = "", action = "") {
    return `<div class="quota-feedback ${type}" role="status"><span>${esc(message)}</span>${action}</div>`;
  }

  function updatedAtHtml(timestamp, automatic = false) {
    if (!timestamp) return "";
    const date = new Date(timestamp);
    if (Number.isNaN(date.getTime())) return "";
    const label = date.toDateString() === new Date().toDateString()
      ? date.toLocaleTimeString("zh-CN", { hour: "2-digit", minute: "2-digit", hour12: false })
      : fmtDate(timestamp, true);
    return `<time class="quota-updated" datetime="${esc(timestamp)}" title="${esc(fmtDate(timestamp))}">更新 ${esc(label)}${automatic ? " · 自动" : ""}</time>`;
  }

  function desktopActions(tool, name, launchAction, stopAction, startStyle = "btn-primary") {
    const id = esc(tool?.id);
    const attrs = `data-id="${id}"`;
    const running = !!tool?.running;
    return `<button class="btn ${running ? "btn-live" : startStyle} btn-sm" data-act="${launchAction}" ${attrs} ${id ? "" : "disabled"}>
      ${running ? "打开" : ICONS.play + " 启动"}</button>
      ${running ? iconButton(stopAction, "关闭 " + name, ICONS.power, { danger: true, attrs }) : ""}`;
  }

  function timeToReset(timestamp) {
    if (!timestamp) return "重置时间未知";
    const until = new Date(timestamp);
    if (Number.isNaN(until.getTime())) return "重置时间未知";
    const minutes = Math.ceil((until.getTime() - Date.now()) / 60000);
    if (minutes <= 0) return "已到重置时间";
    const days = Math.floor(minutes / 1440), hours = Math.floor(minutes / 60) % 24;
    if (days) return `${days}d ${hours}h 后重置`;
    if (hours) return `${hours}h ${minutes % 60}m 后重置`;
    return `${minutes}m 后重置`;
  }

  const webTools = [
    { id: "gemini", name: "Gemini", icon: "gemini.png" },
    { id: "ai-studio", name: "AI Studio", icon: "ai-studio.png" }
  ];
  const chosenIdentities = new Map();

  function webIdentity(tool, profiles) {
    let preferred = chosenIdentities.get(tool);
    if (preferred == null) {
      try { preferred = localStorage.getItem("work-identity-" + tool); } catch (_) {}
    }
    return profiles.find((profile) => profile.dir === preferred) || profiles[0];
  }

  function setWebIdentity(tool, identity) {
    chosenIdentities.set(tool, identity);
    try { localStorage.setItem("work-identity-" + tool, identity); } catch (_) {}
  }

  function renderWebTool(tool, profiles) {
    const profile = webIdentity(tool.id, profiles);
    const available = !!profile && appState.chrome_available !== false;
    return `<section class="surface-card tool-card compact-tool" aria-labelledby="tool-${tool.id}">
      <header class="tool-card-header">
        ${toolHeading("tool-" + tool.id, tool.icon, tool.name, "", statusHtml(available ? "可用" : "不可用", available ? "healthy" : "status-error"))}
        <div class="tool-actions">
          <button class="btn btn-paper btn-sm" data-act="openWebTool" data-id="${tool.id}" aria-label="打开 ${esc(tool.name)}" ${available ? "" : "disabled"}>打开 ${ICONS.ext}</button>
        </div>
      </header>
      <div class="tool-card-body">
        <div class="compact-identity">
          <span class="tool-identity-email" data-web-email="${tool.id}" title="${esc(profile?.email)}">${esc(profile?.email || "—")}</span>
          <select id="identity-${tool.id}" aria-label="${esc(tool.name)} Google 身份" data-web-identity="${tool.id}" ${available ? "" : "disabled"}>
            ${profiles.length ? profiles.map((item) => `<option value="${esc(item.dir)}" ${item.dir === profile?.dir ? "selected" : ""}>${esc(item.name)}</option>`).join("") : "<option>暂无可用身份</option>"}
          </select>
        </div>
      </div>
    </section>`;
  }

  function renderOpenDesign(tool) {
    const ready = !!tool;
    return `<section class="surface-card tool-card compact-tool" aria-labelledby="tool-open-design">
      <header class="tool-card-header">
        ${toolHeading("tool-open-design", tool?.icon_url, "Open Design", "",
          statusHtml(ready ? (tool.running ? "运行中" : "未运行") : "未安装", tool?.running ? "running" : ""))}
        <div class="tool-actions">
          ${desktopActions(tool, "Open Design", "launchTool", "stopTool", "btn-paper")}
        </div>
      </header>
      <div class="tool-card-body compact-purpose">设计构思与原型创作</div>
    </section>`;
  }

  function antigravityGroups(quota) {
    const groups = [
      { name: "Gemini Models", buckets: ["gemini-5h", "gemini-weekly"] },
      { name: "Claude and GPT Models", buckets: ["3p-5h", "3p-weekly"] }
    ];
    const additional = new Map();
    const known = new Set(groups.flatMap((group) => group.buckets));
    for (const key of Object.keys(quota)) {
      if (known.has(key)) continue;
      const model = key.replace(/-(?:wtu-)?(?:weekly|5h)$/, "");
      if (!additional.has(model)) additional.set(model, []);
      additional.get(model).push(key);
    }
    for (const [model, buckets] of additional) {
      const name = model === "gemini-argon" ? "Gemini 4 Argon" : model.split("-").map((part) => part.charAt(0).toUpperCase() + part.slice(1)).join(" ");
      groups.push({ name, buckets });
    }
    return groups;
  }

  function renderAntigravity() {
    const tool = appState.antigravity || {};
    const usage = tool.usage;
    const quota = usage?.quota || {};
    const groups = Object.keys(quota).length ? antigravityGroups(quota).map((group) => `
      <div class="antigravity-quota-group">
        <h3>${esc(group.name)}</h3>
        <div class="meter-grid">${group.buckets.map((key) => {
          const bucket = quota[key];
          const percent = typeof bucket?.remaining_fraction === "number" && !bucket.disabled
            ? Math.min(100, Math.max(0, bucket.remaining_fraction * 100)) : null;
          const title = key.endsWith("-weekly") ? "每周额度" : (key.endsWith("-5h") ? "5h 额度" : "额度");
          const value = percent != null ? percent.toLocaleString("zh-CN", { maximumFractionDigits: 2 }) + "%"
            : bucket?.disabled ? "不可用" : (typeof bucket?.remaining_amount === "number" ? Number(bucket.remaining_amount).toLocaleString("zh-CN") : "—");
          return renderQuotaMeter({ title, value, when: bucket ? timeToReset(bucket.reset_time) : "暂不可读取",
            percent, tone: percent == null ? "" : quotaTone(percent),
            fillAttrs: `data-quota-fill="antigravity:${esc(key)}"`,
            timeAttrs: bucket?.reset_time ? `title="${esc(fmtDate(bucket.reset_time))}"` : "" });
        }).join("")}</div>
      </div>`).join("") : "";
    const error = tool.error
      ? quotaMessage(tool.error, "quota-error", tool.cli_installed ? '<button class="btn btn-soft btn-sm" data-act="refreshAntigravity">重试</button>' : "")
      : "";
    const info = tool.loading ? quotaMessage("正在读取额度…")
      : (!groups && !error ? quotaMessage(tool.cli_installed ? "点击刷新额度" : "暂不可读取 · CLI 未安装") : "");
    return `<section class="surface-card tool-card" aria-labelledby="tool-antigravity">
      <header class="tool-card-header">
        ${toolHeading("tool-antigravity", tool.icon_url, "Antigravity", "",
          statusHtml(tool.id ? (tool.running ? "运行中" : "未运行") : "未安装", tool.running ? "running" : ""),
          `${usage?.plan ? `<span class="tag tag-pro">${esc(usage.plan)}</span>` : ""}
           ${usage?.email ? `<span class="tool-identity-email">${esc(usage.email)}</span>` : ""}`)}
        <div class="tool-actions">
          ${updatedAtHtml(usage?.fetched_at, tool.automatic)}
          <button class="btn btn-soft btn-sm${tool.loading ? " is-spinning" : ""}" data-act="refreshAntigravity"
            aria-busy="${!!tool.loading}" aria-label="刷新 Antigravity 额度" ${tool.loading || !tool.cli_installed ? "disabled" : ""}>${ICONS.refresh} 刷新额度</button>
          ${desktopActions(tool, "Antigravity", "launchTool", "stopTool")}
        </div>
      </header>
      <div class="tool-card-body">
        ${error}${info}
        ${groups ? `<div class="antigravity-quota-grid">${groups}</div>` : ""}
      </div>
    </section>`;
  }

  function findOpenDesign() {
    return (appState.tool_sections || []).flatMap((section) => section.items || []).find((tool) =>
      tool.id === "tool-opendesign" || (tool.uninstall_hint === "Open Design" && tool.kind !== "url"));
  }

  function networkDelayTone(state, delay) {
    if (state === "timeout" || state === "unavailable") return "is-danger";
    if (state !== "reachable" || delay == null) return "";
    return delay < 100 ? "is-ok" : (delay <= 180 ? "is-warning" : "is-danger");
  }

  function formatTraffic(bytes) {
    return (Math.max(0, bytes) / (1024 ** 3)).toFixed(2) + " GB";
  }

  function renderNetwork(proxy) {
    const network = appState.network || {};
    const state = proxy.running ? (network.state === "stopped" ? "unknown" : network.state) : "stopped";
    const down = ["stopped", "timeout", "unavailable"].includes(state);
    const label = state === "reachable" ? "网络正常" : (down ? "网络异常" : (state === "controller" ? "状态未知" : "网络待检测"));
    const tone = state === "reachable" ? "is-ok" : (down ? "is-danger" : "");
    const delayTone = networkDelayTone(state, network.delay);
    const latency = !proxy.running ? "—" : state === "timeout" ? "超时" : state === "unavailable" ? "不可用"
      : (network.delay != null && !network.delay_stale ? network.delay + " ms" : "—");
    const lastDelay = proxy.running && network.delay_stale ? " · 上次 " + network.delay + " ms" : "";
    const mode = ({ rule: "规则路由", global: "全局代理", direct: "直连" })[network.mode];
    const policy = proxy.running ? network.group || mode : "";
    const node = proxy.running ? network.node || "节点未知" : "代理未启动";
    const subscription = network.subscription;
    const traffic = subscription ? "剩余 " + formatTraffic(subscription.total_bytes - subscription.used_bytes) : "流量待获取";
    const trafficDetails = subscription
      ? ["NIVA Network", "已用 " + formatTraffic(subscription.used_bytes), "总量 " + formatTraffic(subscription.total_bytes),
        subscription.expires_at ? "到期 " + fmtDate(subscription.expires_at) : ""].filter(Boolean).join("\n")
      : "NIVA Network";
    const refreshLabel = networkBusy === "refresh" ? "刷新中…" : "刷新网络状态";
    const tools = [
      iconButton("refreshNetwork", refreshLabel, ICONS.refresh, { disabled: !proxy.running || !appState.network_status_refresh || !!networkBusy, spinning: networkBusy === "refresh" }),
      iconButton("openNetworkPicker", "切换节点", ICONS.switch, { disabled: !proxy.running || !network.can_select || !appState.network_japan_picker || !!networkBusy }),
      iconButton("proxyOpen", "打开 Clash Verge", ICONS.ext, { disabled: !proxy.installed && !proxy.running })
    ];
    const danger = [
      iconButton("proxyRestart", "重启代理", ICONS.sync, { danger: true, disabled: !proxy.running || !!networkBusy }),
      iconButton("proxyStop", "关闭代理", ICONS.power, { danger: true, disabled: !proxy.running || !!networkBusy, attrs: 'data-critical="true"' })
    ];

    return `
      <section class="surface-card network-card" aria-label="网络状态">
        <div class="network-data">
          <span class="network-cell network-status ${tone}"><span class="status-dot"></span>${label}</span>
          ${policy ? `<span class="network-cell network-policy">${esc(policy)}</span>` : ""}
          <span class="network-cell network-node-summary" title="${esc(node)}">${esc(node)}</span>
          <span class="network-cell network-latency ${delayTone}" title="当前节点延迟${esc(lastDelay)}">${esc(latency)}${lastDelay ? `<small>${esc(lastDelay)}</small>` : ""}</span>
          <span class="network-cell network-traffic" title="${esc(trafficDetails)}">${esc(traffic)}</span>
        </div>
        <div class="network-actions utility-toolbar" role="group" aria-label="网络工具栏">
          ${tools.join("")}
          <div class="danger-zone" role="group" aria-label="代理中断操作">${danger.join("")}</div>
        </div>
      </section>`;
  }

  function renderCodex(accounts) {
    const loading = accounts.some((account) => account.usage_loading);
    return `<section class="surface-card tool-card codex-tool" aria-labelledby="codex-tool-title">
      <header class="tool-card-header">
        ${toolHeading("codex-tool-title", "https://icons.aihub.local/chatgpt.png", "Codex", accounts.length + " 个账号")}
        <div class="tool-actions">
          <button class="btn btn-soft btn-sm${loading ? " is-spinning" : ""}" data-act="refreshAllUsage" aria-busy="${loading}" title="刷新所有 Codex 账号的额度">${ICONS.refresh} 刷新额度</button>
          <button class="btn btn-primary btn-sm" data-act="openAddAccount">${ICONS.plus} 添加账号</button>
        </div>
      </header>
      <div class="codex-accounts">${accounts.length ? accounts.map(renderCodexAccount).join("") : quotaMessage("暂无账号")}</div>
    </section>`;
  }

  function renderCodexAccount(acc) {
    const cd = computeCountdown(acc);
    const auth = acc.auth || {};
    const name = acc.is_default ? "主账号" : acc.name;
    const email = auth.logged_in ? (auth.email || auth.display_name || "已登录") : "尚未登录";
    const usage = acc.usage;
    const windows = usage?.success ? [["primary", usage.primary], ["secondary", usage.secondary]].filter(([, meter]) => meter) : [];
    let feedback = "";
    if (!auth.logged_in) feedback = quotaMessage("登录后可读取剩余额度");
    else if (auth.can_fetch_usage === false) feedback = quotaMessage("当前登录方式未提供额度");
    else if (acc.usage_loading) feedback = quotaMessage("正在查询额度…");
    else if (acc.usage_error) feedback = quotaMessage(acc.usage_error, "quota-error",
      `<button class="btn btn-soft btn-sm" data-act="refreshUsage" data-id="${esc(acc.id)}" aria-label="重试 ${esc(name)} 的额度查询">重试</button>`);
    else if (!windows.length) feedback = quotaMessage(usage ? "当前套餐暂无独立额度窗口" : "暂无额度数据");

    const meters = windows.map(([slot, w]) => {
      const percent = Math.max(0, Math.min(100, Number(w.left_percent) || 0));
      return renderQuotaMeter({ title: String(w.label).replace("限额", "额度"), value: percent + "%",
        when: w.reset_text || "重置时间未知", percent, tone: quotaTone(percent),
        valueAttrs: `aria-label="剩余 ${percent}%"`, fillAttrs: `data-quota-fill="codex:${esc(acc.id)}:${slot}"` });
    });
    const officialEnd = new Date(auth.subscription_until || "").getTime();
    const recycleEnd = new Date(acc.recycle_at || "").getTime();
    const needsCalibration = !!cd && !acc.is_default && Number.isFinite(officialEnd) && Number.isFinite(recycleEnd)
      && Math.abs(recycleEnd - (officialEnd - 5 * 86400000)) > 6 * 3600000;
    if (cd) meters.push(renderQuotaMeter({
      title: recycleStatus(cd), value: cd.meterValue, when: "截止 " + cd.targetShort,
      percent: cd.pct, tone: cd.level,
      labelAttrs: `data-cd-status="${esc(acc.id)}"`,
      valueAttrs: `data-cd-clock="${esc(acc.id)}" title="${esc(cd.compact)}"`,
      fillAttrs: `data-cd-fill="${esc(acc.id)}"`,
      timeAttrs: `title="${esc(fmtDate(acc.recycle_at))}"`,
      action: needsCalibration
        ? `<button class="btn btn-soft meter-calibrate" data-act="openEditAccount" data-id="${esc(acc.id)}" title="当前回收时间与到期前 5 天相差超过 6 小时">校准时间</button>` : ""
    }));

    return `<article class="codex-account">
      <header class="account-top">
        <div class="app-info">
          <div class="app-name-line">
            <h3 class="app-name account-name">${esc(name)}</h3>
            ${acc.is_default ? '<span class="tag">默认</span>' : ""}
            ${planTagHtml(auth)}
          </div>
          <div class="app-sub account-email">${esc(email)}</div>
        </div>
        <div class="account-actions">
          ${updatedAtHtml(usage?.fetched_at, usage?.automatic)}
          ${statusHtml(acc.running ? "运行中" : "未运行", acc.running ? "running" : "")}
          ${desktopActions(acc, name, "launchAccount", "stopAccount")}
          ${iconButton("openMoreMenu", "更多 " + name + " 操作", ICONS.more, { attrs: `data-id="${esc(acc.id)}"` })}
        </div>
      </header>
      <div class="account-usage">
        ${feedback}
        ${meters.length ? `<div class="meter-grid${!cd && windows.length === 1 ? " meter-grid-single" : ""}">${meters.join("")}</div>` : ""}
      </div>
    </article>`;
  }
  function renderUnifiedWorkspace() {
    const proxy = appState.proxy || {};
    const accounts = appState.accounts || [];
    const chromes = appState.chrome_profiles || [];
    const design = findOpenDesign();
    const running = Number(accounts.some((acc) => acc.running)) + Number(!!appState.antigravity?.running) + Number(!!design?.running);
    $("#view-title").textContent = "工作台";
    $("#view-sub").textContent = running ? `${running} 个工具运行中` : "";

    const view = $("#view-content");
    if (view.querySelector("[data-web-identity]:focus")) return;
    const network = appState.network || {};
    const key = JSON.stringify([
      proxy.running, proxy.installed, networkBusy, appState.network_status_refresh, appState.network_japan_picker,
      network.state, network.delay, network.delay_stale, network.mode, network.group, network.node, network.can_select,
      network.subscription?.used_bytes, network.subscription?.total_bytes, network.subscription?.expires_at,
      accounts.map(({ id, name, is_default, account_type, started_at, recycle_at, recycle_days, running, auth, usage, usage_loading, usage_error }) =>
        [id, name, is_default, account_type, started_at, recycle_at, recycle_days, running, auth, usage, usage_loading, usage_error]),
      appState.antigravity, Object.values(appState.antigravity?.usage?.quota || {}).map((bucket) => timeToReset(bucket?.reset_time)),
      design, appState.chrome_available, chromes
    ]);
    if (view.dataset.page === "workspace" && key === workspaceRenderKey) return;
    const previousQuotas = new Map();
    if (!reducedMotion.matches && view.dataset.page === "workspace") {
      for (const fill of view.querySelectorAll("[data-quota-fill]")) {
        const width = fill.parentElement.clientWidth;
        if (width) previousQuotas.set(fill.dataset.quotaFill, fill.getBoundingClientRect().width / width * 100);
      }
    }
    workspaceRenderKey = key;
    view.dataset.page = "workspace";
    view.innerHTML = `
      ${renderNetwork(proxy)}
      <div class="sec-heading"><h2 class="sec-title work-tools-title">AI 工作工具</h2></div>
      ${renderCodex(accounts)}
      ${renderAntigravity()}
      <div class="sec-heading"><h2 class="sec-title">快捷入口</h2></div>
      <div class="work-tools-grid">
        ${webTools.map((tool) => renderWebTool(tool, chromes)).join("")}
        ${renderOpenDesign(design)}
      </div>`;
    if (previousQuotas.size) animateQuotaWidths(view, previousQuotas);
  }

  function animateQuotaWidths(view, previousQuotas) {
    const changes = [];
    for (const fill of view.querySelectorAll("[data-quota-fill]")) {
      const previous = previousQuotas.get(fill.dataset.quotaFill);
      const target = fill.style.width;
      if (previous == null || Math.abs(previous - parseFloat(target)) < 0.5) continue;
      fill.style.width = `${previous}%`;
      changes.push([fill, target]);
    }
    if (!changes.length) return;
    void view.offsetWidth;
    requestAnimationFrame(() => {
      for (const [fill, target] of changes) {
        if (fill.isConnected) fill.style.width = target;
      }
    });
  }

  function settingSwitch(key, label, checked) {
    return `<label class="toggle">
      <input type="checkbox" data-setting="${key}" aria-label="${esc(label)}" ${checked ? "checked" : ""} />
      <span class="toggle-track"></span><span class="toggle-thumb"></span>
    </label>`;
  }

  function settingsDisclosure(id, label) {
    const open = expandedSettings.has(id);
    return `<button class="btn btn-soft btn-sm settings-disclosure" data-act="toggleSettingsDetails" data-id="${id}"
      data-collapsed-label="${label}" aria-controls="${id}" aria-expanded="${open}"><span>${open ? "收起" : label}</span>${ICONS.chevronDown}</button>`;
  }

  function disclosurePanel(id, open, content) {
    return `<div id="${id}" class="disclosure-panel${open ? " is-expanded" : ""}" ${open ? "" : "inert"}>
      <div class="disclosure-content">${content}</div>
    </div>`;
  }

  function toggleDisclosure(button) {
    const panel = document.getElementById(button.getAttribute("aria-controls"));
    if (!panel) return null;
    const open = panel.classList.toggle("is-expanded", !panel.classList.contains("is-expanded"));
    panel.inert = !open;
    button.setAttribute("aria-expanded", String(open));
    return open;
  }

  function syncSettingsFields(view, settings, shot, shortcuts) {
    const values = { ...settings, screenshot_enabled: !!shot.enabled, quota_auto_refresh: settings.quota_auto_refresh !== false };
    for (const input of view.querySelectorAll("[data-setting]")) input.checked = !!values[input.dataset.setting];
    for (const button of view.querySelectorAll("[data-hk]")) {
      const active = button.dataset.hk === shot.hotkey;
      button.classList.toggle("active", active);
      button.setAttribute("aria-pressed", String(active));
    }
    const status = view.querySelector("#settings-hotkey-status");
    status.className = "tag" + (shot.enabled ? shot.registered ? " tag-ok" : " tag-warn" : "");
    status.textContent = !shot.enabled ? "快捷键已关闭" : shot.registered ? "热键已生效" : "热键被占用";
    const alert = view.querySelector("#settings-hotkey-alert");
    alert.hidden = !(shot.enabled && !shot.registered && shot.error);
    alert.querySelector("span").textContent = shot.error || "";

    const byId = new Map(shortcuts.map((shortcut) => [shortcut.id, shortcut]));
    for (const input of view.querySelectorAll("[data-stash-id]")) {
      const hidden = !!byId.get(input.dataset.stashId)?.hidden;
      input.checked = hidden;
      const label = input.closest(".stash-right").querySelector(".stash-state");
      label.classList.toggle("hidden-on", hidden);
      label.textContent = hidden ? "已隐藏" : "桌面显示中";
    }
    const hiddenCount = shortcuts.filter((shortcut) => shortcut.hidden).length;
    view.querySelector("#stash-count").textContent = `已隐藏 ${hiddenCount}/${shortcuts.length}`;
    view.querySelector('[data-act="hideAllAiShortcuts"]').disabled = !shortcuts.some((shortcut) => shortcut.category === "ai" && !shortcut.hidden);
    view.querySelector('[data-act="restoreAllShortcuts"]').disabled = hiddenCount === 0;
  }

  // ==================== PROJECTS PAGE ====================

  function projectVisualTheme(item) {
    const key = `${item.name || ""} ${item.folder_name || ""}`.toLowerCase();
    if (key.includes("ai hub") || key.includes("ai-hub")) {
      return {
        tint: "tint-slate",
        svg: '<svg class="ic" viewBox="0 0 24 24"><rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><path d="M14 17.5h7M17.5 14v7"/></svg>',
      };
    }
    if (key.includes("astra") || key.includes("unknown")) {
      return {
        tint: "tint-amber",
        svg: '<svg class="ic" viewBox="0 0 24 24"><circle cx="6.5" cy="7" r="2.8"/><circle cx="17.5" cy="6.5" r="2.5"/><circle cx="12" cy="17.5" r="3"/><path d="M9 8.2 15 6.8M8 9.4l2.7 5.5M16.2 8.8l-2.6 6"/></svg>',
      };
    }
    if (key.includes("留学") || key.includes("study")) {
      return {
        tint: "tint-mint",
        svg: '<svg class="ic" viewBox="0 0 24 24"><path d="M12 3 2 8l10 5 10-5-10-5Z"/><path d="M6 10.5V16c0 1.8 2.7 3.5 6 3.5s6-1.7 6-3.5v-5.5"/><path d="M21 9v6"/></svg>',
      };
    }
    if (key.includes("runtime")) {
      return {
        tint: "tint-sky",
        svg: '<svg class="ic" viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="16" rx="2.5"/><path d="m8 9 3 3-3 3M13 15h3"/></svg>',
      };
    }
    if (key.includes("portfolio")) {
      return {
        tint: "tint-violet",
        svg: '<svg class="ic" viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 9h18M9 9v11"/></svg>',
      };
    }
    if (key.includes("ledger") || key.includes("life")) {
      return {
        tint: "tint-amber",
        svg: '<svg class="ic" viewBox="0 0 24 24"><rect x="4" y="3" width="16" height="18" rx="2"/><path d="M8 3v18M12 8h4M12 12h4M12 16h2.5"/></svg>',
      };
    }
    return {
      tint: "tint-slate",
      svg: '<svg class="ic" viewBox="0 0 24 24"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z"/></svg>',
    };
  }

  function compactPathLabel(fullPath) {
    if (!fullPath) return "";
    const parts = String(fullPath).split(/[\\/]+/).filter(Boolean);
    if (parts.length <= 3) return fullPath;
    return `${parts[0]}\\...\\${parts.slice(-2).join("\\")}`;
  }

  function renderProjectCardsHtml(items) {
    if (items.length === 0) {
      return '<section class="surface-card"><div class="backlog-empty">暂无登记项目，可让 AI 助手添加到本地项目列表。</div></section>';
    }
    return `<div class="projects-grid">${items.map((item) => {
      const theme = projectVisualTheme(item);

      let stampClass = "st-plain";
      let stampText = "普通目录";
      if (!item.exists) {
        stampClass = "st-danger";
        stampText = "路径缺失";
      } else if (item.is_git) {
        if (item.dirty_count > 0) {
          stampClass = "st-warn";
          stampText = `${item.dirty_count} 处改动`;
        } else {
          stampClass = "st-ok";
          stampText = "干净";
        }
      }

      const branchTag = item.is_git
        ? `<span class="tag tag-pro">${esc(item.branch || "HEAD")}</span>`
        : '<span class="tag">本地目录</span>';
      const descText = item.notes || (item.is_git ? "本地 Git 仓库，可快速复制路径指给 AI 助手处理。" : "本地工作目录，支持快速复制路径与打开目录。");

      const previewState = appState.project_previews?.[item.id];
      const previewBusy = previewState === "busy";
      const previewRunning = previewState === "running";
      const previewTip = item.preview_port ? "本地预览 · 端口 " + item.preview_port : "运行项目的启动预览脚本";
      const previewBtn = item.has_preview
        ? `<button class="btn btn-live btn-sm${previewBusy ? " is-spinning" : ""}" data-act="launchProjectPreview" data-id="${esc(item.id)}" title="${esc(previewTip)}" ${previewBusy ? 'disabled aria-busy="true"' : ""}>${previewBusy ? ICONS.refresh : ICONS.play}<span>${previewBusy ? "处理中" : previewRunning ? "打开" : "预览"}</span></button>`
        : ((item.is_self || item.id === "prj-ai-hub" || String(item.folder_name || "").toLowerCase() === "ai-hub")
          ? '<span class="project-self-pill" title="当前正在运行的桌面软件本体"><span class="project-self-dot" aria-hidden="true"></span><span>本体运行中</span></span>'
          : "");
      const stopPreviewBtn = previewRunning
        ? `<button class="btn btn-danger-outline btn-sm project-stop-preview" data-act="stopProjectPreview" data-id="${esc(item.id)}" title="停止预览" aria-label="停止 ${esc(item.name)} 预览"><svg class="ic ic-sm" viewBox="0 0 24 24" aria-hidden="true"><rect x="7" y="7" width="10" height="10" rx="1.5"/></svg></button>`
        : "";
      const remoteBtn = item.remote_url
        ? iconButton("openProjectRemote", "打开 GitHub 仓库", ICONS.ext, { attrs: `data-url="${esc(item.remote_url)}"` })
        : "";

      return `<article class="project-card" data-project-id="${esc(item.id)}">
        <header class="project-card-head" title="按住卡头可拖动排序">
          <div class="project-card-lead">
            <div class="project-emblem ${theme.tint}">${theme.svg}</div>
            <div class="project-head-meta">
              <div class="project-name-row">
                <span class="project-name" title="${esc(item.name)}">${esc(item.name)}</span>
              </div>
              <div class="project-sub-tags">${branchTag}</div>
            </div>
          </div>
          <div class="project-head-actions">
            <span class="project-stamp ${stampClass}">${esc(stampText)}</span>
            <button type="button" class="project-grip" data-project-grip aria-label="调整 ${esc(item.name)} 的顺序" title="按住拖动，或聚焦后用方向键排序"><svg class="ic ic-sm" viewBox="0 0 24 24" aria-hidden="true"><path d="M9 5h.01M15 5h.01M9 12h.01M15 12h.01M9 19h.01M15 19h.01" stroke-width="3" stroke-linecap="round"/></svg></button>
          </div>
        </header>

        <div class="project-card-body">
          <p class="project-desc" title="${esc(descText)}">${esc(descText)}</p>
          <div class="project-path-pill" data-act="copyProjectPath" data-path="${esc(item.path)}" title="点击复制完整路径：${esc(item.path)}">
            ${ICONS.folder}
            <span class="project-path">${esc(compactPathLabel(item.path))}</span>
          </div>
        </div>

        <footer class="project-card-foot">
          <div class="project-foot-left">
            <button class="btn btn-paper btn-sm" data-act="copyProjectPath" data-path="${esc(item.path)}" title="复制本地路径，方便贴给 AI 助手">${ICONS.copy}<span>复制路径</span></button>
            ${previewBtn}
            ${stopPreviewBtn}
          </div>
          <div class="project-foot-right">
            ${iconButton("openProjectFolder", "打开文件夹", ICONS.folder, { attrs: `data-path="${esc(item.path)}"` })}
            ${remoteBtn}
            ${iconButton("editProjectItem", "编辑项目名称与介绍", ICONS.edit, { attrs: `data-id="${esc(item.id)}" data-path="${esc(item.path)}"` })}
          </div>
        </footer>
      </article>`;
    }).join("")}</div>`;
  }

  function renderProjects() {
    const items = (appState.projects || []).filter((it) => !it.hidden);
    $("#view-title").textContent = "项目";
    $("#view-sub").textContent = items.length ? `共 ${items.length} 个项目` : "暂无登记项目";

    const view = $("#view-content");
    if ((projectDrag || projectDrop || savingProjectOrder) && view.dataset.page === "projects") return;
    const key = JSON.stringify([[...items].sort((a, b) => a.id.localeCompare(b.id)), appState.project_previews]);
    if (key === projectsRenderKey && view.dataset.page === "projects") {
      const grid = view.querySelector(".projects-grid");
      if (grid && items.some((item, i) => grid.children[i]?.dataset.projectId !== item.id)) {
        const cards = new Map([...grid.children].map(card => [card.dataset.projectId, card]));
        rearrangeProjectCards(grid, () => grid.append(...items.map(item => cards.get(item.id))));
      }
      return;
    }
    projectsRenderKey = key;
    view.dataset.page = "projects";

    view.innerHTML = `<div class="projects-page">${renderProjectCardsHtml(items)}</div>`;
  }

  function projectCardIds(grid) {
    return [...grid.children].map(card => card.dataset.projectId);
  }

  function rearrangeProjectCards(grid, change) {
    const before = new Map([...grid.children].map(card => [card, card.getBoundingClientRect()]));
    for (const card of grid.children) projectMoves.get(card)?.cancel();
    change();
    if (reducedMotion.matches) return;
    for (const card of grid.children) {
      if (card.classList.contains("project-drag-source")) continue;
      const after = card.getBoundingClientRect(), first = before.get(card);
      const dx = first.left - after.left, dy = first.top - after.top;
      if (Math.abs(dx) + Math.abs(dy) < 1) continue;
      projectMoves.set(card, card.animate(
        [{ transform: `translate(${dx}px, ${dy}px)` }, { transform: "translate(0, 0)" }],
        { duration: 200, easing: "cubic-bezier(0.22, 1, 0.36, 1)" }
      ));
    }
  }

  async function persistProjectOrder(ids, focusId) {
    savingProjectOrder = true;
    try {
      await callHost("reorderProjects", { ids });
    } catch {
      projectsRenderKey = undefined; // restore canonical order if saving failed
    } finally {
      savingProjectOrder = false;
      if (activeTab === "projects") {
        renderProjects();
        if (focusId) [...$$(".project-card")].find(card => card.dataset.projectId === focusId)
          ?.querySelector("[data-project-grip]")?.focus({ preventScroll: true });
      }
    }
  }

  function moveDraggedProject() {
    const drag = projectDrag;
    if (!drag) return;
    drag.frame = 0;
    if (!drag.ghost) {
      if (Math.hypot(drag.x - drag.startX, drag.y - drag.startY) < 5) return;
      const bounds = drag.card.getBoundingClientRect();
      drag.left = bounds.left;
      drag.top = bounds.top;
      drag.ghost = drag.card.cloneNode(true);
      drag.ghost.classList.add("project-drag-ghost");
      drag.ghost.setAttribute("aria-hidden", "true");
      drag.ghost.inert = true;
      drag.ghost.style.width = bounds.width + "px";
      drag.ghost.style.height = bounds.height + "px";
      document.body.append(drag.ghost);
      drag.card.classList.add("project-drag-source");
      drag.grid.classList.add("is-reordering");
    }
    const x = drag.left + drag.x - drag.startX;
    const y = drag.top + drag.y - drag.startY;
    drag.ghost.style.transform = `translate3d(${x}px, ${y}px, 0) scale(1.02) rotate(-0.5deg)`;
    const view = $("#view-content");
    const viewport = view.getBoundingClientRect();
    const overView = drag.x >= viewport.left && drag.x <= viewport.right;
    const scroll = !overView ? 0 : drag.y < viewport.top + 54 ? -9 : drag.y > viewport.bottom - 54 ? 9 : 0;
    const beforeScroll = view.scrollTop;
    if (scroll) view.scrollTop += scroll;
    const gridBox = drag.grid.getBoundingClientRect();
    // Hit-test the grid cells (including half-gap padding) without in-flight FLIP transforms.
    const cards = [...drag.grid.children];
    const target = cards.find(card => {
      const left = gridBox.left + card.offsetLeft, top = gridBox.top + card.offsetTop;
      return drag.x >= left - 7 && drag.x <= left + card.offsetWidth + 7 &&
        drag.y >= top - 7 && drag.y <= top + card.offsetHeight + 7;
    });
    if (target && target !== drag.card && overView && drag.y >= viewport.top && drag.y <= viewport.bottom) {
      const after = cards.indexOf(target) > cards.indexOf(drag.card);
      rearrangeProjectCards(drag.grid, () => drag.grid.insertBefore(drag.card, after ? target.nextSibling : target));
    }
    if (view.scrollTop !== beforeScroll) drag.frame = requestAnimationFrame(moveDraggedProject);
  }

  function finishProjectDrag(save) {
    const drag = projectDrag;
    if (!drag) return;
    projectDrag = null;
    cancelAnimationFrame(drag.frame);
    if (drag.grid.hasPointerCapture(drag.pointer)) drag.grid.releasePointerCapture(drag.pointer);
    if (!drag.ghost) { if (activeTab === "projects") renderProjects(); return; }
    if (!save) rearrangeProjectCards(drag.grid, () => drag.grid.append(...drag.before));
    const ids = projectCardIds(drag.grid);
    const changed = ids.some((id, index) => id !== drag.before[index].dataset.projectId);
    const target = drag.card.getBoundingClientRect();
    projectDrop = true;
    if (save && changed) persistProjectOrder(ids);
    const finish = () => {
      drag.ghost.remove();
      drag.card.classList.remove("project-drag-source");
      drag.grid.classList.remove("is-reordering");
      projectDrop = false;
      if (activeTab === "projects") renderProjects();
    };
    if (!reducedMotion.matches) {
      drag.ghost.animate([
        { transform: drag.ghost.style.transform, opacity: 0.98 },
        { transform: `translate3d(${target.left}px, ${target.top}px, 0) scale(1) rotate(0deg)`, opacity: 1 },
      ], { duration: 170, easing: "cubic-bezier(0.22, 1, 0.36, 1)", fill: "forwards" }).finished.then(finish, finish);
    } else finish();
  }

  document.addEventListener("pointerdown", event => {
    const head = event.target.closest("[data-project-grip], .project-card-head");
    if (!head || event.button !== 0 || !event.isPrimary || projectDrag || projectDrop || savingProjectOrder) return;
    if (event.target.closest("button:not([data-project-grip]), a, input, select")) return;
    const card = head.closest(".project-card"), grid = card?.parentElement;
    if (!card || !grid || grid.childElementCount < 2) return;
    const grip = card.querySelector("[data-project-grip]");
    if (grip && event.target.closest("[data-project-grip]")) grip.focus({ preventScroll: true });
    grid.setPointerCapture(event.pointerId);
    projectDrag = { card, grid, before: [...grid.children], pointer: event.pointerId, frame: 0,
      startX: event.clientX, startY: event.clientY, x: event.clientX, y: event.clientY };
    event.preventDefault();
  });

  document.addEventListener("pointermove", event => {
    if (!projectDrag || event.pointerId !== projectDrag.pointer) return;
    projectDrag.x = event.clientX;
    projectDrag.y = event.clientY;
    if (!projectDrag.frame) projectDrag.frame = requestAnimationFrame(moveDraggedProject);
  });

  document.addEventListener("pointerup", event => {
    if (projectDrag?.pointer !== event.pointerId) return;
    cancelAnimationFrame(projectDrag.frame);
    projectDrag.x = event.clientX;
    projectDrag.y = event.clientY;
    moveDraggedProject();
    finishProjectDrag(true);
  });
  document.addEventListener("pointercancel", event => {
    if (projectDrag?.pointer === event.pointerId) finishProjectDrag(false);
  });
  document.addEventListener("lostpointercapture", event => {
    if (projectDrag?.pointer === event.pointerId) finishProjectDrag(false);
  });
  window.addEventListener("blur", () => { if (projectDrag) finishProjectDrag(false); });
  document.addEventListener("keydown", event => {
    if (event.key === "Escape" && projectDrag) { finishProjectDrag(false); return; }
    const grip = event.target.closest("[data-project-grip]");
    if (!grip || projectDrag || projectDrop || savingProjectOrder || !event.key.startsWith("Arrow")) return;
    const card = grip.closest(".project-card"), grid = card.parentElement;
    const cards = [...grid.children], index = cards.indexOf(card);
    const columns = cards.filter(peer => peer.offsetTop === cards[0].offsetTop).length;
    const step = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -columns, ArrowDown: columns }[event.key];
    const target = cards[index + step];
    if (!target) return;
    event.preventDefault();
    rearrangeProjectCards(grid, () => grid.insertBefore(card, step > 0 ? target.nextSibling : target));
    persistProjectOrder(projectCardIds(grid), card.dataset.projectId);
  });

  function openProjectModal(editItem) {
    const isEdit = !!editItem;
    const isScanned = editItem?.source === "scanned";
    openModal(`
      <div class="modal-header">
        <h3>${isEdit ? `编辑项目 · ${esc(editItem.name)}` : "添加项目目录"}</h3>
        <button class="btn btn-ghost btn-icon-only" data-close-modal>${ICONS.close}</button>
      </div>
      <form id="f-project" class="modal-body">
        <div class="field">
          <label>项目名称</label>
          <input id="prj-name" type="text" required placeholder="例如：AI Hub" value="${esc(editItem?.name || "")}" />
        </div>
        <div class="field">
          <label>本地项目路径</label>
          <div class="path-input-row">
            <input id="prj-path" type="text" required placeholder="例如：D:\\Projects\\life-hub" value="${esc(editItem?.path || "")}" ${isScanned ? "readonly" : ""} />
            ${isScanned ? "" : '<button type="button" id="prj-browse" class="btn btn-paper btn-sm">浏览...</button>'}
          </div>
        </div>
        <div class="field">
          <label>项目介绍</label>
          <textarea id="prj-notes" rows="4" placeholder="介绍这个项目是干嘛的、有什么主要功能...">${esc(editItem?.notes || "")}</textarea>
        </div>
      </form>
      <div class="modal-footer">
        ${isEdit && !isScanned ? '<button type="button" id="prj-remove" class="btn btn-danger-outline" style="margin-right:auto;">移除</button>' : ""}
        <button class="btn btn-soft" data-close-modal>取消</button>
        <button id="prj-submit" type="submit" form="f-project" class="btn btn-primary">${isEdit ? "保存修改" : "添加项目"}</button>
      </div>
    `);

    $("#prj-browse")?.addEventListener("click", async () => {
      try {
        const picked = await callHost("browseFolder", { title: "选择项目目录", initial: $("#prj-path").value });
        if (picked) {
          $("#prj-path").value = picked;
          if (!$("#prj-name").value.trim()) {
            const parts = picked.replace(/[\\/]+$/, "").split(/[\\/]/);
            $("#prj-name").value = parts[parts.length - 1] || "";
          }
        }
      } catch (err) { showToast(err.message, true); }
    });

    $("#prj-remove")?.addEventListener("click", async () => {
      try {
        await callHost("removeProjectItem", { id: editItem.id, path: editItem.path });
        closeModal();
      } catch (err) { showToast(err.message, true); }
    });

    $("#f-project").addEventListener("submit", async (e) => {
      e.preventDefault();
      const btn = $("#prj-submit");
      btn.disabled = true;
      try {
        await callHost("saveProjectItem", {
          id: editItem?.id || "",
          path: $("#prj-path").value.trim(),
          name: $("#prj-name").value.trim(),
          notes: $("#prj-notes").value,
          pinned: false,
          hidden: false,
        });
        closeModal();
      } catch (err) {
        btn.disabled = false;
        showToast(err.message, true);
      }
    });
  }

  // ==================== BACKLOG PAGE ====================

  const BACKLOG_PROJECTS = ["AI Hub", "Life Hub", "通用"];
  const BACKLOG_STATUSES = [
    { id: "idea", label: "构思中" },
    { id: "planned", label: "已排期" },
    { id: "doing", label: "进行中" },
    { id: "done", label: "已完成" },
    { id: "paused", label: "搁置" },
  ];
  const BACKLOG_PRIORITIES = {
    high: { label: "高", cls: "tag-danger", rank: 0 },
    medium: { label: "中", cls: "tag-warn", rank: 1 },
    low: { label: "低", cls: "", rank: 2 },
  };
  const BACKLOG_STATUS_RANK = { doing: 0, planned: 1, idea: 2, paused: 3, done: 4 };

  function filterBacklogItems(items) {
    const q = backlogQuery.trim().toLowerCase();
    return [...items]
      .filter((it) => {
        if (backlogProject !== "all" && it.project !== backlogProject) return false;
        if (backlogStatus !== "all" && it.status !== backlogStatus) return false;
        if (q && !`${it.title || ""} ${it.notes || ""} ${it.project || ""}`.toLowerCase().includes(q)) return false;
        return true;
      })
      .sort((a, b) => {
        const sr = (BACKLOG_STATUS_RANK[a.status] ?? 9) - (BACKLOG_STATUS_RANK[b.status] ?? 9);
        if (sr !== 0) return sr;
        const pr = (BACKLOG_PRIORITIES[a.priority]?.rank ?? 9) - (BACKLOG_PRIORITIES[b.priority]?.rank ?? 9);
        if (pr !== 0) return pr;
        return String(b.updated_at || "").localeCompare(String(a.updated_at || ""));
      });
  }

  function renderBacklogListHtml(filtered) {
    if (filtered.length === 0) {
      return '<div class="backlog-empty-card"><div class="backlog-empty">当前筛选条件下暂无需求，点击右上角「+ 新建需求」记录想法。</div></div>';
    }
    return `<div class="backlog-list">${filtered.map((item) => {
      const prio = BACKLOG_PRIORITIES[item.priority] || BACKLOG_PRIORITIES.medium;
      const expanded = expandedBacklog.has(item.id);
      const preview = String(item.notes || "").replace(/\s+/g, " ").trim();
      const projCls = item.project === "AI Hub" ? "tag-pro" : item.project === "Life Hub" ? "tag-ok" : "";
      const statusOpts = BACKLOG_STATUSES.map((st) =>
        `<option value="${st.id}"${item.status === st.id ? " selected" : ""}>${st.label}</option>`
      ).join("");
      return `<article class="backlog-item${item.status === "done" ? " is-done" : ""}${expanded ? " is-expanded" : ""}" data-backlog-id="${esc(item.id)}">
        <div class="backlog-row">
          <div class="backlog-row-main" data-act="toggleBacklogDetail" data-id="${esc(item.id)}">
            <span class="backlog-expand-ic">${ICONS.chevronDown}</span>
            <span class="tag ${prio.cls}">${prio.label}</span>
            <span class="tag ${projCls}">${esc(item.project || "通用")}</span>
            <span class="backlog-title" title="${esc(item.title)}">${esc(item.title)}</span>
            ${preview ? `<span class="backlog-preview">${esc(preview)}</span>` : ""}
          </div>
          <div class="backlog-row-side">
            <select class="backlog-inline-status st-${esc(item.status || "idea")}" data-backlog-status="${esc(item.id)}" aria-label="修改状态">${statusOpts}</select>
            ${iconButton("copyBacklogPrompt", "复制需求 Prompt 给 AI", ICONS.copy, { attrs: `data-id="${esc(item.id)}"` })}
            ${iconButton("editBacklogItem", "编辑需求", ICONS.edit, { attrs: `data-id="${esc(item.id)}"` })}
            ${iconButton("confirmDeleteBacklog", "删除需求", ICONS.trash, { danger: true, attrs: `data-id="${esc(item.id)}"` })}
          </div>
        </div>
        ${expanded ? `<div class="backlog-detail">
          <p class="backlog-notes">${esc(item.notes || "暂无详细说明，点击右侧编辑补充讨论要点。")}</p>
          <div class="backlog-detail-foot">
            <span>创建 ${esc(fmtDate(item.created_at))} · 更新 ${esc(fmtDate(item.updated_at))}</span>
            <div style="display:flex;gap:6px;">
              <button class="btn btn-paper btn-sm" data-act="copyBacklogPrompt" data-id="${esc(item.id)}">复制给 AI</button>
              <button class="btn btn-soft btn-sm" data-act="editBacklogItem" data-id="${esc(item.id)}">编辑内容</button>
            </div>
          </div>
        </div>` : ""}
      </article>`;
    }).join("")}</div>`;
  }

  function renderBacklog(forceListOnly = false) {
    const items = appState.backlog_items || [];
    const openCount = items.filter((it) => it.status !== "done" && it.status !== "paused").length;
    $("#view-title").textContent = "需求池";
    $("#view-sub").textContent = items.length ? `共 ${items.length} 条 · ${openCount} 条待推进` : "记录灵感与待办开发事项";

    const view = $("#view-content");
    const filtered = filterBacklogItems(items);
    const key = JSON.stringify([items, backlogProject, backlogStatus, backlogQuery, [...expandedBacklog]]);

    if (forceListOnly && view.dataset.page === "backlog") {
      const listCard = $("#backlog-list-card");
      if (listCard) listCard.innerHTML = renderBacklogListHtml(filtered);
      backlogRenderKey = key;
      return;
    }

    if (key === backlogRenderKey && view.dataset.page === "backlog") return;
    backlogRenderKey = key;
    view.dataset.page = "backlog";

    const counts = { all: items.length };
    for (const p of BACKLOG_PROJECTS) counts[p] = items.filter((it) => it.project === p).length;

    const pillTabs = ["all", ...BACKLOG_PROJECTS].map((p) => {
      const label = p === "all" ? "全部" : p;
      return `<button type="button" class="backlog-pill${backlogProject === p ? " active" : ""}" data-act="filterBacklogProject" data-project="${esc(p)}">${esc(label)}<span>${counts[p] || 0}</span></button>`;
    }).join("");

    const statusFilterOpts = [
      `<option value="all"${backlogStatus === "all" ? " selected" : ""}>全部状态</option>`,
      ...BACKLOG_STATUSES.map((st) => `<option value="${st.id}"${backlogStatus === st.id ? " selected" : ""}>${st.label}</option>`)
    ].join("");

    view.innerHTML = `<div class="backlog-page">
      <section class="backlog-toolbar">
        <div class="backlog-filters">
          <div class="backlog-pills">${pillTabs}</div>
          <select id="backlog-filter-status" class="backlog-status-select" aria-label="按状态筛选">${statusFilterOpts}</select>
          <input id="backlog-search" class="backlog-search" type="text" placeholder="搜索需求或备注..." value="${esc(backlogQuery)}" />
        </div>
        <div class="backlog-actions">
          <button class="btn btn-paper btn-sm" data-act="exportBacklogMd"${filtered.length ? "" : " disabled"}>导出 Markdown</button>
          <button class="btn btn-primary btn-sm" data-act="openAddBacklog">${ICONS.plus}<span>新建需求</span></button>
        </div>
      </section>
      <div id="backlog-list-card">${renderBacklogListHtml(filtered)}</div>
    </div>`;
  }

  function openBacklogModal(editItem) {
    const isEdit = !!editItem;
    const projectOpts = BACKLOG_PROJECTS.map((p) =>
      `<option value="${esc(p)}"${(editItem?.project || (backlogProject !== "all" ? backlogProject : "Life Hub")) === p ? " selected" : ""}>${esc(p)}</option>`
    ).join("");
    const statusOpts = BACKLOG_STATUSES.map((st) =>
      `<option value="${st.id}"${(editItem?.status || "idea") === st.id ? " selected" : ""}>${st.label}</option>`
    ).join("");
    const priorityOpts = Object.entries(BACKLOG_PRIORITIES).map(([k, v]) =>
      `<option value="${k}"${(editItem?.priority || "medium") === k ? " selected" : ""}>${v.label}优先级</option>`
    ).join("");

    openModal(`
      <div class="modal-header">
        <h3>${isEdit ? "编辑需求" : "新建需求"}</h3>
        <button class="btn btn-ghost btn-icon-only" data-close-modal>${ICONS.close}</button>
      </div>
      <form id="f-backlog" class="modal-body">
        <div class="field">
          <label>需求标题</label>
          <input id="bl-title" type="text" required placeholder="一句话描述想做的模块或改进..." value="${esc(editItem?.title || "")}" />
        </div>
        <div class="field-grid-3">
          <div class="field"><label>归属项目</label><select id="bl-project">${projectOpts}</select></div>
          <div class="field"><label>当前状态</label><select id="bl-status">${statusOpts}</select></div>
          <div class="field"><label>优先级</label><select id="bl-priority">${priorityOpts}</select></div>
        </div>
        <div class="field">
          <label>详细说明与讨论要点（可选）</label>
          <textarea id="bl-notes" rows="6" placeholder="记录核心功能点、UI 设想、数据来源或避坑备注...">${esc(editItem?.notes || "")}</textarea>
        </div>
      </form>
      <div class="modal-footer">
        <button class="btn btn-soft" data-close-modal>取消</button>
        <button id="bl-submit" type="submit" form="f-backlog" class="btn btn-primary">${isEdit ? "保存修改" : "添加到需求池"}</button>
      </div>
    `);

    $("#bl-title")?.focus();
    $("#f-backlog").addEventListener("submit", async (e) => {
      e.preventDefault();
      const btn = $("#bl-submit");
      btn.disabled = true;
      try {
        await callHost("saveBacklogItem", {
          id: editItem?.id || "",
          title: $("#bl-title").value.trim(),
          project: $("#bl-project").value,
          status: $("#bl-status").value,
          priority: $("#bl-priority").value,
          notes: $("#bl-notes").value,
        });
        closeModal();
      } catch (err) {
        btn.disabled = false;
        showToast(err.message, true);
      }
    });
  }

  async function copyBacklogPrompt(item) {
    if (!item) return;
    const statusLabel = BACKLOG_STATUSES.find((s) => s.id === item.status)?.label || item.status;
    const prioLabel = BACKLOG_PRIORITIES[item.priority]?.label || "中";
    const promptText = [
      `请帮我推进以下需求：`,
      `- 项目归属：${item.project || "通用"}`,
      `- 需求标题：${item.title}`,
      `- 当前状态：${statusLabel}（优先级：${prioLabel}）`,
      item.notes ? `- 详细说明：\n${item.notes}` : "",
    ].filter(Boolean).join("\n");
    try {
      await callHost("copyText", { text: promptText });
      showToast("已复制需求描述，可直接粘贴给 AI");
    } catch (err) {
      showToast(err.message, true);
    }
  }

  async function exportBacklogMarkdown() {
    const filtered = filterBacklogItems(appState.backlog_items || []);
    if (!filtered.length) return;
    const lines = ["# AI Hub 需求池导出", ""];
    for (const item of filtered) {
      const st = BACKLOG_STATUSES.find((s) => s.id === item.status)?.label || item.status;
      const pr = BACKLOG_PRIORITIES[item.priority]?.label || "中";
      lines.push(`- [${item.status === "done" ? "x" : " "}] **[${item.project}] ${item.title}**（${st} · ${pr}优）`);
      if (item.notes) {
        for (const noteLine of String(item.notes).split("\n")) lines.push(`  ${noteLine}`);
      }
    }
    try {
      await callHost("copyText", { text: lines.join("\n") });
      showToast(`已复制 ${filtered.length} 条需求为 Markdown`);
    } catch (err) {
      showToast(err.message, true);
    }
  }

  function renderManage() {
    const settings = appState.settings || {};
    const shot = appState.screenshot || {};
    const shortcuts = appState.desktop_shortcuts || [];
    const apps = appState.installed_apps || [];
    const view = $("#view-content");
    $("#view-title").textContent = "设置";
    $("#view-sub").textContent = "";
    const key = JSON.stringify([shortcuts.map(({ id, name, requires_admin }) => [id, name, requires_admin]), apps]);
    if (key === settingsRenderKey && view.dataset.page === "manage") {
      syncSettingsFields(view, settings, shot, shortcuts);
      return;
    }
    settingsRenderKey = key;
    view.dataset.page = "manage";

    const hotkeyPresets = ["Ctrl+Shift+A", "Alt+A", "Ctrl+Alt+A", "F1"];
    const hotkeyPills = hotkeyPresets.map((value) =>
      `<button class="hotkey-pill${shot.hotkey === value ? " active" : ""}" data-act="setScreenshotHotkey"
        data-hk="${value}" aria-pressed="${shot.hotkey === value}">${value}</button>`
    ).join("");
    const stashRows = shortcuts.map((sc) => `
      <div class="stash-row">
        <div class="stash-meta">
          <strong>${esc(sc.name)}</strong>
          <span class="tag">${sc.requires_admin ? "公共桌面 · 需提权" : "个人桌面"}</span>
        </div>
        <div class="stash-right">
          <span class="stash-state ${sc.hidden ? "hidden-on" : ""}">${sc.hidden ? "已隐藏" : "桌面显示中"}</span>
          <label class="toggle">
            <input type="checkbox" data-stash-id="${esc(sc.id)}" aria-label="收纳 ${esc(sc.name)}" ${sc.hidden ? "checked" : ""} />
            <span class="toggle-track"></span><span class="toggle-thumb"></span>
          </label>
        </div>
      </div>`).join("");
    const uninstallRows = apps.map((app) => `
      <div class="app-row">
        <div class="app-left">
          <img class="app-icon" src="${esc(app.icon_url)}" alt="" />
          <div class="app-info"><div class="app-name">${esc(app.name)}</div><div class="app-sub">${esc(app.uninstall_method)}</div></div>
        </div>
        <button class="btn btn-danger-outline btn-sm" data-act="confirmUninstall" data-id="${esc(app.id)}">卸载软件</button>
      </div>`).join("");

    view.innerHTML = `<div class="settings-page">
      <section class="settings-group" aria-labelledby="settings-hotkeys">
        <h2 id="settings-hotkeys" class="settings-heading">快捷键与通知</h2>
        <div class="settings-comic-card">
          <header class="settings-card-banner">
            <div class="settings-banner-lead">
              <div class="settings-emblem tint-amber"><svg class="ic" viewBox="0 0 24 24"><rect x="2" y="5" width="20" height="14" rx="2"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M7 15h10"/></svg></div>
              <h3 class="settings-banner-title">热键与状态提醒</h3>
            </div>
            <span id="settings-hotkey-status" class="tag"></span>
          </header>
          <div class="setting-row">
            <div class="setting-label">全局截图快捷键</div>
            <div class="setting-actions">
              <div class="hotkey-pills">${hotkeyPills}</div>
              ${settingSwitch("screenshot_enabled", "启用全局截图快捷键", shot.enabled)}
            </div>
          </div>
          <div id="settings-hotkey-alert" class="hotkey-alert" hidden>
            <span></span><button class="btn btn-soft btn-sm" data-act="reregisterHotkey">重新注册</button>
          </div>
          <div class="setting-row">
            <div class="setting-label">额度自动刷新与回满提醒</div>
            ${settingSwitch("quota_auto_refresh", "额度自动刷新与回满提醒", settings.quota_auto_refresh !== false)}
          </div>
          <div class="setting-row">
            <div class="setting-label">周期回收号不足 48 小时提醒</div>
            ${settingSwitch("expiry_balloon_alert", "周期回收号不足 48 小时提醒", settings.expiry_balloon_alert)}
          </div>
        </div>
      </section>

      <section class="settings-group" aria-labelledby="settings-apps">
        <h2 id="settings-apps" class="settings-heading">应用与桌面</h2>
        <div class="settings-comic-card">
          <header class="settings-card-banner">
            <div class="settings-banner-lead">
              <div class="settings-emblem tint-mint">${ICONS.folder}</div>
              <h3 class="settings-banner-title">桌面图标收纳</h3>
              <span id="stash-count" class="tag"></span>
            </div>
            <div class="setting-actions">
              <button class="btn btn-paper btn-sm" data-act="hideAllAiShortcuts">一键隐藏 AI 图标</button>
              <button class="btn btn-paper btn-sm" data-act="restoreAllShortcuts">全部恢复</button>
              ${settingsDisclosure("stash-details", "展开明细")}
            </div>
          </header>
          ${disclosurePanel("stash-details", expandedSettings.has("stash-details"), `
            ${stashRows || '<div class="setting-row">未检测到可收纳的快捷方式</div>'}
            ${shortcuts.some((sc) => sc.requires_admin) ? '<div class="setting-row group-sub">更改公共桌面快捷方式时，Windows 会要求管理员确认。</div>' : ""}
          `)}
        </div>
        <div class="settings-comic-card">
          <header class="settings-card-banner">
            <div class="settings-banner-lead">
              <div class="settings-emblem tint-sky">${ICONS.trash}</div>
              <h3 class="settings-banner-title">软件卸载</h3>
              <span class="tag">已检测 ${apps.length} 个</span>
            </div>
            ${settingsDisclosure("uninstall-details", "展开列表")}
          </header>
          ${disclosurePanel("uninstall-details", expandedSettings.has("uninstall-details"), `
            ${uninstallRows || '<div class="setting-row">未检测到支持卸载的桌面应用</div>'}
          `)}
        </div>
      </section>

      <section class="settings-group" aria-labelledby="settings-system">
        <h2 id="settings-system" class="settings-heading">系统与窗口</h2>
        <div class="settings-comic-card">
          <header class="settings-card-banner">
            <div class="settings-banner-lead">
              <div class="settings-emblem tint-slate">${ICONS.monitor}</div>
              <h3 class="settings-banner-title">常驻与托盘行为</h3>
            </div>
          </header>
          <div class="setting-row">
            <div><div class="setting-label">关闭窗口时缩到系统托盘</div>
              <div class="group-sub">隐藏后休眠界面以降低占用，托盘图标始终常驻。</div></div>
            ${settingSwitch("minimize_to_tray", "关闭窗口时缩到系统托盘", settings.minimize_to_tray)}
          </div>
        </div>
      </section>
    </div>`;
    syncSettingsFields(view, settings, shot, shortcuts);
  }

  // ==================== POPOVER & MODALS ====================

  function layerIsOpen(layer) {
    return !layer.classList.contains("hidden") && !layer.classList.contains("is-closing");
  }

  function showLayer(layer) {
    layerExits.get(layer)?.();
    layer.classList.remove("hidden", "is-closing");
    layer.inert = false;
  }

  function dismissLayer(layer, after = () => layer.classList.add("hidden")) {
    if (!layerIsOpen(layer)) return;
    layer.inert = true;
    if (reducedMotion.matches) {
      after();
      return;
    }

    layer.classList.add("is-closing");
    const finish = () => {
      cancel();
      after();
      layer.classList.remove("is-closing");
    };
    const onEnd = (event) => { if (event.target === layer && event.animationName.endsWith("Out")) finish(); };
    const timer = setTimeout(finish, 160);
    const cancel = () => {
      clearTimeout(timer);
      layer.removeEventListener("animationend", onEnd);
      layerExits.delete(layer);
    };
    layerExits.set(layer, cancel);
    layer.addEventListener("animationend", onEnd);
  }

  function closePopover() {
    dismissLayer($("#popover-menu"));
  }

  function openAccountMoreMenu(anchor, acc) {
    const pop = $("#popover-menu");
    const id = esc(acc.id);
    pop.innerHTML = acc.is_default
      ? `<button class="pop-item" data-act="openPermissionMode" data-id="${id}">${ICONS.key} 默认权限设置</button>`
      : `<button class="pop-item" data-act="openEditAccount" data-id="${id}">${ICONS.edit} 编辑账号与权限</button>
         <button class="pop-item" data-act="syncConfig" data-id="${id}">${ICONS.sync} 同步主号指引和 Skills</button>
         <div class="pop-sep"></div>
         <button class="pop-item danger" data-act="openWipe" data-id="${id}">${ICONS.trash} 移除并清空账号…</button>`;
    positionPopover(pop, anchor);
  }

  function positionPopover(pop, anchorBtn) {
    showLayer(pop);

    const rect = anchorBtn.getBoundingClientRect();
    const popW = pop.offsetWidth;
    const popH = pop.offsetHeight || 160;
    let left = rect.right - popW;
    let top = rect.bottom + 6;
    if (left < 10) left = 10;
    const opensUp = top + popH > window.innerHeight - 10;
    if (opensUp) top = Math.max(10, rect.top - popH - 6);
    pop.dataset.direction = opensUp ? "up" : "down";
    pop.style.left = `${left}px`;
    pop.style.top = `${top}px`;
  }

  function openModal(html, wide = false) {
    closePopover();
    closeModal();
    const dlg = $("#modal-dialog");
    dlg.className = "modal-dialog" + (wide ? " modal-wide" : "");
    dlg.innerHTML = html;
    showLayer($("#modal-backdrop"));
  }

  function closeModal() {
    const cancelPicker = onNetworkPickerClose;
    onNetworkPickerClose = null;
    cancelPicker?.();
    dismissLayer($("#modal-backdrop"));
  }

  async function refreshNetwork() {
    if (networkBusy) return;
    networkBusy = "refresh";
    renderAll();
    try {
      const result = await callHost("refreshNetwork");
      if (result.warning) showToast(result.warning, true);
    } catch (err) {
      showToast(err.message, true);
    } finally {
      networkBusy = "";
      renderAll();
    }
  }

  function openNetworkPicker() {
    if (networkBusy) return;
    openModal(`
      <div class="modal-header network-picker-header">
        <h3>切换节点</h3>
        <div class="network-picker-actions utility-toolbar" role="group" aria-label="节点工具栏">
          ${iconButton("", "刷新全部延迟", ICONS.refresh, { disabled: true, attrs: 'id="network-probe-all"' })}
          ${iconButton("", "关闭节点选择器", ICONS.close, { attrs: "data-close-modal" })}
        </div>
      </div>
      <div class="modal-body network-picker" id="network-picker">
        <div class="network-policies" role="group" aria-label="策略组"></div>
        <div class="network-picker-meta">
          <span>日本节点</span>
          <span role="status" data-picker-status></span>
        </div>
        <div class="network-node-list" aria-label="日本节点"></div>
      </div>
    `, true);

    const panel = $("#network-picker");
    const policies = panel.querySelector(".network-policies");
    const message = panel.querySelector("[data-picker-status]");
    const list = panel.querySelector(".network-node-list");
    const probe = $("#network-probe-all");
    const isOpen = () => panel.isConnected && layerIsOpen($("#modal-backdrop"));
    const latencyLabels = { testing: "测试中…", timeout: "超时", unavailable: "不可用", controller: "检测失败" };
    let picker = { group: "", groups: [], nodes: [] };
    let loading = false;
    let pendingGroup = "";
    let rows = new Map();

    function latency(node) {
      return node.delay != null ? node.delay + " ms" : latencyLabels[node.state] || "未测";
    }

    function renderControls() {
      const locked = loading || !!networkBusy;
      policies.innerHTML = picker.groups.map((group) => {
        const active = group.name === (loading ? pendingGroup : picker.group);
        return `<button type="button" class="filter-tab ${active ? "active" : ""}" data-policy="${esc(group.name)}"
          aria-pressed="${active}" ${locked ? "disabled" : ""}>${esc(group.label)}</button>`;
      }).join("");
      probe.disabled = locked || picker.nodes.length === 0;
      const probing = networkBusy === "batch";
      probe.title = probing ? "测速中…" : "刷新全部延迟";
      probe.setAttribute("aria-label", probe.title);
      probe.classList.toggle("is-spinning", probing);
      list.querySelectorAll("[data-node-index]").forEach((button, index) => {
        button.disabled = locked || picker.nodes[index].selected;
      });
    }

    function renderNodes(emptyLabel = "这个策略组暂无日本节点") {
      picker.nodes.sort((a, b) => (a.delay ?? Infinity) - (b.delay ?? Infinity) || a.name.localeCompare(b.name, "zh-CN"));
      list.innerHTML = picker.nodes.length ? picker.nodes.map((node, index) => `
        <button type="button" class="network-node${node.active ? " is-current" : ""}" data-node-index="${index}">
          <span class="network-node-name"><span>${esc(node.name)}</span>${node.active ? '<span class="network-current">当前</span>' : ""}</span>
          <span class="network-node-delay" data-node-state="${esc(node.state)}">${latency(node)}</span>
        </button>`).join("") : `<div class="network-node-empty">${esc(emptyLabel)}</div>`;
      rows = new Map(Array.from(list.querySelectorAll(".network-node-delay"), (row, index) => [picker.nodes[index].name, row]));
    }

    async function loadNodes(group) {
      if (loading || networkBusy) return;
      loading = true;
      pendingGroup = group;
      message.textContent = "正在读取节点…";
      renderControls();
      try {
        const result = await callHost("listNetworkNodes", { group });
        if (!isOpen()) return;
        picker = result;
        list.scrollTop = 0;
        renderNodes();
        message.textContent = picker.nodes.length + " 个";
      } catch (err) {
        if (!isOpen()) return;
        message.textContent = err.message;
        if (!picker.groups.length) {
          list.innerHTML = '<div class="network-node-empty"><button class="btn btn-soft btn-sm" data-picker-retry>重新读取</button></div>';
        }
      } finally {
        loading = false;
        if (isOpen()) renderControls();
      }
    }

    policies.addEventListener("click", (event) => {
      const group = event.target.closest("[data-policy]")?.dataset.policy;
      if (group && group !== picker.group) loadNodes(group);
    });

    list.addEventListener("click", async (event) => {
      if (event.target.closest("[data-picker-retry]")) return loadNodes("");
      const button = event.target.closest("[data-node-index]");
      if (!button || button.disabled || networkBusy || loading) return;
      const node = picker.nodes[Number(button.dataset.nodeIndex)];
      networkBusy = "switch";
      renderAll();
      renderControls();
      message.textContent = "正在切换节点并检测延迟…";
      try {
        await callHost("switchNetworkNode", { group: picker.group, node: node.name });
        if (isOpen()) closeModal();
        showToast("已切换到 " + node.name);
      } catch (err) {
        if (isOpen()) message.textContent = err.message;
        else showToast(err.message, true);
      } finally {
        networkBusy = "";
        renderAll();
        if (isOpen()) renderControls();
      }
    });

    probe.addEventListener("click", async () => {
      if (loading || networkBusy || !picker.nodes.length) return;
      networkBusy = "batch";
      onNetworkPickerClose = () => postAction("cancelNetworkBatch");
      renderAll();
      renderControls();
      message.textContent = "正在准备测速…";
      let checked = 0;
      let completed = false;
      try {
        await callHost("measureNetworkGroup", { group: picker.group }, (update) => {
          if (!isOpen()) return;
          if (update.nodes) {
            picker.nodes = update.nodes;
            renderNodes();
            renderControls();
          } else {
            const node = picker.nodes.find((item) => item.name === update.name);
            const row = rows.get(update.name);
            if (!node || !row) return;
            node.state = update.state;
            node.delay = update.delay ?? null;
            row.dataset.nodeState = node.state;
            row.textContent = latency(node);
            if (node.state !== "testing") checked += 1;
          }
          message.textContent = `正在测速 ${checked} / ${picker.nodes.length}`;
        });
        completed = true;
      } catch (err) {
        if (isOpen()) message.textContent = err.message;
      } finally {
        onNetworkPickerClose = null;
        networkBusy = "";
        renderAll();
        if (isOpen()) {
          renderNodes();
          renderControls();
          if (completed) message.textContent = `测速完成 ${checked} / ${picker.nodes.length}`;
        }
      }
    });

    renderNodes("正在读取节点…");
    loadNodes("");
  }

  function openConfirmModal(title, message, confirmLabel, danger, onConfirm) {
    openModal(`
      <div class="modal-header">
        <h3>${esc(title)}</h3>
        <button class="btn btn-ghost btn-icon-only" data-close-modal>${ICONS.close}</button>
      </div>
      <div class="modal-body">
        <div style="font-size:13px;color:var(--text-secondary);white-space:pre-line;">${esc(message)}</div>
      </div>
      <div class="modal-footer">
        <button class="btn btn-soft" data-close-modal>取消</button>
        <button id="confirm-ok-btn" class="btn ${danger ? "btn-danger" : "btn-primary"}">${esc(confirmLabel)}</button>
      </div>
    `);
    $("#confirm-ok-btn").addEventListener("click", () => { closeModal(); onConfirm(); });
  }

  function openAccountModal(editAcc) {
    const isEdit = !!editAcc;
    const now = new Date();
    const savedStart = new Date(editAcc?.started_at || "");
    const savedEnd = new Date(editAcc?.recycle_at || "");
    const startDt = Number.isFinite(savedStart.getTime()) ? savedStart : now;
    const days = Number(editAcc?.recycle_days) > 0 ? Number(editAcc.recycle_days) : 25;
    const endDt = Number.isFinite(savedEnd.getTime()) ? savedEnd : new Date(startDt.getTime() + days * 86400000);
    const isRec = !isEdit || editAcc.account_type === "recycled";
    const subUntil = new Date(editAcc?.auth?.subscription_until || "").getTime();

    openModal(`
      <div class="modal-header">
        <h3>${isEdit ? `编辑账号与权限 · ${esc(editAcc.name)}` : "添加 Codex 账号"}</h3>
        <button class="btn btn-ghost btn-icon-only" data-close-modal>${ICONS.close}</button>
      </div>
      <form id="f-acc" class="modal-body">
        <div class="field">
          <label>账号备注名称</label>
          <input id="f-name" type="text" required value="${esc(isEdit ? editAcc.name : `Plus 回收号 · ${now.getMonth() + 1}月`)}" />
        </div>
        <div style="display:flex;gap:18px;font-size:12.5px;">
          <label class="check-row"><input type="radio" name="accType" value="recycled" ${isRec ? "checked" : ""} /> 周期回收号（开启倒计时）</label>
          <label class="check-row"><input type="radio" name="accType" value="permanent" ${!isRec ? "checked" : ""} /> 长期自用号</label>
        </div>
        <div id="f-rec-box" class="sub-box ${isRec ? "" : "hidden"}">
          <div style="display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:6px;">
            <strong style="font-size:12.5px;">回收倒计时设置</strong>
            <div style="display:flex;gap:4px;">
              <button type="button" class="btn btn-soft btn-sm" data-preset="25">25天</button>
              <button type="button" class="btn btn-soft btn-sm" data-preset="24">24天</button>
              <button type="button" class="btn btn-soft btn-sm" data-preset="30">30天</button>
              <button type="button" class="btn btn-soft btn-sm" data-preset="7">7天</button>
            </div>
          </div>
          <div class="field-grid">
            <div class="field"><label>起算 / 购买时间</label><input id="f-start" type="datetime-local" value="${toLocalInputValue(startDt)}" /></div>
            <div class="field"><label>周期天数</label><input id="f-days" type="number" step="any" min="0.1" value="${days}" /></div>
          </div>
          <div class="field"><label>精确回收截止时刻</label><input id="f-end" type="datetime-local" value="${toLocalInputValue(endDt)}" /></div>
          ${Number.isFinite(subUntil) ? `
            <div class="calibration-note">
              <span>官方 Plus 到期 ${esc(fmtDate(subUntil))}</span>
              <button type="button" id="f-cal-5d" class="btn btn-soft btn-sm">按到期前 5 天校准</button>
            </div>` : ""}
        </div>
        ${isEdit ? permissionFields(editAcc.permission_mode) : `
          <div class="sub-box">
            <strong style="font-size:12.5px;">初始指引和 Skills</strong>
            <label class="check-row"><input type="radio" name="cfgMode" value="clone" checked /> 复制主号指引和 Skills</label>
            <label class="check-row"><input type="radio" name="cfgMode" value="junction_skills" /> 复制主号指引，共享 Skills 目录</label>
            <label class="check-row"><input type="radio" name="cfgMode" value="fresh" /> 空白环境</label>
            <label class="check-row"><input id="f-full-access" type="checkbox"> 此账号使用完整访问（不询问）</label>
            <label class="check-row" style="margin-top:4px;"><input id="f-dis-mem" type="checkbox" checked /> 在副本配置中关闭本地 Memories</label>
            <label class="check-row"><input id="f-launch" type="checkbox" checked /> 创建后打开官方 Codex 窗口</label>
          </div>`}
        <div class="field">
          <label>备注（可选）</label>
          <input id="f-notes" type="text" placeholder="例如：到期前5天 15r 回收" value="${esc(isEdit ? editAcc.notes : "")}" />
        </div>
      </form>
      <div class="modal-footer">
        <button class="btn btn-soft" data-close-modal>取消</button>
        <button id="f-submit" type="submit" form="f-acc" class="btn btn-primary">${isEdit ? "保存修改" : "创建账号"}</button>
      </div>
    `);

    const syncEnd = () => {
      const s = new Date($("#f-start").value).getTime();
      const d = parseFloat($("#f-days").value);
      if (!Number.isNaN(s) && d > 0) $("#f-end").value = toLocalInputValue(new Date(s + d * 86400000));
    };
    const syncDays = () => {
      const s = new Date($("#f-start").value).getTime();
      const e = new Date($("#f-end").value).getTime();
      if (!Number.isNaN(s) && !Number.isNaN(e) && e > s) $("#f-days").value = String(Math.round(((e - s) / 86400000) * 10) / 10);
    };

    $$('input[name="accType"]').forEach((r) => r.addEventListener("change", () => {
      const rec = $('input[name="accType"]:checked').value === "recycled";
      $("#f-rec-box").classList.toggle("hidden", !rec);
      if ($("#f-dis-mem")) $("#f-dis-mem").checked = rec;
    }));
    $$("[data-preset]").forEach((b) => b.addEventListener("click", () => { $("#f-days").value = b.dataset.preset; syncEnd(); }));
    $("#f-start").addEventListener("change", syncEnd);
    $("#f-days").addEventListener("input", syncEnd);
    $("#f-end").addEventListener("change", syncDays);
    if ($("#f-cal-5d")) {
      $("#f-cal-5d").addEventListener("click", () => {
        const calibrated = new Date(subUntil - 5 * 86400000);
        if (new Date($("#f-start").value).getTime() >= calibrated.getTime())
          $("#f-start").value = toLocalInputValue(new Date(calibrated.getTime() - days * 86400000));
        $("#f-end").value = toLocalInputValue(calibrated);
        syncDays();
      });
    }

    const form = $("#f-acc");
    const submit = $("#f-submit");
    form.addEventListener("submit", async (event) => {
      event.preventDefault();
      if (submit.disabled) return;
      const name = $("#f-name").value.trim();
      if (!name) return showToast("请输入账号备注名称", true);
      const accType = $('input[name="accType"]:checked').value;
      const start = new Date($("#f-start").value);
      const end = new Date($("#f-end").value);
      const duration = Number($("#f-days").value);
      if (accType === "recycled" && (!Number.isFinite(start.getTime()) || !Number.isFinite(end.getTime())
        || end <= start || !Number.isFinite(duration) || duration <= 0)) return showToast("请填写有效的回收期限，截止时间须晚于起算时间。", true);
      const payload = {
        name,
        account_type: accType,
        notes: $("#f-notes").value.trim(),
        started_at: accType === "recycled" ? toIsoWithOffset(start) : null,
        recycle_at: accType === "recycled" ? toIsoWithOffset(end) : null,
        recycle_days: accType === "recycled" ? duration : null,
      };
      submit.disabled = true;
      try {
        if (isEdit) {
          payload.id = editAcc.id;
          payload.permission_mode = form.querySelector('input[name="account-permission"]:checked')?.value || null;
          await callHost("updateAccount", payload);
        } else {
          payload.config_mode = $('input[name="cfgMode"]:checked').value;
          payload.disable_memories = $("#f-dis-mem").checked;
          payload.launch_now = $("#f-launch").checked;
          payload.permission_mode = $("#f-full-access")?.checked ? "full" : "custom";
          await callHost("createAccount", payload);
        }
        if (form.isConnected) closeModal();
      } catch (_) {
        submit.disabled = false;
      }
    });
  }

  function permissionFields(mode) {
    const custom = mode !== "workspace" && mode !== "full";
    return `<fieldset class="sub-box permission-options"><legend>Codex 本地权限</legend>
      ${custom ? '<label class="check-row"><input type="radio" name="account-permission" value="" checked> 保留现有权限配置</label>' : ""}
      <label class="check-row"><input type="radio" name="account-permission" value="workspace" ${mode === "workspace" ? "checked" : ""}> 工作区可写 · 不询问</label>
      <label class="check-row"><input type="radio" name="account-permission" value="full" ${mode === "full" ? "checked" : ""}> 完整访问 · 不询问</label>
      <p>Codex 选择「自定义」时读取这里保存的权限。完整访问可读写当前 Windows 用户有权访问的文件，并使用网络。</p>
    </fieldset>`;
  }

  function openPermissionModal(acc) {
    openModal(`
      <div class="modal-header">
        <h3>默认权限设置</h3>
        <button class="btn btn-ghost btn-icon-only" data-close-modal>${ICONS.close}</button>
      </div>
      <div class="modal-body">
        ${permissionFields(acc.permission_mode)}
      </div>
      <div class="modal-footer">
        <button class="btn btn-soft" data-close-modal>取消</button>
        <button id="permission-save" class="btn btn-primary" ${["workspace", "full"].includes(acc.permission_mode) ? "" : "disabled"}>保存</button>
      </div>
    `);
    const save = $("#permission-save");
    $$('input[name="account-permission"]').forEach((input) => input.addEventListener("change", () => { save.disabled = !input.value; }));
    save.addEventListener("click", async () => {
      const mode = $('input[name="account-permission"]:checked')?.value;
      if (!mode) return;
      save.disabled = true;
      try {
        await callHost("saveProfilePermissionMode", { id: acc.id, mode });
        if (save.isConnected) closeModal();
      } catch (_) {
        save.disabled = false;
      }
    });
  }

  function openWipeModal(acc) {
    openModal(`
      <div class="modal-header">
        <h3>移除并清空账号</h3>
        <button class="btn btn-ghost btn-icon-only" data-close-modal>${ICONS.close}</button>
      </div>
      <div class="modal-body">
        <div class="sub-box">
          <strong>${esc(acc.name)}</strong>
          <p>此操作会移除这张卡片，并永久删除本机隔离登录态、配置和本地会话。云端账号及云端数据保留。请先完全退出此 Codex 实例。</p>
          <label class="check-row"><input type="checkbox" id="w-ack" /> 我确认删除这个账号的本地数据</label>
        </div>
      </div>
      <div class="modal-footer">
        <button class="btn btn-soft" data-close-modal>取消</button>
        <button id="w-confirm" class="btn btn-danger" disabled>确认删除</button>
      </div>
    `);
    const confirm = $("#w-confirm");
    const ack = $("#w-ack");
    ack.addEventListener("change", () => { confirm.disabled = !ack.checked; });
    confirm.addEventListener("click", async () => {
      confirm.disabled = true;
      ack.disabled = true;
      confirm.textContent = "正在删除…";
      try {
        await callHost("wipeAccount", { id: acc.id });
        if (confirm.isConnected) closeModal();
      } catch (_) {
        confirm.disabled = !ack.checked;
        ack.disabled = false;
        confirm.textContent = "确认删除";
      }
    });
  }

  function openBatchExitModal() {
    if (!appState) return;
    const codexRunning = (appState.accounts || []).filter((a) => a.running);
    const toolRunning = [];
    const seen = new Set();
    (appState.tool_sections || []).forEach((s) => (s.items || []).forEach((it) => {
      if (it.running && it.kind !== "url" && it.uninstall_hint !== "OpenAI.Codex" && !seen.has(it.id)) {
        seen.add(it.id);
        toolRunning.push(it);
      }
    }));
    const proxyRunning = !!appState.proxy?.running;
    const total = codexRunning.length + toolRunning.length + (proxyRunning ? 1 : 0);

    const rows = [
      ...codexRunning.map((a) => `<label class="check-row"><input type="checkbox" class="be-codex" value="${esc(a.id)}" checked /> Codex · ${esc(a.name)}</label>`),
      ...toolRunning.map((t) => `<label class="check-row"><input type="checkbox" class="be-tool" value="${esc(t.id)}" checked /> AI 应用 · ${esc(t.name)}</label>`),
      ...(proxyRunning ? [`<label class="check-row"><input type="checkbox" id="be-proxy" checked /> 网络代理 · Clash Verge</label>`] : []),
    ];

    openModal(`
      <div class="modal-header">
        <h3>一键全部退出</h3>
        <button class="btn btn-ghost btn-icon-only" data-close-modal>${ICONS.close}</button>
      </div>
      <div class="modal-body">
        <div id="batch-exit-options" class="sub-box">
          ${rows.length ? rows.join("") : `<span style="font-size:12.5px;color:var(--text-muted);">当前没有正在运行的受管应用或代理。</span>`}
          <label class="check-row batch-exit-hub"><input type="checkbox" id="be-hub" /> 同时退出 AI Hub</label>
        </div>
      </div>
      <div class="modal-footer">
        <button class="btn btn-soft" data-close-modal>取消</button>
        <button id="be-ok" class="btn btn-danger" ${total === 0 ? "disabled" : ""}>确认全部退出</button>
      </div>
    `);

    const options = $("#batch-exit-options");
    const confirm = $("#be-ok");
    const checks = [...options.querySelectorAll("input[type=checkbox]")];
    const syncConfirm = () => { confirm.disabled = !checks.some((input) => input.checked); };
    options.addEventListener("change", syncConfirm);
    confirm.addEventListener("click", async () => {
      confirm.disabled = true;
      checks.forEach((input) => { input.disabled = true; });
      try {
        await callHost("batchExit", {
          codexIds: $$(".be-codex:checked").map((c) => c.value),
          toolIds: $$(".be-tool:checked").map((c) => c.value),
          stopProxy: !!$("#be-proxy")?.checked,
          quitAiHub: $("#be-hub").checked,
        });
        if (confirm.isConnected) closeModal();
      } catch (_) {
        checks.forEach((input) => { input.disabled = false; });
        syncConfirm();
      }
    });
  }

  function changelogDates(markdown) {
    const days = new Map();
    let day = null, version = "", section = "";
    for (const raw of String(markdown).split(/\r?\n/)) {
      const line = raw.trim();
      const heading = line.match(/^##\s+(v[^\s（(]+)\s*[（(](\d{4}-\d{2}-\d{2})[）)]/);
      if (heading) {
        version = heading[1];
        const date = heading[2];
        if (!days.has(date)) days.set(date, { date, versions: [], updates: [] });
        day = days.get(date);
        day.versions.push(version);
        section = "";
      } else if (line.startsWith("### ")) {
        section = line.slice(4);
      } else if (day && /^[-*]\s|^\d+\.\s/.test(line)) {
        const body = line.replace(/^[-*]\s+|^\d+\.\s+/,"").replace(/\*\*|`/g, "");
        const label = body.match(/^\[(新增|改进|修复|调整)\]\s*/);
        const words = body + " " + section;
        const type = label ? label[1] : (/^(修复|纠正)|问题修复|错位|黑屏|黑边/.test(words) ? "修复"
          : /^(新增|加入|首次)|新增功能|初版/.test(words) || section.startsWith("新增") ? "新增"
          : /^(调整|改为|取消|移除)|命名|管理操作/.test(words) ? "调整" : "改进");
        day.updates.push({ body: label ? body.slice(label[0].length) : body, version, type });
      }
    }
    return Array.from(days.values()).sort((a, b) => b.date.localeCompare(a.date));
  }

  async function openChangelogModal() {
    let dates;
    try { dates = changelogDates(await callHost("getChangelog")); } catch (_) { dates = []; }
    const categories = { 新增: "added", 改进: "improved", 修复: "fixed", 调整: "changed" };
    const summaries = {
      "2026-11-22": "新增需求池与漫画风项目管理页，支持固定端口预览、全卡头拖拽排序，并统一需求池、设置与日志的粗线视觉语言。",
      "2026-11-21": "工作台、网络、Codex 和截图完成集中升级，新增 Antigravity 实时额度。",
      "2026-11-20": "AI Hub 初版到工作台合并，加入截图、提字与桌面图标收纳。"
    };
    const currentVer = appState?.version || (dates[0]?.versions?.[0]) || "";
    openModal(`
      <div class="modal-header changelog-modal-head">
        <div class="changelog-head-title">
          <h3>更新日志</h3>
          ${currentVer ? `<span class="changelog-version">${esc(currentVer)}</span>` : ""}
        </div>
        ${iconButton("", "关闭更新日志", ICONS.close, { attrs: "data-close-modal" })}
      </div>
      <div class="modal-body changelog-timeline">
        ${dates.length ? dates.map((day, index) => {
          const [year, month, date] = day.date.split("-");
          const newest = day.versions[0], oldest = day.versions.at(-1);
          const sectionId = `changelog-day-${index}`;
          return `<section class="changelog-day">
            <button type="button" class="changelog-day-toggle" data-act="toggleChangelogDay"
              aria-expanded="${index === 0}" aria-controls="${sectionId}">
              ${ICONS.chevronDown}
              <span class="changelog-date">${year} 年 ${Number(month)} 月 ${Number(date)} 日</span>
              <span class="changelog-version">${oldest === newest ? esc(newest) : `${esc(oldest)} → ${esc(newest)}`}</span>
            </button>
            ${disclosurePanel(sectionId, index === 0, `
            ${summaries[day.date] ? `<p class="changelog-summary">${esc(summaries[day.date])}</p>` : ""}
            <div class="changelog-entries">${day.updates.map((update) => `<div class="changelog-entry">
              <span class="changelog-type ${categories[update.type]}">${update.type}</span>
              <span>${esc(update.body)}</span><small>${esc(update.version)}</small>
            </div>`).join("")}</div>
            `)}
          </section>`;
        }).join("") : quotaMessage("暂时无法读取更新日志", "quota-error")}
      </div>`, true);
  }

  // ==================== CTRL+K COMMAND PALETTE ====================

  function buildCmdkCatalog() {
    if (!appState) return [];
    const items = [];
    for (const acc of appState.accounts || []) {
      items.push({
        title: `启动 Codex · ${acc.name}`,
        sub: acc.auth?.email || "Codex 账号",
        tag: acc.running ? "运行中" : "账号",
        run: () => postAction("launchAccount", { id: acc.id }),
      });
    }
    for (const sec of appState.tool_sections || []) {
      for (const it of sec.items || []) {
        if (it.uninstall_hint === "OpenAI.Codex" || it.id === "tool-gemini") continue;
        items.push({
          title: `${it.kind === "url" ? "打开" : "启动"} ${it.name}`,
          sub: it.subtitle || sec.title,
          tag: it.running ? "运行中" : "应用",
          run: () => postAction("launchTool", { id: it.id }),
        });
      }
    }
    for (const tool of webTools) {
      for (const cp of appState.chrome_profiles || []) {
        items.push({
          title: `打开 ${tool.name} · ${cp.name}`,
          sub: cp.email || "Google 身份",
          tag: "AI 工作工具",
          run: () => postAction("launchChromeUrl", { tool: tool.id, dir: cp.dir }),
        });
      }
    }
    items.push({ title: "AI 录音笔", tag: "全局工具", sub: appState.voice_recorder_hotkey || "", run: () => postAction("startVoiceRecorder") });
    if (appState.antigravity?.cli_installed)
      items.push({ title: "刷新 Antigravity 额度", tag: "AI 工作工具", sub: "模型额度", run: () => postAction("refreshAntigravity") });
    items.push({ title: `快捷截图与提字（${appState.screenshot?.hotkey || "Ctrl+Shift+A"}）`, sub: "框选 · 标注 · OCR 提取文字 · 钉在桌面", tag: "工具", run: () => postAction("startScreenshot") });
    const ledger = appState.quick_ledger_forms?.[0];
    if (ledger) items.push({ title: "记账", sub: "打开飞书账本", tag: "工具", run: () => postAction("openLedgerForm", { id: ledger }) });
    items.push({ title: "一键隐藏已收纳的 AI 桌面图标", sub: "移入本地收纳夹，随时可一键恢复", tag: "桌面", run: () => postAction("hideAllDesktopShortcuts", { category: "ai" }) });
    items.push({ title: "恢复全部隐藏的桌面图标", sub: "将收纳夹里的快捷方式放回桌面", tag: "桌面", run: () => postAction("restoreAllDesktopShortcuts") });
    items.push({ title: appState.proxy?.running ? "Clash Verge · 打开面板" : "Clash Verge · 启动代理", sub: appState.proxy?.running ? "当前运行中" : "当前未启动", tag: "网络", run: () => postAction("proxyOpen") });
    items.push({ title: "重启代理（Clash Verge）", sub: "网络异常时快速重启通道", tag: "操作", run: () => postAction("proxyRestart") });
    items.push({ title: "刷新额度（所有 Codex 账号）", sub: "查询实时 5h / 每周 / 每月额度", tag: "操作", run: () => postAction("refreshAllUsage") });
    items.push({ title: "一键全部退出", sub: "批量关闭运行中的外部应用与代理", tag: "操作", run: openBatchExitModal });
    items.push({ title: "打开项目管理", sub: "集中查看项目介绍、复制路径与启动预览", tag: "导航", run: () => switchTab("projects") });
    for (const prj of (appState.projects || []).filter((p) => !p.hidden)) {
      items.push({
        title: `复制项目路径 · ${prj.name}`,
        sub: prj.path,
        tag: "项目",
        run: () => callHost("copyText", { text: prj.path }).then(() => showToast(`已复制路径：${prj.name}`)).catch((err) => showToast(err.message, true)),
      });
    }
    items.push({ title: "打开需求池", sub: "查看与管理 AI Hub / Life Hub 需求条目", tag: "导航", run: () => switchTab("backlog") });
    items.push({ title: "新建需求（需求池）", sub: "记录新想法与讨论要点", tag: "操作", run: () => { switchTab("backlog"); openBacklogModal(null); } });
    items.push({ title: "查看更新日志", sub: "更新日志.md", tag: "信息", run: openChangelogModal });
    return items;
  }

  function renderCmdkList(query) {
    const q = (query || "").trim().toLowerCase();
    const all = buildCmdkCatalog();
    cmdkItems = q ? all.filter((x) => (x.title + " " + x.sub + " " + x.tag).toLowerCase().includes(q)) : all;
    cmdkSelected = Math.min(cmdkSelected, Math.max(0, cmdkItems.length - 1));
    $("#cmdk-list").innerHTML = cmdkItems.length === 0
      ? `<div style="padding:18px;text-align:center;font-size:12.5px;color:var(--text-muted);">未匹配到相关操作</div>`
      : cmdkItems.map((it, idx) => `
          <button class="cmdk-item ${idx === cmdkSelected ? "selected" : ""}" data-cmdk-idx="${idx}">
            <div>
              <div class="cmdk-item-title">${esc(it.title)}</div>
              <div class="cmdk-item-sub">${esc(it.sub)}</div>
            </div>
            <span class="tag">${esc(it.tag)}</span>
          </button>
        `).join("");
  }

  function openCmdk() {
    closePopover();
    cmdkSelected = 0;
    $("#cmdk-input").value = "";
    renderCmdkList("");
    showLayer($("#cmdk-backdrop"));
    $("#cmdk-input").focus();
  }

  function closeCmdk() {
    dismissLayer($("#cmdk-backdrop"));
  }

  // ==================== EVENTS & WIRING ====================

  document.addEventListener("mousedown", (e) => {
    if (e.button !== 0) return;
    const region = e.target.closest(".drag-region");
    if (!region || e.target.closest(".no-drag, button, input, a")) return;
    postAction("dragWindow");
  });

  document.addEventListener("dblclick", (e) => {
    const region = e.target.closest(".drag-region");
    if (!region || e.target.closest(".no-drag, button, input, a")) return;
    postAction("toggleMaximize");
  });

  $("#win-min").addEventListener("click", () => postAction("minimize"));
  $("#win-max").addEventListener("click", () => postAction("toggleMaximize"));
  $("#win-close").addEventListener("click", () => postAction("close"));

  $$(".nav-item").forEach((btn) => btn.addEventListener("click", () => switchTab(btn.dataset.tab)));

  $("#view-content").addEventListener("animationend", (event) => {
    if (event.animationName === "viewEnter") event.target.classList.remove("view-entering");
    if (event.animationName === "viewCardEnter") event.target.classList.remove("view-card-entering");
  });

  $("#btn-batch-exit").addEventListener("click", openBatchExitModal);

  $("#cmdk-input").addEventListener("input", (e) => { cmdkSelected = 0; renderCmdkList(e.target.value); });
  $("#cmdk-input").addEventListener("keydown", (e) => {
    if (e.key === "Escape") return closeCmdk();
    if (e.key === "ArrowDown") { e.preventDefault(); cmdkSelected = Math.min(cmdkItems.length - 1, cmdkSelected + 1); renderCmdkList($("#cmdk-input").value); }
    else if (e.key === "ArrowUp") { e.preventDefault(); cmdkSelected = Math.max(0, cmdkSelected - 1); renderCmdkList($("#cmdk-input").value); }
    else if (e.key === "Enter" && cmdkItems[cmdkSelected]) { e.preventDefault(); const fn = cmdkItems[cmdkSelected].run; closeCmdk(); fn(); }
  });

  document.addEventListener("keydown", (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
      e.preventDefault();
      if (layerIsOpen($("#cmdk-backdrop"))) closeCmdk();
      else openCmdk();
    } else if (e.key === "Escape") {
      closeCmdk();
      closeModal();
      closePopover();
    }
  });

  document.addEventListener("change", (event) => {
    const tool = event.target.dataset.webIdentity;
    if (!tool) return;
    setWebIdentity(tool, event.target.value);
    const email = event.target.closest(".tool-card")?.querySelector("[data-web-email]");
    const profile = webIdentity(tool, appState?.chrome_profiles || []);
    if (email) {
      email.textContent = profile?.email || "—";
      email.title = profile?.email || "";
    }
  });

  document.addEventListener("click", (e) => {
    if (!e.target.closest('#popover-menu, [data-act="openMoreMenu"]')) closePopover();
    if (e.target === $("#modal-backdrop") || e.target.closest("[data-close-modal]")) return closeModal();
    if (e.target === $("#cmdk-backdrop")) return closeCmdk();

    const cmdkBtn = e.target.closest("[data-cmdk-idx]");
    if (cmdkBtn) {
      const item = cmdkItems[Number(cmdkBtn.dataset.cmdkIdx)];
      closeCmdk();
      if (item) item.run();
      return;
    }

    const actBtn = e.target.closest("[data-act]");
    if (!actBtn) return;
    const act = actBtn.dataset.act;
    const id = actBtn.dataset.id;
    if (appState && !appState.safe_profile_actions && ["syncConfig", "openWipe", "openAddAccount"].includes(act)) {
      closePopover();
      showToast("请重新启动 AI Hub，再执行此账号操作。", true);
      return;
    }

    if (act === "applyLocalUpdate") postAction("applyLocalUpdate");
    else if (act === "refresh") postAction("refresh", { toast: "1" });
    else if (act === "refreshAllUsage") postAction("refreshAllUsage");
    else if (act === "refreshUsage") postAction("refreshUsage", { id });
    else if (act === "refreshNetwork") refreshNetwork();
    else if (act === "openNetworkPicker") openNetworkPicker();
    else if (act === "refreshAntigravity") { closePopover(); postAction("refreshAntigravity"); }
    else if (["proxyOpen", "proxyRestart", "proxyStop"].includes(act)) {
      closePopover();
      postAction(act);
    }
    else if (act === "launchAccount") postAction("launchAccount", { id });
    else if (act === "stopAccount") postAction("stopAccount", { id });
    else if (act === "syncConfig") { closePopover(); postAction("syncConfig", { id }); }
    else if (act === "openChangelog") openChangelogModal();
    else if (act === "startScreenshot") postAction("startScreenshot");
    else if (act === "startVoiceRecorder") postAction("startVoiceRecorder");
    else if (act === "openLedgerForm") postAction("openLedgerForm", { id });
    else if (act === "toggleChangelogDay") toggleDisclosure(actBtn);
    else if (act === "toggleSettingsDetails") {
      const expanded = toggleDisclosure(actBtn);
      if (expanded === null) return;
      if (expanded) expandedSettings.add(id);
      else expandedSettings.delete(id);
      actBtn.querySelector("span").textContent = expanded ? "收起" : actBtn.dataset.collapsedLabel;
    }
    else if (act === "reregisterHotkey") postAction("reregisterHotkey");
    else if (act === "setScreenshotHotkey") postAction("updateSettings", { screenshot_enabled: true, screenshot_hotkey: actBtn.dataset.hk });
    else if (act === "hideAllAiShortcuts") postAction("hideAllDesktopShortcuts", { category: "ai" });
    else if (act === "restoreAllShortcuts") postAction("restoreAllDesktopShortcuts");
    else if (act === "launchTool") postAction("launchTool", { id });
    else if (act === "stopTool") { closePopover(); postAction("stopTool", { id }); }
    else if (act === "openWebTool") {
      const profile = webIdentity(id, appState.chrome_profiles || []);
      if (profile) postAction("launchChromeUrl", { tool: id, dir: profile.dir });
    }
    else if (act === "openAddAccount") openAccountModal(null);
    else if (act === "openPermissionMode") {
      closePopover();
      const acc = (appState.accounts || []).find((a) => a.id === id);
      if (acc?.is_default) openPermissionModal(acc);
    } else if (act === "openEditAccount") {
      const acc = (appState.accounts || []).find((a) => a.id === id);
      if (acc && !acc.is_default) openAccountModal(acc);
    } else if (act === "openWipe") {
      const acc = (appState.accounts || []).find((a) => a.id === id);
      if (acc && !acc.is_default) openWipeModal(acc);
    } else if (act === "openMoreMenu") {
      const acc = (appState.accounts || []).find((a) => a.id === id);
      if (acc) openAccountMoreMenu(actBtn, acc);
    } else if (act === "refreshProjects") {
      postAction("refreshProjects", { toast: "1" });
    } else if (act === "copyProjectPath") {
      const p = actBtn.dataset.path || "";
      callHost("copyText", { text: p }).then(() => showToast("已复制项目路径")).catch((err) => showToast(err.message, true));
    } else if (act === "openProjectFolder") {
      postAction("openProjectFolder", { path: actBtn.dataset.path || "" });
    } else if (act === "launchProjectPreview" || act === "stopProjectPreview") {
      postAction(act, { id });
    } else if (act === "openProjectRemote") {
      postAction("openProjectRemote", { url: actBtn.dataset.url || "" });
    } else if (act === "editProjectItem") {
      const path = actBtn.dataset.path || "";
      const item = (appState.projects || []).find((it) => it.id === id || it.path === path);
      if (item) openProjectModal(item);
    } else if (act === "filterBacklogProject") {
      backlogProject = actBtn.dataset.project || "all";
      renderBacklog();
    } else if (act === "openAddBacklog") {
      openBacklogModal(null);
    } else if (act === "exportBacklogMd") {
      exportBacklogMarkdown();
    } else if (act === "toggleBacklogDetail") {
      if (expandedBacklog.has(id)) expandedBacklog.delete(id);
      else expandedBacklog.add(id);
      renderBacklog(true);
    } else if (act === "copyBacklogPrompt") {
      const item = (appState.backlog_items || []).find((it) => it.id === id);
      copyBacklogPrompt(item);
    } else if (act === "editBacklogItem") {
      const item = (appState.backlog_items || []).find((it) => it.id === id);
      if (item) openBacklogModal(item);
    } else if (act === "confirmDeleteBacklog") {
      const item = (appState.backlog_items || []).find((it) => it.id === id);
      if (!item) return;
      openConfirmModal(
        "确认删除需求",
        `确定要从需求池删除「${item.title}」吗？`,
        "确认删除",
        true,
        () => postAction("deleteBacklogItem", { id })
      );
    } else if (act === "confirmUninstall") {
      const targetApp = (appState.installed_apps || []).find((it) => it.id === id);
      if (!targetApp) return;
      openConfirmModal(
        `确认卸载「${targetApp.name}」`,
        `确认后将${targetApp.uninstall_method}。`,
        "确认卸载",
        true,
        () => postAction("uninstallTool", { id })
      );
    }
  });

  document.addEventListener("input", (e) => {
    if (e.target && e.target.id === "backlog-search") {
      backlogQuery = e.target.value;
      renderBacklog(true);
    }
  });

  document.addEventListener("change", (e) => {
    if (e.target && e.target.id === "backlog-filter-status") {
      backlogStatus = e.target.value || "all";
      renderBacklog(true);
      return;
    }
    const blStatus = e.target.closest("[data-backlog-status]");
    if (blStatus) {
      postAction("setBacklogStatus", { id: blStatus.dataset.backlogStatus, status: blStatus.value });
      return;
    }
    const stashInp = e.target.closest("[data-stash-id]");
    if (stashInp) {
      postAction("toggleDesktopShortcut", { id: stashInp.dataset.stashId, hide: stashInp.checked });
      return;
    }
    const inp = e.target.closest("[data-setting]");
    if (!inp) return;
    postAction("updateSettings", { [inp.dataset.setting]: inp.checked });
  });

  // 1-second live countdown ticker (in-place update, zero flicker!)
  setInterval(() => {
    if (!appState || activeTab !== "workspace") return;
    for (const acc of appState.accounts || []) {
      const cd = computeCountdown(acc);
      if (!cd) continue;
      const clock = document.querySelector(`[data-cd-clock="${CSS.escape(acc.id)}"]`);
      const fill = document.querySelector(`[data-cd-fill="${CSS.escape(acc.id)}"]`);
      const status = document.querySelector(`[data-cd-status="${CSS.escape(acc.id)}"]`);
      const label = recycleStatus(cd);
      if (status && status.textContent !== label) status.textContent = label;
      if (clock) {
        clock.textContent = cd.meterValue;
        clock.title = cd.compact;
        clock.className = `meter-pct tone-${cd.level}`;
      }
      if (fill) {
        fill.className = `grad-fill tone-${cd.level}${cd.pct > 0 && cd.pct < 100 ? " is-partial" : ""}`;
        fill.style.width = `${cd.pct}%`;
      }
    }
  }, 1000);

  if (webview) {
    webview.addEventListener("message", (ev) => {
      const msg = typeof ev.data === "string" ? JSON.parse(ev.data) : ev.data;
      if (!msg || !msg.type) return;

      if (msg.type === "state") {
        appState = msg.payload;
        renderAll();
        if (!uiReadySent) {
          uiReadySent = true;
          postAction("uiReady");
        }
      } else if (msg.type === "toast") {
        showToast(msg.payload?.text, !!msg.payload?.isError);
      } else if (msg.type === "openBatchExit") {
        openBatchExitModal();
      } else if (msg.type === "windowState") {
        const isMax = !!msg.payload?.maximized;
        $("#win-max").title = isMax ? "还原" : "最大化";
      } else if (msg.type === "networkProbeProgress") {
        const p = msg.payload;
        pendingReqs.get(p.reqId)?.onProgress?.(p.update);
      } else if (msg.type === "reply") {
        const p = msg.payload || {};
        const h = pendingReqs.get(p.reqId);
        if (h) {
          pendingReqs.delete(p.reqId);
          if (p.ok) h.resolve(p.result);
          else h.reject(new Error(p.error || "failed"));
        }
      }
    });
  }
})();
