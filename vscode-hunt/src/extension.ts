import * as path from 'node:path';
import * as vscode from 'vscode';
import { hintForIndex, splitGlobList, spokenHint } from './searchCore';
import { RipgrepSearchOptions, SearchMatch, searchWorkspace } from './ripgrepSearch';
import { SearchInputViewProvider } from './searchInputView';

interface SearchModes {
    matchCase: boolean;
    wholeWord: boolean;
    regex: boolean;
    include: string;
    exclude: string;
}

interface MatchNode {
    kind: 'match';
    hint: string;
    match: SearchMatch;
    parent: FileNode;
    index: number;
}

interface FileNode {
    kind: 'file';
    uri: vscode.Uri;
    children: MatchNode[];
}

type SearchNode = FileNode | MatchNode;

const DEFAULT_MODES: SearchModes = {
    matchCase: false,
    wholeWord: false,
    regex: false,
    include: '',
    exclude: '',
};

function enabledExcludePatterns(folder: vscode.WorkspaceFolder): string[] {
    const extensionConfig = vscode.workspace.getConfiguration('hunt', folder.uri);
    if (!extensionConfig.get<boolean>('respectSearchExclude', true)) {
        return [];
    }

    const patterns = new Set<string>();
    for (const section of ['files.exclude', 'search.exclude']) {
        const value = vscode.workspace.getConfiguration(undefined, folder.uri).get<Record<string, unknown>>(section, {});
        for (const [pattern, enabled] of Object.entries(value)) {
            if (enabled === true || (typeof enabled === 'object' && enabled !== null)) {
                patterns.add(pattern);
            }
        }
    }
    return [...patterns];
}

class SearchTreeProvider implements vscode.TreeDataProvider<SearchNode> {
    private readonly changeEmitter = new vscode.EventEmitter<SearchNode | undefined>();
    readonly onDidChangeTreeData = this.changeEmitter.event;
    private roots: FileNode[] = [];
    private matches: MatchNode[] = [];
    private byHint = new Map<string, MatchNode>();

    setResults(results: readonly SearchMatch[]): void {
        const byFile = new Map<string, FileNode>();
        this.matches = [];
        this.byHint.clear();

        results.forEach((match, index) => {
            const key = match.uri.toString();
            let file = byFile.get(key);
            if (!file) {
                file = { kind: 'file', uri: match.uri, children: [] };
                byFile.set(key, file);
            }
            const hint = hintForIndex(index);
            const node: MatchNode = { kind: 'match', hint, match, parent: file, index };
            file.children.push(node);
            this.matches.push(node);
            this.byHint.set(hint, node);
        });

        this.roots = [...byFile.values()];
        this.changeEmitter.fire(undefined);
    }

    clear(): void {
        this.roots = [];
        this.matches = [];
        this.byHint.clear();
        this.changeEmitter.fire(undefined);
    }

    get size(): number {
        return this.matches.length;
    }

    get fileCount(): number {
        return this.roots.length;
    }

    getMatch(value: unknown): MatchNode | undefined {
        if (typeof value === 'string') {
            return this.byHint.get(value.toLowerCase());
        }
        if (value && typeof value === 'object') {
            const candidate = value as Partial<MatchNode>;
            if (candidate.kind === 'match' && candidate.match && candidate.hint) {
                return candidate as MatchNode;
            }
        }
        return undefined;
    }

    matchAt(index: number): MatchNode | undefined {
        return this.matches[index];
    }

    getTreeItem(node: SearchNode): vscode.TreeItem {
        if (node.kind === 'file') {
            const relative = vscode.workspace.asRelativePath(node.uri, true);
            const item = new vscode.TreeItem(
                path.basename(node.uri.fsPath),
                vscode.TreeItemCollapsibleState.Expanded
            );
            item.id = `file:${node.uri.toString()}`;
            item.resourceUri = node.uri;
            item.iconPath = vscode.ThemeIcon.File;
            item.description = `${relative} · ${node.children.length}`;
            item.tooltip = `${node.uri.fsPath}\n${node.children.length} matching ${node.children.length === 1 ? 'line' : 'lines'}`;
            item.accessibilityInformation = {
                label: `${relative}, ${node.children.length} matching ${node.children.length === 1 ? 'line' : 'lines'}`,
            };
            item.contextValue = 'hunt.file';
            return item;
        }

        const config = vscode.workspace.getConfiguration('hunt');
        const visibleHint = config.get<string>('hintStyle', 'lowercase') === 'uppercase'
            ? node.hint.toUpperCase()
            : node.hint;
        const prefix = `[${visibleHint}] `;
        const start = Math.min(prefix.length + node.match.previewMatchStart, prefix.length + node.match.preview.length);
        const end = Math.min(start + node.match.previewMatchLength, prefix.length + node.match.preview.length);
        const label: vscode.TreeItemLabel = {
            label: `${prefix}${node.match.preview}`,
            highlights: end > start ? [[start, end]] : undefined,
        };
        const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
        const relative = vscode.workspace.asRelativePath(node.match.uri, true);
        item.id = `match:${node.hint}:${node.match.uri.toString()}:${node.match.line}:${node.match.column}`;
        item.description = `${node.match.line + 1}:${node.match.column + 1}`;
        item.tooltip = `${relative}:${node.match.line + 1}:${node.match.column + 1}\n${node.match.preview}`;
        item.iconPath = new vscode.ThemeIcon('symbol-text');
        item.contextValue = 'hunt.match';
        item.command = {
            command: 'hunt.openResult',
            title: 'Open search result',
            arguments: [node],
        };
        item.accessibilityInformation = {
            label: `Hint ${spokenHint(node.hint)}. ${relative}, line ${node.match.line + 1}, column ${node.match.column + 1}. ${node.match.preview}`,
        };
        return item;
    }

    getChildren(node?: SearchNode): SearchNode[] {
        if (!node) {
            return this.roots;
        }
        return node.kind === 'file' ? node.children : [];
    }

    getParent(node: SearchNode): SearchNode | undefined {
        return node.kind === 'match' ? node.parent : undefined;
    }

    dispose(): void {
        this.changeEmitter.dispose();
    }
}

class HuntController implements vscode.Disposable {
    private readonly provider = new SearchTreeProvider();
    private readonly tree: vscode.TreeView<SearchNode>;
    private readonly searchInput: SearchInputViewProvider;
    private readonly disposables: vscode.Disposable[] = [];
    private modes: SearchModes;
    private query = '';
    private currentIndex = -1;
    private lastSelectedNode?: SearchNode;
    private showingResults = false;
    private activeSearch?: vscode.CancellationTokenSource;

    constructor(private readonly context: vscode.ExtensionContext) {
        this.modes = {
            ...DEFAULT_MODES,
            ...context.workspaceState.get<Partial<SearchModes>>('hunt.modes', {}),
        };
        this.query = context.workspaceState.get<string>('hunt.query', '');
        this.searchInput = new SearchInputViewProvider({
            runSearch: query => { void this.runSearchFromBox(query); },
            cancelSearch: () => { void this.cancelSearch(); },
        });
        this.disposables.push(
            vscode.window.registerWebviewViewProvider('hunt.input', this.searchInput, {
                webviewOptions: { retainContextWhenHidden: true },
            }),
            this.searchInput
        );
        this.tree = vscode.window.createTreeView('hunt.results', {
            treeDataProvider: this.provider,
            showCollapseAll: true,
        });
        this.tree.message = this.query ? 'Run refresh to restore the previous search.' : undefined;
        this.searchInput.setQuery(this.query);
        this.searchInput.setSettings(this.modes);
        this.disposables.push(this.tree, this.provider);
        this.disposables.push(this.tree.onDidChangeSelection(event => {
            const selected = event.selection[0];
            if (selected) {
                this.lastSelectedNode = selected;
                if (selected.kind === 'match') {
                    this.currentIndex = selected.index;
                }
            }
        }));

        this.register('hunt.search', (query?: unknown) => this.newSearch(query));
        this.register('hunt.refresh', () => this.refresh());
        this.register('hunt.clear', () => this.clear());
        this.register('hunt.openResult', (value?: unknown) => this.openResult(value, false));
        this.register('hunt.openResultToSide', (value?: unknown) => this.openResult(value, true));
        this.register('hunt.nextResult', () => this.moveResult(1));
        this.register('hunt.previousResult', () => this.moveResult(-1));
        this.register('hunt.copyResult', (value?: unknown) => this.copyResult(value));
        this.register('hunt.toggleCase', () => this.toggleMode('matchCase', 'Match case'));
        this.register('hunt.toggleWholeWord', () => this.toggleMode('wholeWord', 'Whole word'));
        this.register('hunt.toggleRegex', () => this.toggleMode('regex', 'Regular expression'));
        this.register('hunt.setInclude', () => this.setGlobFilter('include'));
        this.register('hunt.setExclude', () => this.setGlobFilter('exclude'));
        this.register('hunt.focus', () => this.focus());
        this.register('hunt.focusSearchBox', () => this.showSearchBox());
        this.register('hunt.showSearchBox', () => this.showSearchBox());

        this.disposables.push(vscode.workspace.onDidChangeConfiguration(event => {
            if (event.affectsConfiguration('hunt.hintStyle')) {
                const selectedIndex = this.lastSelectedNode?.kind === 'match'
                    ? this.lastSelectedNode.index
                    : undefined;
                this.provider.setResults(this.currentMatches());
                this.lastSelectedNode = selectedIndex === undefined
                    ? undefined
                    : this.provider.matchAt(selectedIndex);
            }
        }));
        void this.updateContextKeys();
    }

    private register(command: string, callback: (...args: unknown[]) => unknown): void {
        this.disposables.push(vscode.commands.registerCommand(command, callback));
    }

    private currentMatches(): SearchMatch[] {
        const matches: SearchMatch[] = [];
        for (let index = 0; index < this.provider.size; index += 1) {
            const node = this.provider.matchAt(index);
            if (node) {
                matches.push(node.match);
            }
        }
        return matches;
    }

    private async updateContextKeys(): Promise<void> {
        await Promise.all([
            vscode.commands.executeCommand('setContext', 'hunt.hasQuery', Boolean(this.query)),
            vscode.commands.executeCommand('setContext', 'hunt.matchCase', this.modes.matchCase),
            vscode.commands.executeCommand('setContext', 'hunt.wholeWord', this.modes.wholeWord),
            vscode.commands.executeCommand('setContext', 'hunt.regex', this.modes.regex),
            vscode.commands.executeCommand('setContext', 'hunt.showResults', this.showingResults),
        ]);
    }

    private selectedText(): string {
        const editor = vscode.window.activeTextEditor;
        if (!editor || editor.selection.isEmpty) {
            return '';
        }
        const value = editor.document.getText(editor.selection);
        return value.includes('\n') ? '' : value.trim();
    }

    private async runSearchFromBox(query: string): Promise<void> {
        const normalized = query.trim();
        if (!normalized) {
            this.searchInput.setStatus('Enter text to search for.');
            await this.searchInput.focus();
            return;
        }
        await this.runSearch(normalized);
    }

    private async setShowingResults(showing: boolean): Promise<void> {
        this.showingResults = showing;
        await vscode.commands.executeCommand('setContext', 'hunt.showResults', showing);
    }

    private async showSearchBox(): Promise<void> {
        await this.setShowingResults(false);
        await this.searchInput.focus();
    }

    private async cancelSearch(): Promise<void> {
        this.activeSearch?.cancel();
        this.searchInput.setStatus('Search cancelled.');
        await vscode.commands.executeCommand('workbench.action.focusActiveEditorGroup');
    }

    private async newSearch(queryArgument?: unknown): Promise<void> {
        let query = typeof queryArgument === 'string' ? queryArgument.trim() : '';
        if (!query) {
            const selected = this.selectedText();
            if (selected) {
                this.searchInput.setQuery(selected);
            }
            await this.setShowingResults(false);
            await this.searchInput.focus(Boolean(selected || this.query));
            return;
        }
        await this.runSearch(query);
    }

    private async refresh(): Promise<void> {
        if (!this.query) {
            await this.newSearch();
            return;
        }
        await this.runSearch(this.query);
    }

    private async buildOptions(query: string): Promise<RipgrepSearchOptions | undefined> {
        const roots = vscode.workspace.workspaceFolders;
        if (!roots?.length) {
            await vscode.window.showWarningMessage('Hunt needs an open folder or workspace.');
            return undefined;
        }

        const config = vscode.workspace.getConfiguration('hunt');
        const excluded = new Set(splitGlobList(this.modes.exclude));
        for (const root of roots) {
            for (const pattern of enabledExcludePatterns(root)) {
                excluded.add(pattern);
            }
        }
        return {
            query,
            roots,
            matchCase: this.modes.matchCase,
            wholeWord: this.modes.wholeWord,
            regex: this.modes.regex,
            includeGlobs: splitGlobList(this.modes.include),
            excludeGlobs: [...excluded],
            includeHidden: config.get<boolean>('includeHidden', false),
            useIgnoreFiles: config.get<boolean>('useIgnoreFiles', true),
            maxResults: config.get<number>('maxResults', 500),
            previewLength: config.get<number>('previewLength', 180),
            dataFileExtensions: config.get<string[]>('dataFileExtensions', []),
        };
    }

    private async runSearch(query: string): Promise<void> {
        const options = await this.buildOptions(query);
        if (!options) {
            return;
        }

        this.activeSearch?.cancel();
        this.activeSearch?.dispose();
        const cancellation = new vscode.CancellationTokenSource();
        this.activeSearch = cancellation;
        this.tree.message = `Searching for “${query}”…`;
        this.tree.description = 'Searching…';
        this.searchInput.setQuery(query);
        this.searchInput.setStatus(`Searching for “${query}”… Press Escape to cancel.`);

        try {
            const result = await vscode.window.withProgress(
                {
                    location: vscode.ProgressLocation.Notification,
                    title: `Hunt: ${query}`,
                    cancellable: true,
                },
                async (_progress, progressToken) => {
                    const progressCancellation = progressToken.onCancellationRequested(() => cancellation.cancel());
                    try {
                        return await searchWorkspace(options, cancellation.token);
                    } finally {
                        progressCancellation.dispose();
                    }
                }
            );
            if (cancellation.token.isCancellationRequested) {
                return;
            }

            this.query = query;
            this.currentIndex = -1;
            this.provider.setResults(result.matches);
            this.lastSelectedNode = this.provider.matchAt(0);
            this.tree.description = `“${query}” · ${result.matches.length}`;
            this.tree.message = result.matches.length === 0
                ? `No results for “${query}”. Press Escape to return to the search box.`
                : result.limitReached
                    ? `Showing the first ${result.matches.length} matching lines (result limit reached). Press Escape to return to the search box.`
                    : 'Press Escape to return to the search box.';
            this.searchInput.setStatus(
                result.matches.length === 0
                    ? `No results for “${query}”.`
                    : `${result.matches.length} matching ${result.matches.length === 1 ? 'line' : 'lines'} in ${this.provider.fileCount} ${this.provider.fileCount === 1 ? 'file' : 'files'}.${result.limitReached ? ' Result limit reached.' : ''}`
            );
            await this.context.workspaceState.update('hunt.query', query);
            await this.setShowingResults(true);
            await this.updateContextKeys();

            const first = this.provider.matchAt(0);
            if (first) {
                await vscode.commands.executeCommand('hunt.results.focus');
                await this.tree.reveal(first, { select: true, focus: true, expand: true });
            } else {
                await vscode.commands.executeCommand('hunt.results.focus');
            }

        } catch (error) {
            if (!(error instanceof vscode.CancellationError)) {
                this.tree.message = 'Search failed. See the error notification for details.';
                this.searchInput.setStatus('Search failed. See the error notification for details.');
                await vscode.window.showErrorMessage(`Hunt: ${error instanceof Error ? error.message : String(error)}`);
            }
        } finally {
            if (this.activeSearch === cancellation) {
                this.activeSearch = undefined;
            }
            cancellation.dispose();
        }
    }

    private async openResult(value: unknown, beside: boolean): Promise<void> {
        const node = this.provider.getMatch(value);
        if (!node) {
            const hint = typeof value === 'string' ? value : String(value ?? '');
            await vscode.window.showWarningMessage(`Hunt has no visible result for hint “${hint}”.`);
            return;
        }

        const document = await vscode.workspace.openTextDocument(node.match.uri);
        const editor = await vscode.window.showTextDocument(document, {
            preview: true,
            viewColumn: beside ? vscode.ViewColumn.Beside : vscode.ViewColumn.Active,
        });
        const start = new vscode.Position(node.match.line, node.match.column);
        const end = new vscode.Position(node.match.line, node.match.column + node.match.matchLength);
        const range = new vscode.Range(start, end);
        editor.selection = new vscode.Selection(start, end);
        editor.revealRange(range, vscode.TextEditorRevealType.InCenterIfOutsideViewport);
        this.currentIndex = node.index;
        this.lastSelectedNode = node;
        await this.tree.reveal(node, { select: true, focus: false });
    }

    private async moveResult(delta: number): Promise<void> {
        if (!this.provider.size) {
            await vscode.window.showInformationMessage('Hunt has no results to navigate.');
            return;
        }
        this.currentIndex = (this.currentIndex + delta + this.provider.size) % this.provider.size;
        const node = this.provider.matchAt(this.currentIndex);
        if (node) {
            await this.openResult(node, false);
        }
    }

    private async copyResult(value: unknown): Promise<void> {
        const node = this.provider.getMatch(value);
        if (!node) {
            await vscode.window.showWarningMessage('Choose a Hunt result to copy.');
            return;
        }
        const location = `${node.match.uri.fsPath}:${node.match.line + 1}:${node.match.column + 1}: ${node.match.preview}`;
        await vscode.env.clipboard.writeText(location);
        void vscode.window.showInformationMessage(`Copied result ${node.hint.toUpperCase()}.`);
    }

    private async toggleMode(key: 'matchCase' | 'wholeWord' | 'regex', label: string): Promise<void> {
        const rerunVisibleResults = this.showingResults && Boolean(this.query);
        this.modes[key] = !this.modes[key];
        await this.context.workspaceState.update('hunt.modes', this.modes);
        await this.updateContextKeys();
        this.searchInput.setSettings(this.modes);
        void vscode.window.showInformationMessage(`${label} ${this.modes[key] ? 'on' : 'off'}.`);
        if (rerunVisibleResults) {
            await this.refresh();
        } else {
            await this.showSearchBox();
        }
    }

    private async setGlobFilter(kind: 'include' | 'exclude'): Promise<void> {
        const rerunVisibleResults = this.showingResults && Boolean(this.query);
        const include = kind === 'include';
        const value = await vscode.window.showInputBox({
            title: `Hunt: Files to ${include ? 'Include' : 'Exclude'}`,
            prompt: 'Enter comma-separated glob patterns. Leave empty to clear the filter.',
            placeHolder: include ? '**/*.ts, **/*.tsx' : '**/generated/**, **/*.min.js',
            value: this.modes[kind],
            ignoreFocusOut: true,
        });
        if (value === undefined) {
            return;
        }
        this.modes[kind] = value.trim();
        await this.context.workspaceState.update('hunt.modes', this.modes);
        this.searchInput.setSettings(this.modes);
        if (rerunVisibleResults) {
            await this.refresh();
        } else {
            await this.showSearchBox();
        }
    }

    private async clear(): Promise<void> {
        this.activeSearch?.cancel();
        this.query = '';
        this.currentIndex = -1;
        this.lastSelectedNode = undefined;
        this.provider.clear();
        this.searchInput.setQuery('');
        this.searchInput.setStatus('Type or paste text. Press Enter to search and Escape to cancel.');
        this.tree.description = undefined;
        this.tree.message = undefined;
        await this.context.workspaceState.update('hunt.query', '');
        await this.setShowingResults(false);
        await this.updateContextKeys();
        await this.searchInput.focus();
    }

    private async focus(): Promise<void> {
        // Capture this before opening the container: recreating a conditional
        // view can emit a temporary selection of its first row.
        const nodeToRestore = this.lastSelectedNode;
        // Avoid reopening an already-visible container; doing so is another
        // way VS Code can reset a native tree's viewport.
        if (!this.tree.visible) {
            await vscode.commands.executeCommand('workbench.view.extension.hunt');
        }
        if (this.showingResults) {
            if (nodeToRestore) {
                await this.tree.reveal(nodeToRestore, {
                    select: true,
                    focus: true,
                    expand: true,
                });
            } else {
                await vscode.commands.executeCommand('hunt.results.focus');
            }
        } else {
            await this.searchInput.focus();
        }
    }

    dispose(): void {
        this.activeSearch?.cancel();
        this.activeSearch?.dispose();
        for (const disposable of this.disposables) {
            disposable.dispose();
        }
    }
}

export function activate(context: vscode.ExtensionContext): void {
    context.subscriptions.push(new HuntController(context));
}

export function deactivate(): void {
    // Resources are owned by the extension context.
}
