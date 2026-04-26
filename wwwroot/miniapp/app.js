const tg = window.Telegram?.WebApp;

const store = {
  root: document.getElementById("app"),
  toast: document.getElementById("toast"),
  initData: tg?.initData || "",
  debugUserId: new URLSearchParams(window.location.search).get("devUserId") || "",
  state: null,
  activeView: "dashboard",
  selectedTheme: localStorage.getItem("assistKentTheme") || "cobalt",
  selectedDirectionCode: "",
  groups: [],
  scheduleMode: "today",
  selectedHomeworkGroup: "",
  selectedTimerSound: localStorage.getItem("assistKentTimerSound") || "off",
  lastSyncLabel: "РЎРёРЅС…СЂРѕРЅРёР·Р°С†РёСЏ...",
  timerTick: null,
  refreshTick: null,
  audioContext: null,
  activeSoundCleanup: null,
  activeSoundMode: "off"
};

const THEME_LABELS = {
  cobalt: "Cobalt",
  ember: "Ember",
  matrix: "Matrix"
};

const VIEW_META = {
  dashboard: { label: "РћР±Р·РѕСЂ", shortLabel: "РћР±Р·РѕСЂ", icon: "в—«", eyebrow: "MISSION_BOARD" },
  schedule: { label: "Р Р°СЃРїРёСЃР°РЅРёРµ", shortLabel: "РџР°СЂС‹", icon: "вЊ", eyebrow: "SCHEDULE_MATRIX" },
  homework: { label: "Р”РѕРјР°С€РєР°", shortLabel: "Р”Р—", icon: "вњ¦", eyebrow: "HOMEWORK_STACK" },
  plan: { label: "РџР»Р°РЅ", shortLabel: "РџР»Р°РЅ", icon: "в–Ј", eyebrow: "PERSONAL_QUEUE" },
  focus: { label: "Р¤РѕРєСѓСЃ", shortLabel: "Р¤РѕРєСѓСЃ", icon: "в—Ћ", eyebrow: "FOCUS_ENGINE" }
};

VIEW_META.reminders = { label: "РќР°РїРѕРјРёРЅР°РЅРёСЏ", shortLabel: "РќР°РїРѕРј.", icon: "в—Њ", eyebrow: "ALERT_ROUTER" };

const TIMER_SOUND_META = {
  off: { label: "РўРёС€РёРЅР°", hint: "Р±РµР· С„РѕРЅРѕРІРѕРіРѕ Р·РІСѓРєР°" },
  pulse: { label: "Pulse", hint: "РјСЏРіРєРёР№ СЂРёС‚Рј РґР»СЏ С„РѕРєСѓСЃР°" },
  rain: { label: "Rain", hint: "С€СѓРј РґРѕР¶РґСЏ Рё РІРѕР·РґСѓС…Р°" },
  arcade: { label: "Arcade", hint: "РїРёРєСЃРµР»СЊРЅС‹Р№ СЃРёРЅС‚-Р»СѓРї" }
};

boot().catch(handleFatalError);

async function boot() {
  applyTheme(store.selectedTheme);
  tg?.ready();
  tg?.expand();

  if (tg) {
    tg.setHeaderColor?.("#070816");
    tg.setBackgroundColor?.("#070816");
  }

  await refreshState();
  document.addEventListener("click", handleClick);
  document.addEventListener("submit", handleSubmit);
  document.addEventListener("change", handleChange);
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden) {
      refreshState({ silent: true }).catch(() => {});
    }
  });

  store.refreshTick = window.setInterval(() => {
    refreshState({ silent: true }).catch(() => {});
  }, 15000);
}

async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  headers.set("Accept", "application/json");

  if (store.initData) {
    headers.set("X-Telegram-Init-Data", store.initData);
  }

  if (store.debugUserId) {
    headers.set("X-MiniApp-Debug-UserId", store.debugUserId);
  }

  if (options.body !== undefined) {
    headers.set("Content-Type", "application/json");
  }

  const response = await fetch(path, {
    method: options.method || "GET",
    headers,
    body: options.body !== undefined ? JSON.stringify(options.body) : undefined
  });

  const data = await response.json().catch(() => null);
  if (!response.ok) {
    throw new Error(data?.error || `РћС€РёР±РєР° Р·Р°РїСЂРѕСЃР° (${response.status})`);
  }

  return data;
}

async function refreshState({ silent = false } = {}) {
  const state = await api("/api/miniapp/state");
  store.state = state;
  store.lastSyncLabel = `РЎРёРЅС…СЂРѕРЅРёР·РёСЂРѕРІР°РЅРѕ ${new Date().toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit", second: "2-digit" })}`;

  if (!store.selectedDirectionCode) {
    store.selectedDirectionCode = state.schedule.selectedDirectionCode || state.schedule.directions[0]?.directionCode || "";
  }

  store.groups = state.schedule.availableGroups || [];

  normalizeSelectedHomeworkGroup(state.homeworkSubjects);

  render();
  restartTimerTicker();

  if (!silent) {
    tg?.MainButton?.hide();
  }
}

function getPriorityHomeworkGroups(homeworkSubjects) {
  return homeworkSubjects
    .filter((group) => group.isFavorite)
    .sort((left, right) => (left.favoriteOrder || Number.MAX_SAFE_INTEGER) - (right.favoriteOrder || Number.MAX_SAFE_INTEGER));
}

function getVisibleHomeworkGroups(homeworkSubjects) {
  const priorityGroups = getPriorityHomeworkGroups(homeworkSubjects);
  return priorityGroups.length > 0 ? priorityGroups : homeworkSubjects;
}

function normalizeSelectedHomeworkGroup(homeworkSubjects) {
  const visibleHomeworkGroups = getVisibleHomeworkGroups(homeworkSubjects);

  if (!store.selectedHomeworkGroup && visibleHomeworkGroups.length > 0) {
    store.selectedHomeworkGroup = visibleHomeworkGroups[0].title;
    return;
  }

  if (store.selectedHomeworkGroup &&
      !visibleHomeworkGroups.some((group) => group.title === store.selectedHomeworkGroup)) {
    store.selectedHomeworkGroup = visibleHomeworkGroups[0]?.title || "";
  }
}

function render() {
  if (!store.state) {
    return;
  }

  const { user, stats, schedule, timer, reminder, tasks, homeworkSubjects } = store.state;
  const activeHomework = tasks.homework.filter((task) => !task.isCompleted);
  const activePersonal = tasks.personal.filter((task) => !task.isCompleted);
  const completedTasks = [...tasks.homework, ...tasks.personal].filter((task) => task.isCompleted);
  const activeViewMeta = VIEW_META[store.activeView];
  const isDashboard = store.activeView === "dashboard";

  store.root.innerHTML = `
    <section class="app-frame">
      ${isDashboard ? `
        <section class="topbar panel">
          <div class="topbar-main">
            <div class="identity">
              <div class="avatar">${getInitials(user.displayName)}</div>
              <div class="topbar-copy">
                <p class="eyebrow">ASSISKENT_PANEL</p>
                <h1>${escapeHtml(user.displayName)}</h1>
                <p class="muted">${escapeHtml(user.username || "Р±РµР· username")} // ${escapeHtml(store.lastSyncLabel)}</p>
              </div>
            </div>
            <div class="topbar-actions">
              <button class="pixel-button secondary slim" data-action="refresh">РћР±РЅРѕРІРёС‚СЊ</button>
            </div>
          </div>
          <div class="theme-panel">
            <span class="theme-label">РўРµРјР° РёРЅС‚РµСЂС„РµР№СЃР°</span>
            <div class="theme-switcher compact">
              ${Object.entries(THEME_LABELS).map(([key, label]) => `
                <button class="theme-chip ${store.selectedTheme === key ? "active" : ""}" data-theme="${key}" aria-label="РўРµРјР° ${escapeHtml(label)}">
                  <span class="theme-chip-dot theme-${key}"></span>
                  <span>${escapeHtml(label)}</span>
                </button>
              `).join("")}
            </div>
          </div>
          <div class="status-strip">
            ${statusActionButton("schedule", schedule.selection ? "Р Р°СЃРїРёСЃР°РЅРёРµ РїРѕРґРєР»СЋС‡РµРЅРѕ" : "РќСѓР¶РЅРѕ РІС‹Р±СЂР°С‚СЊ СЂР°СЃРїРёСЃР°РЅРёРµ", "accent")}
            ${statusActionButton("reminders", reminder.isEnabled ? `РќР°РїРѕРјРёРЅР°РЅРёСЏ ${escapeHtml(reminder.timeText)}` : "РќР°РїРѕРјРёРЅР°РЅРёСЏ РІС‹РєР»СЋС‡РµРЅС‹", reminder.isEnabled ? "success" : "warning")}
            ${statusActionButton("focus", timer.isActive ? `РўР°Р№РјРµСЂ ${escapeHtml(timer.type || "")}` : "РўР°Р№РјРµСЂ РЅРµ Р·Р°РїСѓС‰РµРЅ", "default")}
          </div>
          <div class="hero-stats">
            ${heroStat("Р”РµРґР»Р°Р№РЅС‹", stats.homeworkPending, "Р°РєС‚РёРІРЅС‹С…")}
            ${heroStat("РџР»Р°РЅ", stats.personalPending, "Р·Р°РґР°С‡")}
            ${heroStat("РќРµРґРµР»СЏ", schedule.currentWeekType, schedule.currentWeekLabel)}
          </div>
          <div class="shortcut-grid">
            ${shortcutCard("schedule", "РћС‚РєСЂС‹С‚СЊ РїР°СЂС‹ Рё СЃРјРµРЅРёС‚СЊ РіСЂСѓРїРїСѓ")}
            ${shortcutCard("homework", "РџРѕСЃРјРѕС‚СЂРµС‚СЊ Рё РґРѕР±Р°РІРёС‚СЊ Р”Р—")}
            ${shortcutCard("plan", "Р‘С‹СЃС‚СЂС‹Р№ РґРѕСЃС‚СѓРї Рє Р»РёС‡РЅС‹Рј РґРµР»Р°Рј")}
            ${shortcutCard("focus", "Р—Р°РїСѓСЃС‚РёС‚СЊ С‚Р°Р№РјРµСЂ СѓС‡РµР±С‹")}
          </div>
        </section>
      ` : ""}
      <section class="screen-shell">
        <div class="screen-meta">
          <div>
            <p class="eyebrow">${escapeHtml(activeViewMeta.eyebrow)}</p>
            <h2 class="screen-title">${escapeHtml(activeViewMeta.label)}</h2>
          </div>
          <div class="screen-badge">${activeViewMeta.icon}</div>
        </div>
        <section class="view-body">
          ${renderView({
            schedule,
            timer,
            reminder,
            tasks,
            activeHomework,
            activePersonal,
            completedTasks,
            homeworkSubjects
          })}
        </section>
      </section>
      <nav class="tabbar panel">
        ${Object.entries(VIEW_META).map(([view, meta]) => tabButton(view, meta)).join("")}
      </nav>
    </section>
  `;
}

function renderView(context) {
  switch (store.activeView) {
    case "schedule":
      return renderScheduleView(context.schedule);
    case "homework":
      return renderHomeworkViewV2(context.homeworkSubjects, context.tasks.homework);
    case "plan":
      return renderPlanView(context.tasks.personal);
    case "focus":
      return renderFocusView(context.timer, context.reminder);
    case "reminders":
      return renderRemindersView(context.reminder);
    default:
      return renderDashboardView(context);
  }
}

function renderDashboardView({ schedule, timer, reminder, activeHomework, activePersonal, completedTasks }) {
  const entries = store.scheduleMode === "today" ? schedule.todayEntries : schedule.weekEntries;
  const grouped = groupScheduleEntries(entries);

  return `
    <div class="content-grid">
      <section class="module panel">
        <div class="module-head">
          <div>
            <p class="eyebrow">MISSION_BOARD</p>
            <h2 class="module-title">РўРµРєСѓС‰Р°СЏ РѕР±СЃС‚Р°РЅРѕРІРєР°</h2>
          </div>
          <button class="pixel-button secondary" data-action="refresh">РћР±РЅРѕРІРёС‚СЊ</button>
        </div>
        <div class="overview-grid">
          <article class="info-card panel">
            <div class="module-head">
              <h3 class="module-title">РЎРµРіРѕРґРЅСЏ</h3>
              <span class="tag accent">${schedule.todayEntries.length} РїР°СЂ</span>
            </div>
            ${schedule.todayEntries.length > 0 ? schedule.todayEntries.slice(0, 4).map(entry => `
              <div class="schedule-entry">
                <div class="lesson-pill">${entry.lessonNumber}</div>
                <div>
                  <div><strong>${escapeHtml(entry.subject)}</strong></div>
                  <div class="muted">${escapeHtml(entry.time || "РІСЂРµРјСЏ РЅРµ СѓРєР°Р·Р°РЅРѕ")}</div>
                </div>
              </div>
            `).join("") : emptyState("РќР° СЃРµРіРѕРґРЅСЏ РїР°СЂ РЅРµС‚ РёР»Рё СЂР°СЃРїРёСЃР°РЅРёРµ РµС‰С‘ РЅРµ РІС‹Р±СЂР°РЅРѕ.")}
          </article>
          <article class="info-card panel">
            <div class="module-head">
              <h3 class="module-title">РђРєС‚РёРІРЅС‹Р№ С„РѕРєСѓСЃ</h3>
              <span class="tag ${timer.isActive ? "success" : "warning"}">${timer.isActive ? "РІ СЂР°Р±РѕС‚Рµ" : "РЅРµР°РєС‚РёРІРµРЅ"}</span>
            </div>
            <div class="focus-display">
              <p class="eyebrow">FOCUS_ENGINE</p>
              <p class="focus-clock">${escapeHtml(timerText(timer))}</p>
              <p class="muted">${timer.isActive ? `СЂРµР¶РёРј ${escapeHtml(timer.type || "")}` : "Р—Р°РїСѓСЃС‚Рё СЂР°Р±РѕС‡РёР№ РёР»Рё РѕС‚РґС‹С…-С‚Р°Р№РјРµСЂ РІ СЂР°Р·РґРµР»Рµ Р¤РѕРєСѓСЃ."}</p>
            </div>
          </article>
        </div>
      </section>
      <section class="stack">
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">Р”РµРґР»Р°Р№РЅС‹</h2>
            <span class="tag accent">${activeHomework.length} Р°РєС‚РёРІРЅС‹С…</span>
          </div>
          ${activeHomework.length > 0
            ? activeHomework.slice(0, 4).map(task => taskCard(task, "homework")).join("")
            : emptyState("Р”РѕРјР°С€РЅРёРµ Р·Р°РґР°РЅРёСЏ РїРѕСЏРІСЏС‚СЃСЏ Р·РґРµСЃСЊ РїРѕСЃР»Рµ РґРѕР±Р°РІР»РµРЅРёСЏ.")}
        </section>
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">Р›РёС‡РЅС‹Р№ РїР»Р°РЅ</h2>
            <span class="tag accent">${activePersonal.length} Р°РєС‚РёРІРЅС‹С…</span>
          </div>
          ${activePersonal.length > 0
            ? activePersonal.slice(0, 4).map(task => taskCard(task, "personal")).join("")
            : emptyState("Р”РѕР±Р°РІСЊ СЃРІРѕРё РґРµР»Р°, С‡С‚РѕР±С‹ РЅРµ РґРµСЂР¶Р°С‚СЊ РІСЃС‘ РІ РіРѕР»РѕРІРµ.")}
        </section>
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">РќР°РїРѕРјРёРЅР°РЅРёСЏ</h2>
            <span class="tag ${reminder.isEnabled ? "success" : "warning"}">${reminder.isEnabled ? reminder.timeText : "off"}</span>
          </div>
          <p class="muted">
            ${reminder.isEnabled
              ? `Р§Р°С‚ РїРѕР»СѓС‡РёС‚ РЅР°РїРѕРјРёРЅР°РЅРёРµ Рѕ РґРµРґР»Р°Р№РЅР°С… РЅР° Р·Р°РІС‚СЂР° РєР°Р¶РґС‹Р№ РґРµРЅСЊ РІ ${escapeHtml(reminder.timeText)} РїРѕ РњРЎРљ.`
              : "РќР°РїРѕРјРёРЅР°РЅРёСЏ РІС‹РєР»СЋС‡РµРЅС‹. Р’РєР»СЋС‡Рё РёС… РІРѕ РІРєР»Р°РґРєРµ Р¤РѕРєСѓСЃ."}
          </p>
          <div class="divider"></div>
          <p class="muted">Р’С‹РїРѕР»РЅРµРЅРѕ РІСЃРµРіРѕ: <strong>${completedTasks.length}</strong></p>
        </section>
      </section>
      <section class="module panel">
        <div class="module-head">
          <div>
            <p class="eyebrow">LESSON_FEED</p>
            <h2 class="module-title">РџСЂРѕСЃРјРѕС‚СЂ РїР°СЂ</h2>
          </div>
          <div class="actions-row">
            <button class="nav-chip ${store.scheduleMode === "today" ? "active" : ""}" data-action="schedule-mode" data-mode="today">РЎРµРіРѕРґРЅСЏ</button>
            <button class="nav-chip ${store.scheduleMode === "week" ? "active" : ""}" data-action="schedule-mode" data-mode="week">РќРµРґРµР»СЏ</button>
          </div>
        </div>
        ${entries.length > 0 ? Object.entries(grouped).map(([day, dayEntries]) => `
          <div class="schedule-day">
            <div class="module-head">
              <h3 class="schedule-day-title">${escapeHtml(day)}</h3>
              <span class="tag">${dayEntries.length} РїР°СЂ</span>
            </div>
            ${dayEntries.map(entry => `
              <div class="schedule-entry">
                <div class="lesson-pill">${entry.lessonNumber}</div>
                <div>
                  <div><strong>${escapeHtml(entry.subject)}</strong></div>
                  <div class="muted">${escapeHtml(entry.time || "РІСЂРµРјСЏ РЅРµ СѓРєР°Р·Р°РЅРѕ")}</div>
                </div>
              </div>
            `).join("")}
          </div>
        `).join("") : emptyState("РќРµС‚ РґР°РЅРЅС‹С… РґР»СЏ РїРѕРєР°Р·Р°. РћР±С‹С‡РЅРѕ СЌС‚Рѕ Р·РЅР°С‡РёС‚, С‡С‚Рѕ СЂР°СЃРїРёСЃР°РЅРёРµ РµС‰Рµ РЅРµ РІС‹Р±СЂР°РЅРѕ РёР»Рё РЅР° СЃРµРіРѕРґРЅСЏ РїР°СЂ РЅРµС‚.")}
      </section>
    </div>
  `;
}

function renderScheduleView(schedule) {
  const selectedGroup = store.groups.find((group) => group.scheduleId === (schedule.selection?.scheduleId || "")) || store.groups[0];
  const selectedSubgroup = schedule.selection?.subGroup ?? selectedGroup?.subGroups?.[0] ?? "";
  const entries = store.scheduleMode === "today" ? schedule.todayEntries : schedule.weekEntries;
  const grouped = groupScheduleEntries(entries);

  return `
    <div class="content-grid">
      <section class="module panel">
        <div class="module-head">
          <div>
            <p class="eyebrow">SCHEDULE_MATRIX</p>
            <h2 class="module-title">Р’С‹Р±РѕСЂ СЂР°СЃРїРёСЃР°РЅРёСЏ</h2>
          </div>
          ${schedule.selection ? '<button class="pixel-button danger slim" data-action="clear-schedule">РЈРґР°Р»РёС‚СЊ</button>' : ""}
        </div>
        <form id="schedule-form" class="stack">
          <div class="field">
            <label for="direction-select">РќР°РїСЂР°РІР»РµРЅРёРµ</label>
            <select id="direction-select" name="directionCode">
              ${schedule.directions.map(direction => `
                <option value="${escapeHtml(direction.directionCode)}" ${store.selectedDirectionCode === direction.directionCode ? "selected" : ""}>
                  ${escapeHtml(direction.shortTitle)} - ${escapeHtml(direction.directionName)}
                </option>
              `).join("")}
            </select>
          </div>
          <div class="field">
            <label for="group-select">РљСѓСЂСЃ / РіСЂСѓРїРїР°</label>
            <select id="group-select" name="scheduleId">
              ${store.groups.map(group => `
                <option value="${escapeHtml(group.scheduleId)}" ${(schedule.selection?.scheduleId || selectedGroup?.scheduleId) === group.scheduleId ? "selected" : ""}>
                  ${escapeHtml(group.title)}
                </option>
              `).join("")}
            </select>
          </div>
          ${selectedGroup && selectedGroup.subGroups.length > 0 ? `
            <div class="field">
              <label for="subgroup-select">РџРѕРґРіСЂСѓРїРїР°</label>
              <select id="subgroup-select" name="subGroup">
                ${selectedGroup.subGroups.map(subGroup => `
                  <option value="${subGroup}" ${String(selectedSubgroup) === String(subGroup) ? "selected" : ""}>РџРѕРґРіСЂСѓРїРїР° ${subGroup}</option>
                `).join("")}
              </select>
            </div>
          ` : ""}
          <button class="pixel-button" type="submit">РЎРѕС…СЂР°РЅРёС‚СЊ СЂР°СЃРїРёСЃР°РЅРёРµ</button>
        </form>        <div class="divider"></div>
        <div class="card-stack">
          <article class="schedule-card">
            <p class="eyebrow">CURRENT_BINDING</p>
            ${schedule.selection ? `
              <h3 class="schedule-day-title">${escapeHtml(schedule.selection.title)}</h3>
              <div class="schedule-meta">
                <span class="tag accent">${escapeHtml(schedule.currentWeekLabel)}</span>
                <span class="tag">${schedule.selection.subGroup ? `РїРѕРґРіСЂСѓРїРїР° ${schedule.selection.subGroup}` : "Р±РµР· РїРѕРґРіСЂСѓРїРїС‹"}</span>
                <span class="tag">${escapeHtml(schedule.semester)}</span>
              </div>
            ` : emptyState("РџРѕРєР° РЅРёС‡РµРіРѕ РЅРµ РІС‹Р±СЂР°РЅРѕ. РџРѕРґРєР»СЋС‡Рё РіСЂСѓРїРїСѓ, Рё mini app РїРѕРґС‚СЏРЅРµС‚ РїСЂРµРґРјРµС‚С‹ Рё РґРµРґР»Р°Р№РЅС‹.")}
          </article>
        </div>
      </section>
      <section class="module panel">
        <div class="module-head">
          <div>
            <p class="eyebrow">LESSON_FEED</p>
            <h2 class="module-title">РџСЂРѕСЃРјРѕС‚СЂ РїР°СЂ</h2>
          </div>
          <div class="actions-row">
            <button class="nav-chip ${store.scheduleMode === "today" ? "active" : ""}" data-action="schedule-mode" data-mode="today">РЎРµРіРѕРґРЅСЏ</button>
            <button class="nav-chip ${store.scheduleMode === "week" ? "active" : ""}" data-action="schedule-mode" data-mode="week">РќРµРґРµР»СЏ</button>
          </div>
        </div>
        ${entries.length > 0 ? Object.entries(grouped).map(([day, dayEntries]) => `
          <div class="schedule-day">
            <div class="module-head">
              <h3 class="schedule-day-title">${escapeHtml(day)}</h3>
              <span class="tag">${dayEntries.length} РїР°СЂ</span>
            </div>
            ${dayEntries.map(entry => `
              <div class="schedule-entry">
                <div class="lesson-pill">${entry.lessonNumber}</div>
                <div>
                  <div><strong>${escapeHtml(entry.subject)}</strong></div>
                  <div class="muted">${escapeHtml(entry.time || "РІСЂРµРјСЏ РЅРµ СѓРєР°Р·Р°РЅРѕ")}</div>
                </div>
              </div>
            `).join("")}
          </div>
        `).join("") : emptyState("РќРµС‚ РґР°РЅРЅС‹С… РґР»СЏ РїРѕРєР°Р·Р°. РћР±С‹С‡РЅРѕ СЌС‚Рѕ Р·РЅР°С‡РёС‚, С‡С‚Рѕ СЂР°СЃРїРёСЃР°РЅРёРµ РµС‰Рµ РЅРµ РІС‹Р±СЂР°РЅРѕ РёР»Рё РЅР° СЃРµРіРѕРґРЅСЏ РїР°СЂ РЅРµС‚.")}
      </section>
    </div>
  `;
}

function renderRemindersViewDuplicate(reminder) {
  return `
    <div class="single-column">
      <section class="module panel">
        <div class="module-head">
          <div>
            <p class="eyebrow">LESSON_FEED</p>
            <h2 class="module-title">РџСЂРѕСЃРјРѕС‚СЂ РїР°СЂ</h2>
          </div>
          <div class="actions-row">
            <button class="nav-chip ${store.scheduleMode === "today" ? "active" : ""}" data-action="schedule-mode" data-mode="today">РЎРµРіРѕРґРЅСЏ</button>
            <button class="nav-chip ${store.scheduleMode === "week" ? "active" : ""}" data-action="schedule-mode" data-mode="week">РќРµРґРµР»СЏ</button>
          </div>
        </div>
        ${entries.length > 0 ? Object.entries(grouped).map(([day, dayEntries]) => `
          <div class="schedule-day">
            <div class="module-head">
              <h3 class="schedule-day-title">${escapeHtml(day)}</h3>
              <span class="tag">${dayEntries.length} РїР°СЂ</span>
            </div>
            ${dayEntries.map(entry => `
              <div class="schedule-entry">
                <div class="lesson-pill">${entry.lessonNumber}</div>
                <div>
                  <div><strong>${escapeHtml(entry.subject)}</strong></div>
                  <div class="muted">${escapeHtml(entry.time || "РІСЂРµРјСЏ РЅРµ СѓРєР°Р·Р°РЅРѕ")}</div>
                </div>
              </div>
            `).join("")}
          </div>
        `).join("") : emptyState("РќРµС‚ РґР°РЅРЅС‹С… РґР»СЏ РїРѕРєР°Р·Р°. РћР±С‹С‡РЅРѕ СЌС‚Рѕ Р·РЅР°С‡РёС‚, С‡С‚Рѕ СЂР°СЃРїРёСЃР°РЅРёРµ РµС‰С‘ РЅРµ РІС‹Р±СЂР°РЅРѕ РёР»Рё РЅР° СЃРµРіРѕРґРЅСЏ РїР°СЂ РЅРµС‚.")}
      </section>
    </div>
  `;
}

function renderHomeworkView(homeworkSubjects, homeworkTasks) {
  const priorityGroups = getPriorityHomeworkGroups(homeworkSubjects);
  const visibleHomeworkGroups = getVisibleHomeworkGroups(homeworkSubjects);
  const hasPriorityGroups = priorityGroups.length > 0;
  const subjectGroup = visibleHomeworkGroups.find((group) => group.title === store.selectedHomeworkGroup) || visibleHomeworkGroups[0];
  const activeTasks = homeworkTasks.filter((task) => !task.isCompleted);
  const completedTasks = homeworkTasks.filter((task) => task.isCompleted);

  return `
    <div class="content-grid">
      <section class="stack">
        <section class="module panel">
          <div class="module-head">
            <div>
              <p class="eyebrow">HOMEWORK_COMPOSER</p>
              <h2 class="module-title">Р”РѕР±Р°РІРёС‚СЊ РґРѕРјР°С€РєСѓ</h2>
            </div>
            <span class="tag accent">${homeworkSubjects.length} РїСЂРµРґРјРµС‚РѕРІ</span>
          </div>
          ${homeworkSubjects.length === 0
            ? emptyState("РЎРЅР°С‡Р°Р»Р° РІС‹Р±РµСЂРё СЂР°СЃРїРёСЃР°РЅРёРµ РІРѕ РІРєР»Р°РґРєРµ Р Р°СЃРїРёСЃР°РЅРёРµ. РўРѕРіРґР° mini app РїРѕРґС‚СЏРЅРµС‚ РїСЂРµРґРјРµС‚С‹ Рё Р±Р»РёР¶Р°Р№С€РёРµ РґРµРґР»Р°Р№РЅС‹.")
            : `
              <form id="homework-form" class="stack">
                <div class="field">
                  <label for="homework-group">Р‘Р°Р·РѕРІС‹Р№ РїСЂРµРґРјРµС‚</label>
                  <select id="homework-group" name="subjectTitle">
                    ${homeworkSubjects.map(group => `
                      <option value="${escapeHtml(group.title)}" ${subjectGroup?.title === group.title ? "selected" : ""}>
                        ${escapeHtml(group.title)}${group.favoriteOrder ? ` // ${group.favoriteOrder}` : ""}
                      </option>
                    `).join("")}
                  </select>
                </div>
                <div class="field">
                  <label for="homework-subject">РўРёРї Р·Р°РЅСЏС‚РёСЏ</label>
                  <select id="homework-subject" name="subject">
                    ${(subjectGroup?.options || []).map(option => `
                      <option value="${escapeHtml(option.subject)}">
                        ${escapeHtml(option.lessonType)}${option.nextDeadlineText ? ` // РґРµРґР»Р°Р№РЅ ${escapeHtml(option.nextDeadlineText)}` : ""}
                      </option>
                    `).join("")}
                  </select>
                </div>
                <div class="field">
                  <label for="homework-title">Р§С‚Рѕ Р·Р°РґР°Р»Рё</label>
                  <textarea id="homework-title" name="title" placeholder="РќР°РїСЂРёРјРµСЂ: СЂРµС€РёС‚СЊ РІР°СЂРёР°РЅС‚С‹ 3-6 Рё РїРѕРґРіРѕС‚РѕРІРёС‚СЊ РєРѕРЅСЃРїРµРєС‚"></textarea>
                </div>
                <button class="pixel-button" type="submit">Р”РѕР±Р°РІРёС‚СЊ Р”Р—</button>
              </form>
            `}
        </section>
        <section class="module panel">
          <div class="module-head">
            <div>
              <p class="eyebrow">PRIORITY_FILTER</p>
              <h2 class="module-title">РР·Р±СЂР°РЅРЅС‹Рµ РїСЂРµРґРјРµС‚С‹</h2>
            </div>
          </div>
          ${homeworkSubjects.length > 0 ? homeworkSubjects.map(group => `
            <article class="subject-card">
              <div class="subject-top">
                <div>
                  <h3 class="subject-title">${escapeHtml(group.title)}</h3>
                  <div class="subject-meta">
                    <span class="tag">${group.options.length} С‚РёРїРѕРІ Р·Р°РЅСЏС‚РёР№</span>
                    ${group.favoriteOrder ? `<span class="tag success">РїРѕР·РёС†РёСЏ ${group.favoriteOrder}</span>` : `<span class="tag warning">РЅРµ РІ РёР·Р±СЂР°РЅРЅРѕРј</span>`}
                  </div>
                </div>
                <button class="subject-toggle ${group.isFavorite ? "active" : ""}" data-action="toggle-favorite" data-subject-title="${escapeHtml(group.title)}">
                  ${group.isFavorite ? "в…" : "в†"}
                </button>
              </div>
            </article>
          `).join("") : emptyState("РР·Р±СЂР°РЅРЅС‹Рµ РїРѕСЏРІСЏС‚СЃСЏ РїРѕСЃР»Рµ РІС‹Р±РѕСЂР° СЂР°СЃРїРёСЃР°РЅРёСЏ.")}
        </section>
      </section>
      <section class="stack">
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">РђРєС‚РёРІРЅС‹Рµ Р”Р—</h2>
            <span class="tag accent">${activeTasks.length}</span>
          </div>
          ${activeTasks.length > 0 ? activeTasks.map(task => taskCard(task, "homework")).join("") : emptyState("Р—РґРµСЃСЊ Р±СѓРґРµС‚ СЃРїРёСЃРѕРє Р°РєС‚СѓР°Р»СЊРЅС‹С… РґРѕРјР°С€РЅРёС… Р·Р°РґР°РЅРёР№.")}
        </section>
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">Р’С‹РїРѕР»РЅРµРЅРЅС‹Рµ</h2>
            <span class="tag">${completedTasks.length}</span>
          </div>
          ${completedTasks.length > 0 ? completedTasks.map(task => taskCard(task, "homework")).join("") : emptyState("РџРѕРєР° Р±РµР· РІС‹РїРѕР»РЅРµРЅРЅС‹С… Р·Р°РґР°С‡.")}
        </section>
      </section>
    </div>
  `;
}

function renderHomeworkViewV2(homeworkSubjects, homeworkTasks) {
  const priorityGroups = getPriorityHomeworkGroups(homeworkSubjects);
  const visibleHomeworkGroups = getVisibleHomeworkGroups(homeworkSubjects);
  const hasPriorityGroups = priorityGroups.length > 0;
  const subjectGroup = visibleHomeworkGroups.find((group) => group.title === store.selectedHomeworkGroup) || visibleHomeworkGroups[0];
  const activeTasks = homeworkTasks.filter((task) => !task.isCompleted);
  const completedTasks = homeworkTasks.filter((task) => task.isCompleted);

  return `
    <div class="content-grid">
      <section class="stack">
        <section class="module panel">
          <div class="module-head">
            <div>
              <p class="eyebrow">HOMEWORK_COMPOSER</p>
              <h2 class="module-title">Р”РѕР±Р°РІРёС‚СЊ Р”Р—</h2>
            </div>
            <span class="tag accent">${visibleHomeworkGroups.length} ${hasPriorityGroups ? "РІ РїСЂРёРѕСЂРёС‚РµС‚Рµ" : "РїСЂРµРґРјРµС‚РѕРІ"}</span>
          </div>
          ${homeworkSubjects.length === 0
            ? emptyState("РЎРЅР°С‡Р°Р»Р° РІС‹Р±РµСЂРё СЂР°СЃРїРёСЃР°РЅРёРµ РІРѕ РІРєР»Р°РґРєРµ Р Р°СЃРїРёСЃР°РЅРёРµ. РўРѕРіРґР° mini app РїРѕРґС‚СЏРЅРµС‚ РїСЂРµРґРјРµС‚С‹ Рё Р±Р»РёР¶Р°Р№С€РёРµ РґРµРґР»Р°Р№РЅС‹.")
            : `
              <div class="priority-banner ${hasPriorityGroups ? "active" : ""}">
                <strong>${hasPriorityGroups
                  ? "Р”РѕР±Р°РІР»РµРЅРёРµ Р”Р— РёРґС‘С‚ РїРѕ РїСЂРёРѕСЂРёС‚РµС‚РЅС‹Рј РїСЂРµРґРјРµС‚Р°Рј."
                  : "РЎРµР№С‡Р°СЃ РІ С„РѕСЂРјРµ РІРёРґРЅС‹ РІСЃРµ РїСЂРµРґРјРµС‚С‹."}</strong>
                <p>${hasPriorityGroups
                  ? "РќРёР¶Рµ РїРѕРєР°Р·С‹РІР°РµРј С‚РѕР»СЊРєРѕ РїСЂРёРѕСЂРёС‚РµС‚С‹, РєР°Рє Рё РІ С‡Р°С‚Рµ. РћСЃС‚Р°Р»СЊРЅС‹Рµ РїСЂРµРґРјРµС‚С‹ РјРѕР¶РЅРѕ РІРµСЂРЅСѓС‚СЊ РІ С„РѕСЂРјСѓ С‡РµСЂРµР· Р±Р»РѕРє РЅР°СЃС‚СЂРѕР№РєРё РЅРёР¶Рµ."
                  : "РћС‚РјРµС‚СЊ РІР°Р¶РЅС‹Рµ РїСЂРµРґРјРµС‚С‹ РЅРёР¶Рµ, Рё РїРѕСЃР»Рµ СЌС‚РѕРіРѕ РІ С„РѕСЂРјРµ РѕСЃС‚Р°РЅСѓС‚СЃСЏ С‚РѕР»СЊРєРѕ РѕРЅРё."}</p>
              </div>
              <form id="homework-form" class="stack">
                <div class="field">
                  <label for="homework-group">РџСЂРµРґРјРµС‚ РґР»СЏ Р”Р—</label>
                  <select id="homework-group" name="subjectTitle">
                    ${visibleHomeworkGroups.map((group) => `
                      <option value="${escapeHtml(group.title)}" ${subjectGroup?.title === group.title ? "selected" : ""}>
                        ${escapeHtml(group.title)}${group.favoriteOrder ? ` // РїСЂРёРѕСЂРёС‚РµС‚ ${group.favoriteOrder}` : ""}
                      </option>
                    `).join("")}
                  </select>
                </div>
                <div class="field">
                  <label for="homework-subject">РўРёРї Р·Р°РЅСЏС‚РёСЏ</label>
                  <select id="homework-subject" name="subject">
                    ${(subjectGroup?.options || []).map((option) => `
                      <option value="${escapeHtml(option.subject)}">
                        ${escapeHtml(option.lessonType)}${option.nextDeadlineText ? ` // РґРµРґР»Р°Р№РЅ ${escapeHtml(option.nextDeadlineText)}` : ""}
                      </option>
                    `).join("")}
                  </select>
                </div>
                <div class="field">
                  <label for="homework-title">Р§С‚Рѕ Р·Р°РґР°Р»Рё</label>
                  <textarea id="homework-title" name="title" placeholder="РќР°РїСЂРёРјРµСЂ: СЂРµС€РёС‚СЊ РІР°СЂРёР°РЅС‚С‹ 3-6 Рё РїРѕРґРіРѕС‚РѕРІРёС‚СЊ РєРѕРЅСЃРїРµРєС‚"></textarea>
                </div>
                <button class="pixel-button" type="submit">Р”РѕР±Р°РІРёС‚СЊ Р”Р—</button>
              </form>
            `}
        </section>
        <section class="module panel">
          <div class="module-head">
            <div>
              <p class="eyebrow">PRIORITY_FILTER</p>
              <h2 class="module-title">РџСЂРёРѕСЂРёС‚РµС‚РЅС‹Рµ РїСЂРµРґРјРµС‚С‹</h2>
            </div>
            <span class="tag ${hasPriorityGroups ? "success" : "warning"}">${hasPriorityGroups ? `${priorityGroups.length} Р°РєС‚РёРІРЅС‹С…` : "РїРѕРєР° РЅРµ РІС‹Р±СЂР°РЅС‹"}</span>
          </div>
          <p class="priority-helper-text">
            РћС‚РјРµС‡РµРЅРЅС‹Рµ РїСЂРµРґРјРµС‚С‹ РїРѕРєР°Р·С‹РІР°СЋС‚СЃСЏ РІ С„РѕСЂРјРµ РґРѕР±Р°РІР»РµРЅРёСЏ Р”Р— РІ РїРµСЂРІСѓСЋ РѕС‡РµСЂРµРґСЊ. Р­С‚Рѕ Р·Р°РјРµРЅСЏРµС‚ СЃС‚Р°СЂРѕРµ В«РёР·Р±СЂР°РЅРЅРѕРµВ».
          </p>
          ${homeworkSubjects.length > 0 ? homeworkSubjects.map((group) => `
            <article class="subject-card ${group.isFavorite ? "priority" : ""}">
              <div class="subject-top">
                <div>
                  <h3 class="subject-title">${escapeHtml(group.title)}</h3>
                  <div class="subject-meta">
                    <span class="tag">${group.options.length} С‚РёРїРѕРІ Р·Р°РЅСЏС‚РёР№</span>
                    ${group.favoriteOrder ? `<span class="tag success">РїСЂРёРѕСЂРёС‚РµС‚ ${group.favoriteOrder}</span>` : `<span class="tag warning">РЅРµ РІ РїСЂРёРѕСЂРёС‚РµС‚Рµ</span>`}
                  </div>
                  <p class="subject-note">${group.isFavorite
                    ? "РџРѕРєР°Р·С‹РІР°РµС‚СЃСЏ РІ С„РѕСЂРјРµ РґРѕР±Р°РІР»РµРЅРёСЏ Р”Р—."
                    : "РЎРєСЂС‹С‚ РёР· С„РѕСЂРјС‹, РїРѕРєР° РЅРµ РґРѕР±Р°РІР»РµРЅ РІ РїСЂРёРѕСЂРёС‚РµС‚."}</p>
                </div>
                <button class="subject-toggle ${group.isFavorite ? "active" : ""}" data-action="toggle-favorite" data-subject-title="${escapeHtml(group.title)}">
                  ${group.isFavorite ? "РЈР±СЂР°С‚СЊ" : "Р’ РїСЂРёРѕСЂРёС‚РµС‚"}
                </button>
              </div>
            </article>
          `).join("") : emptyState("РџСЂРёРѕСЂРёС‚РµС‚С‹ РїРѕСЏРІСЏС‚СЃСЏ РїРѕСЃР»Рµ РІС‹Р±РѕСЂР° СЂР°СЃРїРёСЃР°РЅРёСЏ.")}
        </section>
      </section>
      <section class="stack">
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">РђРєС‚РёРІРЅС‹Рµ Р”Р—</h2>
            <span class="tag accent">${activeTasks.length}</span>
          </div>
          ${activeTasks.length > 0 ? activeTasks.map((task) => taskCard(task, "homework")).join("") : emptyState("Р—РґРµСЃСЊ Р±СѓРґРµС‚ СЃРїРёСЃРѕРє Р°РєС‚СѓР°Р»СЊРЅС‹С… РґРѕРјР°С€РЅРёС… Р·Р°РґР°РЅРёР№.")}
        </section>
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">Р’С‹РїРѕР»РЅРµРЅРЅС‹Рµ</h2>
            <span class="tag">${completedTasks.length}</span>
          </div>
          ${completedTasks.length > 0 ? completedTasks.map((task) => taskCard(task, "homework")).join("") : emptyState("РџРѕРєР° Р±РµР· РІС‹РїРѕР»РЅРµРЅРЅС‹С… Р·Р°РґР°С‡.")}
        </section>
      </section>
    </div>
  `;
}

function renderPlanView(personalTasks) {
  const activeTasks = personalTasks.filter((task) => !task.isCompleted);
  const completedTasks = personalTasks.filter((task) => task.isCompleted);

  return `
    <div class="content-grid">
      <section class="module panel">
        <div class="module-head">
          <div>
            <p class="eyebrow">PERSONAL_QUESTLOG</p>
            <h2 class="module-title">Р”РѕР±Р°РІРёС‚СЊ Р»РёС‡РЅРѕРµ РґРµР»Рѕ</h2>
          </div>
        </div>
        <form id="plan-form" class="stack">
          <div class="field">
            <label for="plan-title">РќР°Р·РІР°РЅРёРµ</label>
            <input id="plan-title" name="title" placeholder="РќР°РїСЂРёРјРµСЂ: Р·Р°РїРёСЃР°С‚СЊСЃСЏ Рє РІСЂР°С‡Сѓ">
          </div>
          <div class="two-column">
            <div class="field">
              <label for="plan-date">Р”Р°С‚Р°</label>
              <input id="plan-date" name="date" type="date">
            </div>
            <div class="field">
              <label for="plan-time">Р’СЂРµРјСЏ</label>
              <input id="plan-time" name="time" type="time">
            </div>
          </div>
          <div class="actions-row">
            <button class="nav-chip" type="button" data-action="plan-date" data-offset="0">РЎРµРіРѕРґРЅСЏ</button>
            <button class="nav-chip" type="button" data-action="plan-date" data-offset="1">Р—Р°РІС‚СЂР°</button>
            <button class="nav-chip" type="button" data-action="plan-date" data-offset="2">РџРѕСЃР»РµР·Р°РІС‚СЂР°</button>
          </div>
          <button class="pixel-button" type="submit">Р”РѕР±Р°РІРёС‚СЊ РґРµР»Рѕ</button>
        </form>
      </section>
      <section class="stack">
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">РђРєС‚РёРІРЅС‹Рµ РґРµР»Р°</h2>
            <span class="tag accent">${activeTasks.length}</span>
          </div>
          ${activeTasks.length > 0 ? activeTasks.map(task => taskCard(task, "personal")).join("") : emptyState("Р—РґРµСЃСЊ РјРѕР¶РЅРѕ РґРµСЂР¶Р°С‚СЊ РІСЃС‘ Р»РёС‡РЅРѕРµ: Р·РІРѕРЅРєРё, РІСЃС‚СЂРµС‡Рё, РїРѕРєСѓРїРєРё, РґРµРґР»Р°Р№РЅС‹ РІРЅРµ СѓС‡С‘Р±С‹.")}
        </section>
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">РђСЂС…РёРІ</h2>
            <span class="tag">${completedTasks.length}</span>
          </div>
          ${completedTasks.length > 0 ? completedTasks.map(task => taskCard(task, "personal")).join("") : emptyState("Р’С‹РїРѕР»РЅРµРЅРЅС‹Рµ Р»РёС‡РЅС‹Рµ РґРµР»Р° РїРѕСЏРІСЏС‚СЃСЏ Р·РґРµСЃСЊ.")}
        </section>
      </section>
    </div>
  `;
}

function renderFocusView(timer, reminder) {
  return `
    <div class="focus-grid">
      <section class="module panel">
        <div class="module-head">
          <div>
            <p class="eyebrow">FOCUS_ENGINE</p>
            <h2 class="module-title">Таймеры</h2>
          </div>
          ${timer.isActive ? '<button class="pixel-button danger slim" data-action="stop-timer">Стоп</button>' : ""}
        </div>
        <div class="focus-display">
          <p class="eyebrow">ACTIVE_LOOP</p>
          <p class="focus-clock">${escapeHtml(timerText(timer))}</p>
          <p class="muted">${timer.isActive ? `режим: ${escapeHtml(timer.type || "")}` : "Выбери рабочий или отдых-таймер."}</p>
        </div>
        <div class="sound-panel">
          <div class="module-head compact">
            <div>
              <p class="eyebrow">SOUNDTRACK</p>
              <h3 class="module-title small">Музыка для таймера</h3>
            </div>
            <span class="tag">${escapeHtml(TIMER_SOUND_META[store.selectedTimerSound]?.label || "Тишина")}</span>
          </div>
          <div class="sound-grid">
            ${Object.entries(TIMER_SOUND_META).map(([soundKey, meta]) => `
              <button class="sound-chip ${store.selectedTimerSound === soundKey ? "active" : ""}" data-action="sound-mode" data-sound="${soundKey}">
                <strong>${escapeHtml(meta.label)}</strong>
                <span>${escapeHtml(meta.hint)}</span>
              </button>
            `).join("")}
          </div>
        </div>
        <div class="divider"></div>
        <div class="stack">
          <div>
            <p class="eyebrow">WORK_PRESETS</p>
            <div class="actions-row">
              ${[25, 30, 45, 60].map((minutes) => `<button class="pixel-button secondary" data-action="start-timer" data-type="work" data-minutes="${minutes}">${minutes} мин</button>`).join("")}
            </div>
          </div>
          <form id="custom-work-form" class="actions-row">
            <input name="minutes" type="number" min="1" max="300" placeholder="своё время">
            <button class="pixel-button" type="submit">Старт учёбы</button>
          </form>
          <div>
            <p class="eyebrow">REST_PRESETS</p>
            <div class="actions-row">
              ${[5, 15, 30].map((minutes) => `<button class="pixel-button secondary" data-action="start-timer" data-type="rest" data-minutes="${minutes}">${minutes} мин</button>`).join("")}
            </div>
          </div>
          <form id="custom-rest-form" class="actions-row">
            <input name="minutes" type="number" min="1" max="300" placeholder="свой перерыв">
            <button class="pixel-button" type="submit">Старт отдыха</button>
          </form>
        </div>
      </section>
      <section class="module panel">
        <div class="module-head">
          <div>
            <p class="eyebrow">ALERT_ROUTER</p>
            <h2 class="module-title">Напоминания</h2>
          </div>
          <span class="tag ${reminder.isEnabled ? "success" : "warning"}">${reminder.isEnabled ? reminder.timeText : "выкл"}</span>
        </div>
        <form id="reminders-form" class="stack">
          <div class="field">
            <label for="reminders-enabled">Режим</label>
            <select id="reminders-enabled" name="isEnabled">
              <option value="true" ${reminder.isEnabled ? "selected" : ""}>Включить</option>
              <option value="false" ${!reminder.isEnabled ? "selected" : ""}>Выключить</option>
            </select>
          </div>
          <div class="field time-field">
            <label for="reminders-time">Время по МСК</label>
            <input id="reminders-time" name="time" type="time" value="${escapeHtml(reminder.timeText)}">
          </div>
          <button class="pixel-button" type="submit">Сохранить напоминания</button>
        </form>
        <div class="divider"></div>
        <p class="muted">Чат и mini app используют одни и те же настройки, поэтому изменения сразу синхронизируются между интерфейсами.</p>
      </section>
    </div>
  `;
}

function renderRemindersView(reminder) {
  return `
    <div class="single-column">
      <section class="module panel">
        <div class="module-head">
          <div>
            <p class="eyebrow">ALERT_ROUTER</p>
            <h2 class="module-title">РќР°РїРѕРјРёРЅР°РЅРёСЏ</h2>
          </div>
          <span class="tag ${reminder.isEnabled ? "success" : "warning"}">${reminder.isEnabled ? reminder.timeText : "РІС‹РєР»"}</span>
        </div>
        <form id="reminders-form" class="stack">
          <div class="field">
            <label for="reminders-enabled">Р РµР¶РёРј</label>
            <select id="reminders-enabled" name="isEnabled">
              <option value="true" ${reminder.isEnabled ? "selected" : ""}>Р’РєР»СЋС‡РёС‚СЊ</option>
              <option value="false" ${!reminder.isEnabled ? "selected" : ""}>Р’С‹РєР»СЋС‡РёС‚СЊ</option>
            </select>
          </div>
          <div class="field time-field">
            <label for="reminders-time">Р’СЂРµРјСЏ РїРѕ РњРЎРљ</label>
            <input id="reminders-time" name="time" type="time" value="${escapeHtml(reminder.timeText)}">
          </div>
          <button class="pixel-button" type="submit">РЎРѕС…СЂР°РЅРёС‚СЊ РЅР°РїРѕРјРёРЅР°РЅРёСЏ</button>
        </form>
        <div class="divider"></div>
        <p class="muted">Р§Р°С‚ Рё mini app РёСЃРїРѕР»СЊР·СѓСЋС‚ РѕРґРЅРё Рё С‚Рµ Р¶Рµ РЅР°СЃС‚СЂРѕР№РєРё, РїРѕСЌС‚РѕРјСѓ РёР·РјРµРЅРµРЅРёСЏ СЃСЂР°Р·Сѓ СЃРёРЅС…СЂРѕРЅРёР·РёСЂСѓСЋС‚СЃСЏ РјРµР¶РґСѓ РёРЅС‚РµСЂС„РµР№СЃР°РјРё.</p>
      </section>
    </div>
  `;
}

function navChip(view, label) {
  return `<button class="nav-chip ${store.activeView === view ? "active" : ""}" data-view="${view}">${escapeHtml(label)}</button>`;
}

function heroStat(label, value, hint) {
  return `
    <article class="hero-stat">
      <p>${escapeHtml(label)}</p>
      <strong>${escapeHtml(String(value))}</strong>
      <span>${escapeHtml(hint)}</span>
    </article>
  `;
}

function statusActionButton(view, label, tone) {
  return `
    <button class="status-action ${tone}" data-view="${view}">
      <span class="status-action-title">${label}</span>
    </button>
  `;
}

function shortcutCard(view, description) {
  const meta = VIEW_META[view];
  return `
    <button class="shortcut-card" data-view="${view}">
      <span class="shortcut-icon">${escapeHtml(meta.icon)}</span>
      <span class="shortcut-copy">
        <strong>${escapeHtml(meta.label)}</strong>
        <small>${escapeHtml(description)}</small>
      </span>
    </button>
  `;
}

function tabButton(view, meta) {
  return `
    <button class="tabbar-button ${store.activeView === view ? "active" : ""}" data-view="${view}">
      <span class="tabbar-icon">${escapeHtml(meta.icon)}</span>
      <span class="tabbar-label">${escapeHtml(meta.shortLabel || meta.label)}</span>
    </button>
  `;
}

function statCard(label, value, subtle, tone) {
  return `
    <article class="stat-card panel">
      <div class="stat-label">${escapeHtml(label)}</div>
      <div class="stat-value">${escapeHtml(String(value))}</div>
      <div class="stat-subtle ${tone}">${escapeHtml(subtle)}</div>
    </article>
  `;
}

function taskCard(task, scope) {
  return `
    <article class="task-card ${task.isCompleted ? "completed" : ""}">
      <div class="task-top">
        <div>
          <h3 class="task-title">${escapeHtml(task.title)}</h3>
          <div class="task-meta">
            <span class="tag accent">${escapeHtml(task.subjectTitle)}</span>
            ${task.lessonType ? `<span class="tag">${escapeHtml(task.lessonType)}</span>` : ""}
            ${task.deadlineText ? `<span class="tag ${task.isCompleted ? "" : "warning"}">${escapeHtml(task.deadlineText)}</span>` : `<span class="tag">Р±РµР· РґРµРґР»Р°Р№РЅР°</span>`}
          </div>
        </div>
        <span class="tag ${task.isCompleted ? "success" : "accent"}">${task.isCompleted ? "done" : "active"}</span>
      </div>
      <div class="task-actions">
        <button class="pixel-button secondary slim" data-action="toggle-task" data-scope="${scope}" data-task-id="${escapeHtml(task.id)}" data-completed="${String(!task.isCompleted)}">
          ${task.isCompleted ? "Р’РµСЂРЅСѓС‚СЊ" : "Р’С‹РїРѕР»РЅРµРЅРѕ"}
        </button>
        <button class="pixel-button ghost slim" data-action="delete-task" data-task-id="${escapeHtml(task.id)}">РЈРґР°Р»РёС‚СЊ</button>
      </div>
    </article>
  `;
}

function emptyState(message) {
  return `<div class="section-empty"><strong>РџСѓСЃС‚Рѕ.</strong><br>${escapeHtml(message)}</div>`;
}

async function handleClick(event) {
  const target = event.target.closest("button");
  if (!target) {
    return;
  }

  if (target.dataset.view) {
    store.activeView = target.dataset.view;
    render();
    return;
  }

  if (target.dataset.theme) {
    applyTheme(target.dataset.theme);
    render();
    return;
  }

  if (target.dataset.action === "sound-mode") {
    store.selectedTimerSound = target.dataset.sound || "off";
    localStorage.setItem("assistKentTimerSound", store.selectedTimerSound);
    await syncTimerAudio({ allowResume: true, forceRestart: true });
    toast(`Р—РІСѓРє С‚Р°Р№РјРµСЂР°: ${TIMER_SOUND_META[store.selectedTimerSound]?.label || "РўРёС€РёРЅР°"}.`);
    render();
    return;
  }

  if (target.dataset.action === "refresh") {
    await runAction(() => refreshState());
    return;
  }

  if (target.dataset.action === "schedule-mode") {
    store.scheduleMode = target.dataset.mode || "today";
    render();
    return;
  }

  if (target.dataset.action === "toggle-favorite") {
    const subjectTitle = target.dataset.subjectTitle;
    await runAction(async () => {
      store.state = await api("/api/miniapp/favorite-subjects/toggle", {
        method: "POST",
        body: { subjectTitle }
      });
      toast("РџСЂРёРѕСЂРёС‚РµС‚С‹ РїРѕ Р”Р— РѕР±РЅРѕРІР»РµРЅС‹.");
      refreshAfterMutation();
    });
    return;
  }

  if (target.dataset.action === "toggle-task") {
    const taskId = target.dataset.taskId;
    const isCompleted = target.dataset.completed === "true";
    await runAction(async () => {
      store.state = await api(`/api/miniapp/tasks/${taskId}/completion`, {
        method: "PATCH",
        body: { isCompleted }
      });
      toast(isCompleted ? "Р—Р°РґР°С‡Р° РѕС‚РјРµС‡РµРЅР° РІС‹РїРѕР»РЅРµРЅРЅРѕР№." : "Р—Р°РґР°С‡Р° РІРѕР·РІСЂР°С‰РµРЅР° РІ Р°РєС‚РёРІРЅС‹Рµ.");
      refreshAfterMutation();
    });
    return;
  }

  if (target.dataset.action === "delete-task") {
    const taskId = target.dataset.taskId;
    await runAction(async () => {
      store.state = await api(`/api/miniapp/tasks/${taskId}`, { method: "DELETE" });
      toast("Р—Р°РґР°С‡Р° СѓРґР°Р»РµРЅР°.");
      refreshAfterMutation();
    });
    return;
  }

  if (target.dataset.action === "plan-date") {
    const offset = Number(target.dataset.offset || 0);
    const planDate = document.getElementById("plan-date");
    if (planDate) {
      const targetDate = new Date();
      targetDate.setDate(targetDate.getDate() + offset);
      planDate.value = targetDate.toISOString().slice(0, 10);
    }
    return;
  }

  if (target.dataset.action === "start-timer") {
    const minutes = Number(target.dataset.minutes || 0);
    const type = target.dataset.type;
    await runAction(async () => {
      store.state = await api("/api/miniapp/timers/start", {
        method: "POST",
        body: { type, minutes }
      });
      toast(type === "rest" ? "РўР°Р№РјРµСЂ РѕС‚РґС‹С…Р° Р·Р°РїСѓС‰РµРЅ." : "Р Р°Р±РѕС‡РёР№ С‚Р°Р№РјРµСЂ Р·Р°РїСѓС‰РµРЅ.");
      refreshAfterMutation();
    });
    return;
  }

  if (target.dataset.action === "stop-timer") {
    await runAction(async () => {
      store.state = await api("/api/miniapp/timers/stop", { method: "POST" });
      toast("РўР°Р№РјРµСЂ РѕСЃС‚Р°РЅРѕРІР»РµРЅ.");
      refreshAfterMutation();
    });
    return;
  }

  if (target.dataset.action === "clear-schedule") {
    await runAction(async () => {
      store.state = await api("/api/miniapp/schedule", { method: "DELETE" });
      toast("РџСЂРёРІСЏР·РєР° СЂР°СЃРїРёСЃР°РЅРёСЏ СѓРґР°Р»РµРЅР°.");
      refreshAfterMutation();
    });
  }
}

async function handleSubmit(event) {
  const form = event.target;
  if (!(form instanceof HTMLFormElement)) {
    return;
  }

  event.preventDefault();

  if (form.id === "schedule-form") {
    const formData = new FormData(form);
    const scheduleId = String(formData.get("scheduleId") || "");
    const subgroupRaw = String(formData.get("subGroup") || "");
    await runAction(async () => {
      store.state = await api("/api/miniapp/schedule", {
        method: "PUT",
        body: {
          scheduleId,
          subGroup: subgroupRaw ? Number(subgroupRaw) : null
        }
      });
      toast("Р Р°СЃРїРёСЃР°РЅРёРµ СЃРѕС…СЂР°РЅРµРЅРѕ.");
      refreshAfterMutation();
    });
    return;
  }

  if (form.id === "homework-form") {
    const formData = new FormData(form);
    const subject = String(formData.get("subject") || "");
    const title = String(formData.get("title") || "").trim();
    await runAction(async () => {
      store.state = await api("/api/miniapp/homework", {
        method: "POST",
        body: { subject, title }
      });
      form.reset();
      toast("Р”РѕРјР°С€РЅРµРµ Р·Р°РґР°РЅРёРµ РґРѕР±Р°РІР»РµРЅРѕ.");
      refreshAfterMutation();
    });
    return;
  }

  if (form.id === "plan-form") {
    const formData = new FormData(form);
    const title = String(formData.get("title") || "").trim();
    const date = String(formData.get("date") || "");
    const time = String(formData.get("time") || "");
    const deadline = buildDeadline(date, time);

    await runAction(async () => {
      store.state = await api("/api/miniapp/plan", {
        method: "POST",
        body: { title, deadline }
      });
      form.reset();
      toast("Р›РёС‡РЅРѕРµ РґРµР»Рѕ РґРѕР±Р°РІР»РµРЅРѕ.");
      refreshAfterMutation();
    });
    return;
  }

  if (form.id === "reminders-form") {
    const formData = new FormData(form);
    const isEnabled = String(formData.get("isEnabled")) === "true";
    const time = String(formData.get("time") || "20:00");
    const [hour, minute] = time.split(":").map(Number);

    await runAction(async () => {
      store.state = await api("/api/miniapp/reminders", {
        method: "PUT",
        body: { isEnabled, hour, minute }
      });
      toast("РќР°РїРѕРјРёРЅР°РЅРёСЏ СЃРѕС…СЂР°РЅРµРЅС‹.");
      refreshAfterMutation();
    });
    return;
  }

  if (form.id === "custom-work-form" || form.id === "custom-rest-form") {
    const formData = new FormData(form);
    const minutes = Number(formData.get("minutes") || 0);
    const type = form.id === "custom-rest-form" ? "rest" : "work";
    await runAction(async () => {
      store.state = await api("/api/miniapp/timers/start", {
        method: "POST",
        body: { type, minutes }
      });
      form.reset();
      toast(type === "rest" ? "РџРµСЂРµСЂС‹РІ Р·Р°РїСѓС‰РµРЅ." : "Р Р°Р±РѕС‡РёР№ С‚Р°Р№РјРµСЂ Р·Р°РїСѓС‰РµРЅ.");
      refreshAfterMutation();
    });
  }
}

async function handleChange(event) {
  const target = event.target;
  if (!(target instanceof HTMLSelectElement)) {
    return;
  }

  if (target.id === "direction-select") {
    store.selectedDirectionCode = target.value;
    await runAction(async () => {
      store.groups = await api(`/api/miniapp/groups?directionCode=${encodeURIComponent(target.value)}`);
      render();
    });
    return;
  }

  if (target.id === "homework-group") {
    store.selectedHomeworkGroup = target.value;
    render();
  }
}

function refreshAfterMutation() {
  if (store.state) {
    store.selectedDirectionCode = store.state.schedule.selectedDirectionCode
      || store.selectedDirectionCode
      || store.state.schedule.directions[0]?.directionCode
      || "";
    store.groups = store.state.schedule.availableGroups || [];

    normalizeSelectedHomeworkGroup(store.state.homeworkSubjects);
  }

  store.lastSyncLabel = `РЎРёРЅС…СЂРѕРЅРёР·РёСЂРѕРІР°РЅРѕ ${new Date().toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit", second: "2-digit" })}`;
  render();
  restartTimerTicker();
  syncTimerAudio();
}

function restartTimerTicker() {
  window.clearInterval(store.timerTick);
  if (!store.state?.timer?.isActive) {
    stopTimerAudio();
    return;
  }

  syncTimerAudio();

  store.timerTick = window.setInterval(() => {
    const activeClock = document.querySelector(".focus-clock");
    if (activeClock instanceof HTMLElement) {
      activeClock.textContent = timerText(store.state.timer);
    }

    if (timerText(store.state.timer) === "00:00") {
      stopTimerAudio();
    }
  }, 1000);
}

async function resumeAudioContext() {
  const AudioContextCtor = window.AudioContext || window.webkitAudioContext;
  if (!AudioContextCtor) {
    return null;
  }

  if (!store.audioContext) {
    store.audioContext = new AudioContextCtor();
  }

  if (store.audioContext.state === "suspended") {
    await store.audioContext.resume();
  }

  return store.audioContext;
}

function stopTimerAudio() {
  if (typeof store.activeSoundCleanup === "function") {
    store.activeSoundCleanup();
  }

  store.activeSoundCleanup = null;
  store.activeSoundMode = "off";
}

async function syncTimerAudio({ allowResume = false, forceRestart = false } = {}) {
  if (!store.state?.timer?.isActive || store.selectedTimerSound === "off") {
    stopTimerAudio();
    return;
  }

  const context = allowResume ? await resumeAudioContext() : store.audioContext;
  if (!context || context.state !== "running") {
    return;
  }

  if (!forceRestart && store.activeSoundMode === store.selectedTimerSound && store.activeSoundCleanup) {
    return;
  }

  stopTimerAudio();
  store.activeSoundCleanup = startTimerSound(context, store.selectedTimerSound);
  store.activeSoundMode = store.selectedTimerSound;
}

function startTimerSound(context, mode) {
  switch (mode) {
    case "pulse":
      return createPulseSound(context);
    case "rain":
      return createRainSound(context);
    case "arcade":
      return createArcadeSound(context);
    default:
      return null;
  }
}

function createPulseSound(context) {
  const master = context.createGain();
  master.gain.value = 0.028;
  master.connect(context.destination);

  const drone = context.createOscillator();
  drone.type = "sine";
  drone.frequency.value = 174;

  const droneGain = context.createGain();
  droneGain.gain.value = 0.7;
  drone.connect(droneGain);
  droneGain.connect(master);
  drone.start();

  const pulse = context.createOscillator();
  pulse.type = "triangle";
  pulse.frequency.value = 522;

  const pulseGain = context.createGain();
  pulseGain.gain.value = 0.0001;
  pulse.connect(pulseGain);
  pulseGain.connect(master);
  pulse.start();

  let disposed = false;
  const runPulse = () => {
    if (disposed) {
      return;
    }

    const now = context.currentTime;
    pulseGain.gain.cancelScheduledValues(now);
    pulseGain.gain.setValueAtTime(0.0001, now);
    pulseGain.gain.linearRampToValueAtTime(0.18, now + 0.08);
    pulseGain.gain.exponentialRampToValueAtTime(0.0001, now + 0.85);
    window.setTimeout(runPulse, 1400);
  };

  runPulse();

  return () => {
    disposed = true;
    drone.stop();
    pulse.stop();
    master.disconnect();
  };
}

function createRainSound(context) {
  const master = context.createGain();
  master.gain.value = 0.02;
  master.connect(context.destination);

  const duration = 2;
  const buffer = context.createBuffer(1, context.sampleRate * duration, context.sampleRate);
  const channel = buffer.getChannelData(0);
  for (let index = 0; index < channel.length; index += 1) {
    channel[index] = (Math.random() * 2 - 1) * 0.35;
  }

  const noise = context.createBufferSource();
  noise.buffer = buffer;
  noise.loop = true;

  const filter = context.createBiquadFilter();
  filter.type = "lowpass";
  filter.frequency.value = 860;
  filter.Q.value = 0.2;

  const swell = context.createOscillator();
  swell.type = "sine";
  swell.frequency.value = 0.08;

  const swellGain = context.createGain();
  swellGain.gain.value = 90;
  swell.connect(swellGain);
  swellGain.connect(filter.frequency);

  noise.connect(filter);
  filter.connect(master);
  noise.start();
  swell.start();

  return () => {
    noise.stop();
    swell.stop();
    master.disconnect();
  };
}

function createArcadeSound(context) {
  const master = context.createGain();
  master.gain.value = 0.022;
  master.connect(context.destination);

  const lead = context.createOscillator();
  lead.type = "square";
  const leadGain = context.createGain();
  leadGain.gain.value = 0.0001;
  lead.connect(leadGain);
  leadGain.connect(master);
  lead.start();

  const bass = context.createOscillator();
  bass.type = "triangle";
  bass.frequency.value = 131;
  const bassGain = context.createGain();
  bassGain.gain.value = 0.07;
  bass.connect(bassGain);
  bassGain.connect(master);
  bass.start();

  const notes = [392, 523.25, 659.25, 523.25, 392, 659.25, 523.25, 329.63];
  let step = 0;
  let disposed = false;
  const playStep = () => {
    if (disposed) {
      return;
    }

    const now = context.currentTime;
    const note = notes[step % notes.length];
    lead.frequency.setValueAtTime(note, now);
    leadGain.gain.cancelScheduledValues(now);
    leadGain.gain.setValueAtTime(0.0001, now);
    leadGain.gain.linearRampToValueAtTime(0.16, now + 0.02);
    leadGain.gain.exponentialRampToValueAtTime(0.0001, now + 0.32);
    step += 1;
    window.setTimeout(playStep, 360);
  };

  playStep();

  return () => {
    disposed = true;
    lead.stop();
    bass.stop();
    master.disconnect();
  };
}

function timerText(timer) {
  if (!timer?.isActive || !timer.endsAtIso) {
    return "00:00";
  }

  const endsAt = new Date(timer.endsAtIso).getTime();
  const diff = endsAt - Date.now();
  if (diff <= 0) {
    return "00:00";
  }

  const totalSeconds = Math.floor(diff / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  return hours > 0
    ? `${pad(hours)}:${pad(minutes)}:${pad(seconds)}`
    : `${pad(minutes)}:${pad(seconds)}`;
}

function groupScheduleEntries(entries) {
  return entries.reduce((accumulator, entry) => {
    const day = entry.dayName;
    accumulator[day] ||= [];
    accumulator[day].push(entry);
    return accumulator;
  }, {});
}

function getInitials(name) {
  return name
    .split(" ")
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() || "")
    .join("");
}

function buildDeadline(date, time) {
  if (!date) {
    return null;
  }

  return `${date}T${time || "00:00"}:00`;
}

function applyTheme(theme) {
  store.selectedTheme = theme;
  localStorage.setItem("assistKentTheme", theme);
  document.documentElement.dataset.theme = theme;
}

function toast(message) {
  store.toast.hidden = false;
  store.toast.textContent = message;
  window.clearTimeout(store.toastTimer);
  store.toastTimer = window.setTimeout(() => {
    store.toast.hidden = true;
  }, 2400);
}

async function runAction(action) {
  try {
    tg?.HapticFeedback?.impactOccurred?.("light");
    await resumeAudioContext();
    await action();
  } catch (error) {
    toast(error.message || "Р§С‚Рѕ-С‚Рѕ РїРѕС€Р»Рѕ РЅРµ С‚Р°Рє.");
  }
}

function handleFatalError(error) {
  store.root.innerHTML = `
    <section class="boot-card panel">
      <p class="eyebrow">BOOT_FAILED</p>
      <h1>Mini App РЅРµРґРѕСЃС‚СѓРїРµРЅ</h1>
      <p class="boot-copy">${escapeHtml(error.message || "РќРµ СѓРґР°Р»РѕСЃСЊ Р·Р°РіСЂСѓР·РёС‚СЊ РґР°РЅРЅС‹Рµ.")}</p>
      <p class="muted">Р•СЃР»Рё РѕС‚РєСЂС‹РІР°РµС€СЊ mini app РЅРµ РёР· Telegram, РґРѕР±Р°РІСЊ <code>?devUserId=...</code> Рё РІРєР»СЋС‡Рё <code>MiniApp:AllowDebugAuth</code>.</p>
    </section>
  `;
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

function pad(value) {
  return String(value).padStart(2, "0");
}



