window.plannerSelect = {
    _watcher: null,

    measure(trigger, count) {
        const rect = trigger.getBoundingClientRect();
        const menuHeight = Math.min(12 + count * 38, 292);
        const gap = 6;
        const spaceBelow = window.innerHeight - rect.bottom - 12;
        const openUp = spaceBelow < menuHeight && rect.top > spaceBelow;
        const top = openUp
            ? Math.max(8, rect.top - menuHeight - gap)
            : Math.min(window.innerHeight - menuHeight - 8, rect.bottom + gap);
        const width = Math.max(rect.width, 190);

        return {
            left: Math.max(8, Math.min(rect.left, window.innerWidth - width - 8)),
            top: Math.max(8, top),
            width
        };
    },

    overflowing(element) {
        return !!element && element.scrollWidth > element.clientWidth + 1;
    },

    watchOutside(element, ref) {
        this.unwatchOutside();

        const state = { element, ref, closed: false };
        const close = () => {
            if (state.closed) {
                return;
            }
            state.closed = true;
            try {
                ref.invokeMethodAsync('CloseFromOutside');
            } catch {
                // 组件已释放或 WebView 正在关闭。
            }
        };
        const onPointerDown = (event) => {
            if (!element.contains(event.target)) {
                close();
            }
        };
        const onLayout = (event) => {
            // 菜单自身滚动（选项较多时）不算失焦，否则滚一下就收起。
            if (event && event.type === 'scroll' && element.contains(event.target)) {
                return;
            }
            close();
        };
        const onKeyDown = (event) => {
            if (event.key === 'Escape') {
                close();
            }
        };

        document.addEventListener('pointerdown', onPointerDown, true);
        document.addEventListener('keydown', onKeyDown, true);
        window.addEventListener('resize', onLayout);
        window.addEventListener('scroll', onLayout, true);
        this._watcher = { element, remove: () => {
            document.removeEventListener('pointerdown', onPointerDown, true);
            document.removeEventListener('keydown', onKeyDown, true);
            window.removeEventListener('resize', onLayout);
            window.removeEventListener('scroll', onLayout, true);
        } };
    },

    unwatchOutside(element) {
        const watcher = this._watcher;
        if (!watcher) {
            return;
        }
        if (element && watcher.element !== element) {
            return;
        }
        watcher.remove();
        this._watcher = null;
    }
};

window.plannerTheme = {
    key: 'planner-theme',
    _ref: null,
    _media: null,
    _onChange: null,

    read() {
        try {
            return localStorage.getItem(this.key) || 'system';
        } catch {
            return 'system';
        }
    },

    prefersDark() {
        return !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
    },

    isDark(mode) {
        if (mode === 'dark') {
            return true;
        }
        if (mode === 'light') {
            return false;
        }
        return this.prefersDark();
    },

    apply(mode) {
        const dark = this.isDark(mode);
        const root = document.documentElement;
        root.dataset.theme = dark ? 'dark' : 'light';
        root.style.colorScheme = dark ? 'dark' : 'light';
        return dark;
    },

    set(mode) {
        const normalized = mode === 'light' || mode === 'dark' ? mode : 'system';
        try {
            localStorage.setItem(this.key, normalized);
        } catch {
            // 存储不可用时只影响持久化。
        }
        return { mode: normalized, dark: this.apply(normalized) };
    },

    init(ref) {
        this.dispose();
        this._ref = ref;
        if (window.matchMedia) {
            this._media = window.matchMedia('(prefers-color-scheme: dark)');
            this._onChange = (event) => {
                const mode = this.read();
                if (mode !== 'system') {
                    return;
                }
                this.apply('system');
                try {
                    this._ref.invokeMethodAsync('OnSystemChanged', event.matches);
                } catch {
                    // circuit 已断开。
                }
            };
            if (this._media.addEventListener) {
                this._media.addEventListener('change', this._onChange);
            } else if (this._media.addListener) {
                this._media.addListener(this._onChange);
            }
        }

        const mode = this.read();
        return { mode, dark: this.apply(mode) };
    },

    dispose() {
        if (this._media && this._onChange) {
            if (this._media.removeEventListener) {
                this._media.removeEventListener('change', this._onChange);
            } else if (this._media.removeListener) {
                this._media.removeListener(this._onChange);
            }
        }
        this._media = null;
        this._onChange = null;
        this._ref = null;
    }
};

// 复制到剪贴板：WebView2 里 navigator.clipboard 在部分来源下不可用，退回 execCommand。
window.plannerClipboard = {
    async copy(text) {
        const value = text ?? '';
        try {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(value);
                return true;
            }
        } catch {
            // 继续走兜底。
        }

        const area = document.createElement('textarea');
        area.value = value;
        area.setAttribute('readonly', '');
        area.style.position = 'fixed';
        area.style.top = '-1000px';
        area.style.opacity = '0';
        document.body.appendChild(area);
        area.select();
        area.setSelectionRange(0, area.value.length);
        let ok = false;
        try {
            ok = document.execCommand('copy');
        } catch {
            ok = false;
        }
        document.body.removeChild(area);
        return ok;
    }
};
