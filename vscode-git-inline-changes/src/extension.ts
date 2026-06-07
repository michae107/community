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

function splitGitPathOutput(output: string): string[] {
    return output.split('\0').filter(Boolean);
}

function runGit(git: API, repo: Repository, args: string[]): Promise<string> {
    const gitPath = git.git.path || 'git';
    return new Promise((resolve, reject) => {
        cp.execFile(
            gitPath,
            ['-C', repo.rootUri.fsPath, ...args],
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

async function getUnstagedFileUris(git: API, repo: Repository): Promise<vscode.Uri[]> {
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
    const uris: vscode.Uri[] = [];
    const relativePaths = [
        ...splitGitPathOutput(trackedOutput),
        ...splitGitPathOutput(untrackedOutput),
    ];

    for (const relativePath of relativePaths) {
        const filePath = path.join(repo.rootUri.fsPath, relativePath);
        if (seen.has(filePath)) continue;
        seen.add(filePath);

        if (!isOpenableFile(filePath)) {
            log.appendLine(`  Skipping non-file or missing path: ${filePath}`);
            continue;
        }

        uris.push(vscode.Uri.file(filePath));
    }

    return uris;
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

    for (const repo of git.repositories) {
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

    log.appendLine(`Opening ${allUris.length} changed file(s)...`);
    for (const uri of allUris) {
        try {
            await vscode.window.showTextDocument(uri, { preview: false, preserveFocus: true });
        } catch (err) {
            log.appendLine(`  Failed to open ${uri.fsPath}: ${err}`);
        }
    }
}

async function showChangedInTree(git: API) {
    const allUris: vscode.Uri[] = [];

    for (const repo of git.repositories) {
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

    // Collapse file tree
    log.appendLine('Collapsing file tree...');
    try {
        await vscode.commands.executeCommand('talon-filetree.collapseRoot');
    } catch {
        log.appendLine('talon-filetree.collapseRoot not available.');
    }

    await new Promise(resolve => setTimeout(resolve, 200));

    // Open each changed file and reveal it in the file tree
    log.appendLine(`Revealing ${allUris.length} changed file(s) in file tree...`);
    for (const uri of allUris) {
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
        vscode.commands.registerCommand('gitInlineChanges.showChangedInTree', () => showChangedInTree(git))
    );

    // File decoration provider
    fileDecProvider = new GitFileDecorationProvider();
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
        log.appendLine(`  No repo found for: ${editor.document.uri.fsPath}`);
        editor.setDecorations(changedLineDecoration, []);
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

export function deactivate() {
    changedLineDecoration?.dispose();
    fileDecProvider?.dispose();
    if (debounceTimer) {
        clearTimeout(debounceTimer);
    }
}
