(() => {
    "use strict";

    const MAX_PHOTO_SIZE = 8 * 1024 * 1024;
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
        apiStatus: document.querySelector("#api-status"),
        globalError: document.querySelector("#global-error"),
        globalErrorText: document.querySelector("#global-error-text"),
        dismissErrorButton: document.querySelector("#dismiss-error-button"),
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
        barcodeForm: document.querySelector("#barcode-form"),
        barcodeResult: document.querySelector("#barcode-result"),
        labelForm: document.querySelector("#label-form"),
        labelPhoto: document.querySelector("#label-photo"),
        photoDropzone: document.querySelector("#photo-dropzone"),
        labelResult: document.querySelector("#label-result"),
        manualProductForm: document.querySelector("#manual-product-form"),
        manualResult: document.querySelector("#manual-result"),
        toast: document.querySelector("#toast")
    };

    const state = {
        date: getLocalIsoDate(),
        daily: null,
        dailyLoading: true,
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
        elements.mealDate.value = state.date;
        bindEvents();
        renderMessages();
        renderPreview();
        renderDaily();
        loadDailyProgress();
    }

    function bindEvents() {
        elements.mealDate.addEventListener("change", () => {
            if (!elements.mealDate.value) {
                elements.mealDate.value = state.date;
                return;
            }

            state.date = elements.mealDate.value;
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
            setLabelFile(event.dataTransfer?.files?.[0] ?? null);
        });
    }

    async function apiRequest(url, options = {}) {
        const requestOptions = {
            method: options.method ?? "GET",
            credentials: "same-origin",
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
        state.dailyLoading = true;
        renderDaily();

        try {
            state.daily = await apiRequest(`/api/daily-progress/${encodeURIComponent(state.date)}`);
        } catch (error) {
            state.daily = null;
            showGlobalError(error);
        } finally {
            state.dailyLoading = false;
            renderDaily();
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
                    <div class="metric-progress"><span style="--progress: 0%"></span></div>
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
                    ? Math.min((consumedValue / targetValue) * 100, 100)
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
                        <div class="metric-progress" aria-label="${escapeHtml(metric.label)}: ${Math.round(progress)}%">
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
                        <span>${formatNumber(entry.weightInGrams)} г</span>
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
        const submitButton = elements.goalForm.querySelector("button[type='submit']");
        const payload = readNutritionForm(elements.goalForm);

        await runButtonTask(submitButton, "Сохраняем…", async () => {
            try {
                const result = await apiRequest(`/api/daily-goals/${encodeURIComponent(state.date)}`, {
                    method: "PUT",
                    body: payload
                });

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

        state.localMessages = [...DEMO_MESSAGES];
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
        state.session = null;
        state.localMessages = [];
        state.sessionBusy = false;
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

            await loadDailyProgress();
            hideGlobalError();
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
                    Порции уже учтены в дневном балансе за ${escapeHtml(formatLongDate(session.mealDate ?? state.date))}.
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
                            return `<li>${escapeHtml(issue.message ?? issue.code ?? "Необходимо проверить данные.")}${action}</li>`;
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
                    </div>
                    <div class="nutrition-box">
                        <span>На 100 г готового блюда</span>
                        ${renderNutrition(dish.nutritionPer100Grams)}
                    </div>
                </div>
                <section class="portion-section">
                    <h4>Съеденные порции</h4>
                    <div class="portion-list">
                        ${portions.length === 0
                            ? `<span class="nutrition-line">Порция не указана.</span>`
                            : portions.map((portion, index) => `
                                <div class="portion-row">
                                    <span>Порция ${index + 1}: <strong>${formatNumber(portion.weightInGrams)} г</strong> ${renderQualityBadge(portion.weightQuality)}</span>
                                    ${renderNutrition(portion.nutrition)}
                                </div>
                            `).join("")}
                    </div>
                </section>
            </article>
        `;
    }

    function renderIngredient(ingredient) {
        const product = ingredient.resolvedProduct;
        const weight = ingredient.weightInGrams == null
            ? "масса неизвестна"
            : `${formatNumber(ingredient.weightInGrams)} г`;
        const source = product
            ? `${renderSourceBadge(product)} ${renderQualityBadge(product.dataQuality)}`
            : `<span class="quality-badge is-unknown">продукт не найден</span>`;
        const action = product
            ? ""
            : `<button class="button button-quiet button-small" type="button" data-resolve-product="${escapeHtml(ingredient.productName ?? "")}">Добавить</button>`;

        return `
            <div class="ingredient-row">
                <div class="ingredient-main">
                    <strong>${escapeHtml(ingredient.productName ?? "Продукт")}</strong>
                    <span>${escapeHtml(weight)} ${renderQualityBadge(ingredient.weightQuality)}</span>
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

    function canResolveIssueWithProduct(issue) {
        if (!issue?.ingredientName) {
            return false;
        }

        const code = String(issue.code ?? "");
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
            : "Найдите продукт по штрихкоду, распознайте этикетку или внесите значения вручную.";

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

    function activateCatalogTab(tabName) {
        document.querySelectorAll("[data-tab]").forEach(tab => {
            const isActive = tab.dataset.tab === tabName;
            tab.classList.toggle("is-active", isActive);
            tab.setAttribute("aria-selected", String(isActive));
        });

        document.querySelectorAll("[data-panel]").forEach(panel => {
            const isActive = panel.dataset.panel === tabName;
            panel.classList.toggle("is-active", isActive);
            panel.hidden = !isActive;
        });

        const activePanel = document.querySelector(`[data-panel="${tabName}"]`);
        window.setTimeout(() => activePanel?.querySelector("input")?.focus(), 0);
    }

    async function findProductByBarcode(event) {
        event.preventDefault();
        const barcode = elements.barcodeForm.elements.barcode.value.trim();
        const submitButton = elements.barcodeForm.querySelector("button[type='submit']");
        elements.barcodeResult.innerHTML = "";

        await runButtonTask(submitButton, "Ищем…", async () => {
            try {
                const product = await apiRequest(`/api/products/barcode/${encodeURIComponent(barcode)}`);
                elements.barcodeResult.innerHTML = renderProductResult(product, "Продукт найден и доступен локально.");
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
        const initialName = draft.productName || state.catalogIngredientName || "";
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

        return `
            <label class="field">
                <span>${label} / 100 г</span>
                <input name="${name}" type="number" min="0" step="0.01" value="${inputValue}" required>
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
                <p>Источник: ${escapeHtml(product?.sourceName ?? product?.sourceKind ?? "не указан")} · качество: ${escapeHtml(formatQuality(product?.dataQuality))}</p>
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
        return String(draft?.basis ?? "").toLowerCase() === "per100grams";
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

    function escapeHtml(value) {
        return String(value ?? "")
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll('"', "&quot;")
            .replaceAll("'", "&#039;");
    }
})();
