"""The texts of DySS Cockpit and their translations.

Every text is written once in the code, in French and in English side by side: T("français", "english") in C#,
{local:Tr Fr=…, En=…} in XAML. The other languages are in src/DyssCockpit.Core/Translations/{tag}.json, a table from the
English wording to its translation, read by Translation.cs. A text a language lacks is shown in English.

Usage (from the repository root):
    python tools/translations/extract.py                  how many texts there are, and how far each language is
    python tools/translations/extract.py --check          the same, and fail on a translation that cannot be used
    python tools/translations/extract.py --source F.json  write the texts as a source file for a translator (see README)

--check fails on: a key that is no text of the application any more, an empty translation, a translation that loses or
adds a placeholder ({0}, {1:N0}…) or changes the spaces at the ends of the text. What a language lacks is only a warning
(GitHub shows the ::warning:: lines of the output as such).
"""
from __future__ import annotations

import json
import re
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from csharp_texts import ROOT, calls_in, tr_in  # noqa: E402

SRC = ROOT / "src"
TRANSLATIONS = SRC / "DyssCockpit.Core" / "Translations"
PLACEHOLDER = re.compile(r"\{(\d+)(?::[^}]*)?\}")


def collect() -> dict[str, dict]:
    """English wording -> {"fr": [French wordings], "where": [places], "xaml": bool}, in the order of the files."""
    texts: dict[str, dict] = {}

    def add(english: str, french: str, where: str, interpolated: bool = False) -> None:
        entry = texts.setdefault(english, {"fr": [], "where": [], "interpolated": False})
        if french not in entry["fr"]:
            entry["fr"].append(french)
        entry["where"].append(where)
        entry["interpolated"] |= interpolated

    for path in sorted(SRC.rglob("*.cs")):
        if {"obj", "bin"} & set(path.parts) or path.name == "Translation.cs":
            continue
        relative = path.relative_to(ROOT).as_posix()
        calls, _ = calls_in(path, relative)
        for call in calls:
            add(call.english.value, call.french.value, f"{path.name} (code)", call.english.interpolated)
    for path in sorted(SRC.rglob("*.xaml")):
        if {"obj", "bin"} & set(path.parts):
            continue
        source = path.read_text(encoding="utf-8")
        relative = path.relative_to(ROOT).as_posix()
        attributes = {m.start(): m.group(1) for m in re.finditer(r"""([\w.:\[\]]+)=(?=["']\{local:Tr\s)""", source)}
        for (_, line, french, english), attribute in zip(tr_in(path, relative), attributes.values(), strict=True):
            add(english, french, f"{path.name} ({attribute})")
    return texts


def placeholders(text: str) -> list[str]:
    return sorted(m.group(0) for m in PLACEHOLDER.finditer(text))


def languages() -> list[str]:
    """The tags of the languages that have a table: all but French and English."""
    declared = (SRC / "DyssCockpit.Core" / "Translation.cs").read_text(encoding="utf-8")
    tags = re.findall(r'new\(AppLanguage\.\w+, "([a-z]{2,3}-[A-Z]{2})"', declared)
    return [t for t in tags if t not in ("fr-FR", "en-US")]


def language_box_problems() -> list[str]:
    """The language box of the settings page lists the languages of AppLanguages, in the same order: the view model maps an index to a tag."""
    declared = re.findall(r'new\(AppLanguage\.\w+, "[^"]+", "([^"]+)"\)', (SRC / "DyssCockpit.Core" / "Translation.cs").read_text(encoding="utf-8"))
    page = (SRC / "DyssCockpit.App" / "Views" / "SettingsView.xaml").read_text(encoding="utf-8")
    box = re.search(r"<ComboBox[^>]*LanguageIndex.*?</ComboBox>", page, re.S)
    if box is None:
        return ["the language box of SettingsView.xaml was not found"]
    listed = re.findall(r'<ComboBoxItem Content="([^"{]+)"', box.group(0))
    return [] if listed == declared else [f"the language box lists {listed}, AppLanguages {declared}"]


def main() -> int:
    check = "--check" in sys.argv[1:]
    texts = collect()
    tags = languages()
    print(f"{len(texts)} texts in French and English; {len(tags)} other languages")
    errors: list[str] = language_box_problems()

    for tag in tags:
        path = TRANSLATIONS / f"{tag}.json"
        if not path.exists():
            print(f"::warning::{tag} has no translations yet (the English texts are shown)")
            continue
        table = json.loads(path.read_text(encoding="utf-8"))
        for key, text in table.items():
            if key not in texts:
                errors.append(f"{tag}: {key[:70]!r} is not a text of the application")
            elif not isinstance(text, str) or not text.strip():
                errors.append(f"{tag}: the translation of {key[:70]!r} is empty")
            elif placeholders(text) != placeholders(key):
                errors.append(f"{tag}: {key[:70]!r} has the placeholders {placeholders(text)}, the English {placeholders(key)}")
            elif (key[:len(key) - len(key.lstrip())], key[len(key.rstrip()):]) != (text[:len(text) - len(text.lstrip())], text[len(text.rstrip()):]):
                # What the code writes before or after the text (a label's trailing space, a blank line) is kept.
                errors.append(f"{tag}: {key[:70]!r} does not keep the spaces and the line breaks at its ends")
        lacking = [k for k in texts if k not in table]
        if lacking:
            print(f"::warning::{tag} lacks {len(lacking)} of {len(texts)} texts (the English ones are shown)")
    for message in errors:
        print(f"error: {message}", file=sys.stderr)
    if "--source" in sys.argv:
        out = Path(sys.argv[sys.argv.index("--source") + 1])
        entries = [{"id": en, "en": en, "fr": e["fr"][0] if len(e["fr"]) == 1 else e["fr"], "where": sorted(set(e["where"]))}
                   for en, e in texts.items()]
        out.write_text(json.dumps(entries, ensure_ascii=False, indent=1), encoding="utf-8")
        print(f"{len(entries)} texts written to {out}")
    return 1 if check and errors else 0


if __name__ == "__main__":
    sys.exit(main())
