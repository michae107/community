import * as path from 'path';
import * as vscode from 'vscode';

const VIEW_TYPE = 'mediaPreviewToggle.audioPreview';

let lastActivePanel: vscode.WebviewPanel | undefined;

class AudioDocument implements vscode.CustomDocument {
    constructor(public readonly uri: vscode.Uri) {}
    dispose() {}
}

class AudioPreviewProvider implements vscode.CustomReadonlyEditorProvider<AudioDocument> {
    constructor(private readonly extensionUri: vscode.Uri) {}

    async openCustomDocument(uri: vscode.Uri): Promise<AudioDocument> {
        return new AudioDocument(uri);
    }

    async resolveCustomEditor(document: AudioDocument, panel: vscode.WebviewPanel): Promise<void> {
        panel.webview.options = {
            enableScripts: true,
            localResourceRoots: [
                vscode.Uri.file(path.dirname(document.uri.fsPath)),
                this.extensionUri,
            ],
        };
        panel.webview.html = renderHtml(panel.webview, document.uri);

        if (panel.active) {
            lastActivePanel = panel;
        }
        panel.onDidChangeViewState(() => {
            if (panel.active) {
                lastActivePanel = panel;
            }
        });
        panel.onDidDispose(() => {
            if (lastActivePanel === panel) {
                lastActivePanel = undefined;
            }
        });
    }
}

function renderHtml(webview: vscode.Webview, fileUri: vscode.Uri): string {
    const src = webview.asWebviewUri(fileUri).toString();
    const csp = `default-src 'none'; media-src ${webview.cspSource}; style-src 'unsafe-inline'; script-src 'unsafe-inline';`;
    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="${csp}">
<style>
  body { display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; }
  audio { width: min(640px, 90%); }
</style>
</head>
<body>
<audio controls src="${src}"></audio>
<script>
  const audio = document.querySelector('audio');
  audio.focus();
  window.addEventListener('message', (e) => {
    if (e.data && e.data.type === 'togglePlay') {
      if (audio.paused) {
        audio.play().catch(() => {});
      } else {
        audio.pause();
      }
    }
  });
</script>
</body>
</html>`;
}

export function activate(context: vscode.ExtensionContext) {
    context.subscriptions.push(
        vscode.window.registerCustomEditorProvider(
            VIEW_TYPE,
            new AudioPreviewProvider(context.extensionUri),
            { webviewOptions: { retainContextWhenHidden: true } },
        ),
        vscode.commands.registerCommand('mediaPreviewToggle.togglePlay', () => {
            lastActivePanel?.webview.postMessage({ type: 'togglePlay' });
        }),
    );
}

export function deactivate() {}
