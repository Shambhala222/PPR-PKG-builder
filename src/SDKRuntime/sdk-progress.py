"""Decode the publisher's streaming console ruler without waiting for a newline."""
import re

class ProgressDecoder:
    def __init__(self, emit, progress_text='Building PKG'):
        self.emit = emit
        self.progress_text = progress_text
        self.escape = ''
        self.line = ''
        self.width = 0
        self.count = 0
        self.last_value = -1

    def feed(self, text):
        for char in text:
            if self.escape:
                self.escape += char
                if len(self.escape) == 2 and char != '[':
                    self.escape = ''
                elif len(self.escape) > 2 and '@' <= char <= '~':
                    self.escape = ''
                continue
            if char == '\x1b':
                self.escape = char
                continue
            if char in '\r\n':
                line = self.line.strip()
                if re.fullmatch(r'\|[_.|]+\|', line):
                    self.width = len(line)
                    self.count = 0
                    self.last_value = -1
                    self.report(0)
                elif line and not re.fullmatch(r'[=\s]+', line) and not re.fullmatch(r'0\s+20\s+40\s+60\s+80\s+100', line):
                    matches = re.findall(r'(?<!\d)(\d{1,3}(?:\.\d+)?)\s*%', line)
                    if matches:
                        self.report(min(100, float(matches[-1])))
                    else:
                        self.emit('log', text=line[:4000])
                self.line = ''
            else:
                self.line += char
                if char == '=' and self.width and re.fullmatch(r'=+', self.line):
                    self.count += 1
                    self.report(min(100, self.count * 100 / self.width))
                if len(self.line) > 8192:
                    self.emit('log', text=self.line[:8192])
                    self.line = ''

    def report(self, value):
        if value != self.last_value:
            self.last_value = value
            self.emit('progress', value=value, text=self.progress_text)

    def finish(self):
        self.feed('\n')
