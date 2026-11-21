(() => {
  const webview = window.chrome && window.chrome.webview;
  const pendingReqs = new Map();
  let reqSeq = 1;

  let appState = null;
  let uiReadySent = false;
  let activeTab = "workspace"; // "workspace" | "manage"
  let cmdkItems = [];
  let cmdkSelected = 0;
  let networkBusy = "";
  let onNetworkPickerClose = null;

  const $ = (sel) => document.querySelector(sel);
  const $$ = (sel) => Array.from(document.querySelectorAll(sel));

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
    folder: '<svg class="ic ic-sm" viewBox="0 0 24 24"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z"/></svg>',
    trash: '<svg class="ic ic-sm" viewBox="0 0 24 24"><path d="M3 6h18"/><path d="M8 6V4h8v2"/><path d="M6 6l1 14h10l1-14"/></svg>',
    close: '<svg class="ic ic-sm" viewBox="0 0 24 24"><path d="M18 6 6 18M6 6l12 12"/></svg>',
    ext: '<svg class="ic ic-xs" viewBox="0 0 24 24"><path d="M14 4h6v6"/><path d="M10 14 20 4"/><path d="M18 13v6a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h6"/></svg>',
    scan: '<svg class="ic ic-sm" viewBox="0 0 24 24"><path d="M3 7V5a2 2 0 0 1 2-2h2M17 3h2a2 2 0 0 1 2 2v2M21 17v2a2 2 0 0 1-2 2h-2M7 21H5a2 2 0 0 1-2-2v-2"/><circle cx="12" cy="12" r="3"/></svg>',
    crop: '<svg class="ic ic-sm" viewBox="0 0 24 24"><path d="M6 2v14a2 2 0 0 0 2 2h14"/><path d="M18 22V8a2 2 0 0 0-2-2H2"/></svg>',
    mic: '<svg class="ic ic-sm" viewBox="0 0 24 24" aria-hidden="true"><rect x="9" y="2" width="6" height="12" rx="3"/><path d="M5 10v2a7 7 0 0 0 14 0v-2M12 19v3M9 22h6"/></svg>',
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
    setTimeout(() => {
      el.style.opacity = "0";
      el.style.transform = "translateY(4px)";
      el.style.transition = "all 0.18s ease";
      setTimeout(() => el.remove(), 200);
    }, 3000);
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
    const startMs = acc.started_at ? new Date(acc.started_at).getTime() : (endMs - (acc.recycle_days || 25) * 86400000);
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
    const visualWidth = clamped <= 0 ? 0 : Math.max(2, clamped);
    return `<div class="grad-track"><div class="grad-fill tone-${tone}" ${fillAttr} style="width:${visualWidth.toFixed(1)}%"></div></div>`;
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
    $("#version-label").textContent = `${appState.version || "v4.1"} · 更新日志`;
    $("#nav-workspace-dot").classList.toggle("hidden", appState.network?.state !== "reachable" || !appState.proxy?.running);

    const globalTools = $("#sidebar-global-tools");
    const shot = appState.screenshot?.hotkey || "Ctrl+Shift+A";
    const voice = appState.voice_recorder_hotkey;
    const globalHtml = `
      <div class="sidebar-tool-label">全局工具</div>
      <button class="sidebar-tool-button btn btn-ghost" data-act="startScreenshot" title="截图 · ${esc(shot)}">${ICONS.crop}<span>截图</span></button>
      <button class="sidebar-tool-button btn btn-ghost" data-act="startVoiceRecorder" title="AI 录音笔${voice ? " · " + esc(voice) : ""}" ${voice ? "" : "disabled"}>${ICONS.mic}<span>AI 录音笔</span></button>`;
    if (globalTools.dataset.rendered !== globalHtml) {
      globalTools.innerHTML = globalHtml;
      globalTools.dataset.rendered = globalHtml;
    }
    const pending = appState.pending_update;
    const headerActions = $("#header-actions");
    if (pending !== (headerActions.dataset.pending === "true")) {
      headerActions.innerHTML = pending ? '<button class="btn btn-primary btn-sm" data-act="applyLocalUpdate">重启应用更新</button>' : "";
      headerActions.dataset.pending = String(!!pending);
    }
    $$(".nav-item").forEach((el) => el.classList.toggle("active", el.dataset.tab === activeTab));

    if (activeTab === "workspace") renderUnifiedWorkspace();
    else renderManage();
  }

  function iconButton(action, label, glyph, { disabled = false, danger = false, spinning = false, attrs = "" } = {}) {
    return `<button type="button" class="btn icon-tool ${danger ? "btn-danger-outline" : "btn-ghost"}${spinning ? " is-spinning" : ""}"
      ${action ? `data-act="${action}"` : ""} title="${esc(label)}" aria-label="${esc(label)}" ${disabled ? "disabled" : ""} ${attrs}>${glyph}</button>`;
  }

  function statusHtml(label, tone = "") {
    return `<span class="status-inline ${tone}"><span class="status-dot"></span>${esc(label)}</span>`;
  }

  function toolHeading(id, icon, name, description, status = "") {
    return `<div class="tool-identity">
      ${icon ? `<img class="app-icon" src="${esc(icon)}" alt="" />` : `<span class="tool-icon-fallback" aria-hidden="true">${ICONS.monitor}</span>`}
      <div class="app-info">
        <div class="app-name-line"><h2 class="app-name" id="${id}">${esc(name)}</h2>${status}</div>
        <div class="tool-purpose">${esc(description)}</div>
      </div>
    </div>`;
  }

  function quotaTone(percent) {
    return percent <= 10 ? "danger" : (percent <= 30 ? "warn" : "ok");
  }

  function renderQuotaMeter({ title, value, when, percent, tone = "", labelAttrs = "", valueAttrs = "", fillAttrs = "", timeAttrs = "", action = "" }) {
    return `<div class="meter-block">
      <span class="meter-name" ${labelAttrs}>${esc(title)}</span>
      <strong class="meter-pct${tone ? ` tone-${tone}` : ""}" ${valueAttrs}>${esc(value)}</strong>
      <span class="meter-reset" ${timeAttrs}>${esc(when)}</span>
      ${percent == null ? '<div class="grad-track"></div>' : renderGradientBar(percent, tone, fillAttrs)}
      ${action}
    </div>`;
  }

  function quotaMessage(message, type = "", action = "") {
    return `<div class="quota-feedback ${type}" role="status"><span>${esc(message)}</span>${action}</div>`;
  }

  function updatedAtHtml(timestamp) {
    if (!timestamp) return "";
    const date = new Date(timestamp);
    if (Number.isNaN(date.getTime())) return "";
    const label = date.toDateString() === new Date().toDateString()
      ? date.toLocaleTimeString("zh-CN", { hour: "2-digit", minute: "2-digit", hour12: false })
      : fmtDate(timestamp, true);
    return `<time datetime="${esc(timestamp)}" title="${esc(fmtDate(timestamp))}">更新 ${esc(label)}</time>`;
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
    { id: "gemini", name: "Gemini", icon: "gemini.png", description: "对话、研究与内容创作" },
    { id: "ai-studio", name: "AI Studio", icon: "ai-studio.png", description: "试用 Gemini 模型与调试提示词" }
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
        ${toolHeading("tool-" + tool.id, tool.icon, tool.name, tool.description, statusHtml(available ? "可用" : "不可用", available ? "healthy" : "status-error"))}
        <div class="tool-actions">
          <button class="btn btn-primary btn-sm" data-act="openWebTool" data-id="${tool.id}" aria-label="打开 ${esc(tool.name)}" ${available ? "" : "disabled"}>打开 ${ICONS.ext}</button>
          ${iconButton("openWorkMore", "更多 " + tool.name + " 操作", ICONS.more, { attrs: `data-id="${tool.id}"` })}
        </div>
      </header>
      <div class="tool-card-body">
        <div class="tool-field">
          <label for="identity-${tool.id}">Google 身份</label>
          <select id="identity-${tool.id}" data-web-identity="${tool.id}" ${available ? "" : "disabled"}>
            ${profiles.length ? profiles.map((item) => `<option value="${esc(item.dir)}" ${item.dir === profile?.dir ? "selected" : ""}>${esc(item.name)}</option>`).join("") : "<option>暂无可用身份</option>"}
          </select>
        </div>
        ${profiles.length ? `<div class="tool-identity-email" data-web-email="${tool.id}">${esc(profile?.email)}</div>` : ""}
        <div class="tool-field tool-resource"><span>额度</span><span>暂不可读取</span></div>
      </div>
    </section>`;
  }

  function renderOpenDesign(tool) {
    const ready = !!tool;
    return `<section class="surface-card tool-card compact-tool" aria-labelledby="tool-open-design">
      <header class="tool-card-header">
        ${toolHeading("tool-open-design", tool?.icon_url, "Open Design", "设计构思与原型创作",
          statusHtml(ready ? (tool.running ? "运行中" : "未运行") : "未安装", tool?.running ? "running" : ""))}
        <div class="tool-actions">
          <button class="btn btn-primary btn-sm" data-act="launchTool" data-id="${esc(tool?.id)}" ${ready ? "" : "disabled"}>${tool?.running ? "打开" : "启动"}</button>
          ${iconButton("openWorkMore", "更多 Open Design 操作", ICONS.more, { attrs: 'data-id="open-design"' })}
        </div>
      </header>
      <div class="tool-card-body">
        <div class="tool-field"><span>启动方式</span><span>桌面</span></div>
        <div class="tool-field"><span>身份</span><span>由 Open Design 管理</span></div>
        <div class="tool-field tool-resource"><span>额度</span><span>暂不可读取</span></div>
      </div>
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
            timeAttrs: bucket?.reset_time ? `title="${esc(fmtDate(bucket.reset_time))}"` : "" });
        }).join("")}</div>
      </div>`).join("") : "";
    const error = tool.error
      ? quotaMessage(tool.error, "quota-error", tool.cli_installed ? '<button class="btn btn-secondary btn-sm" data-act="refreshAntigravity">重试</button>' : "")
      : "";
    const info = tool.loading ? quotaMessage("正在读取额度…")
      : (!groups && !error ? quotaMessage(tool.cli_installed ? "点击刷新额度" : "暂不可读取 · CLI 未安装") : "");
    return `<section class="surface-card tool-card" aria-labelledby="tool-antigravity">
      <header class="tool-card-header">
        ${toolHeading("tool-antigravity", tool.icon_url, "Antigravity", "在本地项目中开发与调试",
          statusHtml(tool.id ? (tool.running ? "运行中" : "未运行") : "未安装", tool.running ? "running" : ""))}
        <div class="tool-actions">
          <button class="btn btn-secondary btn-sm" data-act="refreshAntigravity" aria-label="刷新 Antigravity 额度" ${tool.loading || !tool.cli_installed ? "disabled" : ""}>${ICONS.refresh} 刷新额度</button>
          <button class="btn btn-primary btn-sm" data-act="launchTool" data-id="${esc(tool.id)}" ${tool.id ? "" : "disabled"}>${tool.running ? "打开" : "启动"}</button>
          ${iconButton("openWorkMore", "更多 Antigravity 操作", ICONS.more, { attrs: 'data-id="antigravity"' })}
        </div>
      </header>
      <div class="tool-card-body">
        <div class="tool-resource-meta">
          <span>${esc(usage?.plan || "Google 身份待读取")}${usage?.email ? ` <span class="tool-identity-email">${esc(usage.email)}</span>` : ""}</span>
          ${updatedAtHtml(usage?.fetched_at)}
        </div>
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
    return `<section class="surface-card tool-card codex-tool" aria-labelledby="codex-tool-title">
      <header class="tool-card-header">
        ${toolHeading("codex-tool-title", "https://icons.aihub.local/chatgpt.png", "Codex", accounts.length + " 个账号")}
        <div class="tool-actions">
          <button class="btn btn-secondary btn-sm" data-act="refreshAllUsage" title="刷新所有 Codex 账号的额度">${ICONS.refresh} 刷新额度</button>
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
    const windows = usage?.success ? [usage.primary, usage.secondary].filter(Boolean) : [];
    let feedback = "";
    if (!auth.logged_in) feedback = quotaMessage("登录后可读取剩余额度");
    else if (auth.can_fetch_usage === false) feedback = quotaMessage("当前登录方式未提供额度");
    else if (acc.usage_loading) feedback = quotaMessage("正在查询额度…");
    else if (usage && !usage.success) feedback = quotaMessage(usage.error || "额度查询失败", "quota-error",
      `<button class="btn btn-secondary btn-sm" data-act="refreshUsage" data-id="${esc(acc.id)}" aria-label="重试 ${esc(name)} 的额度查询">重试</button>`);
    else if (!windows.length) feedback = quotaMessage(usage ? "当前套餐暂无独立额度窗口" : "暂无额度数据");

    const meters = windows.map((w) => {
      const percent = Math.max(0, Math.min(100, Number(w.left_percent) || 0));
      return renderQuotaMeter({ title: String(w.label).replace("限额", "额度"), value: percent + "%",
        when: w.reset_text || "重置时间未知", percent, tone: quotaTone(percent),
        valueAttrs: `aria-label="剩余 ${percent}%"` });
    });
    if (cd) meters.push(renderQuotaMeter({
      title: recycleStatus(cd), value: cd.meterValue, when: "截止 " + cd.targetShort,
      percent: cd.pct, tone: cd.level,
      labelAttrs: `data-cd-status="${esc(acc.id)}"`,
      valueAttrs: `data-cd-clock="${esc(acc.id)}" title="${esc(cd.compact)}"`,
      fillAttrs: `data-cd-fill="${esc(acc.id)}"`,
      timeAttrs: `title="${esc(fmtDate(acc.recycle_at))}"`,
      action: auth.subscription_until
        ? `<button class="btn btn-secondary meter-calibrate" data-act="calibrate5Days" data-id="${esc(acc.id)}">按到期前 5 天校准</button>` : ""
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
          ${statusHtml(acc.running ? "运行中" : "未运行", acc.running ? "running" : "")}
          <button class="btn ${acc.running ? "btn-danger-outline" : "btn-primary"} btn-sm"
            data-act="${acc.running ? "stopAccount" : "launchAccount"}" data-id="${esc(acc.id)}"
            ${!acc.running && !auth.logged_in ? 'title="启动 Codex 并登录"' : ""}>
            ${acc.running ? "关闭" : ICONS.play + " 启动"}
          </button>
          ${iconButton("openMoreMenu", "更多 " + name + " 操作", ICONS.more, { attrs: `data-id="${esc(acc.id)}"` })}
        </div>
      </header>
      <div class="account-usage">
        ${feedback}
        ${usage?.success && usage.fetched_at ? `<div class="account-usage-head">${updatedAtHtml(usage.fetched_at)}</div>` : ""}
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
    view.innerHTML = `
      ${renderNetwork(proxy)}
      <div class="sec-heading"><h2 class="sec-title work-tools-title">AI 工作工具</h2></div>
      ${renderCodex(accounts)}
      ${renderAntigravity()}
      <div class="work-tools-grid">
        ${webTools.map((tool) => renderWebTool(tool, chromes)).join("")}
        ${renderOpenDesign(design)}
      </div>`;
  }

  // TAB 2: MANAGE & SETTINGS
  function renderManage() {
    const sections = appState.tool_sections || [];
    const settings = appState.settings || {};

    $("#view-title").textContent = "管理与设置";
    $("#view-sub").textContent = "集中管理软件卸载、工作台分区整理与托盘偏好";

    // 1. Software Uninstall Center (includes ChatGPT / Codex MSIX!)
    const uniqueApps = [];
    const seen = new Set();
    sections.forEach((s) => (s.items || []).forEach((it) => {
      const k = (it.target || it.id).toLowerCase();
      if (it.kind !== "url" && !seen.has(k)) { seen.add(k); uniqueApps.push(it); }
    }));

    const uninstRows = uniqueApps.length === 0
      ? `<div class="app-row"><span style="font-size:12px;color:var(--text-muted);">当前未收纳桌面软件，可点击下方「重新扫描本机 AI 软件」。</span></div>`
      : uniqueApps.map((app) => `
          <div class="app-row">
            <div class="app-left">
              <img class="app-icon" src="${esc(app.icon_url)}" alt="" />
              <div class="app-info">
                <div class="app-name">${esc(app.name)}</div>
                <div class="app-sub">方式：${esc(app.uninstall_method)}</div>
              </div>
            </div>
            <div class="app-right">
              <button class="btn btn-danger-outline btn-sm" data-act="confirmUninstall" data-id="${esc(app.id)}">卸载软件</button>
            </div>
          </div>
        `).join("");

    // 2. Sections & Items Organization
    const secRows = sections.map((sec) => {
      const childItems = (sec.items || []).filter((it) => it.uninstall_hint !== "OpenAI.Codex").map((it) => `
        <div style="display:flex;align-items:center;justify-content:space-between;padding:5px 0 5px 12px;font-size:12.5px;color:var(--text-secondary);">
          <span>${esc(it.name)}</span>
          <button class="btn btn-ghost btn-sm" data-act="confirmHideTool" data-sec="${esc(sec.id)}" data-id="${esc(it.id)}" data-name="${esc(it.name)}">从工作台隐藏</button>
        </div>
      `).join("");
      return `
        <div style="padding:11px 16px;border-top:1px solid var(--hairline);">
          <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:4px;">
            <strong style="font-size:12.5px;">${esc(sec.title)}</strong>
            <button class="btn btn-ghost btn-sm" data-act="confirmDeleteSection" data-id="${esc(sec.id)}" data-title="${esc(sec.title)}">删除分区</button>
          </div>
          ${childItems || `<div style="padding:4px 0 4px 12px;font-size:12px;color:var(--text-muted);">暂无可隐藏的独立应用条目</div>`}
        </div>
      `;
    }).join("");

    // 0A. Screenshot & Local OCR Card
    const shot = appState.screenshot || { enabled: true, hotkey: "Ctrl+Shift+A", registered: false, error: "" };
    const hotkeyPresets = ["Ctrl+Shift+A", "Alt+A", "Ctrl+Alt+A", "F1"];
    const hkPillsHtml = hotkeyPresets.map((hk) => `
      <button class="hotkey-pill ${shot.hotkey === hk ? "active" : ""}" data-act="setScreenshotHotkey" data-hk="${esc(hk)}">${esc(hk)}</button>
    `).join("");

    const shotStatusTag = !shot.enabled
      ? `<span class="tag">快捷键已关闭</span>`
      : (shot.registered
          ? `<span class="tag tag-ok">热键已生效</span>`
          : `<span class="tag tag-warn">热键被占用</span>`);

    const shotConflictBanner = (shot.enabled && !shot.registered && shot.error)
      ? `
        <div class="hotkey-alert">
          <span>${esc(shot.error)}</span>
          <button class="btn btn-secondary btn-sm" data-act="reregisterHotkey" style="flex-shrink:0;">重新注册</button>
        </div>
      `
      : "";

    // 0B. Reversible Desktop Shortcut Stash Card
    const shortcuts = appState.desktop_shortcuts || [];
    const hiddenCount = shortcuts.filter((s) => s.hidden).length;
    const aiVisibleCount = shortcuts.filter((s) => s.category === "ai" && !s.hidden).length;
    const stashRowsHtml = shortcuts.length === 0
      ? `<div class="stash-row"><span style="font-size:12px;color:var(--text-muted);">未在桌面检测到可收纳的 AI 应用快捷方式</span></div>`
      : shortcuts.map((sc) => `
          <div class="stash-row">
            <div class="stash-meta">
              <strong style="font-size:13px;">${esc(sc.name)}</strong>
              <span class="tag ${sc.category === "ai" ? "tag-dark" : ""}">${sc.category === "ai" ? "AI Hub 已收纳" : "关联工具"}</span>
              <span class="tag">${sc.requires_admin ? "公共桌面 · 需提权" : "个人桌面"}</span>
            </div>
            <div class="stash-right">
              <span class="stash-state ${sc.hidden ? "hidden-on" : ""}">${sc.hidden ? "已隐藏" : "桌面显示中"}</span>
              <label class="toggle" title="${sc.hidden ? "点击恢复到桌面" : "点击从桌面隐藏"}">
                <input type="checkbox" data-stash-id="${esc(sc.id)}" ${sc.hidden ? "checked" : ""} />
                <span class="toggle-track"></span><span class="toggle-thumb"></span>
              </label>
            </div>
          </div>
        `).join("");

    $("#view-content").innerHTML = `
      <div class="surface-card">
        <div class="group-header" style="display:flex;align-items:center;justify-content:space-between;gap:12px;">
          <div>
            <div class="group-title" style="display:flex;align-items:center;gap:8px;">
              <span>快捷截图、标注与提取文字（OCR）</span>
              ${shotStatusTag}
            </div>
            <div class="group-sub">智能吸附窗口 · 放大镜取色(C) · 矩形/箭头/画笔/马赛克/文字 · 滚轮调粗细 · 本机离线中英文 OCR · 贴图置顶</div>
          </div>
          <button class="btn btn-primary btn-sm" data-act="startScreenshot" style="flex-shrink:0;">${ICONS.crop} 立即截图</button>
        </div>
        <div class="setting-row">
          <div>
            <div style="font-size:13px;font-weight:500;">启用全局截图快捷键</div>
            <div style="font-size:11.5px;color:var(--text-muted);">缩进托盘时同样随时响应，彻底替代飞书后台截图</div>
          </div>
          <label class="toggle">
            <input type="checkbox" data-setting="screenshot_enabled" ${shot.enabled ? "checked" : ""} />
            <span class="toggle-track"></span><span class="toggle-thumb"></span>
          </label>
        </div>
        <div class="setting-row">
          <div>
            <div style="font-size:13px;font-weight:500;">截图快捷键组合</div>
            <div style="font-size:11.5px;color:var(--text-muted);">如果飞书正在运行会占用 Ctrl+Shift+A，关掉飞书点“重新注册”或直接切其他组合</div>
          </div>
          <div class="hotkey-pills">${hkPillsHtml}</div>
        </div>
        ${shotConflictBanner}
      </div>

      <div class="surface-card">
        <div class="group-header" style="display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap;">
          <div>
            <div class="group-title">桌面图标收纳盒（随时隐藏 / 恢复）</div>
            <div class="group-sub">仅移入本机收纳夹，绝不删除文件，也不碰你的工作文件夹与文档（当前已隐藏 ${hiddenCount}/${shortcuts.length}）</div>
          </div>
          <div style="display:flex;gap:6px;">
            <button class="btn btn-primary btn-sm" data-act="hideAllAiShortcuts" ${aiVisibleCount === 0 ? "disabled" : ""}>一键隐藏 AI 图标</button>
            <button class="btn btn-secondary btn-sm" data-act="restoreAllShortcuts" ${hiddenCount === 0 ? "disabled" : ""}>全部恢复到桌面</button>
          </div>
        </div>
        ${stashRowsHtml}
        <div style="padding:9px 16px;border-top:1px solid var(--hairline);font-size:11.5px;color:var(--text-muted);">
          提示：「个人桌面」图标切换瞬间生效；「公共桌面」图标（如 Clash Verge、Chrome）由 Windows 保护，切换时会弹一次管理员确认。恢复后图标按系统默认空位排列。
        </div>
      </div>

      <div class="surface-card">
        <div class="group-header">
          <div class="group-title">软件卸载中心</div>
          <div class="group-sub">点击后会先弹窗确认，再调用 Windows 系统或软件官方卸载程序</div>
        </div>
        ${uninstRows}
      </div>

      <div class="surface-card">
        <div class="group-header">
          <div class="group-title">工作台分区与条目整理</div>
          <div class="group-sub">隐藏条目或删除分区仅影响工作台展示，不会卸载软件；随时可重新扫描找回</div>
        </div>
        <div style="display:flex;gap:8px;flex-wrap:wrap;padding:12px 16px;">
          <button class="btn btn-primary btn-sm" data-act="rescanTools">${ICONS.scan} 重新扫描本机 AI 软件</button>
          <button class="btn btn-secondary btn-sm" data-act="openAddSection">${ICONS.plus} 新建分区</button>
          <button class="btn btn-secondary btn-sm" data-act="openAddTool">${ICONS.plus} 添加应用 / 网页</button>
        </div>
        ${secRows}
      </div>

      <div class="surface-card">
        <div class="group-header">
          <div class="group-title">托盘、图标与通用设置</div>
        </div>
        <div class="setting-row">
          <div>
            <div style="font-size:13px;font-weight:500;">关闭窗口时缩到系统托盘</div>
            <div style="font-size:11.5px;color:var(--text-muted);">隐藏到托盘后自动休眠渲染视图并停止轮询，保持低内存占用</div>
          </div>
          <label class="toggle">
            <input type="checkbox" data-setting="minimize_to_tray" ${settings.minimize_to_tray ? "checked" : ""} />
            <span class="toggle-track"></span><span class="toggle-thumb"></span>
          </label>
        </div>
        <div class="setting-row">
          <div>
            <div style="font-size:13px;font-weight:500;">仅隐藏到后台时显示托盘图标</div>
            <div style="font-size:11.5px;color:var(--text-muted);">关闭此项（默认）时，只要 AI Hub 在运行，右下角托盘图标会始终常驻</div>
          </div>
          <label class="toggle">
            <input type="checkbox" data-setting="hide_tray_when_open" ${settings.hide_tray_when_open ? "checked" : ""} />
            <span class="toggle-track"></span><span class="toggle-thumb"></span>
          </label>
        </div>
        <div class="setting-row">
          <div>
            <div style="font-size:13px;font-weight:500;">周期回收号不足 48 小时弹出系统提醒</div>
            <div style="font-size:11.5px;color:var(--text-muted);">进入回收临界窗口时发送桌面通知</div>
          </div>
          <label class="toggle">
            <input type="checkbox" data-setting="expiry_balloon_alert" ${settings.expiry_balloon_alert ? "checked" : ""} />
            <span class="toggle-track"></span><span class="toggle-thumb"></span>
          </label>
        </div>
        <div style="display:flex;gap:8px;flex-wrap:wrap;padding:12px 16px;border-top:1px solid var(--hairline);">
          <button class="btn btn-secondary btn-sm" data-act="pinDesktop">更新桌面图标</button>
          <button class="btn btn-secondary btn-sm" data-act="openDataDir">打开数据目录</button>
          ${appState.pending_update ? '<button class="btn btn-primary btn-sm" data-act="applyLocalUpdate">重启应用更新</button>' : ""}
          <button class="btn btn-secondary btn-sm" data-act="openChangelog">查看更新日志</button>
          <button class="btn btn-danger-outline btn-sm" data-act="forceQuit">退出 AI Hub</button>
        </div>
      </div>
    `;
  }

  // ==================== POPOVER & MODALS ====================

  function closePopover() {
    $("#popover-menu").classList.add("hidden");
  }

  function openAccountMoreMenu(anchorBtn, acc) {
    const pop = $("#popover-menu");
    const items = [];
    if (acc.running) {
      items.push(`<button class="pop-item" data-act="openAccountWindow" data-id="${esc(acc.id)}">${ICONS.monitor} 打开 Codex 窗口</button>`);
    }
    if (acc.permission_mode) {
      items.push(`<button class="pop-item" data-act="openPermissionMode" data-id="${esc(acc.id)}">${ICONS.key} Codex 访问权限</button>`);
    }
    if (acc.auth?.logged_in) {
      items.push(`<button class="pop-item" data-act="signOutAccount" data-id="${esc(acc.id)}">${ICONS.key} 打开 Codex 退出登录…</button>`);
    }
    if (!acc.is_default) {
      items.push(`<button class="pop-item" data-act="openEditAccount" data-id="${esc(acc.id)}">${ICONS.edit} 编辑 / 校准倒计时</button>`);
      items.push(`<button class="pop-item" data-act="syncConfig" data-id="${esc(acc.id)}">${ICONS.sync} 同步主号指引和 Skills</button>`);
    }
    items.push(`<button class="pop-item" data-act="createAccountShortcut" data-id="${esc(acc.id)}" data-name="${esc(acc.name)}">${ICONS.monitor} 创建桌面图标</button>`);
    items.push(`<button class="pop-item" data-act="openAccountFolder" data-id="${esc(acc.id)}">${ICONS.folder} 打开账号目录</button>`);
    if (!acc.is_default) {
      items.push(`<div class="pop-sep"></div>`);
      items.push(`<button class="pop-item danger" data-act="openWipe" data-id="${esc(acc.id)}">${ICONS.trash} 删除本地账号数据…</button>`);
    }
    pop.innerHTML = items.join("");
    positionPopover(pop, anchorBtn);
  }

  function openWorkMore(anchor, id) {
    const webTool = webTools.find((tool) => tool.id === id);
    const tool = id === "antigravity" ? appState.antigravity : findOpenDesign();
    const pop = $("#popover-menu");
    if (webTool) {
      pop.innerHTML = `<button class="pop-item" data-act="copyWorkAddress" data-id="${webTool.id}">${ICONS.ext} 复制网页地址</button>`;
    } else {
      const items = [];
      if (id === "antigravity" && tool?.cli_installed) {
        items.push(`<button class="pop-item" data-act="refreshAntigravity" ${tool.loading ? "disabled" : ""}>${ICONS.refresh} 刷新额度</button>`);
      }
      if (tool?.running) {
        items.push(`<button class="pop-item danger" data-act="stopTool" data-id="${esc(tool.id)}">${ICONS.power} 关闭${id === "antigravity" ? " Antigravity" : " Open Design"}</button>`);
      }
      items.push(`<button class="pop-item" data-act="goManage">${ICONS.folder} 管理桌面应用</button>`);
      pop.innerHTML = items.join("");
    }
    positionPopover(pop, anchor);
  }

  function positionPopover(pop, anchorBtn) {
    pop.classList.remove("hidden");

    const rect = anchorBtn.getBoundingClientRect();
    const popW = pop.offsetWidth;
    const popH = pop.offsetHeight || 160;
    let left = rect.right - popW;
    let top = rect.bottom + 6;
    if (left < 10) left = 10;
    if (top + popH > window.innerHeight - 10) top = rect.top - popH - 6;
    pop.style.left = `${left}px`;
    pop.style.top = `${top}px`;
  }

  function openModal(html, wide = false) {
    closePopover();
    onNetworkPickerClose?.();
    const dlg = $("#modal-dialog");
    dlg.className = "modal-dialog" + (wide ? " modal-wide" : "");
    dlg.innerHTML = html;
    $("#modal-backdrop").classList.remove("hidden");
  }

  function closeModal() {
    onNetworkPickerClose?.();
    $("#modal-backdrop").classList.add("hidden");
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
    const isOpen = () => panel.isConnected && !$("#modal-backdrop").classList.contains("hidden");
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
          list.innerHTML = '<div class="network-node-empty"><button class="btn btn-secondary btn-sm" data-picker-retry>重新读取</button></div>';
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
        <button class="btn btn-secondary" data-close-modal>取消</button>
        <button id="confirm-ok-btn" class="btn ${danger ? "btn-danger" : "btn-primary"}">${esc(confirmLabel)}</button>
      </div>
    `);
    $("#confirm-ok-btn").addEventListener("click", () => { closeModal(); onConfirm(); });
  }

  function openAccountModal(editAcc) {
    const isEdit = !!editAcc;
    const now = new Date();
    const startDt = isEdit && editAcc.started_at ? new Date(editAcc.started_at) : now;
    const days = isEdit && editAcc.recycle_days ? editAcc.recycle_days : 25;
    const endDt = isEdit && editAcc.recycle_at ? new Date(editAcc.recycle_at) : new Date(startDt.getTime() + days * 86400000);
    const isRec = !isEdit || editAcc.account_type === "recycled";
    const subUntil = isEdit ? editAcc.auth?.subscription_until : null;

    openModal(`
      <div class="modal-header">
        <h3>${isEdit ? `编辑账号 · ${esc(editAcc.name)}` : "添加 Codex 账号"}</h3>
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
              <button type="button" class="btn btn-secondary btn-sm" data-preset="25">25天</button>
              <button type="button" class="btn btn-secondary btn-sm" data-preset="24">24天</button>
              <button type="button" class="btn btn-secondary btn-sm" data-preset="30">30天</button>
              <button type="button" class="btn btn-secondary btn-sm" data-preset="7">7天</button>
            </div>
          </div>
          <div class="field-grid">
            <div class="field"><label>起算 / 购买时间</label><input id="f-start" type="datetime-local" value="${toLocalInputValue(startDt)}" /></div>
            <div class="field"><label>周期天数</label><input id="f-days" type="number" step="0.5" min="0.5" value="${days}" /></div>
          </div>
          <div class="field"><label>精确回收截止时刻</label><input id="f-end" type="datetime-local" value="${toLocalInputValue(endDt)}" /></div>
          ${subUntil ? `
            <div style="display:flex;align-items:center;justify-content:space-between;padding:8px 10px;border-radius:7px;background:var(--ok-bg);border:1px solid var(--ok-border);font-size:12px;color:var(--ok);">
              <span>官方 Plus 到期：${esc(fmtDate(subUntil))}</span>
              <button type="button" id="f-cal-5d" class="btn btn-secondary btn-sm">按到期前 5 天校准</button>
            </div>` : ""}
        </div>
        ${!isEdit ? `
          <div class="sub-box">
            <strong style="font-size:12.5px;">初始指引和 Skills</strong>
            <label class="check-row"><input type="radio" name="cfgMode" value="clone" checked /> 复制主号指引和 Skills</label>
            <label class="check-row"><input type="radio" name="cfgMode" value="junction_skills" /> 复制主号指引，共享 Skills 目录</label>
            <label class="check-row"><input type="radio" name="cfgMode" value="fresh" /> 空白环境</label>
            ${appState.pending_update !== undefined ? '<label class="check-row"><input id="f-full-access" type="checkbox"> 此账号使用完整访问（不询问）</label>' : ""}
            <label class="check-row" style="margin-top:4px;"><input id="f-dis-mem" type="checkbox" checked /> 在副本配置中关闭本地 Memories</label>
            <label class="check-row"><input id="f-launch" type="checkbox" checked /> 创建后打开官方 Codex 窗口</label>
          </div>` : ""}
        <div class="field">
          <label>备注（可选）</label>
          <input id="f-notes" type="text" placeholder="例如：到期前5天 15r 回收" value="${esc(isEdit ? editAcc.notes : "")}" />
        </div>
      </form>
      <div class="modal-footer">
        <button class="btn btn-secondary" data-close-modal>取消</button>
        <button id="f-submit" class="btn btn-primary">${isEdit ? "保存修改" : "创建并启动"}</button>
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
        $("#f-end").value = toLocalInputValue(new Date(new Date(subUntil).getTime() - 5 * 86400000));
        syncDays();
      });
    }

    $("#f-submit").addEventListener("click", async () => {
      const name = $("#f-name").value.trim();
      if (!name) return showToast("请输入账号备注名称", true);
      const accType = $('input[name="accType"]:checked').value;
      const payload = {
        name,
        account_type: accType,
        notes: $("#f-notes").value.trim(),
        started_at: accType === "recycled" ? toIsoWithOffset(new Date($("#f-start").value)) : null,
        recycle_at: accType === "recycled" ? toIsoWithOffset(new Date($("#f-end").value)) : null,
        recycle_days: accType === "recycled" ? (parseFloat($("#f-days").value) || 25) : null,
      };
      $("#f-submit").disabled = true;
      try {
        if (isEdit) {
          payload.id = editAcc.id;
          await callHost("updateAccount", payload);
        } else {
          payload.config_mode = $('input[name="cfgMode"]:checked').value;
          payload.disable_memories = $("#f-dis-mem").checked;
          payload.launch_now = $("#f-launch").checked;
          payload.permission_mode = $("#f-full-access")?.checked ? "full" : "custom";
          await callHost("createAccount", payload);
        }
        closeModal();
      } catch (_) {
        $("#f-submit").disabled = false;
      }
    });
  }

  function openPermissionModal(acc) {
    openModal(`
      <div class="modal-header">
        <h3>Codex 访问权限</h3>
        <button class="btn btn-ghost btn-icon-only" data-close-modal>${ICONS.close}</button>
      </div>
      <div class="modal-body">
        <strong>${esc(acc.name)}</strong>
        <label class="check-row"><input type="radio" name="account-permission" value="workspace" ${acc.permission_mode === "workspace" ? "checked" : ""}> 工作区可写 · 不询问</label>
        <label class="check-row"><input type="radio" name="account-permission" value="full" ${acc.permission_mode === "full" ? "checked" : ""}> 完整访问 · 不询问</label>
        <p>只写入此账号的配置。完整访问可以读写当前 Windows 用户可访问的文件和使用网络。Codex 选择「自定义」时使用这里保存的权限。</p>
      </div>
      <div class="modal-footer">
        <button class="btn btn-secondary" data-close-modal>取消</button>
        <button id="permission-save" class="btn btn-primary" ${acc.permission_mode === "custom" ? "disabled" : ""}>保存</button>
      </div>
    `);
    const save = $("#permission-save");
    $$('input[name="account-permission"]').forEach((input) => input.addEventListener("change", () => { save.disabled = false; }));
    save.addEventListener("click", async () => {
      const mode = $('input[name="account-permission"]:checked')?.value;
      if (!mode) return;
      save.disabled = true;
      try {
        await callHost("saveProfilePermissionMode", { id: acc.id, mode });
        closeModal();
      } catch (_) {
        save.disabled = false;
      }
    });
  }

  function openWipeModal(acc) {
    openModal(`
      <div class="modal-header">
        <h3>删除本地账号数据</h3>
        <button class="btn btn-ghost btn-icon-only" data-close-modal>${ICONS.close}</button>
      </div>
      <div class="modal-body">
        <div class="sub-box">
          <strong>${esc(acc.name)}</strong>
          <p>此操作将移除 AI Hub 中的这张账号卡片，并永久删除该实例的本地会话、设置和登录数据。请先完全退出该 Codex 实例。</p>
          <label class="check-row"><input type="checkbox" id="w-ack" /> 我确认删除这个账号的本地数据</label>
        </div>
      </div>
      <div class="modal-footer">
        <button class="btn btn-secondary" data-close-modal>取消</button>
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
        closeModal();
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
        <div style="font-size:12.5px;color:var(--text-secondary);">仅退出勾选的外部应用与代理（不会退出 AI Hub 本身），适用于网络异常或集中重启。</div>
        <div class="sub-box">
          ${rows.length ? rows.join("") : `<span style="font-size:12.5px;color:var(--text-muted);">当前没有正在运行的受管应用或代理。</span>`}
        </div>
      </div>
      <div class="modal-footer">
        <button class="btn btn-secondary" data-close-modal>取消</button>
        <button id="be-ok" class="btn btn-danger" ${total === 0 ? "disabled" : ""}>确认全部退出</button>
      </div>
    `);

    $("#be-ok").addEventListener("click", async () => {
      $("#be-ok").disabled = true;
      await callHost("batchExit", {
        codexIds: $$(".be-codex:checked").map((c) => c.value),
        toolIds: $$(".be-tool:checked").map((c) => c.value),
        stopProxy: !!$("#be-proxy")?.checked,
      });
      closeModal();
    });
  }

  function openAddSectionModal() {
    openModal(`
      <div class="modal-header">
        <h3>新建工作台分区</h3>
        <button class="btn btn-ghost btn-icon-only" data-close-modal>${ICONS.close}</button>
      </div>
      <div class="modal-body">
        <div class="field">
          <label>分区名称</label>
          <input id="sec-title-in" type="text" placeholder="例如：设计工具 / 常用 AI 网页" />
        </div>
      </div>
      <div class="modal-footer">
        <button class="btn btn-secondary" data-close-modal>取消</button>
        <button id="sec-ok" class="btn btn-primary">创建分区</button>
      </div>
    `);
    $("#sec-title-in").focus();
    $("#sec-ok").addEventListener("click", async () => {
      const title = $("#sec-title-in").value.trim();
      if (!title) return showToast("请输入分区名称", true);
      await callHost("addSection", { title });
      closeModal();
    });
  }

  function openAddToolModal() {
    const sections = appState.tool_sections || [];
    const opts = sections.map((s) => `<option value="${esc(s.id)}">${esc(s.title)}</option>`).join("");
    openModal(`
      <div class="modal-header">
        <h3>添加应用或 AI 网页</h3>
        <button class="btn btn-ghost btn-icon-only" data-close-modal>${ICONS.close}</button>
      </div>
      <div class="modal-body">
        ${opts ? `<div class="field"><label>归属分区</label><select id="t-sec">${opts}</select></div>` : ""}
        <div style="display:flex;gap:18px;font-size:12.5px;">
          <label class="check-row"><input type="radio" name="tKind" value="exe" checked /> 本机桌面程序 (.exe)</label>
          <label class="check-row"><input type="radio" name="tKind" value="url" /> 网页工具 (URL)</label>
        </div>
        <div class="field"><label>名称</label><input id="t-name" type="text" placeholder="例如：Claude / Cursor" /></div>
        <div class="field">
          <label>程序路径或网址</label>
          <div style="display:flex;gap:8px;">
            <input id="t-target" type="text" placeholder="C:\\... 或 https://..." />
            <button type="button" id="t-browse" class="btn btn-secondary">浏览...</button>
          </div>
        </div>
        <div class="field"><label>简短描述（可选）</label><input id="t-sub" type="text" /></div>
      </div>
      <div class="modal-footer">
        <button class="btn btn-secondary" data-close-modal>取消</button>
        <button id="t-ok" class="btn btn-primary">添加</button>
      </div>
    `);
    $$('input[name="tKind"]').forEach((r) => r.addEventListener("change", () => {
      $("#t-browse").classList.toggle("hidden", $('input[name="tKind"]:checked').value !== "exe");
    }));
    $("#t-browse").addEventListener("click", async () => {
      try {
        const res = await callHost("browseExe");
        if (res && res.path) {
          $("#t-target").value = res.path;
          if (!$("#t-name").value.trim()) $("#t-name").value = res.name || "";
        }
      } catch (_) {}
    });
    $("#t-ok").addEventListener("click", async () => {
      const name = $("#t-name").value.trim();
      const target = $("#t-target").value.trim();
      if (!name || !target) return showToast("请填写名称与路径/网址", true);
      await callHost("addTool", {
        sectionId: $("#t-sec")?.value || "",
        kind: $('input[name="tKind"]:checked').value,
        name,
        target,
        subtitle: $("#t-sub").value.trim(),
      });
      closeModal();
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
      "2026-11-21": "工作台、网络、Codex 和截图完成集中升级，新增 Antigravity 实时额度。",
      "2026-11-20": "AI Hub 初版到工作台合并，加入截图、提字与桌面图标收纳。"
    };
    openModal(`
      <div class="modal-header">
        <h3>更新日志</h3>
        ${iconButton("", "关闭更新日志", ICONS.close, { attrs: "data-close-modal" })}
      </div>
      <div class="modal-body changelog-timeline">
        ${dates.length ? dates.map((day, index) => {
          const [year, month, date] = day.date.split("-");
          const newest = day.versions[0], oldest = day.versions.at(-1);
          return `<details class="changelog-day" ${index === 0 ? "open" : ""}>
            <summary>
              <span class="changelog-date">${year} 年 ${Number(month)} 月 ${Number(date)} 日</span>
              <span class="changelog-version">${oldest === newest ? esc(newest) : `${esc(oldest)} → ${esc(newest)}`}</span>
            </summary>
            ${summaries[day.date] ? `<p class="changelog-summary">${esc(summaries[day.date])}</p>` : ""}
            <div class="changelog-entries">${day.updates.map((update) => `<div class="changelog-entry">
              <span class="changelog-type ${categories[update.type]}">${update.type}</span>
              <span>${esc(update.body)}</span><small>${esc(update.version)}</small>
            </div>`).join("")}</div>
          </details>`;
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
    items.push({ title: "一键隐藏已收纳的 AI 桌面图标", sub: "移入本地收纳夹，随时可一键恢复", tag: "桌面", run: () => postAction("hideAllDesktopShortcuts", { category: "ai" }) });
    items.push({ title: "恢复全部隐藏的桌面图标", sub: "将收纳夹里的快捷方式放回桌面", tag: "桌面", run: () => postAction("restoreAllDesktopShortcuts") });
    items.push({ title: appState.proxy?.running ? "Clash Verge · 打开面板" : "Clash Verge · 启动代理", sub: appState.proxy?.running ? "当前运行中" : "当前未启动", tag: "网络", run: () => postAction("proxyOpen") });
    items.push({ title: "重启代理（Clash Verge）", sub: "网络异常时快速重启通道", tag: "操作", run: () => postAction("proxyRestart") });
    items.push({ title: "刷新额度（所有 Codex 账号）", sub: "查询实时 5h / 每周 / 每月额度", tag: "操作", run: () => postAction("refreshAllUsage") });
    items.push({ title: "一键全部退出", sub: "批量关闭运行中的外部应用与代理", tag: "操作", run: openBatchExitModal });
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
    $("#cmdk-backdrop").classList.remove("hidden");
    $("#cmdk-input").focus();
  }

  function closeCmdk() {
    $("#cmdk-backdrop").classList.add("hidden");
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

  $$(".nav-item").forEach((btn) => btn.addEventListener("click", () => {
    activeTab = btn.dataset.tab;
    closePopover();
    renderAll();
  }));

  $("#btn-open-cmdk").addEventListener("click", openCmdk);
  $("#btn-batch-exit").addEventListener("click", openBatchExitModal);
  $("#btn-open-changelog").addEventListener("click", openChangelogModal);

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
      if ($("#cmdk-backdrop").classList.contains("hidden")) openCmdk();
      else closeCmdk();
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
    const email = event.target.closest(".tool-card").querySelector("[data-web-email]");
    const profile = webIdentity(tool, appState.chrome_profiles || []);
    if (email) email.textContent = profile?.email || "";
  });

  document.addEventListener("click", (e) => {
    if (!e.target.closest("#popover-menu") && !e.target.closest('[data-act="openMoreMenu"], [data-act="openWorkMore"]')) closePopover();
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
    else if (act === "openWorkMore") openWorkMore(actBtn, id);
    else if (act === "refreshAntigravity") { closePopover(); postAction("refreshAntigravity"); }
    else if (act === "goManage") { closePopover(); activeTab = "manage"; renderAll(); }
    else if (act === "copyWorkAddress") { closePopover(); postAction("copyWorkAddress", { tool: id }); }
    else if (["proxyOpen", "proxyRestart", "proxyStop"].includes(act)) {
      closePopover();
      postAction(act);
    }
    else if (act === "launchAccount") postAction("launchAccount", { id });
    else if (act === "stopAccount") postAction("stopAccount", { id });
    else if (act === "calibrate5Days") postAction("calibrate5Days", { id });
    else if (act === "syncConfig") { closePopover(); postAction("syncConfig", { id }); }
    else if (act === "openAccountFolder") { closePopover(); postAction("openAccountFolder", { id }); }
    else if (act === "createAccountShortcut") { closePopover(); postAction("createShortcut", { id, name: actBtn.dataset.name }); }
    else if (act === "pinDesktop") postAction("createShortcut", {});
    else if (act === "openDataDir") postAction("openDataDir");
    else if (act === "openChangelog") openChangelogModal();
    else if (act === "forceQuit") postAction("forceQuit");
    else if (act === "startScreenshot") postAction("startScreenshot");
    else if (act === "startVoiceRecorder") postAction("startVoiceRecorder");
    else if (act === "reregisterHotkey") postAction("reregisterHotkey");
    else if (act === "setScreenshotHotkey") postAction("updateSettings", { screenshot_enabled: true, screenshot_hotkey: actBtn.dataset.hk });
    else if (act === "hideAllAiShortcuts") postAction("hideAllDesktopShortcuts", { category: "ai" });
    else if (act === "restoreAllShortcuts") postAction("restoreAllDesktopShortcuts");
    else if (act === "rescanTools") postAction("rescanTools");
    else if (act === "launchTool") postAction("launchTool", { id });
    else if (act === "stopTool") { closePopover(); postAction("stopTool", { id }); }
    else if (act === "openWebTool") {
      const profile = webIdentity(id, appState.chrome_profiles || []);
      if (profile) postAction("launchChromeUrl", { tool: id, dir: profile.dir });
    }
    else if (act === "openAddAccount") openAccountModal(null);
    else if (act === "openAddSection") openAddSectionModal();
    else if (act === "openAddTool") openAddToolModal();
    else if (act === "openAccountWindow") {
      closePopover();
      postAction("launchAccount", { id });
    } else if (act === "signOutAccount") {
      closePopover();
      postAction("launchAccount", { id, intent: "signOut" });
    } else if (act === "openPermissionMode") {
      closePopover();
      const acc = (appState.accounts || []).find((a) => a.id === id);
      if (acc) openPermissionModal(acc);
    } else if (act === "openEditAccount") {
      const acc = (appState.accounts || []).find((a) => a.id === id);
      if (acc) openAccountModal(acc);
    } else if (act === "openWipe") {
      const acc = (appState.accounts || []).find((a) => a.id === id);
      if (acc) openWipeModal(acc);
    } else if (act === "openMoreMenu") {
      const acc = (appState.accounts || []).find((a) => a.id === id);
      if (acc) openAccountMoreMenu(actBtn, acc);
    } else if (act === "confirmUninstall") {
      let targetApp = null;
      (appState.tool_sections || []).forEach((s) => (s.items || []).forEach((it) => { if (it.id === id) targetApp = it; }));
      if (!targetApp) return;
      openConfirmModal(
        `确认卸载「${targetApp.name}」`,
        `执行方式：${targetApp.uninstall_method}\n程序路径：${targetApp.target}\n\n确认后将调用系统或软件官方卸载程序。`,
        "确认卸载",
        true,
        () => postAction("uninstallTool", { id })
      );
    } else if (act === "confirmHideTool") {
      openConfirmModal(
        `从工作台隐藏「${actBtn.dataset.name}」`,
        "仅从工作台列表隐藏，不会卸载电脑上的软件本体。\n随时可点击「重新扫描本机 AI 软件」恢复显示。",
        "确认隐藏",
        false,
        () => postAction("hideTool", { sectionId: actBtn.dataset.sec, id })
      );
    } else if (act === "confirmDeleteSection") {
      openConfirmModal(
        `删除分区「${actBtn.dataset.title}」`,
        "仅删除此分组显示，不会卸载电脑上的任何软件。\n之后可随时点击「重新扫描本机 AI 软件」恢复。",
        "删除分区",
        false,
        () => postAction("deleteSection", { id })
      );
    }
  });

  document.addEventListener("change", (e) => {
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
        fill.className = `grad-fill tone-${cd.level}`;
        fill.style.width = `${cd.pct <= 0 ? 0 : Math.max(2, cd.pct).toFixed(1)}%`;
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
