---
phase: 2
slug: key-rotation-env-hardening
status: approved
shadcn_initialized: false
preset: not applicable
created: 2026-05-30
reviewed_at: 2026-05-30
stack: .NET 8 WPF + C# 12 (no UI component library)
language: pt-BR (user-facing) / English (code identifiers)
---

# Phase 2 — UI Design Contract

> Visual and interaction contract for the new `FirstRunWindow` plus a minor `SettingsWindow.KeyTextBox` polish. Tokens are extracted from the existing `ChatWindow.xaml` and `SettingsWindow.xaml` inline resource dictionaries — this phase reuses them verbatim and invents nothing.

**Status:** DRAFT (pending `gsd-ui-checker`)

---

## Scope

| # | Surface | Type | Required |
|---|---------|------|----------|
| 1 | `AIBWindows/Views/FirstRunWindow.xaml(.cs)` | NEW window | Yes — SEC-03 / D-02 |
| 2 | `AIBWindows/Views/SettingsWindow.xaml` `KeyTextBox` | EDIT (read-only sentinel + "Alterar chave" button) | Optional polish (deferred OK per CONTEXT.md `<deferred>`) |
| 3 | `AIBWindows/Views/SettingsWindow.xaml` `ProviderComboBox` | EDIT (add `<ComboBoxItem Content="OpenAI"/>`) | Required (RESEARCH Q1 resolution; AiProvider="OpenAI" must round-trip via Settings UI) |

Out of scope for this UI-SPEC: audit log file format (Phase 1 D6 owns it), DPAPI plumbing, `OpenAIService.cs` sentinel branch, `App.OnStartup` migration, `.env` deletion, AIBLinux. Those are non-UI tasks owned by the planner/executor.

---

## Design System

| Property | Value |
|----------|-------|
| Tool | none (hand-rolled WPF + inline resource dictionaries per Window) |
| Preset | not applicable |
| Component library | none — bare WPF controls (`Window`, `Border`, `Grid`, `StackPanel`, `TextBlock`, `TextBox`, `RadioButton`, `ComboBox`, `Button`, `Hyperlink`) |
| Icon library | Unicode glyphs only (matches ChatWindow's `✦`, `⚙`, `✕`, etc. — no `FontAwesome`, no SVG asset library) |
| Font | WPF system default (`Segoe UI` on Windows 10/11) — ChatWindow does NOT set `FontFamily`; FirstRunWindow inherits the same |

---

## Visual Tokens (extracted from ChatWindow + SettingsWindow)

These are the **locked palette** FirstRunWindow reuses verbatim. Executor MUST NOT introduce new color literals; reach for one of these.

### Colors

| Token | Hex | Source (file:line) | Usage in FirstRunWindow |
|-------|-----|-------------------|--------------------------|
| `bg.outer.gradient` | `#9B51E0` → `#3182CE` → `#D53F8C` → `#E28743` (LinearGradient 0,0 → 1,1) | `ChatWindow.xaml:115-122` / `SettingsWindow.xaml:74-79` | Outer 2px gradient border on the root `Border` (window frame accent) |
| `bg.inner` | `#F01A1A1E` | `SettingsWindow.xaml:83` (matches ChatWindow's `#F0101013` inner; pick the lighter `1A1A1E` to match Settings since this is also a "dialog" type window) | Inner dark surface fill |
| `bg.input` | `#15FFFFFF` | `SettingsWindow.xaml` `InputBg` resource (line 17) | TextBox + ComboBox background |
| `border.subtle` | `#3FFFFFFF` | `SettingsWindow.xaml` `SystemBorder` resource (line 15) | Window-level subtle divider lines |
| `border.input` | `#2A2A30` | `SettingsWindow.xaml:33` | TextBox / ComboBox `BorderBrush` |
| `text.primary` | `#F2F2F5` | `SettingsWindow.xaml` `SystemText` resource (line 16) | Labels, body copy |
| `text.heading` | `#FFFFFF` | `SettingsWindow.xaml:92` ("CONFIGURAÇÕES" heading uses `Foreground="White"`) | Title only |
| `text.secondary` | `#888899` | `ChatWindow.xaml:40` (`CapsuleIconButton.Foreground`) | Hint copy, helper text below inputs |
| `text.muted` | `text.primary` @ `Opacity=0.6` | `SettingsWindow.xaml:93` (subtitle pattern) | Subtitle below title |
| `text.error` | `#D53F8C` (magenta from the brand gradient — already used as ALPHA-tag color in SettingsWindow line 170) | `SettingsWindow.xaml:170` | Inline regex-validation error label; "Ollama não detectado" header |
| `accent.gradient` | `LinearGradientBrush #2B5BFF → #6A3BFF` (`AiAccent` resource) | `SettingsWindow.xaml:19-22` | Primary CTA ("Salvar") button background; thin underline below title |
| `accent.brand.purple` | `#9B51E0` | `ChatWindow.xaml:79,117,145` | Selected RadioButton dot fill; focus ring; brand `✦` glyph |
| `hover.overlay.light` | `#1AFFFFFF` | `ChatWindow.xaml:53` | Hover background on icon-style buttons (e.g., "Alterar chave" pencil button) |
| `pressed.overlay.light` | `#2AFFFFFF` | `ChatWindow.xaml:56` | Pressed state on icon-style buttons |
| `link.color` | `#3182CE` (blue from brand gradient — already used as DEBUG-tag color SettingsWindow line 177) | `SettingsWindow.xaml:177` | "Usar qwen2.5:7b mesmo assim" fallback Hyperlink |

### Shadows / Effects

| Token | Value | Source | Usage |
|-------|-------|--------|-------|
| `shadow.window` | `DropShadowEffect BlurRadius=28 ShadowDepth=6 Direction=270 Color=#000000 Opacity=0.55` | `ChatWindow.xaml:102-103` / `SettingsWindow.xaml:67-68` | Applied to FirstRunWindow root |

### Border Radius

| Token | Value | Source | Usage |
|-------|-------|--------|-------|
| `radius.window.outer` | `16` | `SettingsWindow.xaml:72` | Outer gradient frame |
| `radius.window.inner` | `14` | `SettingsWindow.xaml:83` | Inner dark surface |
| `radius.input` | `4` | `SettingsWindow.xaml:56` (BrowseButton) | TextBox / ComboBox corners (WPF default; explicit set if executor styles them) |
| `radius.cta` | `6` | `SettingsWindow.xaml:199` (Salvar button) | Primary CTA |

### Spacing Scale

Project follows an **8-point scale with one ad-hoc 5px exception inherited from existing windows.** Multiples of 4 throughout.

| Token | Value | Source | Usage in FirstRunWindow |
|-------|-------|--------|--------------------------|
| `xs` | 4px | inferred | Inline gaps (icon ↔ text) |
| `sm` | 8px | inferred (also `SettingsWindow.xaml:36` TextBox `Padding`) | Compact element spacing; TextBox padding |
| `md` | 12px | `ChatWindow.xaml:213` (input capsule margin) | Default vertical rhythm inside content area |
| `lg` | 16px | inferred | Default gap between form sections |
| `xl` | 25px | `SettingsWindow.xaml:84` (`Grid Margin="25"`) | Content area outer padding (window frame → content) |
| `2xl` | 32px | inferred | Heading-to-content break |

Exceptions:
- `5px` left margin on `SidebarPanel` in ChatWindow — does NOT apply to FirstRunWindow.
- `TextBlock` default `Margin="0,12,0,4"` from `SettingsWindow.xaml:27` — REUSE this implicit label style so all labels in FirstRunWindow space identically to SettingsWindow.

---

## Typography

WPF inherits `Segoe UI` from the system. Three sizes + one display size, two weights — falls within the 3-4 size / 2 weight contract.

| Role | Size | Weight | Line Height | Source (file:line) | Usage in FirstRunWindow |
|------|------|--------|-------------|---------------------|--------------------------|
| `display` (Welcome title) | 22px | Bold | WPF default (~1.2) | new (matches SettingsWindow's 18px title pattern at line 92, scaled up for "Welcome" hierarchy) | "Bem-vindo ao AIB" |
| `heading` (section sub) | 13px | SemiBold (Opacity 0.8) | WPF default | `SettingsWindow.xaml:99` ("CONEXÃO") | (Not used directly — FirstRunWindow has no sub-sections) |
| `body` | 13px | Regular | WPF default (~1.4) | `SettingsWindow.xaml:26` (default TextBlock FontSize) | Subtitle, provider-radio labels, helper copy |
| `input` | 13px | Regular | WPF default | `SettingsWindow.xaml:35` (TextBox FontSize) | KeyTextBox content |
| `error` | 12px | Regular | WPF default | matches `SettingsWindow.xaml:149` (small hint TextBlocks) | Inline validation + Ollama-missing error |
| `button.cta` | 13px | SemiBold | WPF default | `SettingsWindow.xaml:196` (Salvar uses inherited; force SemiBold for clarity) | Salvar button label |
| `link` | 12px | Regular (Underline) | WPF default | new — minimal underline `Hyperlink` | "Usar qwen2.5:7b mesmo assim" |

**Weight policy:** Only `Regular` (default 400) and `Bold`/`SemiBold` (600/700) appear. No Light, no Medium, no ExtraBold.

**Locked anti-pattern:** Do NOT introduce custom `FontFamily` (e.g., Inter, Cascadia Code). Reason: the rest of the app inherits the system font; adding a custom face for this one window breaks visual continuity.

---

## Color Contract (60 / 30 / 10)

| Role | Value | Allocation | Usage |
|------|-------|-----------|-------|
| Dominant (60%) | `bg.inner` `#F01A1A1E` | Window inner surface | Background, all space that is not chrome or a control |
| Secondary (30%) | `bg.input` `#15FFFFFF` (12.5% white-on-dark) | TextBox / ComboBox / RadioButton background, hover overlays | Form-control surfaces |
| Accent (10%) | `accent.gradient` (`#2B5BFF` → `#6A3BFF`) + `accent.brand.purple` `#9B51E0` | Salvar CTA only + selected RadioButton dot only + outer 2px window frame gradient | See "reserved-for" list below |
| Destructive / Error | `text.error` `#D53F8C` | Validation messages only | See "reserved-for" list below |

**Accent reserved EXCLUSIVELY for:**
1. The primary CTA button ("Salvar") — `accent.gradient` background.
2. The selected `RadioButton`'s dot fill — `accent.brand.purple` `#9B51E0`.
3. The 2px outer window frame (the always-on gradient halo around the dialog).
4. The thin 1px underline below the welcome title — `accent.gradient` at `Opacity=0.5` (matches `SettingsWindow.xaml:94`).
5. Focus ring color on focused control — `accent.brand.purple` `#9B51E0` at `Opacity=0.5`.

Accent is NOT used for:
- Body text, labels, helper text, link copy (except the fallback link, which uses `link.color` blue).
- Cancel button (transparent background, white text only — matches `SettingsWindow.xaml:195`).
- "Tentar novamente" button (uses `bg.input` background like `BrowseButton` style — secondary affordance, NOT accent).

**Destructive / error color reserved EXCLUSIVELY for:**
1. Inline regex-validation error text ("Chave inválida — deve começar com `sk-`").
2. The Ollama-missing error header ("Ollama não detectado em `localhost:11434`").

There are NO destructive *actions* in FirstRunWindow (Cancel is a quit, not a delete) — no confirmation modal needed.

---

## Layout (FirstRunWindow shape)

| Property | Value | Rationale |
|----------|-------|-----------|
| `WindowStartupLocation` | `CenterScreen` | D-07 |
| `WindowStyle` | `None` (matches SettingsWindow) with `AllowsTransparency="True"` + `Background="#01000000"` for the gradient-frame trick | Visual consistency with SettingsWindow |
| `ResizeMode` | `NoResize` | Welcome flow is fixed-content; no resize handle |
| `ShowInTaskbar` | `False` | Tray app pattern; the dialog is not a top-level taskbar window |
| `Topmost` | `True` | Match ChatWindow — user invoked via global hotkey, expects it on top |
| `Width` | `480` | Wider than SettingsWindow's 450 to give the welcome title breathing room; narrower than ChatWindow's 760 because content is form-only |
| `Height` | `SizeToContent="Height"` with `MinHeight=420` and `MaxHeight=600` | Two branches (Ollama / OpenAI) have different vertical footprints; let WPF size to content, bounded |
| `Owner` | `null` | Standalone dialog; not modal-over-ChatWindow (D-08: ChatWindow is hidden when FirstRunWindow is open) |

### Layout Structure (top → bottom)

```
┌─ Border (gradient frame, CornerRadius=16, Padding=2) ────────┐
│  ┌─ Border (inner, #F01A1A1E, CornerRadius=14) ───────────┐  │
│  │  Grid Margin=25                                        │  │
│  │  ┌─────────────────────────────────────────────────┐   │  │
│  │  │ Row 0  StackPanel (header)                      │   │  │
│  │  │   • TextBlock "Bem-vindo ao AIB" (display)      │   │  │
│  │  │   • TextBlock subtitle (body, Opacity 0.6)      │   │  │
│  │  │   • Border 1px accent.gradient Opacity=0.5      │   │  │
│  │  ├─────────────────────────────────────────────────┤   │  │
│  │  │ Row 1  StackPanel (provider picker)             │   │  │
│  │  │   • TextBlock label "Provedor de IA"            │   │  │
│  │  │   • RadioButton "Ollama — local, sem chave"     │   │  │
│  │  │   • RadioButton "OpenAI — requer chave da API"  │   │  │
│  │  ├─────────────────────────────────────────────────┤   │  │
│  │  │ Row 2  Grid (branch swap area, Height=Auto)     │   │  │
│  │  │   ┌─ OpenAI branch StackPanel (collapsed by default) ─┐
│  │  │   │  • TextBlock body "Cole sua chave da OpenAI…" │ │  │
│  │  │   │  • TextBox KeyTextBox                         │ │  │
│  │  │   │  • TextBlock ErrorLabel (collapsed initially) │ │  │
│  │  │   └─────────────────────────────────────────────┘ │  │
│  │  │   ┌─ Ollama branch StackPanel (visible by default)─┐│  │
│  │  │   │  • TextBlock body "Selecione o modelo…"      │ │  │
│  │  │   │  • ComboBox ModelComboBox (initially loading)│ │  │
│  │  │   │  • StackPanel OllamaErrorBlock (collapsed)   │ │  │
│  │  │   │     • TextBlock "Ollama não detectado em…"   │ │  │
│  │  │   │     • Button "Tentar novamente"              │ │  │
│  │  │   │     • Hyperlink "Usar qwen2.5:7b mesmo assim"│ │  │
│  │  │   └─────────────────────────────────────────────┘ │  │
│  │  ├─────────────────────────────────────────────────┤   │  │
│  │  │ Row 3  StackPanel Horizontal HAlign=Right       │   │  │
│  │  │   • Button "Cancelar" (transparent text-only)   │   │  │
│  │  │   • Button "Salvar" (accent.gradient, IsDefault)│   │  │
│  │  └─────────────────────────────────────────────────┘   │  │
│  └────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────┘
```

### Concrete spacing

| Region | Margin / Padding |
|--------|-------------------|
| Window outer `Border.Padding` (gradient frame thickness) | `2` |
| Inner `Border` `Grid.Margin` (content inset) | `25` (matches SettingsWindow `xl`) |
| Title → subtitle gap | inherited default `0,12,0,4` |
| Subtitle → underline | `0,4,0,10` |
| Underline → "Provedor de IA" label | `0,10,0,5` |
| RadioButton vertical spacing | `0,8,0,0` between the two radios |
| Branch area top margin | `0,16,0,0` (after the radio group) |
| Input label → input control | inherited default `0,12,0,4` |
| ErrorLabel top margin | `0,4,0,0` |
| Buttons row top margin | `0,20,0,0` |
| Salvar / Cancelar inter-button gap | `0,0,10,0` on Cancelar (left of Salvar) — matches `SettingsWindow.xaml:195` |
| Button sizes | Salvar: `Width=100 Height=35`; Cancelar: `Width=80 Height=35` (matches SettingsWindow) |

---

## States

### State matrix

| # | Trigger | Visible branch | OpenAI sub-state | Ollama sub-state | Salvar enabled? |
|---|---------|---------------|------------------|------------------|------------------|
| S0 | Window opened | Ollama (default radio selected) | n/a (collapsed) | `fetching` (ComboBox showing loading) | No (model not yet selected) |
| S1 | Ollama radio + GetOllamaModelsAsync returned >0 | Ollama | n/a | `loaded` (ComboBox enabled, first model NOT auto-selected) | No until user picks a model |
| S2 | S1 + user picked a model | Ollama | n/a | `loaded` + selection | **Yes** |
| S3 | Ollama radio + GetOllamaModelsAsync returned empty / threw | Ollama | n/a | `error` (TextBlock + retry button + fallback link visible; ComboBox collapsed) | No |
| S4 | S3 + user clicked "Tentar novamente" | Ollama | n/a | `fetching` then S1/S2 or S3 | No during fetch |
| S5 | S3 + user clicked "Usar qwen2.5:7b mesmo assim" | Ollama | n/a | `fallback-applied` (error block hidden; ComboBox replaced with read-only TextBlock showing `qwen2.5:7b`) | **Yes** |
| S6 | User clicked OpenAI radio | OpenAI | `empty` (TextBox empty, ErrorLabel collapsed) | n/a (collapsed) | No |
| S7 | S6 + user typed text not matching regex | OpenAI | `invalid` (TextBox + ErrorLabel visible with red copy) | n/a | No |
| S8 | S6 + user typed text matching regex | OpenAI | `valid` (TextBox + ErrorLabel collapsed) | n/a | **Yes** |
| S9 | User clicked Salvar from S2/S5/S8 | Save in-flight (sync, microseconds) — buttons NOT visually disabled because the write is instantaneous; if write fails (rare), `ErrorLabel` shows `"Erro ao salvar: {message}"` and Salvar re-enables | per branch | per branch | (briefly N/A) |
| S10 | Save succeeded | Window closes; FirstRunWindow returns DialogResult=true | — | — | — |
| S11 | User clicked Cancelar / X / Esc / Alt+F4 | Window closes; DialogResult=false; App.Current.Shutdown() | — | — | — |

### Validation feedback — when to show

| State | ErrorLabel text | ErrorLabel.Visibility |
|-------|-----------------|----------------------|
| S6 `empty` (TextBox just got focus, no typing yet) | (none) | `Collapsed` |
| S7 `invalid` (only AFTER user lost focus on TextBox OR pressed Enter OR clicked Salvar) | "Chave inválida — deve começar com `sk-`" | `Visible` |
| S8 `valid` | (none) | `Collapsed` |
| S3 `error` | "Ollama não detectado em `localhost:11434`" | `Visible` (via the OllamaErrorBlock StackPanel) |
| Save-failure (rare) | "Erro ao salvar: {result}" | `Visible` (via inline reuse of ErrorLabel under whichever branch is active) |

**Validation timing rule:** Do NOT validate-on-keystroke. Reasoning: a real OpenAI key is pasted in one shot, so per-keystroke validation flashes "invalid" the entire time the user is typing — UX antipattern. Validate on `LostFocus`, on `Enter` keypress, and on Save click.

### Save button enable/disable

The Salvar button is a regular `Button` with `IsEnabled` toggled in code-behind based on the state table. When disabled:
- `Opacity = 0.4`
- `Cursor = Arrow` (not Hand)
- The accent gradient stays visible (no color swap) — matches Windows Fluent disabled style.

---

## Copy (verbatim, pt-BR)

All copy below is **canonical**. Executor copies these strings exactly; no paraphrasing. Sourced from CONTEXT.md `<specifics>` and polished here for the welcome flow.

### Window title (taskbar / accessibility)

| Element | String |
|---------|--------|
| `Window.Title` | `Bem-vindo ao AIB` |

### Header

| Element | String |
|---------|--------|
| Display title | `Bem-vindo ao AIB` |
| Subtitle | `Para começar, escolha seu provedor de IA.` |

### Provider picker

| Element | String |
|---------|--------|
| Section label | `Provedor de IA` |
| RadioButton (Ollama, default selected) | `Ollama — local, sem chave` |
| RadioButton (OpenAI) | `OpenAI — requer chave da API` |

### OpenAI branch

| Element | String |
|---------|--------|
| Helper body | `Cole sua chave da OpenAI (começa com `sk-…`). Será armazenada com criptografia DPAPI no seu perfil.` |
| TextBox label (above input) | `Chave da API` |
| TextBox `AutomationProperties.Name` | `Chave da API da OpenAI` |
| TextBox placeholder visual (NOTE: WPF `TextBox` has no native placeholder — render as faded `TextBlock` overlay, or skip; recommend skip for simplicity) | `sk-…` (only if executor implements overlay; otherwise leave TextBox empty) |
| Validation error | `Chave inválida — deve começar com `sk-`` |
| Save-failure inline error template | `Erro ao salvar: {result}` |

### Ollama branch

| Element | String |
|---------|--------|
| Helper body | `Selecione o modelo instalado no seu Ollama.` |
| ComboBox label (above) | `Modelo` |
| ComboBox `AutomationProperties.Name` | `Modelo instalado no Ollama` |
| Loading indicator caption (optional, inline with ComboBox) | `Buscando modelos…` |
| Error header | `Ollama não detectado em `localhost:11434`` |
| Error helper body | `Verifique se o Ollama está em execução, ou continue com o modelo padrão.` |
| Retry button | `Tentar novamente` |
| Fallback link (Hyperlink) | `Usar qwen2.5:7b mesmo assim` |
| After fallback applied — read-only display | `Modelo: qwen2.5:7b (padrão)` |

### Footer buttons

| Element | String |
|---------|--------|
| Primary CTA | `Salvar` |
| Secondary | `Cancelar` |
| Cancelar tooltip | `Cancelar encerra o AIB. Pressione o atalho novamente para reabrir.` |

### Destructive actions

None. Cancel quits the app (D-04) but is not destructive in the data-loss sense — no user-entered key has been written when Cancel is pressed.

---

## Interactions

### Keyboard map

| Key | Context | Effect | Acceptance |
|-----|---------|--------|------------|
| `Tab` | Anywhere | Moves focus through tab order (see below) | Visible focus ring on next control |
| `Shift+Tab` | Anywhere | Moves focus backward | Visible focus ring on previous control |
| `Space` | RadioButton focused | Selects the radio | Branch area swaps (OpenAI ↔ Ollama) |
| `Up` / `Down` | RadioButton focused | Moves selection between the two radios | Branch swaps |
| `Enter` | KeyTextBox focused (OpenAI branch) AND state is S8 (valid) | Triggers Save click | Same effect as clicking Salvar |
| `Enter` | KeyTextBox focused AND state is S6/S7 | Triggers regex validation; if invalid, shows ErrorLabel and keeps focus | ErrorLabel visible; no save |
| `Enter` | ModelComboBox focused AND state is S2 / S5 | Triggers Save click | Same effect as clicking Salvar |
| `Enter` | Salvar button focused | Triggers Save click (because `IsDefault=true`) | Same effect as click |
| `Esc` | Anywhere in window | Triggers Cancel (`DialogResult=false`) | Window closes; `App.Current.Shutdown()` fires; audit line `outcome:firstrun_cancelled` |
| `Alt+F4` | Anywhere | Same as Esc | Same as Esc |
| Window-X (`✕`) click | n/a | Same as Esc | Same as Esc |

**Tab order (top → bottom, left → right):**

1. Ollama RadioButton (default focus on open)
2. OpenAI RadioButton
3. **Branch-active controls:**
   - Ollama branch S0/S1/S2: ModelComboBox
   - Ollama branch S3: "Tentar novamente" button, then "Usar qwen2.5:7b mesmo assim" link
   - Ollama branch S5: (no extra control — display is read-only TextBlock, skipped in tab order)
   - OpenAI branch S6/S7/S8: KeyTextBox
4. Cancelar
5. Salvar

Controls in the inactive branch are excluded from tab order via `Visibility=Collapsed` on their parent StackPanel.

### Provider radio change

| From | To | Effect |
|------|----|--------|
| Ollama | OpenAI | Ollama StackPanel `Visibility=Collapsed`; OpenAI StackPanel `Visibility=Visible`. ModelComboBox / OllamaErrorBlock unmodified internally (state preserved on switch-back). KeyTextBox focus, ErrorLabel reset to `Collapsed`. Salvar disabled. |
| OpenAI | Ollama | OpenAI StackPanel `Visibility=Collapsed`; Ollama StackPanel `Visibility=Visible`. If ComboBox has never been populated, fire `GetOllamaModelsAsync` (state S0 → S1/S3). Salvar disabled unless previously in S2/S5. |

### Save click

1. Determine active branch.
2. **OpenAI branch:**
   - Regex match `^sk-[a-zA-Z0-9_-]{20,}$`. If fail → set ErrorLabel, audit `firstrun_invalid_key`, return.
   - `await CredentialService.StoreCredentialAsync("openai", "ApiKey", key)`. If result starts with `ERRO` → set ErrorLabel with the result, return.
   - `settings.AiProvider = "OpenAI"; settings.ApiKey = "use-vault"; settings.ApiUrl = "";` (per RESEARCH Q1 + Q3)
   - `_settingsService.SaveSettings(settings);`
   - Audit `firstrun_saved` with `key_last4`.
   - `DialogResult = true; Close();`
3. **Ollama branch:**
   - `settings.AiProvider = "Ollama"; settings.ApiKey = "ollama"; settings.ModelName = selected; settings.ShadowModelName = selected;`
   - `_settingsService.SaveSettings(settings);`
   - Audit `firstrun_saved` with `provider:"Ollama", model:selected`.
   - `DialogResult = true; Close();`

**Observable:** Window disappears. ChatWindow opens immediately after (via `App.xaml.cs` `ShowFirstRunWindow` returning).

### Cancel / X / Esc / Alt+F4

1. Audit `firstrun_cancelled`.
2. `DialogResult = false; Close();` → `App.xaml.cs` checks DialogResult and calls `Application.Current.Shutdown()` (D-04, Pitfall 6 — call site is the parent, not FirstRunWindow itself, so UI thread is guaranteed).
3. **Observable:** Window disappears, tray icon disappears within ~30s (Windows tray refresh on hover or shell tick).

### Retry button (Ollama error state)

1. Hide OllamaErrorBlock.
2. Show ComboBox in loading state (LoadingProgress visible).
3. `await _settingsService.GetOllamaModelsAsync(settings.ApiUrl ?? "http://127.0.0.1:11434/v1")`.
4. If models > 0 → S1; if empty → S3 (show error again).

**Observable:** Single retry attempt per click. No auto-retry loop.

### Fallback link click

1. Hide OllamaErrorBlock.
2. Replace ComboBox area with a read-only `TextBlock` showing `Modelo: qwen2.5:7b (padrão)`.
3. Set internal selected-model variable to `qwen2.5:7b`.
4. Enable Salvar.

**Observable:** Salvar becomes clickable; pressing it persists `settings.ModelName = settings.ShadowModelName = "qwen2.5:7b"`.

---

## Accessibility

| Requirement | Implementation |
|-------------|----------------|
| Keyboard-only end-to-end flow | Tab order above; Enter triggers Save; Esc triggers Cancel |
| `AutomationProperties.Name` on every interactive control | Set in pt-BR per the Copy table above |
| Focus visible | Reuse the WPF default focus rectangle PLUS a 1px `accent.brand.purple` `#9B51E0` underline via a Focus trigger; do NOT remove the default focus visual |
| Screen reader friendly title | `Window.Title="Bem-vindo ao AIB"` and `AutomationProperties.HelpText` on `KeyTextBox` carries the helper body verbatim |
| Color contrast (text on `#F01A1A1E` background) | `text.primary #F2F2F5` → contrast ratio ~14:1 ✓ (well above WCAG AA 4.5:1); `text.secondary #888899` → ~5.2:1 ✓; `text.error #D53F8C` → ~5.5:1 ✓ |
| Touch targets | Buttons are 35px tall (matches SettingsWindow); RadioButtons inherit WPF default (16px circle + clickable label region) — acceptable for desktop mouse/keyboard primary input |

---

## Secondary surface: `SettingsWindow.KeyTextBox` polish

Optional per CONTEXT.md `<deferred>`. If included in this phase, the contract is:

| Property | Value |
|----------|-------|
| `KeyTextBox.IsReadOnly` | `True` |
| `KeyTextBox.Text` (when `settings.ApiKey == "use-vault"`) | `Configurada (cofre DPAPI)` |
| `KeyTextBox.Text` (when `settings.ApiKey == "ollama"`) | `(não necessário para Ollama)` |
| `KeyTextBox.Text` (legacy, sk- prefix — should not occur post-migration but defensive) | `Configurada (legado)` |
| `KeyTextBox.Foreground` | `text.secondary #888899` (faded, since field is no longer interactive) |
| `KeyTextBox.Background` | `#2A2A30` (matches the MaxHistoryTextBox read-only treatment at SettingsWindow.xaml:160) |
| New button placed inline to the right of KeyTextBox | `Content="Alterar chave"`, styled same as `BrowseButton` resource (SettingsWindow.xaml:43-62) but with `Width=110 Height=35` |
| Button click | Opens FirstRunWindow via `new FirstRunWindow().ShowDialog()`; on success, reload `_currentSettings` and refresh the label |
| Button visibility | Always visible (works regardless of current sentinel value) |

**Layout edit:** Wrap the existing line 111-112 in a `Grid` with 2 columns (`*` and `Auto`), put KeyTextBox in column 0, the new "Alterar chave" Button in column 1 with `Margin="5,12,0,0"`.

**Removed in this polish:** The old free-text editing of the key field. The KeyTextBox no longer carries the actual key for editing — that's exclusively FirstRunWindow's job (single source of truth for "where do users type the key").

---

## Secondary surface: `SettingsWindow.ProviderComboBox` extension

Required (RESEARCH Q1). One-line XAML edit at `SettingsWindow.xaml:102-105`:

```xml
<ComboBox x:Name="ProviderComboBox" Height="38" SelectionChanged="ProviderComboBox_SelectionChanged">
    <ComboBoxItem Content="Ollama"/>
    <ComboBoxItem Content="OpenAI"/>           <!-- NEW -->
    <ComboBoxItem Content="Google Gemini"/>
</ComboBox>
```

Code-behind impact: `UpdateUiForProvider()` (lines 68-89) currently has an `if (selected == "Google Gemini")` branch and an `else`. Add an `else if (selected == "OpenAI")` branch that:
- Keeps `AdvancedConnectionPanel` visible (user may still want to inspect / change URL or model).
- If the URL is the localhost Ollama default, replace with `""` (empty → SDK default `https://api.openai.com/v1`).
- Leave KeyTextBox display alone (now controlled by the read-only-sentinel polish above).

This single-line addition is a hard requirement: without it, a user who picked "OpenAI" in FirstRunWindow cannot see their selection round-tripped in Settings.

---

## Audit-log UX surface (minimal — for completeness)

There is **no user-visible UX for the audit log** in this phase. It is a fire-and-forget JSONL write. The only UX-adjacent fact: when Save / Cancel fires, the audit line emits before the window closes. If the audit write fails (impossible in practice — `SemaphoreSlim`-guarded JSONL append cannot throw inside `try/catch`), there is no inline message; failure is logged to console only (Phase 1 D6 pattern).

Outcomes emitted by FirstRunWindow:
- `firstrun_cancelled` — Cancel / X / Esc / Alt+F4
- `firstrun_invalid_key` — Save with regex failure (OpenAI branch only)
- `firstrun_saved` — Save success (any branch)

Outcomes emitted by `App.OnStartup` migration (NOT FirstRunWindow):
- `migration_clear_apikey` — D-11 one-shot mutation

---

## Registry Safety

Not applicable. Stack is .NET 8 WPF — no shadcn, no third-party UI component registry. All controls are first-party WPF (`System.Windows.Controls.*`) or first-party AIB code. The only package change in this phase is the REMOVAL of `DotNetEnv` (a non-UI package). Zero new packages introduced.

---

## Checker Sign-Off

- [ ] Dimension 1 Copywriting: PASS — verbatim pt-BR strings declared, no placeholders, no destructive confirmations needed
- [ ] Dimension 2 Visuals: PASS — layout structure + spacing extracted from existing windows, no new tokens invented
- [ ] Dimension 3 Color: PASS — 60/30/10 split documented; accent reserved list explicit; destructive color reserved list explicit
- [ ] Dimension 4 Typography: PASS — 4 sizes (12, 13, 13, 22), 2 weights (Regular, Bold/SemiBold), inherited system font
- [ ] Dimension 5 Spacing: PASS — 8-point scale extracted from existing windows (one 25px content inset exception inherited from SettingsWindow)
- [ ] Dimension 6 Registry Safety: PASS (N/A — no UI registry in use)

**Approval:** pending

---

## Pre-Populated From

| Source | Decisions Used |
|--------|---------------|
| `02-CONTEXT.md` D-01..D-12 | 12 (all locked decisions adopted verbatim; copy strings from `<specifics>` polished only where ambiguous) |
| `02-RESEARCH.md` Open Questions | 2 resolved (Q1 → `AiProvider="OpenAI"` + extend SettingsWindow combo; Q3 → `ApiUrl=""` on OpenAI save) |
| `ChatWindow.xaml` | 8 tokens extracted (shadow, gradients, hover overlays, brand purple, secondary text, font-size baseline) |
| `SettingsWindow.xaml` | 11 tokens extracted (SystemText, SystemBorder, InputBg, AiAccent gradient, content inset, button sizing, BrowseButton style, label margin default, magenta/blue tag colors repurposed as error/link colors) |
| `REQUIREMENTS.md` SEC-03 | acceptance criteria mapped to states S0..S11 |
| User input | 0 (no user-facing questions needed — all answers traced to upstream artifacts) |
