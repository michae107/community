import * as vscode from 'vscode';
import * as fs from 'fs';
import * as cp from 'child_process';
import * as path from 'path';
import type { GitExtension, API, Repository, Change } from './typings/git';
import { parseChangedLines } from './diffParser';
import { GitFileDecorationProvider } from './fileDecorator';

// Status enum values from the git extension
const STATUS_INDEX_DELETED = 2;
const STATUS_DELETED = 6;
const STATUS_UNTRACKED = 7;

let changedLineDecoration: vscode.TextEditorDecorationType;
let fileDecProvider: GitFileDecorationProvider;
let debounceTimer: NodeJS.Timeout | undefined;
let log: vscode.OutputChannel;

function isDeleted(change: Change): boolean {
    return change.status === STATUS_INDEX_DELETED || change.status === STATUS_DELETED;
}

function isNewFile(change: Change): boolean {
    // INDEX_ADDED=1, UNTRACKED=7, INTENT_TO_ADD=9
    return change.status === 1 || change.status === STATUS_UNTRACKED || change.status === 9;
}

function getAllChanges(repo: Repository): Change[] {
    // Only unstaged: working tree + untracked. Staged files (indexChanges) are
    // intentionally excluded so large committed libraries don't flood the UI.
    return [
        ...repo.state.workingTreeChanges,
        ...(repo.state.untrackedChanges || []),
    ];
}

function isSameOrSubPath(parent: string, child: string): boolean {
    const rel = path.relative(parent, child);
    return rel === '' || (!rel.startsWith('..') && !path.isAbsolute(rel));
}

// Only repositories that live inside the open workspace folder(s). The VSCode
// git extension also surfaces linked git worktrees (e.g. ~/worktrees/...) as
// separate repositories; without this filter the "open/tree changed files"
// commands pull in changes from those worktrees too.
function reposInWorkspace(git: API): Repository[] {
    const folders = vscode.workspace.workspaceFolders;
    if (!folders || folders.length === 0) {
        return git.repositories;
    }
    return git.repositories.filter(repo => {
        const repoPath = repo.rootUri.fsPath;
        return folders.some(folder => {
            const folderPath = folder.uri.fsPath;
            return isSameOrSubPath(folderPath, repoPath) || isSameOrSubPath(repoPath, folderPath);
        });
    });
}

function splitGitPathOutput(output: string): string[] {
    return output.split('\0').filter(Boolean);
}

function runGit(git: API, repo: Repository, args: string[]): Promise<string> {
    return runGitInDir(git, repo.rootUri.fsPath, args);
}

function runGitInDir(git: API, cwd: string, args: string[]): Promise<string> {
    const gitPath = git.git.path || 'git';
    return new Promise((resolve, reject) => {
        cp.execFile(
            gitPath,
            ['-C', cwd, ...args],
            { maxBuffer: 20 * 1024 * 1024 },
            (err, stdout, stderr) => {
                if (err) {
                    reject(new Error(stderr || err.message));
                    return;
                }
                resolve(stdout);
            }
        );
    });
}

function isOpenableFile(filePath: string): boolean {
    try {
        return fs.statSync(filePath).isFile();
    } catch {
        return false;
    }
}

const LOCKFILE_NAMES = new Set([
    'package-lock.json', 'yarn.lock', 'pnpm-lock.yaml', 'poetry.lock',
    'uv.lock', 'Cargo.lock', 'go.sum', 'Gemfile.lock', 'composer.lock',
]);

function isDataFile(filePath: string): boolean {
    const base = path.basename(filePath);
    if (LOCKFILE_NAMES.has(base)) return true;
    if (base.endsWith('.min.js') || base.endsWith('.min.css')) return true;
    const ext = path.extname(base).slice(1).toLowerCase();
    if (!ext) return false;
    const config = vscode.workspace.getConfiguration('gitInlineChanges');
    const dataExts = config.get<string[]>('dataFileExtensions', []);
    return dataExts.includes(ext);
}

// Pops a modal warning when a lot of files (or a lot of data files, e.g.
// dumped .jsonl/.json artifacts) are about to be opened. Returns the uris to
// open, or null if the user cancelled.
async function confirmOpenFiles(uris: vscode.Uri[]): Promise<vscode.Uri[] | null> {
    const config = vscode.workspace.getConfiguration('gitInlineChanges');
    const warnTotal = config.get<number>('warnFileCount', 15);
    const warnData = config.get<number>('warnDataFileCount', 5);

    const dataUris = uris.filter(uri => isDataFile(uri.fsPath));
    const tooManyTotal = warnTotal > 0 && uris.length > warnTotal;
    const tooManyData = warnData > 0 && dataUris.length > warnData;
    if (!tooManyTotal && !tooManyData) {
        return uris;
    }

    const detail = tooManyData
        ? `${uris.length} changed file(s), ${dataUris.length} of them data files (e.g. ${path.basename(dataUris[0].fsPath)}).`
        : `${uris.length} changed file(s).`;

    const openAll = `Open all ${uris.length}`;
    const skipData = dataUris.length > 0 && dataUris.length < uris.length
        ? `Skip ${dataUris.length} data file(s)`
        : undefined;
    const buttons = skipData ? [openAll, skipData] : [openAll];

    const choice = await vscode.window.showWarningMessage(
        'Open many changed files?',
        { modal: true, detail },
        ...buttons
    );
    if (choice === openAll) return uris;
    if (skipData && choice === skipData) {
        return uris.filter(uri => !isDataFile(uri.fsPath));
    }
    log.appendLine('Open changed files cancelled by user.');
    return null;
}

interface UnstagedFiles {
    tracked: vscode.Uri[];
    untracked: vscode.Uri[];
}

async function getUnstagedFiles(git: API, repo: Repository): Promise<UnstagedFiles> {
    const trackedOutput = await runGit(git, repo, [
        'diff',
        '--name-only',
        '--diff-filter=d',
        '-z',
        '--',
    ]);
    const untrackedOutput = await runGit(git, repo, [
        'ls-files',
        '--others',
        '--exclude-standard',
        '-z',
        '--',
    ]);

    const seen = new Set<string>();
    const result: UnstagedFiles = { tracked: [], untracked: [] };

    const collect = (output: string, into: vscode.Uri[]) => {
        for (const relativePath of splitGitPathOutput(output)) {
            const filePath = path.join(repo.rootUri.fsPath, relativePath);
            if (seen.has(filePath)) continue;
            seen.add(filePath);

            if (!isOpenableFile(filePath)) {
                log.appendLine(`  Skipping non-file or missing path: ${filePath}`);
                continue;
            }

            into.push(vscode.Uri.file(filePath));
        }
    };

    collect(trackedOutput, result.tracked);
    collect(untrackedOutput, result.untracked);
    return result;
}

async function getUnstagedFileUris(git: API, repo: Repository): Promise<vscode.Uri[]> {
    const files = await getUnstagedFiles(git, repo);
    return [...files.tracked, ...files.untracked];
}

export function activate(context: vscode.ExtensionContext) {
    log = vscode.window.createOutputChannel('Git Inline Changes');
    log.appendLine('Extension activating...');

    const config = vscode.workspace.getConfiguration('gitInlineChanges');
    const color = config.get<string>('addedColor', 'rgba(80, 180, 255, 0.12)');
    log.appendLine(`Using highlight color: ${color}`);

    changedLineDecoration = vscode.window.createTextEditorDecorationType({
        backgroundColor: color,
        isWholeLine: true,
        overviewRulerColor: 'rgba(80, 180, 255, 0.5)',
        overviewRulerLane: vscode.OverviewRulerLane.Left,
    });

    const gitExt = vscode.extensions.getExtension<GitExtension>('vscode.git');
    if (!gitExt) {
        log.appendLine('ERROR: vscode.git extension not found!');
        return;
    }
    log.appendLine(`Git extension found, isActive=${gitExt.isActive}`);

    if (gitExt.isActive) {
        initWithGit(gitExt.exports.getAPI(1), context);
    } else {
        log.appendLine('Waiting for git extension to activate...');
        gitExt.activate().then(ext => {
            initWithGit(ext.getAPI(1), context);
        });
    }
}

function initWithGit(git: API, context: vscode.ExtensionContext) {
    log.appendLine(`Git API state: ${git.state}, repos: ${git.repositories.length}`);

    if (git.state === 'initialized') {
        setup(git, context);
    } else {
        log.appendLine('Git API not initialized yet, waiting...');
        const disposable = git.onDidChangeState(state => {
            log.appendLine(`Git API state changed to: ${state}`);
            if (state === 'initialized') {
                disposable.dispose();
                setup(git, context);
            }
        });
        context.subscriptions.push(disposable);
    }

    context.subscriptions.push(
        git.onDidOpenRepository(repo => {
            log.appendLine(`New repo opened: ${repo.rootUri.fsPath}`);
            watchRepo(repo, git, context);
            fileDecProvider?.refresh(repo);
            const editor = vscode.window.activeTextEditor;
            if (editor) {
                updateLineDecorations(editor, git);
            }
        })
    );
}

async function openAllChangedFiles(git: API) {
    const allUris: vscode.Uri[] = [];

    for (const repo of reposInWorkspace(git)) {
        try {
            const uris = await getUnstagedFileUris(git, repo);
            log.appendLine(`openAllChanged: ${uris.length} unstaged file(s) in ${repo.rootUri.fsPath}`);
            for (const uri of uris) {
                log.appendLine(`  unstaged: ${uri.fsPath}`);
            }
            allUris.push(...uris);
        } catch (err) {
            log.appendLine(`openAllChanged: git file list failed for ${repo.rootUri.fsPath}: ${err}`);
        }
    }

    if (allUris.length === 0) {
        log.appendLine('No changed files to open.');
        vscode.window.showInformationMessage('No changed files found.');
        return;
    }

    const urisToOpen = await confirmOpenFiles(allUris);
    if (!urisToOpen) return;

    log.appendLine(`Opening ${urisToOpen.length} changed file(s)...`);
    for (const uri of urisToOpen) {
        try {
            await vscode.window.showTextDocument(uri, { preview: false, preserveFocus: true });
        } catch (err) {
            log.appendLine(`  Failed to open ${uri.fsPath}: ${err}`);
        }
    }
}

// Opens one diff editor tab per unstaged file (index vs working tree), so you
// stay in the main editor area instead of the SCM multi-diff view. Untracked
// files have nothing to diff against, so they open as plain editors.
async function openAllChangedDiffs(git: API) {
    const tracked: vscode.Uri[] = [];
    const untracked: vscode.Uri[] = [];

    for (const repo of reposInWorkspace(git)) {
        try {
            const files = await getUnstagedFiles(git, repo);
            tracked.push(...files.tracked);
            untracked.push(...files.untracked);
        } catch (err) {
            log.appendLine(`openAllDiffs: git file list failed for ${repo.rootUri.fsPath}: ${err}`);
        }
    }

    if (tracked.length + untracked.length === 0) {
        vscode.window.showInformationMessage('No changed files found.');
        return;
    }

    const toOpen = await confirmOpenFiles([...tracked, ...untracked]);
    if (!toOpen) return;
    const keep = new Set(toOpen.map(uri => uri.fsPath));

    log.appendLine(`Opening ${toOpen.length} diff tab(s)...`);
    for (const uri of tracked) {
        if (!keep.has(uri.fsPath)) continue;
        try {
            await vscode.commands.executeCommand(
                'vscode.diff',
                git.toGitUri(uri, '~'),
                uri,
                `${path.basename(uri.fsPath)} (Working Tree)`,
                { preview: false, preserveFocus: true }
            );
        } catch (err) {
            log.appendLine(`  Failed to open diff for ${uri.fsPath}: ${err}`);
        }
    }
    for (const uri of untracked) {
        if (!keep.has(uri.fsPath)) continue;
        try {
            await vscode.window.showTextDocument(uri, { preview: false, preserveFocus: true });
        } catch (err) {
            log.appendLine(`  Failed to open ${uri.fsPath}: ${err}`);
        }
    }
}

function markReviewed() {
    const editor = vscode.window.activeTextEditor;
    if (!editor) {
        vscode.window.showInformationMessage('No active file to flag for review.');
        return;
    }
    fileDecProvider.addReview(editor.document.uri);
    log.appendLine(`Flagged for review: ${editor.document.uri.fsPath}`);
}

function clearReviewed() {
    const editor = vscode.window.activeTextEditor;
    if (!editor) {
        vscode.window.showInformationMessage('No active file to clear.');
        return;
    }
    fileDecProvider.removeReview(editor.document.uri);
    log.appendLine(`Cleared review flag: ${editor.document.uri.fsPath}`);
}

async function openReviewedFiles() {
    const uris = fileDecProvider.getReviewedUris().filter(uri => isOpenableFile(uri.fsPath));

    if (uris.length === 0) {
        vscode.window.showInformationMessage('No files flagged for review.');
        return;
    }

    log.appendLine(`Opening ${uris.length} review file(s)...`);
    for (const uri of uris) {
        try {
            await vscode.window.showTextDocument(uri, { preview: false, preserveFocus: true });
        } catch (err) {
            log.appendLine(`  Failed to open ${uri.fsPath}: ${err}`);
        }
    }
}

async function showChangedInTree(git: API) {
    const allUris: vscode.Uri[] = [];

    for (const repo of reposInWorkspace(git)) {
        const seen = new Set<string>();
        const changes = getAllChanges(repo);

        for (const change of changes) {
            if (isDeleted(change)) continue;
            const key = change.uri.fsPath;
            if (seen.has(key)) continue;
            seen.add(key);
            if (!fs.existsSync(key)) continue;
            allUris.push(change.uri);
        }
    }

    if (allUris.length === 0) {
        vscode.window.showInformationMessage('No changed files found.');
        return;
    }

    const urisToReveal = await confirmOpenFiles(allUris);
    if (!urisToReveal) return;

    // Collapse file tree
    log.appendLine('Collapsing file tree...');
    try {
        await vscode.commands.executeCommand('talon-filetree.collapseRoot');
    } catch {
        log.appendLine('talon-filetree.collapseRoot not available.');
    }

    await new Promise(resolve => setTimeout(resolve, 200));

    // Open each changed file and reveal it in the file tree
    log.appendLine(`Revealing ${urisToReveal.length} changed file(s) in file tree...`);
    for (const uri of urisToReveal) {
        try {
            await vscode.window.showTextDocument(uri, { preview: false, preserveFocus: false });
            await vscode.commands.executeCommand('talon-filetree.revealCurrentFile');
            // Small delay so the tree can process each reveal
            await new Promise(resolve => setTimeout(resolve, 50));
        } catch (err) {
            log.appendLine(`  Failed to reveal ${uri.fsPath}: ${err}`);
        }
    }
}

function setup(git: API, context: vscode.ExtensionContext) {
    log.appendLine(`Setup starting. ${git.repositories.length} repo(s) found.`);
    for (const repo of git.repositories) {
        log.appendLine(`  Repo: ${repo.rootUri.fsPath}`);
        const changes = getAllChanges(repo);
        log.appendLine(`    Total changes: ${changes.length}`);
        for (const c of changes) {
            log.appendLine(`      ${c.uri.fsPath} status=${c.status} isNew=${isNewFile(c)} isDel=${isDeleted(c)}`);
        }
    }

    // Register command
    context.subscriptions.push(
        vscode.commands.registerCommand('gitInlineChanges.openAllChanged', () => openAllChangedFiles(git)),
        vscode.commands.registerCommand('gitInlineChanges.openAllDiffs', () => openAllChangedDiffs(git)),
        vscode.commands.registerCommand('gitInlineChanges.showChangedInTree', () => showChangedInTree(git)),
        vscode.commands.registerCommand('gitInlineChanges.markReviewed', () => markReviewed()),
        vscode.commands.registerCommand('gitInlineChanges.clearReviewed', () => clearReviewed()),
        vscode.commands.registerCommand('gitInlineChanges.clearAllReviewed', () => fileDecProvider.clearAllReviews()),
        vscode.commands.registerCommand('gitInlineChanges.openReviewed', () => openReviewedFiles())
    );

    // File decoration provider
    fileDecProvider = new GitFileDecorationProvider(context.workspaceState);
    context.subscriptions.push(
        vscode.window.registerFileDecorationProvider(fileDecProvider)
    );

    // Update decorations when switching editors
    context.subscriptions.push(
        vscode.window.onDidChangeActiveTextEditor(editor => {
            if (editor) {
                log.appendLine(`Active editor changed: ${editor.document.uri.fsPath}`);
                updateLineDecorations(editor, git);
            }
        })
    );

    // Update on save
    context.subscriptions.push(
        vscode.workspace.onDidSaveTextDocument(doc => {
            const editor = vscode.window.activeTextEditor;
            if (editor && editor.document === doc) {
                log.appendLine(`File saved: ${doc.uri.fsPath}`);
                updateLineDecorations(editor, git);
            }
        })
    );

    // Update on text changes (debounced)
    context.subscriptions.push(
        vscode.workspace.onDidChangeTextDocument(e => {
            const editor = vscode.window.activeTextEditor;
            if (editor && editor.document === e.document) {
                if (debounceTimer) {
                    clearTimeout(debounceTimer);
                }
                debounceTimer = setTimeout(() => {
                    updateLineDecorations(editor, git);
                }, 500);
            }
        })
    );

    // Watch repos
    for (const repo of git.repositories) {
        watchRepo(repo, git, context);
    }

    // Initial decoration
    if (vscode.window.activeTextEditor) {
        log.appendLine(`Initial editor: ${vscode.window.activeTextEditor.document.uri.fsPath}`);
        updateLineDecorations(vscode.window.activeTextEditor, git);
    }

    // Refresh file decorations
    for (const repo of git.repositories) {
        fileDecProvider.refresh(repo);
    }

    log.appendLine('Setup complete.');
}

function watchRepo(repo: Repository, git: API, context: vscode.ExtensionContext) {
    context.subscriptions.push(
        repo.state.onDidChange(() => {
            fileDecProvider?.refresh(repo);
            const editor = vscode.window.activeTextEditor;
            if (editor) {
                updateLineDecorations(editor, git);
            }
        })
    );
}

async function updateLineDecorations(editor: vscode.TextEditor, git: API) {
    const config = vscode.workspace.getConfiguration('gitInlineChanges');
    if (!config.get<boolean>('showInlineHighlights', true)) {
        editor.setDecorations(changedLineDecoration, []);
        return;
    }

    const repo = git.getRepository(editor.document.uri);
    if (!repo) {
        // The built-in git extension only tracks repos at/under the open
        // workspace folder. If the file lives in a folder whose .git is in a
        // PARENT directory (e.g. you opened a subproject of a larger repo),
        // getRepository returns undefined. Fall back to the git CLI run from
        // the file's own directory, which resolves the enclosing repo.
        log.appendLine(`  No API repo for: ${editor.document.uri.fsPath} — trying CLI fallback`);
        await updateViaGitCli(editor, git);
        return;
    }

    const fileFsPath = editor.document.uri.fsPath;
    const relativePath = vscode.workspace.asRelativePath(editor.document.uri);
    log.appendLine(`  Checking: ${relativePath}`);

    // Check all change lists for this file
    const allChanges = getAllChanges(repo);
    const fileChange = allChanges.find(c => c.uri.fsPath === fileFsPath);

    if (fileChange) {
        log.appendLine(`  Found in change list, status=${fileChange.status}, isNew=${isNewFile(fileChange)}`);
    } else {
        log.appendLine(`  Not in any change list.`);
    }

    // If it's a new/untracked file, highlight everything
    if (fileChange && isNewFile(fileChange)) {
        const lineCount = editor.document.lineCount;
        log.appendLine(`  New file, highlighting all ${lineCount} lines.`);
        const ranges: vscode.Range[] = [];
        for (let i = 0; i < lineCount; i++) {
            ranges.push(editor.document.lineAt(i).range);
        }
        editor.setDecorations(changedLineDecoration, ranges);
        return;
    }

    // Try to get a diff
    try {
        const diff: string = await repo.diffWithHEAD(relativePath);
        log.appendLine(`  Diff length: ${diff ? diff.length : 0} chars`);

        const changedLines = parseChangedLines(diff);
        log.appendLine(`  Changed lines: ${changedLines.length}`);

        const ranges: vscode.Range[] = [];
        for (const lineNum of changedLines) {
            if (lineNum >= 0 && lineNum < editor.document.lineCount) {
                ranges.push(editor.document.lineAt(lineNum).range);
            }
        }

        editor.setDecorations(changedLineDecoration, ranges);
    } catch (err) {
        log.appendLine(`  diffWithHEAD failed: ${err}`);
        // If diff fails and file is known changed, highlight everything
        if (fileChange && !isDeleted(fileChange)) {
            log.appendLine(`  Fallback: highlighting all lines.`);
            const ranges: vscode.Range[] = [];
            for (let i = 0; i < editor.document.lineCount; i++) {
                ranges.push(editor.document.lineAt(i).range);
            }
            editor.setDecorations(changedLineDecoration, ranges);
        } else {
            editor.setDecorations(changedLineDecoration, []);
        }
    }
}

function highlightAllLines(editor: vscode.TextEditor) {
    const ranges: vscode.Range[] = [];
    for (let i = 0; i < editor.document.lineCount; i++) {
        ranges.push(editor.document.lineAt(i).range);
    }
    editor.setDecorations(changedLineDecoration, ranges);
}

// Fallback used when the VSCode git API has no repo for the file (the file's
// repo root is above the open workspace folder). Computes the diff with the
// git CLI run from the file's own directory.
async function updateViaGitCli(editor: vscode.TextEditor, git: API) {
    const fileFsPath = editor.document.uri.fsPath;
    const dir = path.dirname(fileFsPath);

    // Confirm an enclosing repo exists; bail quietly if not.
    try {
        await runGitInDir(git, dir, ['rev-parse', '--is-inside-work-tree']);
    } catch {
        log.appendLine(`  CLI fallback: not inside a git work tree.`);
        editor.setDecorations(changedLineDecoration, []);
        return;
    }

    // Untracked file (new): highlight everything.
    try {
        const untracked = await runGitInDir(git, dir, [
            'ls-files', '--others', '--exclude-standard', '-z', '--', fileFsPath,
        ]);
        if (splitGitPathOutput(untracked).length > 0) {
            log.appendLine(`  CLI fallback: untracked, highlighting all lines.`);
            highlightAllLines(editor);
            return;
        }
    } catch (err) {
        log.appendLine(`  CLI fallback: ls-files failed: ${err}`);
    }

    // Tracked: diff against HEAD (staged + unstaged), matching diffWithHEAD.
    try {
        const diff = await runGitInDir(git, dir, ['diff', 'HEAD', '--', fileFsPath]);
        const changedLines = parseChangedLines(diff);
        log.appendLine(`  CLI fallback: ${changedLines.length} changed line(s).`);
        const ranges: vscode.Range[] = [];
        for (const lineNum of changedLines) {
            if (lineNum >= 0 && lineNum < editor.document.lineCount) {
                ranges.push(editor.document.lineAt(lineNum).range);
            }
        }
        editor.setDecorations(changedLineDecoration, ranges);
    } catch (err) {
        log.appendLine(`  CLI fallback: diff failed: ${err}`);
        editor.setDecorations(changedLineDecoration, []);
    }
}

export function deactivate() {
    changedLineDecoration?.dispose();
    fileDecProvider?.dispose();
    if (debounceTimer) {
        clearTimeout(debounceTimer);
    }
}
