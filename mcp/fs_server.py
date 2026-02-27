"""
macOS Filesystem MCP Server
============================
Umožňuje Claude Desktop číst, zapisovat a prohledávat soubory na macOS.
APFS + plné UTF-8 včetně češtiny (č, š, ž, ě, ř, ý, á, í, é, ú, ů, ď, ť, ň).

Bezpečnost:
- Přístup omezen pouze na ALLOWED_ROOTS (default: ~/Projects, ~/Documents, ~/Desktop)
- Soubory mimo povolené kořeny nelze číst ani zapsat
- Mazání jen souborů (ne složek) – ochrana před rm -rf

Konfigurace (env):
    FS_ALLOWED_ROOTS  – čárkou oddělené cesty, default: ~/Projects,~/Documents,~/Desktop
    FS_MAX_FILE_CHARS – max znaků při čtení souboru, default: 200000
    FS_MAX_RESULTS    – max výsledků při search, default: 100

Spuštění (stdio – Claude Desktop):
    python fs_server.py
"""

import os
import stat
import fnmatch
import mimetypes
import subprocess
from datetime import datetime
from pathlib import Path
from typing import Optional

from fastmcp import FastMCP

# ─── Konfigurace ──────────────────────────────────────────────────────────────

_DEFAULT_ROOTS = [
    os.path.expanduser("~/Projects"),
    os.path.expanduser("~/Documents"),
    os.path.expanduser("~/Desktop"),
]

def _parse_roots() -> list[str]:
    raw = os.getenv("FS_ALLOWED_ROOTS", "")
    if raw.strip():
        return [os.path.expanduser(p.strip()) for p in raw.split(",") if p.strip()]
    return _DEFAULT_ROOTS

ALLOWED_ROOTS: list[str] = _parse_roots()
MAX_FILE_CHARS: int = int(os.getenv("FS_MAX_FILE_CHARS", "200000"))
MAX_RESULTS: int = int(os.getenv("FS_MAX_RESULTS", "100"))

# ─── MCP server ───────────────────────────────────────────────────────────────

mcp = FastMCP(
    name="macOS Filesystem",
    instructions=f"""
Filesystem přístup na macOS. Čteš, zapišuješ a prohledáváš soubory.
Plná podpora češtiny (diakritika) a dlouhých cest (APFS).

Povolené kořeny:
{chr(10).join(f'  - {r}' for r in ALLOWED_ROOTS)}

Dostupné nástroje:
- list_dir: Výpis obsahu složky
- read_file: Přečte obsah souboru (UTF-8 nebo binárně jako hex)
- write_file: Zapíše nebo přepíše soubor
- append_file: Připojí text na konec souboru
- search_files: Najde soubory dle vzoru nebo obsahu (grep)
- get_file_info: Metadata souboru (velikost, datum, typ)
- create_dir: Vytvoří složku (mkdir -p)
- delete_file: Smaže soubor (ne složku!)
- move_file: Přesune nebo přejmenuje soubor/složku
""",
)

# ─── Bezpečnostní helper ──────────────────────────────────────────────────────

def _resolve(path: str) -> Path:
    """Resolvne cestu na absolutní, expanduje ~ a ověří ALLOWED_ROOTS."""
    p = Path(os.path.normpath(os.path.expanduser(path))).resolve()
    for root in ALLOWED_ROOTS:
        try:
            p.relative_to(root)
            return p
        except ValueError:
            continue
    allowed = "\n".join(f"  • {r}" for r in ALLOWED_ROOTS)
    raise PermissionError(
        f"❌ Přístup odepřen: '{p}'\n"
        f"Povolené kořeny:\n{allowed}"
    )


def _fmt_size(n: int) -> str:
    for unit in ("B", "KB", "MB", "GB"):
        if n < 1024:
            return f"{n:.0f} {unit}"
        n /= 1024
    return f"{n:.1f} TB"


def _fmt_time(ts: float) -> str:
    return datetime.fromtimestamp(ts).strftime("%Y-%m-%d %H:%M")


# ─── Nástroje ─────────────────────────────────────────────────────────────────

@mcp.tool()
def list_dir(
    path: str,
    show_hidden: bool = False,
    sort_by: str = "name",
) -> str:
    """
    Vypíše obsah složky s velikostmi a daty.

    Args:
        path: Cesta ke složce (absolutní nebo ~)
        show_hidden: Zobrazit skryté soubory (začínající .) – default False
        sort_by: Řazení: "name" | "size" | "modified" – default "name"
    """
    p = _resolve(path)
    if not p.exists():
        return f"❌ Složka neexistuje: {p}"
    if not p.is_dir():
        return f"❌ Není složka: {p}"

    entries = []
    try:
        for entry in p.iterdir():
            if not show_hidden and entry.name.startswith("."):
                continue
            try:
                st = entry.stat()
                entries.append((entry, st))
            except OSError:
                continue
    except PermissionError:
        return f"❌ Nemám oprávnění číst: {p}"

    # Řazení
    if sort_by == "size":
        entries.sort(key=lambda x: x[1].st_size, reverse=True)
    elif sort_by == "modified":
        entries.sort(key=lambda x: x[1].st_mtime, reverse=True)
    else:
        entries.sort(key=lambda x: (not x[0].is_dir(), x[0].name.lower()))

    lines = [f"📁 **{p}** ({len(entries)} položek):\n"]
    dirs_count = sum(1 for e, _ in entries if e.is_dir())
    files_count = sum(1 for e, _ in entries if e.is_file())

    for entry, st in entries:
        if entry.is_dir():
            icon = "📁"
            size_str = "  <složka>"
        elif entry.is_symlink():
            icon = "🔗"
            size_str = f"  {_fmt_size(st.st_size):>10}"
        else:
            icon = "📄"
            size_str = f"  {_fmt_size(st.st_size):>10}"

        mod = _fmt_time(st.st_mtime)
        lines.append(f"{icon} {entry.name:<50} {size_str}  {mod}")

    lines.append(f"\n📊 Celkem: {dirs_count} složek, {files_count} souborů")
    return "\n".join(lines)


@mcp.tool()
def read_file(
    path: str,
    encoding: str = "utf-8",
    start_line: int = 1,
    max_lines: int = 0,
) -> str:
    """
    Přečte obsah souboru. Podporuje češtinu a diakritiku.

    Args:
        path: Cesta k souboru
        encoding: Kódování – default "utf-8" (nebo "latin-1", "cp1250", "binary")
        start_line: Od které řádky číst – default 1 (začátek)
        max_lines: Max počet řádků – default 0 = vše (omezeno FS_MAX_FILE_CHARS)
    """
    p = _resolve(path)
    if not p.exists():
        return f"❌ Soubor neexistuje: {p}"
    if p.is_dir():
        return f"❌ Je to složka, ne soubor: {p}"

    file_size = p.stat().st_size

    if encoding == "binary":
        with open(p, "rb") as f:
            data = f.read(1024)
        hex_str = data.hex()
        return (
            f"📄 **{p.name}** ({_fmt_size(file_size)}) – binární soubor:\n"
            f"[prvních 1024 bytů hex]\n{hex_str}"
        )

    try:
        with open(p, "r", encoding=encoding, errors="replace") as f:
            if start_line > 1:
                for _ in range(start_line - 1):
                    f.readline()
            if max_lines > 0:
                content = "".join(f.readline() for _ in range(max_lines))
            else:
                content = f.read()
    except (UnicodeDecodeError, LookupError) as e:
        return f"❌ Chyba kódování ({encoding}): {e}\nZkus encoding='latin-1' nebo encoding='binary'"

    truncated = ""
    if len(content) > MAX_FILE_CHARS:
        content = content[:MAX_FILE_CHARS]
        last_nl = content.rfind("\n")
        if last_nl > MAX_FILE_CHARS // 2:
            content = content[:last_nl]
        truncated = f"\n\n---\n⚠️ Soubor zkrácen na {MAX_FILE_CHARS:,} znaků. Použij start_line pro čtení dalšího obsahu."

    header = f"📄 **{p.name}** ({_fmt_size(file_size)}, {encoding})"
    if start_line > 1 or max_lines > 0:
        header += f" [řádky {start_line}–{start_line + content.count(chr(10))}]"

    return f"{header}\n\n```\n{content}\n```{truncated}"


@mcp.tool()
def write_file(
    path: str,
    content: str,
    encoding: str = "utf-8",
    overwrite: bool = True,
) -> str:
    """
    Zapíše nebo přepíše soubor. Automaticky vytvoří chybějící složky.

    Args:
        path: Cesta k souboru
        content: Obsah k zapsání (plný text)
        encoding: Kódování – default "utf-8"
        overwrite: True = přepiš existující (default), False = chyba pokud existuje
    """
    p = _resolve(path)

    if p.exists() and not overwrite:
        return f"❌ Soubor již existuje: {p}\nPoužij overwrite=True pro přepsání."
    if p.is_dir():
        return f"❌ Je to složka: {p}"

    p.parent.mkdir(parents=True, exist_ok=True)
    old_size = p.stat().st_size if p.exists() else None

    with open(p, "w", encoding=encoding) as f:
        f.write(content)

    new_size = p.stat().st_size
    action = "Přepsán" if old_size is not None else "Vytvořen"
    size_info = f"{_fmt_size(old_size)} → {_fmt_size(new_size)}" if old_size is not None else _fmt_size(new_size)
    return f"✅ {action}: {p} ({size_info})"


@mcp.tool()
def append_file(
    path: str,
    content: str,
    encoding: str = "utf-8",
    newline_before: bool = True,
) -> str:
    """
    Připojí text na konec souboru (append). Pokud soubor neexistuje, vytvoří ho.

    Args:
        path: Cesta k souboru
        content: Text k připojení
        encoding: Kódování – default "utf-8"
        newline_before: Přidat prázdný řádek před obsah – default True
    """
    p = _resolve(path)
    if p.is_dir():
        return f"❌ Je to složka: {p}"

    p.parent.mkdir(parents=True, exist_ok=True)
    prefix = "\n" if newline_before and p.exists() and p.stat().st_size > 0 else ""

    with open(p, "a", encoding=encoding) as f:
        f.write(prefix + content)

    return f"✅ Připojeno do: {p} (celková velikost: {_fmt_size(p.stat().st_size)})"


@mcp.tool()
def search_files(
    root: str,
    name_pattern: str = "*",
    content_pattern: str = "",
    max_results: int = 50,
    include_hidden: bool = False,
    file_extensions: str = "",
) -> str:
    """
    Najde soubory dle jména nebo obsahu (grep). Rekurzivní prohledávání.

    Args:
        root: Kořenová složka hledání
        name_pattern: Vzor pro jméno souboru (glob), např. "*.py", "*.cs", "report*"
        content_pattern: Hledaný text v obsahu souborů (case-insensitive), default "" = nehledat v obsahu
        max_results: Max výsledků – default 50 (max 100)
        include_hidden: Zahrnout skryté soubory/složky – default False
        file_extensions: Čárkou oddělené přípony, např. ".py,.cs,.md" – default "" = vše
    """
    p = _resolve(root)
    if not p.is_dir():
        return f"❌ Není složka: {p}"

    max_results = min(max_results, MAX_RESULTS)
    ext_filter = {e.strip().lstrip(".").lower() for e in file_extensions.split(",") if e.strip()} if file_extensions else set()

    results = []
    searched_dirs = 0

    try:
        for dirpath, dirnames, filenames in os.walk(str(p)):
            if not include_hidden:
                dirnames[:] = [d for d in dirnames if not d.startswith(".")]
            searched_dirs += 1

            for fname in filenames:
                if not include_hidden and fname.startswith("."):
                    continue

                # Filtr přípony
                if ext_filter:
                    fext = Path(fname).suffix.lstrip(".").lower()
                    if fext not in ext_filter:
                        continue

                # Filtr jména (glob)
                if name_pattern != "*" and not fnmatch.fnmatch(fname.lower(), name_pattern.lower()):
                    continue

                fpath = Path(dirpath) / fname
                rel = fpath.relative_to(p)

                # Filtr obsahu
                if content_pattern:
                    try:
                        text = fpath.read_text(encoding="utf-8", errors="ignore")
                        if content_pattern.lower() not in text.lower():
                            continue
                        # Najdi první výskyt pro náhled
                        idx = text.lower().find(content_pattern.lower())
                        snippet_start = max(0, idx - 60)
                        snippet_end = min(len(text), idx + len(content_pattern) + 60)
                        snippet = text[snippet_start:snippet_end].replace("\n", " ").strip()
                        results.append((str(rel), fpath.stat().st_size, snippet))
                    except (OSError, PermissionError):
                        continue
                else:
                    try:
                        results.append((str(rel), fpath.stat().st_size, ""))
                    except OSError:
                        continue

                if len(results) >= max_results:
                    break
            if len(results) >= max_results:
                break

    except PermissionError as e:
        return f"❌ Přístup odepřen: {e}"

    if not results:
        search_desc = f"vzor '{name_pattern}'"
        if content_pattern:
            search_desc += f" + obsah '{content_pattern}'"
        return f"🔍 Nenalezeny žádné soubory ({search_desc}) v {p}"

    lines = [f"🔍 **Nalezeno {len(results)} souborů** v `{p}`:\n"]
    for rel_path, size, snippet in results:
        size_str = _fmt_size(size)
        line = f"  📄 {rel_path}  ({size_str})"
        if snippet:
            line += f"\n     `...{snippet}...`"
        lines.append(line)

    if len(results) >= max_results:
        lines.append(f"\n⚠️ Výsledky zkráceny na {max_results}. Upřesni vzor nebo použij file_extensions filtr.")

    return "\n".join(lines)


@mcp.tool()
def get_file_info(path: str) -> str:
    """
    Vrátí metadata souboru nebo složky: velikost, datum, oprávnění, typ MIME.

    Args:
        path: Cesta k souboru nebo složce
    """
    p = _resolve(path)
    if not p.exists():
        return f"❌ Neexistuje: {p}"

    st = p.stat()
    is_dir = p.is_dir()
    is_link = p.is_symlink()

    mime_type = ""
    if not is_dir:
        mime_type, _ = mimetypes.guess_type(str(p))
        mime_type = mime_type or "application/octet-stream"

    perms = stat.filemode(st.st_mode)

    lines = [
        f"## 📋 Informace o: `{p.name}`",
        f"**Plná cesta:** `{p}`",
        f"**Typ:** {'Složka' if is_dir else ('Symlink' if is_link else 'Soubor')}",
    ]

    if not is_dir:
        lines.append(f"**Velikost:** {_fmt_size(st.st_size)} ({st.st_size:,} bytů)")
        lines.append(f"**MIME typ:** {mime_type}")

    lines += [
        f"**Vytvořen:** {_fmt_time(st.st_birthtime) if hasattr(st, 'st_birthtime') else 'N/A'}",
        f"**Změněn:** {_fmt_time(st.st_mtime)}",
        f"**Oprávnění:** {perms}",
    ]

    if is_dir:
        try:
            children = list(p.iterdir())
            dirs = sum(1 for c in children if c.is_dir())
            files = sum(1 for c in children if c.is_file())
            lines.append(f"**Obsah:** {dirs} složek, {files} souborů (přímo)")
        except PermissionError:
            pass

    if is_link:
        lines.append(f"**Cíl symlinku:** {os.readlink(str(p))}")

    return "\n".join(lines)


@mcp.tool()
def create_dir(path: str) -> str:
    """
    Vytvoří složku (včetně chybějících nadřazených – mkdir -p).

    Args:
        path: Cesta k nové složce
    """
    p = _resolve(path)
    if p.exists():
        return f"ℹ️ Složka již existuje: {p}"

    p.mkdir(parents=True, exist_ok=True)
    return f"✅ Složka vytvořena: {p}"


@mcp.tool()
def delete_file(path: str, confirm: bool = False) -> str:
    """
    Smaže soubor. Složky NELZE smazat (ochrana před náhodným rm -rf).
    Vyžaduje potvrzení: confirm=True.

    Args:
        path: Cesta k souboru
        confirm: Musí být True pro skutečné smazání (ochrana)
    """
    if not confirm:
        p_safe = os.path.normpath(os.path.expanduser(path))
        return (
            f"⚠️ Bezpečnostní kontrola: chystáš se smazat:\n  `{p_safe}`\n\n"
            f"Pro skutečné smazání zavolej znovu s `confirm=True`."
        )

    p = _resolve(path)
    if not p.exists():
        return f"❌ Soubor neexistuje: {p}"
    if p.is_dir():
        return f"❌ Nelze smazat složky (ochrana). Pro smazání složky použij terminál."

    size = p.stat().st_size
    p.unlink()
    return f"🗑️ Smazán: {p} ({_fmt_size(size)})"


@mcp.tool()
def move_file(src: str, dst: str, overwrite: bool = False) -> str:
    """
    Přesune nebo přejmenuje soubor nebo složku.

    Args:
        src: Zdrojová cesta
        dst: Cílová cesta
        overwrite: True = přepiš existující cíl – default False
    """
    src_p = _resolve(src)
    # dst musí být v allowed roots – ověříme přes _resolve
    dst_p = _resolve(dst)

    if not src_p.exists():
        return f"❌ Zdroj neexistuje: {src_p}"
    if dst_p.exists() and not overwrite:
        return f"❌ Cíl již existuje: {dst_p}\nPoužij overwrite=True."

    dst_p.parent.mkdir(parents=True, exist_ok=True)
    src_p.rename(dst_p)
    return f"✅ Přesunuto: `{src_p}` → `{dst_p}`"


# ─── Spuštění ─────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import sys
    if "--info" in sys.argv:
        print("macOS Filesystem MCP Server")
        print(f"Povolené kořeny:")
        for r in ALLOWED_ROOTS:
            print(f"  • {r}")
        print(f"Max velikost souboru: {MAX_FILE_CHARS:,} znaků")
        sys.exit(0)

    mcp.run(transport="stdio")
