(() => {
  const terminal = new Terminal({
    cursorBlink: true,
    lineHeight: 1.1,
    fontFamily: 'Cascadia Mono, Consolas, monospace',
    fontSize: 14,
    theme: {
      background: '#111111',
      foreground: '#e5e5e5',
      cursor: '#e5e5e5',
      black: '#1e2127', red: '#e06c75', green: '#98c379', yellow: '#e5c07b',
      blue: '#61afef', magenta: '#c678dd', cyan: '#56b6c2', white: '#abb2bf',
      brightBlack: '#5c6370', brightRed: '#e06c75', brightGreen: '#98c379', brightYellow: '#e5c07b',
      brightBlue: '#61afef', brightMagenta: '#c678dd', brightCyan: '#56b6c2', brightWhite: '#ffffff'
    }
  });
  const terminalContainer = document.getElementById('terminal');
  terminal.open(terminalContainer);

  // WebView2 exposes chrome.webview.postMessage, while Avalonia's
  // WebKit-based Linux WebView exposes invokeCSharpAction instead.
  const postMessageToHost = message => {
    const webview = window.chrome && window.chrome.webview;
    if (webview && typeof webview.postMessage === 'function') {
      webview.postMessage(message);
      return true;
    }
    if (typeof window.invokeCSharpAction === 'function') {
      window.invokeCSharpAction(message);
      return true;
    }
    return false;
  };
  const writeBridgeError = () => terminal.write('\r\nTerminal host bridge is unavailable.\r\n');

  // xterm.js defaults to 80 columns by 24 rows. Keep its grid fitted to
  // the WebView viewport as the Avalonia window is resized.
  // Load image rendering lazily on first entry to the image context. Keep the addon
  // alive until this terminal is closed so inline images remain available in scrollback.
  let imageContextActive = false;
  let imageAddon = null;
  let imageAddonLoadPromise = null;
  const ensureImageAddon = () => {
    if (imageAddon) return Promise.resolve();
    if (imageAddonLoadPromise) return imageAddonLoadPromise;

    return imageAddonLoadPromise = (async () => {
      if (typeof window.XtermImageAddon !== 'function') {
        await new Promise((resolve, reject) => {
          const script = document.createElement('script');
          script.src = './image-addon.bundle.js';
          script.onload = resolve;
          script.onerror = () => reject(new Error('Could not load image addon bundle.'));
          document.head.appendChild(script);
        });
      }

      if (!imageContextActive || imageAddon) return;
      if (typeof window.XtermImageAddon !== 'function')
        throw new Error('Image addon bundle did not expose XtermImageAddon.');

      const addon = new window.XtermImageAddon();
      terminal.loadAddon(addon);
      imageAddon = addon;
    })().catch(error => {
      terminal.write(`\r\nImage preview support could not be loaded: ${error.message}\r\n`);
    }).finally(() => {
      imageAddonLoadPromise = null;
    });
  };
  const syncImageAddonContext = () => {
    const isImageContext = prompt.startsWith('img:');
    if (isImageContext === imageContextActive) return;

    imageContextActive = isImageContext;
    if (imageContextActive) {
      ensureImageAddon();
    }
  };

  const fitTerminal = () => {
    const screen = terminalContainer.querySelector('.xterm-screen');
    if (!screen || terminal.cols < 1 || terminal.rows < 1) return;

    const styles = getComputedStyle(terminalContainer);
    const availableWidth = terminalContainer.clientWidth -
      parseFloat(styles.paddingLeft) - parseFloat(styles.paddingRight);
    const availableHeight = terminalContainer.clientHeight -
      parseFloat(styles.paddingTop) - parseFloat(styles.paddingBottom);
    const screenBounds = screen.getBoundingClientRect();
    const cellWidth = screenBounds.width / terminal.cols;
    const cellHeight = screenBounds.height / terminal.rows;

    if (cellWidth <= 0 || cellHeight <= 0) return;

    const cols = Math.max(2, Math.floor(availableWidth / cellWidth));
    const rows = Math.max(1, Math.floor(availableHeight / cellHeight));
    if (cols !== terminal.cols || rows !== terminal.rows) {
      terminal.resize(cols, rows);
    }
  };

  const resizeObserver = new ResizeObserver(() => requestAnimationFrame(fitTerminal));
  resizeObserver.observe(terminalContainer);
  requestAnimationFrame(fitTerminal);

  let line = '';
  let cursor = 0;
  let busy = false;
  let pager = null;
  const pendingWorkspaceNotifications = [];
  let historyIndex = -1;
  let historyDraft = '';
  const history = [];
  let prompt = '~/ $ ';
  const filesystemCommandCompletions = ['help', 'pwd', 'ls', 'cd', 'cat', 'grep', 'connect', 'img', 'clear', 'quit', 'exit'];
  const databaseCommandCompletions = ['help', 'pwd', 'ls', 'cd', 'grep', 'find', 'disconnect', 'sessions', 'tables', 'describe', 'messages', 'show', 'columns', 'db', 'img', 'clear', 'quit', 'exit'];
  const imageCommandCompletions = ['help', 'pwd', 'ls', 'cd', 'info', 'preview', 'app', 'database', 'workspace', 'clear', 'quit', 'exit'];
  const ansi = { reset: '\u001b[0m', cyan: '\u001b[36m', green: '\u001b[32m', yellow: '\u001b[33m', red: '\u001b[31m' };
  const coloredPrompt = () => ansi.cyan + prompt + ansi.reset;
  const redrawLine = () => {
    const suffixLength = Array.from(line).slice(cursor).length;
    terminal.write('\r\u001b[2K' + coloredPrompt() + line + (suffixLength ? `\u001b[${suffixLength}D` : ''));
  };
  terminal.write(coloredPrompt());
  terminal.focus();

  const renderPager = () => {
    if (!pager) return;
    const pageSize = Math.max(1, terminal.rows - 1);
    const pageCount = Math.max(1, Math.ceil(pager.lines.length / pageSize));
    pager.page = Math.max(0, Math.min(pager.page, pageCount - 1));
    const pageLines = pager.lines.slice(pager.page * pageSize, (pager.page + 1) * pageSize);
    const endOfOutput = pager.page >= pageCount - 1;
    const hint = pager.mode === 'less'
      ? 'Space/Down: next  Up/b: back  g/G: start/end  q: quit'
      : 'Space/Down: next  q: quit';
    const status = endOfOutput
      ? `-- ${pager.page + 1}/${pageCount} (END) -- ${hint}`
      : `-- ${pager.page + 1}/${pageCount} -- ${hint}`;
    terminal.write('\u001b[2J\u001b[H' + pageLines.join('\r\n') + '\r\n' + status);
  };
  // Wrap by visible characters, not raw string length: ANSI SGR color/style sequences are invisible.
  // Carry the active SGR style onto wrapped segments and reset it at each segment boundary.
  const wrapAnsiLine = (sourceLine, width) => {
    const segments = [];
    let segment = '';
    let visibleLength = 0;
    let activeSgr = '';
    for (let index = 0; index < sourceLine.length;) {
      if (sourceLine[index] === '\u001b' && sourceLine[index + 1] === '[') {
        const end = sourceLine.indexOf('m', index + 2);
        if (end >= 0) {
          const sequence = sourceLine.slice(index, end + 1);
          segment += sequence;
          const parameters = sequence.slice(2, -1).split(';').map(value => value || '0');
          const resetIndex = parameters.lastIndexOf('0');
          if (resetIndex >= 0) {
            const remainingStyles = parameters.slice(resetIndex + 1);
            activeSgr = remainingStyles.length ? `\u001b[${remainingStyles.join(';')}m` : '';
          } else {
            activeSgr += sequence;
          }
          index = end + 1;
          continue;
        }
      }

      const character = String.fromCodePoint(sourceLine.codePointAt(index));
      segment += character;
      visibleLength++;
      index += character.length;
      const hasVisibleRemainder = sourceLine.slice(index).replace(/\u001b\[[0-9;]*m/g, '').length > 0;
      if (visibleLength >= width && hasVisibleRemainder) {
        segments.push(segment + (activeSgr ? '\u001b[0m' : ''));
        segment = activeSgr;
        visibleLength = 0;
      }
    }
    segments.push(segment);
    return segments;
  };

  const openPager = (text, mode) => {
    const width = Math.max(1, terminal.cols - 1);
    const lines = [];
    for (const sourceLine of text.replace(/\r/g, '').split('\n')) {
      lines.push(...wrapAnsiLine(sourceLine, width));
    }
    pager = { lines, mode, page: 0 };
    terminal.write('\u001b[?1049h');
    renderPager();
  };
  const closePager = () => {
    pager = null;
    terminal.write('\u001b[?1049l\r\n');
    busy = false;
    if (pendingWorkspaceNotifications.length > 0) flushWorkspaceNotifications();
    else terminal.write(coloredPrompt());
    terminal.focus();
  };

  const applyWorkspaceNotification = change => {
    if (typeof change.prompt === 'string' && change.prompt.length > 0) {
      prompt = change.prompt;
      syncImageAddonContext();
    }
    const pagerWasOpen = !!pager;
    if (change.cancelActiveWorkspacePager && pager) {
      pager = null;
      terminal.write('\u001b[?1049l\r\n');
      busy = false;
    }
    if (!pagerWasOpen && line.length > 0) terminal.write('\r\u001b[2K');
    terminal.write('\r\n' + ansi.yellow + change.message + ansi.reset + '\r\n');
    redrawLine();
    terminal.focus();
  };
  const flushWorkspaceNotifications = () => {
    while (pendingWorkspaceNotifications.length > 0 && !busy && !pager)
      applyWorkspaceNotification(pendingWorkspaceNotifications.shift());
  };

  const getCompletions = () => {
    const trimmedLine = line.trimEnd();
    const parts = trimmedLine ? trimmedLine.split(/\s+/) : [];
    const hasTrailingSpace = trimmedLine.length < line.length;
    const current = hasTrailingSpace ? '' : (parts.pop() || '');
    const completed = parts;
    let candidates = [];

    if (completed.length === 0) {
      candidates = prompt.startsWith('db[') ? databaseCommandCompletions : prompt.startsWith('img:') ? imageCommandCompletions : filesystemCommandCompletions;
    } else if (completed.length === 1 && completed[0] === 'db') {
      candidates = ['help', 'ls', 'cd', 'pwd', 'show', 'columns'];
    } else if (completed[completed.length - 1] === '|') {
      candidates = ['more', 'less'];
    } else if (completed.length === 1 && completed[0] === 'sessions') {
      candidates = ['list', 'show'];
    } else if (completed.length === 1 && completed[0] === 'messages') {
      candidates = ['show'];
    }

    return candidates.filter(candidate => candidate.startsWith(current || ''));
  };

  terminal.onData(data => {
    if (pager) {
      if (data === 'q' || data === '\u001b' || data === '\u001b[3~') {
        closePager();
        return;
      }
      if (data === ' ' || data === '\u001b[B' || data === 'j' || data === '\u001b[6~') {
        pager.page++;
        renderPager();
        return;
      }
      if (pager.mode === 'less') {
        if (data === '\u001b[A' || data === 'b' || data === '\u001b[5~') pager.page--;
        else if (data === 'g') pager.page = 0;
        else if (data === 'G') pager.page = Math.ceil(pager.lines.length / Math.max(1, terminal.rows - 1)) - 1;
        else return;
        renderPager();
      }
      return;
    }
    if (busy) return;

    if (data === '\u001b[A' || data === '\u001b[B') {
      if (history.length === 0) return;
      if (data === '\u001b[A') {
        if (historyIndex === -1) historyDraft = line;
        historyIndex = Math.min(historyIndex + 1, history.length - 1);
        line = history[history.length - 1 - historyIndex];
      } else if (historyIndex >= 0) {
        historyIndex--;
        line = historyIndex < 0 ? historyDraft : history[history.length - 1 - historyIndex];
      }
      cursor = Array.from(line).length;
      redrawLine();
      return;
    }

    if (data === '\u001b[D' || data === '\u001b[C') {
      const characters = Array.from(line);
      cursor = Math.max(0, Math.min(characters.length, cursor + (data === '\u001b[D' ? -1 : 1)));
      redrawLine();
      return;
    }

    if (data === '\u001b[H' || data === '\u001b[1~' || data === '\u0001') {
      cursor = 0;
      redrawLine();
      return;
    }

    if (data === '\u001b[F' || data === '\u001b[4~' || data === '\u0005') {
      cursor = Array.from(line).length;
      redrawLine();
      return;
    }

    if (data === '\u001b[3~') {
      const characters = Array.from(line);
      if (cursor < characters.length) {
        characters.splice(cursor, 1);
        line = characters.join('');
        redrawLine();
      }
      return;
    }

    if (data === '\t') {
      if (cursor !== Array.from(line).length) return;
      const candidates = getCompletions();
      if (candidates.length === 1) {
        const lastSpace = line.lastIndexOf(' ');
        line = line.slice(0, lastSpace + 1) + candidates[0] + ' ';
        cursor = Array.from(line).length;
        redrawLine();
      } else if (candidates.length > 1) {
        terminal.write('\r\n' + candidates.join('    ') + '\r\n');
        redrawLine();
      } else {
        if (!postMessageToHost(JSON.stringify({ type: 'complete', line }))) {
          writeBridgeError();
          redrawLine();
        }
      }
      return;
    }

    if (data === '\r' || data === '\n') {
      terminal.write('\r\n');
      const submittedLine = line;
      if (submittedLine.trim()) {
        if (history[history.length - 1] !== submittedLine) history.push(submittedLine);
        if (history.length > 100) history.shift();
      }
      historyIndex = -1;
      historyDraft = '';
      cursor = 0;
      busy = true;
      line = '';
      if (!postMessageToHost(JSON.stringify({ type: 'command', line: submittedLine }))) {
        busy = false;
        writeBridgeError();
        terminal.write(coloredPrompt());
        terminal.focus();
      }
      return;
    }

    if (data === '\u007f' || data === '\b') {
      const characters = Array.from(line);
      if (cursor > 0) {
        characters.splice(cursor - 1, 1);
        line = characters.join('');
        cursor--;
        redrawLine();
      }
      return;
    }

    if (data === '\u0003') {
      line = '';
      cursor = 0;
      historyIndex = -1;
      terminal.write('^C\r\n' + coloredPrompt());
      return;
    }

    if ([...data].every(character => character >= ' ' && character !== '\u007f')) {
      const characters = Array.from(line);
      const inserted = Array.from(data);
      characters.splice(cursor, 0, ...inserted);
      line = characters.join('');
      cursor += inserted.length;
      redrawLine();
    }
  });

  window.receiveCompletions = result => {
    const candidates = Array.isArray(result.candidates) ? result.candidates : [];
    if (candidates.length === 1) {
      const completed = candidates[0];
      line = line.slice(0, result.replaceFrom) + completed;
      if (!completed.endsWith('/') && !completed.endsWith('\\')) line += ' ';
      cursor = Array.from(line).length;
      redrawLine();
    } else if (candidates.length > 1) {
      const candidateCharacters = candidates.map(candidate => Array.from(candidate));
      const firstCandidate = candidateCharacters[0];
      let commonLength = 0;
      while (commonLength < firstCandidate.length &&
        candidateCharacters.every(candidate => candidate[commonLength] === firstCandidate[commonLength])) {
        commonLength++;
      }

      const commonPrefix = firstCandidate.slice(0, commonLength).join('');
      const currentToken = line.slice(result.replaceFrom);
      if (commonPrefix.startsWith(currentToken) && commonLength > Array.from(currentToken).length) {
        line = line.slice(0, result.replaceFrom) + commonPrefix;
        cursor = Array.from(line).length;
        redrawLine();
      } else {
        terminal.write('\r\n' + candidates.join('    ') + '\r\n');
        redrawLine();
      }
    }
    terminal.focus();
  };

  window.workspaceConfigurationChanged = change => {
    if (!change || typeof change.message !== 'string') return;
    if (pager && change.cancelActiveWorkspacePager) {
      applyWorkspaceNotification(change);
      return;
    }
    if (busy || pager) {
      pendingWorkspaceNotifications.push(change);
      return;
    }
    applyWorkspaceNotification(change);
  };

  const normalizePreviewImage = async (preview, widthCells, heightCells) => {
    const screen = terminalContainer.querySelector('.xterm-screen');
    const bounds = screen?.getBoundingClientRect();
    const cellWidth = bounds && terminal.cols > 0 ? bounds.width / terminal.cols : 8;
    const cellHeight = bounds && terminal.rows > 0 ? bounds.height / terminal.rows : 16;
    const canvas = document.createElement('canvas');
    canvas.width = Math.max(1, Math.round(widthCells * cellWidth));
    canvas.height = Math.max(1, Math.round(heightCells * cellHeight));

    const image = new Image();
    await new Promise((resolve, reject) => {
      image.onload = resolve;
      image.onerror = () => reject(new Error(`Could not decode preview image: ${preview.fileName}`));
      image.src = `data:image/png;base64,${preview.base64}`;
    });

    const context = canvas.getContext('2d');
    const scale = Math.min(canvas.width / image.naturalWidth, canvas.height / image.naturalHeight);
    const drawWidth = image.naturalWidth * scale;
    const drawHeight = image.naturalHeight * scale;
    context.drawImage(image, (canvas.width - drawWidth) / 2, (canvas.height - drawHeight) / 2, drawWidth, drawHeight);
    return { ...preview, base64: canvas.toDataURL('image/png').split(',')[1] };
  };

  const renderImagePreviewGrid = async (previews, requestedWidth = 8, requestedHeight = 4) => {
    const maxImageWidth = Math.max(1, requestedWidth);
    const maxImageHeight = Math.max(1, requestedHeight);
    const gap = 2;
    const imageWidth = Math.min(maxImageWidth, Math.max(1, terminal.cols - 1));
    const imageHeight = Math.max(1, Math.round(maxImageHeight * imageWidth / maxImageWidth));
    const tileColumns = Math.max(1, Math.floor((terminal.cols - 1 + gap) / (imageWidth + gap)));
    const fitCaption = value => value.length > imageWidth ? value.slice(0, imageWidth) : value;
    const captionsFor = fileName => {
      const dot = fileName.lastIndexOf('.');
      const hasExtension = dot > 0;
      const stem = hasExtension ? fileName.slice(0, dot) : fileName;
      const extension = hasExtension ? fileName.slice(dot) : '';
      const shortStem = stem.length > imageWidth
        ? imageWidth >= 8
          ? `${stem.slice(0, 3)}~${stem.slice(-4)}`
          : `${stem.slice(0, Math.max(1, imageWidth - 1))}~`
        : stem;
      return [fitCaption(shortStem), fitCaption(extension)];
    };
    const writeCaptionLine = (row, lineIndex) => row.map((preview, index) => {
      const caption = captionsFor(preview.fileName)[lineIndex].padEnd(imageWidth, ' ');
      return caption + (index < row.length - 1 ? ' '.repeat(gap) : '');
    }).join('');

    const normalizedPreviews = await Promise.all(previews.map(preview => normalizePreviewImage(preview, imageWidth, imageHeight)));
    for (let offset = 0; offset < normalizedPreviews.length; offset += tileColumns) {
      const row = normalizedPreviews.slice(offset, offset + tileColumns);
      terminal.write(writeCaptionLine(row, 0) + '\r\n' + writeCaptionLine(row, 1) + '\r\n');
      row.forEach((preview, index) => {
        if (index > 0) {
          const column = index * (imageWidth + gap) + 1;
          terminal.write(`\x1b[${imageHeight - 1}A\x1b[${column}G`);
        }
        const base64 = preview.base64;
        const padding = base64.endsWith('==') ? 2 : base64.endsWith('=') ? 1 : 0;
        const imageSize = Math.floor(base64.length * 3 / 4) - padding;
        terminal.write(`\x1b]1337;File=inline=1;size=${imageSize};width=${imageWidth};height=${imageHeight};preserveAspectRatio=0:${base64}\x07`);
      });
      terminal.write('\r\n');
    }
  };

  window.receiveResult = async result => {
    if (typeof result.prompt === 'string' && result.prompt.length > 0) {
      prompt = result.prompt;
      syncImageAddonContext();
    }
    if (!result.closeWindow && pendingWorkspaceNotifications.some(change => change.cancelActiveWorkspacePager)) {
      busy = false;
      flushWorkspaceNotifications();
      return;
    }
    if (typeof result.output === 'string' && result.output.length > 0) {
      if (result.pagerMode && !result.closeWindow) {
        openPager(result.output, result.pagerMode);
        return;
      }
      const outputColor = result.kind === 'error' ? ansi.red : result.kind === 'success' ? ansi.green : '';
      terminal.write(outputColor + result.output.replace(/\r?\n/g, '\r\n') + (outputColor ? ansi.reset : ''));
      if (!result.output.endsWith('\n')) terminal.write('\r\n');
    }
    if (Array.isArray(result.imagePreviews) && result.imagePreviews.length > 0) {
      await ensureImageAddon();
      if (!imageContextActive || !imageAddon) {
        terminal.write('\r\nInline image preview is unavailable because the image addon did not load.\r\n');
      } else {
        await renderImagePreviewGrid(result.imagePreviews, result.previewWidth || 8, result.previewHeight || 4);
      }
    }
    busy = false;
    if (result.closeWindow) {
      if (!postMessageToHost(JSON.stringify({ type: 'close' }))) writeBridgeError();
      return;
    }
    if (pendingWorkspaceNotifications.length > 0) {
      flushWorkspaceNotifications();
      terminal.focus();
      return;
    }
    terminal.write(coloredPrompt());
    terminal.focus();
  };
})();
