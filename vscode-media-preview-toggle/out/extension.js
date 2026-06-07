"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.activate = activate;
exports.deactivate = deactivate;
const path = __importStar(require("path"));
const vscode = __importStar(require("vscode"));
const VIEW_TYPE = 'mediaPreviewToggle.audioPreview';
let lastActivePanel;
class AudioDocument {
    constructor(uri) {
        this.uri = uri;
    }
    dispose() { }
}
class AudioPreviewProvider {
    constructor(extensionUri) {
        this.extensionUri = extensionUri;
    }
    async openCustomDocument(uri) {
        return new AudioDocument(uri);
    }
    async resolveCustomEditor(document, panel) {
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
function renderHtml(webview, fileUri) {
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
function activate(context) {
    context.subscriptions.push(vscode.window.registerCustomEditorProvider(VIEW_TYPE, new AudioPreviewProvider(context.extensionUri), { webviewOptions: { retainContextWhenHidden: true } }), vscode.commands.registerCommand('mediaPreviewToggle.togglePlay', () => {
        lastActivePanel?.webview.postMessage({ type: 'togglePlay' });
    }));
}
function deactivate() { }
//# sourceMappingURL=extension.js.map