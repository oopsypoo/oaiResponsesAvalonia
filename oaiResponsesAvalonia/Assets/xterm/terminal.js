(() => {
  const terminal = new Terminal({
    cursorBlink: true,
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
  let historyIndex = -1;
  let historyDraft = '';
  const history = [];
  let prompt = '~/ $ ';
  const commandCompletions = ['help', 'pwd', 'ls', 'cd', 'cat', 'grep', 'connect', 'disconnect', 'sessions', 'tables', 'describe', 'messages', 'db', 'clear', 'quit', 'exit'];
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
  const openPager = (text, mode) => {
    const width = Math.max(1, terminal.cols - 1);
    const lines = [];
    for (const sourceLine of text.replace(/\r/g, '').split('\n')) {
      const characters = Array.from(sourceLine);
      if (characters.length === 0) lines.push('');
      else for (let i = 0; i < characters.length; i += width) lines.push(characters.slice(i, i + width).join(''));
    }
    pager = { lines, mode, page: 0 };
    terminal.write('\u001b[?1049h');
    renderPager();
  };
  const closePager = () => {
    pager = null;
    terminal.write('\u001b[?1049l\r\n' + coloredPrompt());
    busy = false;
    terminal.focus();
  };

  const getCompletions = () => {
    const trimmedLine = line.trimEnd();
    const parts = trimmedLine ? trimmedLine.split(/\s+/) : [];
    const hasTrailingSpace = trimmedLine.length < line.length;
    const current = hasTrailingSpace ? '' : (parts.pop() || '');
    const completed = parts;
    let candidates = [];

    if (completed.length === 0) {
      candidates = commandCompletions;
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
      terminal.write('\r\n' + candidates.join('    ') + '\r\n');
      redrawLine();
    }
    terminal.focus();
  };

  window.receiveResult = result => {
    if (typeof result.prompt === 'string' && result.prompt.length > 0) prompt = result.prompt;
    if (typeof result.output === 'string' && result.output.length > 0) {
      if (result.pagerMode && !result.closeWindow) {
        openPager(result.output, result.pagerMode);
        return;
      }
      const outputColor = result.kind === 'error' ? ansi.red : result.kind === 'success' ? ansi.green : '';
      terminal.write(outputColor + result.output.replace(/\r?\n/g, '\r\n') + (outputColor ? ansi.reset : ''));
      if (!result.output.endsWith('\n')) terminal.write('\r\n');
    }
    busy = false;
    if (result.closeWindow) {
      if (!postMessageToHost(JSON.stringify({ type: 'close' }))) writeBridgeError();
      return;
    }
    terminal.write(coloredPrompt());
    terminal.focus();
  };
})();
