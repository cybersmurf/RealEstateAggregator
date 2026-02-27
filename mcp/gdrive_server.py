"""
Google Drive MCP Server
========================
Model Context Protocol server pro přístup ke Google Drive.

Podporuje dvě auth metody:
  1. Service Account (SA) – pro listing export složky (realestate-drive@emistr-easy.iam.gserviceaccount.com)
  2. User OAuth token  – pro osobní Drive (megamrsk@gmail.com → Můj disk)

Konfigurace (env nebo Claude Desktop config):
  GDRIVE_SA_CREDENTIALS  – cesta k service account JSON (default: secrets/google-drive-sa.json)
  GDRIVE_USER_TOKEN      – cesta k user OAuth token JSON (default: secrets/google-drive-token.json)
  GDRIVE_CLIENT_SECRETS  – cesta k OAuth client secrets JSON (pro refresh tokenu)
  REALESTATE_API_URL     – URL .NET API pro lookup listing folder ID (default: http://localhost:5001)
  MCP_MAX_OUTPUT_CHARS   – max znaků na výstup (default: 200000)

Spuštění (diagnostika):
  python gdrive_server.py --info

Spuštění (stdio – Claude Desktop):
  python gdrive_server.py
"""

import os
import sys
import io
import json
import logging
from typing import Optional
from pathlib import Path

# ── Konfigurace ────────────────────────────────────────────────────────────────
_BASE = Path(__file__).parent.parent  # kořen projektu

SA_CREDENTIALS = os.getenv("GDRIVE_SA_CREDENTIALS", str(_BASE / "secrets" / "google-drive-sa.json"))
USER_TOKEN_PATH = os.getenv("GDRIVE_USER_TOKEN", str(_BASE / "secrets" / "google-drive-token.json"))
CLIENT_SECRETS_PATH = os.getenv("GDRIVE_CLIENT_SECRETS", "")
API_BASE_URL = os.getenv("REALESTATE_API_URL", "http://localhost:5001")
MAX_OUTPUT_CHARS = int(os.getenv("MCP_MAX_OUTPUT_CHARS", "200000"))
MAX_FILE_CHARS = int(os.getenv("GDRIVE_MAX_FILE_CHARS", "100000"))

# ── --info diagnostika ─────────────────────────────────────────────────────────
if "--info" in sys.argv:
    print("=== gdrive_server.py diagnostika ===")
    print(f"SA credentials : {SA_CREDENTIALS} ({'✅ OK' if Path(SA_CREDENTIALS).exists() else '❌ CHYBÍ'})")
    print(f"User OAuth token: {USER_TOKEN_PATH} ({'✅ OK' if Path(USER_TOKEN_PATH).exists() else '❌ CHYBÍ'})")
    print(f"API base URL   : {API_BASE_URL}")
    print(f"MAX_OUTPUT_CHARS: {MAX_OUTPUT_CHARS:,}")
    print(f"MAX_FILE_CHARS  : {MAX_FILE_CHARS:,}")
    sys.exit(0)

# ── Google API imports ─────────────────────────────────────────────────────────
from google.oauth2 import service_account
from google.oauth2.credentials import Credentials
from googleapiclient.discovery import build as _build_service
from googleapiclient.errors import HttpError
from googleapiclient.http import MediaIoBaseDownload, MediaInMemoryUpload

import httpx
from fastmcp import FastMCP

logging.basicConfig(level=logging.WARNING)
log = logging.getLogger("gdrive_mcp")

mcp = FastMCP("gdrive")

SCOPES = ["https://www.googleapis.com/auth/drive"]
MIME_FOLDER = "application/vnd.google-apps.folder"
MIME_GDOC   = "application/vnd.google-apps.document"
MIME_GSHEET = "application/vnd.google-apps.spreadsheet"


# ── Auth helpery ───────────────────────────────────────────────────────────────

def _sa_credentials():
    """Service Account credentials – přístup k listing export složkám."""
    return service_account.Credentials.from_service_account_file(
        SA_CREDENTIALS, scopes=SCOPES
    )

def _user_credentials():
    """User OAuth credentials – přístup k osobnímu Drive."""
    if not Path(USER_TOKEN_PATH).exists():
        return None
    token_data = json.loads(Path(USER_TOKEN_PATH).read_text())
    creds = Credentials(
        token=token_data.get("access_token"),
        refresh_token=token_data.get("refresh_token"),
        token_uri="https://oauth2.googleapis.com/token",
        client_id=token_data.get("client_id"),
        client_secret=token_data.get("client_secret"),
        scopes=token_data.get("scopes") or SCOPES,
    )
    return creds

def _drive(auth: str = "sa"):
    """Vrátí Drive API service. auth='sa' | 'user'"""
    if auth == "user":
        creds = _user_credentials()
        if creds is None:
            raise ValueError("User OAuth token nenalezen – nastav GDRIVE_USER_TOKEN")
    else:
        creds = _sa_credentials()
    return _build_service("drive", "v3", credentials=creds, cache_discovery=False)


# ── Output cap ─────────────────────────────────────────────────────────────────

def _cap(text: str) -> str:
    if len(text) <= MAX_OUTPUT_CHARS:
        return text
    truncated = text[:MAX_OUTPUT_CHARS]
    nl = truncated.rfind("\n")
    if nl > MAX_OUTPUT_CHARS // 2:
        truncated = truncated[:nl]
    pct = len(truncated) * 100 // len(text)
    return (truncated
            + f"\n\n---\n⚠️ Výstup zkrácen na {MAX_OUTPUT_CHARS:,} znaků ({pct}% z {len(text):,}).")


# ── Utility ────────────────────────────────────────────────────────────────────

def _extract_id(id_or_url: str) -> str:
    """Extrahuje Google Drive folder/file ID z URL nebo vrátí ID přímo."""
    if id_or_url.startswith("http"):
        # https://drive.google.com/drive/folders/ABC123?usp=sharing
        # https://drive.google.com/file/d/ABC123/view
        for segment in id_or_url.replace("/drive/folders/", "\n").replace("/file/d/", "\n").split("\n"):
            part = segment.split("?")[0].split("/")[0].strip()
            if part and len(part) > 20:
                return part
    return id_or_url.strip()

def _file_icon(mime: str) -> str:
    if mime == MIME_FOLDER:    return "📁"
    if mime == MIME_GDOC:      return "📝"
    if mime == MIME_GSHEET:    return "📊"
    if "image" in mime:        return "🖼️"
    if "pdf" in mime:          return "📄"
    if "markdown" in mime or mime in ("text/plain",): return "📋"
    if "json" in mime:         return "{ }"
    return "📄"

def _size_str(size_str: Optional[str]) -> str:
    if not size_str:
        return ""
    try:
        b = int(size_str)
        if b < 1024:      return f"{b} B"
        if b < 1048576:   return f"{b/1024:.1f} KB"
        return f"{b/1048576:.1f} MB"
    except Exception:
        return ""


# ── Tools ──────────────────────────────────────────────────────────────────────

@mcp.tool()
def list_folder(
    folder_id_or_url: str,
    auth: str = "sa",
    show_details: bool = True,
) -> str:
    """
    Vypíše obsah Google Drive složky.

    Parametry:
      folder_id_or_url – ID složky nebo URL (drive.google.com/drive/folders/...)
      auth             – 'sa' (service account pro listing složky) nebo 'user' (osobní Drive)
      show_details     – True = zobraz velikost, MIME, datum

    Typické použití:
      • list_folder("1ABC...", auth="sa")         – listing export složka
      • list_folder("https://drive.google.com/drive/folders/1ABC...", auth="user")  – osobní Drive
    """
    fid = _extract_id(folder_id_or_url)
    try:
        svc = _drive(auth)
        # Metadata složky
        meta = svc.files().get(fileId=fid, fields="id,name,mimeType").execute()
        folder_name = meta.get("name", fid)

        # Obsah
        results = svc.files().list(
            q=f"'{fid}' in parents and trashed=false",
            fields="files(id,name,mimeType,size,modifiedTime,webViewLink)",
            orderBy="folder,name",
            pageSize=200,
        ).execute()
        files = results.get("files", [])

        if not files:
            return f"📁 **{folder_name}** – prázdná složka"

        lines = [f"📁 **{folder_name}** ({len(files)} položek, folder ID: `{fid}`):\n"]
        folders_count = 0
        files_count = 0
        for f in files:
            icon = _file_icon(f["mimeType"])
            if f["mimeType"] == MIME_FOLDER:
                folders_count += 1
            else:
                files_count += 1
            line = f"  {icon} {f['name']}"
            if show_details:
                size = _size_str(f.get("size"))
                mod  = (f.get("modifiedTime","")[:16]).replace("T"," ")
                details = "  ".join(filter(None, [size, mod]))
                if details:
                    line += f"   [{details}]"
            line += f"   ID: `{f['id']}`"
            lines.append(line)
        lines.append(f"\n📊 Celkem: {folders_count} složek, {files_count} souborů")
        return _cap("\n".join(lines))

    except HttpError as e:
        if e.resp.status == 404:
            return f"❌ Složka `{fid}` nenalezena. Zkontroluj ID nebo zda je složka sdílena se SA účtem."
        if e.resp.status == 403:
            return f"❌ Přístup odepřen k `{fid}`. Složka není sdílena se SA účtem `realestate-drive@emistr-easy.iam.gserviceaccount.com`."
        return f"❌ Drive API chyba: {e}"
    except Exception as e:
        return f"❌ Chyba: {e}"


@mcp.tool()
def read_drive_file(
    file_id_or_url: str,
    auth: str = "sa",
    start_line: int = 1,
    max_lines: int = 0,
) -> str:
    """
    Přečte obsah textového souboru z Google Drive.

    Podporované typy: .md, .txt, .json, .csv, .py, .cs, .html + Google Docs (export jako text).
    Binární soubory (obrázky, PDF) vrátí metadata místo obsahu.

    Parametry:
      file_id_or_url – ID souboru nebo URL (drive.google.com/file/d/...)
      auth           – 'sa' nebo 'user'
      start_line     – první řádek (1-based), pro stránkování
      max_lines      – max počet řádků (0 = vše, resp. MAX_FILE_CHARS)
    """
    fid = _extract_id(file_id_or_url)
    try:
        svc = _drive(auth)
        meta = svc.files().get(
            fileId=fid,
            fields="id,name,mimeType,size,modifiedTime,webViewLink"
        ).execute()
        name  = meta.get("name", fid)
        mime  = meta.get("mimeType", "")
        mtime = (meta.get("modifiedTime","")[:16]).replace("T", " ")

        header = f"📄 **{name}** (ID: `{fid}`, {mtime})\n\n"

        # Binární soubory – jen metadata
        if any(t in mime for t in ["image/", "video/", "audio/", "pdf", "zip"]):
            size = _size_str(meta.get("size"))
            return f"{header}ℹ️ Binární soubor ({mime}, {size}) – obsah nelze zobrazit jako text."

        # Google Docs – export jako plain text
        if mime == MIME_GDOC:
            response = svc.files().export(fileId=fid, mimeType="text/plain").execute()
            content = response.decode("utf-8") if isinstance(response, bytes) else str(response)
        elif mime == MIME_GSHEET:
            response = svc.files().export(fileId=fid, mimeType="text/csv").execute()
            content = response.decode("utf-8") if isinstance(response, bytes) else str(response)
        else:
            # Ostatní textové soubory – přímý download
            request = svc.files().get_media(fileId=fid)
            buf = io.BytesIO()
            downloader = MediaIoBaseDownload(buf, request)
            done = False
            while not done:
                _, done = downloader.next_chunk()
            raw = buf.getvalue()
            # Detekce kódování – preferuj UTF-8
            for enc in ("utf-8", "utf-8-sig", "cp1250", "latin-1"):
                try:
                    content = raw.decode(enc)
                    break
                except Exception:
                    pass
            else:
                return f"{header}❌ Nepodařilo se dekódovat soubor (pravděpodobně binární formát)."

        # Stránkování po řádcích
        lines = content.splitlines()
        total_lines = len(lines)
        start = max(1, start_line) - 1
        end   = (start + max_lines) if max_lines > 0 else total_lines
        chunk = "\n".join(lines[start:end])

        pagination = ""
        if end < total_lines:
            pagination = f"\n\n---\n📄 Zobrazeny řádky {start+1}–{end} z {total_lines}. Další: `start_line={end+1}`"

        result = header + chunk + pagination
        return _cap(result)

    except HttpError as e:
        if e.resp.status == 404:
            return f"❌ Soubor `{fid}` nenalezen."
        if e.resp.status == 403:
            return f"❌ Přístup odepřen k `{fid}`."
        return f"❌ Drive API chyba: {e}"
    except Exception as e:
        return f"❌ Chyba: {e}"


@mcp.tool()
def upload_to_drive(
    folder_id_or_url: str,
    filename: str,
    content: str,
    mime_type: str = "text/markdown",
    auth: str = "sa",
    overwrite: bool = False,
) -> str:
    """
    Nahraje textový soubor do Google Drive složky.

    Parametry:
      folder_id_or_url – ID nebo URL cílové složky
      filename         – název souboru (např. "ANALYZA_2026-02-27.md")
      content          – obsah souboru (text / markdown / JSON)
      mime_type        – MIME typ (default: text/markdown)
      auth             – 'sa' nebo 'user'
      overwrite        – True = přepiš existující soubor se stejným názvem

    Vrátí: URL nového/updatovaného souboru a jeho ID.
    """
    fid = _extract_id(folder_id_or_url)
    try:
        svc = _drive(auth)

        # Pokud overwrite=True, zkusíme najít existující soubor
        if overwrite:
            existing = svc.files().list(
                q=f"'{fid}' in parents and name='{filename}' and trashed=false",
                fields="files(id,name)",
                pageSize=1,
            ).execute().get("files", [])
            if existing:
                eid = existing[0]["id"]
                media = MediaInMemoryUpload(content.encode("utf-8"), mimetype=mime_type, resumable=False)
                updated = svc.files().update(fileId=eid, media_body=media).execute()
                url = f"https://drive.google.com/file/d/{eid}/view"
                return f"✅ Soubor **{filename}** aktualizován.\nID: `{eid}`\nURL: {url}"

        # Nový soubor
        media = MediaInMemoryUpload(content.encode("utf-8"), mimetype=mime_type, resumable=False)
        file_meta = {"name": filename, "parents": [fid]}
        created = svc.files().create(
            body=file_meta,
            media_body=media,
            fields="id,name,webViewLink",
        ).execute()
        file_id = created["id"]
        # Nastavit public read
        svc.permissions().create(
            fileId=file_id,
            body={"type": "anyone", "role": "reader"},
        ).execute()
        url = created.get("webViewLink", f"https://drive.google.com/file/d/{file_id}/view")
        return f"✅ Soubor **{filename}** nahrán do Drive.\nID: `{file_id}`\nURL: {url}"

    except HttpError as e:
        if e.resp.status == 404:
            return f"❌ Cílová složka `{fid}` nenalezena."
        if e.resp.status == 403:
            return f"❌ Přístup odepřen – složka není sdílena se SA účtem."
        return f"❌ Drive API chyba: {e}"
    except Exception as e:
        return f"❌ Chyba: {e}"


@mcp.tool()
def search_drive(
    query: str,
    folder_id_or_url: str = "",
    auth: str = "sa",
    file_type: str = "",
    max_results: int = 30,
) -> str:
    """
    Vyhledá soubory v Google Drive.

    Parametry:
      query            – hledaný výraz (název souboru nebo obsah)
      folder_id_or_url – omezit hledání na složku (prázdné = celý Drive)
      auth             – 'sa' nebo 'user'
      file_type        – filtr: 'folder', 'doc', 'sheet', 'image', 'pdf' (prázdné = vše)
      max_results      – max počet výsledků (default 30)

    Příklady:
      search_drive("ANALYZA", folder_id_or_url="1ABC...", auth="sa")
      search_drive("Baráček", auth="user", file_type="doc")
    """
    try:
        svc = _drive(auth)

        # Sestavení q podmínky
        parts = [f"fullText contains '{query}' or name contains '{query}'", "trashed=false"]
        if folder_id_or_url:
            fid = _extract_id(folder_id_or_url)
            parts.append(f"'{fid}' in parents")
        if file_type:
            type_map = {
                "folder": f"mimeType='{MIME_FOLDER}'",
                "doc":    f"mimeType='{MIME_GDOC}'",
                "sheet":  f"mimeType='{MIME_GSHEET}'",
                "image":  "mimeType contains 'image/'",
                "pdf":    "mimeType='application/pdf'",
            }
            if file_type in type_map:
                parts.append(type_map[file_type])

        q = " and ".join(parts)
        results = svc.files().list(
            q=q,
            fields="files(id,name,mimeType,size,modifiedTime,webViewLink,parents)",
            orderBy="modifiedTime desc",
            pageSize=min(max_results, 100),
        ).execute()
        files = results.get("files", [])

        if not files:
            return f"🔍 Žádné výsledky pro dotaz: **{query}**"

        lines = [f"🔍 Výsledky pro **{query}** ({len(files)} souborů):\n"]
        for f in files:
            icon = _file_icon(f["mimeType"])
            size = _size_str(f.get("size"))
            mod  = (f.get("modifiedTime","")[:16]).replace("T"," ")
            url  = f.get("webViewLink","")
            line = f"  {icon} **{f['name']}**  ID: `{f['id']}`"
            if size:  line += f"  {size}"
            if mod:   line += f"  {mod}"
            if url:   line += f"\n      🔗 {url}"
            lines.append(line)
        return _cap("\n".join(lines))

    except HttpError as e:
        return f"❌ Drive API chyba: {e}"
    except Exception as e:
        return f"❌ Chyba: {e}"


@mcp.tool()
def list_listing_drive(
    listing_id: str,
    auth: str = "sa",
) -> str:
    """
    Zobrazí obsah Google Drive složky konkrétního inzerátu.

    Automaticky dohledá DriveFolderId přes .NET API a pak zobrazí obsah složky.
    Je to zkratka za get_listing() → vezme DriveFolderUrl → list_folder().

    Parametry:
      listing_id – UUID inzerátu (nebo začátek UUID, doplní se automaticky)
      auth       – 'sa' (default) nebo 'user'
    """
    try:
        # 1. Dohledání folder ID přes .NET API
        with httpx.Client(timeout=10) as client:
            resp = client.get(f"{API_BASE_URL}/api/listings/{listing_id}")
            if resp.status_code == 404:
                return f"❌ Inzerát {listing_id} nenalezen v databázi."
            resp.raise_for_status()
            data = resp.json()

        folder_url = data.get("driveFolderUrl", "")
        folder_id  = data.get("driveFolderId",  "")
        title      = data.get("title", listing_id)

        if not folder_url and not folder_id:
            return (f"⚠️ Inzerát **{title}** (`{listing_id[:8]}`) nemá Drive složku.\n"
                    f"Nejprve spusť export: POST /api/listings/{listing_id}/export-drive")

        fid = folder_id or _extract_id(folder_url)
        header = f"## Drive složka: {title}\n`{listing_id}`  →  [Drive]({folder_url})\n\n"

        # 2. List hlavní složky
        main_listing = list_folder(fid, auth=auth)

        # 3. Inspection folder (pokud existuje)
        insp_folder_id = data.get("driveInspectionFolderId", "")
        insp_section = ""
        if insp_folder_id:
            insp_section = "\n\n---\n### 📷 Inspection folder:\n" + list_folder(insp_folder_id, auth=auth)

        return _cap(header + main_listing + insp_section)

    except httpx.RequestError as e:
        return f"❌ API nedostupné ({API_BASE_URL}): {e}"
    except Exception as e:
        return f"❌ Chyba: {e}"


# ── Hlavní entry point ─────────────────────────────────────────────────────────
if __name__ == "__main__":
    mcp.run()
