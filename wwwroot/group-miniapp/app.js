const tg = window.Telegram?.WebApp;

const store = {
  root: document.getElementById("app"),
  toast: document.getElementById("toast"),
  initData: readTelegramInitData(),
  debugUserId: new URLSearchParams(window.location.search).get("devUserId") || "",
  launchContext: readLaunchContext(),
  state: null,
  activeView: "dashboard",
  selectedDirectionCode: "",
  groups: [],
  scheduleMode: "today",
  selectedSubject: "",
  lastSyncLabel: "Синхронизация..."
};

const VIEW_META = {
  dashboard: { label: "Обзор", shortLabel: "Обзор", icon: "◫", eyebrow: "ГРУППА" },
  schedule: { label: "Расписание", shortLabel: "Пары", icon: "⌘", eyebrow: "РАСПИСАНИЕ" },
  homework: { label: "Домашка", shortLabel: "ДЗ", icon: "✦", eyebrow: "ОБЩЕЕ ДЗ" },
  reminders: { label: "Напоминания", shortLabel: "Напом.", icon: "◷", eyebrow: "НАПОМИНАНИЯ" }
};

boot().catch(handleFatalError);

async function boot() {
  tg?.ready();
  tg?.expand();

  if (!store.launchContext) {
    throw new Error("Не удалось определить контекст группы для запуска mini app.");
  }

  await refreshState();

  document.addEventListener("click", handleClick);
  document.addEventListener("submit", handleSubmit);
  document.addEventListener("change", handleChange);
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

  headers.set("X-Group-Chat-Id", String(store.launchContext.chatId));
  headers.set("X-Group-Token", store.launchContext.groupToken);

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

async function refreshState() {
  store.state = await api("/api/group-miniapp/state");
  store.lastSyncLabel = `Синхронизировано ${new Date().toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit" })}`;
  store.selectedDirectionCode = store.state.schedule.selectedDirectionCode
    || store.selectedDirectionCode
    || store.state.schedule.directions[0]?.directionCode
    || "";
  store.groups = store.state.schedule.availableGroups || [];

  if (!store.selectedSubject) {
    store.selectedSubject = store.state.homeworkSubjects[0]?.options[0]?.subject || "";
  }

  render();
}

function render() {
  if (!store.state) {
    return;
  }

  const { chat, stats, schedule, reminder, homework, homeworkSubjects } = store.state;
  const activeViewMeta = VIEW_META[store.activeView];
  const selectedGroup = store.groups.find((group) => group.scheduleId === (schedule.selection?.scheduleId || "")) || store.groups[0];
  const selectedSubgroup = schedule.selection?.subGroup ?? selectedGroup?.subGroups?.[0] ?? "";
  const activeHomework = homework.filter((task) => !task.isCompleted);

  store.root.innerHTML = `
    <section class="app-frame">
      <section class="topbar panel">
        <div class="topbar-main">
          <div class="identity">
            <div class="avatar">${getInitials(chat.title)}</div>
            <div class="topbar-copy">
              <p class="eyebrow">GROUP MINI APP</p>
              <h1>${escapeHtml(chat.title)}</h1>
              <p class="muted">открыто: ${escapeHtml(chat.openedBy)} // ${escapeHtml(store.lastSyncLabel)}</p>
            </div>
          </div>
          <div class="topbar-actions">
            <button class="pixel-button secondary slim" data-action="refresh">Обновить</button>
          </div>
        </div>
        <div class="status-strip">
          ${statusActionButton("schedule", stats.hasSchedule ? "Расписание подключено" : "Нужно выбрать расписание", "accent")}
          ${statusActionButton("homework", `${stats.homeworkPending} активных ДЗ`, stats.homeworkPending > 0 ? "success" : "warning")}
          ${statusActionButton("reminders", reminder.isEnabled ? `Напоминания ${escapeHtml(reminder.timeText)}` : "Напоминания выключены", reminder.isEnabled ? "success" : "warning")}
        </div>
        <div class="hero-stats">
          ${heroStat("Активные ДЗ", stats.homeworkPending, "для всей группы")}
          ${heroStat("Выполнено", stats.homeworkCompleted, "в архиве")}
          ${heroStat("Неделя", schedule.currentWeekType, schedule.currentWeekLabel)}
        </div>
      </section>
      <section class="screen-shell">
        <div class="screen-meta">
          <div>
            <p class="eyebrow">${escapeHtml(activeViewMeta.eyebrow)}</p>
            <h2 class="screen-title">${escapeHtml(activeViewMeta.label)}</h2>
          </div>
          <div class="screen-badge">${activeViewMeta.icon}</div>
        </div>
        <section class="view-body">
          ${renderView({ schedule, reminder, homework, homeworkSubjects, selectedGroup, selectedSubgroup, activeHomework })}
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
      return renderScheduleView(context.schedule, context.selectedGroup, context.selectedSubgroup);
    case "homework":
      return renderHomeworkView(context.homeworkSubjects, context.homework);
    case "reminders":
      return renderRemindersView(context.reminder);
    default:
      return renderDashboardView(context.schedule, context.reminder, context.activeHomework);
  }
}

function renderDashboardView(schedule, reminder, activeHomework) {
  const entries = store.scheduleMode === "today" ? schedule.todayEntries : schedule.weekEntries;
  const grouped = groupScheduleEntries(entries);

  return `
    <div class="content-grid">
      <section class="module panel">
        <div class="module-head">
          <div>
            <p class="eyebrow">СВОДКА</p>
            <h2 class="module-title">Что происходит в группе</h2>
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
        `).join("") : emptyState("Выбери расписание группы, чтобы здесь появились пары.")}
      </section>
      <section class="stack">
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">Ближайшие ДЗ</h2>
            <span class="tag accent">${activeHomework.length} активных</span>
          </div>
          ${activeHomework.length > 0
            ? activeHomework.slice(0, 5).map(taskCard).join("")
            : emptyState("Пока ничего не добавлено.")}
        </section>
        <section class="module panel">
          <div class="module-head">
            <h2 class="module-title">Напоминания</h2>
            <span class="tag ${reminder.isEnabled ? "success" : "warning"}">${reminder.isEnabled ? reminder.timeText : "выкл"}</span>
          </div>
          <p class="group-caption">
            ${reminder.isEnabled
              ? `Бот будет писать в группу ${escapeHtml(reminder.frequencyText)} в ${escapeHtml(reminder.timeText)} по МСК и отмечать участников, которых уже видел в чате.`
              : "Групповые напоминания пока выключены."}
          </p>
        </section>
      </section>
    </div>
  `;
}

function renderScheduleView(schedule, selectedGroup, selectedSubgroup) {
  return `
    <div class="content-grid">
      <section class="module panel">
        <div class="module-head">
          <div>
            <p class="eyebrow">ВЫБОР</p>
            <h2 class="module-title">Расписание группы</h2>
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
      </section>
      <section class="module panel">
        <div class="module-head">
          <div>
            <p class="eyebrow">ТЕКУЩЕЕ</p>
            <h2 class="module-title">Текущая привязка</h2>
          </div>
        </div>
        ${schedule.selection ? `
          <article class="schedule-card">
            <h3 class="schedule-day-title">${escapeHtml(schedule.selection.title)}</h3>
            <div class="schedule-meta">
              <span class="tag accent">${escapeHtml(schedule.currentWeekLabel)}</span>
              <span class="tag">${schedule.selection.subGroup ? `подгруппа ${schedule.selection.subGroup}` : "без подгруппы"}</span>
              <span class="tag">${escapeHtml(schedule.semester)}</span>
            </div>
          </article>
        ` : emptyState("Пока ничего не выбрано.")}
      </section>
    </div>
  `;
}

function renderHomeworkView(homeworkSubjects, homework) {
  const hasSubjects = homeworkSubjects.length > 0;
  const selectedOptions = homeworkSubjects.flatMap(group => group.options);
  const selectedValue = selectedOptions.some(option => option.subject === store.selectedSubject)
    ? store.selectedSubject
    : (selectedOptions[0]?.subject || "");

  return `
    <div class="group-homework-grid">
      <section class="module panel">
        <div class="module-head">
          <div>
            <p class="eyebrow">ДОБАВИТЬ</p>
            <h2 class="module-title">Новое общее ДЗ</h2>
          </div>
        </div>
        ${hasSubjects ? `
          <form id="homework-form" class="group-homework-form">
            <div class="field">
              <label for="homework-subject">Предмет</label>
              <select id="homework-subject" name="subject">
                ${homeworkSubjects.map(group => `
                  <optgroup label="${escapeHtml(group.title)}">
                    ${group.options.map(option => `
                      <option value="${escapeHtml(option.subject)}" ${selectedValue === option.subject ? "selected" : ""}>
                        ${escapeHtml(option.lessonType)}${option.nextDeadlineText ? ` — ${escapeHtml(option.nextDeadlineText)}` : ""}
                      </option>
                    `).join("")}
                  </optgroup>
                `).join("")}
              </select>
            </div>
            <div class="field">
              <label for="homework-title">Текст ДЗ</label>
              <textarea id="homework-title" name="title" placeholder="Например: решить №5-12 и подготовить конспект"></textarea>
            </div>
            <button class="pixel-button" type="submit">Добавить ДЗ</button>
          </form>
        ` : emptyState("Сначала подключи расписание группы, тогда здесь появятся предметы.")}
      </section>
      <section class="module panel">
        <div class="module-head">
          <div>
            <p class="eyebrow">СПИСОК</p>
            <h2 class="module-title">Текущие задания</h2>
          </div>
          <span class="tag accent">${homework.filter(task => !task.isCompleted).length} активных</span>
        </div>
        ${homework.length > 0
          ? homework.map(taskCard).join("")
          : emptyState("Общий список пока пуст.")}
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
            <p class="eyebrow">НАПОМИНАНИЯ</p>
            <h2 class="module-title">Настройки группы</h2>
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
            <label for="reminders-frequency">Как часто</label>
            <select id="reminders-frequency" name="frequency">
              <option value="daily" ${reminder.frequency === "daily" ? "selected" : ""}>Каждый день</option>
              <option value="weekdays" ${reminder.frequency === "weekdays" ? "selected" : ""}>По будням</option>
            </select>
          </div>
          <div class="field time-field native-input-field">
            <label for="reminders-time">Время по МСК</label>
            <input id="reminders-time" name="time" type="time" value="${escapeHtml(reminder.timeText)}">
          </div>
          <button class="pixel-button" type="submit">Сохранить напоминания</button>
        </form>
      </section>
    </div>
  `;
}

function taskCard(task) {
  return `
    <article class="task-card ${task.isCompleted ? "completed" : ""}">
      <div class="task-top">
        <div>
          <h3 class="task-title">${escapeHtml(task.title)}</h3>
          <div class="task-meta">
            <span class="tag accent">${escapeHtml(task.subjectTitle)}</span>
            ${task.deadlineText ? `<span class="tag warning">${escapeHtml(task.deadlineText)}</span>` : `<span class="tag">без дедлайна</span>`}
          </div>
          ${task.createdByName ? `<div class="group-task-author">Добавил${endsWithA(task.createdByName) ? "а" : ""}: ${escapeHtml(task.createdByName)}</div>` : ""}
        </div>
        <button class="pixel-button ghost slim" data-action="delete-task" data-task-id="${escapeHtml(task.id)}">Удалить</button>
      </div>
    </article>
  `;
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

  if (target.dataset.action === "refresh") {
    await runAction(async () => {
      await refreshState();
      toast("Данные обновлены.");
    });
    return;
  }

  if (target.dataset.action === "schedule-mode") {
    store.scheduleMode = target.dataset.mode || "today";
    render();
    return;
  }

  if (target.dataset.action === "clear-schedule") {
    await runAction(async () => {
      store.state = await api("/api/group-miniapp/schedule", { method: "DELETE" });
      toast("Расписание группы удалено.");
      afterMutation();
    });
    return;
  }

  if (target.dataset.action === "delete-task") {
    const taskId = target.dataset.taskId;
    await runAction(async () => {
      store.state = await api(`/api/group-miniapp/homework/${taskId}`, { method: "DELETE" });
      toast("Общее ДЗ удалено.");
      afterMutation();
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
      store.state = await api("/api/group-miniapp/schedule", {
        method: "PUT",
        body: {
          scheduleId,
          subGroup: subgroupRaw ? Number(subgroupRaw) : null
        }
      });
      toast("Расписание группы сохранено.");
      afterMutation();
    });
    return;
  }

  if (form.id === "homework-form") {
    const formData = new FormData(form);
    const subject = String(formData.get("subject") || "");
    const title = String(formData.get("title") || "").trim();

    await runAction(async () => {
      store.state = await api("/api/group-miniapp/homework", {
        method: "POST",
        body: { subject, title }
      });
      form.reset();
      toast("Общее ДЗ добавлено.");
      afterMutation();
    });
    return;
  }

  if (form.id === "reminders-form") {
    const formData = new FormData(form);
    const isEnabled = String(formData.get("isEnabled")) === "true";
    const frequency = String(formData.get("frequency") || "daily");
    const time = String(formData.get("time") || "20:00");
    const [hour, minute] = time.split(":").map(Number);

    await runAction(async () => {
      store.state = await api("/api/group-miniapp/reminders", {
        method: "PUT",
        body: { isEnabled, frequency, hour, minute }
      });
      toast("Напоминания сохранены.");
      afterMutation();
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
      store.groups = await api(`/api/group-miniapp/groups?directionCode=${encodeURIComponent(target.value)}`);
      render();
    });
    return;
  }

  if (target.id === "homework-subject") {
    store.selectedSubject = target.value;
  }
}

function afterMutation() {
  store.selectedDirectionCode = store.state.schedule.selectedDirectionCode
    || store.selectedDirectionCode
    || store.state.schedule.directions[0]?.directionCode
    || "";
  store.groups = store.state.schedule.availableGroups || [];
  store.lastSyncLabel = `Синхронизировано ${new Date().toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit" })}`;
  render();
}

function statusActionButton(view, label, tone) {
  return `
    <button class="status-action ${tone}" data-view="${view}">
      <span class="status-action-title">${escapeHtml(label)}</span>
    </button>
  `;
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

function tabButton(view, meta) {
  return `
    <button class="tabbar-button ${store.activeView === view ? "active" : ""}" data-view="${view}">
      <span class="tabbar-icon">${escapeHtml(meta.icon)}</span>
      <span class="tabbar-label">${escapeHtml(meta.shortLabel || meta.label)}</span>
    </button>
  `;
}

function emptyState(message) {
  return `<div class="section-empty"><strong>Пусто.</strong><br>${escapeHtml(message)}</div>`;
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
  return String(name || "Группа")
    .split(" ")
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() || "")
    .join("");
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
      <p class="eyebrow">ОШИБКА</p>
      <h1>Group Mini App недоступен</h1>
      <p class="boot-copy">${escapeHtml(error.message || "Не удалось загрузить данные.")}</p>
    </section>
  `;
}

function toast(message) {
  store.toast.hidden = false;
  store.toast.textContent = message;
  window.clearTimeout(store.toastTimer);
  store.toastTimer = window.setTimeout(() => {
    store.toast.hidden = true;
  }, 2400);
}

function readTelegramInitData() {
  if (tg?.initData) {
    return tg.initData;
  }

  const queryInitData = new URLSearchParams(window.location.search).get("initData");
  if (queryInitData) {
    return queryInitData;
  }

  const hash = window.location.hash.startsWith("#")
    ? window.location.hash.slice(1)
    : window.location.hash;

  if (!hash) {
    return "";
  }

  const hashParams = new URLSearchParams(hash);
  return hashParams.get("tgWebAppData") || "";
}

function readLaunchContext() {
  const startParam = readStartParam();
  if (!startParam) {
    return null;
  }

  const match = /^chat-(-?\d+)-([0-9a-f]+)$/i.exec(startParam);
  if (!match) {
    return null;
  }

  return {
    chatId: Number(match[1]),
    groupToken: match[2]
  };
}

function readStartParam() {
  const searchParams = new URLSearchParams(window.location.search);
  const directValue = searchParams.get("tgWebAppStartParam") || searchParams.get("startapp");
  if (directValue) {
    return directValue;
  }

  if (store.initData) {
    const initParams = new URLSearchParams(store.initData);
    const initStartParam = initParams.get("start_param");
    if (initStartParam) {
      return initStartParam;
    }
  }

  return "";
}

function endsWithA(value) {
  return /а$/i.test(String(value || "").trim());
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}
