# RAG UI Design – Obecné standardy a implementační průvodce

**Verze:** 1.0  
**Datum:** 1. března 2026  
**Platí pro:** RealEstateAggregator · eMISTR · jakýkoliv projekt s obecným RAG systémem  
**Stack:** .NET 10 · Blazor Server · MudBlazor 9 · pgvector · Ollama / OpenAI

---

## Obsah

1. [Přehled a filosofie](#1-přehled-a-filosofie)
2. [Topologie stránek a komponent](#2-topologie-stránek-a-komponent)
3. [Chat UI – hlavní hráč](#3-chat-ui--hlavní-hráč)
4. [Citation cards – zdrojové záznamy](#4-citation-cards--zdrojové-záznamy)
5. [Knowledge base management](#5-knowledge-base-management)
6. [Document ingestion UI](#6-document-ingestion-ui)
7. [Conversation history](#7-conversation-history)
8. [Settings & konfigurace](#8-settings--konfigurace)
9. [Stavy UI: loading · thinking · error · empty](#9-stavy-ui-loading--thinking--error--empty)
10. [Streaming odpovědí](#10-streaming-odpovědí)
11. [Přístupnost (WCAG 2.2 AA)](#11-přístupnost-wcag-22-aa)
12. [State management v Blazor](#12-state-management-v-blazor)
13. [MudBlazor 9 – konkrétní komponenty](#13-mudblazor-9--konkrétní-komponenty)
14. [Integrace s backendem (.NET API)](#14-integrace-s-backendem-net-api)
15. [Kontextový (embedded) RAG vs. standalone](#15-kontextový-embedded-rag-vs-standalone)
16. [Checklist před releasem](#16-checklist-před-releasem)

---

## 1. Přehled a filosofie

### Co je obecný RAG UI?

Obecný RAG (Retrieval-Augmented Generation) UI je rozhraní, které:

1. **Příjímá otázku** od uživatele v přirozeném jazyce
2. **Vyhledá relevantní fragmenty** z knowledge base (pgvector cosine similarity)
3. **Sestaví odpověď** pomocí LLM s injektovaným kontextem
4. **Zobrazí odpověď + zdrojové záznamy** s metadaty (relevance, zdroj, datum)

### Dva způsoby použití

| Typ | Popis | Příklad |
|-----|-------|---------|
| **Embedded (kontextový)** | RAG chat uvnitř větší stránky, kontext je fixovaný (1 entita) | Záložka „AI Chat" v detailu inzerátu |
| **Standalone** | Celá stránka věnovaná RAG chatu, uživatel volí scope | `/rag` stránka v eMISTR, `/knowledge-base` v RealEstate |

Tento dokument popisuje **standardy pro oba typy**. Oddíl 15 uvádí rozdíly.

### Klíčové principy

- **Context is visible** – uživatel vždy vidí, z čeho AI odpovídá (citation cards)
- **Zpětná vazba okamžitě** – každá akce (embed, send, save) má okamžitý loading stav
- **Failure gracefully** – offline Ollama, prázdná KB, timeout → srozumitelné chybové sdělení
- **Keyboard-first** – celý chat ovladatelný bez myši (`Enter` = odeslat, `Shift+Enter` = nový řádek)

---

## 2. Topologie stránek a komponent

### Standalone RAG stránka – layout

```
/rag  (nebo /knowledge-base)
┌──────────────────────────────────────────────────────────────────┐
│  NavMenu (sidebar)                                               │
├──────────────────────┬───────────────────────────────────────────┤
│  LEFT PANEL 320px    │  CENTER PANEL (flex-1)                    │
│  ─────────────────   │  ─────────────────────────────────────    │
│  🗂 Znalostní báze   │                                           │
│  ─────────────────   │   [ CONVERSATION AREA ]                   │
│  Vyhledat dokument   │                                           │
│  ┌──────────────┐    │    Bubble (user)                          │
│  │ Dokument 1   │    │    Bubble (AI) + Citation cards           │
│  │ Dokument 2   │    │    Bubble (user)                          │
│  │ ...          │    │    ...                                    │
│  └──────────────┘    │                                           │
│                      │   ─────────────────────────────────────   │
│  ─────────────────   │   [ CHAT INPUT AREA ]                    │
│  + Přidat dokumenty  │    TextField + Send button + actions      │
│  ⚙ Nastavení         │                                           │
└──────────────────────┴───────────────────────────────────────────┘
```

### Embedded RAG – layout

```
<MudPaper Class="pa-4">
  <MudText Typo="Typo.h6">🤖 AI Chat</MudText>
  [ KB status chip + Embed button ]
  [ MudTextField – otázka ]
  [ Odeslat button ]
  [ Answer MudAlert ]
  [ Citation cards (MudPaper @foreach) ]
</MudPaper>
```

### Strom komponent (Blazor)

```
RagPage.razor                    ← standalone stránka
  ├─ RagKnowledgePanel.razor     ← levý panel: seznam dokumentů/zdrojů
  │   ├─ KbDocumentCard.razor    ← jeden dokument/záznam v KB
  │   └─ KbUploadDropzone.razor  ← nahrání nového dokumentu
  ├─ RagChatPanel.razor          ← pravý panel: konverzace + input
  │   ├─ ChatBubble.razor        ← jedna zpráva (user nebo AI)
  │   ├─ CitationCard.razor      ← jeden citovaný fragment z KB
  │   └─ ChatInputBar.razor      ← input + tlačítka
  └─ RagSettingsDrawer.razor     ← MudDrawer: konfigurace

** Embedded varianta: vše z RagChatPanel.razor přímo vložit do
   nadřazené stránky (ListingDetail.razor, EntityDetail.razor) **
```

---

## 3. Chat UI – hlavní hráč

### Vstupní pole (ChatInputBar)

```razor
<MudStack Spacing="1">

    @* Akce nad inputem (scope, model toggle...) *@
    <MudStack Row="true" Spacing="1" Wrap="Wrap.Wrap">
        @* Pokud je scope volitelný – výběr znalostní báze *@
        @if (AllowScopeSelection)
        {
            <MudSelect T="string?" @bind-Value="_selectedScope"
                       Label="Scope" Variant="Variant.Outlined"
                       Dense="true" Style="min-width:160px;">
                <MudSelectItem T="string?" Value="@(null)">Celá KB</MudSelectItem>
                @foreach (var scope in AvailableScopes)
                {
                    <MudSelectItem T="string?" Value="@scope.Id">@scope.Label</MudSelectItem>
                }
            </MudSelect>
        }
    </MudStack>

    @* Hlavní textové pole *@
    <MudTextField @bind-Value="_question"
                  T="string"
                  Label="@InputLabel"
                  Placeholder="Otázka... (Enter = odeslat, Shift+Enter = nový řádek)"
                  Lines="3"
                  AutoGrow="true"
                  MaxLines="8"
                  Variant="Variant.Outlined"
                  FullWidth="true"
                  Disabled="_loading"
                  OnKeyDown="HandleKeyDown"
                  aria-label="Pole pro otázku k AI"
                  aria-describedby="rag-input-hint" />
    <span id="rag-input-hint" style="display:none;">
        Stiskni Enter pro odeslání, Shift+Enter pro nový řádek
    </span>

    @* Tlačítka *@
    <MudStack Row="true" Spacing="2" Justify="Justify.SpaceBetween" AlignItems="AlignItems.Center">
        <MudText Typo="Typo.caption" Color="Color.Secondary">
            @(_kbCount > 0 ? $"{_kbCount} záznamů v knowledge base" : "Knowledge base prázdná")
        </MudText>
        <MudStack Row="true" Spacing="1">
            @if (!string.IsNullOrWhiteSpace(_question))
            {
                <MudIconButton Icon="@Icons.Material.Filled.Clear"
                               Size="Size.Small"
                               Color="Color.Default"
                               OnClick="() => _question = string.Empty"
                               aria-label="Smazat otázku" />
            }
            <MudButton Variant="Variant.Filled"
                       Color="Color.Primary"
                       StartIcon="@Icons.Material.Filled.Send"
                       Disabled="@(string.IsNullOrWhiteSpace(_question) || _loading)"
                       OnClick="SendAsync"
                       aria-label="Odeslat otázku">
                @if (_loading)
                {
                    <MudProgressCircular Size="Size.Small" Indeterminate="true" Class="mr-2" />
                    <span>Přemýšlím…</span>
                }
                else
                {
                    <span>Zeptat se</span>
                }
            </MudButton>
        </MudStack>
    </MudStack>
</MudStack>
```

**C# logika vstupu:**

```csharp
private string _question = string.Empty;
private bool   _loading  = false;

private async Task HandleKeyDown(KeyboardEventArgs e)
{
    if (e.Key == "Enter" && !e.ShiftKey)
        await SendAsync();
}

private async Task SendAsync()
{
    if (string.IsNullOrWhiteSpace(_question) || _loading) return;

    var q = _question.Trim();
    _question = string.Empty;   // okamžitě vymaž pole
    _loading  = true;
    StateHasChanged();

    try
    {
        await OnAskAsync.InvokeAsync(q);
    }
    finally
    {
        _loading = false;
    }
}
```

---

### Chat bubbles (ChatBubble)

```razor
@* USER bubble – zarovnání vpravo *@
<MudStack Row="true" Justify="Justify.FlexEnd" Class="mb-3">
    <MudPaper Elevation="0"
              Class="pa-3"
              Style="max-width:75%;background:var(--mud-palette-primary);
                     color:var(--mud-palette-primary-text);border-radius:12px 12px 2px 12px;">
        <MudText Typo="Typo.body2" Style="white-space:pre-wrap;">@Message.Content</MudText>
        <MudText Typo="Typo.caption" Style="opacity:.6;font-size:10px;" Class="mt-1">
            @Message.Timestamp.ToString("HH:mm")
        </MudText>
    </MudPaper>
</MudStack>

@* AI bubble – zarovnání vlevo *@
<MudStack Row="true" Justify="Justify.FlexStart" Class="mb-1">
    <MudAvatar Color="Color.Secondary" Size="Size.Small" Class="mr-2 mt-1">AI</MudAvatar>
    <MudStack Spacing="1" Style="max-width:85%;">
        <MudPaper Class="pa-3"
                  Style="background:var(--mud-palette-surface);border-radius:2px 12px 12px 12px;">
            @if (Message.IsStreaming)
            {
                <MudText Typo="Typo.body2" Style="white-space:pre-wrap;">@Message.Content<span class="rag-cursor">▋</span></MudText>
            }
            else
            {
                <MudText Typo="Typo.body2" Style="white-space:pre-wrap;">@Message.Content</MudText>
            }
            <MudStack Row="true" Spacing="1" Class="mt-2" AlignItems="AlignItems.Center">
                <MudText Typo="Typo.caption" Color="Color.Secondary">@Message.Timestamp.ToString("HH:mm")</MudText>
                @if (Message.Citations.Count > 0)
                {
                    <MudChip T="string" Size="Size.Small" Variant="Variant.Outlined"
                             Color="Color.Info" Icon="@Icons.Material.Filled.Source">
                        @Message.Citations.Count zdrojů
                    </MudChip>
                }
                @if (Message.ModelName is not null)
                {
                    <MudChip T="string" Size="Size.Small" Variant="Variant.Text">@Message.ModelName</MudChip>
                }
                <MudIconButton Icon="@Icons.Material.Filled.ContentCopy"
                               Size="Size.Small"
                               Color="Color.Default"
                               OnClick="() => CopyToClipboardAsync(Message.Content)"
                               aria-label="Zkopírovat odpověď" />
            </MudStack>
        </MudPaper>

        @* Citation cards pod bublinou *@
        @if (Message.Citations.Count > 0 && ShowCitations)
        {
            <MudStack Spacing="1" Class="mb-3">
                @foreach (var citation in Message.Citations)
                {
                    <CitationCard Source="@citation" />
                }
            </MudStack>
        }
    </MudStack>
</MudStack>
```

**CSS pro blikající kurzor (streaming):**

```css
/* wwwroot/css/rag.css */
.rag-cursor {
    display: inline-block;
    animation: rag-blink 0.8s step-end infinite;
}

@keyframes rag-blink {
    0%, 100% { opacity: 1; }
    50%       { opacity: 0; }
}
```

---

## 4. Citation cards – zdrojové záznamy

Citation card zobrazuje jeden fragment z KB, který byl použit při generování odpovědi. Je to klíčový prvek RAG UI, který buduje **důvěru uživatele** (lze ověřit, z čeho AI vycházela).

```razor
@* CitationCard.razor *@
@code {
    [Parameter] public RagSourceDto Source { get; set; } = null!;
    [Parameter] public bool Expanded { get; set; } = false;

    private bool _expanded;
    protected override void OnParametersSet() => _expanded = Expanded;
}

<MudPaper Outlined="true"
          Class="pa-3 mb-1"
          Style="border-radius:8px;border-color:var(--mud-palette-divider);">
    <MudStack Spacing="1">

        @* Hlavička *@
        <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2">
            @* Score chip *@
            <MudTooltip Text="@($"Cosine similarity: {Source.Similarity:F3}")">
                <MudChip T="string"
                         Color="@GetScoreColor(Source.Similarity)"
                         Size="Size.Small"
                         Variant="Variant.Filled"
                         Icon="@Icons.Material.Filled.Analytics">
                    @Source.Similarity.ToString("P0")
                </MudChip>
            </MudTooltip>

            @* Relevance bar *@
            <MudProgressLinear Value="@(Source.Similarity * 100)"
                               Color="@GetScoreColor(Source.Similarity)"
                               Rounded="true"
                               Size="Size.Small"
                               Style="flex:1;max-width:80px;" />

            @* Titulek záznamu *@
            <MudText Typo="Typo.body2"
                     Style="font-weight:600;flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">
                @(Source.Title ?? "–")
            </MudText>

            @* Source badge *@
            <MudChip T="string"
                     Size="Size.Small"
                     Variant="Variant.Outlined"
                     Color="@GetSourceColor(Source.Source)">
                @GetSourceLabel(Source.Source)
            </MudChip>

            @* Datum *@
            <MudText Typo="Typo.caption" Color="Color.Secondary" Style="white-space:nowrap;">
                @Source.CreatedAt.ToString("dd.MM.yy")
            </MudText>

            @* Expand toggle *@
            <MudIconButton Icon="@(_expanded ? Icons.Material.Filled.ExpandLess : Icons.Material.Filled.ExpandMore)"
                           Size="Size.Small"
                           OnClick="() => _expanded = !_expanded"
                           aria-expanded="@_expanded.ToString().ToLower()"
                           aria-label="@(_expanded ? "Sbalit fragment" : "Rozbalit fragment")" />
        </MudStack>

        @* Excerpt – vždy viditelný *@
        <MudText Typo="Typo.caption"
                 Color="Color.Secondary"
                 Style="@(_expanded ? "" : "display:-webkit-box;-webkit-line-clamp:3;-webkit-box-orient:vertical;overflow:hidden;")">
            @Source.ContentExcerpt
        </MudText>

        @* Plný obsah – rozbalitelný *@
        @if (_expanded && !string.IsNullOrEmpty(Source.FullContent))
        {
            <MudDivider Class="my-1" />
            <MudText Typo="Typo.caption" Style="white-space:pre-wrap;">@Source.FullContent</MudText>
        }
    </MudStack>
</MudPaper>
```

### Pravidla pro barevnost skóre

```csharp
private static Color GetScoreColor(double similarity) => similarity switch
{
    >= 0.85 => Color.Success,   // výborná shoda
    >= 0.65 => Color.Warning,   // dobrá shoda
    _       => Color.Error      // slabá shoda (zobrazovat opatrně)
};

// Minimální threshold pro zobrazení citation card: 0.50
// Záznamy s similarity < 0.50 nezobrazovat (jsou irelevantní)
```

### Pravidla pro zdrojový badge

```csharp
private static Color GetSourceColor(string source) => source switch
{
    "claude"    => Color.Secondary,
    "user"      => Color.Tertiary,
    "mcp"       => Color.Info,
    "ai"        => Color.Secondary,
    "import"    => Color.Default,
    _           => Color.Default
};

private static string GetSourceLabel(string source) => source switch
{
    "claude"    => "Claude",
    "user"      => "Ručně",
    "mcp"       => "MCP",
    "ai"        => "AI",
    "import"    => "Import",
    _           => source
};
```

---

## 5. Knowledge base management

### Panel správy KB (KnowledgePanel)

Zobrazuje přehled záznamů v KB s možností správy.

```razor
<MudStack Spacing="2">

    @* Header s akcemi *@
    <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2">
        <MudText Typo="Typo.subtitle1" Style="flex:1;">🗂 Znalostní báze</MudText>
        <MudChip T="string" Size="Size.Small" Color="Color.Info">
            @_docs.Count záznamů
        </MudChip>
        <MudIconButton Icon="@Icons.Material.Filled.Refresh"
                       Size="Size.Small"
                       OnClick="LoadAsync"
                       aria-label="Obnovit seznam" />
    </MudStack>

    @* Vyhledávání *@
    <MudTextField @bind-Value="_filter"
                  T="string"
                  Placeholder="Hledat záznamy…"
                  Adornment="Adornment.Start"
                  AdornmentIcon="@Icons.Material.Filled.Search"
                  Variant="Variant.Outlined"
                  Dense="true"
                  Clearable="true"
                  aria-label="Filtr záznamů v knowledge base" />

    @* Embedding status + bulk akce *@
    @if (_unembedded > 0)
    {
        <MudAlert Severity="Severity.Warning" Dense="true" Class="py-1 px-2">
            @_unembedded záznamů bez embeddingu
            <MudButton Variant="Variant.Text" Size="Size.Small"
                       OnClick="BulkEmbedAsync" Class="ml-2">
                Embedovat vše
            </MudButton>
        </MudAlert>
    }

    @* Seznam záznamů *@
    <div role="list" aria-label="Záznamy v knowledge base">
        @foreach (var doc in FilteredDocs)
        {
            <KbDocumentCard Doc="@doc"
                            OnDelete="() => DeleteDocAsync(doc.Id)"
                            OnEdit="() => OpenEditAsync(doc)" />
        }
    </div>

    @if (!_loading && FilteredDocs.Count == 0)
    {
        <MudText Typo="Typo.body2" Color="Color.Secondary" Align="Align.Center" Class="mt-4">
            @(string.IsNullOrEmpty(_filter)
                ? "Žádné záznamy v knowledge base. Přidej první dokument níže."
                : "Žádné záznamy neodpovídají filtru.")
        </MudText>
    }

    <MudDivider />

    @* Tlačítko pro přidání *@
    <MudButton Variant="Variant.Outlined"
               StartIcon="@Icons.Material.Filled.Add"
               FullWidth="true"
               OnClick="OpenAddDocumentAsync">
        Přidat dokument / záznam
    </MudButton>
</MudStack>
```

### KbDocumentCard

```razor
@* KbDocumentCard.razor *@
<MudPaper Outlined="true"
          Class="pa-2 mb-1"
          Style="border-radius:6px;"
          role="listitem">
    <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="1">
        <MudIcon Icon="@GetDocIcon(Doc.Source)"
                 Size="Size.Small"
                 Color="Color.Secondary"
                 aria-hidden="true" />
        <MudStack Spacing="0" Style="flex:1;overflow:hidden;">
            <MudText Typo="Typo.body2"
                     Style="font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">
                @(Doc.Title ?? "Bez názvu")
            </MudText>
            <MudStack Row="true" Spacing="1" AlignItems="AlignItems.Center">
                @if (Doc.HasEmbedding)
                {
                    <MudIcon Icon="@Icons.Material.Filled.Verified"
                             Size="Size.Small"
                             Color="Color.Success"
                             Style="font-size:12px;"
                             aria-label="Embedováno" />
                }
                else
                {
                    <MudIcon Icon="@Icons.Material.Filled.WarningAmber"
                             Size="Size.Small"
                             Color="Color.Warning"
                             Style="font-size:12px;"
                             aria-label="Bez embeddingu" />
                }
                <MudText Typo="Typo.caption" Color="Color.Secondary">
                    @Doc.CreatedAt.ToString("dd.MM.yyyy") · @Doc.Source
                </MudText>
            </MudStack>
        </MudStack>
        <MudIconButton Icon="@Icons.Material.Filled.Edit"
                       Size="Size.Small"
                       OnClick="() => OnEdit.InvokeAsync()"
                       aria-label="Upravit záznam" />
        <MudIconButton Icon="@Icons.Material.Filled.Delete"
                       Size="Size.Small"
                       Color="Color.Error"
                       OnClick="() => OnDelete.InvokeAsync()"
                       aria-label="Smazat záznam" />
    </MudStack>
</MudPaper>
```

---

## 6. Document ingestion UI

### Dialog pro přidání záznamu

```razor
<MudDialog>
    <TitleContent>
        <MudText Typo="Typo.h6">Přidat záznam do Knowledge Base</MudText>
    </TitleContent>
    <DialogContent>
        <MudStack Spacing="3">

            @* Způsob vložení *@
            <MudButtonGroup Variant="Variant.Outlined" Size="Size.Small" FullWidth="true">
                <MudButton Color="@(_mode == "text" ? Color.Primary : Color.Default)"
                           OnClick="() => _mode = "text"">
                    ✏️ Přímý text
                </MudButton>
                <MudButton Color="@(_mode == "file" ? Color.Primary : Color.Default)"
                           OnClick="() => _mode = "file"">
                    📎 Soubor
                </MudButton>
                <MudButton Color="@(_mode == "url" ? Color.Primary : Color.Default)"
                           OnClick="() => _mode = "url"">
                    🌐 URL
                </MudButton>
            </MudButtonGroup>

            @* Název záznamu *@
            <MudTextField @bind-Value="_title"
                          T="string"
                          Label="Název / titulek záznamu"
                          Placeholder="Např. Analýza lokality Pohořelice 2026"
                          Variant="Variant.Outlined"
                          Required="true"
                          RequiredError="Název je povinný"
                          aria-required="true" />

            @* Zdroj (source tag) *@
            <MudSelect T="string" @bind-Value="_source"
                       Label="Zdroj"
                       Variant="Variant.Outlined"
                       Required="true"
                       aria-required="true">
                <MudSelectItem T="string" Value="@("user")">Ručně zadaný</MudSelectItem>
                <MudSelectItem T="string" Value="@("import")">Import ze souboru</MudSelectItem>
                <MudSelectItem T="string" Value="@("claude")">Analýza z Clauda</MudSelectItem>
                <MudSelectItem T="string" Value="@("ai")">Jiná AI</MudSelectItem>
            </MudSelect>

            @* Text mode *@
            @if (_mode == "text")
            {
                <MudTextField @bind-Value="_content"
                              T="string"
                              Label="Obsah záznamu"
                              Placeholder="Vlož text záznamu…"
                              Lines="8"
                              Variant="Variant.Outlined"
                              FullWidth="true"
                              Required="true"
                              RequiredError="Obsah je povinný"
                              aria-required="true"
                              aria-describedby="content-hint" />
                <span id="content-hint" style="font-size:12px;color:var(--mud-palette-text-secondary);">
                    Tip: Vlož výsledek AI analýzy (např. z Claude.ai) přímo sem.
                </span>
            }

            @* File mode *@
            @if (_mode == "file")
            {
                <MudFileUpload T="IBrowserFile"
                               FilesChanged="HandleFileSelected"
                               Accept=".txt,.md,.pdf,.docx"
                               aria-label="Nahrát soubor do knowledge base">
                    <ActivatorContent>
                        <MudButton HtmlTag="label"
                                   Variant="Variant.Outlined"
                                   StartIcon="@Icons.Material.Filled.CloudUpload"
                                   FullWidth="true">
                            Vybrat soubor (.txt, .md, .pdf, .docx)
                        </MudButton>
                    </ActivatorContent>
                </MudFileUpload>
                @if (_selectedFile is not null)
                {
                    <MudAlert Severity="Severity.Info" Dense="true">
                        @_selectedFile.Name (@(_selectedFile.Size / 1024) KB)
                    </MudAlert>
                }
            }

            @* URL mode *@
            @if (_mode == "url")
            {
                <MudTextField @bind-Value="_url"
                              T="string"
                              Label="URL stránky"
                              Placeholder="https://..."
                              Adornment="Adornment.Start"
                              AdornmentIcon="@Icons.Material.Filled.Link"
                              Variant="Variant.Outlined"
                              InputType="InputType.Url" />
                <MudText Typo="Typo.caption" Color="Color.Secondary">
                    Obsah stránky bude stažen a přidán jako záznam. Funguje pro veřejné HTML stránky.
                </MudText>
            }

            @* Metadata *@
            <MudExpansionPanel Text="Rozšířená metadata (volitelné)">
                <MudStack Spacing="2">
                    <MudTextField @bind-Value="_tags"
                                  T="string"
                                  Label="Tagy (oddělené čárkou)"
                                  Placeholder="lokalita, cena, renovace"
                                  Variant="Variant.Outlined" />
                    <MudDatePicker @bind-Date="_validUntil"
                                   Label="Platnost dokumentu do"
                                   Variant="Variant.Outlined" />
                </MudStack>
            </MudExpansionPanel>

        </MudStack>
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Zrušit</MudButton>
        <MudButton Color="Color.Primary"
                   Variant="Variant.Filled"
                   OnClick="SubmitAsync"
                   Disabled="_saving">
            @if (_saving)
            {
                <MudProgressCircular Size="Size.Small" Indeterminate="true" Class="mr-2" />
                <span>Ukládám…</span>
            }
            else
            {
                <span>Přidat do KB</span>
            }
        </MudButton>
    </DialogActions>
</MudDialog>
```

### Drag & drop nahrávání (KbUploadDropzone)

```razor
@* KbUploadDropzone.razor *@
<div @ref="_dropzone"
     class="rag-dropzone @(_dragging ? "rag-dropzone--over" : "")"
     role="region"
     aria-label="Oblast pro přetažení souborů"
     tabindex="0"
     @onkeydown="HandleDropzoneKeyDown">
    <MudIcon Icon="@Icons.Material.Filled.CloudUpload"
             Size="Size.Large"
             Color="@(_dragging ? Color.Primary : Color.Secondary)"
             aria-hidden="true" />
    <MudText Typo="Typo.body2" Color="Color.Secondary" Align="Align.Center">
        Přetáhni .txt / .md / .pdf soubor sem<br />
        nebo <MudLink OnClick="OpenFilePicker">vyber soubor</MudLink>
    </MudText>
</div>
```

```css
/* rag.css */
.rag-dropzone {
    border: 2px dashed var(--mud-palette-divider);
    border-radius: 8px;
    padding: 24px;
    text-align: center;
    cursor: pointer;
    transition: all 0.2s ease;
    background: transparent;
}

.rag-dropzone--over {
    border-color: var(--mud-palette-primary);
    background: color-mix(in srgb, var(--mud-palette-primary) 10%, transparent);
}

.rag-dropzone:focus-visible {
    outline: 2px solid var(--mud-palette-primary);
    outline-offset: 2px;
}
```

---

## 7. Conversation history

### Kdy ukládat historii

| Typ RAG | Historie | Odkud načítat |
|---------|----------|---------------|
| Embedded (v detailu entity) | SessionStorage – přežije F5, nikoliv zavření tabu | `ProtectedSessionStorage` |
| Standalone stránka | DB – `rag_conversations` tabulka | `.NET API endpoint` |
| Anonymous session | SessionStorage | `ProtectedSessionStorage` |

### Datový model konverzace (C#)

```csharp
public record RagConversation(
    Guid    Id,
    string  Title,           // auto-generovaný z první otázky (prvních 60 znaků)
    string? Scope,           // null = global, jinak entity_id nebo tag
    List<RagMessage> Messages,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record RagMessage(
    Guid    Id,
    string  Role,            // "user" | "assistant"
    string  Content,
    List<RagSourceDto> Citations,
    string? ModelName,
    bool    IsStreaming,
    DateTime Timestamp
);
```

### Zobrazení v postranním panelu

```razor
@* Skupiny: Dnes · Tento týden · Starší *@
@foreach (var (groupLabel, conversations) in GroupedConversations)
{
    <MudText Typo="Typo.overline" Color="Color.Secondary" Class="px-2 mt-2">
        @groupLabel
    </MudText>
    @foreach (var conv in conversations)
    {
        <MudButton FullWidth="true"
                   Variant="Variant.Text"
                   Class="justify-start px-2 py-1"
                   Style="text-transform:none;"
                   Color="@(_activeId == conv.Id ? Color.Primary : Color.Default)"
                   OnClick="() => LoadConversationAsync(conv.Id)">
            <MudStack Row="true" AlignItems="AlignItems.Center" Style="width:100%;" Spacing="1">
                <MudIcon Icon="@Icons.Material.Filled.Chat"
                         Size="Size.Small"
                         aria-hidden="true" />
                <MudText Typo="Typo.body2"
                         Style="flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;text-align:left;">
                    @conv.Title
                </MudText>
                <MudText Typo="Typo.caption" Color="Color.Secondary">
                    @conv.UpdatedAt.ToString("dd.MM")
                </MudText>
            </MudStack>
        </MudButton>
    }
}
```

---

## 8. Settings & konfigurace

### RagSettingsDrawer

Zobrazujeme jako `MudDrawer Anchor="Anchor.Right"` z tlačítka `⚙` v toolbaru.

```razor
<MudDrawer @bind-Open="_settingsOpen"
           Anchor="Anchor.Right"
           Variant="DrawerVariant.Temporary"
           Width="360px"
           aria-label="Nastavení RAG systému">
    <MudDrawerHeader>
        <MudText Typo="Typo.h6">⚙ Nastavení RAG</MudText>
    </MudDrawerHeader>
    <MudStack Class="pa-4" Spacing="4">

        @* Embedding provider *@
        <fieldset style="border:none;padding:0;margin:0;">
            <legend style="font-size:14px;font-weight:600;margin-bottom:8px;">Embedding provider</legend>
            <MudRadioGroup T="string" @bind-Value="_settings.EmbeddingProvider">
                <MudRadio T="string" Value="@("ollama")" Color="Color.Primary">
                    Ollama (lokální · nomic-embed-text)
                </MudRadio>
                <MudRadio T="string" Value="@("openai")" Color="Color.Primary">
                    OpenAI (text-embedding-3-small)
                </MudRadio>
            </MudRadioGroup>
        </fieldset>

        @* Chat model *@
        <MudSelect T="string" @bind-Value="_settings.ChatModel"
                   Label="Chat model"
                   Variant="Variant.Outlined"
                   aria-label="Výběr chat modelu">
            <MudSelectItem T="string" Value="@("qwen2.5:14b")">qwen2.5:14b (lokální)</MudSelectItem>
            <MudSelectItem T="string" Value="@("llama3.1:8b")">llama3.1:8b (rychlejší)</MudSelectItem>
            <MudSelectItem T="string" Value="@("gpt-4o-mini")">gpt-4o-mini (OpenAI)</MudSelectItem>
            <MudSelectItem T="string" Value="@("gpt-4o")">gpt-4o (OpenAI, premium)</MudSelectItem>
        </MudSelect>

        @* Počet citací *@
        <MudStack>
            <MudText Typo="Typo.body2">Počet výsledků z KB: @_settings.TopK</MudText>
            <MudSlider T="int" @bind-Value="_settings.TopK"
                       Min="1" Max="15" Step="1"
                       Color="Color.Primary"
                       aria-label="Počet výsledků z knowledge base" />
        </MudStack>

        @* Similarity threshold *@
        <MudStack>
            <MudText Typo="Typo.body2">Min. similarity: @_settings.MinSimilarity.ToString("P0")</MudText>
            <MudSlider T="double" @bind-Value="_settings.MinSimilarity"
                       Min="0.3" Max="0.95" Step="0.05"
                       Color="Color.Primary"
                       aria-label="Minimální cosine similarity pro zobrazení citace" />
        </MudStack>

        @* Temperature *@
        <MudStack>
            <MudText Typo="Typo.body2">Temperature: @_settings.Temperature.ToString("F1")</MudText>
            <MudSlider T="double" @bind-Value="_settings.Temperature"
                       Min="0.0" Max="1.0" Step="0.1"
                       Color="Color.Primary"
                       aria-label="Temperature LLM modelu" />
            <MudText Typo="Typo.caption" Color="Color.Secondary">
                0.0 = deterministický · 0.7 = kreativní · 1.0 = maximálně kreativní
            </MudText>
        </MudStack>

        @* Streaming toggle *@
        <MudSwitch T="bool" @bind-Value="_settings.StreamingEnabled"
                   Label="Streamovat odpovědi"
                   Color="Color.Primary" />

        <MudDivider />

        <MudButton Variant="Variant.Filled"
                   Color="Color.Primary"
                   OnClick="SaveSettingsAsync"
                   FullWidth="true">
            Uložit nastavení
        </MudButton>
    </MudStack>
</MudDrawer>
```

---

## 9. Stavy UI: loading · thinking · error · empty

### Thinking state (AI přemýšlí)

```razor
@* Zobrazit jako poslední zprávu v konverzaci, dokud nepřijde odpověď *@
<MudStack Row="true" Justify="Justify.FlexStart" Class="mb-3">
    <MudAvatar Color="Color.Secondary" Size="Size.Small" Class="mr-2 mt-1">AI</MudAvatar>
    <MudPaper Class="pa-3"
              Style="background:var(--mud-palette-surface);border-radius:2px 12px 12px 12px;">
        <MudStack Row="true" Spacing="1" AlignItems="AlignItems.Center">
            <MudProgressCircular Size="Size.Small" Indeterminate="true" Color="Color.Secondary" />
            <MudText Typo="Typo.body2" Color="Color.Secondary">
                Vyhledávám v knowledge base…
            </MudText>
        </MudStack>
    </MudPaper>
</MudStack>
```

**Progressive thinking messages** (pokud je k dispozici server-sent status):

| Fáze | Zpráva |
|------|--------|
| Embedding otázky | Analyzuji otázku… |
| Vector search | Vyhledávám v knowledge base… |
| Context assembly | Sestavuji kontext… |
| LLM inference | Generuji odpověď… |
| Done | (zpráva se nahradí odpovědí) |

### Error states

```razor
@* Chyba – Ollama nedostupná *@
<MudAlert Severity="Severity.Error" Dense="false">
    <MudText Typo="Typo.subtitle2">AI model není dostupný</MudText>
    <MudText Typo="Typo.body2">
        Nepodařilo se připojit k Ollama. Zkontroluj, zda Ollama běží na portu 11434.
    </MudText>
    <MudButton Size="Size.Small" Variant="Variant.Text" OnClick="RetryAsync" Class="mt-1">
        Zkusit znovu
    </MudButton>
</MudAlert>

@* Chyba – Knowledge base prázdná *@
<MudAlert Severity="Severity.Info">
    <MudText Typo="Typo.subtitle2">Knowledge base je prázdná</MudText>
    <MudText Typo="Typo.body2">
        Přidej záznamy do knowledge base, aby AI mohla odpovídat s kontextem.
        Bez záznamů odpovídá AI pouze z obecných znalostí.
    </MudText>
</MudAlert>

@* Chyba – Embedding selhal *@
<MudAlert Severity="Severity.Warning">
    Embedding záznamu se nezdařil. Záznam byl uložen, ale nebude vyhledatelný.
    <MudButton Size="Size.Small" Variant="Variant.Text" OnClick="RetryEmbedAsync">
        Zkusit embedovat
    </MudButton>
</MudAlert>
```

### Empty state

```razor
@* Prázdná konverzace – start screen *@
@if (_messages.Count == 0)
{
    <MudStack AlignItems="AlignItems.Center" Justify="Justify.Center" Style="height:100%;" Spacing="3">
        <MudIcon Icon="@Icons.Material.Filled.QuestionAnswer"
                 Style="font-size:64px;"
                 Color="Color.Secondary"
                 aria-hidden="true" />
        <MudText Typo="Typo.h6" Align="Align.Center">Jak ti mohu pomoci?</MudText>
        <MudText Typo="Typo.body2" Color="Color.Secondary" Align="Align.Center" Style="max-width:400px;">
            Ptej se na cokoli z nahrané knowledge base.
            Odpovědi jsou vždy doplněny zdrojovými záznamy.
        </MudText>

        @* Suggested questions – maximálně 4 *@
        <MudStack Row="true" Wrap="Wrap.Wrap" Justify="Justify.Center" Spacing="2">
            @foreach (var suggestion in SuggestedQuestions)
            {
                <MudButton Variant="Variant.Outlined"
                           Size="Size.Small"
                           Color="Color.Default"
                           Style="text-transform:none;"
                           OnClick="() => UseQuestion(suggestion)">
                    @suggestion
                </MudButton>
            }
        </MudStack>
    </MudStack>
}
```

---

## 10. Streaming odpovědí

Streaming je klíčový pro UX – uživatel vidí odpověď jak přichází, nikoliv čeká 10 s na nic.

### Backend (Server-Sent Events)

```csharp
// .NET API endpoint
app.MapGet("/api/rag/stream", async (
    string question,
    string? scope,
    HttpContext ctx,
    IRagService rag,
    CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";

    await foreach (var chunk in rag.StreamAnswerAsync(question, scope, ct))
    {
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
});
```

### Frontend (Blazor – SSE reader)

```csharp
// V RagChatPanel.razor
private async Task SendStreamingAsync(string question)
{
    var assistantMsg = new RagMessage(
        Guid.NewGuid(), "assistant", string.Empty,
        [], null, IsStreaming: true, DateTime.Now);
    _messages.Add(assistantMsg);

    using var response = await Http.GetAsync(
        $"/api/rag/stream?question={Uri.EscapeDataString(question)}",
        HttpCompletionOption.ResponseHeadersRead);

    using var stream   = await response.Content.ReadAsStreamAsync();
    using var reader   = new StreamReader(stream);

    while (!reader.EndOfStream)
    {
        var line = await reader.ReadLineAsync();
        if (line is null || !line.StartsWith("data: ")) continue;

        var json  = line[6..];
        var chunk = JsonSerializer.Deserialize<RagStreamChunk>(json);

        if (chunk?.Type == "token")
        {
            assistantMsg = assistantMsg with { Content = assistantMsg.Content + chunk.Value };
            _messages[^1] = assistantMsg;
            await InvokeAsync(StateHasChanged);
        }
        else if (chunk?.Type == "citations")
        {
            assistantMsg = assistantMsg with
            {
                Citations   = chunk.Citations ?? [],
                IsStreaming = false
            };
            _messages[^1] = assistantMsg;
        }
    }

    await InvokeAsync(StateHasChanged);
}
```

### Datový model chunks

```csharp
public record RagStreamChunk(
    string Type,             // "token" | "citations" | "error" | "done"
    string? Value,           // pro Type == "token"
    List<RagSourceDto>? Citations,  // pro Type == "citations"
    string? Error            // pro Type == "error"
);
```

---

## 11. Přístupnost (WCAG 2.2 AA)

### Povinné atributy na každou komponentu

| Prvek | Povinné ARIA |
|-------|-------------|
| Chat input textarea | `aria-label`, `aria-describedby` (hint pro Enter/Shift+Enter) |
| Send button (v disabled stavu) | `aria-disabled="true"` (MudButton to dělá automaticky) |
| Citation card expand button | `aria-expanded`, `aria-label` |
| Knowledge base list | `role="list"` na wrapperu, `role="listitem"` na každé kartě |
| Progress (embed/loading) | `aria-live="polite"` na status textu |
| Thinking state | `role="status"` |
| Conversation area | `aria-live="polite"` + `aria-atomic="false"` |
| Settings drawer | `aria-label` na MudDrawer |

### Live region pro nové zprávy

```razor
<div aria-live="polite"
     aria-relevant="additions"
     aria-atomic="false"
     id="rag-conversation"
     style="overflow-y:auto;flex:1;">
    @foreach (var msg in _messages)
    {
        <ChatBubble Message="@msg" />
    }
</div>
```

### Focus management

```csharp
// Po odeslání otázky: zachovat focus na input poli
// Po přidání záznamu do KB: focus zpět na input chatu
// Po otevření dialogs: focus na první focusable element (MudDialog to řeší automaticky)
// Po zavření dialogu: focus zpět na trigger button

protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (_focusInput)
    {
        await _inputRef.FocusAsync();
        _focusInput = false;
    }
}
```

### Keyboard navigation

| Klávesa | Akce |
|---------|------|
| `Enter` (v input) | Odeslat otázku |
| `Shift+Enter` (v input) | Nový řádek |
| `Escape` | Zrušit loading / zavřít drawer |
| `Tab` | Navigace przez prvky |
| `/` (standalone stránka) | Focus na chat input (jak v ChatGPT) |

---

## 12. State management v Blazor

### Co kam ukládat

| Stav | Úložiště | Proč |
|------|----------|------|
| Aktuální konverzace (embedded) | `ProtectedSessionStorage` | Přežije F5, ne zavření tabu |
| Nastavení RAG (model, TopK...) | `ProtectedLocalStorage` | Trvalé preference uživatele |
| Standalone konverzace | `.NET API + DB` | Sdílení, multi-device |
| Filter v KB panelu | In-memory `_filter` | Nepersistuje, reset při navigaci |

### Vzor pro state v RagChatPanel

```csharp
@code {
    // Parametry (pokud je embedded)
    [Parameter] public Guid? EntityId { get; set; }
    [Parameter] public string? InitialScope { get; set; }
    [Parameter] public IReadOnlyList<string>? SuggestedQuestions { get; set; }

    // Konverzace
    private List<RagMessage> _messages   = [];
    private string           _question   = string.Empty;
    private bool             _loading    = false;
    private bool             _focusInput = false;

    // KB metadata
    private int    _kbCount     = 0;
    private int    _unembedded  = 0;
    private bool   _kbLoaded    = false;

    // Settings
    private RagSettings _settings = new();

    // CancellationToken (IDisposable pattern)
    private CancellationTokenSource _cts = new();

    protected override async Task OnInitializedAsync()
    {
        await LoadKbMetaAsync();
        await LoadSettingsAsync();
    }

    public void Dispose() => _cts.Cancel();
}
```

---

## 13. MudBlazor 9 – konkrétní komponenty

### Co rozhodně použít

| Usecase | Komponenta | Klíčové parametry |
|---------|-----------|-------------------|
| Chat input | `MudTextField` | `Lines=3 AutoGrow=true MaxLines=8` |
| Odeslat button | `MudButton` | `Variant.Filled Color.Primary` |
| Citation score | `MudChip T="string"` | `Color=@GetScoreColor(...)` |
| Relevance bar | `MudProgressLinear` | `Value=@(sim*100) Rounded=true Size.Small` |
| Loading v buttonu | `MudProgressCircular` | `Size.Small Indeterminate=true Class="mr-2"` |
| KB seznam | `MudPaper Outlined=true` | `border-radius:6px` |
| Settings | `MudDrawer` | `Anchor.Right Variant.Temporary Width="360px"` |
| Source select | `MudSelect T="string"` | vždy s explicitním `T=` |
| Ingestion dialog | `MudDialog` | s `TitleContent` a `DialogActions` |
| Model toggle | `MudButtonGroup` | `Variant.Outlined Size.Small FullWidth=true` |
| Expand/collapse | `MudIconButton` | s `aria-expanded` |

### Co nepoužívat

- `MudChip` bez `T="string"` (způsobí warning v MudBlazor 9)
- `MudCarousel` bez `TData="object"` (type inference bug)
- `@onclick` na `<div>` bez `tabindex="0"` (a11y)
- `style="overflow:hidden"` na scroll containeru konverzace (použij `overflow-y:auto`)

---

## 14. Integrace s backendem (.NET API)

### Endpointy (standardizovat napříč projekty)

```
# RAG query
POST /api/rag/ask
     Body: { question, scope?, topK?, minSimilarity?, model? }
     Resp: { answer, sources: [{ id, title, contentExcerpt, similarity, source, createdAt }], modelName }

# RAG streaming
GET  /api/rag/stream?question=&scope=
     Content-Type: text/event-stream

# Knowledge base – CRUD
GET    /api/kb/documents?scope=&page=&pageSize=
POST   /api/kb/documents           # přidání záznamu
GET    /api/kb/documents/{id}
PUT    /api/kb/documents/{id}
DELETE /api/kb/documents/{id}

# Embed
POST   /api/kb/documents/{id}/embed        # embedovat konkrétní záznam
POST   /api/kb/documents/bulk-embed        # embedovat vše bez embeddingu (batch)

# KB metadata
GET    /api/kb/stats?scope=
       Resp: { total, embedded, unembedded, lastUpdated }

# Konverzace (volitelné – pro standalone)
GET    /api/rag/conversations
POST   /api/rag/conversations
DELETE /api/rag/conversations/{id}
```

### DTO vzory

```csharp
// Request
public record RagAskRequest(
    string  Question,
    string? Scope       = null,
    int     TopK        = 5,
    double  MinSimilarity = 0.50,
    string? Model       = null
);

// Response
public record RagAnswerResponse(
    string             Answer,
    List<RagSourceDto> Sources,
    string?            ModelName,
    double             LatencyMs
);

public record RagSourceDto(
    Guid     Id,
    string?  Title,
    string   ContentExcerpt,
    string?  FullContent,       // null pro performance; volitelně expandovat
    double   Similarity,
    string   Source,
    DateTime CreatedAt
);

// KB entry
public record KbDocumentDto(
    Guid      Id,
    string?   Title,
    string    ContentExcerpt,
    bool      HasEmbedding,
    string    Source,
    string?   Scope,
    DateTime  CreatedAt
);
```

---

## 15. Kontextový (embedded) RAG vs. standalone

### Srovnání

| Aspekt | Embedded (v detailu entity) | Standalone stránka |
|--------|----------------------------|-------------------|
| Scope | Fixovaný na `entity_id` | Uživatel volí scope |
| KB panel | Není (skrytý) | Levý panel s dokumenty |
| Conversation history | SessionStorage | DB |
| Suggested questions | Hardcoded pro daný typ entity | Generické nebo z KB |
| Settings | Skryté / globální | Viditelné tlačítko ⚙ |
| URL | Součást stránky entity | `/rag`, `/knowledge-base` |

### Sdílené komponenty

Pro maximální znovupoužitelnost:

```
CitationCard.razor          ← identický v obou
ChatBubble.razor            ← identický v obou
KbDocumentCard.razor        ← použijen i v embedded (hidden panel)
ChatInputBar.razor          ← parametrizovaný (AllowScopeSelection, InputLabel)
RagChatPanel.razor          ← použitelný jako embedded i jako stránka
```

### Embedding (RealEstateAggregator specifika)

V RealEstateAggregator je embedded RAG v `ListingDetail.razor` – záložka AI Chat. Doporučené chování:
- `scope = listingId.ToString()` – izoluje KB na jeden inzerát
- První otázka auto-embeduje popis inzerátu (tlačítko „Embedovat popis")
- Suggested questions: `["Jaké jsou nevýhody?", "Je cena přiměřená?", "Vhodné pro rodinu?", "Co bylo renovováno?"]`

---

## 16. Checklist před releasem

### Funkční

- [ ] Odeslání otázky Enterem funguje (ne Shift+Enter)
- [ ] Loading spinner se zobrazí do 200 ms od kliknutí
- [ ] Citation cards se zobrazí jen pro similarity ≥ 0.50
- [ ] Prázdná KB zobrazí info alert, ne chybu
- [ ] Offline Ollama zobrazí srozumitelnou chybu + Retry button
- [ ] Embedding záznamu aktualizuje KB stats okamžitě
- [ ] Smazání záznamu z KB vyžaduje potvrzení (MudDialog)
- [ ] Streaming: blikající kurzor mizí po dokončení

### Přístupnost

- [ ] Celý chat ovladatelný bez myši (Tab, Enter, Escape)
- [ ] Screen reader oznamuje nové AI zprávy (`aria-live`)
- [ ] Citation card expand/collapse má `aria-expanded`
- [ ] Input má `aria-label` a `aria-describedby`
- [ ] Kontrast textu na citation chips ≥ 4.5:1

### Výkon

- [ ] KB seznam virtualizovaný pro > 50 položek (`MudVirtualize`)
- [ ] Citation cards nezobrazují `FullContent` dokud uživatel neexpanduje
- [ ] Streaming se nezasekne při rapid re-render (rate limit: max 60 fps = `await Task.Delay(16)` ve smyčce)

### Bezpečnost

- [ ] Question text je escapován před odesláním do LLM (žádná raw string concatenation SQL)
- [ ] Nahraný soubor validuje Content-Type i příponu
- [ ] API key pro scraping endpointy nesouvisí s RAG endpointy (oddělené skupiny)

---

*Dokument připraven: 1. března 2026*  
*Autor: AI-assisted design (GitHub Copilot), Petr Šrámek*  
*Relevantní soubory: `src/RealEstate.App/Components/Pages/ListingDetail.razor` (referenční implementace embedded RAG), `docs/RAG_MCP_DESIGN.md` (backend design), `mcp/server.py` (MCP integrace)*
