import * as vscode from 'vscode';

interface SearchInputCallbacks {
    runSearch(query: string): void;
    cancelSearch(): void;
}

export interface SearchSettingsDisplay {
    matchCase: boolean;
    wholeWord: boolean;
    regex: boolean;
    include: string;
    exclude: string;
}

function nonce(): string {
    const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
    let value = '';
    for (let index = 0; index < 32; index += 1) {
        value += alphabet.charAt(Math.floor(Math.random() * alphabet.length));
    }
    return value;
}

export class SearchInputViewProvider implements vscode.WebviewViewProvider, vscode.Disposable {
    private view?: vscode.WebviewView;
    private query = '';
    private status = 'Type or paste text. Press Enter to search and Escape to cancel.';
    private settings: SearchSettingsDisplay = {
        matchCase: false,
        wholeWord: false,
        regex: false,
        include: '',
        exclude: '',
    };
    private pendingFocus = false;
    private webviewReady = false;
    private readonly disposables: vscode.Disposable[] = [];

    constructor(
        private readonly callbacks: SearchInputCallbacks
    ) {}

    resolveWebviewView(view: vscode.WebviewView): void {
        this.view = view;
        this.webviewReady = false;
        view.webview.options = { enableScripts: true };
        this.disposables.push(view.webview.onDidReceiveMessage((message: unknown) => {
            if (!message || typeof message !== 'object') {
                return;
            }
            const event = message as { type?: string; query?: unknown };
            if (event.type === 'search' && typeof event.query === 'string') {
                this.query = event.query;
                this.callbacks.runSearch(event.query);
            } else if (event.type === 'cancel') {
                this.callbacks.cancelSearch();
            } else if (event.type === 'change' && typeof event.query === 'string') {
                this.query = event.query;
            } else if (event.type === 'ready') {
                this.webviewReady = true;
                void this.sync();
                if (this.pendingFocus) {
                    this.pendingFocus = false;
                    void this.post({ type: 'focus' });
                }
            }
        }));
        this.disposables.push(view.onDidDispose(() => {
            if (this.view === view) {
                this.view = undefined;
                this.webviewReady = false;
            }
        }));
        view.webview.html = this.html(view.webview);
    }

    async focus(select = false): Promise<void> {
        this.pendingFocus = true;
        await vscode.commands.executeCommand('workbench.view.extension.hunt');
        await vscode.commands.executeCommand('hunt.input.focus');
        this.view?.show(true);
        if (this.view && this.webviewReady) {
            this.pendingFocus = false;
            await this.post({ type: 'focus', select });
        }
    }

    setQuery(query: string): void {
        this.query = query;
        void this.post({ type: 'query', value: query });
    }

    setStatus(status: string): void {
        this.status = status;
        void this.post({ type: 'status', value: status });
    }

    setSettings(settings: SearchSettingsDisplay): void {
        this.settings = { ...settings };
        void this.post({ type: 'settings', value: this.settings });
    }

    private async sync(): Promise<void> {
        await this.post({
            type: 'sync',
            query: this.query,
            status: this.status,
            settings: this.settings,
        });
    }

    private async post(message: unknown): Promise<void> {
        if (this.view) {
            await this.view.webview.postMessage(message);
        }
    }

    private html(webview: vscode.Webview): string {
        const scriptNonce = nonce();
        const csp = [
            "default-src 'none'",
            `style-src ${webview.cspSource} 'unsafe-inline'`,
            `script-src 'nonce-${scriptNonce}'`,
        ].join('; ');
        return `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta http-equiv="Content-Security-Policy" content="${csp}">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <style>
        :root { color-scheme: light dark; }
        body {
            box-sizing: border-box;
            color: var(--vscode-foreground);
            background: var(--vscode-sideBar-background);
            font: var(--vscode-font-size) var(--vscode-font-family);
            margin: 0;
            padding: 8px 10px 10px;
        }
        form { margin: 0; }
        input {
            box-sizing: border-box;
            width: 100%;
            min-height: 28px;
            color: var(--vscode-input-foreground);
            background: var(--vscode-input-background);
            border: 1px solid var(--vscode-input-border, transparent);
            border-radius: 2px;
            padding: 4px 7px;
            outline: none;
        }
        input:focus {
            border-color: var(--vscode-focusBorder);
            outline: 1px solid var(--vscode-focusBorder);
            outline-offset: -1px;
        }
        #settings, #status {
            color: var(--vscode-descriptionForeground);
            font-size: 0.9em;
            line-height: 1.35;
            margin-top: 6px;
        }
        #status { min-height: 1.35em; }
        #settings {
            border-top: 1px solid var(--vscode-sideBarSectionHeader-border, var(--vscode-widget-border));
            margin-top: 10px;
            padding-top: 8px;
        }
        #settings h2 {
            color: var(--vscode-foreground);
            font-size: 1em;
            margin: 0 0 6px;
        }
        #settings ul {
            list-style: none;
            margin: 0;
            padding: 0;
        }
        #settings li { margin: 5px 0; }
        .state {
            color: var(--vscode-foreground);
            font-weight: 700;
        }
        .voice-command {
            color: var(--vscode-textPreformat-foreground);
            background: var(--vscode-textCodeBlock-background);
            border: 1px solid var(--vscode-widget-border, transparent);
            border-radius: 2px;
            font-family: var(--vscode-editor-font-family, monospace);
            padding: 0 3px;
            white-space: nowrap;
        }
        .sr-only {
            position: absolute;
            width: 1px;
            height: 1px;
            padding: 0;
            margin: -1px;
            overflow: hidden;
            clip: rect(0, 0, 0, 0);
            white-space: nowrap;
            border: 0;
        }
    </style>
</head>
<body>
    <form id="search-form" role="search">
        <label for="query" class="sr-only">Search all workspace files</label>
        <input id="query" type="text" aria-describedby="settings status"
            autocomplete="off" autocapitalize="off" spellcheck="false"
            placeholder="Search all files" />
    </form>
    <div id="status" role="status" aria-live="polite" aria-atomic="true"></div>
    <section id="settings" aria-labelledby="settings-heading">
        <h2 id="settings-heading">Search settings</h2>
        <ul>
            <li>Match case: <span id="match-case" class="state"></span> — <span class="voice-command">hunt case</span></li>
            <li>Whole word: <span id="whole-word" class="state"></span> — <span class="voice-command">hunt word</span></li>
            <li>Regular expression: <span id="regex" class="state"></span> — <span class="voice-command">hunt expression</span></li>
            <li>Files included: <span id="include" class="state"></span> — <span class="voice-command">hunt include</span></li>
            <li>Files excluded: <span id="exclude" class="state"></span> — <span class="voice-command">hunt exclude</span></li>
        </ul>
    </section>
    <script nonce="${scriptNonce}">
        const vscode = acquireVsCodeApi();
        const form = document.getElementById('search-form');
        const input = document.getElementById('query');
        const status = document.getElementById('status');
        const matchCase = document.getElementById('match-case');
        const wholeWord = document.getElementById('whole-word');
        const regex = document.getElementById('regex');
        const include = document.getElementById('include');
        const exclude = document.getElementById('exclude');
        function displaySettings(settings) {
            const value = settings || {};
            matchCase.textContent = value.matchCase ? 'ON' : 'OFF';
            wholeWord.textContent = value.wholeWord ? 'ON' : 'OFF';
            regex.textContent = value.regex ? 'ON' : 'OFF';
            include.textContent = value.include || 'all files';
            exclude.textContent = value.exclude || 'workspace defaults';
        }
        const saved = vscode.getState();
        if (saved && typeof saved.query === 'string') input.value = saved.query;

        input.addEventListener('input', () => {
            vscode.setState({ query: input.value });
            vscode.postMessage({ type: 'change', query: input.value });
        });
        input.addEventListener('keydown', event => {
            if (event.key === 'Escape') {
                event.preventDefault();
                event.stopPropagation();
                vscode.postMessage({ type: 'cancel' });
            }
        });
        form.addEventListener('submit', event => {
            event.preventDefault();
            vscode.postMessage({ type: 'search', query: input.value });
        });
        window.addEventListener('message', event => {
            const message = event.data;
            if (!message) return;
            if (message.type === 'sync') {
                input.value = message.query || '';
                displaySettings(message.settings);
                status.textContent = message.status || '';
            } else if (message.type === 'query') {
                input.value = message.value || '';
            } else if (message.type === 'settings') {
                displaySettings(message.value);
            } else if (message.type === 'status') {
                status.textContent = message.value || '';
            } else if (message.type === 'focus') {
                input.focus();
                if (message.select) input.select();
            }
            vscode.setState({ query: input.value });
        });
        vscode.postMessage({ type: 'ready' });
    </script>
</body>
</html>`;
    }

    dispose(): void {
        for (const disposable of this.disposables) {
            disposable.dispose();
        }
    }
}
