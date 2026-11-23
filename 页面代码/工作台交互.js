(() => {
  const webview = window.chrome && window.chrome.webview;
  const pendingReqs = new Map();
  let reqSeq = 1;

  let appState = null;
  let uiReadySent = false;
  let tabRestored = false;
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
  const pendingBacklogChecks = new Map();
  const checklistLine = /^(\s*-\s+)(?:\[([ xX])\]\s+)?(.*)$/;
  let backlogProject = "all";
  let backlogStatus = "all";
  let backlogQuery = "";
  let backlogViewMode = (() => {
    try {
      const saved = localStorage.getItem("aihub_backlog_view");
      return saved === "kanban" ? "kanban" : "split";
    } catch (_) {
      return "split";
    }
  })();
  let selectedBacklogId = "";
  let settingsSection = "settings-hotkeys";
  let settingsTrigger = null;
  let designWarmupQueued = false;
  const defaultBacklogGroups = ["doing", "planned", "idea", "paused", "done"];
  const backlogGroupPreferences = (() => {
    try {
      const saved = JSON.parse(localStorage.getItem("aibase_backlog_groups") || "{}");
      const order = Array.isArray(saved.order) ? saved.order : [];
      const collapsed = Array.isArray(saved.collapsed) ? saved.collapsed : ["done"];
      return {
        order: [...new Set([...order.flatMap((id) => id === "closed" ? ["paused", "done"] : [id]), ...defaultBacklogGroups])]
          .filter((id) => defaultBacklogGroups.includes(id)),
        collapsed: new Set(collapsed.map((id) => id === "closed" ? "done" : id))
      };
    } catch (_) {
      return { order: [...defaultBacklogGroups], collapsed: new Set(["done"]) };
    }
  })();
  let backlogDrag = null;
  const searchCollapsedGroups = new Set();
  let archiveRailOpen = false;
  let archiveRailOverride = null;
  let backlogDrawerId = "";
  let backlogDrawerKey = "";
  const backlogMoves = new Map();
  let networkBusy = "";
  let onNetworkPickerClose = null;

  const $ = (sel) => document.querySelector(sel);
  const $$ = (sel) => Array.from(document.querySelectorAll(sel));
  const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
  const layerExits = new WeakMap();

  const gSvg = (sym, cls = "ic ic-sm") => `<svg class="${cls}" viewBox="0 0 16 16" aria-hidden="true"><use href="#${sym}"/></svg>`;
  const ICONS = {
    power: gSvg("g-power"),
    switch: gSvg("g-switch"),
    refresh: gSvg("g-refresh"),
    plus: gSvg("g-plus"),
    play: gSvg("g-play", "ic ic-xs"),
    more: gSvg("g-more"),
    edit: gSvg("g-edit"),
    sync: gSvg("g-sync"),
    monitor: gSvg("g-monitor"),
    trash: gSvg("g-trash"),
    close: gSvg("g-close"),
    ext: gSvg("g-ext", "ic ic-xs"),
    crop: gSvg("g-crop"),
    mic: gSvg("g-mic"),
    ledger: gSvg("g-ledger"),
    changelog: gSvg("g-list"),
    chevronDown: gSvg("g-chev", "ic ic-xs"),
    copy: gSvg("g-copy"),
    folder: gSvg("g-folder"),
    pin: gSvg("g-pin"),
    eye: gSvg("g-eye"),
    eyeOff: gSvg("g-eye-off"),
    key: gSvg("g-key"),
    bolt: gSvg("g-bolt", "ic ic-xs"),
    node: gSvg("g-node"),
    term: gSvg("g-term"),
    workspace: gSvg("g-workspace"),
    grip: gSvg("g-grip"),
  };
  const PULSE_LOADER = `<span class="dot-loader" aria-hidden="true"><span></span><span></span><span></span></span>`;

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

  function showToast(text, isErrorOrOpts = false) {
    if (!text) return;
    const opts = typeof isErrorOrOpts === "object" && isErrorOrOpts !== null
      ? isErrorOrOpts
      : { isError: !!isErrorOrOpts };
    const isError = !!opts.isError;
    const ms = opts.ms || 3800;
    const host = $("#toast-host");
    while (host.children.length >= 3) host.firstElementChild.remove();

    const el = document.createElement("div");
    el.className = "toast" + (isError ? " err" : "");
    el.style.setProperty("--toast-ms", `${ms}ms`);
    el.innerHTML = `
      <span class="toast-left"><span>${esc(text)}</span></span>
      ${opts.undo ? `<button type="button" class="toast-undo">撤销</button>` : ""}
      <div class="toast-progress"><div class="toast-progress-bar"></div></div>`;
    if (opts.undo) {
      el.querySelector(".toast-undo").addEventListener("click", () => {
        opts.undo();
        el.remove();
      });
    }
    el.querySelector(".toast-progress-bar").addEventListener("animationend", () => el.remove());
    host.appendChild(el);
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
    const remainMs = endMs - Date.now();
    const expired = remainMs <= 0;
    const abs = Math.abs(remainMs);

    const d = Math.floor(abs / 86400000);
    const h = Math.floor((abs % 86400000) / 3600000);
    const m = Math.floor((abs % 3600000) / 60000);
    const s = Math.floor((abs % 60000) / 1000);
    const level = (expired || remainMs <= 48 * 3600000) ? "danger" : (remainMs <= 5 * 86400000 ? "warn" : "ok");
    const p2 = (n) => String(n).padStart(2, "0");
    const compact = expired ? `已超期 ${d}天 ${p2(h)}:${p2(m)}:${p2(s)}` : `${d}天 ${p2(h)}时 ${p2(m)}分 ${p2(s)}秒`;
    const meterValue = d > 0 ? `${d}天 ${p2(h)}时` : (h > 0 ? `${h}时 ${p2(m)}分` : `${m}分 ${p2(s)}秒`);
    return { expired, level, compact, meterValue, targetShort: fmtDate(acc.recycle_at, true) };
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
    if (!auth || !auth.logged_in) return `<span class="stamp sand">待登录</span>`;
    const p = String(auth.plan_type || "").toLowerCase();
    if (p === "plus") return `<span class="stamp mint">Plus</span>`;
    if (p === "pro") return `<span class="stamp sky">Pro</span>`;
    if (p === "team" || p === "enterprise") return `<span class="stamp sky">${esc(p.toUpperCase())}</span>`;
    if (p === "free") return `<span class="stamp stone">Free</span>`;
    return `<span class="stamp stone">${esc(p || "已登录")}</span>`;
  }

  // ==================== RENDERING ====================

  function renderAll() {
    if (!appState) return;
    renderSidebarTools();

    const brandLogBtn = $("#brand-log-btn");
    if (brandLogBtn && appState.version) {
      brandLogBtn.title = `更新日志 · ${appState.version}`;
    }

    const pending = !!appState.pending_update;
    const refreshingProjects = activeTab === "projects" && !!appState.projects_refreshing;
    const refreshingWorkspace = activeTab === "workspace"
      && ((appState.accounts || []).some((a) => a.usage_loading) || !!appState.antigravity?.loading);
    const headerActions = $("#header-actions");
    const headerKey = `${activeTab}|${pending}|${refreshingProjects}|${refreshingWorkspace}|${appState.version || ""}`;
    if (headerKey !== headerActions.dataset.key) {
      const parts = [];
      if (activeTab === "workspace") {
        parts.push(`<button type="button" class="btn btn-paper btn-sm" style="min-width:112px;" data-act="refreshAllQuotas" aria-disabled="${refreshingWorkspace}" aria-busy="${refreshingWorkspace}">${refreshingWorkspace ? PULSE_LOADER + " <span>刷新中</span>" : ICONS.refresh + " <span>刷新全部额度</span>"}</button>`);
      } else if (activeTab === "projects") {
        parts.push(`<button type="button" class="btn btn-paper btn-sm btn-w-pair" data-act="refreshProjects" ${refreshingProjects ? "disabled" : ""}>${refreshingProjects ? PULSE_LOADER : ICONS.refresh + " <span>检测 Git</span>"}</button>`);
        parts.push(`<button type="button" class="btn btn-dark btn-sm btn-w-pair" data-act="openAddProject">${ICONS.plus} <span>导入项目</span></button>`);
      } else if (activeTab === "backlog") {
        parts.push(`<button type="button" class="btn btn-paper btn-sm btn-w-pair" data-act="exportBacklogMd"><span>导出 MD</span></button>`);
        parts.push(`<button type="button" class="btn btn-dark btn-sm btn-w-pair" data-act="openAddBacklog">${ICONS.plus} <span>新建需求</span></button>`);
      } else if (activeTab === "design") {
        parts.push(`<button type="button" class="btn btn-paper btn-sm" data-act="openDesignCopyMenu" data-tip="一键复制可独立运行的设计系统 HTML 源码">${ICONS.copy} <span>复制代码</span> ${ICONS.chevronDown}</button>`);
        parts.push(`<button type="button" class="btn btn-dark btn-sm" data-act="openDesignSystemExternal" data-tip="在默认浏览器中全屏打开设计系统.html"><span>浏览器打开</span> ${ICONS.ext}</button>`);
      }
      if (pending) {
        parts.push('<button class="btn btn-mint btn-sm" data-act="applyLocalUpdate">重启应用更新</button>');
      }
      const restoreFocus = rememberFocus(headerActions);
      headerActions.innerHTML = parts.join("");
      restoreFocus();
      headerActions.dataset.key = headerKey;
    }
    $$(".nav-item").forEach((el) => {
      const selected = el.dataset.tab === activeTab;
      el.classList.toggle("active", selected);
      if (selected) el.setAttribute("aria-current", "page");
      else el.removeAttribute("aria-current");
    });

    const view = $("#view-content");
    view.hidden = activeTab === "design";
    $("#design-view").hidden = activeTab !== "design";

    if (activeTab === "workspace") renderUnifiedWorkspace();
    else if (activeTab === "projects") renderProjects();
    else if (activeTab === "backlog") renderBacklog();
    else if (activeTab === "design") renderDesignSystem();
    if (!designWarmupQueued && activeTab !== "design") {
      designWarmupQueued = true;
      window.requestIdleCallback(mountDesignSystem, { timeout: 1200 });
    }
    if (layerIsOpen($("#settings-backdrop"))) renderManage();
  }

  function renderDesignSystem() {
    $("#view-title").textContent = "设计系统";
    $("#view-sub").textContent = "v1.2 松烟暖墨 · 0px 建筑线稿规范 (01–10)";
    mountDesignSystem();
  }

  function mountDesignSystem() {
    const view = $("#design-view");
    if (view.querySelector("#design-sys-frame")) return;
    const frame = document.createElement("iframe");
    frame.id = "design-sys-frame";
    frame.className = "design-sys-frame";
    frame.title = "AI BASE & LIFE BASE 设计系统";
    frame.src = "/%E8%AE%BE%E8%AE%A1%E7%B3%BB%E7%BB%9F.html?embed=1";
    frame.hidden = true;
    frame.addEventListener("load", () => {
      $("#design-loading").hidden = true;
      frame.hidden = false;
    }, { once: true });
    view.append(frame);
  }

  function switchTab(tab) {
    if (tab === "manage") { openSettings(); return; }
    closePopover();
    closeInlineNodeCombo();
    if (activeTab === tab) return;
    if (projectDrag) finishProjectDrag(false);
    if (backlogDrag) finishBacklogDrag(false);
    activeTab = tab;
    try { localStorage.setItem("aihub_last_tab", tab); } catch (_) { }
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
    if (tab === "projects") postAction("refreshProjects");
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
    const shotKey = shot?.enabled && shot.registered ? (shot.hotkey || "").replace(/\s*\+\s*/g, "+") : "";
    const voiceKey = (appState.voice_recorder_hotkey || "").replace(/\s*\+\s*/g, "+");
    const ledger = appState.quick_ledger_forms?.[0];
    const key = JSON.stringify([shotKey, voiceKey, ledger]);
    if (key === sidebarToolsKey) return;
    sidebarToolsKey = key;
    $("#sidebar-tools").innerHTML = `
      <div class="tools-stack">
        <button type="button" class="tool-tile" data-act="startScreenshot" data-tip="${esc(shotKey ? `截图 (${shotKey})` : "截图")}">
          <span class="tool-tile-left">${ICONS.crop}<span>截图</span></span>
          ${shotKey ? `<span class="tool-tile-key mono">${esc(shotKey)}</span>` : ""}
        </button>
        <button type="button" class="tool-tile" data-act="startVoiceRecorder" data-tip="${esc(voiceKey ? `录音 (${voiceKey})` : "录音不可用")}" ${voiceKey ? "" : "disabled"}>
          <span class="tool-tile-left">${ICONS.mic}<span>录音</span></span>
          ${voiceKey ? `<span class="tool-tile-key mono">${esc(voiceKey)}</span>` : ""}
        </button>
        ${ledger ? `<button type="button" class="tool-tile" data-act="openLedgerForm" data-id="${esc(ledger)}" data-tip="打开飞书账本">
          <span class="tool-tile-left">${ICONS.ledger}<span>记账</span></span>
          <span class="tool-tile-key">飞书</span>
        </button>` : ""}
      </div>`;
  }

  function iconButton(action, label, glyph, { disabled = false, danger = false, spinning = false, attrs = "" } = {}) {
    return `<button type="button" class="btn btn-flat icon-tool ${danger ? "danger" : ""}${spinning ? " is-spinning" : ""}"
      ${action ? `data-act="${action}"` : ""} title="${esc(label)}" aria-label="${esc(label)}" ${disabled ? "disabled" : ""} ${attrs}>${glyph}</button>`;
  }

  function statusHtml(label, tone = "") {
    return `<span class="stamp ${tone === "running" || tone === "healthy" ? "semantic-ok" : tone === "status-error" ? "coral" : "stone"}"><span class="status-dot ${tone}"></span>${esc(label)}</span>`;
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
    return `<div class="meter-block slot${action ? " meter-with-action" : ""}">
      <div class="meter-top">
        <span class="meter-name" ${labelAttrs}>${esc(title)}</span>
        <strong class="meter-pct mono${tone ? ` tone-${tone}` : ""}" ${valueAttrs}>${esc(value)}</strong>
      </div>
      ${percent == null ? '<div class="grad-track"></div>' : renderGradientBar(percent, tone, fillAttrs)}
      <div class="meter-foot">
        <span class="meter-reset mono" ${timeAttrs}>${esc(when)}</span>
        ${action}
      </div>
    </div>`;
  }

  function quotaMessage(message, type = "", action = "") {
    return `<div class="quota-feedback ${type}" role="status"><span>${esc(message)}</span>${action}</div>`;
  }

  function updatedAtHtml(timestamp, automatic = false, inline = false) {
    if (!timestamp) return "";
    const date = new Date(timestamp);
    if (Number.isNaN(date.getTime())) return "";
    const label = date.toDateString() === new Date().toDateString()
      ? date.toLocaleTimeString("zh-CN", { hour: "2-digit", minute: "2-digit", hour12: false })
      : fmtDate(timestamp, true);
    return `<time class="quota-updated mono${inline ? " quota-updated-inline" : ""}" datetime="${esc(timestamp)}" title="${esc(fmtDate(timestamp))}">更新 ${esc(label)}${automatic ? " · 自动" : ""}</time>`;
  }

  function desktopActions(tool, name, launchAction, stopAction, startStyle = "btn-dark") {
    const id = esc(tool?.id);
    const attrs = `data-id="${id}"`;
    const running = !!tool?.running;
    return `<button type="button" class="btn ${running ? "btn-focus" : startStyle} btn-sm btn-w-act" data-act="${running ? stopAction : launchAction}" ${attrs} ${id ? "" : "disabled"}>
      ${running ? ICONS.power + " <span>关闭</span>" : ICONS.play + " <span>启动</span>"}</button>`;
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
    return `<section class="surface-card block tool-card compact-tool" aria-labelledby="tool-${tool.id}">
      <header class="block-head">
        ${toolHeading("tool-" + tool.id, tool.icon, tool.name, "")}
        <div class="tool-actions">
          <button type="button" class="btn btn-paper btn-sm btn-w-act" data-act="openWebTool" data-id="${tool.id}" aria-label="打开 ${esc(tool.name)}" ${available ? "" : "disabled"}><span>打开</span> ${ICONS.ext}</button>
        </div>
      </header>
      <div class="tool-card-body">
        <div class="compact-identity">
          <button type="button" class="web-id-btn" data-act="openWebIdentityMenu" data-id="${tool.id}" data-tip="切换 Chrome 身份 (${esc(profile?.email || "默认")})" ${available ? "" : "disabled"}>
            <span>Web · ${esc(profile?.name || "默认")}</span>${ICONS.chevronDown}
          </button>
          <span class="tool-identity-email mono" data-web-email="${tool.id}" title="${esc(profile?.email)}">${esc(profile?.email || "—")}</span>
        </div>
      </div>
    </section>`;
  }

  function renderOpenDesign(tool) {
    const ready = !!tool;
    return `<section class="surface-card block tool-card compact-tool" aria-labelledby="tool-open-design">
      <header class="block-head">
        ${toolHeading("tool-open-design", tool?.icon_url, "Open Design", "")}
        <div class="tool-actions">
          ${desktopActions(tool, "Open Design", "launchTool", "stopTool", "btn-dark")}
        </div>
      </header>
      <div class="tool-card-body compact-purpose">
        <span class="compact-identity">
          ${statusHtml(ready ? (tool.running ? "运行中" : "未运行") : "未安装", tool?.running ? "running" : "")}
          <span class="tool-identity-email">本地桌面端 · 原型工作区</span>
        </span>
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
    const groups = Object.keys(quota).length ? antigravityGroups(quota).map((group) => {
      let hasDanger = false;
      const metersHtml = group.buckets.map((key) => {
        const bucket = quota[key];
        const percent = typeof bucket?.remaining_fraction === "number" && !bucket.disabled
          ? Math.min(100, Math.max(0, bucket.remaining_fraction * 100)) : null;
        const tone = percent == null ? "" : quotaTone(percent);
        if (tone === "danger") hasDanger = true;
        const title = key.endsWith("-weekly") ? "每周额度" : (key.endsWith("-5h") ? "5h 额度" : "额度");
        const value = percent != null ? percent.toLocaleString("zh-CN", { maximumFractionDigits: 2 }) + "%"
          : bucket?.disabled ? "不可用" : (typeof bucket?.remaining_amount === "number" ? Number(bucket.remaining_amount).toLocaleString("zh-CN") : "—");
        return renderQuotaMeter({ title, value, when: bucket ? timeToReset(bucket.reset_time) : "暂不可读取",
          percent, tone,
          fillAttrs: `data-quota-fill="antigravity:${esc(key)}"`,
          timeAttrs: bucket?.reset_time ? `data-tip="预计 ${esc(fmtDate(bucket.reset_time))}"` : "" });
      }).join("");
      const single = group.buckets.length === 1;
      return `
      <div class="antigravity-quota-group ${hasDanger ? "slot-alert" : "slot"}">
        <div class="ag-group-head">
          <h3>${esc(group.name)}</h3>
          ${hasDanger ? '<span class="stamp quota-alert-badge"><span class="dot-danger-sq" aria-hidden="true"></span><span>额度告急</span></span>' : ""}
        </div>
        <div class="meter-grid${single ? " meter-grid-single" : ""}">${metersHtml}</div>
      </div>`;
    }).join("") : "";
    const error = tool.error
      ? quotaMessage(tool.error, "quota-error", tool.cli_installed ? '<button class="btn btn-flat btn-sm" data-act="refreshAntigravity">重试</button>' : "")
      : "";
    // Never insert a layout-shifting "正在读取额度…" banner when quota groups are already on screen!
    const info = (!groups && !error)
      ? (tool.loading ? quotaMessage("正在读取额度…") : quotaMessage(tool.cli_installed ? "点击刷新额度" : "暂不可读取 · CLI 未安装"))
      : "";
    return `<section class="surface-card block tool-card antigravity-tool" aria-labelledby="tool-antigravity">
      <header class="block-head">
        ${toolHeading("tool-antigravity", tool.icon_url, "ANTIGRAVITY", "",
          statusHtml(tool.id ? (tool.running ? "运行中" : "未运行") : "未安装", tool.running ? "running" : ""))}
        <div class="tool-actions">
          ${updatedAtHtml(usage?.fetched_at, tool.automatic)}
          <button type="button" class="btn btn-paper btn-sm btn-w-act" data-act="refreshAntigravity"
            aria-busy="${!!tool.loading}" aria-label="刷新 Antigravity 额度" ${tool.loading || !tool.cli_installed ? "disabled" : ""}>${tool.loading ? PULSE_LOADER : ICONS.refresh + " <span>刷新</span>"}</button>
          ${desktopActions(tool, "Antigravity", "launchTool", "stopTool")}
        </div>
      </header>
      ${tool.loading && groups ? '<div class="card-marquee" aria-hidden="true"></div>' : ""}
      <div class="tool-card-body ag-body">
        ${updatedAtHtml(usage?.fetched_at, tool.automatic, true)}
        ${usage?.email || usage?.plan ? `<div class="ag-account-strip"><span class="mono">${esc(usage.email || "")}</span><span>${esc(usage.plan || "")}</span></div>` : ""}
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
    const isDegraded = proxy.running && (state === "timeout" || state === "unavailable");
    const isRefreshing = networkBusy === "refresh";
    const isSwitching = !!networkBusy && !isRefreshing;
    const capCls = isDegraded ? "is-degraded" : (!proxy.running ? "" : (state === "reachable" ? "is-ok" : "is-warn"));
    const dotCls = isDegraded ? "is-danger" : (!proxy.running ? "" : (state === "reachable" ? "is-ok" : ""));
    const lifecycleLabel = ({ proxyStop: "正在关闭代理", proxyRestart: "正在重启代理" })[networkBusy];
    const label = isSwitching ? lifecycleLabel || "切换/测速中" : (!proxy.running ? "代理已关闭" : (isDegraded ? "节点超时" : (state === "reachable" ? "网络正常" : "网络待检测")));

    const delayTone = networkDelayTone(state, network.delay);
    const latency = !proxy.running ? "—" : state === "timeout" ? "TIMEOUT" : state === "unavailable" ? "不可用"
      : (network.delay != null && !network.delay_stale ? network.delay + " ms" : "—");
    const lastDelay = proxy.running && network.delay_stale ? " · 上次 " + network.delay + " ms" : "";
    const mode = ({ rule: "规则路由", global: "全局代理", direct: "直连" })[network.mode];
    const policy = proxy.running ? network.group || mode : "";
    const node = proxy.running ? network.node || "节点未知" : "本地代理未启动";
    const subscription = network.subscription;
    const traffic = subscription ? "剩余 " + formatTraffic(subscription.total_bytes - subscription.used_bytes) : "流量待获取";
    const trafficDetails = subscription
      ? ["NIVA Network", "已用 " + formatTraffic(subscription.used_bytes), "总量 " + formatTraffic(subscription.total_bytes),
        subscription.expires_at ? "到期 " + fmtDate(subscription.expires_at) : ""].filter(Boolean).join(" · ")
      : "NIVA Network";

    const canInlinePick = proxy.running && network.can_select && appState.network_japan_picker && !networkBusy;
    const refreshLabel = isRefreshing ? "刷新中…" : "刷新网络状态";

    let cellsHtml = "";
    let actionsHtml = "";
    if (isSwitching) {
      cellsHtml = `<span class="net-cell">${PULSE_LOADER}<span>${lifecycleLabel ? `${lifecycleLabel}…` : "正在探测并应用网络状态…"}</span></span>`;
      actionsHtml = `<button type="button" class="btn btn-flat btn-sm" disabled>处理中</button>`;
    } else if (!proxy.running) {
      cellsHtml = `<span class="net-cell" style="color:var(--text-muted)">本地 Clash Verge 未启动，点击右侧拉起</span>`;
      actionsHtml = `<button type="button" class="btn btn-dark btn-sm" data-act="proxyOpen" ${!proxy.installed ? "disabled" : ""}>${ICONS.play} 启动代理</button>`;
    } else if (isDegraded) {
      cellsHtml = `
        ${policy ? `<span class="net-cell"><span class="net-k">策略</span><strong>${esc(policy)}</strong></span>` : ""}
        <span class="net-cell net-node"><button type="button" class="net-node-btn mono" title="${esc(node)}" data-act="toggleInlineNodeCombo" aria-expanded="false" ${canInlinePick ? "" : "disabled"}><span>${esc(node)}</span>${canInlinePick ? ICONS.chevronDown : ""}</button></span>
        <span class="net-cell">${isRefreshing ? PULSE_LOADER : `<span class="stamp coral mono">${esc(latency)}</span>`}</span>`;
      actionsHtml = `
        <button type="button" class="btn btn-dark btn-sm" data-act="rescueFastestNode">${ICONS.bolt} 一键换最快节点</button>
        <span class="net-sep" aria-hidden="true"></span>
        ${iconButton("proxyRestart", "重启代理", ICONS.sync, { danger: true })}
        ${iconButton("proxyStop", "关闭代理", ICONS.power, { danger: true, attrs: 'data-critical="true"' })}`;
    } else {
      cellsHtml = `
        ${policy ? `<span class="net-cell"><span class="net-k">策略</span><strong>${esc(policy)}</strong></span>` : ""}
        <span class="net-cell net-node" data-tip="点击就地切换节点">${canInlinePick
          ? `<button type="button" class="net-node-btn mono" title="${esc(node)}" data-act="toggleInlineNodeCombo" aria-expanded="false"><span>${esc(node)}</span>${ICONS.chevronDown}</button>`
          : `<strong class="mono">${esc(node)}</strong>`}</span>
        <span class="net-cell" data-tip="当前节点延迟${esc(lastDelay)}">${isRefreshing ? PULSE_LOADER : `<span class="net-latency ${delayTone} mono">${esc(latency)}</span>${lastDelay ? `<small class="mono">${esc(lastDelay)}</small>` : ""}`}</span>
        <span class="net-cell net-traffic mono" data-tip="${esc(trafficDetails)}">${esc(traffic)}</span>`;
      actionsHtml = `
        ${iconButton("refreshNetwork", refreshLabel, isRefreshing ? PULSE_LOADER : ICONS.refresh, { disabled: !appState.network_status_refresh || isRefreshing })}
        ${iconButton("openNetworkPicker", "打开完整节点选择器", ICONS.switch, { disabled: !canInlinePick })}
        ${iconButton("proxyOpen", "打开 Clash Verge", ICONS.ext)}
        <span class="net-sep" aria-hidden="true"></span>
        ${iconButton("proxyRestart", "重启代理", ICONS.sync, { danger: true })}
        ${iconButton("proxyStop", "关闭代理", ICONS.power, { danger: true, attrs: 'data-critical="true"' })}`;
    }

    return `
      <section class="surface-card net-bar" aria-label="网络状态">
        <div class="net-cap ${capCls}"${subscription ? ` title="${esc(traffic)} · ${esc(trafficDetails)}"` : ""}><span class="status-dot ${dotCls}"></span>${label}</div>
        <div class="net-cells">${cellsHtml}</div>
        <div class="net-actions" role="group" aria-label="网络工具栏">${actionsHtml}</div>
      </section>`;
  }

  function renderCodex(accounts) {
    const loading = accounts.some((account) => account.usage_loading);
    const latestTs = accounts.map((a) => a.usage?.fetched_at).filter(Boolean).sort().at(-1);
    const anyAuto = accounts.some((a) => a.usage?.automatic);
    return `<section class="surface-card block tool-card codex-tool" aria-labelledby="codex-tool-title">
      <header class="block-head">
        ${toolHeading("codex-tool-title", "https://icons.aihub.local/chatgpt.png", "CODEX", "", `<span class="stamp mono">${accounts.length} 账号</span>`)}
        <div class="tool-actions">
          ${updatedAtHtml(latestTs, anyAuto)}
          <button type="button" class="btn btn-paper btn-sm btn-w-pair" data-act="refreshAllUsage" aria-busy="${loading}" ${loading ? "disabled" : ""} data-tip="刷新所有 Codex 账号的额度">${loading ? PULSE_LOADER : ICONS.refresh + " <span>刷新额度</span>"}</button>
          <button type="button" class="btn btn-dark btn-sm btn-w-pair" data-act="openAddAccount">${ICONS.plus} <span>添加账号</span></button>
        </div>
      </header>
      ${loading ? '<div class="card-marquee" aria-hidden="true"></div>' : ""}
      <div class="codex-accounts">${updatedAtHtml(latestTs, anyAuto, true)}${accounts.length ? accounts.map(renderCodexAccount).join("") : quotaMessage("暂无账号")}</div>
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
    else if (acc.usage_loading && !windows.length) feedback = quotaMessage("正在查询额度…");
    else if (acc.usage_error && !windows.length) feedback = quotaMessage(acc.usage_error, "quota-error",
      `<button class="btn btn-flat btn-sm" data-act="refreshUsage" data-id="${esc(acc.id)}" aria-label="重试 ${esc(name)} 的额度查询">重试</button>`);
    else if (!windows.length) feedback = quotaMessage(usage ? "当前套餐暂无独立额度窗口" : "暂无额度数据");

    const meters = windows.map(([slot, w]) => {
      const percent = Math.max(0, Math.min(100, Number(w.left_percent) || 0));
      return renderQuotaMeter({ title: String(w.label).replace("限额", "额度"), value: percent + "%",
        when: w.reset_text || "重置时间未知", percent, tone: quotaTone(percent),
        valueAttrs: `aria-label="剩余 ${percent}%"`, fillAttrs: `data-quota-fill="codex:${esc(acc.id)}:${slot}"`,
        timeAttrs: w.reset_at ? `data-tip="预计 ${esc(fmtDate(w.reset_at))}"` : "" });
    });
    const officialEnd = new Date(auth.subscription_until || "").getTime();
    const recycleEnd = new Date(acc.recycle_at || "").getTime();
    const needsCalibration = !!cd && !acc.is_default && Number.isFinite(officialEnd) && Number.isFinite(recycleEnd)
      && Math.abs(recycleEnd - (officialEnd - 5 * 86400000)) > 6 * 3600000;
    const recycle = cd ? `<div class="account-recycle">
      <span data-cd-status="${esc(acc.id)}">${esc(recycleStatus(cd))}</span>
      <strong class="mono tone-${cd.level}" data-cd-clock="${esc(acc.id)}" title="${esc(cd.compact)}">${esc(cd.meterValue)}</strong>
      <span>截止 <time datetime="${esc(acc.recycle_at)}" title="${esc(fmtDate(acc.recycle_at))}">${esc(cd.targetShort)}</time></span>
      ${needsCalibration ? `<button class="btn btn-flat btn-xs" data-act="openEditAccount" data-id="${esc(acc.id)}" data-tip="当前回收时间与到期前 5 天相差超过 6 小时">校准时间</button>` : ""}
    </div>` : "";

    return `<article class="codex-account">
      <header class="account-top">
        <div class="app-info">
          <div class="app-name-line">
            <span class="status-dot ${acc.running ? "running" : ""}"></span>
            <h3 class="app-name account-name">${esc(name)}</h3>
            ${acc.is_default ? '<span class="stamp stone">默认</span>' : ""}
            ${planTagHtml(auth)}
          </div>
          <div class="app-sub account-email mono">${esc(email)}</div>
        </div>
        <div class="account-actions">
          ${desktopActions(acc, name, "launchAccount", "stopAccount")}
          ${iconButton("openMoreMenu", "更多 " + name + " 操作", ICONS.more, { attrs: `data-id="${esc(acc.id)}"` })}
        </div>
      </header>
      <div class="account-usage">
        ${feedback}
        ${meters.length ? `<div class="meter-grid${windows.length === 1 ? " meter-grid-single" : ""}">${meters.join("")}</div>` : ""}
        ${recycle}
      </div>
    </article>`;
  }
  function rememberFocus(container) {
    const focus = document.activeElement;
    if (!focus || !container.contains(focus)) return () => {};
    const data = Object.entries(focus.dataset);
    if (!focus.id && !data.length) return () => {};
    return () => {
      const target = focus.id ? document.getElementById(focus.id)
        : [...container.querySelectorAll("button, input, select, [tabindex]")].find((candidate) =>
          data.every(([key, value]) => candidate.dataset[key] === value));
      if (target && container.contains(target) && !target.closest("[inert]")) target.focus({ preventScroll: true });
    };
  }

  function syncWorkspaceAfterInteraction() {
    queueMicrotask(() => {
      if (activeTab === "workspace" && appState) renderUnifiedWorkspace();
    });
  }

  function renderUnifiedWorkspace() {
    const proxy = appState.proxy || {};
    const accounts = appState.accounts || [];
    const chromes = appState.chrome_profiles || [];
    const design = findOpenDesign();
    const network = appState.network || {};
    const running = Number(accounts.some((acc) => acc.running)) + Number(!!appState.antigravity?.running) + Number(!!design?.running);
    const proxyTxt = proxy.running
      ? ((network.state === "timeout" || network.state === "unavailable") ? "代理超时" : "代理已连")
      : "代理未连";
    $("#view-title").textContent = "工作台";
    $("#view-sub").innerHTML = running
      ? `<span class="header-sub-dot" aria-hidden="true"></span><span>${running} 应用运行中 · ${proxyTxt}</span>`
      : `<span>全部就绪 · ${proxyTxt}</span>`;

    const view = $("#view-content");
    if (activeComboEl || layerIsOpen($("#popover-menu")) || (lastModalTrigger && view.contains(lastModalTrigger))) return;
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
    const restoreFocus = rememberFocus(view);
    workspaceRenderKey = key;
    view.dataset.page = "workspace";
    view.innerHTML = `
      ${renderNetwork(proxy)}
      <div class="ws-monitoring">
        ${renderCodex(accounts)}
        ${renderAntigravity()}
      </div>
      <section class="block surface-card work-launchers" aria-labelledby="work-launchers-title">
        <header class="block-head"><h2 id="work-launchers-title">应用与网页入口</h2></header>
        <div class="work-tools-grid">
          ${webTools.map((tool) => renderWebTool(tool, chromes)).join("")}
          ${renderOpenDesign(design)}
        </div>
      </section>`;
    restoreFocus();
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
    for (const input of view.querySelectorAll("[data-quiet-time]")) {
      if (document.activeElement !== input) input.value = settings[input.dataset.quietTime] || input.defaultValue;
      input.disabled = !settings.quiet_quota_alerts;
    }
    for (const button of view.querySelectorAll("[data-hk]")) {
      const active = button.dataset.hk === shot.hotkey;
      button.classList.toggle("active", active);
      button.setAttribute("aria-pressed", String(active));
    }
    const status = view.querySelector("#settings-hotkey-status");
    status.className = "stamp" + (shot.enabled ? (shot.registered ? " semantic-ok" : " semantic-warn") : " stone");
    status.innerHTML = !shot.enabled
      ? "<span>快捷键已关闭</span>"
      : (shot.registered ? '<span class="dot-ok-sq" aria-hidden="true"></span><span>热键已生效</span>' : '<span class="dot-warn-sq" aria-hidden="true"></span><span>热键被占用</span>');
    const alert = view.querySelector("#settings-hotkey-alert");
    alert.hidden = !(shot.enabled && !shot.registered && shot.error);
    alert.querySelector("span").textContent = shot.error || "";

    const byId = new Map(shortcuts.map((shortcut) => [shortcut.id, shortcut]));
    for (const seg of view.querySelectorAll("[data-stash-seg]")) {
      const hidden = !!byId.get(seg.dataset.stashSeg)?.hidden;
      for (const btn of seg.querySelectorAll("[data-act='setDesktopShortcutHidden']")) {
        const active = (btn.dataset.hidden === "true") === hidden;
        btn.classList.toggle("is-active", active);
        btn.setAttribute("aria-pressed", String(active));
      }
    }
    const hiddenCount = shortcuts.filter((shortcut) => shortcut.hidden).length;
    const aiShortcuts = shortcuts.filter((shortcut) => shortcut.category === "ai");
    const aiHiddenCount = aiShortcuts.filter((shortcut) => shortcut.hidden).length;
    view.querySelector("#stash-count").textContent = `已收纳 ${hiddenCount}/${shortcuts.length}`;
    const hideAllBtn = view.querySelector('[data-act="hideAllAiShortcuts"]');
    const allAiHidden = aiShortcuts.length > 0 && aiHiddenCount === aiShortcuts.length;
    hideAllBtn.disabled = allAiHidden || aiShortcuts.length === 0;
    hideAllBtn.textContent = allAiHidden ? `AI 图标已全收纳 (${aiHiddenCount}/${aiShortcuts.length})` : "一键收纳 AI 图标";
    view.querySelector('[data-act="restoreAllShortcuts"]').disabled = hiddenCount === 0;
    const customBtn = view.querySelector("#custom-hotkey-btn");
    if (customBtn && !customBtn.classList.contains("is-recording")) {
      const isPreset = ["Ctrl+Shift+A", "Alt+A", "Ctrl+Alt+A", "F1"].includes(shot.hotkey);
      customBtn.classList.toggle("active", !isPreset && !!shot.hotkey);
      customBtn.textContent = !isPreset && shot.hotkey ? shot.hotkey : "自定义…";
    }
  }

  // ==================== PROJECTS PAGE ====================

  function normBrandText(text) {
    return String(text || "")
      .replace(/\bAI Hub\b/gi, "AI BASE")
      .replace(/\bLife Hub\b/gi, "LIFE BASE");
  }

  function projectVisualTheme(item) {
    const key = `${item.name || ""} ${item.folder_name || ""}`.toLowerCase();
    if (key.includes("ai hub") || key.includes("ai-hub") || key.includes("ai base")) return { svg: ICONS.workspace };
    if (key.includes("astra") || key.includes("unknown")) return { svg: ICONS.node };
    if (key.includes("runtime")) return { svg: ICONS.term };
    if (key.includes("ledger") || key.includes("life")) return { svg: ICONS.ledger };
    return { svg: ICONS.folder };
  }

  function compactPathLabel(fullPath) {
    if (!fullPath) return "";
    const parts = String(fullPath).split(/[\\/]+/).filter(Boolean);
    if (parts.length <= 3) return fullPath;
    return `${parts[0]}\\...\\${parts.slice(-2).join("\\")}`;
  }

  function renderProjectCardsHtml(items) {
    if (items.length === 0) {
      return '<section class="surface-card block"><div class="backlog-empty">暂无登记项目，点击右上角「+ 导入项目」添加到本地列表。</div></section>';
    }
    return `<div class="projects-grid">${items.map((item) => {
      const theme = projectVisualTheme(item);
      const displayName = normBrandText(item.name);
      const isDefaultBranch = item.branch === "main" || item.branch === "master";
      let branch = "";
      let statusMeta = "";
      if (!item.exists) {
        statusMeta = `<span class="stamp project-git-status coral">路径缺失</span>`;
      } else if (item.is_git) {
        if (item.dirty_count > 0) {
          const count = Number(item.dirty_count);
          const label = count > 99 ? "99+ 改动" : `${count} 处改动`;
          statusMeta = `<span class="stamp project-git-status semantic-warn" title="${count} 处改动"><span class="dot-warn-sq" aria-hidden="true"></span>${label}</span>`;
        } else {
          const branchClass = !isDefaultBranch && item.branch ? "stamp mono" : "project-clean-meta mono";
          branch = `<span class="${branchClass}">${esc(item.branch || "main")}</span>`;
          statusMeta = `<span class="stamp project-git-status semantic-ok"><span class="dot-ok-sq" aria-hidden="true"></span>干净</span>`;
        }
      } else {
        statusMeta = `<span class="stamp project-git-status">本地目录</span>`;
      }

      const descText = normBrandText(item.notes || (item.is_git ? "本地 Git 仓库，可快速复制路径指给 AI 助手处理。" : "本地工作目录，支持快速复制路径与打开目录。"));
      const previewState = appState.project_previews?.[item.id];
      const previewBusy = previewState === "busy";
      const previewRunning = previewState === "running";
      const portSuffix = item.preview_port ? ` :${item.preview_port}` : "";
      const previewBtn = item.has_preview
        ? `<button type="button" class="btn ${previewRunning ? "btn-focus" : "btn-dark"} btn-sm btn-w-pair" data-act="${previewRunning ? "stopProjectPreview" : "launchProjectPreview"}" data-id="${esc(item.id)}" ${previewBusy ? 'disabled aria-busy="true"' : ""}>${previewBusy ? PULSE_LOADER : (previewRunning ? ICONS.power : ICONS.play)}<span>${previewBusy ? "处理中" : previewRunning ? `关闭${portSuffix}` : `启动${portSuffix || "预览"}`}</span></button>`
        : ((item.is_self || item.id === "prj-ai-hub" || String(item.folder_name || "").toLowerCase() === "ai-hub")
          ? '<span class="stamp semantic-ok project-self-pill"><span class="dot-ok-sq" aria-hidden="true"></span><span>本体运行中</span></span>'
          : "");
      const agIcon = appState.antigravity?.icon_url;
      const agGlyph = agIcon ? `<img class="project-tool-icon" src="${esc(agIcon)}" alt="" />` : '<span class="project-tool-letter" aria-hidden="true">A</span>';

      return `<article class="project-card block" data-project-id="${esc(item.id)}" tabindex="-1">
        <header class="project-card-head block-head" tabindex="0" role="group" aria-label="${esc(displayName)}" aria-description="使用方向键调整顺序" title="拖动卡头排序，也可按方向键调整">
          <div class="project-card-lead">
            <div class="project-emblem">${theme.svg}</div>
            <div class="project-head-meta">
              <div class="project-name-row">
                <span class="project-name" title="${esc(displayName)}">${esc(displayName)}</span>
                ${branch}
              </div>
            </div>
          </div>
          ${statusMeta}
        </header>

        <div class="project-card-body">
          <p class="project-desc" title="${esc(descText)}">${esc(descText)}</p>
          <button type="button" class="path-strip-btn" data-act="copyProjectPath" aria-label="复制项目路径 ${esc(item.path)}" data-path="${esc(item.path)}" data-tip="点击复制完整路径：${esc(item.path)}">
            <span class="project-path mono">${esc(compactPathLabel(item.path))}</span>
            <span class="path-copy-hint" aria-hidden="true">${ICONS.copy}</span>
          </button>
        </div>

        <footer class="project-card-foot">
          <div class="project-foot-left">
            ${previewBtn}
          </div>
          <div class="project-foot-right">
            ${iconButton("openProjectFolder", "打开目录", ICONS.folder, { attrs: `data-path="${esc(item.path)}" data-tip="打开文件夹"` })}
            ${iconButton("openProjectTerminal", "打开终端", ICONS.term, { attrs: `data-path="${esc(item.path)}" data-tip="在此目录打开终端"` })}
            ${iconButton("openProjectInAi", "用 Antigravity 打开", agGlyph, { attrs: `data-path="${esc(item.path)}" data-tool="antigravity" data-tip="用 Antigravity 打开此项目"` })}
            ${iconButton("openProjectMoreMenu", "更多项目操作", ICONS.more, { attrs: `data-id="${esc(item.id)}" data-tip="更多项目操作"` })}
          </div>
        </footer>
      </article>`;
    }).join("")}</div>`;
  }

  function renderProjects() {
    const items = (appState.projects || []).filter((it) => !it.hidden);
    $("#view-title").textContent = "项目";
    $("#view-sub").textContent = items.length ? `${items.length} 个项目` : "暂无项目";

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
          ?.querySelector(".project-card-head")?.focus({ preventScroll: true });
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
    const head = event.target.closest(".project-card-head");
    if (!head || event.button !== 0 || !event.isPrimary || projectDrag || projectDrop || savingProjectOrder) return;
    if (event.target.closest("button, a, input, select")) return;
    const card = head.closest(".project-card"), grid = card?.parentElement;
    if (!card || !grid || grid.childElementCount < 2) return;
    head.focus({ preventScroll: true });
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
    const head = event.target.closest(".project-card-head");
    if (!head || projectDrag || projectDrop || savingProjectOrder || !event.key.startsWith("Arrow")) return;
    const card = head.closest(".project-card"), grid = card.parentElement;
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

  const BACKLOG_PROJECTS = ["AI BASE", "LIFE BASE", "通用"];
  const BACKLOG_STATUSES = [
    { id: "idea", label: "构思中", next: "planned", nextLabel: "→ 排期" },
    { id: "planned", label: "已排期", next: "doing", nextLabel: "→ 进行中" },
    { id: "doing", label: "进行中", next: "done", nextLabel: "✓ 完成" },
    { id: "done", label: "已完成" },
    { id: "paused", label: "已搁置", next: "idea", nextLabel: "→ 重启" },
  ];
  const BACKLOG_PRIORITIES = {
    high: { label: "高", cls: "backlog-priority-high", rank: 0 },
    medium: { label: "中", cls: "backlog-priority-medium", rank: 1 },
    low: { label: "低", cls: "stone", rank: 2 },
  };
  const BACKLOG_STATUS_RANK = { doing: 0, planned: 1, idea: 2, paused: 3, done: 4 };

  function normBacklogProject(proj) {
    const p = String(proj || "").trim();
    if (/^ai\s*(hub|base)$/i.test(p)) return "AI BASE";
    if (/^life\s*(hub|base)$/i.test(p)) return "LIFE BASE";
    return p || "通用";
  }

  function backlogStatusMeta(id) {
    return BACKLOG_STATUSES.find((s) => s.id === id) || BACKLOG_STATUSES[0];
  }

  function backlogStatusLabel(status) {
    return `<span class="kv-pill-lead"><span class="backlog-status-dot" data-status="${status.id}" aria-hidden="true"></span>${status.label}</span>`;
  }

  function backlogProjectTag(project, bare = false) {
    const style = project === "AI BASE" ? "ai" : project === "LIFE BASE" ? "life" : "general";
    if (bare) {
      return `<span class="backlog-project-dot is-${style}" aria-hidden="true"></span><span class="backlog-project-text">${esc(project)}</span>`;
    }
    return `<span class="stamp backlog-project-tag is-${style}">${esc(project)}</span>`;
  }

  function filterBacklogItems(items) {
    const q = backlogQuery.trim().toLowerCase();
    return [...items]
      .filter((it) => {
        const proj = normBacklogProject(it.project);
        if (backlogProject !== "all" && proj !== backlogProject) return false;
        if (backlogStatus !== "all" && it.status !== backlogStatus) return false;
        if (q && !`${normBrandText(it.title)} ${normBrandText(it.notes)} ${proj}`.toLowerCase().includes(q)) return false;
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

  function renderBacklogNotesHtml(item) {
    const raw = normBrandText(item.notes || "");
    if (!raw.trim()) {
      return {
        progressBadge: "",
        bodyHtml: `<p class="backlog-notes">暂无详细说明，点击右上角「编辑」补充讨论要点。</p>`,
      };
    }
    let done = 0;
    let total = 0;
    const blocks = [];
    const lines = raw.split(/\r?\n/);
    for (const [index, line] of lines.entries()) {
      const match = line.match(checklistLine);
      const kind = match ? "checks" : "prose";
      if (blocks.at(-1)?.kind !== kind) blocks.push({ kind, lines: [] });
      const block = blocks.at(-1);
      if (!match) {
        block.lines.push(line);
        continue;
      }

      const text = match[3];
      const pending = pendingBacklogChecks.get(JSON.stringify([item.id, index]));
      const checked = pending?.text === text ? pending.checked : match[2]?.toLowerCase() === "x";
      total++;
      if (checked) done++;
      block.lines.push(`
        <label class="bl-check-item${checked ? " is-checked" : ""}">
          <input type="checkbox" data-act="toggleBacklogCheck" data-id="${esc(item.id)}" data-line="${index}" ${checked ? "checked" : ""} />
          <span>${esc(text)}</span>
        </label>`);
    }
    const progressBadge = total ? `<span class="stamp ${done === total ? "semantic-ok" : "stone"} mono">${done}/${total}</span>` : "";
    const bodyHtml = blocks.map(({ kind, lines }) => {
      if (kind === "checks") return `<div class="bl-checklist">${lines.join("")}</div>`;
      const prose = lines.join("\n").trim();
      return prose ? `<p class="backlog-notes">${esc(prose)}</p>` : "";
    }).join("");
    return { progressBadge, bodyHtml };
  }

  function renderBacklogSplitHtml(filtered) {
    if (filtered.length === 0) {
      return '<div class="block backlog-empty-card"><div class="backlog-empty">当前筛选条件下暂无需求，点击右上角「+ 新建需求」记录想法。</div></div>';
    }
    const reveal = backlogStatus !== "all" || !!backlogQuery.trim();
    const groups = backlogGroupPreferences.order.map((id) => ({
      id,
      title: id === "done" ? "已完成（归档）" : backlogStatusMeta(id).label,
      list: filtered.filter((item) => item.status === id)
    })).filter((group) => group.list.length);
    const isCollapsed = (id) => (reveal ? searchCollapsedGroups : backlogGroupPreferences.collapsed).has(id);
    const visibleItems = groups.filter((group) => !isCollapsed(group.id)).flatMap((group) => group.list);
    const sel = visibleItems.find((it) => it.id === selectedBacklogId) || visibleItems[0];
    selectedBacklogId = sel?.id || "";

    return `<div class="cp-split">
      <section class="block cp-split-list">
        <div class="block-head white">
          <span class="cp-grp-lead"><span class="head-bar" aria-hidden="true"></span><span>需求清单</span></span>
          <span class="stamp mono">${filtered.length} 条</span>
        </div>
        <div class="cp-list-scroll">
          ${groups.map((g) => `
            <section class="cp-group" data-backlog-group="${g.id}">
            <div class="cp-group-header">
              <button type="button" class="cp-grp-hdr" data-act="toggleBacklogGroup" data-id="${g.id}" aria-expanded="${!isCollapsed(g.id)}" aria-controls="backlog-group-${g.id}">
              ${backlogStatusLabel({ id: g.id, label: g.title })}
              <span class="stamp mono">${g.list.length}</span>
              ${ICONS.chevronDown}
              </button>
              <button type="button" class="backlog-grip" data-backlog-grip="group" data-id="${g.id}" aria-label="拖动或用上下方向键调整${g.title}的位置">${ICONS.grip}</button>
            </div>
            <div id="backlog-group-${g.id}" class="disclosure-panel${isCollapsed(g.id) ? "" : " is-expanded"}" ${isCollapsed(g.id) ? "inert" : ""}><div class="disclosure-content">
            ${g.list.map((it) => {
              const pr = BACKLOG_PRIORITIES[it.priority] || BACKLOG_PRIORITIES.medium;
              const st = backlogStatusMeta(it.status);
              const proj = normBacklogProject(it.project);
              const title = normBrandText(it.title);
              const dateShort = it.updated_at ? fmtDate(it.updated_at, true).split(" ")[0] : "";
              const { next, nextLabel } = st;
              const progress = renderBacklogNotesHtml(it).progressBadge;
              return `<div class="cp-row${it.id === sel?.id ? " selected" : ""}${it.status === "done" ? " is-done" : ""}" data-act="selectBacklogItem" data-id="${esc(it.id)}" tabindex="0">
                <div class="cp-row-main">
                  <span class="stamp ${pr.cls}">${pr.label}</span>
                  ${backlogProjectTag(proj)}
                  <span class="cp-row-title" title="${esc(title)}">${esc(title)}</span>
                  ${progress}
                </div>
                <div class="cp-row-right">
                  <span class="cp-row-date mono${next ? " can-hide" : ""}">${esc(dateShort)}</span>
                  ${next ? `<button type="button" class="cp-quick-adv" data-act="quickAdvanceBacklog" data-id="${esc(it.id)}" data-next="${next}" aria-label="${nextLabel.slice(2)}「${esc(title)}」">${nextLabel}</button>` : ""}
                </div>
              </div>`;
            }).join("")}
            </div></div>
            </section>
          `).join("")}
        </div>
      </section>

      <aside class="block cp-split-detail">${sel ? renderBacklogDetail(sel) : '<div class="backlog-empty-card">展开分组后选择一条需求</div>'}</aside>
    </div>`;
  }

  function renderBacklogDetail(sel, drawer = false) {
    const selProj = normBacklogProject(sel.project);
    const selTitle = normBrandText(sel.title);
    const selPrioKey = BACKLOG_PRIORITIES[sel.priority] ? sel.priority : "medium";
    const selPrio = BACKLOG_PRIORITIES[selPrioKey];
    const property = (key, label, value, tone = "") => `<button type="button" class="stamp kv-pill-btn ${tone}" data-act="openBacklogProperty" data-property="${key}" data-id="${esc(sel.id)}" aria-label="${label}" aria-haspopup="menu" aria-controls="popover-menu" aria-expanded="false">${value}</button>`;
    const notesRendered = renderBacklogNotesHtml(sel);
    return `
        <div class="block-head" style="background:var(--surface-paper);border-bottom:1px solid var(--line-subtle);">
          <span class="cp-grp-lead"><span class="head-bar" aria-hidden="true"></span><span>详情 · ${esc(selProj)}</span></span>
          <div class="cp-detail-actions">
            <button type="button" class="btn btn-primary btn-xs" data-act="copyBacklogPrompt" data-id="${esc(sel.id)}" data-tip="打包项目路径与详细说明为 AI Prompt">复制 Prompt</button>
            <button type="button" class="btn btn-flat btn-xs" data-act="editBacklogItem" data-id="${esc(sel.id)}">编辑</button>
            <button type="button" class="btn btn-flat danger btn-xs" data-act="armDeleteBacklog" data-id="${esc(sel.id)}">删除</button>
            ${drawer ? iconButton("", "关闭需求详情", ICONS.close, { attrs: "data-close-modal" }) : ""}
          </div>
        </div>
        <div class="cp-detail-body">
          <h2 class="cp-detail-title">${esc(selTitle)}</h2>
          <div class="cp-detail-kv cp-property-grid">
            <div>
              <div class="cp-kv-label">项目
                <button type="button" class="kv-jump-btn" data-act="jumpToProject" data-project="${esc(selProj)}" aria-label="跳转到项目页" data-tip="跳转到项目页">${ICONS.ext}</button>
              </div>
              ${property("project", "修改归属项目", backlogProjectTag(selProj, true), "backlog-project-pill")}
            </div>
            <div>
              <div class="cp-kv-label">状态</div>
              ${property("status", "修改状态", backlogStatusLabel(backlogStatusMeta(sel.status)))}
            </div>
            <div>
              <div class="cp-kv-label">优先级</div>
              ${property("priority", "修改优先级", `${selPrio.label}优先级`, selPrio.cls)}
            </div>
            <div>
              <div class="cp-kv-label">更新</div>
              <span class="stamp mono cp-kv-time" data-tip="创建 ${esc(fmtDate(sel.created_at))}">${esc(fmtDate(sel.updated_at, true))}</span>
            </div>
          </div>
          <div class="cp-detail-sec" style="display:flex;align-items:center;gap:8px;"><span>详细说明</span>${notesRendered.progressBadge}</div>
          ${notesRendered.bodyHtml}
        </div>`;
  }

  function openBacklogDrawer(id) {
    const item = (appState.backlog_items || []).find((it) => it.id === id);
    if (!item) return;
    openModal(renderBacklogDetail(item, true), "backlog");
    backlogDrawerId = id;
    backlogDrawerKey = "";
  }

  function syncBacklogDrawer() {
    if (!backlogDrawerId) return;
    const item = (appState.backlog_items || []).find((it) => it.id === backlogDrawerId);
    if (!item) { closeModal(); return; }
    const key = JSON.stringify([item, [...pendingBacklogChecks]]);
    if (key === backlogDrawerKey) return;
    backlogDrawerKey = key;
    const dialog = $("#modal-dialog");
    const restoreFocus = rememberFocus(dialog);
    const scroll = dialog.querySelector(".cp-detail-body").scrollTop;
    dialog.innerHTML = renderBacklogDetail(item, true);
    dialog.querySelector(".cp-detail-body").scrollTop = scroll;
    restoreFocus();
  }

  function renderBacklogKanbanHtml(filtered) {
    const lane = (status, archive = false) => {
      const title = backlogStatusMeta(status).label;
      const items = filtered.filter((item) => item.status === status);
      return `<section class="${archive ? "kanban-archive-section" : "block "}kanban-col" data-kanban-status="${status}">
        <header class="block-head" style="background:var(--surface-paper);border-bottom:1px solid var(--line-subtle);">
          ${backlogStatusLabel({ id: status, label: title })}
          <span class="stamp mono">${items.length}</span>
        </header>
        <div class="kanban-cards">
          ${items.length === 0
            ? '<div class="kanban-empty">拖入卡片到此处</div>'
            : items.map((item) => {
                const prio = BACKLOG_PRIORITIES[item.priority] || BACKLOG_PRIORITIES.medium;
                const proj = normBacklogProject(item.project);
                const title = normBrandText(item.title);
                const notes = normBrandText(item.notes || "").split(/\r?\n/).filter((line) => !checklistLine.test(line)).join(" ").trim();
                const progress = renderBacklogNotesHtml(item).progressBadge;
                return `<article class="kcard${item.status === "done" ? " is-done" : ""}" data-backlog-id="${esc(item.id)}" data-priority-rank="${prio.rank}" data-act="openBacklogDrawer" data-id="${esc(item.id)}">
                  <div class="kcard-top">
                    <div class="kcard-tags">
                      <span class="stamp ${prio.cls}">${prio.label}</span>
                      ${backlogProjectTag(proj)}
                    </div>
                    <button type="button" class="backlog-grip" data-backlog-grip="card" data-id="${esc(item.id)}" aria-label="拖动需求">${ICONS.grip}</button>
                  </div>
                  <button type="button" class="kcard-title" data-act="openBacklogDrawer" data-id="${esc(item.id)}">${esc(title)}</button>
                  ${notes ? `<div class="kcard-desc">${esc(notes)}</div>` : ""}
                  <div class="kcard-foot">
                    ${progress}
                    <span class="mono kcard-date">${esc(fmtDate(item.updated_at, true))}</span>
                    <div class="kcard-actions">
                      <button type="button" class="btn btn-flat btn-xs" data-act="copyBacklogPrompt" data-id="${esc(item.id)}">Prompt</button>
                    </div>
                  </div>
                </article>`;
              }).join("")}
        </div>
      </section>`;
    };
    const archived = filtered.filter((item) => item.status === "done" || item.status === "paused");
    const autoArchive = ["done", "paused"].includes(backlogStatus) || (!!backlogQuery.trim() && archived.length > 0);
    const showArchive = archiveRailOverride !== null ? archiveRailOverride : (archiveRailOpen || autoArchive);
    return `<div class="kanban-board">
      ${["doing", "planned", "idea"].map((status) => lane(status)).join("")}
      <div class="block kanban-archive${showArchive ? " is-open" : ""}">
        <button type="button" class="kanban-archive-toggle" data-act="toggleKanbanArchive" aria-controls="kanban-archive-lanes" aria-expanded="${showArchive}">
          <span>归档</span><span class="stamp mono">${archived.length}</span>${ICONS.chevronDown}
        </button>
        <div id="kanban-archive-lanes" class="kanban-archive-lanes disclosure-panel${showArchive ? " is-expanded" : ""}" ${showArchive ? "" : "inert"}><div class="disclosure-content kanban-archive-sections">${["paused", "done"].map((status) => lane(status, true)).join("")}</div></div>
      </div>
    </div>`;
  }

  function renderBacklogListHtml(filtered) {
    return backlogViewMode === "kanban" ? renderBacklogKanbanHtml(filtered) : renderBacklogSplitHtml(filtered);
  }

  function renderBacklog(forceListOnly = false) {
    if (backlogDrag?.clone) return;
    syncBacklogDrawer();
    const items = (appState.backlog_items || []).map((item) => backlogMoves.has(item.id)
      ? { ...item, ...backlogMoves.get(item.id) } : item);
    const openCount = items.filter((it) => it.status !== "done" && it.status !== "paused").length;
    $("#view-title").textContent = "需求池";
    $("#view-sub").textContent = items.length ? `${items.length} 条 · ${openCount} 待推进` : "暂无需求";

    const view = $("#view-content");
    const filtered = filterBacklogItems(items);
    const key = JSON.stringify([items, backlogProject, backlogStatus, backlogQuery, backlogViewMode, selectedBacklogId,
      [...pendingBacklogChecks], backlogGroupPreferences.order, [...backlogGroupPreferences.collapsed], [...searchCollapsedGroups], archiveRailOpen, archiveRailOverride]);
    const restoreFocus = rememberFocus(view);

    if (forceListOnly && view.dataset.page === "backlog") {
      const listCard = $("#backlog-list-card");
      const scroll = listCard?.querySelector(".cp-list-scroll")?.scrollTop || 0;
      if (listCard) listCard.innerHTML = renderBacklogListHtml(filtered);
      const listScroll = listCard?.querySelector(".cp-list-scroll");
      if (listScroll) listScroll.scrollTop = scroll;
      backlogRenderKey = key;
      restoreFocus();
      return;
    }

    if (key === backlogRenderKey && view.dataset.page === "backlog") return;
    backlogRenderKey = key;
    view.dataset.page = "backlog";

    const counts = { all: items.length };
    for (const p of BACKLOG_PROJECTS) counts[p] = items.filter((it) => normBacklogProject(it.project) === p).length;

    const pillTabs = ["all", ...BACKLOG_PROJECTS].map((p) => {
      const label = p === "all" ? "全部" : p;
      const cnt = counts[p] || 0;
      return `<button type="button" class="seg-btn${backlogProject === p ? " active" : ""}${cnt === 0 && p !== "all" ? " is-zero" : ""}" aria-pressed="${backlogProject === p}" data-act="filterBacklogProject" data-project="${esc(p)}">${esc(label)} <span class="mono">${cnt}</span></button>`;
    }).join("");

    const statusFilterOpts = [
      `<option value="all"${backlogStatus === "all" ? " selected" : ""}>全部状态</option>`,
      ...BACKLOG_STATUSES.map((st) => `<option value="${st.id}"${backlogStatus === st.id ? " selected" : ""}>${st.label}</option>`)
    ].join("");

    view.innerHTML = `<div class="backlog-page">
      <section class="bl-toolbar">
        <div class="backlog-filters">
          <div class="seg" role="group" aria-label="视图切换">
            <button type="button" class="seg-btn${backlogViewMode === "split" ? " active" : ""}" data-act="setBacklogView" data-mode="split">${ICONS.changelog}<span>清单详情</span></button>
            <button type="button" class="seg-btn${backlogViewMode === "kanban" ? " active" : ""}" data-act="setBacklogView" data-mode="kanban">${ICONS.workspace}<span>专业看板</span></button>
          </div>
          <div class="seg" role="group" aria-label="项目筛选">${pillTabs}</div>
        </div>
        <div class="backlog-actions">
          <select id="backlog-filter-status" class="sel backlog-status-select" aria-label="按状态筛选">${statusFilterOpts}</select>
          <input id="backlog-search" class="backlog-search" type="text" placeholder="按 / 搜索需求..." value="${esc(backlogQuery)}" />
        </div>
      </section>
      <div id="backlog-list-card">${renderBacklogListHtml(filtered)}</div>
    </div>`;
    restoreFocus();
  }

  function openBacklogModal(editItem) {
    const isEdit = !!editItem;
    const defaultProj = editItem ? normBacklogProject(editItem.project) : (backlogProject !== "all" ? backlogProject : "LIFE BASE");
    const projectOpts = BACKLOG_PROJECTS.map((p) =>
      `<option value="${esc(p)}"${defaultProj === p ? " selected" : ""}>${esc(p)}</option>`
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
          <input id="bl-title" type="text" required placeholder="一句话描述想做的模块或改进..." value="${esc(normBrandText(editItem?.title || ""))}" />
        </div>
        <div class="field-grid-3">
          <div class="field"><label>归属项目</label><select id="bl-project">${projectOpts}</select></div>
          <div class="field"><label>当前状态</label><select id="bl-status">${statusOpts}</select></div>
          <div class="field"><label>优先级</label><select id="bl-priority">${priorityOpts}</select></div>
        </div>
        <div class="field">
          <label>详细说明与讨论要点（可选）</label>
          <textarea id="bl-notes" rows="6" placeholder="记录核心功能点、UI 设想、数据来源或避坑备注...">${esc(normBrandText(editItem?.notes || ""))}</textarea>
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

  function resolveBacklogRepoPath(projectLabel) {
    const p = normBacklogProject(projectLabel).toLowerCase();
    if (!p || p === "通用") return "";
    const projects = appState?.projects || [];
    const hit = projects.find((it) => {
      const n = String(it.name || "").toLowerCase();
      const f = String(it.folder_name || "").toLowerCase();
      if (n === p || f === p) return true;
      if ((p === "ai hub" || p === "ai base") && (f === "ai-hub" || n.includes("ai hub") || n.includes("ai base"))) return true;
      if ((p === "life hub" || p === "life base") && (f === "life-hub" || n.includes("life"))) return true;
      return false;
    });
    return hit?.path || "";
  }

  async function copyBacklogPrompt(item) {
    if (!item) return;
    const statusLabel = BACKLOG_STATUSES.find((s) => s.id === item.status)?.label || item.status;
    const prioLabel = BACKLOG_PRIORITIES[item.priority]?.label || "中";
    const proj = normBacklogProject(item.project);
    const repoPath = resolveBacklogRepoPath(proj);
    const promptText = [
      `请帮我推进以下开发需求：`,
      `- 项目归属：${proj}`,
      repoPath ? `- 本地仓库路径：${repoPath}` : "",
      `- 需求标题：${normBrandText(item.title)}`,
      `- 当前状态：${statusLabel}（优先级：${prioLabel}）`,
      item.notes ? `- 详细说明：\n${normBrandText(item.notes)}` : "",
    ].filter(Boolean).join("\n");
    try {
      await callHost("copyText", { text: promptText });
      showToast("已复制 AI 开发 Prompt，可直接粘贴给 Codex / Antigravity");
    } catch (err) {
      showToast(err.message, true);
    }
  }

  async function exportBacklogMarkdown() {
    const filtered = filterBacklogItems(appState.backlog_items || []);
    if (!filtered.length) return;
    const lines = ["# AI BASE 需求池导出", ""];
    for (const item of filtered) {
      const st = BACKLOG_STATUSES.find((s) => s.id === item.status)?.label || item.status;
      const pr = BACKLOG_PRIORITIES[item.priority]?.label || "中";
      const proj = normBacklogProject(item.project);
      lines.push(`- [${item.status === "done" ? "x" : " "}] **[${proj}] ${normBrandText(item.title)}**（${st} · ${pr}优）`);
      if (item.notes) {
        for (const noteLine of String(normBrandText(item.notes)).split("\n")) lines.push(`  ${noteLine}`);
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
    const view = $("#settings-content");
    const key = JSON.stringify([shortcuts.map(({ id, name, requires_admin }) => [id, name, requires_admin]), apps]);
    if (key === settingsRenderKey) {
      syncSettingsFields(view, settings, shot, shortcuts);
      return;
    }
    settingsRenderKey = key;
    const restoreFocus = rememberFocus(view);
    const scrollTop = view.scrollTop;
    cancelHotkeyRecord();

    const hotkeyPresets = ["Ctrl+Shift+A", "Alt+A", "Ctrl+Alt+A", "F1"];
    const isPresetHk = hotkeyPresets.includes(shot.hotkey);
    const hotkeyPills = hotkeyPresets.map((value) =>
      `<button class="hotkey-pill${shot.hotkey === value ? " active" : ""}" data-act="setScreenshotHotkey"
        data-hk="${value}" aria-pressed="${shot.hotkey === value}">${value}</button>`
    ).join("") + `<button type="button" id="custom-hotkey-btn" class="hotkey-pill${!isPresetHk && shot.hotkey ? " active" : ""}" data-act="startCustomHotkeyRecord">${!isPresetHk && shot.hotkey ? esc(shot.hotkey) : "自定义…"}</button>`;
    const stashRows = shortcuts.map((sc) => `
      <div class="stash-row">
        <div class="stash-meta">
          <span class="stamp stone stash-tag">${sc.requires_admin ? "公共 · 提权" : "个人桌面"}</span>
          <strong>${esc(normBrandText(sc.name))}</strong>
        </div>
        <div class="stash-right">
          <div class="stash-seg" data-stash-seg="${esc(sc.id)}" role="group" aria-label="收纳 ${esc(sc.name)}">
            <button type="button" class="stash-seg-btn${!sc.hidden ? " is-active" : ""}" data-act="setDesktopShortcutHidden" data-id="${esc(sc.id)}" data-hidden="false" aria-pressed="${!sc.hidden}">显示</button>
            <button type="button" class="stash-seg-btn${sc.hidden ? " is-active" : ""}" data-act="setDesktopShortcutHidden" data-id="${esc(sc.id)}" data-hidden="true" aria-pressed="${!!sc.hidden}">收纳</button>
          </div>
        </div>
      </div>`).join("");
    const uninstallRows = apps.map((app) => `
      <div class="app-row">
        <div class="app-left">
          <img class="app-icon" src="${esc(app.icon_url)}" alt="" />
          <div class="app-info"><div class="app-name">${esc(app.name)}</div><div class="app-sub">${esc(app.uninstall_method)}</div></div>
        </div>
        <button type="button" class="btn btn-paper uninstall-trigger btn-sm btn-w-row" data-act="confirmUninstall" data-id="${esc(app.id)}">卸载软件</button>
      </div>`).join("");

    view.innerHTML = `<div class="settings-page">
      <section class="settings-group" data-settings-section="settings-hotkeys" aria-label="热键与提醒">
        <div class="settings-comic-card block">
          <header class="settings-card-banner block-head">
            <div class="settings-banner-lead">
              <div class="settings-emblem">${ICONS.key}</div>
              <h3 class="settings-banner-title">热键与状态提醒</h3>
            </div>
            <span id="settings-hotkey-status" class="stamp"></span>
          </header>
          <div class="setting-row setting-hotkey-row">
            <div class="setting-label">全局截图快捷键</div>
            <div class="setting-actions">
              <div class="hotkey-pills">${hotkeyPills}</div>
              <span class="v-sep" aria-hidden="true"></span>
              ${settingSwitch("screenshot_enabled", "启用全局截图快捷键", shot.enabled)}
            </div>
          </div>
          <div id="settings-hotkey-alert" class="hotkey-alert" hidden>
            <span></span><button class="btn btn-flat btn-sm" data-act="reregisterHotkey">重新注册</button>
          </div>
          <div class="setting-row">
            <div class="setting-label">额度自动刷新与回满提醒</div>
            ${settingSwitch("quota_auto_refresh", "额度自动刷新与回满提醒", settings.quota_auto_refresh !== false)}
          </div>
          <div class="setting-row">
            <div class="setting-label">周期回收号不足 48 小时提醒</div>
            ${settingSwitch("expiry_balloon_alert", "周期回收号不足 48 小时提醒", settings.expiry_balloon_alert)}
          </div>
          <div class="setting-row">
            <div><div class="setting-label">额度提醒免打扰</div><div class="group-sub">按本机时间暂停桌面额度及回收提醒，额度继续刷新。</div></div>
            <div class="setting-actions">
              <input type="time" class="sel" value="22:00" data-quiet-time="quiet_quota_start" aria-label="免打扰开始时间">
              <span>至</span>
              <input type="time" class="sel" value="08:00" data-quiet-time="quiet_quota_end" aria-label="免打扰结束时间">
              ${settingSwitch("quiet_quota_alerts", "启用额度提醒免打扰", settings.quiet_quota_alerts)}
            </div>
          </div>
        </div>
      </section>

      <section class="settings-group" data-settings-section="settings-stash" aria-label="桌面收纳">
        <div class="settings-comic-card block">
          <header class="settings-card-banner block-head">
            <div class="settings-banner-lead">
              <div class="settings-emblem">${ICONS.folder}</div>
              <h3 class="settings-banner-title">桌面图标收纳</h3>
              <span id="stash-count" class="stamp"></span>
            </div>
            <div class="setting-actions">
              <button type="button" class="btn btn-flat btn-sm" data-act="hideAllAiShortcuts">一键收纳 AI 图标</button>
              <button type="button" class="btn btn-flat btn-sm" data-act="restoreAllShortcuts">全部恢复</button>
            </div>
          </header>
          <div>
            ${stashRows || '<div class="setting-row">未检测到可收纳的快捷方式</div>'}
            ${shortcuts.some((sc) => sc.requires_admin) ? '<div class="setting-row group-sub">更改公共桌面快捷方式时，Windows 会要求管理员确认。</div>' : ""}
          </div>
        </div>
      </section>

      <section class="settings-group" data-settings-section="settings-uninstall" aria-label="软件卸载">
        <div class="settings-comic-card block">
          <header class="settings-card-banner block-head">
            <div class="settings-banner-lead">
              <div class="settings-emblem">${ICONS.trash}</div>
              <h3 class="settings-banner-title">软件卸载</h3>
              <span class="stamp">已检测 ${apps.length} 个</span>
            </div>
          </header>
          <div>
            ${uninstallRows || '<div class="setting-row">未检测到支持卸载的桌面应用</div>'}
          </div>
        </div>
      </section>

      <section class="settings-group" data-settings-section="settings-system" aria-label="常驻与备份">
        <div class="settings-comic-card block">
          <header class="settings-card-banner block-head">
            <div class="settings-banner-lead">
              <div class="settings-emblem">${ICONS.monitor}</div>
              <h3 class="settings-banner-title">常驻、自启与备份</h3>
            </div>
          </header>
          <div class="setting-row">
            <div><div class="setting-label">开机静默启动到托盘</div>
              <div class="group-sub">开机后直接后台常驻系统托盘，随时响应全局截图与快速唤起。</div></div>
            ${settingSwitch("auto_start_silent", "开机静默启动到托盘", settings.auto_start_silent)}
          </div>
          <div class="setting-row">
            <div><div class="setting-label">关闭窗口时缩到系统托盘</div>
              <div class="group-sub">隐藏后休眠界面以降低占用，托盘图标始终常驻。</div></div>
            ${settingSwitch("minimize_to_tray", "关闭窗口时缩到系统托盘", settings.minimize_to_tray)}
          </div>
          <div class="setting-row">
            <div><div class="setting-label">配置与需求池本地备份</div>
              <div class="group-sub">导出或恢复设置与需求池记录（不含账号登录态与密钥）。</div></div>
            <div class="setting-actions">
              <button type="button" class="btn btn-paper btn-sm btn-w-pair" data-act="exportBackupJson">导出备份</button>
              <button type="button" class="btn btn-flat btn-sm btn-w-pair" data-act="importBackupJson">导入恢复</button>
            </div>
          </div>
        </div>
      </section>
    </div>`;
    syncSettingsFields(view, settings, shot, shortcuts);
    applySettingsSection();
    view.scrollTop = scrollTop;
    restoreFocus();
  }

  function applySettingsSection() {
    $$("#settings-content .settings-group").forEach((group) => {
      group.hidden = group.dataset.settingsSection !== settingsSection;
    });
    $$("#settings-dialog [data-act='settingsSection']").forEach((button) => {
      const selected = button.dataset.id === settingsSection;
      button.classList.toggle("is-active", selected);
      button.setAttribute("aria-pressed", String(selected));
    });
  }

  function openSettings() {
    if (!appState || layerIsOpen($("#settings-backdrop"))) return;
    closePopover();
    closeInlineNodeCombo();
    settingsTrigger = document.activeElement;
    $("#settings-actions").innerHTML = `
      <button type="button" class="btn btn-paper btn-sm" data-act="openDataDir">${ICONS.folder} <span>打开数据目录</span></button>
      ${appState.version ? `<button type="button" class="btn btn-ghost btn-sm mono" data-act="openChangelog" data-tip="查看更新日志">${esc(appState.version)}</button>` : ""}
      ${iconButton("closeSettings", "关闭设置", ICONS.close)}`;
    showLayer($("#settings-backdrop"));
    renderManage();
    $("#settings-dialog [aria-pressed='true']")?.focus({ preventScroll: true });
    postAction("refreshInstalledApps");
  }

  function closeSettings() {
    cancelHotkeyRecord();
    dismissLayer($("#settings-backdrop"));
    const trigger = settingsTrigger;
    settingsTrigger = null;
    trigger?.focus({ preventScroll: true });
  }

  function cancelHotkeyRecord() {
    if (!recordingHotkeyBtn) return;
    const button = recordingHotkeyBtn;
    const presets = ["Ctrl+Shift+A", "Alt+A", "Ctrl+Alt+A", "F1"];
    const hotkey = appState?.screenshot?.hotkey;
    recordingHotkeyBtn = null;
    button.classList.remove("is-recording");
    button.textContent = hotkey && !presets.includes(hotkey) ? hotkey : "自定义…";
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
    const pop = $("#popover-menu");
    if (!layerIsOpen(pop)) return;
    backlogPropertyAnchor?.setAttribute("aria-expanded", "false");
    backlogPropertyAnchor = null;
    dismissLayer(pop);
    syncWorkspaceAfterInteraction();
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

  let backlogPropertyAnchor = null;

  function positionPopover(pop, anchorBtn, alignStart = false) {
    pop.classList.toggle("backlog-property-menu", alignStart);
    pop.style.minWidth = alignStart ? `${Math.ceil(anchorBtn.getBoundingClientRect().width)}px` : "";
    if (!alignStart) {
      pop.removeAttribute("role");
      pop.removeAttribute("aria-label");
    }
    showLayer(pop);

    const rect = anchorBtn.getBoundingClientRect();
    const popW = pop.offsetWidth;
    const popH = pop.offsetHeight || 160;
    let left = alignStart ? rect.left : rect.right - popW;
    let top = rect.bottom + 6;
    left = Math.max(10, Math.min(left, window.innerWidth - popW - 10));
    const opensUp = top + popH > window.innerHeight - 10;
    if (opensUp) top = Math.max(10, rect.top - popH - 6);
    pop.dataset.direction = opensUp ? "up" : "down";
    pop.style.left = `${left}px`;
    pop.style.top = `${top}px`;
  }

  function openBacklogProperty(anchor) {
    if (backlogPropertyAnchor === anchor && layerIsOpen($("#popover-menu"))) {
      closePopover();
      return;
    }
    closePopover();
    const item = (appState.backlog_items || []).find((entry) => entry.id === anchor.dataset.id);
    if (!item) return;
    clearTimeout(tipTimer);
    tipBubble.classList.remove("is-show");
    const field = anchor.dataset.property;
    let options;
    if (field === "status") options = BACKLOG_STATUSES.map((status) => [status.id, backlogStatusLabel(status)]);
    else if (field === "priority") options = Object.entries(BACKLOG_PRIORITIES).map(([key, value]) =>
      [key, `<span class="stamp ${value.cls}">${value.label}</span><span>${value.label}优先级</span>`]);
    else options = BACKLOG_PROJECTS.map((project) => [project, backlogProjectTag(project)]);

    const selected = field === "project" ? normBacklogProject(item.project) : item[field];
    const pop = $("#popover-menu");
    pop.setAttribute("role", "menu");
    pop.setAttribute("aria-label", anchor.getAttribute("aria-label"));
    pop.innerHTML = options.map(([value, content]) =>
      `<button type="button" class="pop-item" role="menuitemradio" aria-checked="${value === selected}" data-act="setBacklogProperty" data-id="${esc(item.id)}" data-property="${field}" data-value="${esc(value)}">${content}</button>`).join("");
    backlogPropertyAnchor = anchor;
    anchor.setAttribute("aria-expanded", "true");
    positionPopover(pop, anchor, true);
    (pop.querySelector('[aria-checked="true"]') || pop.firstElementChild)?.focus({ preventScroll: true });
  }

  function openWebIdentityMenu(anchor, toolId) {
    const profiles = appState?.chrome_profiles || [];
    const current = webIdentity(toolId, profiles);
    const pop = $("#popover-menu");
    pop.innerHTML = profiles.length
      ? profiles.map((p) => `<button type="button" class="pop-item" data-act="setWebIdentityMenu" data-id="${esc(toolId)}" data-dir="${esc(p.dir)}">
          <span class="dot-ok-sq" style="${p.dir === current?.dir ? "" : "opacity:0.15;"}"></span>
          <span>${esc(p.name)}</span>
        </button>`).join("")
      : `<div class="pop-item" style="color:var(--text-muted);cursor:default;">暂无可用 Chrome 身份</div>`;
    positionPopover(pop, anchor);
  }

  let activeComboEl = null;
  function closeInlineNodeCombo() {
    if (activeComboEl) {
      activeComboEl.remove();
      activeComboEl = null;
      syncWorkspaceAfterInteraction();
    }
    const trigger = document.querySelector('[data-act="toggleInlineNodeCombo"]');
    if (trigger) trigger.setAttribute("aria-expanded", "false");
  }

  async function openInlineNodeCombo(anchorBtn) {
    if (activeComboEl) {
      closeInlineNodeCombo();
      return;
    }
    closePopover();
    anchorBtn.setAttribute("aria-expanded", "true");
    const box = document.createElement("div");
    box.className = "combo-popover";
    box.innerHTML = `
      <input type="text" class="combo-search" placeholder="输入关键字筛选节点..." aria-label="筛选节点" />
      <div class="combo-list"><div style="padding:10px;font-size:12px;color:var(--text-muted);text-align:center;">正在读取节点…</div></div>
      <div class="combo-foot"><span>↑/↓ 选择 · Enter 切换</span><button type="button" class="btn btn-flat btn-xs" data-act="openNetworkPickerFromCombo">完整面板</button></div>`;
    document.body.appendChild(box);
    activeComboEl = box;

    const r = anchorBtn.getBoundingClientRect();
    box.style.left = Math.max(10, Math.min(window.innerWidth - 320, r.left)) + "px";
    box.style.top = (r.bottom + 5) + "px";

    const searchInp = box.querySelector(".combo-search");
    const listEl = box.querySelector(".combo-list");
    let nodes = [];
    let groupName = "";
    let hiIdx = 0;

    function renderList() {
      const kw = searchInp.value.trim().toLowerCase();
      const filtered = nodes.filter((n) => !kw || n.name.toLowerCase().includes(kw) || String(n.delay ?? "").includes(kw));
      if (hiIdx >= filtered.length) hiIdx = Math.max(0, filtered.length - 1);
      listEl.innerHTML = filtered.length ? filtered.map((n, pos) => {
        const dotCls = n.delay == null ? "" : (n.delay < 100 ? "is-ok" : (n.delay <= 180 ? "is-warning" : "is-danger"));
        const dotColor = dotCls === "is-ok" ? "var(--ok-fill)" : (dotCls === "is-warning" ? "var(--warn-fill)" : (dotCls === "is-danger" ? "var(--danger)" : "#9C988F"));
        const delayTxt = n.delay != null ? `${n.delay} ms` : (n.state === "timeout" ? "超时" : "未测");
        return `<button type="button" class="combo-opt${n.active ? " is-selected" : ""}${pos === hiIdx ? " is-hi" : ""}" data-combo-node="${esc(n.name)}">
          <span style="display:flex;align-items:center;gap:7px;min-width:0;">
            <span style="width:6px;height:6px;background:${dotColor};flex-shrink:0;"></span>
            <span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${esc(n.name)}</span>
          </span>
          <span class="mono" style="font-size:11px;color:${dotCls === "is-ok" ? "var(--ok)" : (dotCls === "is-warning" ? "var(--warn)" : "var(--text-muted)")};flex-shrink:0;">${esc(delayTxt)}</span>
        </button>`;
      }).join("") : `<div style="padding:10px;font-size:12px;color:var(--text-muted);text-align:center;">无匹配节点</div>`;
    }

    async function pickNode(nodeName) {
      closeInlineNodeCombo();
      networkBusy = "switch";
      renderAll();
      try {
        await callHost("switchNetworkNode", { group: groupName, node: nodeName });
        showToast("已切换到 " + nodeName);
      } catch (err) {
        showToast(err.message, true);
      } finally {
        networkBusy = "";
        renderAll();
      }
    }

    searchInp.addEventListener("input", () => { hiIdx = 0; renderList(); });
    searchInp.addEventListener("keydown", (ev) => {
      const opts = listEl.querySelectorAll("[data-combo-node]");
      if (ev.key === "ArrowDown") { ev.preventDefault(); hiIdx = Math.min(opts.length - 1, hiIdx + 1); renderList(); }
      else if (ev.key === "ArrowUp") { ev.preventDefault(); hiIdx = Math.max(0, hiIdx - 1); renderList(); }
      else if (ev.key === "Enter" && opts[hiIdx]) { ev.preventDefault(); pickNode(opts[hiIdx].dataset.comboNode); }
      else if (ev.key === "Escape") { closeInlineNodeCombo(); anchorBtn.focus(); }
    });
    listEl.addEventListener("click", (ev) => {
      const btn = ev.target.closest("[data-combo-node]");
      if (btn) pickNode(btn.dataset.comboNode);
    });
    box.querySelector('[data-act="openNetworkPickerFromCombo"]').addEventListener("click", () => {
      closeInlineNodeCombo();
      openNetworkPicker();
    });
    searchInp.focus();

    try {
      const res = await callHost("listNetworkNodes", { group: "" });
      if (!activeComboEl || activeComboEl !== box) return;
      groupName = res.group || "";
      nodes = (res.nodes || []).slice().sort((a, b) => (a.delay ?? Infinity) - (b.delay ?? Infinity) || a.name.localeCompare(b.name, "zh-CN"));
      renderList();
    } catch (err) {
      if (activeComboEl === box) listEl.innerHTML = `<div style="padding:10px;font-size:12px;color:var(--danger-hover);text-align:center;">${esc(err.message)}</div>`;
    }
  }

  async function rescueFastestNode() {
    if (networkBusy) return;
    networkBusy = "switch";
    renderAll();
    try {
      const res = await callHost("listNetworkNodes", { group: "" });
      const group = res.group || "";
      let candidates = (res.nodes || []).filter((n) => n.delay != null && n.state === "reachable");
      if (!candidates.length && (res.nodes || []).length) {
        const measured = await callHost("measureNetworkGroup", { group });
        const list = measured?.nodes || res.nodes || [];
        candidates = list.filter((n) => n.delay != null && n.state === "reachable");
      }
      candidates.sort((a, b) => a.delay - b.delay);
      const best = candidates[0];
      if (!best) {
        networkBusy = "";
        renderAll();
        showToast("未找到可用低延迟节点，已打开节点选择器", true);
        openNetworkPicker();
        return;
      }
      await callHost("switchNetworkNode", { group, node: best.name });
      showToast(`已自动切至 ${best.name} (${best.delay} ms)`);
    } catch (err) {
      showToast(err.message, true);
    } finally {
      networkBusy = "";
      renderAll();
    }
  }

  let lastModalTrigger = null;
  function openModal(html, size = false) {
    closePopover();
    closeInlineNodeCombo();
    closeModal();
    lastModalTrigger = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const dlg = $("#modal-dialog");
    const extra = size === "changelog" ? " modal-changelog" : size === "backlog" ? " modal-backlog" : size ? " modal-wide" : "";
    dlg.className = "modal-dialog" + extra;
    dlg.innerHTML = html;
    showLayer($("#modal-backdrop"));
    const firstFocusable = dlg.querySelector('button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])');
    firstFocusable?.focus();
  }

  function closeModal() {
    backlogDrawerId = "";
    const cancelPicker = onNetworkPickerClose;
    onNetworkPickerClose = null;
    cancelPicker?.();
    dismissLayer($("#modal-backdrop"));
    if (lastModalTrigger && lastModalTrigger.isConnected) {
      const restore = lastModalTrigger;
      lastModalTrigger = null;
      setTimeout(() => {
        restore.focus({ preventScroll: true });
        syncWorkspaceAfterInteraction();
      }, 0);
    }
  }

  function saveBacklogGroupPreferences() {
    const { order, collapsed } = backlogGroupPreferences;
    try { localStorage.setItem("aibase_backlog_groups", JSON.stringify({ order, collapsed: [...collapsed] })); } catch (_) {}
  }

  function moveBacklogGroup(id, target, after) {
    const order = backlogGroupPreferences.order.filter((group) => group !== id);
    order.splice(order.indexOf(target) + (after ? 1 : 0), 0, id);
    backlogGroupPreferences.order = order;
    saveBacklogGroupPreferences();
    renderBacklog(true);
    $(`[data-backlog-grip="group"][data-id="${id}"]`)?.focus({ preventScroll: true });
  }

  async function moveBacklogCard(id, status) {
    const item = (appState.backlog_items || []).find((entry) => entry.id === id);
    if (!item || item.status === status || backlogMoves.has(id)) return;
    backlogMoves.set(id, { status, updated_at: toIsoWithOffset(new Date()) });
    if (activeTab === "backlog") renderBacklog(true);
    try {
      await callHost("setBacklogStatus", { id, status });
      showToast(`已移至「${backlogStatusMeta(status).label}」`);
    } catch (err) {
      showToast(err.message, true);
    } finally {
      backlogMoves.delete(id);
      if (activeTab === "backlog") renderBacklog(true);
    }
  }

  async function runProxyLifecycle(action) {
    if (networkBusy) return;
    networkBusy = action;
    renderAll();
    try {
      await callHost(action);
      showToast(action === "proxyStop" ? "已关闭 Clash Verge" : "已重启 Clash Verge");
    } catch (err) {
      showToast(err.message, true);
    } finally {
      networkBusy = "";
      renderAll();
    }
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
      let loadTimer = null;
      if (!picker.nodes.length) {
        message.textContent = "正在读取节点…";
        renderControls();
      } else {
        policies.querySelectorAll("[data-policy]").forEach((btn) => {
          const active = btn.dataset.policy === group;
          btn.classList.toggle("active", active);
          btn.setAttribute("aria-pressed", String(active));
        });
        loadTimer = setTimeout(() => {
          if (loading && isOpen()) {
            message.textContent = "正在读取节点…";
            renderControls();
          }
        }, 180);
      }
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
        clearTimeout(loadTimer);
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
      ...codexRunning.map((a) => `<label class="exit-check-item"><span class="exit-check-main"><input type="checkbox" class="be-codex" value="${esc(a.id)}" checked /><span>${esc(a.name)}</span></span><span class="exit-check-cat">Codex</span></label>`),
      ...toolRunning.map((t) => `<label class="exit-check-item"><span class="exit-check-main"><input type="checkbox" class="be-tool" value="${esc(t.id)}" checked /><span>${esc(t.name)}</span></span><span class="exit-check-cat">AI 应用</span></label>`),
      ...(proxyRunning ? [`<label class="exit-check-item"><span class="exit-check-main"><input type="checkbox" id="be-proxy" checked /><span>Clash Verge</span></span><span class="exit-check-cat">网络代理</span></label>`] : []),
    ];

    openModal(`
      <div class="modal-header">
        <h3>一键全部退出</h3>
        <button type="button" class="btn btn-ghost btn-icon-only" data-close-modal title="关闭" aria-label="关闭">${ICONS.close}</button>
      </div>
      <div class="modal-body">
        ${rows.length ? `<p class="exit-lead">勾选要关闭的运行中项目，确认后将安全退出：</p>` : ""}
        <div id="batch-exit-options" class="exit-checklist">
          ${rows.length ? rows.join("") : `<div class="exit-empty">当前没有正在运行的受管应用或代理。</div>`}
          <div class="exit-hub-divider">
            <label class="exit-check-item exit-hub-item"><span class="exit-check-main"><input type="checkbox" id="be-hub" /><span>同时退出 AI BASE</span></span><span class="exit-check-cat">主程序</span></label>
          </div>
        </div>
      </div>
      <div class="modal-footer">
        <button type="button" class="btn btn-soft" data-close-modal>取消</button>
        <button type="button" id="be-ok" class="btn btn-danger" ${total === 0 ? "disabled" : ""}>${total > 0 ? `确认退出 (${total})` : "确认退出"}</button>
      </div>
    `);

    const options = $("#batch-exit-options");
    const confirm = $("#be-ok");
    const checks = [...options.querySelectorAll("input[type=checkbox]")];
    const syncConfirm = () => {
      const count = checks.filter((input) => input.checked).length;
      confirm.disabled = count === 0;
      confirm.textContent = count > 0 ? `确认退出 (${count})` : "确认退出";
    };
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
      "2026-11-23": "重构工作台、项目卡、需求池与设置弹窗，内置设计系统预加载，并完善截图标注交互。",
      "2026-11-22": "落地暖墨线稿设计，更新截图工具，收紧工作台和项目卡，并修正需求池保存与撤销。",
      "2026-11-21": "工作台、网络、Codex 和截图完成集中升级，新增 Antigravity 实时额度。",
      "2026-11-20": "AI Hub 初版到工作台合并，加入截图、提字与桌面图标收纳。"
    };
    const currentVer = appState?.version || (dates[0]?.versions?.[0]) || "";
    const formatDay = (iso) => {
      const [y, m, d] = iso.split("-");
      return `${y} 年 ${Number(m)} 月 ${Number(d)} 日`;
    };
    let activeCatFilter = "all";
    const renderDayPane = (day) => {
      if (!day) return quotaMessage("暂时无法读取更新日志", "quota-error");
      const list = activeCatFilter === "all" ? day.updates : day.updates.filter((u) => u.type === activeCatFilter);
      const byVer = [];
      for (const u of list) {
        let g = byVer.find((x) => x.ver === u.version);
        if (!g) { g = { ver: u.version, items: [] }; byVer.push(g); }
        g.items.push(u);
      }
      const cats = ["all", ...Object.keys(categories)];
      const counts = new Map(cats.map((cat) => [cat, cat === "all" ? day.updates.length : day.updates.filter((u) => u.type === cat).length]));
      return `
        <div class="changelog-pane-head">
          <span class="changelog-pane-date">${formatDay(day.date)}</span>
          <span class="changelog-pane-count mono">${list.length} 项记录</span>
        </div>
        ${summaries[day.date] ? `<p class="changelog-summary">${esc(summaries[day.date])}</p>` : ""}
        <div class="changelog-filter-bar">
          <div class="seg" role="group" aria-label="筛选更新类型">
            ${cats.map((c) => `<button type="button" class="seg-btn${activeCatFilter === c ? " active" : ""}" aria-pressed="${activeCatFilter === c}" data-cl-cat="${c}">${c === "all" ? "全部" : c} <span class="mono">${counts.get(c)}</span></button>`).join("")}
          </div>
        </div>
        <div class="changelog-entries">${list.length ? byVer.map((g) => `
          ${g.ver ? `<div class="changelog-ver-hdr mono">${esc(g.ver)}</div>` : ""}
          ${g.items.map((update) => `<div class="changelog-entry">
            <span class="changelog-type ${categories[update.type]}">${update.type}</span>
            <span class="changelog-entry-body">${esc(update.body)}</span>
          </div>`).join("")}
        `).join("") : '<p class="changelog-empty">这一天没有符合该筛选的更新记录。</p>'}</div>`;
    };

    openModal(`
      <div class="modal-header">
        <div class="changelog-head-title">
          <h3>更新日志</h3>
          ${currentVer ? `<span class="changelog-version">${esc(currentVer)}</span>` : ""}
        </div>
        ${iconButton("", "关闭更新日志", ICONS.close, { attrs: "data-close-modal" })}
      </div>
      ${dates.length ? `<div class="modal-split">
        <nav id="changelog-nav" class="modal-section-nav" aria-label="日志日期">
          ${dates.map((day, index) => {
            const newest = day.versions[0], oldest = day.versions.at(-1);
            const range = oldest === newest ? newest : `${oldest} → ${newest}`;
            return `<button type="button" class="modal-section-btn${index === 0 ? " is-active" : ""}" data-changelog-idx="${index}" aria-pressed="${index === 0}">
              <span class="modal-section-meta">
                <span class="modal-section-label">${formatDay(day.date)}</span>
                <span class="changelog-date-range">${esc(range)}</span>
              </span>
              <span class="modal-section-dot" aria-hidden="true"></span>
            </button>`;
          }).join("")}
        </nav>
        <div id="changelog-pane" class="changelog-pane">${renderDayPane(dates[0])}</div>
      </div>` : `<div class="modal-body">${quotaMessage("暂时无法读取更新日志", "quota-error")}</div>`}`, "changelog");

    const nav = $("#changelog-nav");
    const pane = $("#changelog-pane");
    if (nav && pane) {
      let curIdx = 0;
      nav.addEventListener("click", (e) => {
        const btn = e.target.closest("[data-changelog-idx]");
        if (!btn) return;
        curIdx = Number(btn.dataset.changelogIdx);
        const day = dates[curIdx];
        if (!day) return;
        nav.querySelectorAll(".modal-section-btn").forEach((el, i) => {
          el.classList.toggle("is-active", i === curIdx);
          el.setAttribute("aria-pressed", String(i === curIdx));
        });
        pane.innerHTML = renderDayPane(day);
        pane.scrollTop = 0;
      });
      pane.addEventListener("click", (e) => {
        const catBtn = e.target.closest("[data-cl-cat]");
        if (!catBtn) return;
        activeCatFilter = catBtn.dataset.clCat || "all";
        pane.innerHTML = renderDayPane(dates[curIdx]);
        pane.querySelector(`[data-cl-cat="${CSS.escape(activeCatFilter)}"]`)?.focus({ preventScroll: true });
      });
    }
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

  const sidebarNav = $(".nav-list");
  const sidebarNavItems = $$(".nav-item");
  let draggedNavItem = null;
  try {
    const order = JSON.parse(localStorage.getItem("aibase_sidebar_order") || "[]");
    if (Array.isArray(order)) {
      const byId = new Map(sidebarNavItems.map((item) => [item.dataset.tab, item]));
      for (const id of new Set([...order, ...byId.keys()])) {
        if (byId.has(id)) sidebarNav.append(byId.get(id));
      }
    }
  } catch (_) {}
  for (const button of sidebarNavItems) {
    button.draggable = true;
    button.title = "拖动排序 · Alt + ↑/↓";
    button.addEventListener("click", () => switchTab(button.dataset.tab));
    button.addEventListener("keydown", (event) => {
      if (!event.altKey || !["ArrowUp", "ArrowDown"].includes(event.key)) return;
      event.preventDefault();
      const after = event.key === "ArrowDown";
      const target = after ? button.nextElementSibling : button.previousElementSibling;
      if (target) moveSidebarItem(button, target, after);
    });
  }
  function moveSidebarItem(item, target, after) {
    if (item === target) return;
    sidebarNav.insertBefore(item, after ? target.nextElementSibling : target);
    try {
      localStorage.setItem("aibase_sidebar_order", JSON.stringify([...sidebarNav.children].map((button) => button.dataset.tab)));
    } catch (_) {}
    item.focus({ preventScroll: true });
  }
  sidebarNav.addEventListener("dragstart", (event) => {
    draggedNavItem = event.target.closest(".nav-item");
    if (draggedNavItem) event.dataTransfer.setData("text/plain", draggedNavItem.dataset.tab);
  });
  sidebarNav.addEventListener("dragover", (event) => {
    if (!draggedNavItem || !event.target.closest(".nav-item")) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = "move";
  });
  sidebarNav.addEventListener("drop", (event) => {
    const target = event.target.closest(".nav-item");
    if (!draggedNavItem || !target) return;
    event.preventDefault();
    const bounds = target.getBoundingClientRect();
    moveSidebarItem(draggedNavItem, target, event.clientY >= bounds.top + bounds.height / 2);
    draggedNavItem = null;
  });
  sidebarNav.addEventListener("dragend", () => { draggedNavItem = null; });

  $("#view-content").addEventListener("animationend", (event) => {
    if (event.animationName === "viewEnter") event.target.classList.remove("view-entering");
    if (event.animationName === "viewCardEnter") event.target.classList.remove("view-card-entering");
  });

  $("#btn-batch-exit").addEventListener("click", openBatchExitModal);

  // 240ms Debounced Global 0px Tooltip
  const tipBubble = $("#tip-bubble");
  let tipTimer = null;
  document.addEventListener("mouseover", (e) => {
    const target = e.target.closest("[data-tip]");
    if (!target || !target.dataset.tip) return;
    clearTimeout(tipTimer);
    tipTimer = setTimeout(() => {
      if (!target.isConnected) return;
      tipBubble.textContent = target.dataset.tip;
      tipBubble.classList.add("is-show");
      const r = target.getBoundingClientRect();
      const left = Math.max(8, Math.min(window.innerWidth - tipBubble.offsetWidth - 8, r.left + r.width / 2 - tipBubble.offsetWidth / 2));
      const top = r.top - tipBubble.offsetHeight - 7 >= 8 ? r.top - tipBubble.offsetHeight - 7 : r.bottom + 7;
      tipBubble.style.left = `${left}px`;
      tipBubble.style.top = `${top}px`;
    }, 240);
  });
  document.addEventListener("mouseout", (e) => {
    if (e.target.closest("[data-tip]")) {
      clearTimeout(tipTimer);
      tipBubble.classList.remove("is-show");
    }
  });

  let recordingHotkeyBtn = null;
  let armingDeleteTimer = null;
  let armedDeleteButton = null;

  function disarmBacklogDelete() {
    clearInterval(armingDeleteTimer);
    if (!armedDeleteButton) return;
    armedDeleteButton.classList.remove("btn-arming");
    armedDeleteButton.textContent = armedDeleteButton.closest(".kcard-actions") ? "删" : "删除";
    armedDeleteButton = null;
  }

  async function deleteBacklogItem(item) {
    try {
      const receipt = await callHost("deleteBacklogItem", { id: item.id });
      showToast(`已删除「${item.title}」`, {
        ms: 5000,
        undo: () => callHost("restoreBacklogItem", { receipt })
          .then(() => showToast("已撤销删除")).catch((err) => showToast(err.message, true)),
      });
    } catch (err) {
      showToast(err.message, true);
    }
  }

  function formatCapturedHotkey(e) {
    if (["Control", "Shift", "Alt", "Meta"].includes(e.key)) return "";
    const parts = [];
    if (e.ctrlKey) parts.push("Ctrl");
    if (e.altKey) parts.push("Alt");
    if (e.shiftKey) parts.push("Shift");
    let keyName = e.key;
    if (keyName === " ") keyName = "Space";
    else if (keyName.length === 1) keyName = keyName.toUpperCase();
    else if (keyName === "PrintScreen") keyName = "PrintScreen";
    const isFn = /^F([1-9]|1[0-2])$/i.test(keyName) || keyName === "PrintScreen";
    if (!isFn && parts.length === 0) return "";
    parts.push(keyName.length <= 3 && /^f\d+$/i.test(keyName) ? keyName.toUpperCase() : keyName);
    return parts.join("+");
  }

  document.addEventListener("keydown", (e) => {
    if (recordingHotkeyBtn) {
      e.preventDefault();
      e.stopPropagation();
      if (e.key === "Escape") {
        cancelHotkeyRecord();
        return;
      }
      const spec = formatCapturedHotkey(e);
      if (!spec) return;
      cancelHotkeyRecord();
      postAction("updateSettings", { screenshot_enabled: true, screenshot_hotkey: spec });
      return;
    }

    const primaryModal = layerIsOpen($("#modal-backdrop"));
    const settingsOpen = layerIsOpen($("#settings-backdrop"));
    const modalOpen = primaryModal || settingsOpen;
    if (backlogPropertyAnchor && layerIsOpen($("#popover-menu"))) {
      const anchor = backlogPropertyAnchor;
      if (["Escape", "Tab"].includes(e.key)) {
        closePopover();
        anchor.focus({ preventScroll: true });
        if (e.key === "Escape") { e.preventDefault(); return; }
      } else if (["ArrowDown", "ArrowUp", "Home", "End"].includes(e.key)) {
        e.preventDefault();
        const options = $$("#popover-menu [role='menuitemradio']");
        const index = options.indexOf(document.activeElement);
        const next = e.key === "Home" ? 0 : e.key === "End" ? options.length - 1 :
          (index + options.length + (e.key === "ArrowDown" ? 1 : -1)) % options.length;
        options[next]?.focus({ preventScroll: true });
        return;
      }
    }
    if (e.key === "Escape") {
      if (activeComboEl) { closeInlineNodeCombo(); return; }
      if (primaryModal) closeModal();
      else if (settingsOpen) closeSettings();
      closePopover();
      return;
    }
    if (modalOpen && e.key === "Tab") {
      const dlg = $(primaryModal ? "#modal-dialog" : "#settings-dialog");
      const focusables = [...dlg.querySelectorAll('button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])')].filter((el) => el.getClientRects().length);
      if (!focusables.length) return;
      const first = focusables[0], last = focusables[focusables.length - 1];
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
      return;
    }
    if (!modalOpen && e.ctrlKey && !e.shiftKey && !e.altKey) {
      const tabMap = { "1": "workspace", "2": "projects", "3": "backlog", "4": "manage", "5": "design" };
      if (tabMap[e.key]) {
        e.preventDefault();
        switchTab(tabMap[e.key]);
        return;
      }
    }
    const inInput = ["INPUT", "TEXTAREA", "SELECT"].includes(document.activeElement?.tagName);
    if (!modalOpen && !inInput && activeTab === "backlog") {
      const groupGrip = e.target.closest('[data-backlog-grip="group"]');
      if (groupGrip && ["ArrowUp", "ArrowDown"].includes(e.key)) {
        e.preventDefault();
        const groups = $$(".cp-group");
        const group = groupGrip.closest(".cp-group");
        const after = e.key === "ArrowDown";
        const peer = groups[groups.indexOf(group) + (after ? 1 : -1)];
        if (peer) moveBacklogGroup(groupGrip.dataset.id, peer.dataset.backlogGroup, after);
        return;
      }
      if (e.target.matches('.cp-row') && ["Enter", " "].includes(e.key)) {
        e.preventDefault();
        e.target.click();
        return;
      }
      if (e.key === "/") {
        e.preventDefault();
        $("#backlog-search")?.focus();
        return;
      }
      if ((e.key === "ArrowDown" || e.key === "ArrowUp") && backlogViewMode === "split") {
        const rows = $$(".cp-row[data-id]").filter((row) => !row.closest("[inert]"));
        if (!rows.length) return;
        e.preventDefault();
        const curIdx = rows.findIndex((r) => r.dataset.id === selectedBacklogId);
        const nextIdx = e.key === "ArrowDown"
          ? Math.min(rows.length - 1, curIdx + 1)
          : Math.max(0, (curIdx < 0 ? 0 : curIdx - 1));
        selectedBacklogId = rows[nextIdx].dataset.id;
        renderBacklog(true);
        document.querySelector(`.cp-row[data-id="${CSS.escape(selectedBacklogId)}"]`)?.scrollIntoView({ block: "nearest" });
      }
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
    if (!e.target.closest('#popover-menu, [data-act="openMoreMenu"], [data-act="openWebIdentityMenu"], [data-act="openBacklogProperty"], [data-act="openProjectMoreMenu"], [data-act="openDesignCopyMenu"]')) closePopover();
    if (activeComboEl && !e.target.closest('.combo-popover, [data-act="toggleInlineNodeCombo"]')) closeInlineNodeCombo();
    if (e.target === $("#modal-backdrop") || e.target.closest("[data-close-modal]")) return closeModal();
    if (e.target === $("#settings-backdrop")) return closeSettings();
    if (e.target.closest("[data-backlog-grip]")) return;

    const actBtn = e.target.closest("[data-act]");
    if (!actBtn) return;
    const act = actBtn.dataset.act;
    const id = actBtn.dataset.id;
    if (appState && !appState.safe_profile_actions && ["syncConfig", "openWipe", "openAddAccount"].includes(act)) {
      closePopover();
      showToast("请重新启动 AI BASE，再执行此账号操作。", true);
      return;
    }

    if (act === "openSettings") openSettings();
    else if (act === "closeSettings") closeSettings();
    else if (act === "settingsSection") {
      if (settingsSection === id) return;
      cancelHotkeyRecord();
      settingsSection = id;
      applySettingsSection();
      const content = $("#settings-content");
      content.scrollTop = 0;
      content.classList.remove("is-changing");
      void content.offsetWidth;
      content.classList.add("is-changing");
    }
    else if (act === "applyLocalUpdate") postAction("applyLocalUpdate");
    else if (act === "refresh") postAction("refresh", { toast: "1" });
    else if (act === "refreshAllQuotas" && actBtn.getAttribute("aria-disabled") !== "true") { postAction("refreshAllUsage"); postAction("refreshAntigravity"); }
    else if (act === "refreshAllUsage") postAction("refreshAllUsage");
    else if (act === "refreshUsage") postAction("refreshUsage", { id });
    else if (act === "refreshNetwork") refreshNetwork();
    else if (act === "openNetworkPicker") openNetworkPicker();
    else if (act === "toggleInlineNodeCombo") openInlineNodeCombo(actBtn);
    else if (act === "rescueFastestNode") rescueFastestNode();
    else if (act === "openWebIdentityMenu") openWebIdentityMenu(actBtn, id);
    else if (act === "setWebIdentityMenu") {
      const dir = actBtn.dataset.dir || "";
      setWebIdentity(id, dir);
      closePopover();
      renderAll();
    }
    else if (act === "refreshAntigravity") { closePopover(); postAction("refreshAntigravity"); }
    else if (act === "proxyOpen") postAction(act);
    else if (act === "proxyRestart") runProxyLifecycle(act);
    else if (act === "proxyStop" && !networkBusy) {
      openConfirmModal("关闭代理", "关闭 Clash Verge 会中断通过代理连接的应用。", "关闭代理", true,
        () => runProxyLifecycle(act));
    }
    else if (act === "launchAccount") postAction("launchAccount", { id });
    else if (act === "stopAccount") postAction("stopAccount", { id });
    else if (act === "syncConfig") { closePopover(); postAction("syncConfig", { id }); }
    else if (act === "openChangelog") openChangelogModal();
    else if (act === "startScreenshot") postAction("startScreenshot");
    else if (act === "startVoiceRecorder") postAction("startVoiceRecorder");
    else if (act === "openLedgerForm") postAction("openLedgerForm", { id });
    else if (act === "toggleChangelogDay") toggleDisclosure(actBtn);
    else if (act === "openDesignCopyMenu") {
      const pop = $("#popover-menu");
      pop.innerHTML = `
        <button type="button" class="pop-item" data-act="triggerDsCopy" data-mode="current">${ICONS.copy} <span>复制当前分页独立 HTML</span></button>
        <button type="button" class="pop-item" data-act="triggerDsCopy" data-mode="aibase">${ICONS.workspace} <span>复制 AI BASE 专属 HTML</span></button>
        <button type="button" class="pop-item" data-act="triggerDsCopy" data-mode="lifebase">${ICONS.ledger} <span>复制 LIFE BASE 专属 HTML</span></button>
        <div class="pop-sep"></div>
        <button type="button" class="pop-item" data-act="triggerDsCopy" data-mode="all">${ICONS.changelog} <span>复制全量 10 页完整 HTML</span></button>`;
      positionPopover(pop, actBtn);
    }
    else if (act === "triggerDsCopy") {
      closePopover();
      const mode = actBtn.dataset.mode || "current";
      const frame = $("#design-sys-frame");
      frame?.contentWindow?.postMessage({ type: "ds-request-copy", mode }, location.origin);
    }
    else if (act === "openDesignSystemExternal") {
      postAction("openDesignSystemExternal");
    }
    else if (act === "reregisterHotkey") postAction("reregisterHotkey");
    else if (act === "setScreenshotHotkey") postAction("updateSettings", { screenshot_enabled: true, screenshot_hotkey: actBtn.dataset.hk });
    else if (act === "startCustomHotkeyRecord") {
      recordingHotkeyBtn = actBtn;
      actBtn.classList.add("is-recording");
      actBtn.textContent = "请按组合键…";
    }
    else if (act === "setDesktopShortcutHidden") {
      postAction("toggleDesktopShortcut", { id, hide: actBtn.dataset.hidden === "true" });
    }
    else if (act === "openDataDir") postAction("openDataDir");
    else if (act === "exportBackupJson") callHost("exportBackupJson").catch((err) => showToast(err.message, true));
    else if (act === "importBackupJson") callHost("importBackupJson").catch((err) => showToast(err.message, true));
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
    } else if (act === "openAddProject") {
      openProjectModal(null);
    } else if (act === "copyProjectPath") {
      const p = actBtn.dataset.path || "";
      callHost("copyText", { text: p }).then(() => showToast("已复制项目路径")).catch((err) => showToast(err.message, true));
    } else if (act === "openProjectFolder") {
      postAction("openProjectFolder", { path: actBtn.dataset.path || "" });
    } else if (act === "openProjectTerminal") {
      postAction("openProjectTerminal", { path: actBtn.dataset.path || "" });
    } else if (act === "openProjectInAi") {
      closePopover();
      postAction("openProjectInAi", { path: actBtn.dataset.path || "", tool: actBtn.dataset.tool || "antigravity", accountId: actBtn.dataset.accountId || "" });
    } else if (act === "launchProjectPreview" || act === "stopProjectPreview" || act === "reopenProjectPreview") {
      closePopover();
      postAction(act, { id });
    } else if (act === "openProjectMoreMenu") {
      const item = (appState.projects || []).find((it) => it.id === id);
      if (!item) return;
      const pop = $("#popover-menu");
      pop.innerHTML = `${appState.project_previews?.[id] === "running" ? `<button type="button" class="pop-item" data-act="reopenProjectPreview" data-id="${esc(id)}">${ICONS.ext} 重新打开预览网页</button>` : ""}
        ${(appState.accounts || []).filter(() => item.exists).map((acc) => `
          <button type="button" class="pop-item" data-act="openProjectInAi" data-tool="codex" data-path="${esc(item.path)}" data-account-id="${esc(acc.id)}">打开 Codex · ${esc(acc.is_default ? "主账号" : acc.name)}</button>`).join("")}
        ${item.remote_url ? `<button type="button" class="pop-item" data-act="openProjectRemote" data-url="${esc(item.remote_url)}">${ICONS.ext} 打开 GitHub 仓库</button>` : ""}
        <button type="button" class="pop-item" data-act="editProjectItem" data-id="${esc(id)}">${ICONS.edit} 编辑项目</button>`;
      positionPopover(pop, actBtn);
      pop.querySelector("button")?.focus({ preventScroll: true });
    } else if (act === "openProjectRemote") {
      closePopover();
      postAction("openProjectRemote", { url: actBtn.dataset.url || "" });
    } else if (act === "editProjectItem") {
      const path = actBtn.dataset.path || "";
      const item = (appState.projects || []).find((it) => it.id === id || it.path === path);
      if (item) openProjectModal(item);
    } else if (act === "filterBacklogProject") {
      backlogProject = actBtn.dataset.project || "all";
      renderBacklog();
    } else if (act === "setBacklogView") {
      const mode = actBtn.dataset.mode === "kanban" ? "kanban" : "split";
      if (mode !== backlogViewMode) {
        backlogViewMode = mode;
        try { localStorage.setItem("aihub_backlog_view", mode); } catch (_) { }
        renderBacklog();
      }
    } else if (act === "toggleBacklogGroup") {
      const transient = backlogStatus !== "all" || !!backlogQuery.trim();
      const collapsed = transient ? searchCollapsedGroups : backlogGroupPreferences.collapsed;
      if (collapsed.has(id)) collapsed.delete(id);
      else collapsed.add(id);
      if (!transient) saveBacklogGroupPreferences();
      toggleDisclosure(actBtn);
      const rows = $$(".cp-row").filter((row) => !row.closest("[inert]"));
      if (!rows.some((row) => row.dataset.id === selectedBacklogId)) {
        selectedBacklogId = rows[0]?.dataset.id || "";
        $$(".cp-row").forEach((row) => row.classList.toggle("selected", row.dataset.id === selectedBacklogId));
        const selected = (appState.backlog_items || []).find((item) => item.id === selectedBacklogId);
        $(".cp-split-detail").innerHTML = selected ? renderBacklogDetail(selected) : '<div class="backlog-empty-card">展开分组后选择一条需求</div>';
      }
    } else if (act === "toggleKanbanArchive") {
      const open = !!toggleDisclosure(actBtn);
      actBtn.closest(".kanban-archive").classList.toggle("is-open", open);
      const hasAuto = ["done", "paused"].includes(backlogStatus) || !!backlogQuery.trim();
      if (hasAuto) archiveRailOverride = open;
      else {
        archiveRailOpen = open;
        archiveRailOverride = null;
      }
    } else if (act === "openBacklogDrawer") {
      openBacklogDrawer(id);
    } else if (act === "selectBacklogItem") {
      if (id && id !== selectedBacklogId) {
        selectedBacklogId = id;
        renderBacklog(true);
      }
      if (id && window.matchMedia("(max-width: 980px)").matches) {
        $(`.cp-row[data-id="${CSS.escape(id)}"]`)?.focus({ preventScroll: true });
        openBacklogDrawer(id);
      }
    } else if (act === "quickAdvanceBacklog") {
      e.stopPropagation();
      const next = actBtn.dataset.next || "";
      if (id && next) moveBacklogCard(id, next);
    } else if (act === "openAddBacklog") {
      openBacklogModal(null);
    } else if (act === "exportBacklogMd") {
      exportBacklogMarkdown();
    } else if (act === "copyBacklogPrompt") {
      const item = (appState.backlog_items || []).find((it) => it.id === id);
      copyBacklogPrompt(item);
    } else if (act === "openBacklogProperty") {
      openBacklogProperty(actBtn);
    } else if (act === "setBacklogProperty") {
      const anchor = backlogPropertyAnchor;
      closePopover();
      anchor?.focus({ preventScroll: true });
      if (actBtn.getAttribute("aria-checked") === "true") return;
      const { property: field, value } = actBtn.dataset;
      if (field === "status") moveBacklogCard(id, value);
      else callHost("setBacklogMetadata", { id, field, value }).catch((err) => showToast(err.message, true));
    } else if (act === "jumpToProject") {
      const path = resolveBacklogRepoPath(actBtn.dataset.project);
      const item = (appState.projects || []).find((project) => !project.hidden && project.path === path);
      if (!item) { showToast("未找到已显示的对应项目卡", true); return; }
      if (backlogDrawerId) closeModal();
      switchTab("projects");
      requestAnimationFrame(() => {
        const card = $(`.project-card[data-project-id="${CSS.escape(item.id)}"]`);
        card?.scrollIntoView({ block: "nearest" });
        card?.focus({ preventScroll: true });
      });
    } else if (act === "editBacklogItem") {
      const item = (appState.backlog_items || []).find((it) => it.id === id);
      if (item) openBacklogModal(item);
    } else if (act === "armDeleteBacklog") {
      const item = (appState.backlog_items || []).find((it) => it.id === id);
      if (!item) return;
      if (armedDeleteButton === actBtn) {
        disarmBacklogDelete();
        deleteBacklogItem(item);
        return;
      }
      disarmBacklogDelete();
      armedDeleteButton = actBtn;
      actBtn.classList.add("btn-arming");
      let left = 3;
      actBtn.textContent = `再次点击确认 (${left}s)`;
      armingDeleteTimer = setInterval(() => {
        if (!actBtn.isConnected || --left <= 0) disarmBacklogDelete();
        else actBtn.textContent = `再次点击确认 (${left}s)`;
      }, 1000);
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
      searchCollapsedGroups.clear();
      archiveRailOverride = null;
      backlogQuery = e.target.value;
      renderBacklog(true);
    }
  });

  document.addEventListener("change", (e) => {
    if (e.target && e.target.id === "backlog-filter-status") {
      searchCollapsedGroups.clear();
      archiveRailOverride = null;
      backlogStatus = e.target.value || "all";
      renderBacklog(true);
      return;
    }
    const blCheck = e.target.closest('[data-act="toggleBacklogCheck"]');
    if (blCheck) {
      const id = blCheck.dataset.id;
      const line = Number(blCheck.dataset.line);
      const item = (appState.backlog_items || []).find((it) => it.id === id);
      const text = item?.notes?.split(/\r?\n/)[line]?.match(checklistLine)?.[3];
      if (text !== undefined) {
        const key = JSON.stringify([id, line]);
        const update = { text, checked: blCheck.checked };
        pendingBacklogChecks.set(key, update);
        renderBacklog(true);
        callHost("setBacklogCheck", { id, line, text, checked: update.checked ? "1" : "0" })
          .catch((err) => showToast(err.message, true))
          .finally(() => {
            if (pendingBacklogChecks.get(key) !== update) return;
            pendingBacklogChecks.delete(key);
            if (activeTab === "backlog") renderBacklog(true);
          });
      }
      return;
    }
    const stashInp = e.target.closest("[data-stash-id]");
    if (stashInp) {
      postAction("toggleDesktopShortcut", { id: stashInp.dataset.stashId, hide: stashInp.checked });
      return;
    }
    const inp = e.target.closest("[data-setting]");
    const quietTime = e.target.closest("[data-quiet-time]");
    if (quietTime?.value) {
      postAction("updateSettings", { [quietTime.dataset.quietTime]: quietTime.value });
      return;
    }
    if (!inp) return;
    postAction("updateSettings", { [inp.dataset.setting]: inp.checked });
  });

  document.addEventListener("pointerdown", (event) => {
    const grip = event.target.closest("[data-backlog-grip]");
    if (!grip || !event.isPrimary || event.button !== 0 || backlogDrag || backlogMoves.has(grip.dataset.id)) return;
    const group = grip.dataset.backlogGrip === "group";
    const source = grip.closest(group ? ".cp-group" : ".kcard");
    const bounds = source.getBoundingClientRect();
    backlogDrag = { grip, source, bounds, group, pointer: event.pointerId, startX: event.clientX, startY: event.clientY,
      x: event.clientX, y: event.clientY, scroll: group ? source.closest(".cp-list-scroll") : $("#view-content") };
    grip.setPointerCapture(event.pointerId);
  });

  function updateBacklogDrag() {
    const drag = backlogDrag;
    if (!drag) return;
    drag.frame = 0;
    const dx = drag.x - drag.startX, dy = drag.y - drag.startY;
    if (!drag.clone) {
      if (Math.hypot(dx, dy) < 5) return;
      drag.clone = drag.source.cloneNode(true);
      drag.clone.removeAttribute("id");
      drag.clone.querySelectorAll("[id]").forEach((el) => el.removeAttribute("id"));
      drag.clone.inert = true;
      drag.clone.classList.add("backlog-drag-clone");
      Object.assign(drag.clone.style, { width: `${drag.bounds.width}px`, left: `${drag.bounds.left}px`, top: `${drag.bounds.top}px` });
      document.body.append(drag.clone);
      drag.source.classList.add("backlog-drag-source");
    }
    drag.clone.style.transform = `translate3d(${dx}px, ${dy}px, 0)`;
    const hit = document.elementFromPoint(drag.x, drag.y);
    const target = hit?.closest(drag.group ? ".cp-group" : "[data-kanban-status]");
    const previousTarget = drag.target;
    previousTarget?.classList.remove("backlog-drop-before", "backlog-drop-after", "is-drag-over");
    drag.target = target && target !== drag.source ? target : null;
    if (drag.target) {
      const bounds = drag.target.getBoundingClientRect();
      drag.after = drag.y >= bounds.top + bounds.height / 2;
      drag.target.classList.add(drag.group ? (drag.after ? "backlog-drop-after" : "backlog-drop-before") : "is-drag-over");
    }
    if (!drag.group && previousTarget !== drag.target) {
      drag.placeholder?.remove();
      drag.placeholder = null;
      const cards = drag.target?.querySelector(".kanban-cards");
      if (cards && !cards.contains(drag.source)) {
        const item = (appState.backlog_items || []).find((entry) => entry.id === drag.grip.dataset.id);
        const rank = BACKLOG_PRIORITIES[item?.priority]?.rank ?? 9;
        const next = [...cards.querySelectorAll(".kcard")].find((card) => Number(card.dataset.priorityRank) >= rank);
        drag.placeholder = document.createElement("div");
        drag.placeholder.className = "backlog-drop-slot";
        drag.placeholder.style.blockSize = `${Math.min(drag.bounds.height, 112)}px`;
        cards.insertBefore(drag.placeholder, next || cards.querySelector(".kanban-empty"));
      }
    }
    const scrollBounds = drag.scroll.getBoundingClientRect();
    if (drag.y < scrollBounds.top + 40 || drag.y > scrollBounds.bottom - 40) {
      const previous = drag.scroll.scrollTop;
      drag.scroll.scrollTop += drag.y < scrollBounds.top + 40 ? -8 : 8;
      if (drag.scroll.scrollTop !== previous) drag.frame = requestAnimationFrame(updateBacklogDrag);
    }
  }

  function finishBacklogDrag(accept) {
    const drag = backlogDrag;
    if (!drag) return;
    backlogDrag = null;
    cancelAnimationFrame(drag.frame);
    const floatingBounds = drag.clone?.getBoundingClientRect();
    drag.clone?.remove();
    drag.placeholder?.remove();
    drag.source.classList.remove("backlog-drag-source");
    drag.target?.classList.remove("backlog-drop-before", "backlog-drop-after", "is-drag-over");
    if (!drag.clone) return;
    if (accept && drag.target) {
      const id = drag.grip.dataset.id;
      if (drag.group) moveBacklogGroup(id, drag.target.dataset.backlogGroup, drag.after);
      else moveBacklogCard(id, drag.target.dataset.kanbanStatus);
      const landed = $(`[${drag.group ? "data-backlog-group" : "data-backlog-id"}="${CSS.escape(id)}"]`);
      if (landed && floatingBounds && !reducedMotion.matches) {
        const bounds = landed.getBoundingClientRect();
        landed.animate([
          { transform: `translate(${floatingBounds.left - bounds.left}px, ${floatingBounds.top - bounds.top}px)` },
          { transform: "translate(0, 0)" }
        ], { duration: 170, easing: "cubic-bezier(.22, 1, .36, 1)" });
      }
    } else if (activeTab === "backlog") {
      renderBacklog();
    }
  }

  document.addEventListener("pointermove", (event) => {
    if (event.pointerId !== backlogDrag?.pointer) return;
    backlogDrag.x = event.clientX;
    backlogDrag.y = event.clientY;
    if (!backlogDrag.frame) backlogDrag.frame = requestAnimationFrame(updateBacklogDrag);
  });
  document.addEventListener("pointerup", (event) => {
    if (event.pointerId !== backlogDrag?.pointer) return;
    cancelAnimationFrame(backlogDrag.frame);
    backlogDrag.x = event.clientX;
    backlogDrag.y = event.clientY;
    updateBacklogDrag();
    finishBacklogDrag(true);
  });
  document.addEventListener("pointercancel", (event) => {
    if (event.pointerId === backlogDrag?.pointer) finishBacklogDrag(false);
  });
  window.addEventListener("blur", () => finishBacklogDrag(false));
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") finishBacklogDrag(false);
  });

  // 1-second live countdown ticker (in-place update, zero flicker!)
  setInterval(() => {
    if (!appState || activeTab !== "workspace") return;
    for (const acc of appState.accounts || []) {
      const cd = computeCountdown(acc);
      if (!cd) continue;
      const clock = document.querySelector(`[data-cd-clock="${CSS.escape(acc.id)}"]`);
      const status = document.querySelector(`[data-cd-status="${CSS.escape(acc.id)}"]`);
      const label = recycleStatus(cd);
      if (status && status.textContent !== label) status.textContent = label;
      if (clock) {
        clock.textContent = cd.meterValue;
        clock.title = cd.compact;
        clock.className = `mono tone-${cd.level}`;
      }
    }
  }, 1000);

  window.addEventListener("message", (ev) => {
    const frame = $("#design-sys-frame");
    if (ev.origin !== location.origin || ev.source !== frame?.contentWindow) return;
    const msg = ev.data;
    if (msg && typeof msg === "object" && msg.type === "ds-copy-html" && msg.html) {
      callHost("copyText", { text: msg.html })
        .then(() => showToast(`已复制「${msg.label || "设计系统 HTML"}」（${Math.round(msg.html.length / 1024)} KB）`))
        .catch((err) => showToast(err.message, true));
    }
  });

  if (webview) {
    webview.addEventListener("message", (ev) => {
      const msg = typeof ev.data === "string" ? JSON.parse(ev.data) : ev.data;
      if (!msg || !msg.type) return;

      if (msg.type === "state") {
        appState = msg.payload;
        if (!tabRestored) {
          tabRestored = true;
          if (appState?.just_updated) {
            try {
              const saved = localStorage.getItem("aihub_last_tab");
              if (["workspace", "projects", "backlog", "manage", "design"].includes(saved) && saved !== activeTab) {
                if (saved === "manage") requestAnimationFrame(openSettings);
                else activeTab = saved;
                if (saved === "projects") postAction("refreshProjects");
              }
            } catch (_) { }
          }
        }
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
