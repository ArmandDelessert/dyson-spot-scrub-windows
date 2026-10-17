"""Finds the texts of DySS Cockpit that are written in two languages side by side: the T("français", "english") calls of the
C# files and the {local:Tr Fr=…, En=…} markups of the XAML files.

A small scanner, not a regular expression: it skips comments and tells strings from code, reads regular, verbatim and
interpolated strings (with their holes, however nested), and puts the holes in the order .NET numbers them, which is the key
of FormattableString.Format at run time: $"{a} of {b:N0}" is "{0} of {1:N0}".
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path
from xml.sax.saxutils import unescape

ROOT = Path(__file__).resolve().parents[2]
ESCAPES = {'n': '\n', 'r': '\r', 't': '\t', '0': '\0', '\\': '\\', '"': '"', "'": "'", 'a': '\a', 'b': '\b', 'f': '\f', 'v': '\v'}


@dataclass
class Text:
    """A string argument: its value (an interpolated one with its holes as {0}, {1:format}) and where it is in the file."""
    value: str
    interpolated: bool
    start: int
    end: int
    holes: list[str] = field(default_factory=list)


@dataclass
class Call:
    path: str
    line: int
    french: Text
    english: Text
    at: int = 0  # where the call starts in the file: the index of its "T"


class Scanner:
    def __init__(self, source: str) -> None:
        self.s = source
        self.n = len(source)

    # ---- helpers -------------------------------------------------------------------------------------------
    def skip_space(self, i: int) -> int:
        while i < self.n:
            if self.s[i].isspace():
                i += 1
            elif self.s.startswith("//", i):
                i = self.s.find("\n", i)
                i = self.n if i < 0 else i
            elif self.s.startswith("/*", i):
                i = self.s.find("*/", i)
                i = self.n if i < 0 else i + 2
            else:
                break
        return i

    def string_literal(self, i: int) -> Text | None:
        """A string literal at i (maybe several joined by +); None when there is not one."""
        start = i
        parts: list[Text] = []
        while True:
            lit = self._one_literal(i)
            if lit is None:
                break
            parts.append(lit)
            i = self.skip_space(lit.end)
            if i < self.n and self.s[i] == '+':
                j = self.skip_space(i + 1)
                if self._one_literal(j) is not None:
                    i = j
                    continue
            break
        if not parts:
            return None
        if len(parts) == 1:
            return parts[0]
        # Several literals joined: renumber the holes of the interpolated ones.
        value, holes, interpolated = "", [], any(p.interpolated for p in parts)
        for p in parts:
            text = p.value if not interpolated or p.interpolated else p.value.replace("{", "{{").replace("}", "}}")
            if p.interpolated:
                text = re.sub(r"\{(\d+)", lambda m: "{" + str(int(m.group(1)) + len(holes)), text)
            value += text
            holes += p.holes
        return Text(value, interpolated, start, parts[-1].end, holes)

    def _one_literal(self, i: int) -> Text | None:
        s = self.s
        interpolated = verbatim = False
        j = i
        while j < self.n and s[j] in '$@' and j - i < 2:
            interpolated |= s[j] == '$'
            verbatim |= s[j] == '@'
            j += 1
        if j >= self.n or s[j] != '"' or s.startswith('"""', j):
            return None
        j += 1
        out: list[str] = []
        holes: list[str] = []
        while j < self.n:
            c = s[j]
            if c == '"':
                if verbatim and s.startswith('""', j):
                    out.append('"')
                    j += 2
                    continue
                return Text("".join(out), interpolated, i, j + 1, holes)
            if c == '\\' and not verbatim:
                e = s[j + 1]
                if e == 'u':
                    out.append(chr(int(s[j + 2:j + 6], 16)))
                    j += 6
                elif e == 'x':
                    m = re.match(r"[0-9a-fA-F]{1,4}", s[j + 2:])
                    out.append(chr(int(m.group(0), 16)))
                    j += 2 + len(m.group(0))
                else:
                    out.append(ESCAPES[e])
                    j += 2
                continue
            if interpolated and c == '{':
                if s.startswith('{{', j):
                    out.append('{{')
                    j += 2
                    continue
                j, expression, spec = self._hole(j + 1)
                holes.append(expression)
                out.append("{" + str(len(holes) - 1) + (":" + spec if spec else "") + "}")
                continue
            if interpolated and c == '}' and s.startswith('}}', j):
                out.append('}}')
                j += 2
                continue
            out.append(c)
            j += 1
        raise ValueError(f"unterminated string at {i}")

    def _hole(self, i: int) -> tuple[int, str, str]:
        """Reads a hole of an interpolated string from just after its '{'; returns (index after '}', expression, format)."""
        s = self.s
        depth = 0
        start = i
        spec_at = -1
        while i < self.n:
            c = s[i]
            if c in '"@$' and self._one_literal(i) is not None:
                i = self._one_literal(i).end
                continue
            if c == "'":
                i = s.index("'", i + 2 if s[i + 1] == '\\' else i + 1) + 1
                continue
            if c in '([{':
                depth += 1
            elif c in ')]':
                depth -= 1
            elif c == '}':
                if depth == 0:
                    break
                depth -= 1
            elif c == ':' and depth == 0 and spec_at < 0:
                spec_at = i
            i += 1
        expression = s[start:spec_at if spec_at >= 0 else i].strip()
        spec = s[spec_at + 1:i] if spec_at >= 0 else ""
        return i + 1, expression, spec


IDENT = re.compile(r"[A-Za-z0-9_]")


def calls_in(path: Path, relative: str) -> tuple[list[Call], list[tuple[str, int]]]:
    """The T(…, …) calls of a C# file with two string arguments, and the (file, line) of the ones with something else."""
    source = path.read_text(encoding="utf-8")
    sc = Scanner(source)
    found, odd = [], []
    i = 0
    while i < sc.n:
        c = source[i]
        if source.startswith("//", i) or source.startswith("/*", i):
            i = sc.skip_space(i)
            continue
        if c in '"@$' and sc._one_literal(i) is not None:
            i = sc._one_literal(i).end
            continue
        if c == "'":
            i = source.index("'", i + 2 if source[i + 1] == '\\' else i + 1) + 1
            continue
        if c in "Tt" and source[i:i + 2] in ("T(", "Tf") and (i == 0 or not (IDENT.match(source[i - 1]) or source[i - 1] == '.')
                                                               or source[max(0, i - 12):i] == "Translation."):
            m = re.match(r"Tf?\(", source[i:])
            if m:
                a = sc.skip_space(i + m.end())
                french = sc.string_literal(a)
                if french is not None:
                    b = sc.skip_space(french.end)
                    if b < sc.n and source[b] == ',':
                        english = sc.string_literal(sc.skip_space(b + 1))
                        if english is not None:
                            found.append(Call(relative, source.count("\n", 0, i) + 1, french, english, i))
                            i = english.end
                            continue
                odd.append((relative, source.count("\n", 0, i) + 1))
        i += 1
    return found, odd


TR = re.compile(r"\{local:Tr\s+(.*?)\}", re.S)
TR_ARG = re.compile(r"""(Fr|En)\s*=\s*(?:"((?:[^"\\]|\\.)*)"|'((?:[^'\\]|\\.)*)'|([^,'"}]+))""")


def tr_in(path: Path, relative: str) -> list[tuple[str, int, str, str]]:
    """The {local:Tr Fr=…, En=…} markups of a XAML file: (file, line, French, English), as the XAML parser then the markup reads them."""
    source = path.read_text(encoding="utf-8")
    found = []
    for m in re.finditer(r"""=(["'])(\{local:Tr\s.*?\})\1(?=[\s/>])""", source, re.S):
        raw = unescape(m.group(2), {"&apos;": "'", "&quot;": '"'})
        args = {}
        for a in TR_ARG.finditer(raw[len("{local:Tr"):-1]):
            quoted = a.group(2) if a.group(2) is not None else a.group(3)
            value = a.group(4).strip() if quoted is None else re.sub(r"\\(.)", r"\1", quoted)
            args[a.group(1)] = value
        found.append((relative, source.count("\n", 0, m.start()) + 1, args.get("Fr", ""), args.get("En", "")))
    return found
