(() => {
    "use strict";

    const MAX_PHOTO_SIZE = 8 * 1024 * 1024;
    const ACTIVE_SESSION_STORAGE_KEY = "nutriflow.activeMealSessionId";
    const PENDING_DRAFT_STORAGE_KEY = "nutriflow.pendingMealDraft";
    const ALLOWED_PHOTO_TYPES = new Set([
        "image/jpeg",
        "image/png",
        "image/webp"
    ]);
    const DEMO_MESSAGES = [
        "Добавил 200 г демо-продукта A.",
        "Потом добавил 100 г демо-продукта B.",
        "Готовое блюдо весит 250 г.",
        "Съел 125 г, потом ещё две порции по 62,5 г."
    ];
    const NUTRITION_METRICS = [
        { key: "calories", label: "Калории", unit: "ккал" },
        { key: "proteinGrams", label: "Белки", unit: "г" },
        { key: "fatGrams", label: "Жиры", unit: "г" },
        { key: "carbohydratesGrams", label: "Углеводы", unit: "г" }
    ];

    const elements = {
        mealDate: document.querySelector("#meal-date"),
        dateControl: document.querySelector("#date-control"),
        dateControlLabel: document.querySelector("#date-control-label"),
        apiStatus: document.querySelector("#api-status"),
        globalError: document.querySelector("#global-error"),
        globalErrorText: document.querySelector("#global-error-text"),
        dismissErrorButton: document.querySelector("#dismiss-error-button"),
        capabilitiesBanner: document.querySelector("#capabilities-banner"),
        capabilitiesTitle: document.querySelector("#capabilities-title"),
        capabilitiesText: document.querySelector("#capabilities-text"),
        dailyCaption: document.querySelector("#daily-caption"),
        dailyMetrics: document.querySelector("#daily-metrics"),
        dailyEntries: document.querySelector("#daily-entries"),
        entriesCount: document.querySelector("#entries-count"),
        editGoalButton: document.querySelector("#edit-goal-button"),
        cancelGoalButton: document.querySelector("#cancel-goal-button"),
        goalForm: document.querySelector("#goal-form"),
        messageList: document.querySelector("#message-list"),
        messageForm: document.querySelector("#message-form"),
        messageInput: document.querySelector("#message-input"),
        addMessageButton: document.querySelector("#add-message-button"),
        exampleButton: document.querySelector("#example-button"),
        buildDraftButton: document.querySelector("#build-draft-button"),
        newSessionButton: document.querySelector("#new-session-button"),
        captureHint: document.querySelector("#capture-hint"),
        sessionStatus: document.querySelector("#session-status"),
        previewContent: document.querySelector("#preview-content"),
        confirmBar: document.querySelector("#confirm-bar"),
        confirmTitle: document.querySelector("#confirm-title"),
        confirmHint: document.querySelector("#confirm-hint"),
        confirmButton: document.querySelector("#confirm-button"),
        openCatalogButton: document.querySelector("#open-catalog-button"),
        catalogDialog: document.querySelector("#catalog-dialog"),
        catalogContext: document.querySelector("#catalog-context"),
        closeCatalogButton: document.querySelector("#close-catalog-button"),
        labelTab: document.querySelector("#catalog-tab-label"),
        labelTabNote: document.querySelector("#label-tab-note"),
        barcodeForm: document.querySelector("#barcode-form"),
        barcodeResult: document.querySelector("#barcode-result"),
        labelForm: document.querySelector("#label-form"),
        labelPhoto: document.querySelector("#label-photo"),
        photoDropzone: document.querySelector("#photo-dropzone"),
        labelUnavailable: document.querySelector("#label-unavailable"),
        labelUnavailableText: document.querySelector("#label-unavailable-text"),
        labelResult: document.querySelector("#label-result"),
        manualProductForm: document.querySelector("#manual-product-form"),
        manualResult: document.querySelector("#manual-result"),
        toast: document.querySelector("#toast")
    };

    const state = {
        date: getLocalIsoDate(),
        capabilities: null,
        capabilitiesLoading: true,
        daily: null,
        dailyLoading: true,
        dailyRequestSequence: 0,
        dailyAbortController: null,
        localMessages: [],
        session: null,
        sessionBusy: false,
        catalogIngredientName: null,
        labelDraft: null,
        labelFile: null,
        toastTimer: null
    };

    initialize();

    function initialize() {
        restorePendingDraft();
        elements.mealDate.value = state.date;
        bindEvents();
        renderMessages();
        renderPreview();
        renderDaily();
        renderCapabilities();
        void loadCapabilities();
        void restoreSessionAndDailyProgress();
    }

    async function restoreSessionAndDailyProgress() {
        await restoreCurrentSession();
        await loadDailyProgress();
    }

    async function loadCapabilities() {
        try {
            const capabilities = await apiRequest("/api/capabilities");
            state.capabilities = {
                aiProvider: String(capabilities?.aiProvider ?? "Unknown"),
                supportsFreeText: capabilities?.supportsFreeText === true,
                supportsLabelPhotos: capabilities?.supportsLabelPhotos === true
            };
        } catch {
            state.capabilities = null;
        } finally {
            state.capabilitiesLoading = false;
            renderCapabilities();
        }
    }

    function renderCapabilities() {
        const capabilities = state.capabilities;
        const supportsLabelPhotos = capabilities?.supportsLabelPhotos === true;
        const labelSubmitButton = elements.labelForm.querySelector("button[type='submit']");

        elements.labelTab.disabled = !supportsLabelPhotos;
        elements.labelTab.setAttribute("aria-disabled", String(!supportsLabelPhotos));
        elements.labelTabNote.hidden = supportsLabelPhotos;
        elements.labelTabNote.textContent = state.capabilitiesLoading ? "проверяем" : "недоступно";
        elements.labelPhoto.disabled = !supportsLabelPhotos;
        labelSubmitButton.disabled = !supportsLabelPhotos;
        elements.photoDropzone.classList.toggle("is-disabled", !supportsLabelPhotos);
        elements.photoDropzone.setAttribute("aria-disabled", String(!supportsLabelPhotos));
        elements.labelUnavailable.hidden = supportsLabelPhotos;

        if (state.capabilitiesLoading) {
            elements.capabilitiesBanner.hidden = true;
            elements.labelUnavailableText.textContent = "Возможности сервера ещё проверяются.";
            return;
        }

        if (!capabilities) {
            elements.capabilitiesTitle.textContent = "Возможности сервера не определены";
            elements.capabilitiesText.textContent = "Основной интерфейс доступен, но обработка свободного текста и фотографий может быть ограничена.";
            elements.capabilitiesBanner.hidden = false;
            elements.labelUnavailableText.textContent = "Сервер не сообщил, доступно ли распознавание фотографий.";
            return;
        }

        const limitations = [];

        if (!capabilities.supportsFreeText) {
            limitations.push("обработчик сообщений работает только с демонстрационным сценарием");
        }

        if (!capabilities.supportsLabelPhotos) {
            limitations.push("распознавание фотографий этикеток отключено");
        }

        if (limitations.length === 0) {
            elements.capabilitiesBanner.hidden = true;
            return;
        }

        const isDemoProvider = capabilities.aiProvider.toLowerCase() === "fake";
        elements.capabilitiesTitle.textContent = isDemoProvider
            ? "Демонстрационный режим"
            : "Ограниченные возможности";
        elements.capabilitiesText.textContent = `${limitations.join("; ")}. Штрихкод и ручной ввод продуктов остаются доступны.`;
        elements.capabilitiesBanner.hidden = false;
        elements.labelUnavailableText.textContent = isDemoProvider
            ? "В демонстрационном режиме фотографии не отправляются на распознавание."
            : "Текущая конфигурация сервера не поддерживает распознавание фотографий.";
    }

    async function restoreCurrentSession() {
        const sessionId = readActiveSessionId();

        if (!sessionId) {
            return;
        }

        state.sessionBusy = true;
        renderMessages();

        try {
            const session = await apiRequest(`/api/meal-sessions/${encodeURIComponent(sessionId)}`);

            if (isSessionConfirmed(session)) {
                forgetActiveSession();

                if (state.localMessages.length > 0) {
                    return;
                }
            }

            state.session = session;
            state.localMessages = [];

            if (!isSessionConfirmed(session)) {
                state.date = session.mealDate ?? state.date;
                rememberActiveSession(session.id);
                clearPendingDraft();
            }

            elements.mealDate.value = state.date;
        } catch (error) {
            if (error.status === 400 || error.status === 404) {
                forgetActiveSession();
            } else {
                showGlobalError(error);
            }
        } finally {
            state.sessionBusy = false;
            renderMessages();
            renderPreview();
        }
    }

    function readActiveSessionId() {
        try {
            return window.localStorage.getItem(ACTIVE_SESSION_STORAGE_KEY);
        } catch {
            return null;
        }
    }

    function rememberActiveSession(sessionId) {
        if (!sessionId) {
            return;
        }

        try {
            window.localStorage.setItem(ACTIVE_SESSION_STORAGE_KEY, sessionId);
        } catch {
            // private browsing can make local storage unavailable
        }
    }

    function forgetActiveSession() {
        try {
            window.localStorage.removeItem(ACTIVE_SESSION_STORAGE_KEY);
        } catch {
            // private browsing can make local storage unavailable
        }
    }

    function restorePendingDraft() {
        let storedDraft;

        try {
            storedDraft = JSON.parse(
                window.localStorage.getItem(PENDING_DRAFT_STORAGE_KEY) ?? "null"
            );
        } catch {
            clearPendingDraft();
            return;
        }

        if (!isValidPendingDraft(storedDraft)) {
            clearPendingDraft();
            return;
        }

        state.localMessages = [...storedDraft.messages];
        state.date = storedDraft.date;
    }

    function savePendingDraft() {
        if (state.session || state.localMessages.length === 0) {
            clearPendingDraft();
            return;
        }

        try {
            window.localStorage.setItem(
                PENDING_DRAFT_STORAGE_KEY,
                JSON.stringify({
                    date: state.date,
                    messages: state.localMessages
                })
            );
        } catch {
            // private browsing can make local storage unavailable
        }
    }

    function clearPendingDraft() {
        try {
            window.localStorage.removeItem(PENDING_DRAFT_STORAGE_KEY);
        } catch {
            // private browsing can make local storage unavailable
        }
    }

    function isValidPendingDraft(value) {
        if (!value ||
            typeof value.date !== "string" ||
            !isValidIsoDate(value.date) ||
            !Array.isArray(value.messages) ||
            value.messages.length === 0 ||
            value.messages.length > 50) {
            return false;
        }

        let totalLength = 0;

        for (const message of value.messages) {
            if (typeof message !== "string" ||
                !message.trim() ||
                message.length > 4000) {
                return false;
            }

            totalLength += message.length;
        }

        return totalLength <= 20000;
    }

    function bindEvents() {
        elements.mealDate.addEventListener("change", () => {
            if (state.session && !isSessionConfirmed(state.session)) {
                state.date = state.session.mealDate ?? state.date;
                elements.mealDate.value = state.date;
                showToast("Дата закреплена за текущей неподтверждённой сессией.");
                return;
            }

            if (!elements.mealDate.value) {
                elements.mealDate.value = state.date;
                return;
            }

            state.date = elements.mealDate.value;
            savePendingDraft();
            hideGlobalError();
            loadDailyProgress();
        });

        elements.dismissErrorButton.addEventListener("click", hideGlobalError);
        elements.editGoalButton.addEventListener("click", showGoalForm);
        elements.cancelGoalButton.addEventListener("click", hideGoalForm);
        elements.goalForm.addEventListener("submit", saveDailyGoal);
        elements.dailyMetrics.addEventListener("click", event => {
            if (event.target.closest("[data-edit-goal]")) {
                showGoalForm();
            }
        });

        elements.messageForm.addEventListener("submit", addMessage);
        elements.exampleButton.addEventListener("click", fillExample);
        elements.buildDraftButton.addEventListener("click", createMealSession);
        elements.newSessionButton.addEventListener("click", startNewSession);
        elements.confirmButton.addEventListener("click", confirmSession);
        elements.previewContent.addEventListener("click", event => {
            const productButton = event.target.closest("[data-resolve-product]");

            if (productButton) {
                openCatalog("manual", productButton.dataset.resolveProduct);
            }
        });

        elements.openCatalogButton.addEventListener("click", () => openCatalog("barcode"));
        elements.closeCatalogButton.addEventListener("click", closeCatalog);
        elements.catalogDialog.addEventListener("click", event => {
            if (event.target === elements.catalogDialog) {
                closeCatalog();
            }
        });
        elements.catalogDialog.addEventListener("cancel", event => {
            event.preventDefault();
            closeCatalog();
        });

        document.querySelectorAll("[data-tab]").forEach(tab => {
            tab.addEventListener("click", () => activateCatalogTab(tab.dataset.tab));
            tab.addEventListener("keydown", handleCatalogTabKeydown);
        });

        elements.barcodeForm.addEventListener("submit", findProductByBarcode);
        elements.manualProductForm.addEventListener("submit", saveManualProduct);
        elements.labelForm.addEventListener("submit", analyzeLabel);
        elements.labelResult.addEventListener("submit", event => {
            if (event.target.matches("#label-product-form")) {
                saveLabelProduct(event);
            }
        });
        elements.labelPhoto.addEventListener("change", () => {
            setLabelFile(elements.labelPhoto.files?.[0] ?? null);
        });

        ["dragenter", "dragover"].forEach(eventName => {
            elements.photoDropzone.addEventListener(eventName, event => {
                event.preventDefault();

                if (state.capabilities?.supportsLabelPhotos !== true) {
                    return;
                }

                elements.photoDropzone.classList.add("is-dragging");
            });
        });

        ["dragleave", "drop"].forEach(eventName => {
            elements.photoDropzone.addEventListener(eventName, event => {
                event.preventDefault();
                elements.photoDropzone.classList.remove("is-dragging");
            });
        });

        elements.photoDropzone.addEventListener("drop", event => {
            if (state.capabilities?.supportsLabelPhotos !== true) {
                return;
            }

            setLabelFile(event.dataTransfer?.files?.[0] ?? null);
        });
    }

    async function apiRequest(url, options = {}) {
        const requestOptions = {
            method: options.method ?? "GET",
            credentials: "same-origin",
            signal: options.signal,
            headers: {
                Accept: "application/json",
                ...(options.headers ?? {})
            }
        };

        if (options.body instanceof FormData) {
            requestOptions.body = options.body;
        } else if (options.body !== undefined) {
            requestOptions.body = JSON.stringify(options.body);
            requestOptions.headers["Content-Type"] = "application/json";
        }

        let response;

        try {
            response = await fetch(url, requestOptions);
        } catch (error) {
            if (error?.name === "AbortError") {
                throw error;
            }

            setApiStatus(false);
            throw new Error("Нет соединения с NutriFlow API. Проверьте, запущено ли приложение.", { cause: error });
        }

        setApiStatus(true);

        const responseText = await response.text();
        let responseBody = null;

        if (responseText) {
            try {
                responseBody = JSON.parse(responseText);
            } catch {
                responseBody = { detail: responseText };
            }
        }

        if (!response.ok) {
            const error = new Error(getProblemMessage(responseBody, response.status));
            error.status = response.status;
            error.problem = responseBody;
            throw error;
        }

        return responseBody;
    }

    function getProblemMessage(problem, status) {
        const messages = [];

        if (problem?.detail) {
            messages.push(problem.detail);
        }

        if (problem?.message) {
            messages.push(problem.message);
        }

        if (problem?.errors && typeof problem.errors === "object") {
            Object.values(problem.errors).forEach(value => {
                const validationMessages = Array.isArray(value) ? value : [value];
                validationMessages.filter(Boolean).forEach(message => messages.push(String(message)));
            });
        }

        if (messages.length === 0 && problem?.title) {
            messages.push(problem.title);
        }

        if (messages.length === 0) {
            messages.push(`Сервер вернул ошибку ${status}.`);
        }

        return [...new Set(messages)].join(" ");
    }

    function setApiStatus(isOnline) {
        elements.apiStatus.classList.remove("is-checking", "is-online", "is-offline");
        elements.apiStatus.classList.add(isOnline ? "is-online" : "is-offline");
        elements.apiStatus.lastChild.textContent = isOnline ? " API доступен" : " API недоступен";
    }

    async function loadDailyProgress() {
        const requestedDate = state.date;
        const previousDaily = state.daily;
        const requestSequence = ++state.dailyRequestSequence;

        state.dailyAbortController?.abort();
        const abortController = new AbortController();
        state.dailyAbortController = abortController;
        state.dailyLoading = true;
        renderDaily();

        try {
            const daily = await apiRequest(`/api/daily-progress/${encodeURIComponent(requestedDate)}`, {
                signal: abortController.signal
            });

            if (requestSequence === state.dailyRequestSequence && requestedDate === state.date) {
                state.daily = daily;
            }
        } catch (error) {
            if (error?.name === "AbortError") {
                return;
            }

            if (requestSequence === state.dailyRequestSequence && requestedDate === state.date) {
                state.daily = String(previousDaily?.date ?? "") === requestedDate
                    ? previousDaily
                    : null;
                showGlobalError(error);
            }
        } finally {
            if (requestSequence === state.dailyRequestSequence) {
                state.dailyLoading = false;
                state.dailyAbortController = null;
                renderDaily();
            }
        }
    }

    function renderDaily() {
        elements.dailyCaption.textContent = state.dailyLoading
            ? "Загружаем цель и съеденные порции…"
            : `Баланс за ${formatLongDate(state.date)}`;

        if (state.dailyLoading) {
            elements.dailyMetrics.innerHTML = NUTRITION_METRICS.map(metric => `
                <article class="metric-card" aria-label="Загрузка показателя ${metric.label}">
                    <span class="metric-label">${metric.label}</span>
                    <div class="metric-values"><strong>—</strong><span>${metric.unit}</span></div>
                    <div class="metric-progress" role="progressbar"
                         aria-label="Загрузка показателя ${escapeHtml(metric.label)}"
                         aria-valuemin="0" aria-valuemax="100">
                        <span style="--progress: 0%"></span>
                    </div>
                </article>
            `).join("");
            elements.dailyEntries.innerHTML = "";
            elements.entriesCount.textContent = "0";
            return;
        }

        const daily = state.daily;
        const goal = daily?.goal ?? null;
        const consumed = daily?.consumed ?? emptyNutrition();
        const remaining = daily?.remaining ?? null;
        const exceeded = daily?.exceeded ?? null;
        const entries = Array.isArray(daily?.entries) ? daily.entries : [];

        if (!goal) {
            elements.dailyMetrics.innerHTML = `
                <div class="metric-placeholder">
                    <span>Установите дневную цель, чтобы видеть остаток и превышения по КБЖУ.</span>
                    <button class="button button-secondary" type="button" data-edit-goal>Установить цель</button>
                </div>
            `;
        } else {
            elements.dailyMetrics.innerHTML = NUTRITION_METRICS.map(metric => {
                const consumedValue = readNumber(consumed, metric.key);
                const targetValue = readNumber(goal, metric.key);
                const remainingValue = readNumber(remaining, metric.key);
                const exceededValue = readNumber(exceeded, metric.key);
                const progress = targetValue > 0
                    ? Math.max(0, Math.min((consumedValue / targetValue) * 100, 100))
                    : consumedValue > 0 ? 100 : 0;
                const isExceeded = exceededValue > 0;
                const note = isExceeded
                    ? `Превышено на ${formatNumber(exceededValue)} ${metric.unit}`
                    : `Осталось ${formatNumber(remainingValue)} ${metric.unit}`;

                return `
                    <article class="metric-card${isExceeded ? " is-exceeded" : ""}">
                        <div class="metric-topline">
                            <span class="metric-label">${metric.label}</span>
                            <span class="metric-note">${escapeHtml(note)}</span>
                        </div>
                        <div class="metric-values">
                            <strong>${formatNumber(consumedValue)}</strong>
                            <span>из ${formatNumber(targetValue)} ${metric.unit}</span>
                        </div>
                        <div class="metric-progress" role="progressbar"
                             aria-label="${escapeHtml(metric.label)}"
                             aria-valuemin="0" aria-valuemax="100" aria-valuenow="${Math.round(progress)}"
                             aria-valuetext="${escapeHtml(`${formatNumber(consumedValue)} из ${formatNumber(targetValue)} ${metric.unit}`)}">
                            <span style="--progress: ${progress.toFixed(2)}%"></span>
                        </div>
                    </article>
                `;
            }).join("");
        }

        elements.entriesCount.textContent = String(entries.length);
        elements.dailyEntries.innerHTML = entries.length === 0
            ? `<div class="message-empty">В этот день ещё нет подтверждённых порций.</div>`
            : entries.map(entry => `
                <article class="entry-row">
                    <div>
                        <strong>${escapeHtml(entry.name ?? "Приём пищи")}</strong>
                        <span class="entry-meta">${formatNumber(entry.weightInGrams)} г ${renderQualityBadge(entry.quality)}</span>
                    </div>
                    <div class="entry-nutrition">${formatNutritionText(entry.nutrition)}</div>
                </article>
            `).join("");
    }

    function showGoalForm() {
        const goal = state.daily?.goal;
        const defaults = goal ?? {
            calories: 2000,
            proteinGrams: 100,
            fatGrams: 70,
            carbohydratesGrams: 250
        };

        NUTRITION_METRICS.forEach(metric => {
            elements.goalForm.elements[metric.key].value = readNumber(defaults, metric.key);
        });

        elements.goalForm.hidden = false;
        elements.editGoalButton.hidden = true;
        elements.goalForm.elements.calories.focus();
    }

    function hideGoalForm() {
        elements.goalForm.hidden = true;
        elements.editGoalButton.hidden = false;
    }

    async function saveDailyGoal(event) {
        event.preventDefault();
        const goalDate = state.date;
        const submitButton = elements.goalForm.querySelector("button[type='submit']");
        const payload = readNutritionForm(elements.goalForm);

        await runButtonTask(submitButton, "Сохраняем…", async () => {
            try {
                const result = await apiRequest(`/api/daily-goals/${encodeURIComponent(goalDate)}`, {
                    method: "PUT",
                    body: payload
                });

                if (goalDate !== state.date) {
                    hideGoalForm();
                    showToast(`Дневная цель за ${formatLongDate(goalDate)} сохранена.`);
                    return;
                }

                if (result?.date && result?.consumed) {
                    state.daily = result;
                } else {
                    await loadDailyProgress();
                }

                hideGoalForm();
                renderDaily();
                showToast("Дневная цель сохранена.");
            } catch (error) {
                showGlobalError(error);
            }
        });
    }

    function fillExample() {
        if (state.session) {
            showGlobalError(new Error("Сначала начните новую сессию."));
            return;
        }

        if (state.localMessages.length > 0) {
            const shouldReplace = window.confirm(
                "Заменить уже введённые сообщения демонстрационным примером?"
            );

            if (!shouldReplace) {
                return;
            }
        }

        state.localMessages = [...DEMO_MESSAGES];
        savePendingDraft();
        renderMessages();
        renderPreview();
        elements.messageList.lastElementChild?.scrollIntoView({ block: "nearest" });
    }

    async function addMessage(event) {
        event.preventDefault();
        const message = elements.messageInput.value.trim();

        if (!message) {
            elements.messageInput.focus();
            return;
        }

        const currentMessages = getVisibleMessages();

        if (currentMessages.length >= 50) {
            showGlobalError(new Error("В одной сессии может быть не более 50 сообщений."));
            return;
        }

        const characterCount = currentMessages.reduce((sum, item) => sum + item.length, 0) + message.length;

        if (characterCount > 20000) {
            showGlobalError(new Error("Общий объём сообщений не может превышать 20 000 символов."));
            return;
        }

        if (!state.session) {
            state.localMessages.push(message);
            savePendingDraft();
            elements.messageInput.value = "";
            renderMessages();
            renderPreview();
            return;
        }

        if (isSessionConfirmed(state.session)) {
            showGlobalError(new Error("Подтверждённую сессию нельзя изменять. Начните новую."));
            return;
        }

        state.sessionBusy = true;
        renderMessages();
        renderPreviewLoading("Добавляем уточнение и пересобираем черновик…");

        try {
            const response = await apiRequest(`/api/meal-sessions/${encodeURIComponent(state.session.id)}/messages`, {
                method: "POST",
                body: { message }
            });
            state.session = response?.id ? response : await fetchCurrentSession();
            elements.messageInput.value = "";
            hideGlobalError();
        } catch (error) {
            showGlobalError(error);
        } finally {
            state.sessionBusy = false;
            renderMessages();
            renderPreview();
        }
    }

    async function createMealSession() {
        if (state.localMessages.length === 0 || state.sessionBusy) {
            return;
        }

        state.sessionBusy = true;
        renderMessages();
        renderPreviewLoading("Разбираем сообщения и рассчитываем предпросмотр…");

        try {
            state.session = await apiRequest("/api/meal-sessions", {
                method: "POST",
                body: {
                    messages: state.localMessages,
                    mealDate: state.date
                }
            });
            rememberActiveSession(state.session?.id);
            clearPendingDraft();
            hideGlobalError();
        } catch (error) {
            showGlobalError(error);
        } finally {
            state.sessionBusy = false;
            renderMessages();
            renderPreview();
        }
    }

    async function refreshCurrentSession() {
        if (!state.session?.id) {
            return null;
        }

        state.session = await fetchCurrentSession();
        renderMessages();
        renderPreview();
        return state.session;
    }

    function fetchCurrentSession() {
        return apiRequest(`/api/meal-sessions/${encodeURIComponent(state.session.id)}`);
    }

    function startNewSession() {
        if (state.sessionBusy) {
            return;
        }

        if (state.session && !isSessionConfirmed(state.session)) {
            const shouldDiscard = window.confirm(
                "Текущая сессия ещё не подтверждена. Начать новую и убрать её из рабочего экрана?"
            );

            if (!shouldDiscard) {
                return;
            }
        }

        state.session = null;
        state.localMessages = [];
        state.sessionBusy = false;
        forgetActiveSession();
        clearPendingDraft();
        hideGlobalError();
        renderMessages();
        renderPreview();
        elements.messageInput.disabled = false;
        elements.messageInput.focus();
    }

    async function confirmSession() {
        if (!state.session?.id || !state.session.previewToken || !state.session.canConfirm) {
            return;
        }

        const sessionId = state.session.id;
        const previewToken = state.session.previewToken;
        state.sessionBusy = true;
        renderMessages();
        renderPreview();

        try {
            const response = await apiRequest(`/api/meal-sessions/${encodeURIComponent(sessionId)}/confirm`, {
                method: "POST",
                body: { previewToken }
            });

            if (response?.session?.id) {
                state.session = response.session;
            } else if (response?.id && response?.status) {
                state.session = response;
            } else {
                state.session = await fetchCurrentSession();
            }

            forgetActiveSession();
            hideGlobalError();
            await loadDailyProgress();
            showToast("Порции записаны в дневник.");
        } catch (error) {
            showGlobalError(error);

            if (error.status === 409 || error.status === 422) {
                if (error.problem?.session?.id) {
                    state.session = error.problem.session;
                } else {
                    try {
                        await refreshCurrentSession();
                    } catch {
                        // основная ошибка уже показана пользователю
                    }
                }
            }
        } finally {
            state.sessionBusy = false;
            renderMessages();
            renderPreview();
        }
    }

    function getVisibleMessages() {
        if (!state.session) {
            return state.localMessages;
        }

        return Array.isArray(state.session.messages)
            ? state.session.messages.map(message => typeof message === "string" ? message : message?.text ?? "")
            : [];
    }

    function renderMessages() {
        const messages = getVisibleMessages();
        const confirmed = isSessionConfirmed(state.session);
        const dateLocked = Boolean(state.session) && !confirmed;

        elements.messageList.innerHTML = messages.length === 0
            ? `<div class="message-empty">Можно описать готовку несколькими короткими сообщениями — порядок сохранится.</div>`
            : messages.map((message, index) => `
                <div class="message-row">
                    <span class="message-index">${index + 1}</span>
                    <div class="message-bubble">${escapeHtml(message)}</div>
                </div>
            `).join("");

        elements.messageInput.disabled = state.sessionBusy || confirmed;
        elements.addMessageButton.disabled = state.sessionBusy || confirmed;
        elements.addMessageButton.textContent = state.session ? "Добавить уточнение" : "Добавить сообщение";
        elements.exampleButton.disabled = state.sessionBusy || Boolean(state.session);
        elements.buildDraftButton.hidden = Boolean(state.session);
        elements.buildDraftButton.disabled = state.sessionBusy || state.localMessages.length === 0;
        elements.newSessionButton.hidden = !state.session;
        elements.newSessionButton.disabled = state.sessionBusy;
        elements.mealDate.disabled = dateLocked;
        elements.dateControl.classList.toggle("is-locked", dateLocked);
        elements.dateControlLabel.textContent = dateLocked ? "День сессии" : "День";
        elements.dateControl.title = dateLocked
            ? "Дата закреплена до подтверждения или начала новой сессии"
            : "Выберите день для дневного баланса";

        if (state.sessionBusy) {
            elements.captureHint.textContent = "NutriFlow обрабатывает сессию…";
        } else if (confirmed) {
            elements.captureHint.textContent = "Сессия подтверждена. Для следующего приёма пищи начните новую.";
        } else if (state.session) {
            elements.captureHint.textContent = "Добавьте ответ на уточняющий вопрос — черновик пересоберётся автоматически.";
        } else if (state.localMessages.length === 0) {
            elements.captureHint.textContent = "Добавьте хотя бы одно сообщение.";
        } else {
            elements.captureHint.textContent = `Сообщений: ${state.localMessages.length}. Теперь можно собрать черновик.`;
        }
    }

    function renderPreviewLoading(message) {
        elements.sessionStatus.hidden = true;
        elements.confirmBar.hidden = true;
        elements.previewContent.innerHTML = `
            <div class="loading-state">
                <span class="spinner" aria-hidden="true"></span>
                <h3>Собираем картину приёма пищи</h3>
                <p>${escapeHtml(message)}</p>
            </div>
        `;
    }

    function renderPreview() {
        if (state.sessionBusy && state.session) {
            renderPreviewLoading("Обновляем расчёт и проверяем возможность подтверждения…");
            return;
        }

        if (!state.session) {
            elements.sessionStatus.hidden = true;
            elements.confirmBar.hidden = true;
            elements.previewContent.innerHTML = `
                <div class="empty-state">
                    <span class="empty-state-icon" aria-hidden="true">⌁</span>
                    <h3>Черновик пока не собран</h3>
                    <p>Сообщения превратятся в блюда, ингредиенты и рассчитанные порции.</p>
                </div>
            `;
            return;
        }

        const session = state.session;
        const confirmed = isSessionConfirmed(session);
        const status = getStatusPresentation(session.status, session.canConfirm);
        const questions = Array.isArray(session.clarificationQuestions) ? session.clarificationQuestions : [];
        const issues = Array.isArray(session.issues) ? session.issues : [];
        const dishes = Array.isArray(session.dishes) ? session.dishes : [];
        const sections = [];

        elements.sessionStatus.hidden = false;
        elements.sessionStatus.textContent = status.label;
        elements.sessionStatus.className = `status-badge ${status.className}`.trim();

        if (confirmed) {
            sections.push(`
                <div class="notice notice-info">
                    <strong>Приём пищи записан</strong>
                    Порции уже учтены в дневном балансе за ${escapeHtml(formatLongDate(session.mealDate ?? state.date))}
                </div>
            `);
        }

        if (questions.length > 0) {
            sections.push(`
                <div class="notice notice-warning">
                    <strong>Нужно уточнение</strong>
                    <ul>${questions.map(question => `<li>${escapeHtml(question)}</li>`).join("")}</ul>
                </div>
            `);
        }

        if (issues.length > 0) {
            sections.push(`
                <div class="notice notice-error">
                    <strong>Что мешает расчёту</strong>
                    <ul>
                        ${issues.map(issue => {
                            const ingredientName = issue.ingredientName ?? "";
                            const action = canResolveIssueWithProduct(issue)
                                ? ` <button class="button button-quiet button-small" type="button" data-resolve-product="${escapeHtml(ingredientName)}">Добавить продукт</button>`
                                : "";
                            return `<li>${escapeHtml(formatWorkflowIssue(issue))}${action}</li>`;
                        }).join("")}
                    </ul>
                </div>
            `);
        }

        if (dishes.length === 0) {
            sections.push(`
                <div class="empty-state">
                    <span class="empty-state-icon" aria-hidden="true">?</span>
                    <h3>Блюда пока не определены</h3>
                    <p>Добавьте недостающую информацию сообщением слева.</p>
                </div>
            `);
        } else {
            sections.push(dishes.map(renderDish).join(""));
        }

        elements.previewContent.innerHTML = sections.join("");

        if (confirmed) {
            elements.confirmBar.hidden = true;
            return;
        }

        elements.confirmBar.hidden = false;
        elements.confirmButton.disabled = !session.canConfirm || !session.previewToken || state.sessionBusy;

        if (session.canConfirm) {
            elements.confirmTitle.textContent = "Черновик готов к записи";
            elements.confirmHint.textContent = "После подтверждения порции попадут в дневной рацион.";
        } else {
            elements.confirmTitle.textContent = "Подтверждение пока недоступно";
            elements.confirmHint.textContent = questions.length > 0
                ? "Ответьте на вопросы сообщением слева."
                : "Добавьте недостающие продукты или данные.";
        }
    }

    function renderDish(dish) {
        const ingredients = Array.isArray(dish.ingredients) ? dish.ingredients : [];
        const portions = Array.isArray(dish.portions) ? dish.portions : [];
        const finalWeight = dish.finalWeightInGrams == null
            ? "итоговый вес неизвестен"
            : `${formatNumber(dish.finalWeightInGrams)} г готового блюда`;
        const totalCalories = dish.totalNutrition
            ? `${formatNumber(dish.totalNutrition.calories)} ккал`
            : "расчёт неполный";

        return `
            <article class="dish-card">
                <header class="dish-header">
                    <div>
                        <h3>${escapeHtml(dish.name ?? "Блюдо")}</h3>
                        <p>${escapeHtml(finalWeight)} ${renderQualityBadge(dish.finalWeightQuality)}</p>
                    </div>
                    <div class="dish-total">
                        <strong>${escapeHtml(totalCalories)}</strong>
                        <span>во всём блюде</span>
                    </div>
                </header>
                <div class="ingredient-list">
                    ${ingredients.map(renderIngredient).join("")}
                </div>
                <div class="dish-nutrition">
                    <div class="nutrition-box">
                        <span>Всё блюдо</span>
                        ${renderNutrition(dish.totalNutrition)}
                        ${renderNutritionQuality(dish.totalNutritionQuality)}
                    </div>
                    <div class="nutrition-box">
                        <span>На 100 г готового блюда</span>
                        ${renderNutrition(dish.nutritionPer100Grams)}
                        ${renderNutritionQuality(dish.nutritionPer100GramsQuality)}
                    </div>
                </div>
                <section class="portion-section">
                    <h4>Съеденные порции</h4>
                    <div class="portion-list">
                        ${portions.length === 0
                            ? `<span class="nutrition-line">Порция не указана.</span>`
                            : portions.map((portion, index) => `
                                <div class="portion-row">
                                    <span>Порция ${index + 1}: ${renderPortionSize(portion)}</span>
                                    <div class="portion-nutrition-result">
                                        ${renderNutrition(portion.nutrition)}
                                        ${renderNutritionQuality(portion.nutritionQuality)}
                                    </div>
                                </div>
                            `).join("")}
                    </div>
                </section>
            </article>
        `;
    }

    function renderIngredient(ingredient) {
        const product = ingredient.resolvedProduct;
        const source = product
            ? `${renderSourceBadge(product)} ${renderQualityBadge(product.dataQuality)} ${renderSourceReference(product)}`
            : `<span class="quality-badge is-unknown">продукт не найден</span>`;
        const action = product
            ? ""
            : `<button class="button button-quiet button-small" type="button" data-resolve-product="${escapeHtml(ingredient.productName ?? "")}">Добавить</button>`;

        return `
            <div class="ingredient-row">
                <div class="ingredient-main">
                    <strong>${escapeHtml(ingredient.productName ?? "Продукт")}</strong>
                    <div class="ingredient-weights">${renderIngredientWeight(ingredient)}</div>
                </div>
                <div class="ingredient-source">${source}</div>
                <div class="nutrition-line">
                    ${ingredient.nutrition ? formatNutritionText(ingredient.nutrition) : action}
                </div>
            </div>
        `;
    }

    function renderNutrition(nutrition) {
        if (!nutrition) {
            return `<span class="nutrition-line">Недостаточно данных</span>`;
        }

        return `
            <div class="nutrition-values">
                <span><strong>${formatNumber(nutrition.calories)}</strong> ккал</span>
                <span>Б <strong>${formatNumber(nutrition.proteinGrams)}</strong></span>
                <span>Ж <strong>${formatNumber(nutrition.fatGrams)}</strong></span>
                <span>У <strong>${formatNumber(nutrition.carbohydratesGrams)}</strong></span>
            </div>
        `;
    }

    function renderNutritionQuality(quality) {
        if (!quality) {
            return "";
        }

        return `<span class="calculation-quality">качество КБЖУ ${renderQualityBadge(quality)}</span>`;
    }

    function renderQualityBadge(quality) {
        if (!quality) {
            return "";
        }

        const normalized = String(quality).toLowerCase();
        const labels = {
            exact: "точно",
            verified: "проверено",
            estimated: "примерно",
            unknown: "неизвестно"
        };
        const className = normalized === "estimated"
            ? " is-estimated"
            : normalized === "unknown" ? " is-unknown" : "";

        return `<span class="quality-badge${className}">${escapeHtml(labels[normalized] ?? quality)}</span>`;
    }

    function renderSourceBadge(product) {
        const sourceKind = String(product.sourceKind ?? "").toLowerCase();
        const sourceLabels = {
            manualinput: "ручной ввод",
            labelphoto: "этикетка",
            externalservice: product.sourceName || "внешний каталог",
            nutriflowcatalog: "каталог NutriFlow",
            webpage: "веб-источник",
            dishphoto: "оценка по фото",
            unknown: "источник неизвестен"
        };

        return `<span class="source-badge" title="${escapeHtml(product.sourceName ?? "")}">${escapeHtml(sourceLabels[sourceKind] ?? product.sourceName ?? "источник")}</span>`;
    }

    function renderSourceReference(product) {
        const reference = getSafeSourceUrl(product?.sourceReference);

        if (!reference) {
            return "";
        }

        return `<a class="source-link" href="${escapeHtml(reference)}" target="_blank" rel="noopener noreferrer" referrerpolicy="no-referrer" aria-label="Открыть источник данных о продукте в новой вкладке">источник ↗</a>`;
    }

    function getSafeSourceUrl(value) {
        if (!value) {
            return null;
        }

        const rawReference = String(value);
        const labelPhotoMatch = /^label-photo:([a-f0-9]{32}\.(?:jpg|png|webp))$/i.exec(
            rawReference
        );

        if (labelPhotoMatch) {
            return `/api/label-photos/${encodeURIComponent(labelPhotoMatch[1])}`;
        }

        try {
            const url = new URL(rawReference);
            return url.protocol === "http:" || url.protocol === "https:"
                ? url.href
                : null;
        } catch {
            return null;
        }
    }

    function formatWorkflowIssue(issue) {
        const ingredientName = issue?.ingredientName
            ? ` «${issue.ingredientName}»`
            : "";
        const dishName = issue?.dishName
            ? ` «${issue.dishName}»`
            : "";
        const messages = {
            product_not_found: `Продукт${ingredientName} не найден в каталоге.`,
            product_ambiguous: `Для продукта${ingredientName} найдено несколько вариантов с разными КБЖУ.`,
            ingredient_weight_missing: `Укажите массу ингредиента${ingredientName}.`,
            removed_weight_missing: `Укажите, сколько ингредиента${ingredientName} было удалено.`,
            final_weight_missing: `Укажите итоговую массу блюда${dishName}, чтобы рассчитать порцию.`,
            portion_missing: `Укажите массу съеденной порции блюда${dishName}.`
        };
        const code = String(issue?.code ?? "").toLowerCase();

        return messages[code] ?? issue?.message ?? issue?.code ?? "Необходимо проверить данные.";
    }

    function canResolveIssueWithProduct(issue) {
        if (!issue?.ingredientName) {
            return false;
        }

        const code = String(issue.code ?? "").toLowerCase();

        if (code === "product_ambiguous") {
            return false;
        }

        return /product|catalog|ambiguous|not.?found|unresolved/i.test(code);
    }

    function getStatusPresentation(statusValue, canConfirm) {
        const status = String(statusValue ?? "").toLowerCase();

        if (status === "confirmed") {
            return { label: "Записано", className: "is-confirmed" };
        }

        if (status === "readyforconfirmation" || canConfirm) {
            return { label: "Готово к проверке", className: "" };
        }

        if (status === "needsproducts") {
            return { label: "Нужны продукты", className: "is-warning" };
        }

        if (status === "needsclarification") {
            return { label: "Нужно уточнение", className: "is-warning" };
        }

        return { label: "Требует внимания", className: "is-warning" };
    }

    function isSessionConfirmed(session) {
        return String(session?.status ?? "").toLowerCase() === "confirmed";
    }

    function openCatalog(tabName, ingredientName = null) {
        state.catalogIngredientName = ingredientName || null;
        elements.catalogContext.textContent = state.catalogIngredientName
            ? `Ищем данные для «${state.catalogIngredientName}». После сохранения обновим предпросмотр.`
            : state.capabilities?.supportsLabelPhotos === true
                ? "Найдите продукт по штрихкоду, распознайте этикетку или внесите значения вручную."
                : "Найдите продукт по штрихкоду или внесите значения вручную.";

        if (state.catalogIngredientName) {
            elements.manualProductForm.elements.name.value = state.catalogIngredientName;
        }

        activateCatalogTab(tabName);

        if (!elements.catalogDialog.open) {
            elements.catalogDialog.showModal();
        }
    }

    function closeCatalog() {
        if (elements.catalogDialog.open) {
            elements.catalogDialog.close();
        }
    }

    function activateCatalogTab(tabName, focusPanel = true) {
        const tabs = [...document.querySelectorAll("[data-tab]")];
        const requestedTab = tabs.find(tab => tab.dataset.tab === tabName && !tab.disabled);
        const activeTab = requestedTab ?? tabs.find(tab => !tab.disabled);

        if (!activeTab) {
            return;
        }

        tabs.forEach(tab => {
            const isActive = tab === activeTab;
            tab.classList.toggle("is-active", isActive);
            tab.setAttribute("aria-selected", String(isActive));
            tab.tabIndex = isActive ? 0 : -1;
        });

        document.querySelectorAll("[data-panel]").forEach(panel => {
            const isActive = panel.dataset.panel === activeTab.dataset.tab;
            panel.classList.toggle("is-active", isActive);
            panel.hidden = !isActive;
        });

        if (focusPanel) {
            const activePanel = document.querySelector(`[data-panel="${activeTab.dataset.tab}"]`);
            window.setTimeout(() => activePanel?.querySelector("input:not(:disabled)")?.focus(), 0);
        }
    }

    function handleCatalogTabKeydown(event) {
        if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) {
            return;
        }

        const tabs = [...document.querySelectorAll("[data-tab]")]
            .filter(tab => !tab.disabled);

        if (tabs.length === 0) {
            return;
        }

        event.preventDefault();
        const currentIndex = Math.max(0, tabs.indexOf(event.currentTarget));
        let nextIndex;

        if (event.key === "Home") {
            nextIndex = 0;
        } else if (event.key === "End") {
            nextIndex = tabs.length - 1;
        } else {
            const direction = event.key === "ArrowRight" ? 1 : -1;
            nextIndex = (currentIndex + direction + tabs.length) % tabs.length;
        }

        const nextTab = tabs[nextIndex];
        activateCatalogTab(nextTab.dataset.tab, false);
        nextTab.focus();
    }

    async function findProductByBarcode(event) {
        event.preventDefault();
        const barcode = elements.barcodeForm.elements.barcode.value.trim();
        const submitButton = elements.barcodeForm.querySelector("button[type='submit']");
        elements.barcodeResult.innerHTML = "";

        await runButtonTask(submitButton, "Ищем…", async () => {
            try {
                let product = await apiRequest(`/api/products/barcode/${encodeURIComponent(barcode)}`);
                let resultMessage = "Продукт найден и доступен локально.";

                if (state.catalogIngredientName) {
                    product = await apiRequest("/api/products/aliases", {
                        method: "POST",
                        body: {
                            barcode,
                            alias: state.catalogIngredientName
                        }
                    });
                    resultMessage = `Продукт связан с ингредиентом «${state.catalogIngredientName}».`;
                }

                elements.barcodeResult.innerHTML = renderProductResult(product, resultMessage);
                await refreshSessionAfterProductChange();
                showToast("Каталог обновлён.");
            } catch (error) {
                showInlineError(elements.barcodeResult, error);
            }
        });
    }

    async function saveManualProduct(event) {
        event.preventDefault();
        const form = elements.manualProductForm;
        const submitButton = form.querySelector("button[type='submit']");
        const barcode = form.elements.barcode.value.trim();
        const payload = {
            name: form.elements.name.value.trim(),
            ...readNutritionForm(form),
            isEstimated: form.elements.isEstimated.checked,
            barcode: barcode || null
        };
        elements.manualResult.innerHTML = "";

        await runButtonTask(submitButton, "Сохраняем…", async () => {
            try {
                const product = await apiRequest("/api/products/manual", {
                    method: "POST",
                    body: payload
                });
                elements.manualResult.innerHTML = renderProductResult(product, "Ручные данные сохранены в SQLite.");
                await refreshSessionAfterProductChange();
                showToast("Продукт сохранён.");
            } catch (error) {
                showInlineError(elements.manualResult, error);
            }
        });
    }

    function setLabelFile(file) {
        state.labelFile = file;
        const title = elements.photoDropzone.querySelector("strong");
        const caption = elements.photoDropzone.querySelector("span:last-child");

        if (!file) {
            title.textContent = "Выберите фотографию этикетки";
            caption.textContent = "JPEG, PNG или WebP, не более 8 МБ";
            return;
        }

        title.textContent = file.name;
        caption.textContent = `${formatFileSize(file.size)} · нажмите «Распознать этикетку»`;
    }

    async function analyzeLabel(event) {
        event.preventDefault();

        if (state.capabilities?.supportsLabelPhotos !== true) {
            showInlineError(elements.labelResult, new Error("Распознавание фотографий недоступно в текущем режиме."));
            return;
        }

        const file = state.labelFile ?? elements.labelPhoto.files?.[0];
        const submitButton = elements.labelForm.querySelector("button[type='submit']");
        elements.labelResult.innerHTML = "";

        if (!file) {
            showInlineError(elements.labelResult, new Error("Выберите фотографию этикетки."));
            return;
        }

        if (!ALLOWED_PHOTO_TYPES.has(file.type)) {
            showInlineError(elements.labelResult, new Error("Разрешены только JPEG, PNG и WebP."));
            return;
        }

        if (file.size > MAX_PHOTO_SIZE) {
            showInlineError(elements.labelResult, new Error("Фотография не должна превышать 8 МБ."));
            return;
        }

        const formData = new FormData();
        formData.append("photo", file, file.name);

        await runButtonTask(submitButton, "Распознаём…", async () => {
            try {
                state.labelDraft = await apiRequest("/api/labels/analyze", {
                    method: "POST",
                    body: formData
                });
                renderLabelDraft();
            } catch (error) {
                state.labelDraft = null;
                showInlineError(elements.labelResult, error);
            }
        });
    }

    function renderLabelDraft() {
        const draft = state.labelDraft;

        if (!draft) {
            elements.labelResult.innerHTML = "";
            return;
        }

        const questions = Array.isArray(draft.clarificationQuestions)
            ? draft.clarificationQuestions
            : [];
        const initialName = state.catalogIngredientName || draft.productName || "";
        const canSave = canSaveLabelDraft(draft);

        elements.labelResult.innerHTML = `
            <form id="label-product-form" class="result-card stack-form">
                <div>
                    <h3>Проверьте распознанные данные</h3>
                    <p>Основа: ${escapeHtml(formatNutritionBasis(draft.basis))}. Значения сохранятся только после подтверждения.</p>
                </div>
                ${questions.length > 0 ? `
                    <div class="notice notice-warning">
                        <strong>Нужно проверить</strong>
                        <ul>${questions.map(question => `<li>${escapeHtml(question)}</li>`).join("")}</ul>
                    </div>
                ` : ""}
                <label class="field">
                    <span>Название продукта</span>
                    <input name="name" type="text" maxlength="200" value="${escapeHtml(initialName)}" required>
                </label>
                <div class="field-grid field-grid-two">
                    ${renderLabelNutritionInput("calories", "Калории, ккал", draft.calories)}
                    ${renderLabelNutritionInput("proteinGrams", "Белки, г", draft.proteinGrams)}
                    ${renderLabelNutritionInput("fatGrams", "Жиры, г", draft.fatGrams)}
                    ${renderLabelNutritionInput("carbohydratesGrams", "Углеводы, г", draft.carbohydratesGrams)}
                </div>
                <label class="field">
                    <span>Штрихкод, если есть</span>
                    <input name="barcode" type="text" inputmode="numeric" pattern="[0-9]{8}|[0-9]{12,14}">
                </label>
                ${canSave ? "" : `<p class="form-hint">Сохранение доступно только для значений на 100 г. Данные на порцию или 100 мл нельзя пересчитывать автоматически.</p>`}
                <button class="button button-primary" type="submit" ${canSave ? "" : "disabled"}>Сохранить данные с этикетки</button>
            </form>
        `;
    }

    function renderLabelNutritionInput(name, label, value) {
        const inputValue = value == null ? "" : escapeHtml(value);
        const maximum = name === "calories" ? 1000 : 100;

        return `
            <label class="field">
                <span>${label} / 100 г</span>
                <input name="${name}" type="number" min="0" max="${maximum}" step="0.01" value="${inputValue}" required>
            </label>
        `;
    }

    async function saveLabelProduct(event) {
        event.preventDefault();

        if (!state.labelDraft?.photoReference || !canSaveLabelDraft(state.labelDraft)) {
            return;
        }

        const form = event.target;
        const submitButton = form.querySelector("button[type='submit']");
        const barcode = form.elements.barcode.value.trim();
        const payload = {
            photoReference: state.labelDraft.photoReference,
            basis: state.labelDraft.basis,
            name: form.elements.name.value.trim(),
            ...readNutritionForm(form),
            barcode: barcode || null
        };

        await runButtonTask(submitButton, "Сохраняем…", async () => {
            try {
                const product = await apiRequest("/api/products/from-label", {
                    method: "POST",
                    body: payload
                });
                elements.labelResult.innerHTML = renderProductResult(product, "Проверенные данные этикетки сохранены.");
                await refreshSessionAfterProductChange();
                showToast("Продукт с этикетки сохранён.");
            } catch (error) {
                const existingError = form.querySelector(".result-error");
                existingError?.remove();
                form.insertAdjacentHTML("beforeend", `<div class="result-error">${escapeHtml(error.message)}</div>`);
            }
        });
    }

    async function refreshSessionAfterProductChange() {
        if (!state.session?.id || isSessionConfirmed(state.session)) {
            return;
        }

        try {
            await refreshCurrentSession();
        } catch (error) {
            showGlobalError(error);
        }
    }

    function renderProductResult(product, message) {
        return `
            <article class="result-card">
                <h3>${escapeHtml(product?.name ?? "Продукт сохранён")}</h3>
                ${renderNutrition(product)}
                <p>${escapeHtml(message)}</p>
                <p>Источник: ${escapeHtml(product?.sourceName ?? product?.sourceKind ?? "не указан")} · качество: ${escapeHtml(formatQuality(product?.dataQuality))} ${renderSourceReference(product)}</p>
                ${product?.barcode ? `<p>Штрихкод: ${escapeHtml(product.barcode)}</p>` : ""}
            </article>
        `;
    }

    function showInlineError(container, error) {
        container.innerHTML = `<div class="result-error" role="alert">${escapeHtml(error.message ?? "Неизвестная ошибка.")}</div>`;
    }

    function showGlobalError(error) {
        elements.globalErrorText.textContent = error?.message ?? "Неизвестная ошибка.";
        elements.globalError.hidden = false;
    }

    function hideGlobalError() {
        elements.globalError.hidden = true;
        elements.globalErrorText.textContent = "";
    }

    function showToast(message) {
        window.clearTimeout(state.toastTimer);
        elements.toast.textContent = message;
        elements.toast.hidden = false;
        state.toastTimer = window.setTimeout(() => {
            elements.toast.hidden = true;
        }, 3200);
    }

    async function runButtonTask(button, busyText, task) {
        const originalText = button.textContent;
        button.disabled = true;
        button.textContent = busyText;
        button.setAttribute("aria-busy", "true");

        try {
            await task();
        } finally {
            button.disabled = false;
            button.textContent = originalText;
            button.removeAttribute("aria-busy");
        }
    }

    function readNutritionForm(form) {
        return {
            calories: readFormNumber(form, "calories"),
            proteinGrams: readFormNumber(form, "proteinGrams"),
            fatGrams: readFormNumber(form, "fatGrams"),
            carbohydratesGrams: readFormNumber(form, "carbohydratesGrams")
        };
    }

    function readFormNumber(form, name) {
        const value = form.elements[name]?.valueAsNumber;
        return Number.isFinite(value) ? value : 0;
    }

    function readNumber(object, key) {
        const value = Number(object?.[key]);
        return Number.isFinite(value) ? value : 0;
    }

    function emptyNutrition() {
        return {
            calories: 0,
            proteinGrams: 0,
            fatGrams: 0,
            carbohydratesGrams: 0
        };
    }

    function formatNutritionText(nutrition) {
        if (!nutrition) {
            return "КБЖУ не рассчитаны";
        }

        return `${formatNumber(nutrition.calories)} ккал · Б ${formatNumber(nutrition.proteinGrams)} · Ж ${formatNumber(nutrition.fatGrams)} · У ${formatNumber(nutrition.carbohydratesGrams)}`;
    }

    function renderIngredientWeight(ingredient) {
        const originalWeight = ingredient?.weightInGrams == null
            ? "исходная масса неизвестна"
            : `исходно ${formatNumber(ingredient.weightInGrams)} г`;
        const isLegacyRemoval = ingredient?.removedWeightInGrams == null &&
            !ingredient?.removedWeightQuality;
        const removedWeight = isLegacyRemoval
            ? 0
            : ingredient?.removedWeightInGrams;
        const removedQuality = isLegacyRemoval
            ? "Exact"
            : ingredient?.removedWeightQuality;
        const includedWeight = isLegacyRemoval
            ? ingredient?.weightInGrams
            : ingredient?.includedWeightInGrams;
        const removedWeightText = removedWeight == null
            ? "удалённая масса неизвестна"
            : `удалено ${formatNumber(removedWeight)} г`;
        const includedWeightText = includedWeight == null
            ? "вошедшая масса неизвестна"
            : `вошло ${formatNumber(includedWeight)} г`;
        const removalWasReported = removedWeight == null || removedWeight > 0;

        if (!removalWasReported) {
            return `
                <span class="ingredient-weight-row">
                    <span>${escapeHtml(originalWeight)}</span>
                    ${renderQualityBadge(ingredient?.weightQuality)}
                </span>
            `;
        }

        return `
            <span class="ingredient-weight-row">
                <span>${escapeHtml(originalWeight)}</span>
                ${renderQualityBadge(ingredient?.weightQuality)}
            </span>
            <span class="ingredient-weight-row">
                <span>${escapeHtml(removedWeightText)}</span>
                ${renderQualityBadge(removedQuality)}
            </span>
            <span class="ingredient-included-weight">${escapeHtml(includedWeightText)}</span>
        `;
    }

    function renderPortionSize(portion) {
        if (portion?.fractionOfDish != null) {
            const resolvedWeight = portion.weightInGrams == null
                ? ""
                : `<span class="portion-derived-weight">${formatNumber(portion.weightInGrams)} г по итоговому весу</span>`;

            return `<strong>${formatNumber(Number(portion.fractionOfDish) * 100)}% блюда</strong> ${renderQualityBadge(portion.weightQuality)} ${resolvedWeight}`;
        }

        if (portion?.weightInGrams != null) {
            return `<strong>${formatNumber(portion.weightInGrams)} г</strong> ${renderQualityBadge(portion.weightQuality)}`;
        }

        return "масса неизвестна";
    }

    function formatNumber(value) {
        const number = Number(value);

        if (!Number.isFinite(number)) {
            return "—";
        }

        return new Intl.NumberFormat("ru-RU", {
            maximumFractionDigits: 2
        }).format(number);
    }

    function formatLongDate(isoDate) {
        const parts = String(isoDate).split("-").map(Number);

        if (parts.length !== 3 || parts.some(part => !Number.isFinite(part))) {
            return isoDate;
        }

        const date = new Date(parts[0], parts[1] - 1, parts[2]);
        return new Intl.DateTimeFormat("ru-RU", {
            day: "numeric",
            month: "long",
            year: "numeric"
        }).format(date);
    }

    function formatQuality(quality) {
        const labels = {
            exact: "точно",
            verified: "проверено",
            estimated: "примерно",
            unknown: "неизвестно"
        };

        return labels[String(quality ?? "unknown").toLowerCase()] ?? quality;
    }

    function formatNutritionBasis(basis) {
        const labels = {
            per100grams: "на 100 г",
            per100milliliters: "на 100 мл",
            perserving: "на порцию",
            unknown: "не определена"
        };

        return labels[String(basis ?? "unknown").toLowerCase()] ?? basis;
    }

    function canSaveLabelDraft(draft) {
        return draft?.canCreateProduct === true &&
            String(draft.basis ?? "").toLowerCase() === "per100grams";
    }

    function formatFileSize(size) {
        if (size < 1024 * 1024) {
            return `${Math.max(1, Math.round(size / 1024))} КБ`;
        }

        return `${(size / (1024 * 1024)).toFixed(1).replace(".", ",")} МБ`;
    }

    function getLocalIsoDate() {
        const now = new Date();
        const year = now.getFullYear();
        const month = String(now.getMonth() + 1).padStart(2, "0");
        const day = String(now.getDate()).padStart(2, "0");
        return `${year}-${month}-${day}`;
    }

    function isValidIsoDate(value) {
        const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);

        if (!match) {
            return false;
        }

        const year = Number(match[1]);
        const month = Number(match[2]);
        const day = Number(match[3]);
        const isLeapYear = year % 4 === 0 &&
            (year % 100 !== 0 || year % 400 === 0);
        const daysInMonth = [
            31,
            isLeapYear ? 29 : 28,
            31,
            30,
            31,
            30,
            31,
            31,
            30,
            31,
            30,
            31
        ];

        return year >= 1 &&
            month >= 1 &&
            month <= 12 &&
            day >= 1 &&
            day <= daysInMonth[month - 1];
    }

    function escapeHtml(value) {
        return String(value ?? "")
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll('"', "&quot;")
            .replaceAll("'", "&#039;");
    }
})();
