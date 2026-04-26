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
  lastSyncLabel: "Синхронизация...",
  timerTick: null,
  refreshTick: null
};

const THEME_LABELS = {
  cobalt: "Cobalt",
  ember: "Ember",
  matrix: "Matrix"
};

const VIEW_META = {
  dashboard: { label: "Обзор", icon: "◫", eyebrow: "MISSION_BOARD" },
  schedule: { label: "Расписание", icon: "⌘", eyebrow: "SCHEDULE_MATRIX" },
  homework: { label: "Домашка", icon: "✦", eyebrow: "HOMEWORK_STACK" },
  plan: { label: "План", icon: "▣", eyebrow: "PERSONAL_QUEUE" },
  focus: { label: "Фокус", icon: "◎", eyebrow: "FOCUS_ENGINE" }
};

VIEW_META.reminders = { label: "Напоминания", icon: "◌", eyebrow: "ALERT_ROUTER" };

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
    throw new Error(data?.error || `Ошибка запроса (${response.status})`);
  }

  return data;
}

async function refreshState({ silent = false } = {}) {
  const state = await api("/api/miniapp/state");
  store.state = state;
  store.lastSyncLabel = `Синхронизировано ${new Date().toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit", second: "2-digit" })}`;

  if (!store.selectedDirectionCode) {
    store.selectedDirectionCode = state.schedule.selectedDirectionCode || state.schedule.directions[0]?.directionCode || "";
  }

  store.groups = state.schedule.availableGroups || [];

  if (!store.selectedHomeworkGroup && state.homeworkSubjects.length > 0) {
    store.selectedHomeworkGroup = state.homeworkSubjects[0].title;
  }

  if (store.selectedHomeworkGroup &&
      !state.homeworkSubjects.some((group) => group.title === store.selectedHomeworkGroup)) {
    store.selectedHomeworkGroup = state.homeworkSubjects[0]?.title || "";
  }

  render();
  restartTimerTicker();

  if (!silent) {
    tg?.MainButton?.hide();
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
                <p class="muted">${escapeHtml(user.username || "без username")} // ${escapeHtml(store.lastSyncLabel)}</p>
              </div>
            </div>
            <div class="topbar-actions">
              <button class="pixel-button secondary slim" data-action="refresh">Обновить</button>
            </div>
          </div>
          <div class="theme-panel">
            <span class="theme-label">Тема интерфейса</span>
            <div class="theme-switcher compact">
              ${Object.entries(THEME_LABELS).map(([key, label]) => `
                <button class="theme-chip ${store.selectedTheme === key ? "active" : ""}" data-theme="${key}" aria-label="Тема ${escapeHtml(label)}">
                  <span class="theme-chip-dot theme-${key}"></span>
                  <span>${escapeHtml(label)}</span>
                </button>
              `).join("")}
            </div>
          </div>
          <div class="status-strip">
            ${statusActionButton("schedule", schedule.selection ? "Расписание подключено" : "Нужно выбрать расписание", "accent")}
            ${statusActionButton("reminders", reminder.isEnabled ? `Напоминания ${escapeHtml(reminder.timeText)}` : "Напоминания выключены", reminder.isEnabled ? "success" : "warning")}
            ${statusActionButton("focus", timer.isActive ? `Таймер ${escapeHtml(timer.type || "")}` : "Таймер не запущен", "default")}
          </div>
          <div class="hero-stats">
            ${heroStat("Дедлайны", stats.homeworkPending, "активных")}
            ${heroStat("План", stats.personalPending, "задач")}
            ${heroStat("Неделя", schedule.currentWeekType, schedule.currentWeekLabel)}
          </div>
          <div class="shortcut-grid">
            ${shortcutCard("schedule", "Открыть пары и сменить группу")}
            ${shortcutCard("homework", "Посмотреть и добавить ДЗ")}
            ${shortcutCard("plan", "Быстрый доступ к личным делам")}
            ${shortcutCard("focus", "Запустить таймер учебы")}
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
      return renderHomeworkView(context.homeworkSubjects, context.tasks.homework);
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
  return `
    <div class="content-grid">
      <section class="module panel">
        <div class="module-head">
          <div>
            <p class="eyebrow">MISSION_BOARD</p>
            <h2 class="module-title">Текущая обстановка</h2>
          </div>
          <button class="pixel-button secondary" data-action="refresh">Обновить</button>
        </div>
        <div class="overview-grid">
          <article class="info-card panel">
            <div class="module-head">
              <h3 class="module-title">Сегодня</h3>
              <span class="tag accent">${schedule.todayEntries.length} пар</span>
            </div>
            ${schedule.todayEntries.length > 0 ? schedule.todayEntries.slice(0, 4).map(entry => `
              <div class="schedule-entry">
                <div class="lesson-pill">${entry.lessonNumber}</div>
                <div>
                  <div><strong>${escapeHtml(entry.subject)}</strong></div>
                  <div class="muted">${escapeHtml(entry.time || "время не указано")}</div>
                </div>
              </div>
            `).join("") : emptyState("На сегодня пар нет или расписание ещё не выбрано.")}
          </article>
          <article class="info-card panel">
            <div class="module-head">
              <h3 class="module-title">Активный фокус</h3>
              <span class="tag ${timer.isActive ? "success" : "warning"}">${timer.isActive ? "в работе" : "неактивен"}</span>
            </div>
            <div class="focus-display">
              <p class="eyebrow">FOCUS_ENGINE</p>
              <p class="focus-clock">${escapeHtml(timerText(timer))}</p>
              <p class="muted">${timer.isActive ? `режим ${escapeHtml(timer.type || "")}` : "Запусти рабочий или отдых-таймер в разделе Фокус."}</p>
            </div>
          </article>
        </div>
      </section>
      <section class="stack">
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">Дедлайны</h2>
            <span class="tag accent">${activeHomework.length} активных</span>
          </div>
          ${activeHomework.length > 0
            ? activeHomework.slice(0, 4).map(task => taskCard(task, "homework")).join("")
            : emptyState("Домашние задания появятся здесь после добавления.")}
        </section>
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">Личный план</h2>
            <span class="tag accent">${activePersonal.length} активных</span>
          </div>
          ${activePersonal.length > 0
            ? activePersonal.slice(0, 4).map(task => taskCard(task, "personal")).join("")
            : emptyState("Добавь свои дела, чтобы не держать всё в голове.")}
        </section>
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">Напоминания</h2>
            <span class="tag ${reminder.isEnabled ? "success" : "warning"}">${reminder.isEnabled ? reminder.timeText : "off"}</span>
          </div>
          <p class="muted">
            ${reminder.isEnabled
              ? `Чат получит напоминание о дедлайнах на завтра каждый день в ${escapeHtml(reminder.timeText)} по МСК.`
              : "Напоминания выключены. Включи их во вкладке Фокус."}
          </p>
          <div class="divider"></div>
          <p class="muted">Выполнено всего: <strong>${completedTasks.length}</strong></p>
        </section>
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
            <h2 class="module-title">Выбор расписания</h2>
          </div>
          ${schedule.selection ? '<button class="pixel-button danger slim" data-action="clear-schedule">Удалить</button>' : ""}
        </div>
        <form id="schedule-form" class="stack">
          <div class="field">
            <label for="direction-select">Направление</label>
            <select id="direction-select" name="directionCode">
              ${schedule.directions.map(direction => `
                <option value="${escapeHtml(direction.directionCode)}" ${store.selectedDirectionCode === direction.directionCode ? "selected" : ""}>
                  ${escapeHtml(direction.shortTitle)} - ${escapeHtml(direction.directionName)}
                </option>
              `).join("")}
            </select>
          </div>
          <div class="field">
            <label for="group-select">Курс / группа</label>
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
              <label for="subgroup-select">Подгруппа</label>
              <select id="subgroup-select" name="subGroup">
                ${selectedGroup.subGroups.map(subGroup => `
                  <option value="${subGroup}" ${String(selectedSubgroup) === String(subGroup) ? "selected" : ""}>Подгруппа ${subGroup}</option>
                `).join("")}
              </select>
            </div>
          ` : ""}
          <button class="pixel-button" type="submit">Сохранить расписание</button>
        </form>
        <div class="divider"></div>
        <div class="card-stack">
          <article class="schedule-card">
            <p class="eyebrow">CURRENT_BINDING</p>
            ${schedule.selection ? `
              <h3 class="schedule-day-title">${escapeHtml(schedule.selection.title)}</h3>
              <div class="schedule-meta">
                <span class="tag accent">${escapeHtml(schedule.currentWeekLabel)}</span>
                <span class="tag">${schedule.selection.subGroup ? `подгруппа ${schedule.selection.subGroup}` : "без подгруппы"}</span>
                <span class="tag">${escapeHtml(schedule.semester)}</span>
              </div>
            ` : emptyState("Пока ничего не выбрано. Подключи группу, и mini app подтянет предметы и дедлайны.")}
          </article>
        </div>
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
            <h2 class="module-title">Просмотр пар</h2>
          </div>
          <div class="actions-row">
            <button class="nav-chip ${store.scheduleMode === "today" ? "active" : ""}" data-action="schedule-mode" data-mode="today">Сегодня</button>
            <button class="nav-chip ${store.scheduleMode === "week" ? "active" : ""}" data-action="schedule-mode" data-mode="week">Неделя</button>
          </div>
        </div>
        ${entries.length > 0 ? Object.entries(grouped).map(([day, dayEntries]) => `
          <div class="schedule-day">
            <div class="module-head">
              <h3 class="schedule-day-title">${escapeHtml(day)}</h3>
              <span class="tag">${dayEntries.length} пар</span>
            </div>
            ${dayEntries.map(entry => `
              <div class="schedule-entry">
                <div class="lesson-pill">${entry.lessonNumber}</div>
                <div>
                  <div><strong>${escapeHtml(entry.subject)}</strong></div>
                  <div class="muted">${escapeHtml(entry.time || "время не указано")}</div>
                </div>
              </div>
            `).join("")}
          </div>
        `).join("") : emptyState("Нет данных для показа. Обычно это значит, что расписание ещё не выбрано или на сегодня пар нет.")}
      </section>
    </div>
  `;
}

function renderHomeworkView(homeworkSubjects, homeworkTasks) {
  const subjectGroup = homeworkSubjects.find((group) => group.title === store.selectedHomeworkGroup) || homeworkSubjects[0];
  const activeTasks = homeworkTasks.filter((task) => !task.isCompleted);
  const completedTasks = homeworkTasks.filter((task) => task.isCompleted);

  return `
    <div class="content-grid">
      <section class="stack">
        <section class="module panel">
          <div class="module-head">
            <div>
              <p class="eyebrow">HOMEWORK_COMPOSER</p>
              <h2 class="module-title">Добавить домашку</h2>
            </div>
            <span class="tag accent">${homeworkSubjects.length} предметов</span>
          </div>
          ${homeworkSubjects.length === 0
            ? emptyState("Сначала выбери расписание во вкладке Расписание. Тогда mini app подтянет предметы и ближайшие дедлайны.")
            : `
              <form id="homework-form" class="stack">
                <div class="field">
                  <label for="homework-group">Базовый предмет</label>
                  <select id="homework-group" name="subjectTitle">
                    ${homeworkSubjects.map(group => `
                      <option value="${escapeHtml(group.title)}" ${subjectGroup?.title === group.title ? "selected" : ""}>
                        ${escapeHtml(group.title)}${group.favoriteOrder ? ` // ${group.favoriteOrder}` : ""}
                      </option>
                    `).join("")}
                  </select>
                </div>
                <div class="field">
                  <label for="homework-subject">Тип занятия</label>
                  <select id="homework-subject" name="subject">
                    ${(subjectGroup?.options || []).map(option => `
                      <option value="${escapeHtml(option.subject)}">
                        ${escapeHtml(option.lessonType)}${option.nextDeadlineText ? ` // дедлайн ${escapeHtml(option.nextDeadlineText)}` : ""}
                      </option>
                    `).join("")}
                  </select>
                </div>
                <div class="field">
                  <label for="homework-title">Что задали</label>
                  <textarea id="homework-title" name="title" placeholder="Например: решить варианты 3-6 и подготовить конспект"></textarea>
                </div>
                <button class="pixel-button" type="submit">Добавить ДЗ</button>
              </form>
            `}
        </section>
        <section class="module panel">
          <div class="module-head">
            <div>
              <p class="eyebrow">PRIORITY_FILTER</p>
              <h2 class="module-title">Избранные предметы</h2>
            </div>
          </div>
          ${homeworkSubjects.length > 0 ? homeworkSubjects.map(group => `
            <article class="subject-card">
              <div class="subject-top">
                <div>
                  <h3 class="subject-title">${escapeHtml(group.title)}</h3>
                  <div class="subject-meta">
                    <span class="tag">${group.options.length} типов занятий</span>
                    ${group.favoriteOrder ? `<span class="tag success">позиция ${group.favoriteOrder}</span>` : `<span class="tag warning">не в избранном</span>`}
                  </div>
                </div>
                <button class="subject-toggle ${group.isFavorite ? "active" : ""}" data-action="toggle-favorite" data-subject-title="${escapeHtml(group.title)}">
                  ${group.isFavorite ? "★" : "☆"}
                </button>
              </div>
            </article>
          `).join("") : emptyState("Избранные появятся после выбора расписания.")}
        </section>
      </section>
      <section class="stack">
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">Активные ДЗ</h2>
            <span class="tag accent">${activeTasks.length}</span>
          </div>
          ${activeTasks.length > 0 ? activeTasks.map(task => taskCard(task, "homework")).join("") : emptyState("Здесь будет список актуальных домашних заданий.")}
        </section>
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">Выполненные</h2>
            <span class="tag">${completedTasks.length}</span>
          </div>
          ${completedTasks.length > 0 ? completedTasks.map(task => taskCard(task, "homework")).join("") : emptyState("Пока без выполненных задач.")}
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
            <h2 class="module-title">Добавить личное дело</h2>
          </div>
        </div>
        <form id="plan-form" class="stack">
          <div class="field">
            <label for="plan-title">Название</label>
            <input id="plan-title" name="title" placeholder="Например: записаться к врачу">
          </div>
          <div class="two-column">
            <div class="field">
              <label for="plan-date">Дата</label>
              <input id="plan-date" name="date" type="date">
            </div>
            <div class="field">
              <label for="plan-time">Время</label>
              <input id="plan-time" name="time" type="time">
            </div>
          </div>
          <div class="actions-row">
            <button class="nav-chip" type="button" data-action="plan-date" data-offset="0">Сегодня</button>
            <button class="nav-chip" type="button" data-action="plan-date" data-offset="1">Завтра</button>
            <button class="nav-chip" type="button" data-action="plan-date" data-offset="2">Послезавтра</button>
          </div>
          <button class="pixel-button" type="submit">Добавить дело</button>
        </form>
      </section>
      <section class="stack">
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">Активные дела</h2>
            <span class="tag accent">${activeTasks.length}</span>
          </div>
          ${activeTasks.length > 0 ? activeTasks.map(task => taskCard(task, "personal")).join("") : emptyState("Здесь можно держать всё личное: звонки, встречи, покупки, дедлайны вне учёбы.")}
        </section>
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">Архив</h2>
            <span class="tag">${completedTasks.length}</span>
          </div>
          ${completedTasks.length > 0 ? completedTasks.map(task => taskCard(task, "personal")).join("") : emptyState("Выполненные личные дела появятся здесь.")}
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
        <div class="divider"></div>
        <div class="stack">
          <div>
            <p class="eyebrow">WORK_PRESETS</p>
            <div class="actions-row">
              ${[25, 30, 45, 60].map(minutes => `<button class="pixel-button secondary" data-action="start-timer" data-type="work" data-minutes="${minutes}">${minutes} мин</button>`).join("")}
            </div>
          </div>
          <form id="custom-work-form" class="actions-row">
            <input name="minutes" type="number" min="1" max="300" placeholder="своё время">
            <button class="pixel-button" type="submit">Старт учёбы</button>
          </form>
          <div>
            <p class="eyebrow">REST_PRESETS</p>
            <div class="actions-row">
              ${[5, 15, 30].map(minutes => `<button class="pixel-button secondary" data-action="start-timer" data-type="rest" data-minutes="${minutes}">${minutes} мин</button>`).join("")}
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
          <div class="field">
            <label for="reminders-time">Время по МСК</label>
            <input id="reminders-time" name="time" type="time" value="${escapeHtml(reminder.timeText)}">
          </div>
          <button class="pixel-button" type="submit">Сохранить напоминания</button>
        </form>
        <div class="divider"></div>
        <p class="muted">Чат и miniapp используют одни и те же настройки, поэтому изменения сразу синхронизируются между интерфейсами.</p>
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
          <div class="field">
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
      <span class="tabbar-label">${escapeHtml(meta.label)}</span>
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
            ${task.deadlineText ? `<span class="tag ${task.isCompleted ? "" : "warning"}">${escapeHtml(task.deadlineText)}</span>` : `<span class="tag">без дедлайна</span>`}
          </div>
        </div>
        <span class="tag ${task.isCompleted ? "success" : "accent"}">${task.isCompleted ? "done" : "active"}</span>
      </div>
      <div class="task-actions">
        <button class="pixel-button secondary slim" data-action="toggle-task" data-scope="${scope}" data-task-id="${escapeHtml(task.id)}" data-completed="${String(!task.isCompleted)}">
          ${task.isCompleted ? "Вернуть" : "Выполнено"}
        </button>
        <button class="pixel-button ghost slim" data-action="delete-task" data-task-id="${escapeHtml(task.id)}">Удалить</button>
      </div>
    </article>
  `;
}

function emptyState(message) {
  return `<div class="section-empty"><strong>Пусто.</strong><br>${escapeHtml(message)}</div>`;
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
      toast("Список избранных обновлён.");
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
      toast(isCompleted ? "Задача отмечена выполненной." : "Задача возвращена в активные.");
      refreshAfterMutation();
    });
    return;
  }

  if (target.dataset.action === "delete-task") {
    const taskId = target.dataset.taskId;
    await runAction(async () => {
      store.state = await api(`/api/miniapp/tasks/${taskId}`, { method: "DELETE" });
      toast("Задача удалена.");
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
      toast(type === "rest" ? "Таймер отдыха запущен." : "Рабочий таймер запущен.");
      refreshAfterMutation();
    });
    return;
  }

  if (target.dataset.action === "stop-timer") {
    await runAction(async () => {
      store.state = await api("/api/miniapp/timers/stop", { method: "POST" });
      toast("Таймер остановлен.");
      refreshAfterMutation();
    });
    return;
  }

  if (target.dataset.action === "clear-schedule") {
    await runAction(async () => {
      store.state = await api("/api/miniapp/schedule", { method: "DELETE" });
      toast("Привязка расписания удалена.");
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
      toast("Расписание сохранено.");
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
      toast("Домашнее задание добавлено.");
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
      toast("Личное дело добавлено.");
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
      toast("Напоминания сохранены.");
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
      toast(type === "rest" ? "Перерыв запущен." : "Рабочий таймер запущен.");
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

    if (store.selectedHomeworkGroup &&
        !store.state.homeworkSubjects.some((group) => group.title === store.selectedHomeworkGroup)) {
      store.selectedHomeworkGroup = store.state.homeworkSubjects[0]?.title || "";
    }
  }

  store.lastSyncLabel = `Синхронизировано ${new Date().toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit", second: "2-digit" })}`;
  render();
  restartTimerTicker();
}

function restartTimerTicker() {
  window.clearInterval(store.timerTick);
  if (!store.state?.timer?.isActive) {
    return;
  }

  store.timerTick = window.setInterval(() => {
    const activeClock = document.querySelector(".focus-clock");
    if (activeClock instanceof HTMLElement) {
      activeClock.textContent = timerText(store.state.timer);
    }
  }, 1000);
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
    await action();
  } catch (error) {
    toast(error.message || "Что-то пошло не так.");
  }
}

function handleFatalError(error) {
  store.root.innerHTML = `
    <section class="boot-card panel">
      <p class="eyebrow">BOOT_FAILED</p>
      <h1>Mini App недоступен</h1>
      <p class="boot-copy">${escapeHtml(error.message || "Не удалось загрузить данные.")}</p>
      <p class="muted">Если открываешь mini app не из Telegram, добавь <code>?devUserId=...</code> и включи <code>MiniApp:AllowDebugAuth</code>.</p>
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
