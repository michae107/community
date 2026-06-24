import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import type { Repository } from './typings/git';

// Status enum values
const STATUS_INDEX_DELETED = 2;
const STATUS_DELETED = 6;

export class GitFileDecorationProvider implements vscode.FileDecorationProvider {
    private readonly _onDidChangeFileDecorations = new vscode.EventEmitter<vscode.Uri | vscode.Uri[] | undefined>();
    readonly onDidChangeFileDecorations = this._onDidChangeFileDecorations.event;

    // Changed URIs keyed by repo root. Each refresh replaces only the entry for
    // the repo it was given, so refreshing one repo (e.g. a linked worktree the
    // git extension surfaces separately) never wipes another repo's badges.
    private changedUrisByRepo = new Map<string, Set<string>>();

    refresh(repo: Repository): void {
        const repoRoot = repo.rootUri.fsPath;
        const uris = new Set<string>();

        const allChanges = [
            ...repo.state.workingTreeChanges,
            ...(repo.state.untrackedChanges || []),
        ];

        for (const change of allChanges) {
            // Skip deleted files
            if (change.status === STATUS_INDEX_DELETED || change.status === STATUS_DELETED) {
                continue;
            }

            const filePath = change.uri.fsPath;

            // Skip files that don't exist on disk
            if (!fs.existsSync(filePath)) {
                continue;
            }

            // Add the file itself
            uris.add(vscode.Uri.file(filePath).toString());

            // Walk up and add every parent folder until we hit the repo root
            let dir = path.dirname(filePath);
            while (dir.length >= repoRoot.length) {
                uris.add(vscode.Uri.file(dir).toString());
                const parent = path.dirname(dir);
                if (parent === dir) break;
                dir = parent;
            }
        }

        this.changedUrisByRepo.set(repoRoot, uris);
        this._onDidChangeFileDecorations.fire(undefined);
    }

    private isChanged(uri: string): boolean {
        for (const uris of this.changedUrisByRepo.values()) {
            if (uris.has(uri)) {
                return true;
            }
        }
        return false;
    }

    provideFileDecoration(uri: vscode.Uri): vscode.FileDecoration | undefined {
        const config = vscode.workspace.getConfiguration('gitInlineChanges');
        if (!config.get<boolean>('showFileDecorations', true)) {
            return undefined;
        }

        if (!this.isChanged(uri.toString())) {
            return undefined;
        }

        const decoration = new vscode.FileDecoration(
            '\u2022', // bullet dot badge
            'Changed - git inline changes',
            new vscode.ThemeColor('gitInlineChanges.changedFileForeground')
        );
        decoration.propagate = true;
        return decoration;
    }

    dispose(): void {
        this._onDidChangeFileDecorations.dispose();
    }
}
